using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Connects the configured Model Context Protocol servers and publishes their tools to the session
/// toolset. It maintains one supervised connection per
/// server, tools registered under server-qualified names, and a bounded reconnect budget per outage.
/// </summary>
/// <remarks>
/// A server's tool list is re-synced when it sends <c>notifications/tools/list_changed</c>.
/// The .NET MCP client does not expose notification registration on its public client type, so this
/// supervisor re-lists on a health-check interval and on every reconnect instead. The observable
/// difference is latency: a server that adds a tool is picked up at the next check rather than
/// immediately.
/// </remarks>
public sealed class McpClientService : IMcpToolProvider, IAsyncDisposable
{
	/// <summary>How often a connected server is pinged and its tool list re-read.</summary>
	private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

	/// <summary>One supervised server: its published tools, its state, and its reconnect bookkeeping.</summary>
	private sealed class Supervised(McpServerConfig config)
	{
		public McpServerConfig Config => config;
		public McpServerState State = config.Enabled ? McpServerState.Connecting : McpServerState.Disabled;
		public string? Detail;
		public IReadOnlyList<AITool> Tools = [];
		public McpClient? Client;
		public Task? Worker;

		/// <summary>Ends this supervisor alone, so a reload can replace one server's connection.</summary>
		public readonly CancellationTokenSource Stop = new();

		public McpServerSnapshot Snapshot() => new(config.Name, State, Tools.Count, Detail);
	}

	private readonly List<Supervised> _servers = [];
	private readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.OrdinalIgnoreCase);
	private readonly CancellationTokenSource _shutdown = new();
	private readonly IMcpConfigurationService? _configuration;
	private readonly Action<string>? _log;
	private readonly Func<McpServerConfig, IClientTransport> _transportFactory;
	private readonly SemaphoreSlim _reload = new(1, 1);
	private volatile IReadOnlyList<AITool> _tools = [];
	private volatile IReadOnlyList<McpServerTools> _published = [];

	/// <param name="configuration">
	/// Where the server list comes from. Null starts an empty provider, which is what a composition
	/// with no MCP configured wants.
	/// </param>
	/// <param name="transportFactory">
	/// How a configured server becomes a connection. Overridden only by tests, which connect an
	/// in-process server rather than launching one.
	/// </param>
	public McpClientService(
		IMcpConfigurationService? configuration = null,
		Action<string>? log = null,
		Func<McpServerConfig, IClientTransport>? transportFactory = null)
	{
		_configuration = configuration;
		_log = log;
		_transportFactory = transportFactory ?? BuildTransport;
	}

	public event Action? Changed;

	public IReadOnlyList<AITool> Tools => _tools;

	public IReadOnlyList<McpServerTools> Published => _published;

	public string? ServerOf(string toolName)
	{
		foreach (var entry in _published)
		{
			if (entry.Tools.Any(tool => tool is AIFunction function && function.Name == toolName)) return entry.Server;
		}
		return null;
	}

	public IReadOnlyList<McpServerSnapshot> Servers
	{
		get { lock (_servers) return [.. _servers.Select(server => server.Snapshot())]; }
	}

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		var servers = _configuration is null ? [] : await _configuration.ResolveAsync(cancellationToken);
		await StartAsync(new McpConfiguration(servers), cancellationToken);
	}

	/// <summary>
	/// Drop every connection and reconnect from the current configuration. An edited endpoint or a
	/// newly supplied key takes effect on the next turn rather than at the next restart.
	/// </summary>
	public async Task ReloadAsync(CancellationToken cancellationToken = default)
	{
		if (_configuration is null) return;
		await _reload.WaitAsync(cancellationToken);
		try
		{
			await StopAllAsync();
			var servers = await _configuration.ResolveAsync(cancellationToken);
			await StartAsync(new McpConfiguration(servers), cancellationToken);
		}
		finally { _reload.Release(); }
	}

	/// <summary>Stop every supervisor and withdraw every published tool.</summary>
	private async Task StopAllAsync()
	{
		List<Supervised> servers;
		lock (_servers)
		{
			servers = [.. _servers];
			_servers.Clear();
		}
		foreach (var server in servers)
		{
			server.Stop.Cancel();
			if (server.Worker is not null)
			{
				try { await server.Worker; }
				catch (Exception) { /* a supervisor's last failure on the way out is not worth surfacing */ }
			}
			await DisposeClientAsync(server);
		}
		_names.Clear();
		Recompose();
	}

	/// <summary>
	/// How long startup waits for servers to settle their first attempt before continuing without
	/// them. A server that is merely slow keeps connecting in the background; the alternative is a
	/// harness that never finishes starting because one configured server hangs.
	/// </summary>
	public static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(30);

	/// <summary>Start an explicit configuration. Separated from the file path so it can be tested.</summary>
	public async Task StartAsync(McpConfiguration configuration, CancellationToken cancellationToken = default)
	{
		var first = new List<Task>();
		foreach (var server in configuration.Servers)
		{
			if (!McpToolNaming.IsValidServerName(server.Name))
			{
				throw new InvalidOperationException(
					$"MCP server name \"{server.Name}\" is invalid: use 1-32 characters from A-Z, a-z, 0-9, underscore, or hyphen.");
			}
			// Two servers under one name would publish colliding tool names, and the model would have
			// no way to tell which server it was calling.
			if (!_names.TryAdd(server.Name, 0))
				throw new InvalidOperationException($"MCP server name \"{server.Name}\" is configured more than once.");

			var supervised = new Supervised(server);
			lock (_servers) _servers.Add(supervised);
			if (!server.Enabled) continue;

			var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, supervised.Stop.Token);
			supervised.Worker = SuperviseAsync(supervised, ready, lifetime.Token);
			first.Add(ready.Task);
		}

		// Startup waits only for each server's first attempt to settle, and only for a bounded time.
		// A server still connecting past the budget carries on in the background and publishes its
		// tools when it lands.
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		try { await Task.WhenAll(first).WaitAsync(StartupBudget, startup.Token); }
		catch (TimeoutException)
		{
			_log?.Invoke($"mcp: some servers had not connected within {StartupBudget.TotalSeconds:F0}s; continuing without them.");
		}

		var broken = Servers.Where(server => server.State == McpServerState.Failed).ToList();
		foreach (var server in broken)
		{
			var config = configuration.Servers.Single(item => item.Name == server.Name);
			if (config.FailOnStartupError)
				throw new InvalidOperationException($"MCP server \"{server.Name}\" could not be reached: {server.Detail}");
		}
	}

	/// <summary>
	/// Keep one server connected for the life of the harness. Each iteration connects, publishes the
	/// server's tools, and then holds the connection until it fails; a failure is retried under the
	/// server's budget, and the last good tool set stays published while that happens.
	/// </summary>
	private async Task SuperviseAsync(Supervised server, TaskCompletionSource firstAttempt, CancellationToken cancellationToken)
	{
		var reconnect = server.Config.EffectiveReconnect;
		var consecutiveFailures = 0;

		while (!cancellationToken.IsCancellationRequested)
		{
			var connectedAt = DateTimeOffset.UtcNow;
			try
			{
				server.Client = await McpClient.CreateAsync(_transportFactory(server.Config), cancellationToken: cancellationToken);
				await PublishAsync(server, cancellationToken);
				Update(server, McpServerState.Connected, null);
				consecutiveFailures = 0;
				firstAttempt.TrySetResult();

				await HoldAsync(server, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				firstAttempt.TrySetResult();
				return;
			}
			catch (Exception exception)
			{
				_log?.Invoke($"mcp: server \"{server.Config.Name}\" failed: {exception.Message}");
				Update(server, McpServerState.Reconnecting, exception.Message);
			}
			finally
			{
				await DisposeClientAsync(server);
			}

			// An outage that ends after a long healthy stretch is a different outage. Resetting the
			// budget on that basis is what lets an occasionally-crashing server recover forever while a
			// crash-looping one — which never stays up — still exhausts its cap.
			if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMilliseconds(reconnect.MaxDelayMs)) consecutiveFailures = 0;
			consecutiveFailures++;

			if (!reconnect.Enabled || consecutiveFailures > reconnect.MaxAttempts)
			{
				Withdraw(server, reconnect.Enabled
					? $"gave up after {reconnect.MaxAttempts} consecutive reconnect attempts"
					: "reconnect is disabled for this server");
				firstAttempt.TrySetResult();
				return;
			}

			firstAttempt.TrySetResult();
			var delay = Math.Min(
				reconnect.MaxDelayMs,
				reconnect.InitialDelayMs * Math.Pow(2, consecutiveFailures - 1));
			try { await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken); }
			catch (OperationCanceledException) { return; }
		}
	}

	/// <summary>The connection one configured server describes.</summary>
	private static IClientTransport BuildTransport(McpServerConfig config) =>
		config.Transport switch
		{
			McpTransport.Stdio => new StdioClientTransport(new StdioClientTransportOptions
			{
				Name = config.Name,
				Command = config.Command
					?? throw new InvalidOperationException($"MCP server \"{config.Name}\" uses the stdio transport but has no command."),
				Arguments = config.Args is null ? null : [.. config.Args],
				WorkingDirectory = config.Cwd,
				EnvironmentVariables = config.Env is null ? null : new Dictionary<string, string?>(config.Env),
			}),
			McpTransport.StreamableHttp => new HttpClientTransport(new HttpClientTransportOptions
			{
				Name = config.Name,
				Endpoint = new Uri(config.Url
					?? throw new InvalidOperationException($"MCP server \"{config.Name}\" uses the HTTP transport but has no url.")),
				AdditionalHeaders = config.Headers is null ? null : new Dictionary<string, string>(config.Headers),
			}),
			_ => throw new InvalidOperationException($"MCP server \"{config.Name}\" has an unknown transport."),
		};

	/// <summary>Read the server's tool list and replace this server's published generation with it.</summary>
	private async Task PublishAsync(Supervised server, CancellationToken cancellationToken)
	{
		var listed = await server.Client!.ListToolsAsync(cancellationToken: cancellationToken);
		var published = new List<AITool>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var tool in listed)
		{
			// A server listing one raw name twice cannot be published coherently: the model would be
			// shown two tools it has no way to tell apart.
			if (!seen.Add(tool.Name))
				throw new InvalidOperationException($"MCP server \"{server.Config.Name}\" listed the tool \"{tool.Name}\" twice.");

			var publicName = McpToolNaming.PublicName(server.Config.Name, tool.Name);
			published.Add(new McpBridgedTool(
				tool.WithName(publicName),
				server.Config.Name,
				tool.Name,
				server.Config.ToolCallTimeoutMs));
		}

		server.Tools = published;
		Recompose();
	}

	/// <summary>
	/// Hold a healthy connection. The ping proves the transport is still alive; the re-list is what
	/// picks up a server that gained or lost a tool while connected.
	/// </summary>
	private async Task HoldAsync(Supervised server, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await Task.Delay(HealthCheckInterval, cancellationToken);
			await server.Client!.PingAsync(cancellationToken: cancellationToken);
			await PublishAsync(server, cancellationToken);
		}
	}

	/// <summary>
	/// Take a failed server's tools out of circulation, so the model stops being offered them. The
	/// reason for giving up is reported alongside the failure that caused it — on its own, "gave up"
	/// tells the reader nothing about what to fix.
	/// </summary>
	private void Withdraw(Supervised server, string reason)
	{
		server.Tools = [];
		Update(server, McpServerState.Failed, server.Detail is { Length: > 0 } cause ? $"{reason} — last failure: {cause}" : reason);
		Recompose();
	}

	private void Update(Supervised server, McpServerState state, string? detail)
	{
		server.State = state;
		server.Detail = detail;
		Changed?.Invoke();
	}

	/// <summary>Rebuild the published tool list from every server's current generation.</summary>
	private void Recompose()
	{
		lock (_servers)
		{
			_published = [.. _servers.Where(server => server.Tools.Count > 0)
				.Select(server => new McpServerTools(server.Config.Name, server.Tools))];
			_tools = [.. _published.SelectMany(entry => entry.Tools)];
		}
		Changed?.Invoke();
	}

	private static async Task DisposeClientAsync(Supervised server)
	{
		if (server.Client is null) return;
		try { await server.Client.DisposeAsync(); }
		catch (Exception) { /* the connection is already gone; disposal is best effort */ }
		server.Client = null;
	}

	public async ValueTask DisposeAsync()
	{
		try { await _shutdown.CancelAsync(); }
		catch (ObjectDisposedException) { return; }

		List<Task> workers;
		lock (_servers) workers = [.. _servers.Select(server => server.Worker).OfType<Task>()];
		try { await Task.WhenAll(workers); }
		catch (Exception) { /* shutdown: a worker's last failure is not worth surfacing */ }

		List<Supervised> servers;
		lock (_servers) servers = [.. _servers];
		foreach (var server in servers) await DisposeClientAsync(server);
		_shutdown.Dispose();
	}
}

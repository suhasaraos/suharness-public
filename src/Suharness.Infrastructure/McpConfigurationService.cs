using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suharness.Infrastructure;

/// <summary>
/// Reads and writes the MCP server list, and holds the per-server secrets separately from it.
/// </summary>
/// <remarks>
/// The file uses the portable <c>mcpServers</c> configuration shape. A server entry accepts both
/// <c>args</c> and <c>arguments</c>, and both <c>enabled</c> and <c>disabled</c>, so existing MCP
/// configurations can be used unchanged.
/// Secrets never appear in it: an API key is stored protected alongside the model provider
/// credentials and substituted into <c>${apiKey}</c> at connect time, so the file stays safe to copy,
/// commit, or paste into a bug report.
/// </remarks>
public sealed class McpConfigurationService : IMcpConfigurationService
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly string _configurationPath;
	private readonly string _credentialsPath;
	private readonly IDataProtector _protector;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public McpConfigurationService(string root, IDataProtectionProvider protectionProvider)
	{
		var directory = Path.GetFullPath(root);
		_configurationPath = Path.Combine(directory, "mcp.json");
		_credentialsPath = Path.Combine(directory, "mcp-credentials.bin");
		_protector = protectionProvider.CreateProtector("Suharness.McpCredentials.v1");
	}

	public event Action? Changed;

	/// <summary>The path the server list is read from, for the settings view to show.</summary>
	public string ConfigurationPath => _configurationPath;

	public async Task<McpConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var servers = await ReadServersAsync(cancellationToken);
			var credentials = await ReadCredentialsAsync(cancellationToken);
			return new McpConfigurationSnapshot(
				[.. servers.Select(server => new ConfiguredMcpServer(server, credentials.ContainsKey(server.Name)))]);
		}
		finally { _gate.Release(); }
	}

	public async Task SaveServerAsync(McpServerConfig config, string? apiKey, CancellationToken cancellationToken = default)
	{
		Validate(config);
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var servers = await ReadServersAsync(cancellationToken);
			var updated = servers
				.Where(server => !server.Name.Equals(config.Name, StringComparison.OrdinalIgnoreCase))
				.Append(config)
				.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			await WriteServersAsync(updated, cancellationToken);

			// A null key means "leave whatever is stored alone", which is what lets the settings view
			// edit an endpoint without making the user retype a secret it never shows them.
			if (apiKey is not null)
			{
				var credentials = await ReadCredentialsAsync(cancellationToken);
				var trimmed = apiKey.Trim();
				var changed = trimmed.Length == 0 ? credentials.Remove(config.Name) : Set(credentials, config.Name, trimmed);
				if (changed) await WriteCredentialsAsync(credentials, cancellationToken);
			}
		}
		finally { _gate.Release(); }
		Changed?.Invoke();
	}

	public async Task DeleteServerAsync(string name, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var servers = await ReadServersAsync(cancellationToken);
			await WriteServersAsync(
				[.. servers.Where(server => !server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))],
				cancellationToken);
			var credentials = await ReadCredentialsAsync(cancellationToken);
			if (credentials.Remove(name)) await WriteCredentialsAsync(credentials, cancellationToken);
		}
		finally { _gate.Release(); }
		Changed?.Invoke();
	}

	public async Task<IReadOnlyList<McpServerConfig>> ResolveAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var servers = await ReadServersAsync(cancellationToken);
			var credentials = await ReadCredentialsAsync(cancellationToken);
			return [.. servers.Where(server => server.Enabled).Select(server => Resolve(server, credentials.GetValueOrDefault(server.Name)))];
		}
		finally { _gate.Release(); }
	}

	/// <summary>
	/// Expand every reference a server's configuration makes to a secret or an environment variable.
	/// A stored key that the configuration never places explicitly is sent as a bearer token, which is
	/// what the great majority of HTTP MCP servers expect; a server that wants it somewhere else says
	/// so with <c>${apiKey}</c> and that placement wins.
	/// </summary>
	private static McpServerConfig Resolve(McpServerConfig server, string? apiKey)
	{
		var resolved = server with
		{
			Command = Expand(server.Command, apiKey),
			Cwd = Expand(server.Cwd, apiKey),
			Url = Expand(server.Url, apiKey),
			Args = server.Args is null ? null : [.. server.Args.Select(value => Expand(value, apiKey) ?? value)],
			Headers = server.Headers?
				.Select(pair => new KeyValuePair<string, string>(pair.Key, Expand(pair.Value, apiKey) ?? pair.Value))
				.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
				.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
			Env = server.Env?.ToDictionary(pair => pair.Key, pair => Expand(pair.Value, apiKey)),
		};

		if (string.IsNullOrEmpty(apiKey) || server.ReferencesApiKey || server.Transport != McpTransport.StreamableHttp)
			return resolved;

		var headers = new Dictionary<string, string>(resolved.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
		headers.TryAdd("Authorization", $"Bearer {apiKey}");
		return resolved with { Headers = headers };
	}

	/// <summary>Substitute <c>${apiKey}</c> and <c>${env:NAME}</c>. An absent variable expands to empty.</summary>
	private static string? Expand(string? value, string? apiKey)
	{
		if (string.IsNullOrEmpty(value)) return value;

		var text = value.Contains(McpServerConfig.ApiKeyPlaceholder, StringComparison.Ordinal)
			? value.Replace(McpServerConfig.ApiKeyPlaceholder, apiKey ?? string.Empty, StringComparison.Ordinal)
			: value;

		const string Opening = "${env:";
		var builder = new StringBuilder();
		var position = 0;
		while (true)
		{
			var start = text.IndexOf(Opening, position, StringComparison.Ordinal);
			if (start < 0) break;
			var end = text.IndexOf('}', start);
			if (end < 0) break;
			var name = text[(start + Opening.Length)..end];
			builder.Append(text, position, start - position);
			builder.Append(Environment.GetEnvironmentVariable(name) ?? string.Empty);
			position = end + 1;
		}
		if (position == 0) return text;
		builder.Append(text, position, text.Length - position);
		return builder.ToString();
	}

	private static bool Set(Dictionary<string, string> credentials, string name, string value)
	{
		credentials[name] = value;
		return true;
	}

	private static void Validate(McpServerConfig config)
	{
		if (!McpToolNaming.IsValidServerName(config.Name))
		{
			throw new InvalidOperationException(
				$"MCP server name \"{config.Name}\" is invalid: use 1-32 characters from A-Z, a-z, 0-9, underscore, or hyphen.");
		}
		if (config.Transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(config.Command))
			throw new InvalidOperationException($"MCP server \"{config.Name}\" uses the stdio transport, so it needs a command.");
		if (config.Transport == McpTransport.StreamableHttp)
		{
			if (string.IsNullOrWhiteSpace(config.Url))
				throw new InvalidOperationException($"MCP server \"{config.Name}\" uses the HTTP transport, so it needs a URL.");
			if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
				throw new InvalidOperationException($"MCP server \"{config.Name}\" needs an absolute http or https URL.");
		}
		if (config.ToolCallTimeoutMs <= 0)
			throw new InvalidOperationException($"MCP server \"{config.Name}\" needs a positive tool-call timeout.");
	}

	// ─── Persistence ─────────────────────────────────────────────────────

	private async Task<IReadOnlyList<McpServerConfig>> ReadServersAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_configurationPath)) return [];
		try
		{
			await using var stream = File.OpenRead(_configurationPath);
			var document = await JsonSerializer.DeserializeAsync<McpConfigFile>(stream, JsonOptions, cancellationToken);
			return document is null ? [] : [.. document.ToConfigs()];
		}
		catch (JsonException)
		{
			// A malformed file must not take the harness down with it; the settings view reports the
			// server list as empty and rewriting it from the UI repairs the file.
			return [];
		}
	}

	private async Task WriteServersAsync(IReadOnlyList<McpServerConfig> servers, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
		var temporary = _configurationPath + ".tmp";
		await using (var stream = File.Create(temporary))
			await JsonSerializer.SerializeAsync(stream, McpConfigFile.From(servers), JsonOptions, cancellationToken);
		File.Move(temporary, _configurationPath, true);
	}

	private async Task<Dictionary<string, string>> ReadCredentialsAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_credentialsPath)) return new(StringComparer.OrdinalIgnoreCase);
		try
		{
			var protectedBytes = await File.ReadAllBytesAsync(_credentialsPath, cancellationToken);
			var plain = _protector.Unprotect(protectedBytes);
			return JsonSerializer.Deserialize<Dictionary<string, string>>(plain, JsonOptions) is { } stored
				? new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase)
				: new(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException)
		{
			// Keys protected by a data-protection ring this machine no longer has cannot be recovered.
			// Reporting none is right: the settings view then shows them as missing and asks again.
			return new(StringComparer.OrdinalIgnoreCase);
		}
	}

	private async Task WriteCredentialsAsync(Dictionary<string, string> credentials, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_credentialsPath)!);
		var plain = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
		await File.WriteAllBytesAsync(_credentialsPath, _protector.Protect(plain), cancellationToken);
	}

	// ─── The on-disk shape ───────────────────────────────────────────────

	private sealed class McpConfigFile
	{
		[JsonPropertyName("mcpServers")]
		public Dictionary<string, McpServerEntry> McpServers { get; set; } = [];

		public static McpConfigFile From(IReadOnlyList<McpServerConfig> servers) => new()
		{
			McpServers = servers.ToDictionary(server => server.Name, McpServerEntry.From, StringComparer.OrdinalIgnoreCase),
		};

		public IEnumerable<McpServerConfig> ToConfigs() =>
			McpServers.Where(pair => McpToolNaming.IsValidServerName(pair.Key)).Select(pair => pair.Value.ToConfig(pair.Key));
	}

	/// <summary>
	/// One entry as it appears on disk. The duplicated fields are not an accident: the ecosystem's
	/// configurations in the wild use <c>args</c> or <c>arguments</c> and <c>disabled</c> or
	/// <c>enabled</c> interchangeably, and rejecting either half would reject working files.
	/// </summary>
	private sealed class McpServerEntry
	{
		public string? Transport { get; set; }
		public string? Command { get; set; }
		public List<string>? Args { get; set; }
		public List<string>? Arguments { get; set; }
		public Dictionary<string, string?>? Env { get; set; }
		public string? Cwd { get; set; }
		public string? Url { get; set; }
		public Dictionary<string, string>? Headers { get; set; }
		public int? ToolCallTimeoutMs { get; set; }
		public bool? FailOnStartupError { get; set; }
		public bool? Enabled { get; set; }
		public bool? Disabled { get; set; }
		public bool? RequiresApproval { get; set; }
		public McpReconnectOptions? Reconnect { get; set; }

		public static McpServerEntry From(McpServerConfig config) => new()
		{
			Transport = config.Transport == McpTransport.Stdio ? "stdio" : "http",
			Command = config.Command,
			Args = config.Args is null ? null : [.. config.Args],
			Env = config.Env is null ? null : new Dictionary<string, string?>(config.Env),
			Cwd = config.Cwd,
			Url = config.Url,
			Headers = config.Headers is null ? null : new Dictionary<string, string>(config.Headers),
			ToolCallTimeoutMs = config.ToolCallTimeoutMs,
			FailOnStartupError = config.FailOnStartupError ? true : null,
			Enabled = config.Enabled ? null : false,
			RequiresApproval = config.RequiresApproval ? true : null,
			Reconnect = config.Reconnect,
		};

		public McpServerConfig ToConfig(string name) => new(
			name,
			// The transport is stated when the file says so and inferred from the presence of a URL
			// otherwise, which is how every other harness reads these files.
			Transport switch
			{
				"stdio" => McpTransport.Stdio,
				"http" or "https" or "streamable-http" or "sse" => McpTransport.StreamableHttp,
				_ => string.IsNullOrWhiteSpace(Url) ? McpTransport.Stdio : McpTransport.StreamableHttp,
			},
			Command,
			Arguments is { Count: > 0 } ? Arguments : Args,
			Env,
			Cwd,
			Url,
			Headers,
			ToolCallTimeoutMs ?? 60_000,
			FailOnStartupError ?? false,
			Enabled ?? Disabled is not true,
			RequiresApproval ?? false,
			Reconnect);
	}
}

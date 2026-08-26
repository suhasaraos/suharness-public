using Microsoft.Extensions.AI;

namespace Suharness.Abstractions;

/// <summary>How the harness reaches one Model Context Protocol server.</summary>
public enum McpTransport
{
	/// <summary>Launch the server as a child process and speak over its standard streams.</summary>
	Stdio,

	/// <summary>Connect to a server already running behind an HTTP endpoint.</summary>
	StreamableHttp,
}

/// <summary>
/// How a lost connection is recovered, including bounded exponential backoff and a finite default
/// attempt budget.
/// </summary>
/// <param name="MaxAttempts">
/// Consecutive failed attempts allowed per outage. A crash-looping server that briefly succeeds
/// between crashes still exhausts this budget instead of restarting forever; a connection that
/// survives longer than <paramref name="MaxDelayMs"/> resets it, so an occasionally-failing server
/// recovers indefinitely.
/// </param>
public sealed record McpReconnectOptions(
	bool Enabled = true,
	int InitialDelayMs = 500,
	int MaxDelayMs = 30_000,
	int MaxAttempts = 10);

/// <summary>One configured MCP server.</summary>
/// <param name="Name">
/// The namespace for this server's model-facing tool names, and the identity the harness reports it
/// under. Restricted to <c>[A-Za-z0-9_-]</c> and unique across configured servers, because it is
/// what keeps two servers publishing the same raw tool name from colliding.
/// </param>
/// <param name="FailOnStartupError">
/// When true, a server that cannot be reached at startup fails harness startup instead of being
/// reported as unavailable. Off by default so one broken server cannot stop the harness from running.
/// </param>
public sealed record McpServerConfig(
	string Name,
	McpTransport Transport,
	string? Command = null,
	IReadOnlyList<string>? Args = null,
	IReadOnlyDictionary<string, string?>? Env = null,
	string? Cwd = null,
	string? Url = null,
	IReadOnlyDictionary<string, string>? Headers = null,
	int ToolCallTimeoutMs = 60_000,
	bool FailOnStartupError = false,
	bool Enabled = true,
	bool RequiresApproval = false,
	McpReconnectOptions? Reconnect = null)
{
	/// <summary>
	/// The placeholder a server's configuration uses to say where its API key belongs. The key itself
	/// is never written to the configuration file; it is stored protected and substituted at connect
	/// time, so a config that is copied or shared carries no secret.
	/// </summary>
	public const string ApiKeyPlaceholder = "${apiKey}";

	public McpReconnectOptions EffectiveReconnect => Reconnect ?? new McpReconnectOptions();

	/// <summary>Whether this server's configuration says where its API key goes.</summary>
	public bool ReferencesApiKey =>
		Contains(Url) || Contains(Cwd) || Contains(Command)
		|| (Args?.Any(Contains) ?? false)
		|| (Headers?.Any(pair => Contains(pair.Value)) ?? false)
		|| (Env?.Any(pair => Contains(pair.Value)) ?? false);

	private static bool Contains(string? value) =>
		value?.Contains(ApiKeyPlaceholder, StringComparison.Ordinal) ?? false;
}

/// <summary>One configured server and whether a secret has been stored for it.</summary>
public sealed record ConfiguredMcpServer(McpServerConfig Config, bool CredentialConfigured);

/// <summary>Everything the settings view needs about the configured servers.</summary>
public sealed record McpConfigurationSnapshot(IReadOnlyList<ConfiguredMcpServer> Servers)
{
	public static readonly McpConfigurationSnapshot Empty = new([]);
}

/// <summary>
/// Stores the MCP server list and the write-only secrets that go with it. Kept separate from the
/// client so a key can be added, changed, or removed without the connection code ever seeing the
/// storage, and so the configuration file stays free of secrets.
/// </summary>
public interface IMcpConfigurationService
{
	/// <summary>Raised after a server or its credential changes.</summary>
	event Action? Changed;

	Task<McpConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Create or replace one server. A null <paramref name="apiKey"/> leaves any stored key alone;
	/// an empty one clears it. The key is never written to the configuration file.
	/// </summary>
	Task SaveServerAsync(McpServerConfig config, string? apiKey, CancellationToken cancellationToken = default);

	Task DeleteServerAsync(string name, CancellationToken cancellationToken = default);

	/// <summary>
	/// The enabled servers with every <c>${apiKey}</c> and <c>${env:NAME}</c> reference expanded,
	/// ready to connect. This is the only place a stored secret is materialized.
	/// </summary>
	Task<IReadOnlyList<McpServerConfig>> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>The whole MCP configuration document.</summary>
public sealed record McpConfiguration(IReadOnlyList<McpServerConfig> Servers)
{
	public static readonly McpConfiguration Empty = new([]);
}

/// <summary>Where one server currently stands.</summary>
public enum McpServerState
{
	/// <summary>Configured but switched off; nothing is connected and no tools are published.</summary>
	Disabled,

	/// <summary>The first connection attempt is in progress.</summary>
	Connecting,

	/// <summary>Connected, with its tools published.</summary>
	Connected,

	/// <summary>The connection dropped and is being retried. The last good tools stay published.</summary>
	Reconnecting,

	/// <summary>The reconnect budget is spent. The server's tools are withdrawn until a restart.</summary>
	Failed,
}

/// <summary>A read-only projection of one server, for the settings view.</summary>
public sealed record McpServerSnapshot(
	string Name,
	McpServerState State,
	int ToolCount,
	string? Detail = null);

/// <summary>One server's published tools, kept together so a catalog can be judged per server.</summary>
public sealed record McpServerTools(string Server, IReadOnlyList<AITool> Tools);

/// <summary>
/// Publishes the tools of every connected MCP server. Each server's tools reach the model as
/// ordinary native tools under
/// server-qualified names, so the model never learns that MCP exists.
/// </summary>
public interface IMcpToolProvider
{
	/// <summary>The tools currently published across every connected server.</summary>
	IReadOnlyList<AITool> Tools { get; }

	/// <summary>
	/// The same tools, grouped by the server that published them. Catalog size is a property of one
	/// server, so any policy that narrows a large catalog has to be able to tell them apart.
	/// </summary>
	IReadOnlyList<McpServerTools> Published { get; }

	/// <summary>The server a tool came from, or null when it is not an MCP tool.</summary>
	string? ServerOf(string toolName);

	/// <summary>The current state of every configured server.</summary>
	IReadOnlyList<McpServerSnapshot> Servers { get; }

	/// <summary>Raised when a server connects, disconnects, or republishes its tools.</summary>
	event Action? Changed;

	/// <summary>
	/// Connect every enabled server and publish its tools. Completes once each server has either
	/// connected or failed its first attempt; servers whose first attempt failed keep retrying in the
	/// background unless they are configured to fail startup.
	/// </summary>
	Task StartAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Drop every connection and reconnect from the current configuration. Called after the settings
	/// view changes a server, so an edited endpoint or a newly supplied key takes effect without a
	/// restart.
	/// </summary>
	Task ReloadAsync(CancellationToken cancellationToken = default);
}

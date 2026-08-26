namespace Suharness.Abstractions;

/// <summary>
/// One language server the harness may run, however it was declared: shipped as a built-in preset
/// or written by hand into <c>lsp.json</c>. Both produce this same record, so a hand-written server
/// is a first-class citizen rather than a second mechanism.
/// </summary>
/// <param name="Command">
/// The executable to launch. It is resolved against <c>PATH</c>, so a server that is not installed
/// simply does not appear rather than failing at query time.
/// </param>
/// <param name="Extensions">File extension to LSP language id, for example <c>.cs</c> to <c>csharp</c>.</param>
/// <param name="TimeoutMs">Budget for one request once the server is running and initialised.</param>
/// <param name="InitializeTimeoutMs">
/// Budget for the handshake. Much larger than a request budget because this is where a server loads
/// the project: a Roslyn or JDT server can take a minute to open a large solution, and that cost is
/// paid once per session rather than once per query.
/// </param>
/// <param name="IdleShutdownMs">
/// How long an initialised server is kept alive with no queries. Holding it costs memory; restarting
/// it costs the whole handshake again, so the default leans towards keeping it.
/// </param>
/// <param name="WarmupRetries">
/// How many times a workspace-wide symbol query is retried while the server reports nothing. Servers
/// that index in the background answer an early query with an empty result rather than an error, and
/// an empty result is indistinguishable from "no such symbol" unless it is retried.
/// </param>
public sealed record LanguageServerConfig(
	string Id,
	string Command,
	IReadOnlyList<string>? Args,
	IReadOnlyDictionary<string, string>? Extensions,
	IReadOnlyDictionary<string, string?>? Env = null,
	string? Cwd = null,
	int TimeoutMs = 20_000,
	bool Enabled = true,
	int InitializeTimeoutMs = 90_000,
	int IdleShutdownMs = 600_000,
	int WarmupRetries = 4);

/// <summary>What the harness can tell the model about one server without starting it.</summary>
/// <param name="Resolved">The absolute executable path, or null when the command is not installed.</param>
/// <param name="Running">True when a session for this server is initialised and holding a process.</param>
public sealed record LanguageServerDescriptor(
	string Id,
	string Command,
	string? Resolved,
	IReadOnlyList<string> Extensions,
	bool BuiltIn,
	bool Running)
{
	public bool Available => Resolved is not null;
}

/// <summary>
/// Semantic code navigation through the Language Server Protocol.
/// </summary>
/// <remarks>
/// Sessions are pooled per workspace and language. The first query pays for the handshake and the
/// project load; every later query reuses the running process. An earlier implementation started a
/// server, asked one question, and killed the process tree before returning, which meant every
/// query paid a project load it could rarely finish inside its own timeout.
/// </remarks>
public interface ILanguageServerService
{
	/// <summary>True when at least one enabled server for this workspace has an executable that resolves.</summary>
	bool IsConfiguredFor(string workspaceRoot);

	/// <summary>Every enabled server for this workspace, whether or not its command is installed.</summary>
	IReadOnlyList<LanguageServerDescriptor> Servers(string workspaceRoot);

	/// <summary>
	/// Ask about one position: <c>definition</c>, <c>references</c>, <c>implementation</c>,
	/// <c>hover</c>, or <c>document_symbols</c>. Line and character are zero-based, as in the protocol.
	/// </summary>
	Task<string> QueryAsync(
		string workspaceRoot,
		string operation,
		string filePath,
		int line,
		int character,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Search the whole project for a symbol by name. This is the only operation that can be entered
	/// from a name rather than a position, which is what makes the rest of the surface reachable.
	/// </summary>
	/// <param name="languageHintPath">
	/// A path whose extension selects which server to ask. Without it the call succeeds only when
	/// exactly one server is available, because starting every installed language server to answer
	/// one question is not a reasonable default.
	/// </param>
	Task<string> SymbolsAsync(
		string workspaceRoot,
		string query,
		string? languageHintPath = null,
		CancellationToken cancellationToken = default);
}

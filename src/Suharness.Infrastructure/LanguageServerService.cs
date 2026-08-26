using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Semantic navigation over a pool of long-lived language servers.
/// </summary>
/// <remarks>
/// <para>
/// Two things changed here relative to the first implementation, and they are the two reasons the
/// semantic path was unreachable. Sessions are now pooled per workspace and server, so the project
/// load is paid once instead of on every query; and <c>workspace/symbol</c> is implemented, so the
/// symbol graph can be entered from a name. Without the first the tool timed out; without the second
/// it could only answer questions about a position the caller had to find by other means, which was
/// the search it was supposed to replace.
/// </para>
/// <para>
/// Server definitions come from <c>lsp.json</c> merged with the shipped presets for .NET, Java, and
/// Python. Any other language is added by declaring it in that file; there is no separate mechanism
/// for built-ins, so an added server behaves exactly as a shipped one does.
/// </para>
/// </remarks>
public sealed class LanguageServerService : ILanguageServerService, IAsyncDisposable
{
	private const int MaxLocations = 100;
	private const int MaxSymbols = 200;

	/// <summary>Pause between attempts while a server is still building its own index.</summary>
	private static readonly TimeSpan WarmupDelay = TimeSpan.FromMilliseconds(750);

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
	};

	private readonly string _configurationPath;
	private readonly string? _dataRoot;
	private readonly ConcurrentDictionary<string, LanguageServerSession> _sessions =
		new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
	private readonly Timer _reaper;
	private bool _disposed;

	/// <param name="configurationRoot">Directory holding <c>lsp.json</c>.</param>
	/// <param name="dataRoot">
	/// Harness-owned directory for servers that need scratch space of their own. Eclipse JDT is the
	/// motivating case: it insists on a writable data directory and fills whatever it is given with
	/// derived state, which must not be the user's project.
	/// </param>
	public LanguageServerService(string configurationRoot, string? dataRoot = null)
	{
		_configurationPath = Path.Combine(Path.GetFullPath(configurationRoot), "lsp.json");
		_dataRoot = string.IsNullOrWhiteSpace(dataRoot) ? null : Path.GetFullPath(dataRoot);
		_reaper = new Timer(_ => ReapIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
	}

	public bool IsConfiguredFor(string workspaceRoot) => Servers(workspaceRoot).Any(server => server.Available);

	public IReadOnlyList<LanguageServerDescriptor> Servers(string workspaceRoot) =>
	[
		.. Configured(workspaceRoot).Select(server => new LanguageServerDescriptor(
			server.Id,
			server.Command,
			LanguageServerCatalog.ResolveExecutable(server.Command),
			[.. (server.Extensions?.Keys ?? []).Select(Normalize).Order(StringComparer.Ordinal)],
			LanguageServerCatalog.IsBuiltIn(server.Id),
			_sessions.Values.Any(session => session.Id == server.Id && session.Running))),
	];

	public async Task<string> QueryAsync(
		string workspaceRoot,
		string operation,
		string filePath,
		int line,
		int character,
		CancellationToken cancellationToken = default)
	{
		if (line < 0 || character < 0)
			throw new ArgumentOutOfRangeException(nameof(line), "Line and character must be non-negative.");

		var method = operation.Trim().ToLowerInvariant() switch
		{
			"definition" => "textDocument/definition",
			"references" => "textDocument/references",
			"implementation" => "textDocument/implementation",
			"hover" => "textDocument/hover",
			"document_symbols" or "documentsymbols" => "textDocument/documentSymbol",
			_ => throw new InvalidOperationException(
				"operation must be definition, references, implementation, hover, document_symbols, or symbol"),
		};

		if (!File.Exists(filePath)) throw new FileNotFoundException("The source file does not exist.", filePath);
		var (session, languageId) = ServerFor(workspaceRoot, filePath);
		await session.EnsureStartedAsync(cancellationToken);
		await session.EnsureDocumentAsync(filePath, languageId, cancellationToken);

		var documentUri = LanguageServerSession.FileUri(filePath);
		object parameters = method switch
		{
			"textDocument/references" => new
			{
				textDocument = new { uri = documentUri },
				position = new { line, character },
				context = new { includeDeclaration = true },
			},
			"textDocument/documentSymbol" => new { textDocument = new { uri = documentUri } },
			_ => new { textDocument = new { uri = documentUri }, position = new { line, character } },
		};

		var result = await session.RequestAsync(method, parameters, cancellationToken);
		return method switch
		{
			"textDocument/hover" => NormalizeHover(result),
			"textDocument/documentSymbol" => NormalizeSymbols(result, workspaceRoot, Display(filePath, workspaceRoot)),
			_ => NormalizeLocations(result, workspaceRoot),
		};
	}

	public async Task<string> SymbolsAsync(
		string workspaceRoot,
		string query,
		string? languageHintPath = null,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("query must be a non-empty string.");
		var session = SymbolServerFor(workspaceRoot, languageHintPath);
		await session.EnsureStartedAsync(cancellationToken);
		var config = ServerConfig(session.Id, workspaceRoot);

		// A server that indexes in the background answers early queries with an empty list rather than
		// an error, and a large workspace may not answer the first query inside the ordinary request
		// budget. The first attempt gets the startup budget because project indexing is part of startup
		// in practice even when the protocol handshake has already completed. Later calls return to the
		// ordinary request budget once one bounded warmup has completed.
		var attempts = Math.Max(1, config?.WarmupRetries ?? 1);
		var normalTimeoutMs = config?.TimeoutMs ?? 20_000;
		var warmupTimeoutMs = session.WorkspaceSymbolsWarmed
			? normalTimeoutMs
			: Math.Max(normalTimeoutMs, config?.InitializeTimeoutMs ?? normalTimeoutMs);
		JsonElement result = default;
		for (var attempt = 0; attempt < attempts; attempt++)
		{
			try
			{
				result = await session.RequestAsync(
					"workspace/symbol",
					new { query },
					cancellationToken,
					attempt == 0 ? warmupTimeoutMs : normalTimeoutMs);
			}
			catch (TimeoutException) when (attempt + 1 < attempts)
			{
				await Task.Delay(WarmupDelay, cancellationToken);
				continue;
			}
			if (result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 0) break;
			if (attempt + 1 < attempts) await Task.Delay(WarmupDelay, cancellationToken);
		}
		session.MarkWorkspaceSymbolsWarmed();
		return NormalizeSymbols(result, workspaceRoot, null);
	}

	// ---------------------------------------------------------------- server selection

	/// <summary>The enabled servers for a workspace: what the file declares, merged with the presets.</summary>
	private IReadOnlyList<LanguageServerConfig> Configured(string workspaceRoot)
	{
		var (declared, useBuiltIns) = ReadConfiguration();
		return LanguageServerCatalog.Resolve(declared, useBuiltIns, _dataRoot, workspaceRoot)
			.Where(Valid)
			.OrderBy(server => server.Id, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private LanguageServerConfig? ServerConfig(string id, string workspaceRoot) =>
		Configured(workspaceRoot).FirstOrDefault(server => server.Id == id);

	private (LanguageServerSession Session, string LanguageId) ServerFor(string workspaceRoot, string filePath)
	{
		var extension = Normalize(Path.GetExtension(filePath));
		var server = Configured(workspaceRoot).FirstOrDefault(config =>
			config.Extensions?.Keys.Any(item => Normalize(item) == extension) == true
			&& LanguageServerCatalog.ResolveExecutable(config.Command) is not null)
			?? throw new InvalidOperationException(Unavailable(extension, workspaceRoot));
		var languageId = server.Extensions!.First(pair => Normalize(pair.Key) == extension).Value;
		return (Session(workspaceRoot, server), languageId);
	}

	private LanguageServerSession SymbolServerFor(string workspaceRoot, string? languageHintPath)
	{
		if (!string.IsNullOrWhiteSpace(languageHintPath)) return ServerFor(workspaceRoot, languageHintPath).Session;

		var available = Configured(workspaceRoot)
			.Where(config => LanguageServerCatalog.ResolveExecutable(config.Command) is not null)
			.ToArray();
		return available.Length switch
		{
			0 => throw new InvalidOperationException("No language server is available for this workspace."),
			1 => Session(workspaceRoot, available[0]),
			// Starting every installed server to answer one question would cost several project loads,
			// so the caller is asked to say which language it means instead.
			_ => throw new InvalidOperationException(
				"Several language servers are available ("
				+ string.Join(", ", available.Select(server => server.Id))
				+ "). Pass file_path so the right one is chosen."),
		};
	}

	private LanguageServerSession Session(string workspaceRoot, LanguageServerConfig config)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		return _sessions.GetOrAdd($"{root}\0{config.Id}", _ => new LanguageServerSession(config, root));
	}

	private string Unavailable(string extension, string workspaceRoot)
	{
		var known = Configured(workspaceRoot)
			.Where(config => config.Extensions?.Keys.Any(item => Normalize(item) == extension) == true)
			.ToArray();
		if (known.Length == 0) return $"No language server is configured for '{extension}'.";
		return $"A language server for '{extension}' is configured ({string.Join(", ", known.Select(server => server.Id))}) "
			+ "but its command was not found on PATH.";
	}

	private (IReadOnlyList<LanguageServerConfig> Declared, bool UseBuiltIns) ReadConfiguration()
	{
		if (!File.Exists(_configurationPath)) return ([], true);
		try
		{
			var document = JsonSerializer.Deserialize<ConfigurationFile>(File.ReadAllText(_configurationPath), JsonOptions);
			return (document?.Servers ?? [], document?.UseBuiltIns ?? true);
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			// A malformed file must not take the built-in servers down with it.
			return ([], true);
		}
	}

	private static bool Valid(LanguageServerConfig server) => server.Enabled
		&& !string.IsNullOrWhiteSpace(server.Id)
		&& !string.IsNullOrWhiteSpace(server.Command)
		&& server.TimeoutMs > 0
		&& server.Extensions is { Count: > 0 }
		&& server.Extensions.All(pair => Normalize(pair.Key).Length > 1 && !string.IsNullOrWhiteSpace(pair.Value));

	/// <summary>
	/// Shut down sessions nothing has used for their configured idle window. A language server holds
	/// a whole project in memory, so an abandoned one is worth reclaiming — but only after long
	/// enough that reclaiming it does not simply buy another project load on the next question.
	/// </summary>
	private void ReapIdle()
	{
		foreach (var (key, session) in _sessions.ToArray())
		{
			if (Environment.TickCount64 - session.LastUsedTick < session.IdleShutdownMs) continue;
			if (!_sessions.TryRemove(key, out var removed)) continue;
			_ = removed.DisposeAsync().AsTask();
		}
	}

	// ---------------------------------------------------------------- result shaping

	private static string NormalizeLocations(JsonElement result, string workspaceRoot)
	{
		var values = result.ValueKind switch
		{
			JsonValueKind.Array => result.EnumerateArray().ToArray(),
			JsonValueKind.Object => new[] { result },
			_ => [],
		};
		var locations = new List<object>();
		foreach (var item in values.Take(MaxLocations))
		{
			var uri = String(item, "uri") ?? String(item, "targetUri");
			var range = Property(item, "range") ?? Property(item, "targetSelectionRange") ?? Property(item, "targetRange");
			if (uri is null || range is null || !TryPosition(range.Value, "start", out var start) || !TryPosition(range.Value, "end", out var end)) continue;
			locations.Add(new
			{
				path = DisplayUri(uri, workspaceRoot),
				line = start.Line + 1,
				character = start.Character + 1,
				endLine = end.Line + 1,
				endCharacter = end.Character + 1,
			});
		}
		return JsonSerializer.Serialize(new { kind = "locations", locations, truncated = values.Length > MaxLocations }, JsonOptions);
	}

	/// <summary>
	/// Flatten either symbol shape into one list. The protocol has two: a flat <c>SymbolInformation</c>
	/// carrying a location, and a nested <c>DocumentSymbol</c> tree carrying children, and servers
	/// differ on which they return for the same request.
	/// </summary>
	private static string NormalizeSymbols(JsonElement result, string workspaceRoot, string? defaultPath)
	{
		var symbols = new List<object>();
		Collect(result, null);
		return JsonSerializer.Serialize(
			new { kind = "symbols", symbols = symbols.Take(MaxSymbols), truncated = symbols.Count > MaxSymbols },
			JsonOptions);

		void Collect(JsonElement element, string? container)
		{
			if (element.ValueKind == JsonValueKind.Array)
			{
				foreach (var item in element.EnumerateArray()) Collect(item, container);
				return;
			}
			if (element.ValueKind != JsonValueKind.Object) return;
			if (String(element, "name") is not { } name) return;

			string? path = defaultPath;
			(int Line, int Character)? start = null;
			if (Property(element, "location") is { } location)
			{
				// SymbolInformation: the position travels in a location alongside its own document uri.
				if (String(location, "uri") is { } uri) path = DisplayUri(uri, workspaceRoot);
				if (Property(location, "range") is { } range && TryPosition(range, "start", out var located)) start = located;
			}
			else if ((Property(element, "selectionRange") ?? Property(element, "range")) is { } own)
			{
				// DocumentSymbol: the position is on the symbol itself and the document is implied by
				// the request, which is why a default path is carried in.
				if (TryPosition(own, "start", out var located)) start = located;
			}

			symbols.Add(new
			{
				name,
				kind = SymbolKind(element),
				path,
				line = start is { } position ? position.Line + 1 : (int?)null,
				character = start is { } column ? column.Character + 1 : (int?)null,
				container = String(element, "containerName") ?? container,
			});

			if (Property(element, "children") is { } children)
			{
				Collect(children, container is null ? name : $"{container}.{name}");
			}
		}
	}

	/// <summary>The protocol numbers symbol kinds; the model reads names.</summary>
	private static string SymbolKind(JsonElement element) =>
		element.TryGetProperty("kind", out var kind) && kind.TryGetInt32(out var value) && value is >= 1 and <= 26
			? KindNames[value - 1]
			: "symbol";

	private static readonly string[] KindNames =
	[
		"file", "module", "namespace", "package", "class", "method", "property", "field", "constructor",
		"enum", "interface", "function", "variable", "constant", "string", "number", "boolean", "array",
		"object", "key", "null", "enum member", "struct", "event", "operator", "type parameter",
	];

	private static string NormalizeHover(JsonElement result)
	{
		if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
			return JsonSerializer.Serialize(new { kind = "hover", contents = (string?)null }, JsonOptions);
		var contents = result.TryGetProperty("contents", out var value) ? HoverText(value) : null;
		object? range = null;
		if (result.TryGetProperty("range", out var rangeValue)
			&& TryPosition(rangeValue, "start", out var start)
			&& TryPosition(rangeValue, "end", out var end))
		{
			range = new { line = start.Line + 1, character = start.Character + 1, endLine = end.Line + 1, endCharacter = end.Character + 1 };
		}
		return JsonSerializer.Serialize(new { kind = "hover", contents, range }, JsonOptions);
	}

	private static string? HoverText(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.String => value.GetString(),
		JsonValueKind.Object when value.TryGetProperty("value", out var text) => text.GetString(),
		JsonValueKind.Array => string.Join("\n\n", value.EnumerateArray().Select(HoverText).Where(text => !string.IsNullOrWhiteSpace(text))),
		_ => value.GetRawText(),
	};

	private static bool TryPosition(JsonElement range, string name, out (int Line, int Character) position)
	{
		position = default;
		if (!range.TryGetProperty(name, out var value)
			|| !value.TryGetProperty("line", out var line)
			|| !value.TryGetProperty("character", out var character)
			|| !line.TryGetInt32(out var lineValue)
			|| !character.TryGetInt32(out var characterValue)) return false;
		position = (lineValue, characterValue);
		return true;
	}

	private static JsonElement? Property(JsonElement value, string name) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : null;

	private static string? String(JsonElement value, string name) =>
		value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static string Normalize(string extension)
	{
		var value = extension.Trim().ToLowerInvariant();
		return value.StartsWith('.') ? value : "." + value;
	}

	private static string DisplayUri(string uri, string workspaceRoot)
	{
		if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return uri;
		return Display(parsed.LocalPath, workspaceRoot);
	}

	private static string Display(string path, string workspaceRoot)
	{
		var full = Path.GetFullPath(path);
		var relative = Path.GetRelativePath(workspaceRoot, full);
		return relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == ".."
			? full
			: relative.Replace(Path.DirectorySeparatorChar, '/');
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed) return;
		_disposed = true;
		await _reaper.DisposeAsync();
		foreach (var (key, session) in _sessions.ToArray())
		{
			if (_sessions.TryRemove(key, out var removed)) await removed.DisposeAsync();
		}
	}

	/// <param name="UseBuiltIns">
	/// Whether the shipped presets are merged in. Turning it off leaves only what the file declares,
	/// which is how a user takes full control of the set.
	/// </param>
	private sealed record ConfigurationFile(IReadOnlyList<LanguageServerConfig> Servers, bool? UseBuiltIns);
}

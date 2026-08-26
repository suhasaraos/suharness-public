using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// One long-lived language server process, initialised once and reused for every later query.
/// </summary>
/// <remarks>
/// <para>
/// The lifetime is the whole point. The protocol splits work into an expensive handshake — during
/// which a server reads the project, restores packages, and builds its own index — and cheap
/// per-position queries afterwards. Starting a process per query pays the handshake every time,
/// which for any real .NET, Java, or Python project exceeds the query budget on its own, so the
/// semantic path effectively never answered.
/// </para>
/// <para>
/// A single reader task owns the output stream and dispatches by request id, because responses are
/// not ordered: a server may interleave progress notifications and its own requests between the
/// call and its answer. Writes are serialised separately, since two concurrent requests would
/// otherwise interleave their framing headers on the input stream.
/// </para>
/// </remarks>
internal sealed class LanguageServerSession(LanguageServerConfig config, string workspaceRoot) : IAsyncDisposable
{
	private const int MaxHeaderBytes = 16 * 1024;
	private const int MaxMessageBytes = 64 * 1024 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly SemaphoreSlim _startGate = new(1, 1);
	private readonly SemaphoreSlim _writeGate = new(1, 1);
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
	private readonly Dictionary<string, DocumentState> _documents = new(
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	private Process? _process;
	private Task? _reader;
	private int _nextId;
	private bool _initialised;
	private bool _workspaceSymbolsWarmed;
	private bool _disposed;

	/// <summary>What the server was last told about one open document.</summary>
	private sealed record DocumentState(int Version, long ModifiedTicks, long Size);

	public string Id => config.Id;

	/// <summary>How long this session may sit unused before the reaper is entitled to close it.</summary>
	public int IdleShutdownMs => config.IdleShutdownMs;

	/// <summary>When this session last did useful work, for the idle reaper.</summary>
	public long LastUsedTick { get; private set; } = Environment.TickCount64;

	public bool Running => _initialised && _process is { HasExited: false };

	/// <summary>Whether this process has completed its first bounded workspace-symbol warmup.</summary>
	public bool WorkspaceSymbolsWarmed => _workspaceSymbolsWarmed;

	public void MarkWorkspaceSymbolsWarmed() => _workspaceSymbolsWarmed = true;

	/// <summary>Bring the process up and complete the handshake, at most once.</summary>
	public async Task EnsureStartedAsync(CancellationToken cancellationToken)
	{
		if (Running) return;
		await _startGate.WaitAsync(cancellationToken);
		try
		{
			if (Running) return;
			// A server that died is replaced rather than reported: a crashed process is a transient
			// condition the next query can recover from on its own.
			if (_process is not null) await TeardownAsync();

			_process = Start();
			_reader = Task.Run(ReadLoopAsync);
			// stderr is drained so a chatty server cannot fill its pipe buffer and deadlock its own
			// stdout; the content is not otherwise useful.
			_ = Task.Run(async () =>
			{
				try { await _process.StandardError.ReadToEndAsync(); }
				catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) { }
			});

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromMilliseconds(config.InitializeTimeoutMs));
			var rootUri = FileUri(workspaceRoot);
			// The handshake result is not read, but it is a pooled-memory document and has to be
			// returned rather than dropped.
			using var handshake = await RequestCoreAsync("initialize", new
			{
				processId = Environment.ProcessId,
				clientInfo = new { name = "SuHarness", version = "0.1.0" },
				rootUri,
				rootPath = workspaceRoot,
				capabilities = new
				{
					workspace = new { symbol = new { dynamicRegistration = false }, workspaceFolders = true },
					textDocument = new
					{
						definition = new { dynamicRegistration = false },
						references = new { dynamicRegistration = false },
						implementation = new { dynamicRegistration = false },
						hover = new { dynamicRegistration = false, contentFormat = new[] { "markdown", "plaintext" } },
						documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
						synchronization = new { dynamicRegistration = false, didSave = false },
					},
				},
				workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(workspaceRoot) } },
			}, timeout.Token);
			await NotifyAsync("initialized", new { }, cancellationToken);
			_initialised = true;
			LastUsedTick = Environment.TickCount64;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			await TeardownAsync();
			throw new TimeoutException(
				$"Language server '{config.Id}' did not finish starting within {config.InitializeTimeoutMs} ms.");
		}
		catch
		{
			await TeardownAsync();
			throw;
		}
		finally { _startGate.Release(); }
	}

	/// <summary>
	/// Tell the server about a document, opening it the first time and re-sending its text whenever
	/// the file has changed on disk since it was opened. Without the update a query answers against
	/// the text the server read when it opened the file, which after any edit is the wrong text.
	/// </summary>
	public async Task EnsureDocumentAsync(string filePath, string languageId, CancellationToken cancellationToken)
	{
		var info = new FileInfo(filePath);
		if (!info.Exists) throw new FileNotFoundException("The source file does not exist.", filePath);
		var ticks = info.LastWriteTimeUtc.Ticks;
		var size = info.Length;

		DocumentState? known;
		lock (_documents) _documents.TryGetValue(filePath, out known);
		if (known is not null && known.ModifiedTicks == ticks && known.Size == size) return;

		var text = await File.ReadAllTextAsync(filePath, cancellationToken);
		var uri = FileUri(filePath);
		if (known is null)
		{
			await NotifyAsync("textDocument/didOpen", new
			{
				textDocument = new { uri, languageId, version = 1, text },
			}, cancellationToken);
			lock (_documents) _documents[filePath] = new DocumentState(1, ticks, size);
			return;
		}

		var version = known.Version + 1;
		await NotifyAsync("textDocument/didChange", new
		{
			textDocument = new { uri, version },
			contentChanges = new[] { new { text } },
		}, cancellationToken);
		lock (_documents) _documents[filePath] = new DocumentState(version, ticks, size);
	}

	/// <summary>Send one request and wait for its response, bounded by the per-request budget.</summary>
	public async Task<JsonElement> RequestAsync(
		string method,
		object parameters,
		CancellationToken cancellationToken,
		int? timeoutMs = null)
	{
		await EnsureStartedAsync(cancellationToken);
		var requestTimeoutMs = timeoutMs ?? config.TimeoutMs;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromMilliseconds(requestTimeoutMs));
		try
		{
			using var response = await RequestCoreAsync(method, parameters, timeout.Token);
			LastUsedTick = Environment.TickCount64;
			if (response.RootElement.TryGetProperty("error", out var error))
				throw new InvalidOperationException($"Language server error: {error.GetRawText()}");
			return response.RootElement.TryGetProperty("result", out var value) ? value.Clone() : default;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"Language server '{config.Id}' did not answer within {requestTimeoutMs} ms.");
		}
	}

	private async Task<JsonDocument> RequestCoreAsync(string method, object parameters, CancellationToken cancellationToken)
	{
		var id = Interlocked.Increment(ref _nextId);
		var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;
		try
		{
			await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);
			return await completion.Task.WaitAsync(cancellationToken);
		}
		finally { _pending.TryRemove(id, out _); }
	}

	private Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
		SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

	private async Task SendAsync(object message, CancellationToken cancellationToken)
	{
		var stream = _process?.StandardInput.BaseStream
			?? throw new InvalidOperationException($"Language server '{config.Id}' is not running.");
		var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
		var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
		await _writeGate.WaitAsync(cancellationToken);
		try
		{
			await stream.WriteAsync(header, cancellationToken);
			await stream.WriteAsync(payload, cancellationToken);
			await stream.FlushAsync(cancellationToken);
		}
		finally { _writeGate.Release(); }
	}

	/// <summary>
	/// Read framed messages until the stream closes, completing waiters by id. A request from the
	/// server is answered with a null result: refusing to answer at all leaves servers that ask for
	/// configuration during startup waiting forever.
	/// </summary>
	private async Task ReadLoopAsync()
	{
		var process = _process;
		if (process is null) return;
		try
		{
			while (true)
			{
				var document = await ReadMessageAsync(process.StandardOutput.BaseStream, CancellationToken.None);
				var root = document.RootElement;
				var hasId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;
				if (hasId && !root.TryGetProperty("method", out _))
				{
					if (_pending.TryRemove(id.GetInt32(), out var waiter) && waiter.TrySetResult(document)) continue;
					document.Dispose();
					continue;
				}
				if (hasId)
				{
					var reply = id.GetInt32();
					_ = SendAsync(new { jsonrpc = "2.0", id = reply, result = (object?)null }, CancellationToken.None);
				}
				document.Dispose();
			}
		}
		catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException
			or ObjectDisposedException or InvalidOperationException or JsonException or OperationCanceledException)
		{
			// The server exited or produced something unreadable. Everything still waiting is failed
			// here rather than left to time out one budget at a time.
			foreach (var key in _pending.Keys)
			{
				if (_pending.TryRemove(key, out var waiter))
				{
					waiter.TrySetException(new InvalidOperationException(
						$"Language server '{config.Id}' stopped responding."));
				}
			}
			_initialised = false;
			_workspaceSymbolsWarmed = false;
		}
	}

	private static async Task<JsonDocument> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
	{
		var header = new List<byte>();
		var one = new byte[1];
		while (header.Count < MaxHeaderBytes)
		{
			if (await stream.ReadAsync(one, cancellationToken) == 0)
				throw new EndOfStreamException("Language server closed its output stream.");
			header.Add(one[0]);
			if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
		}
		if (header.Count >= MaxHeaderBytes) throw new InvalidDataException("Language server response headers are too large.");

		var headerText = Encoding.ASCII.GetString([.. header]);
		var lengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
		if (lengthLine is null
			|| !int.TryParse(lengthLine[(lengthLine.IndexOf(':') + 1)..].Trim(), out var length)
			|| length is <= 0 or > MaxMessageBytes)
		{
			throw new InvalidDataException("Language server returned an invalid Content-Length header.");
		}

		var payload = new byte[length];
		await stream.ReadExactlyAsync(payload, cancellationToken);
		return JsonDocument.Parse(payload);
	}

	private Process Start()
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = LanguageServerCatalog.ResolveExecutable(config.Command) ?? config.Command,
			WorkingDirectory = string.IsNullOrWhiteSpace(config.Cwd)
				? workspaceRoot
				: Path.GetFullPath(Path.IsPathRooted(config.Cwd) ? config.Cwd : Path.Combine(workspaceRoot, config.Cwd)),
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in config.Args ?? []) startInfo.ArgumentList.Add(argument);
		foreach (var pair in config.Env ?? new Dictionary<string, string?>()) startInfo.Environment[pair.Key] = pair.Value;

		var process = new Process { StartInfo = startInfo };
		if (!process.Start()) throw new InvalidOperationException($"Could not start language server '{config.Id}'.");
		return process;
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed) return;
		_disposed = true;
		await TeardownAsync();
	}

	private async Task TeardownAsync()
	{
		_initialised = false;
		_workspaceSymbolsWarmed = false;
		var process = _process;
		_process = null;
		lock (_documents) _documents.Clear();

		if (process is not null)
		{
			// A polite shutdown is attempted but never waited on: a wedged server is exactly the case
			// where it will not answer, and holding the process is worse than killing it.
			try
			{
				if (!process.HasExited)
				{
					await SendShutdownAsync(process);
					await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(500));
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException) { }

			try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
			catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) { }
			process.Dispose();
		}

		if (_reader is not null)
		{
			try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); }
			catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidOperationException) { }
			_reader = null;
		}

		foreach (var key in _pending.Keys)
		{
			if (_pending.TryRemove(key, out var waiter))
				waiter.TrySetException(new InvalidOperationException($"Language server '{config.Id}' was shut down."));
		}
	}

	private async Task SendShutdownAsync(Process process)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(
			new { jsonrpc = "2.0", id = Interlocked.Increment(ref _nextId), method = "shutdown" }, JsonOptions);
		var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
		var stream = process.StandardInput.BaseStream;
		await stream.WriteAsync(header);
		await stream.WriteAsync(payload);
		await stream.FlushAsync();
	}

	internal static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

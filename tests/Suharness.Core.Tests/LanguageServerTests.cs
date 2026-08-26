using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for pooled language server sessions, the built-in server catalogue, and the name-based
/// entry points built on top of them.
/// </summary>
/// <remarks>
/// The protocol tests drive a small stub server over real stdio rather than mocking the transport,
/// because the two defects being fixed here — a process started per query, and no way to enter the
/// symbol graph from a name — are both properties of how the harness talks to a real process.
/// </remarks>
public sealed class LanguageServerTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-lsp-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	/// <summary>
	/// A stub server that answers the handshake, records how many times it was initialised, and
	/// serves both symbol requests. Counting initialisations is how session reuse is observed.
	/// </summary>
	private const string StubServer = """
		const fs = require('fs');
		const counter = process.argv[2];
		let buffer = Buffer.alloc(0);
		function send(value) {
		  const body = Buffer.from(JSON.stringify(value));
		  process.stdout.write(`Content-Length: ${body.length}\r\n\r\n`);
		  process.stdout.write(body);
		}
		process.stdin.on('data', chunk => {
		  buffer = Buffer.concat([buffer, chunk]);
		  while (true) {
		    const end = buffer.indexOf('\r\n\r\n');
		    if (end < 0) return;
		    const header = buffer.subarray(0, end).toString();
		    const match = /Content-Length:\s*(\d+)/i.exec(header);
		    const length = Number(match[1]);
		    if (buffer.length < end + 4 + length) return;
		    const message = JSON.parse(buffer.subarray(end + 4, end + 4 + length).toString());
		    buffer = buffer.subarray(end + 4 + length);
		    if (message.method === 'initialize') {
		      fs.appendFileSync(counter, 'x');
		      send({ jsonrpc: '2.0', id: message.id, result: { capabilities: {} } });
		    } else if (message.method === 'workspace/symbol') {
		      const reply = { jsonrpc: '2.0', id: message.id, result: [{
		        name: message.params.query,
		        kind: 5,
		        containerName: 'Contoso',
		        location: { uri: 'file:///' + process.env.STUB_ROOT.replace(/\\/g, '/') + '/Sample.cs',
		                    range: { start: { line: 4, character: 2 }, end: { line: 4, character: 9 } } }
		      }] };
		      const delay = Number(process.env.STUB_SYMBOL_DELAY_MS || 0);
		      if (delay > 0) setTimeout(() => send(reply), delay); else send(reply);
		    } else if (message.method === 'textDocument/documentSymbol') {
		      send({ jsonrpc: '2.0', id: message.id, result: [{
		        name: 'Outer', kind: 5,
		        selectionRange: { start: { line: 0, character: 6 }, end: { line: 0, character: 11 } },
		        children: [{ name: 'Inner', kind: 6,
		          selectionRange: { start: { line: 2, character: 8 }, end: { line: 2, character: 13 } } }]
		      }] });
		    } else if (message.id !== undefined) {
		      send({ jsonrpc: '2.0', id: message.id, result: null });
		    }
		  }
		});
		""";

	private (string Workspace, string Configuration, string Counter) StubWorkspace(
		int timeoutMs = 8000,
		int? initializeTimeoutMs = null,
		int symbolDelayMs = 0)
	{
		var workspace = Path.Combine(_root, "workspace");
		var configuration = Path.Combine(_root, "configuration");
		Directory.CreateDirectory(workspace);
		Directory.CreateDirectory(configuration);
		File.WriteAllText(Path.Combine(workspace, "Sample.cs"), "class Sample\n{\n\tvoid Run() { }\n}\n");

		var script = Path.Combine(_root, "stub-lsp.js");
		File.WriteAllText(script, StubServer);
		var counter = Path.Combine(_root, "initialisations.txt");

		File.WriteAllText(Path.Combine(configuration, "lsp.json"), $$"""
			{
			  "useBuiltIns": false,
			  "servers": [
			    {
			      "id": "stub",
			      "command": "node",
			      "args": [{{JsonSerializer.Serialize(script)}}, {{JsonSerializer.Serialize(counter)}}],
			      "extensions": { ".cs": "csharp" },
			      "timeoutMs": {{timeoutMs}},
			      "initializeTimeoutMs": {{initializeTimeoutMs ?? timeoutMs}},
			      "warmupRetries": 1,
			      "env": {
			        "STUB_ROOT": {{JsonSerializer.Serialize(workspace.Replace('\\', '/'))}},
			        "STUB_SYMBOL_DELAY_MS": {{JsonSerializer.Serialize(symbolDelayMs.ToString())}}
			      }
			    }
			  ]
			}
			""");
		return (workspace, configuration, counter);
	}

	private static int Initialisations(string counter) =>
		File.Exists(counter) ? File.ReadAllText(counter).Length : 0;

	// ---------------------------------------------------------------- pooling

	/// <summary>
	/// The change that makes the semantic path usable at all. Previously every query started a
	/// server, completed the handshake, and killed the process, so the project load was paid per
	/// question and routinely exceeded the query budget on its own.
	/// </summary>
	[Fact]
	public async Task ThreeQueriesShareOneServerProcess()
	{
		var (workspace, configuration, counter) = StubWorkspace();
		var source = Path.Combine(workspace, "Sample.cs");
		await using var service = new LanguageServerService(configuration);

		for (var attempt = 0; attempt < 3; attempt++)
		{
			await service.QueryAsync(workspace, "definition", source, 0, 0);
		}

		Assert.Equal(1, Initialisations(counter));
		Assert.True(Assert.Single(service.Servers(workspace)).Running);
	}

	[Fact]
	public async Task DisposingTheServiceStopsTheServerItStarted()
	{
		var (workspace, configuration, _) = StubWorkspace();
		var service = new LanguageServerService(configuration);
		await service.QueryAsync(workspace, "definition", Path.Combine(workspace, "Sample.cs"), 0, 0);
		Assert.True(Assert.Single(service.Servers(workspace)).Running);

		await service.DisposeAsync();

		Assert.False(Assert.Single(service.Servers(workspace)).Running);
	}

	// ---------------------------------------------------------------- symbols

	/// <summary>
	/// The operation the surface was missing. Everything else the protocol offers answers a question
	/// about a position, which the caller could only obtain by searching first.
	/// </summary>
	[Fact]
	public async Task WorkspaceSymbolSearchAnswersFromANameAlone()
	{
		var (workspace, configuration, _) = StubWorkspace();
		await using var service = new LanguageServerService(configuration);

		var result = await service.SymbolsAsync(workspace, "InvoiceService");

		Assert.Contains("\"kind\": \"symbols\"", result);
		Assert.Contains("\"name\": \"InvoiceService\"", result);
		Assert.Contains("\"path\": \"Sample.cs\"", result);
		// Protocol positions are zero-based; everything the model sees is one-based.
		Assert.Contains("\"line\": 5", result);
		Assert.Contains("\"container\": \"Contoso\"", result);
		Assert.Contains("\"kind\": \"class\"", result);
	}

	[Fact]
	public async Task FirstWorkspaceSymbolQueryUsesStartupBudgetThenNormalBudget()
	{
		var (workspace, configuration, _) = StubWorkspace(
			timeoutMs: 100,
			initializeTimeoutMs: 2_000,
			symbolDelayMs: 250);
		await using var service = new LanguageServerService(configuration);

		var first = await service.SymbolsAsync(workspace, "FirstSymbol");
		var second = await Assert.ThrowsAsync<TimeoutException>(
			() => service.SymbolsAsync(workspace, "SecondSymbol"));

		Assert.Contains("\"name\": \"FirstSymbol\"", first);
		Assert.Contains("within 100 ms", second.Message);
	}

	[Fact]
	public async Task NestedDocumentSymbolsAreFlattenedWithTheirContainers()
	{
		var (workspace, configuration, _) = StubWorkspace();
		await using var service = new LanguageServerService(configuration);

		var result = await service.QueryAsync(
			workspace, "document_symbols", Path.Combine(workspace, "Sample.cs"), 0, 0);

		Assert.Contains("\"name\": \"Outer\"", result);
		Assert.Contains("\"name\": \"Inner\"", result);
		Assert.Contains("\"container\": \"Outer\"", result);
		Assert.Contains("\"path\": \"Sample.cs\"", result);
	}

	[Fact]
	public async Task AWorkspaceSymbolQueryWithNoServerSaysSoPlainly()
	{
		var configuration = Path.Combine(_root, "empty-configuration");
		Directory.CreateDirectory(configuration);
		File.WriteAllText(Path.Combine(configuration, "lsp.json"), """{ "useBuiltIns": false, "servers": [] }""");
		await using var service = new LanguageServerService(configuration);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => service.SymbolsAsync(_root, "Anything"));

		Assert.Contains("No language server is available", error.Message);
	}

	// ---------------------------------------------------------------- catalogue

	[Fact]
	public void EveryBuiltInPresetIsWellFormed()
	{
		Assert.NotEmpty(LanguageServerCatalog.BuiltIns);
		foreach (var preset in LanguageServerCatalog.BuiltIns)
		{
			Assert.False(string.IsNullOrWhiteSpace(preset.Id));
			Assert.False(string.IsNullOrWhiteSpace(preset.Command));
			Assert.NotEmpty(preset.Extensions);
			Assert.All(preset.Extensions, pair =>
			{
				Assert.StartsWith(".", pair.Key);
				Assert.False(string.IsNullOrWhiteSpace(pair.Value));
			});
			Assert.True(LanguageServerCatalog.IsBuiltIn(preset.Id));
		}
	}

	[Fact]
	public void TheShippedPresetsCoverDotNetJavaAndPython()
	{
		var languages = LanguageServerCatalog.BuiltIns.Select(preset => preset.Language).Distinct().ToArray();

		Assert.Contains("dotnet", languages);
		Assert.Contains("java", languages);
		Assert.Contains("python", languages);
		Assert.Contains(LanguageServerCatalog.BuiltIns, preset => preset.Extensions.ContainsKey(".cs"));
		Assert.Contains(LanguageServerCatalog.BuiltIns, preset => preset.Extensions.ContainsKey(".java"));
		Assert.Contains(LanguageServerCatalog.BuiltIns, preset => preset.Extensions.ContainsKey(".py"));
	}

	/// <summary>
	/// A preset is a recipe, not a promise. Nothing is installed on the user's behalf, so a preset
	/// whose command is absent must not become a server that fails on first use.
	/// </summary>
	[Fact]
	public void OnlyPresetsWhoseCommandIsInstalledBecomeServers()
	{
		var resolved = LanguageServerCatalog.Resolve([], useBuiltIns: true, dataRoot: _root, workspaceRoot: _root);

		Assert.All(resolved, server => Assert.NotNull(LanguageServerCatalog.ResolveExecutable(server.Command)));
		Assert.Null(LanguageServerCatalog.ResolveExecutable("suharness-definitely-not-installed"));
		Assert.NotNull(LanguageServerCatalog.ResolveExecutable("node"));
	}

	[Fact]
	public void OnlyOnePresetPerLanguageIsUsed()
	{
		var resolved = LanguageServerCatalog.Resolve([], useBuiltIns: true, dataRoot: _root, workspaceRoot: _root);

		var languages = resolved
			.Select(server => LanguageServerCatalog.BuiltIns.First(preset => preset.Id == server.Id).Language)
			.ToArray();
		Assert.Equal(languages.Length, languages.Distinct().Count());
	}

	[Fact]
	public void ADeclaredServerReplacesTheBuiltInWithTheSameId()
	{
		var replacement = new LanguageServerConfig(
			"python-pyright", "node", ["custom.js"], new Dictionary<string, string> { [".py"] = "python" });

		var resolved = LanguageServerCatalog.Resolve([replacement], useBuiltIns: true, dataRoot: _root, workspaceRoot: _root);

		var pyright = Assert.Single(resolved, server => server.Id == "python-pyright");
		Assert.Equal("node", pyright.Command);
	}

	[Fact]
	public void ADeclaredServerForALanguageSuppressesThePresetsForThatLanguage()
	{
		var mine = new LanguageServerConfig(
			"my-python", "node", ["mine.js"], new Dictionary<string, string> { [".py"] = "python" });

		var resolved = LanguageServerCatalog.Resolve([mine], useBuiltIns: true, dataRoot: _root, workspaceRoot: _root);

		Assert.Contains(resolved, server => server.Id == "my-python");
		Assert.DoesNotContain(resolved, server => server.Id.StartsWith("python-", StringComparison.Ordinal));
	}

	[Fact]
	public void TurningBuiltInsOffLeavesOnlyWhatWasDeclared()
	{
		var mine = new LanguageServerConfig(
			"mine", "node", [], new Dictionary<string, string> { [".zig"] = "zig" });

		var resolved = LanguageServerCatalog.Resolve([mine], useBuiltIns: false, dataRoot: _root, workspaceRoot: _root);

		Assert.Equal("mine", Assert.Single(resolved).Id);
	}

	/// <summary>
	/// Eclipse JDT fills whatever directory it is given with derived state, so it must never be
	/// pointed at the project. The substituted path has to be harness-owned and outside the workspace.
	/// </summary>
	[Fact]
	public void TheJavaPresetAsksForAHarnessOwnedDataDirectory()
	{
		var jdtls = LanguageServerCatalog.BuiltIns.Single(preset => preset.Id == "java-jdtls");

		Assert.Contains("-data", jdtls.Args);
		Assert.Contains(LanguageServerCatalog.DataDirectoryToken, jdtls.Args);
	}

	/// <summary>
	/// A preset that cannot be given the scratch directory it requires is dropped rather than
	/// launched without one, which for Eclipse JDT would mean it chooses a directory itself.
	/// </summary>
	[Fact]
	public void APresetNeedingADataDirectoryIsSkippedWhenThereIsNowhereToPutIt()
	{
		var withRoot = LanguageServerCatalog.Resolve([], useBuiltIns: true, dataRoot: _root, workspaceRoot: _root);
		var withoutRoot = LanguageServerCatalog.Resolve([], useBuiltIns: true, dataRoot: null, workspaceRoot: _root);

		Assert.DoesNotContain(withoutRoot, server => server.Id == "java-jdtls");
		// Resolving the catalogue is a pure function: naming a data directory must not create one.
		Assert.All(withRoot, server => Assert.All(
			server.Args ?? [],
			argument => Assert.DoesNotContain(LanguageServerCatalog.DataDirectoryToken, argument)));
	}

	// ---------------------------------------------------------------- tool surface

	private (AIFunction Symbol, AIFunction Lsp, RecordingServer Backend) BuildTools()
	{
		File.WriteAllText(Path.Combine(_root, "Player.cs"),
			"namespace Game;\n\npublic sealed class PlayerController\n{\n\tpublic void Move(int steps) { }\n}\n");
		var files = new WorkspaceIndex();
		var backend = new RecordingServer();
		var tools = new SessionToolset(
			_root,
			languageServers: backend,
			workspaceIndex: files,
			symbols: new SymbolIndex(files)).BuildTools();
		return (
			Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "symbol")),
			Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "lsp")),
			backend);
	}

	[Fact]
	public async Task TheSymbolToolReportsWhereADeclarationLives()
	{
		var (symbol, _, _) = BuildTools();

		var result = (await symbol.InvokeAsync(new AIFunctionArguments(
			new Dictionary<string, object?> { ["name"] = "playercontroller" })))?.ToString();

		Assert.Contains("Player.cs:3", result);
		Assert.Contains("class PlayerController", result);
		Assert.Contains("(in Game)", result);
	}

	[Fact]
	public async Task TheSymbolToolExplainsAnEmptyResultInsteadOfReturningNothing()
	{
		var (symbol, _, _) = BuildTools();

		var result = (await symbol.InvokeAsync(new AIFunctionArguments(
			new Dictionary<string, object?> { ["name"] = "NoSuchThing" })))?.ToString();

		Assert.Contains("No declarations found", result);
		Assert.Contains("Use grep", result);
	}

	/// <summary>
	/// The step that removes the grep-then-lsp dance: a caller that knows a name should not have to
	/// find its line and column before it can ask a semantic question about it.
	/// </summary>
	[Fact]
	public async Task LspResolvesAPositionFromASymbolName()
	{
		var (_, lsp, backend) = BuildTools();

		await lsp.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "definition",
			["file_path"] = "Player.cs",
			["symbol"] = "Move",
		}));

		// "public void Move" sits on line 5, and the identifier starts at column 14 — both one-based,
		// so the protocol sees 4 and 13.
		Assert.Equal(4, backend.Line);
		Assert.Equal(13, backend.Character);
	}

	[Fact]
	public async Task LspSaysWhatIsMissingWhenGivenNeitherAPositionNorASymbol()
	{
		var (_, lsp, _) = BuildTools();

		var result = (await lsp.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "hover",
			["file_path"] = "Player.cs",
		})))?.ToString();

		Assert.Contains("give either symbol, or both line and column", result);
	}

	[Fact]
	public async Task LspPassesAProjectWideSymbolQueryStraightThrough()
	{
		var (_, lsp, backend) = BuildTools();

		await lsp.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "symbol",
			["symbol"] = "PlayerController",
			["file_path"] = "Player.cs",
		}));

		Assert.Equal("PlayerController", backend.SymbolQuery);
		Assert.Equal(Path.Combine(_root, "Player.cs"), backend.SymbolHint);
	}

	private sealed class RecordingServer : ILanguageServerService
	{
		public int Line { get; private set; } = -1;
		public int Character { get; private set; } = -1;
		public string? SymbolQuery { get; private set; }
		public string? SymbolHint { get; private set; }

		public bool IsConfiguredFor(string workspaceRoot) => true;

		public IReadOnlyList<LanguageServerDescriptor> Servers(string workspaceRoot) =>
			[new("recording", "recording", "recording", [".cs"], BuiltIn: false, Running: true)];

		public Task<string> QueryAsync(string workspaceRoot, string operation, string filePath, int line, int character, CancellationToken cancellationToken = default)
		{
			Line = line;
			Character = character;
			return Task.FromResult("{\"kind\":\"locations\"}");
		}

		public Task<string> SymbolsAsync(string workspaceRoot, string query, string? languageHintPath = null, CancellationToken cancellationToken = default)
		{
			SymbolQuery = query;
			SymbolHint = languageHintPath;
			return Task.FromResult("{\"kind\":\"symbols\"}");
		}
	}
}

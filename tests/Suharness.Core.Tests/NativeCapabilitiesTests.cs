using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;

namespace Suharness.Core.Tests;

public sealed class NativeCapabilitiesTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-capabilities-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	[Fact]
	public void SkillsAreDiscoveredWithProjectPrecedenceAndLoadedWithoutFrontMatter()
	{
		var preferred = Path.Combine(_root, ".suharness", "skills", "review", "SKILL.md");
		var fallback = Path.Combine(_root, ".agents", "skills", "review.md");
		Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
		Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
		File.WriteAllText(preferred, "---\nname: review\ndescription: Check risky changes\n---\n# Review\nInspect tests first.\n");
		File.WriteAllText(fallback, "---\nname: review\ndescription: Lower priority\n---\nIgnore this body.\n");

		var skill = Assert.Single(SkillCatalog.Discover(_root));
		Assert.Equal("review", skill.Name);
		Assert.Equal("Check risky changes", skill.Description);
		var body = SkillCatalog.Load(_root, "review");
		Assert.Contains("Inspect tests first", body);
		Assert.DoesNotContain("description:", body);
		Assert.DoesNotContain("Ignore this body", body);
	}

	[Fact]
	public void DisabledAndInvalidSkillsStayOutOfTheCatalog()
	{
		var directory = Path.Combine(_root, ".suharness", "skills");
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, "disabled.md"), "---\nname: disabled\ndescription: Hidden\ndisable-model-invocation: true\n---\nHidden");
		File.WriteAllText(Path.Combine(directory, "invalid.md"), "---\nname: not valid\ndescription: Invalid\n---\nInvalid");

		Assert.Empty(SkillCatalog.Discover(_root));
	}

	[Fact]
	public async Task ImagesAreContentAddressedValidatedAndReplayedAsDataContent()
	{
		var store = new ImageAttachmentStore(Path.Combine(_root, "attachments"));
		byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

		var first = await store.SaveAsync("screen.png", "image/png", png);
		var second = await store.SaveAsync("renamed.png", "image/png", png);

		Assert.Equal(first.Id, second.Id);
		Assert.Equal(png, store.Read(first).Bytes.ToArray());
		var sessionId = Guid.NewGuid();
		var history = HarnessRuntime.BuildHistory([
			new SessionEvent(Guid.NewGuid(), sessionId, "message", "user", "Inspect this", DateTimeOffset.UtcNow, Attachments: [first]),
		], store);
		var message = Assert.Single(history);
		Assert.Equal("Inspect this", Assert.IsType<TextContent>(message.Contents[0]).Text);
		var image = Assert.IsType<DataContent>(message.Contents[1]);
		Assert.Equal("image/png", image.MediaType);
		Assert.Equal("screen.png", image.Name);
		Assert.Equal(png, image.Data.ToArray());
	}

	[Fact]
	public async Task ImageValidationRejectsDeclaredTypeMismatch()
	{
		var store = new ImageAttachmentStore(Path.Combine(_root, "attachments"));
		byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];

		var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("wrong.jpg", "image/jpeg", png));

		Assert.Contains("do not match", error.Message);
	}

	[Fact]
	public async Task LspToolConvertsCoordinatesAndScopesThePath()
	{
		var source = Path.Combine(_root, "Sample.cs");
		File.WriteAllText(source, "class Sample {}\n");
		var backend = new RecordingLanguageServer();
		var tool = Assert.IsAssignableFrom<AIFunction>(new SessionToolset(_root, languageServers: backend)
			.BuildTools()
			.Single(item => item.Name == "lsp"));

		var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "definition",
			["file_path"] = "Sample.cs",
			["line"] = 3,
			["column"] = 7,
		}));

		Assert.Equal("{\"kind\":\"locations\"}", result?.ToString());
		Assert.Equal(Path.GetFullPath(source), backend.FilePath);
		Assert.Equal(2, backend.Line);
		Assert.Equal(6, backend.Character);
	}

	[Fact]
	public async Task LspToolReturnsReadableErrorsAndRefusesEscapingPaths()
	{
		var tool = Assert.IsAssignableFrom<AIFunction>(new SessionToolset(_root, languageServers: new RecordingLanguageServer())
			.BuildTools()
			.Single(item => item.Name == "lsp"));

		var invalidPosition = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "hover",
			["file_path"] = "Sample.cs",
			["line"] = 0,
			["column"] = 1,
		}));
		var escaped = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "hover",
			["file_path"] = "../outside.cs",
			["line"] = 1,
			["column"] = 1,
		}));

		Assert.Contains("positive one-based", invalidPosition?.ToString());
		Assert.Contains("escapes the session workspace", escaped?.ToString());
	}

	[Fact]
	public async Task SearchToolsRemainAvailableAlongsideLanguageServerTools()
	{
		File.WriteAllText(Path.Combine(_root, "Level.cs"), "class MeadowGlideNight { }\n");
		var tools = new SessionToolset(_root, languageServers: new RecordingLanguageServer()).BuildTools();
		var lsp = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "lsp"));
		var glob = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "glob"));
		var grep = Assert.IsAssignableFrom<AIFunction>(tools.Single(tool => tool.Name == "grep"));

		var globResult = await glob.InvokeAsync(new AIFunctionArguments(
			new Dictionary<string, object?> { ["pattern"] = "**/*.cs" }));
		var grepResult = await grep.InvokeAsync(new AIFunctionArguments(
			new Dictionary<string, object?> { ["pattern"] = "Meadow" }));
		var lspResult = await lsp.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
		{
			["operation"] = "symbol",
			["symbol"] = "MeadowGlideNight",
		}));

		Assert.True(tools.IndexOf(lsp) < tools.IndexOf(glob));
		Assert.Contains("Level.cs", globResult?.ToString());
		Assert.Contains("Level.cs", grepResult?.ToString());
		Assert.Equal("{\"kind\":\"symbols\"}", lspResult?.ToString());
	}

	[Fact]
	public async Task LanguageServerServiceSpeaksStdioProtocolAndNormalizesLocations()
	{
		var workspace = Path.Combine(_root, "workspace");
		var configuration = Path.Combine(_root, "configuration");
		Directory.CreateDirectory(workspace);
		Directory.CreateDirectory(configuration);
		var source = Path.Combine(workspace, "Sample.cs");
		File.WriteAllText(source, "class Sample {}\n");
		var serverScript = Path.Combine(_root, "fake-lsp.js");
		File.WriteAllText(serverScript, """
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
			    if (message.id === 1) send({ jsonrpc: '2.0', id: 1, result: { capabilities: { definitionProvider: true } } });
			    if (message.id === 2) send({ jsonrpc: '2.0', id: 2, result: [{ uri: message.params.textDocument.uri, range: { start: { line: 1, character: 2 }, end: { line: 1, character: 8 } } }] });
			  }
			});
			""");
		var config = $$"""
			{
			  "servers": [
			    {
			      "id": "test",
			      "command": "node",
			      "args": [{{System.Text.Json.JsonSerializer.Serialize(serverScript)}}],
			      "extensions": { ".cs": "csharp" },
			      "timeoutMs": 5000
			    }
			  ]
			}
			""";
		File.WriteAllText(Path.Combine(configuration, "lsp.json"), config);
		// The service now holds the server process open between queries, so the test owns its lifetime.
		await using var service = new LanguageServerService(configuration);

		var result = await service.QueryAsync(workspace, "definition", source, 0, 0);

		Assert.Contains("\"path\": \"Sample.cs\"", result);
		Assert.Contains("\"line\": 2", result);
		Assert.Contains("\"character\": 3", result);
		Assert.Contains("\"endCharacter\": 9", result);
	}

	private sealed class RecordingLanguageServer : ILanguageServerService
	{
		public string? FilePath { get; private set; }
		public int Line { get; private set; }
		public int Character { get; private set; }
		public string? SymbolQuery { get; private set; }
		public string? SymbolHint { get; private set; }

		public bool IsConfiguredFor(string workspaceRoot) => true;

		public IReadOnlyList<LanguageServerDescriptor> Servers(string workspaceRoot) =>
			[new("recording", "recording", "recording", [".cs"], BuiltIn: false, Running: true)];

		public Task<string> QueryAsync(string workspaceRoot, string operation, string filePath, int line, int character, CancellationToken cancellationToken = default)
		{
			FilePath = filePath;
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

using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.Diagnostics;
using System.Text.Json;

namespace Suharness.Core.Tests;

public sealed class WorkspaceAgentStateTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-workspace-state-").FullName;
	private readonly WorkspaceIndex _index = new();

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	[Fact]
	public async Task EnsureAndRepositoryProjectionCreateRegenerablePrivateState()
	{
		Write("Demo.sln", string.Empty);
		Write("src/Program.cs", "namespace Demo;\nclass Program { static void Main() {} }");
		Write("package.json", "{}");
		var store = new WorkspaceAgentStateStore(_index, new SymbolIndex(_index));

		await store.ProjectRepositoryAsync(_root);

		var agentRoot = Path.Combine(_root, ".suharness");
		Assert.True(File.Exists(Path.Combine(agentRoot, ".gitignore")));
		Assert.True(Directory.Exists(Path.Combine(agentRoot, "skills")));
		Assert.True(Directory.Exists(Path.Combine(agentRoot, "sessions")));
		Assert.True(File.Exists(Path.Combine(agentRoot, "index.json")));
		Assert.True(File.Exists(Path.Combine(agentRoot, "architecture.md")));
		Assert.True(File.Exists(Path.Combine(agentRoot, "knowledge.json")));
		var ignore = File.ReadAllText(Path.Combine(agentRoot, ".gitignore"));
		Assert.Contains("/sessions/", ignore);
		using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(agentRoot, "index.json")));
		Assert.Equal(1, index.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.DoesNotContain(index.RootElement.GetProperty("files").EnumerateArray(), file =>
			file.GetProperty("path").GetString()!.StartsWith(".suharness/", StringComparison.OrdinalIgnoreCase));
		var program = index.RootElement.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("path").GetString() == "src/Program.cs");
		Assert.Contains(program.GetProperty("symbols").EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "Program");
		Assert.Contains(".NET solution", File.ReadAllText(Path.Combine(agentRoot, "architecture.md")));
	}

	[Fact]
	public async Task GeneratedWorkspaceStateDoesNotAppearInGitStatus()
	{
		Write("src/App.cs", "class App {}");
		Git("init", "--quiet");
		Git("config", "user.email", "test@example.com");
		Git("config", "user.name", "Test");
		Git("add", ".");
		Git("commit", "-m", "initial", "--quiet");
		var store = new WorkspaceAgentStateStore(_index);

		await store.ProjectRepositoryAsync(_root);

		Assert.Equal(string.Empty, Git("status", "--short", "--untracked-files=all").Trim());
	}

	[Fact]
	public async Task WorkspaceInstructionsAndSkillsReachStandardAndWorkflowPrompts()
	{
		Write("AGENTS.md", "broad-rule");
		Write(".github/copilot-instructions.md", "copilot-rule");
		Write(".suharness/instructions.md", "suharness-highest-rule");
		Write(".suharness/skills/review.md", "---\nname: review\ndescription: Review this workspace\n---\nUse local review rules.");
		var store = new WorkspaceAgentStateStore(_index);
		await store.ProjectRepositoryAsync(_root);

		var rendered = WorkspaceInstructions.Render(_root);
		var standard = HarnessSystemPrompt.Build(_root);
		var workflow = HarnessSystemPrompt.Build(_root, toolProfile: AgentToolProfile.WorkflowReadOnly);

		Assert.Contains("broad-rule", rendered);
		Assert.Contains("copilot-rule", rendered);
		Assert.Contains("suharness-highest-rule", rendered);
		Assert.True(rendered.LastIndexOf("suharness-highest-rule", StringComparison.Ordinal) > rendered.IndexOf("broad-rule", StringComparison.Ordinal));
		Assert.Contains("suharness-highest-rule", standard);
		Assert.Contains("suharness-highest-rule", workflow);
		Assert.Contains("Generated architecture", standard);
		Assert.Contains(SkillCatalog.Discover(_root), skill => skill.Name == "review");
	}

	[Fact]
	public async Task GeneratedStateIsHiddenFromGlobButDirectSkillsRemainDiscoverable()
	{
		Write("src/App.cs", "class App {}");
		Write(".suharness/skills/test.md", "---\nname: test\ndescription: Test\n---\nTest content");
		var store = new WorkspaceAgentStateStore(_index);
		await store.ProjectRepositoryAsync(_root);
		var scope = new WorkspaceScope(_root);

		var walked = SearchTools.Glob(scope, "**/*", null, CancellationToken.None);
		var indexed = SearchTools.Glob(scope, "**/*", null, CancellationToken.None, index: _index);

		Assert.Contains("src/App.cs", walked);
		Assert.DoesNotContain(".suharness", walked, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(".suharness", indexed, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Test content", SkillCatalog.Load(_root, "test"));
	}

	[Fact]
	public async Task WorkflowProjectionWritesTasksFindingsSummariesAndKnowledge()
	{
		Write("src/App.cs", "class App {}");
		var store = new WorkspaceAgentStateStore(_index);
		var sessionId = Guid.NewGuid();
		var workflow = """
			{
			  "status":"pass",
			  "repairRounds":0,
			  "summary":"verified",
			  "stages":[
			    {"role":"explorer","status":"completed","summary":"found app","handoff":{"files":["src/App.cs"],"constraints":["Preserve the public contract"],"risks":[]}},
			    {"role":"reviewer","status":"pass","summary":"review passed","handoff":{"status":"pass","summary":"review passed","findings":[]}},
			    {"role":"verifier","status":"pass","summary":"2 tests passed","handoff":{"status":"pass","summary":"2 tests passed","commands":["test => 2 passed"],"findings":[]}}
			  ]
			}
			""";
		var callId = "call-workflow";
		var now = DateTimeOffset.UtcNow;
		var transcript = new SessionTranscript(
			new HarnessSession(sessionId, _root, "Test", now, now),
			[
				new SessionEvent(Guid.NewGuid(), sessionId, "message", "user", "Implement it", now),
				new SessionEvent(Guid.NewGuid(), sessionId, "tool_call", "assistant", "coding_workflow", now, ToolCallId: callId, ToolName: "coding_workflow", ToolArguments: "{}"),
				new SessionEvent(Guid.NewGuid(), sessionId, "tool_result", "tool", workflow, now, ToolCallId: callId),
			]);

		await store.ProjectSessionAsync(transcript);

		var sessionRoot = Path.Combine(_root, ".suharness", "sessions", sessionId.ToString("N"));
		Assert.True(File.Exists(Path.Combine(sessionRoot, "tasks.json")));
		Assert.True(File.Exists(Path.Combine(sessionRoot, "working-memory.json")));
		Assert.True(File.Exists(Path.Combine(sessionRoot, "findings.json")));
		Assert.Equal(3, Directory.GetFiles(Path.Combine(sessionRoot, "summaries"), "*.md").Length);
		Assert.Contains("Preserve the public contract", File.ReadAllText(Path.Combine(sessionRoot, "findings.json")));
		Assert.Contains("Preserve the public contract", File.ReadAllText(Path.Combine(_root, ".suharness", "knowledge.json")));
		Assert.Contains("Preserve the public contract", WorkspaceAgentContext.Render(_root));
	}

	[Fact]
	public async Task MirrorsAttachmentsAndImportsSuAgentWithoutOverwritingInstructions()
	{
		Write(".suharness/instructions.md", "keep-me");
		Write(".suagent/instructions.md", "old-instructions");
		Write(".suagent/skills/legacy.md", "---\nname: legacy\ndescription: Legacy\n---\nLegacy skill");
		Write(".suagent/knowledge.json", "{\"notes\":[{\"topic\":\"Build\",\"text\":\"Use the native build\",\"source\":\"legacy\"}]}");
		var store = new WorkspaceAgentStateStore(_index);
		var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
		var attachment = new ImageAttachmentData(new ImageAttachmentRef(new string('a', 64), "pixel.png", "image/png", bytes.Length), bytes);

		var mirrored = await store.MirrorAttachmentAsync(_root, Guid.Parse("11111111-1111-1111-1111-111111111111"), attachment);
		var imported = await store.ImportSuAgentAsync(_root);

		Assert.NotNull(mirrored);
		Assert.True(File.Exists(Path.Combine(_root, mirrored!.Replace('/', Path.DirectorySeparatorChar))));
		Assert.False(imported.ImportedInstructions);
		Assert.Equal("keep-me", File.ReadAllText(Path.Combine(_root, ".suharness", "instructions.md")));
		Assert.Equal(1, imported.ImportedSkills);
		Assert.Equal(1, imported.ImportedKnowledgeNotes);
		Assert.Contains(SkillCatalog.Discover(_root), skill => skill.Name == "legacy");
	}

	[Fact]
	public async Task CorruptGeneratedStateIsReportedAndRegenerated()
	{
		Write("src/App.cs", "class App {}");
		Write(".suharness/index.json", "not json");
		Write(".suharness/knowledge.json", "not json");
		var store = new WorkspaceAgentStateStore(_index);

		var before = await store.GetStatusAsync(_root);
		await store.ProjectRepositoryAsync(_root);
		var after = await store.GetStatusAsync(_root);

		Assert.Equal(2, before.Warnings.Count);
		Assert.Empty(after.Warnings);
		Assert.NotNull(after.MetadataGeneratedAt);
	}

	[Fact]
	public async Task RuntimeCreatesAndRefreshesWorkspaceSessionProjection()
	{
		Write("src/App.cs", "class App {}");
		var sessions = new JsonlSessionStore(Path.Combine(_root, "harness-sessions"));
		var state = new WorkspaceAgentStateStore(_index);
		var runtime = new HarnessRuntime(sessions, new NullAgentFactory(), workspaceAgentState: state);

		var session = await runtime.CreateSessionAsync(_root);
		await runtime.SubmitAsync(session.Id, "Inspect the repository");

		var sessionRoot = Path.Combine(_root, ".suharness", "sessions", session.Id.ToString("N"));
		Assert.True(File.Exists(Path.Combine(_root, ".suharness", "index.json")));
		Assert.True(File.Exists(Path.Combine(sessionRoot, "working-memory.json")));
		Assert.Contains("Inspect the repository", File.ReadAllText(Path.Combine(sessionRoot, "working-memory.json")));

		await runtime.DeleteSessionAsync(session.Id);

		Assert.False(Directory.Exists(sessionRoot));
	}

	private void Write(string relative, string content)
	{
		var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private string Git(params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = _root,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo)!;
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) throw new InvalidOperationException(error);
		return output;
	}
}

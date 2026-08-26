using Suharness.Infrastructure;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// Behavioural tests for the tool layer. The expected strings are the stable result
/// formats, so a drift in either the envelope or the remediation wording fails here rather than
/// silently diverging from the harness SuHarness is meant to match.
/// </summary>
public sealed class ToolsTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-tools-").FullName;

	public void Dispose()
	{
		// A junction inside the tree makes a plain recursive delete fail, so links are unhooked first.
		try { RemoveLinks(_root); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		try { Directory.Delete(_root, recursive: true); }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
	}

	private static void RemoveLinks(string directory)
	{
		foreach (var entry in new DirectoryInfo(directory).GetDirectories())
		{
			if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) entry.Delete();
			else RemoveLinks(entry.FullName);
		}
	}

	private (WorkspaceScope Scope, FileObservationLog Log) NewSession() => (new WorkspaceScope(_root), new FileObservationLog());

	private string Write(string relative, string content)
	{
		var full = Path.Combine(_root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
		return full;
	}

	[Fact]
	public void ReadReturnsLineNumberedEnvelopeWithEndOfFileFooter()
	{
		Write("a.txt", "alpha\nbeta\n");
		var (scope, log) = NewSession();

		var result = ReadTool.Execute(scope, log, "a.txt", null, null);

		Assert.Equal(
			"<path>a.txt</path>\n<type>file</type>\n<content>\n1: alpha\n2: beta\n\n(End of file - total 2 lines)\n</content>",
			result);
	}

	[Fact]
	public void ReadHonoursOffsetAndLimitAndReportsContinuation()
	{
		Write("b.txt", "one\ntwo\nthree\nfour\n");
		var (scope, log) = NewSession();

		var result = ReadTool.Execute(scope, log, "b.txt", 2, 2);

		Assert.Contains("2: two\n3: three", result);
		Assert.Contains("(Showing lines 2-3 of 4. Use offset=4 to continue.)", result);
	}

	[Fact]
	public void ReadRefusesAPathOutsideTheWorkspace()
	{
		var (scope, log) = NewSession();

		var error = Assert.Throws<ToolInputException>(() => ReadTool.Execute(scope, log, "../escape.txt", null, null));

		Assert.Contains("escapes the session workspace", error.Message);
	}

	[Fact]
	public void FullAccessScopeAllowsAnAbsolutePathOutsideTheWorkspace()
	{
		var outside = Path.Combine(Path.GetTempPath(), $"suharness-outside-{Guid.NewGuid():N}.txt");
		var scope = new WorkspaceScope(_root, allowOutsideRoot: true);

		Assert.Equal(Path.GetFullPath(outside), scope.Resolve(outside, "file_path"));
	}

	[Fact]
	public void ReadOnlyToolsetExcludesMutationAndCommandTools()
	{
		var names = new SessionToolset(_root, Suharness.Abstractions.HarnessAccessMode.ReadOnly)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();

		// Asking a question, tracking todos, and submitting a plan touch nothing, so they stay
		// available in a read-only session; exit_plan_mode is always listed to keep the catalog stable.
		Assert.Equal(["read", "glob", "grep", "skill", "ask_user_question", "todo_write", "exit_plan_mode"], names);
	}

	[Fact]
	public void NoWorkspacePromptContainsNoFolderContext()
	{
		var prompt = HarnessSystemPrompt.Build(string.Empty, modelName: "test-model");

		Assert.Contains("No local workspace or folder is selected", prompt);
		Assert.DoesNotContain("Working directory:", prompt);
		Assert.DoesNotContain("this repo", prompt);
	}

	[Fact]
	public void LocalModelPromptReinforcesWorkspaceAndExecutionDiscipline()
	{
		var prompt = HarnessSystemPrompt.Build(_root, modelName: "koboldcpp/Ornith-1.0-35B-UD-Q4_K_M");

		Assert.Contains("<local-model-execution>", prompt);
		Assert.Contains($"selected repository is exactly {_root}", prompt);
		Assert.Contains("Never ask where the repository", prompt);
		Assert.Contains("Do not narrate a future action and stop", prompt);
	}

	[Fact]
	public void RemoteModelPromptDoesNotAddLocalExecutionContract()
	{
		var prompt = HarnessSystemPrompt.Build(_root, modelName: "google/gemini-3.7-flash");

		Assert.DoesNotContain("<local-model-execution>", prompt);
	}

	[Theory]
	[InlineData("Where should I find the NopCommerce source with the existing plugins?")]
	[InlineData("Which folder contains the repository code?")]
	public void ExistingRepositoryLocationQuestionsAreDiscoverableAndBlocked(string question) =>
		Assert.True(SessionToolset.LooksLikeDiscoverableLocationQuestion(question));

	[Theory]
	[InlineData("Where should generated reports be written?")]
	[InlineData("Which deployment region should the user choose?")]
	public void UserOwnedLocationChoicesAreNotBlocked(string question) =>
		Assert.False(SessionToolset.LooksLikeDiscoverableLocationQuestion(question));

	[Fact]
	public void TodoWriteSummarizesTheListItReplaced()
	{
		var summary = TodoTool.Write([
			new TodoItem("Find the speed constant", "completed"),
			new TodoItem("Apply the change", "in_progress"),
			new TodoItem("Run the tests", "pending"),
		]);

		Assert.Equal("Updated todo list: 1 pending, 1 in progress, 1 completed.", summary);
	}

	[Fact]
	public void TodoWriteRefusesMoreThanOneActiveTask()
	{
		var error = Assert.Throws<ToolInputException>(() => TodoTool.Write([
			new TodoItem("First", "in_progress"),
			new TodoItem("Second", "in_progress"),
		]));

		Assert.Contains("at most one task may be in_progress", error.Message);
	}

	[Fact]
	public void TodoWriteRefusesDuplicateAndEmptyContent()
	{
		Assert.Contains("duplicate content", Assert.Throws<ToolInputException>(() => TodoTool.Write([
			new TodoItem("Same", "pending"),
			new TodoItem("Same", "pending"),
		])).Message);
		Assert.Contains("non-empty", Assert.Throws<ToolInputException>(() => TodoTool.Write([new TodoItem("  ", "pending")])).Message);
	}

	[Fact]
	public void MinimalPresetComposesTwoTools()
	{
		var names = new SessionToolset(_root, preset: Suharness.Abstractions.HarnessAgentPreset.Minimal)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();

		Assert.Equal(["bash", "str_replace_editor"], names);
	}

	[Fact]
	public void ReadReportsAMissingFileInTheStableFormat()
	{
		var (scope, log) = NewSession();

		var error = Assert.Throws<ToolInputException>(() => ReadTool.Execute(scope, log, "nope.txt", null, null));

		Assert.Equal("cannot read \"nope.txt\": not found", error.Message);
	}

	[Fact]
	public void EditRequiresTheFileToHaveBeenReadFirst()
	{
		Write("c.txt", "hello world");
		var (scope, log) = NewSession();

		var error = Assert.Throws<ToolInputException>(() => MutationTools.Edit(scope, log, "c.txt", "hello", "goodbye", null));

		Assert.Equal("edit requires reading \"c.txt\" first — read the file, then retry", error.Message);
	}

	[Fact]
	public void EditSucceedsAfterAReadAndReportsConfirmation()
	{
		var full = Write("d.txt", "hello world");
		var (scope, log) = NewSession();
		ReadTool.Execute(scope, log, "d.txt", null, null);

		var result = MutationTools.Edit(scope, log, "d.txt", "hello", "goodbye", null);

		Assert.Equal("The file d.txt has been updated successfully.", result);
		Assert.Equal("goodbye world", File.ReadAllText(full));
	}

	[Fact]
	public void EditMatchesLfInputAndPreservesCrLfFiles()
	{
		var full = Write("crlf.txt", "alpha\r\nbeta\r\n");
		var (scope, log) = NewSession();
		ReadTool.Execute(scope, log, "crlf.txt", null, null);

		MutationTools.Edit(scope, log, "crlf.txt", "alpha\nbeta", "alpha\r\ngamma", null);

		Assert.Equal("alpha\r\ngamma\r\n", File.ReadAllText(full));
	}

	[Fact]
	public void EditRejectsAFileThatChangedSinceItWasRead()
	{
		var full = Write("e.txt", "original");
		var (scope, log) = NewSession();
		ReadTool.Execute(scope, log, "e.txt", null, null);

		// Simulate a change made outside the session between the read and the edit.
		File.WriteAllText(full, "changed by someone else");
		File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddSeconds(5));

		var error = Assert.Throws<ToolInputException>(() => MutationTools.Edit(scope, log, "e.txt", "changed", "x", null));

		Assert.Equal("cannot edit \"e.txt\": file changed since it was read — re-read the file, then retry", error.Message);
	}

	[Fact]
	public void EditRefusesAnAmbiguousMatchUnlessReplaceAllIsSet()
	{
		Write("f.txt", "x\nx\n");
		var (scope, log) = NewSession();
		ReadTool.Execute(scope, log, "f.txt", null, null);

		var error = Assert.Throws<ToolInputException>(() => MutationTools.Edit(scope, log, "f.txt", "x", "y", null));
		Assert.Equal(
			"old_string matched 2 times in \"f.txt\"; provide a more specific old_string or set replace_all to true",
			error.Message);

		var result = MutationTools.Edit(scope, log, "f.txt", "x", "y", true);
		Assert.Equal("The file f.txt has been updated. All occurrences were successfully replaced.", result);
	}

	[Fact]
	public void WriteCreatesWithoutAReadButCannotBlindlyOverwrite()
	{
		var (scope, log) = NewSession();

		var created = MutationTools.Write(scope, log, "g.txt", "first");
		Assert.Equal("<path>g.txt</path>\n<type>file</type>\n<content>\nCreated file\n</content>", created);

		// The same session wrote it, so it is observed and may be replaced.
		var updated = MutationTools.Write(scope, log, "g.txt", "second");
		Assert.Equal("<path>g.txt</path>\n<type>file</type>\n<content>\nUpdated file\n</content>", updated);

		// A different session has not observed it, so the overwrite is refused.
		var (_, freshLog) = NewSession();
		var error = Assert.Throws<ToolInputException>(() => MutationTools.Write(scope, freshLog, "g.txt", "third"));
		Assert.Equal("cannot overwrite existing \"g.txt\" without reading it first — read the file, then retry", error.Message);
	}

	[Fact]
	public void GlobMatchesBasenamesAtAnyDepthWhenThePatternHasNoSeparator()
	{
		Write("src/deep/one.cs", "//");
		Write("two.cs", "//");
		Write("notes.txt", "text");
		var (scope, _) = NewSession();

		var result = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None);

		Assert.Contains("src/deep/one.cs", result);
		Assert.Contains("two.cs", result);
		Assert.DoesNotContain("notes.txt", result);
	}

	[Fact]
	public void GlobAnchorsToDepthWhenThePatternHasASeparator()
	{
		Write("src/deep/one.cs", "//");
		Write("two.cs", "//");
		var (scope, _) = NewSession();

		var result = SearchTools.Glob(scope, "*/*.cs", null, CancellationToken.None);

		Assert.Equal("No files found", result);
	}

	[Fact]
	public void GrepGroupsMatchesByFileWithLineNumbers()
	{
		Write("h.cs", "using System;\nvar target = 1;\n");
		var (scope, _) = NewSession();

		var result = SearchTools.Grep(scope, "target", null, "*.cs", CancellationToken.None);

		Assert.Equal("Found 1 match\n\nh.cs\nLine 2: var target = 1;", result);
	}

	[Fact]
	public void GrepReportsNoMatchesInTheStableFormat()
	{
		Write("i.cs", "nothing here");
		var (scope, _) = NewSession();

		Assert.Equal("No matches found", SearchTools.Grep(scope, "absent", null, null, CancellationToken.None));
	}

	[Fact]
	public void GrepSkipsGitIgnoredFilesLikeRipgrepDoes()
	{
		Write(".gitignore", "ignored/\n");
		Write("ignored/skipped.cs", "needle");
		Write("kept.cs", "needle");
		var (scope, _) = NewSession();

		var result = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None);

		Assert.Contains("kept.cs", result);
		Assert.DoesNotContain("skipped.cs", result);
	}

	[Fact]
	public void GrepHonoursNestedGitIgnoreFiles()
	{
		Write("nested/.gitignore", "generated/\n");
		Write("nested/generated/skipped.cs", "needle");
		Write("nested/kept.cs", "needle");
		var (scope, _) = NewSession();

		var result = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None);

		Assert.Contains("nested/kept.cs", result);
		Assert.DoesNotContain("nested/generated/skipped.cs", result);
	}

	[Fact]
	public void BashRendersTheExitCodeMarkerOnFailure()
	{
		Assert.Equal("out\n[exit code: 3]", BashTool.Render("out\n", "", 3, timedOut: false, timeoutMs: 1000));
		Assert.Equal("(no output)", BashTool.Render("", "", 0, timedOut: false, timeoutMs: 1000));
		Assert.Equal("[stderr]\nboom\n[exit code: 1]", BashTool.Render("", "boom\n", 1, timedOut: false, timeoutMs: 1000));
	}

	[Fact]
	public async Task BashRunsInTheWorkspaceAndReportsOutput()
	{
		Write("marker.txt", "present");
		var (scope, _) = NewSession();

		var result = await BashTool.ExecuteAsync(scope, "ls marker.txt", null, null, CancellationToken.None);

		Assert.Contains("marker.txt", result);
		Assert.DoesNotContain("[exit code:", result);
	}

	[Fact]
	public async Task PowerShellRunsNativelyInTheWorkspaceOnWindows()
	{
		if (!OperatingSystem.IsWindows()) return;
		Write("marker.txt", "present");
		var (scope, _) = NewSession();

		var result = await PowerShellTool.ExecuteAsync(
			scope,
			"Get-Content -LiteralPath marker.txt",
			null,
			null,
			CancellationToken.None);

		Assert.Contains("present", result);
		Assert.DoesNotContain("[exit code:", result);
	}

	[Fact]
	public void ToolchainInventoryReportsWorkspaceEntryPoints()
	{
		Write("src/App.sln", string.Empty);
		Write("src/App.csproj", "<Project />");

		var inventory = ToolchainInventory.Render(_root);

		Assert.Contains("<toolchain>", inventory);
		Assert.Contains("src/App.sln", inventory);
		Assert.Contains("src/App.csproj", inventory);
		Assert.Contains("dotnet:", inventory);
	}

	[Fact]
	public void StandardWindowsToolsetAdvertisesNativePowerShell()
	{
		if (!OperatingSystem.IsWindows()) return;
		var names = new SessionToolset(_root).BuildTools().Select(tool => tool.Name).ToArray();

		Assert.Contains("powershell", names);
		Assert.Contains("bash", names);
	}

	[Fact]
	public void TeamModePromptRequiresCodingWorkflowWhileStandardPromptDoesNot()
	{
		var standard = HarnessSystemPrompt.Build(_root);
		var team = HarnessSystemPrompt.Build(_root, teamMode: true);

		Assert.DoesNotContain("<team-mode>", standard);
		Assert.Contains("<team-mode>", team);
		Assert.Contains("call coding_workflow exactly once", team);
	}

	[Fact]
	public void WindowsWorkflowToolsetUsesOnlyNativePowerShellForCommands()
	{
		if (!OperatingSystem.IsWindows()) return;
		var codeNames = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowCode)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();
		var verifyNames = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();

		Assert.DoesNotContain("powershell", codeNames);
		Assert.DoesNotContain("bash", codeNames);
		Assert.Contains("edit", codeNames);
		Assert.Contains("powershell", verifyNames);
		Assert.DoesNotContain("bash", verifyNames);
	}

	[Fact]
	public async Task WorkflowReadOnlyGetsFixedWorkspaceChangesWithoutShellAccess()
	{
		using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
			"git", $"init \"{_root}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		}))
		{
			process!.WaitForExit();
			Assert.Equal(0, process.ExitCode);
		}
		Write("new.txt", "untracked");

		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowReadOnly);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);

		Assert.Contains("workspace_changes", tools.Keys);
		Assert.DoesNotContain("powershell", tools.Keys);
		Assert.DoesNotContain("bash", tools.Keys);
		var result = (await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))?.ToString();
		Assert.Contains("--- TRACKED DIFF ---", result);
		Assert.Contains("?? new.txt", result);

		var verifyTools = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();
		Assert.Contains("workspace_changes", verifyTools);
		Assert.Contains("powershell", verifyTools);
	}

	[Fact]
	public async Task WorkflowReviewerReadsReportedUntrackedPathWithoutRediscovery()
	{
		using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
			"git", $"init \"{_root}\"")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		}))
		{
			process!.WaitForExit();
			Assert.Equal(0, process.ExitCode);
		}
		Write("new-test.cs", "untracked");
		var scratch = Path.Combine(Path.GetTempPath(), "suharness-reviewer", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(scratch);
		File.WriteAllText(Path.Combine(scratch, "new-test.cs"), "scratch");

		var toolset = new SessionToolset(
			_root,
			scratchRoot: scratch,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowReadOnly);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());

		var scratchRead = (await tools["read"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["file_path"] = Path.Combine(scratch, "new-test.cs"),
		}))?.ToString();
		var discovery = (await tools["glob"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["pattern"] = "**/new-test.cs",
		}))?.ToString();
		var repeatedEvidence = (await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))?.ToString();
		var workspaceRead = (await tools["read"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["file_path"] = "new-test.cs",
		}))?.ToString();

		Assert.Contains("workspace-relative path", scratchRead);
		Assert.Contains("discovery is blocked", discovery);
		Assert.Contains("exactly once", repeatedEvidence);
		Assert.Contains("untracked", workspaceRead);
	}

	[Fact]
	public void WorkflowRepairToolsetIsEditOnlyForMutation()
	{
		var names = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowRepair)
			.BuildTools()
			.Select(tool => tool.Name)
			.ToArray();

		Assert.Contains("read", names);
		Assert.Contains("edit", names);
		Assert.Contains("write", names);
		Assert.DoesNotContain("powershell", names);
		Assert.DoesNotContain("bash", names);
	}

	[Fact]
	public async Task WorkflowRepairWriteCreatesNewFileButCannotReplaceIt()
	{
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowRepair);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["write"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["file_path"] = "new-test.cs",
			["content"] = "created\n",
		});
		var blocked = (await tools["write"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["file_path"] = "new-test.cs",
			["content"] = "replaced\n",
		}))?.ToString();

		Assert.Equal("created\n", File.ReadAllText(Path.Combine(_root, "new-test.cs")));
		Assert.Contains("create new files only", blocked);
	}

	[Fact]
	public async Task WorkflowCodeWriteCannotReplaceExistingFile()
	{
		Write("existing.cs", "original\n");
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowCode);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["read"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments { ["file_path"] = "existing.cs" });

		var blocked = (await tools["write"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["file_path"] = "existing.cs",
			["content"] = "replacement\n",
		}))?.ToString();

		Assert.Contains("create new files only", blocked);
		Assert.Equal("original\n", File.ReadAllText(Path.Combine(_root, "existing.cs")));
	}

	[Fact]
	public void WindowsWorkflowPromptExplainsCompilerAndLegacyFrameworkValidation()
	{
		if (!OperatingSystem.IsWindows()) return;
		var prompt = HarnessSystemPrompt.Build(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);

		Assert.Contains("Call workspace_changes exactly once as your first tool action", prompt);
		Assert.Contains("use workspace_changes, never PowerShell, for status and diff", prompt);
		Assert.Contains("Framework64\\v4.0.30319\\MSBuild.exe", prompt);
		Assert.Contains("minimal reflection runner", prompt);
		Assert.Contains("Never pass /v, /verbosity, or MSBuild-only flags to csc.exe", prompt);
		Assert.Contains("Do not probe csc.exe with Test-Path", prompt);
		Assert.Contains("$runnerExe = Join-Path $testBin 'Runner.exe'", prompt);
		Assert.Contains("Do not use IResult, ResultState", prompt);
		Assert.Contains("Never search for, list, probe, invoke, or download an NUnit console runner", prompt);
		Assert.Contains("MUST be emitted directly beside Nop.Core.Tests.dll", prompt);
		Assert.Contains("changing PowerShell's current directory does not change", prompt);
		Assert.Contains("Path.Combine(AppDomain.CurrentDomain.BaseDirectory", prompt);
		Assert.Contains("never Environment.CurrentDirectory", prompt);
		Assert.Contains("Do not separately build the production project", prompt);
		Assert.Contains("only the named focused fixture", prompt);
		Assert.Contains("Do not enumerate or print unrelated assembly tests", prompt);
		Assert.Contains("compiler or test failure", prompt);
		Assert.Contains("untracked requested files", prompt);
		Assert.Contains("at most 12 executed command calls", prompt);
		Assert.Contains("run the provided native MSBuild path immediately", prompt);
	}

	[Fact]
	public async Task WorkflowVerifierStopsExecutingCommandsAfterItsBudget()
	{
		if (!OperatingSystem.IsWindows()) return;
		var marker = Path.Combine(_root, "budget-marker.txt");
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
		var powershell = tools["powershell"];
		for (var index = 0; index < SessionToolset.WorkflowVerifyCommandBudget; index++)
			await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
			{
				["command"] = $"Write-Output 'MSBuild.exe validation {index}'",
				["description"] = "Consume verifier budget",
			});

		var blocked = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value created",
			["description"] = "Attempt command after budget",
		}))?.ToString();

		Assert.Contains("command budget exhausted", blocked);
		Assert.False(File.Exists(marker));
	}

	[Fact]
	public async Task WorkflowVerifierBlocksShellDiscoveryWithoutConsumingValidationSlot()
	{
		if (!OperatingSystem.IsWindows()) return;
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
		var powershell = tools["powershell"];

		var blocked = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "Get-ChildItem 'C:\\Program Files\\dotnet\\sdk'",
			["description"] = "Probe installed SDKs",
		}))?.ToString();
		var validation = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "Write-Output 'MSBuild.exe validation'",
			["description"] = "Run validation command",
		}))?.ToString();
		Assert.Contains("discovery command blocked", blocked);
		Assert.Contains("MSBuild.exe validation", validation);
	}

	[Fact]
	public async Task WorkflowVerifierRequiresDedicatedWorkspaceChangesAndBlocksShellGit()
	{
		if (!OperatingSystem.IsWindows()) return;
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		var powershell = tools["powershell"];

		var beforeEvidence = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "Write-Output 'MSBuild.exe validation'",
			["description"] = "Attempt validation before evidence",
		}))?.ToString();
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
		var repeatedEvidence = (await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))?.ToString();
		var shellGit = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "git status --porcelain; git diff --stat",
			["description"] = "Attempt shell Git inspection",
		}))?.ToString();

		Assert.Contains("workspace_changes tool exactly once before", beforeEvidence);
		Assert.Contains("exactly once", repeatedEvidence);
		Assert.Contains("shell Git inspection is blocked", shellGit);
	}

	[Fact]
	public async Task WorkflowVerifierRejectsMutationsAndNonNativeMsBuild()
	{
		if (!OperatingSystem.IsWindows()) return;
		var toolset = new SessionToolset(
			_root,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
		var powershell = tools["powershell"];

		var mutation = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "Set-Content src/App.cs changed",
			["description"] = "Attempt source mutation",
		}))?.ToString();
		var wrongMsBuild = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "& 'C:\\Program Files\\Visual Studio\\MSBuild.exe' App.csproj",
			["description"] = "Attempt wrong MSBuild",
		}))?.ToString();

		Assert.Contains("validation-only", mutation);
		Assert.Contains("known native path", wrongMsBuild);
	}

	[Fact]
	public async Task WorkflowVerifierForcesFrameworkRunnerIntoTestBin()
	{
		if (!OperatingSystem.IsWindows()) return;
		var scratch = Path.Combine(Path.GetTempPath(), "suharness-verifier", "scratch", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(scratch);
		var toolset = new SessionToolset(
			_root,
			scratchRoot: scratch,
			toolProfile: Suharness.Abstractions.AgentToolProfile.WorkflowVerify);
		var tools = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().ToDictionary(tool => tool.Name);
		await tools["workspace_changes"].InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments());
		var powershell = tools["powershell"];

		var reflection = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "[System.Reflection.Assembly]::LoadFrom('tests.dll')",
			["description"] = "Attempt PowerShell reflection",
		}))?.ToString();
		var compileBeforeSource = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"csc.exe /out:'{scratch}\\Runner.exe' '{scratch}\\Runner.cs'",
			["description"] = "Attempt compile before source",
		}))?.ToString();
		var runnerSource = Path.Combine(scratch, "Runner.cs");
		var broadRunner = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"Set-Content -LiteralPath '{runnerSource}' -Value 'class Runner {{ void Run() {{ assembly.GetTypes(); }} }}'",
			["description"] = "Attempt broad runner source",
		}))?.ToString();
		var modernRunner = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"Set-Content -LiteralPath '{runnerSource}' -Value 'class Runner {{ void Run() {{ var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"Tests.dll\"); var assembly = Assembly.LoadFrom(path); var fixture = assembly.GetType(\"Tests.Fixture\"); foreach (var method in fixture.GetMethods()) method.GetCustomAttributes(typeof(TestAttribute), false); Console.WriteLine(error?.Message); }} }}'",
			["description"] = "Attempt modern runner syntax",
		}))?.ToString();
		var runnerCode = "using System; using System.IO; using System.Reflection; using NUnit.Framework; class Runner { void Run() { var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"Tests.dll\"); var assembly = Assembly.LoadFrom(path); var fixture = assembly.GetType(\"Tests.Fixture\"); foreach (var method in fixture.GetMethods()) method.GetCustomAttributes(typeof(TestAttribute), false); } }";
		var scratchSource = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"Set-Content -LiteralPath '{runnerSource}' -Value '{runnerCode}'",
			["description"] = "Write scratch runner source",
		}))?.ToString();
		var scratchExe = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"csc.exe /out:'{scratch}\\Runner.exe' '{scratch}\\Runner.cs'",
			["description"] = "Attempt scratch runner",
		}))?.ToString();
		var bin = Path.Combine(_root, "tests", "bin", "Debug");
		Directory.CreateDirectory(bin);
		var variableBin = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"$outDir = '{bin}'; Write-Output csc.exe /out:$outDir\\Runner.exe",
			["description"] = "Use verified bin variable",
		}))?.ToString();
		var runnerVariable = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"$testDir = '{bin}'; $runnerExe = Join-Path $testDir 'Runner.exe'; $compiler = 'compiler.exe'; & $compiler /target:exe /out:$runnerExe /reference:$testDir\\nunit.framework.dll '{scratch}\\Runner.cs'",
			["description"] = "Use verified runner variable",
		}))?.ToString();
		var joinedOutputVariable = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "$bin = 'tests\\bin\\Debug'; $outPath = Join-Path $bin 'Runner.exe'; $compiler = 'compiler.exe'; & $compiler /target:exe /out:$outPath /reference:$bin\\nunit.framework.dll 'scratch\\Runner.cs'",
			["description"] = "Use joined output variable",
		}))?.ToString();
		var wrongJoinedFilename = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = "$bin = 'tests\\bin\\Debug'; $outPath = Join-Path $bin 'TestHarness.exe'; $compiler = 'compiler.exe'; & $compiler /target:exe /out:$outPath /reference:$bin\\nunit.framework.dll 'scratch\\Runner.cs'",
			["description"] = "Use wrong runner filename",
		}))?.ToString();
		var expandedBinVariable = (await powershell.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["command"] = $"$testBin = '{bin}'; $compiler = 'compiler.exe'; & $compiler /target:exe /out:\"$testBin\\Runner.exe\" /reference:\"$testBin\\nunit.framework.dll\" '{scratch}\\Runner.cs'",
			["description"] = "Use expanded bin variable",
		}))?.ToString();

		Assert.Contains("wrong CLR", reflection);
		Assert.Contains("Runner.cs has not been accepted", compileBeforeSource);
		Assert.Contains("directly into the test project's bin", scratchExe);
		Assert.Contains("target exactly one named fixture", broadRunner);
		Assert.Contains("modern syntax such as ?.", modernRunner);
		Assert.DoesNotContain("validation-only", scratchSource);
		Assert.Equal(runnerCode + "\r\n", File.ReadAllText(runnerSource));
		Assert.DoesNotContain("directly into the test project's bin", variableBin);
		Assert.DoesNotContain("directly into the test project's bin", runnerVariable);
		Assert.DoesNotContain("directly into the test project's bin", joinedOutputVariable);
		Assert.Contains("directly into the test project's bin", wrongJoinedFilename);
		Assert.DoesNotContain("directly into the test project's bin", expandedBinVariable);
		Directory.Delete(scratch, recursive: true);
	}

	[Fact]
	public void SessionScratchIsScopedDisclosedAndDeletedWithItsSession()
	{
		var store = new SessionScratchStore(Path.Combine(_root, "scratch"));
		var sessionId = Guid.NewGuid();
		var scratch = store.GetOrCreate(sessionId);
		File.WriteAllText(Path.Combine(scratch, "probe.txt"), "temporary");
		var scope = new WorkspaceScope(_root, additionalRoots: new[] { scratch });

		Assert.Equal(Path.Combine(scratch, "probe.txt"), scope.Resolve(Path.Combine(scratch, "probe.txt"), "path"));
		var prompt = HarnessSystemPrompt.Build(_root, scratchRoot: scratch);
		Assert.Contains($"Session scratch directory: {scratch}", prompt);
		Assert.Contains("ad hoc runners", prompt);

		store.Delete(sessionId);
		Assert.False(Directory.Exists(scratch));
	}

	/// <summary>
	/// Directory symlinks are pruned, matching ripgrep's default. Before this, a link that pointed at
	/// an ancestor made the walk cyclic, and a pnpm workspace — which links every package back into
	/// its own store — turned a 70k-file tree into an unbounded graph that never finished traversing.
	/// </summary>
	[Fact]
	public void GlobDoesNotDescendThroughDirectorySymlinks()
	{
		Write("real/kept.cs", "//");
		if (!TryLinkDirectory("loop", _root)) return;
		var scope = new WorkspaceScope(_root, allowOutsideRoot: true);

		var result = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None);

		Assert.Equal("real/kept.cs", result);
	}

	[Fact]
	public void GrepDoesNotDescendThroughDirectorySymlinks()
	{
		Write("real/kept.cs", "needle");
		if (!TryLinkDirectory("loop", _root)) return;
		var (scope, _) = NewSession();

		var result = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None);

		Assert.Equal("Found 1 match\n\nreal/kept.cs\nLine 1: needle", result);
	}

	/// <summary>
	/// A walk that runs out of wall clock returns what it found and says so. A search that never
	/// returns hangs the whole turn, because no tool result reaches the model until every call in
	/// the batch has completed — so a partial answer is the only useful outcome.
	/// </summary>
	[Fact]
	public void GlobReturnsAPartialResultWhenTheWalkRunsOutOfTime()
	{
		for (var index = 0; index < 400; index++) Write($"pack{index}/file.cs", "//");
		var (scope, _) = NewSession();

		var result = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, timeout: TimeSpan.Zero);

		Assert.Contains("Search stopped after 0s and these results are partial.", result);
		Assert.Contains("Narrow path to a specific subdirectory to search the rest.", result);
	}

	[Fact]
	public void GrepReturnsAPartialResultWhenTheWalkRunsOutOfTime()
	{
		for (var index = 0; index < 400; index++) Write($"pack{index}/file.cs", "needle");
		var (scope, _) = NewSession();

		var result = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None, timeout: TimeSpan.Zero);

		Assert.Contains("Search stopped after 0s and these results are partial.", result);
	}

	[Fact]
	public void FileWalkerReportsWhichBudgetStoppedIt()
	{
		for (var index = 0; index < 20; index++) Write($"file{index}.cs", "//");

		var exhausted = new FileWalker(respectGitIgnore: false, includeHidden: true, budget: 5);
		Assert.Equal(5, exhausted.Walk(_root, CancellationToken.None).Count());
		Assert.Equal(WalkTruncation.Budget, exhausted.Truncation);

		var complete = new FileWalker(respectGitIgnore: false, includeHidden: true);
		Assert.Equal(20, complete.Walk(_root, CancellationToken.None).Count());
		Assert.Equal(WalkTruncation.None, complete.Truncation);
	}

	/// <summary>
	/// Link a directory in the tree at <paramref name="target"/>. Windows is given a junction rather
	/// than a symlink: a symlink needs Developer Mode or elevation, a junction needs neither, and both
	/// carry the reparse-point attribute the walker prunes on. Returns false only where neither kind
	/// can be created, so the suite still runs on a locked-down box instead of failing there.
	/// </summary>
	private bool TryLinkDirectory(string relative, string target)
	{
		var link = Path.Combine(_root, relative);
		if (OperatingSystem.IsWindows())
		{
			try
			{
				using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
					"cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
				{ RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
				process!.WaitForExit();
				if (process.ExitCode == 0) return true;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}

		try
		{
			Directory.CreateSymbolicLink(link, target);
			return true;
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	// ── Spill policy ───────────────────────────────────────────────────────

	private (ISpillStore Store, string Root) NewSpillStore()
	{
		var root = Path.Combine(_root, "spill");
		return (new FileSpillStore(root), root);
	}

	/// <summary>
	/// A result inside the cap is handed to the model exactly as the tool produced it. The policy
	/// must be invisible until it is needed, or every transcript changes unnecessarily.
	/// </summary>
	[Fact]
	public void AResultInsideTheCapIsUntouched()
	{
		var (store, _) = NewSpillStore();

		var result = SpillPolicy.Apply("small enough", "grep", Guid.NewGuid(), store, maxInlineBytes: 1000);

		Assert.Equal("small enough", result);
	}

	[Fact]
	public void AnOversizedResultIsReplacedWithAPreviewAndItsLocator()
	{
		var (store, _) = NewSpillStore();
		var text = string.Join('\n', Enumerable.Range(0, 400).Select(index => $"line {index} of the result"));

		var result = SpillPolicy.Apply(text, "grep", Guid.NewGuid(), store, maxInlineBytes: 600);

		Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 600, $"replacement was {result.Length} chars");
		Assert.Contains("line 0 of the result", result);          // head kept
		Assert.Contains("line 399 of the result", result);        // tail kept
		Assert.Contains("[...]", result);
		Assert.Contains("Full formatted result stored at:", result);
		Assert.Contains("Use read with offset/limit, or grep this path to search within it.", result);
	}

	/// <summary>The whole point: what was moved out of context has to be recoverable from context.</summary>
	[Fact]
	public void TheSpilledArtifactHoldsTheCompleteResultAndIsReadableByTheReadTool()
	{
		var (store, spillRoot) = NewSpillStore();
		var sessionId = Guid.NewGuid();
		var text = string.Join('\n', Enumerable.Range(0, 400).Select(index => $"line {index} of the result"));

		var result = SpillPolicy.Apply(text, "grep", sessionId, store, maxInlineBytes: 600);

		var locator = result.Split("Full formatted result stored at: ")[1].Split(". Use read")[0];
		Assert.Equal(text, File.ReadAllText(locator));

		// A workspace-scoped session still resolves the spill path, or the locator would be useless.
		var scope = new WorkspaceScope(_root, allowOutsideRoot: false, additionalRoots: spillRoot);
		var read = ReadTool.Execute(scope, new FileObservationLog(), locator, null, null);
		Assert.Contains("line 399 of the result", read);
	}

	/// <summary>
	/// <c>read</c> is exempt because answering a read with a path to read is a
	/// loop. Its own byte and line caps bound it instead.
	/// </summary>
	[Theory]
	[InlineData("read", false)]
	[InlineData("todo_write", false)]
	[InlineData("ask_user_question", false)]
	[InlineData("grep", true)]
	[InlineData("bash", true)]
	[InlineData("glob", true)]
	public void TheModelFacingArmSkipsExemptTools(string toolName, bool spills) =>
		Assert.Equal(spills, SpillPolicy.Applies(toolName));

	[Fact]
	public void AnOversizedReadIsKeptInlineRatherThanSpilled()
	{
		var (store, _) = NewSpillStore();
		var text = new string('x', 5000);

		Assert.Equal(text, SpillPolicy.Apply(text, "read", Guid.NewGuid(), store, maxInlineBytes: 100));
	}

	/// <summary>A spill failure must never turn a successful tool call into a failure or a stub.</summary>
	[Fact]
	public void AStorageFailureKeepsTheOriginalResult()
	{
		var text = new string('x', 5000);

		Assert.Equal(text, SpillPolicy.Apply(text, "grep", Guid.NewGuid(), new FailingSpillStore(), maxInlineBytes: 100));
	}

	/// <summary>
	/// A cap too small to hold even the notice has no within-cap replacement to offer, so the
	/// original is kept rather than handing the model a truncated locator it cannot use.
	/// </summary>
	[Fact]
	public void ACapTooSmallForTheNoticeKeepsTheOriginalResult()
	{
		var (store, _) = NewSpillStore();
		var text = new string('x', 5000);

		Assert.Equal(text, SpillPolicy.Apply(text, "grep", Guid.NewGuid(), store, maxInlineBytes: 20));
	}

	[Fact]
	public void SpillingIsOffWhenNoStoreIsConfigured()
	{
		var text = new string('x', 5000);

		Assert.Equal(text, SpillPolicy.Apply(text, "grep", Guid.NewGuid(), store: null, maxInlineBytes: 100));
	}

	/// <summary>
	/// The policy has to be reachable through the real tool surface, not just callable in isolation:
	/// the wrapper sits outside the lifecycle waterfall so it sees the final text a tool produced.
	/// </summary>
	[Fact]
	public async Task AToolInvokedThroughTheToolsetHasItsOversizedResultSpilled()
	{
		for (var index = 0; index < 60; index++) Write($"file{index}.cs", "needle in a haystack of source text\n");
		var (store, _) = NewSpillStore();
		var sessionId = Guid.NewGuid();
		var toolset = new SessionToolset(_root, sessionId: sessionId, spillStore: store, maxInlineBytes: 400);
		var grep = (Microsoft.Extensions.AI.AIFunction)toolset.BuildTools().Single(tool => tool.Name == "grep");

		var result = (await grep.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
		{
			["pattern"] = "needle",
		}))?.ToString() ?? string.Empty;

		Assert.Contains("Full formatted result stored at:", result);
		Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 400, $"result was {result.Length} chars");

		var locator = result.Split("Full formatted result stored at: ")[1].Split(". Use read")[0];
		Assert.Contains("file59.cs", File.ReadAllText(locator));
	}

	private sealed class FailingSpillStore : ISpillStore
	{
		public string Root => Path.Combine(Path.GetTempPath(), "suharness-spill-absent");

		public SpillRef? SaveText(Guid sessionId, string toolName, string label, string content) => null;
	}

	[Theory]
	[InlineData("**/*.cs", "src/a/b.cs", true)]
	[InlineData("**/*.cs", "b.cs", true)]
	[InlineData("src/*.cs", "src/a/b.cs", false)]
	[InlineData("src/**/*.cs", "src/a/b.cs", true)]
	[InlineData("*.{js,ts}", "app/main.ts", true)]
	[InlineData("*.{js,ts}", "app/main.cs", false)]
	public void GlobPatternFollowsRipgrepSemantics(string pattern, string path, bool expected) =>
		Assert.Equal(expected, GlobPattern.Compile(pattern, caseInsensitive: false).IsMatch(path));
}

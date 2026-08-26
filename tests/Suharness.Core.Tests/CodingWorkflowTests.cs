using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Tools;

namespace Suharness.Core.Tests;

public sealed class CodingWorkflowTests
{
	[Fact]
	public async Task CodingWorkflowRunsOnlyOncePerParentTurn()
	{
		var workspace = Directory.CreateTempSubdirectory("suharness-coding-workflow-").FullName;
		try
		{
			var subagents = new ScriptedSubagents(
				BaselineExplorer(),
				BaselinePlan(),
				SingleTaskPlan(),
				Junior("implemented"),
				Review("pass", "review passed"),
				Verify("pass", "all green", ["dotnet test => 5 passed"]));
			var sessionId = Guid.NewGuid();
			var lifecycle = new Suharness.Infrastructure.AgentLifecycle();
			await lifecycle.FireBeforeTurnAsync(new TurnContext(sessionId, workspace, 1, 0, CancellationToken.None));
			var standardTools = new SessionToolset(
				workspace,
				sessionId: sessionId,
				subagents: subagents,
				lifecycle: lifecycle)
				.BuildTools()
				.Select(item => item.Name)
				.ToArray();
			var toolset = new SessionToolset(
				workspace,
				sessionId: sessionId,
				subagents: subagents,
				lifecycle: lifecycle,
				teamConfiguration: new TeamConfiguration(true, []));
			var tool = toolset.BuildTools().OfType<AIFunction>().Single(item => item.Name == "coding_workflow");
			var read = toolset.BuildTools().OfType<AIFunction>().Single(item => item.Name == "read");
			var arguments = new AIFunctionArguments { ["request"] = "implement the feature", ["max_repair_rounds"] = 1 };

			var beforeWorkflow = (await read.InvokeAsync(new AIFunctionArguments { ["file_path"] = "anything.txt" }))?.ToString();
			var first = (await tool.InvokeAsync(arguments))?.ToString();
			var second = (await tool.InvokeAsync(arguments))?.ToString();
			var afterWorkflow = (await read.InvokeAsync(new AIFunctionArguments { ["file_path"] = "anything.txt" }))?.ToString();

			Assert.DoesNotContain("coding_workflow", standardTools);
			Assert.Contains("Team mode is active", beforeWorkflow);
			Assert.Contains("\"status\": \"pass\"", first);
			Assert.Contains("already ran in this turn", second);
			Assert.Contains("Do not call further workspace tools", afterWorkflow);
			Assert.Equal(6, subagents.StartCalls);
		}
		finally
		{
			try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
		}
	}

	[Fact]
	public async Task RunsFreshRolesAndSequentialJuniorMicroTasks()
	{
		var subagents = new ScriptedSubagents(
			Json(new { summary = "found", files = new[] { "src/App.cs" }, constraints = Array.Empty<string>(), risks = Array.Empty<string>() }),
			Json(new { summary = "plan", steps = new[] { "edit", "test" }, acceptanceCriteria = new[] { "works" } }),
			Json(new
			{
				summary = "split",
				microTasks = new[]
				{
					new { id = "1", instruction = "edit App.cs", acceptanceCriteria = new[] { "implementation updated" } },
					new { id = "2", instruction = "add focused tests", acceptanceCriteria = new[] { "tests added" } },
				},
			}),
			Junior("edited"),
			Junior("tested"),
			Review("pass", "requirements met"),
			Verify("pass", "build and tests passed", ["dotnet test => 4 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("Update HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior.");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Equal(
			["explorer", "planner", "senior-developer", "junior-developer-1", "junior-developer-2", "reviewer", "verifier"],
			subagents.Labels);
		Assert.Equal(7, subagents.StartCalls);
		Assert.Equal(0, subagents.ForkCalls);
		Assert.Equal(
			[
				AgentToolProfile.WorkflowReadOnly,
				AgentToolProfile.None,
				AgentToolProfile.None,
				AgentToolProfile.WorkflowCode,
				AgentToolProfile.WorkflowCode,
				AgentToolProfile.WorkflowReadOnly,
				AgentToolProfile.WorkflowVerify,
			],
			subagents.Options.Select(option => option.ToolProfile));
		Assert.Null(subagents.Options[0].ModelOverride);
		Assert.Null(subagents.Options[1].ModelOverride);
		Assert.Null(subagents.Options[2].ModelOverride);
		Assert.Equal(new ModelSelection("openrouter", "google/gemini-3.7-flash"), subagents.Options[3].ModelOverride);
		Assert.Equal(new ModelSelection("openrouter", "google/gemini-3.7-flash"), subagents.Options[4].ModelOverride);
		Assert.Null(subagents.Options[5].ModelOverride);
		Assert.Null(subagents.Options[6].ModelOverride);
		Assert.Contains("Micro-task id: 1", subagents.Prompts[3]);
		Assert.DoesNotContain("add focused tests", subagents.Prompts[3]);
		Assert.Contains("Micro-task id: 2", subagents.Prompts[4]);
		Assert.Contains("non-anchor regression", subagents.Prompts[4]);
		Assert.Contains("<address>contact</address>", subagents.Prompts[4]);
		Assert.Contains("<a data-rel=\"external\">link</a>", subagents.Prompts[4]);
		Assert.Contains("<a data-rel=\"external\" rel=\"nofollow\">link</a>", subagents.Prompts[4]);
		Assert.Contains("<a rel=\"nofollow\" />", subagents.Prompts[4]);
		Assert.Contains("Call workspace_changes exactly once", subagents.Prompts[5]);
		Assert.Contains("do not reread tracked files", subagents.Prompts[5]);
		Assert.Contains("Read each changed untracked source or test file", subagents.Prompts[5]);
		Assert.Contains("explicitly reject any matcher that can cross a `<`", subagents.Prompts[5]);
		Assert.Contains("must remain exactly unchanged", subagents.Prompts[5]);
		Assert.Contains("word boundary immediately after `a`", subagents.Prompts[5]);
		Assert.Contains("<address>contact</address>", subagents.Prompts[5]);
		Assert.Contains("Require whitespace immediately before the real rel attribute", subagents.Prompts[5]);
		Assert.Contains("<a data-rel=\"external\" rel=\"nofollow\">link</a>", subagents.Prompts[5]);
		Assert.Contains("insert the attribute before the slash", subagents.Prompts[5]);
		Assert.Contains("<a rel=\"nofollow\" />", subagents.Prompts[5]);
		Assert.Contains("<absolute Scratch directory>\\Runner.cs", subagents.Prompts[6]);
		Assert.Contains("first post-build command MUST be one standalone", subagents.Prompts[6]);
		Assert.Contains("do not compile until that command succeeds", subagents.Prompts[6]);
		Assert.Contains("never write it in `$env:TEMP`", subagents.Prompts[6]);
		Assert.Contains("never run Get-ChildItem", subagents.Prompts[6]);
	}

	[Fact]
	public async Task ReportsCumulativeRoleHandoffsWhileWorkflowRuns()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		var updates = new List<string>();
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), progress: updates.Add);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(6, updates.Count);
		for (var index = 0; index < updates.Count; index++)
		{
			using var document = System.Text.Json.JsonDocument.Parse(updates[index]);
			Assert.Equal("running", document.RootElement.GetProperty("status").GetString());
			Assert.Equal(index + 1, document.RootElement.GetProperty("stages").GetArrayLength());
		}
		using var finalUpdate = System.Text.Json.JsonDocument.Parse(updates[^1]);
		var finalStages = finalUpdate.RootElement.GetProperty("stages");
		Assert.Equal("verifier", finalStages[finalStages.GetArrayLength() - 1].GetProperty("role").GetString());
	}

	[Fact]
	public async Task UnrelatedCacheRequestOmitsAnchorGuidanceFromCodingRoles()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("fail", "one defect", ["Fix CacheEventConsumerTests.cs to cover cache invalidation."]),
			Junior("tests fixed"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 14 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("Implement missing cache event consumers and add focused invalidation tests.");

		Assert.Equal("pass", result.Status);
		var codingPrompts = subagents.Labels
			.Select((label, index) => (label, prompt: subagents.Prompts[index]))
			.Where(item => item.label.StartsWith("junior-developer", StringComparison.Ordinal)
				|| item.label.StartsWith("reviewer", StringComparison.Ordinal))
			.Select(item => item.prompt)
			.ToArray();
		Assert.NotEmpty(codingPrompts);
		Assert.All(codingPrompts, prompt =>
		{
			Assert.DoesNotContain("HtmlHelper", prompt, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("<address>", prompt, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("data-rel", prompt, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("self-closing anchor", prompt, StringComparison.OrdinalIgnoreCase);
		});
	}

	[Fact]
	public async Task JuniorReceivesOnlyItsMicroTaskAndVerifierOwnsValidation()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			Json(new
			{
				summary = "bounded tasks",
				microTasks = new[]
				{
					new { id = "1", instruction = "Implement the source behavior in src/App.cs.", acceptanceCriteria = new[] { "implementation works" } },
					new { id = "2", instruction = "Run the focused tests and require zero failures.", acceptanceCriteria = new[] { "tests pass" } },
				},
			}),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		await workflow.RunAsync("Implement HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior and add tests for the entire feature.");

		Assert.DoesNotContain("add tests for the entire feature", subagents.Prompts[3]);
		Assert.Contains("complete assignment", subagents.Prompts[3]);
		Assert.Contains("<a\\b[^<>]*>", subagents.Prompts[3]);
		Assert.Contains("<address>contact</address>", subagents.Prompts[3]);
		Assert.Contains("cannot treat `data-rel` as rel", subagents.Prompts[3]);
		Assert.Contains("insert it before the slash", subagents.Prompts[3]);
		Assert.Equal(AgentToolProfile.WorkflowCode, subagents.Options[3].ToolProfile);
		Assert.DoesNotContain(subagents.Labels, label => label == "junior-developer-2");
		Assert.Equal("reviewer", subagents.Labels[4]);
		Assert.Equal(AgentToolProfile.WorkflowReadOnly, subagents.Options[4].ToolProfile);
	}

	[Fact]
	public async Task TeamAssignmentsRouteEachPersonaToItsSelectedModel()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			Json(new
			{
				summary = "split",
				microTasks = new[]
				{
					new { id = "1", instruction = "edit App.cs", acceptanceCriteria = new[] { "implementation updated" } },
					new { id = "2", instruction = "add focused tests", acceptanceCriteria = new[] { "tests added" } },
				},
			}),
			Junior("edited"),
			Junior("tested"),
			Review("pass", "requirements met"),
			Verify("pass", "build and tests passed", ["dotnet test => 4 passed"]));
		var assignments = Enum.GetValues<CodingPersona>().ToDictionary(
			persona => persona,
			persona => new ModelSelection("provider", persona.ToString()));
		var workflow = new CodingWorkflow(
			subagents,
			Guid.NewGuid(),
			Guid.NewGuid(),
			teamConfiguration: new TeamConfiguration(true, assignments));

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(assignments[CodingPersona.Explorer], subagents.Options[0].ModelOverride);
		Assert.Equal(assignments[CodingPersona.Planner], subagents.Options[1].ModelOverride);
		Assert.Equal(assignments[CodingPersona.SeniorDeveloper], subagents.Options[2].ModelOverride);
		Assert.Equal(assignments[CodingPersona.JuniorDeveloper], subagents.Options[3].ModelOverride);
		Assert.Equal(assignments[CodingPersona.JuniorDeveloper], subagents.Options[4].ModelOverride);
		Assert.Equal(assignments[CodingPersona.Reviewer], subagents.Options[5].ModelOverride);
		Assert.Equal(assignments[CodingPersona.Verifier], subagents.Options[6].ModelOverride);
	}

	[Fact]
	public async Task ReviewAndVerificationReceiveLongerDeadlinesThanMutationRoles()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		await workflow.RunAsync("implement the feature");

		Assert.Equal(300, subagents.Options[subagents.Labels.IndexOf("junior-developer-1")].TimeoutSeconds);
		Assert.Equal(600, subagents.Options[subagents.Labels.IndexOf("reviewer")].TimeoutSeconds);
		Assert.Equal(600, subagents.Options[subagents.Labels.IndexOf("verifier")].TimeoutSeconds);
		Assert.Null(subagents.Options[subagents.Labels.IndexOf("junior-developer-1")].MaxOutputTokens);
		Assert.Equal(1024, subagents.Options[subagents.Labels.IndexOf("reviewer")].MaxOutputTokens);
		Assert.Equal(1024, subagents.Options[subagents.Labels.IndexOf("verifier")].MaxOutputTokens);
	}

	[Fact]
	public async Task VerifierRunnerLoadingConcernDoesNotMutateTestInfrastructure()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify(
				"fail",
				"reflection runner could not load NUnit",
				["runner => failed to load nunit.framework.dll"],
				["src/Tests/App.Tests test execution could not complete: run the reflection runner from bin/Debug or add an AppDomain.AssemblyResolve handler to load nunit.framework.dll."]),
			Verify("pass", "fresh runner passed", ["Runner.exe => EXECUTED=2 FAILED=0"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add focused tests");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Contains("verifier-attempt-2", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ScratchRunnerCompileErrorsRetryVerificationInsteadOfRepairingWorkspace()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify(
				"fail",
				"Runner.cs failed to compile because Path and IResult were unavailable",
				["csc Runner.cs => CS0103 Path; CS0246 IResult"],
				["Runner.cs compiler errors require System.IO and removal of IResult/ResultState."]),
			Verify("pass", "focused fixture passed", ["Runner.exe => EXECUTED=6 FAILED=0"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 2);

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Contains("verifier-attempt-2", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MissingNUnitConsoleRunnerRetriesReflectionVerificationWithoutRepair()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify(
				"fail",
				"No NUnit console runner was present, so no executed-test count was obtained.",
				["MSBuild => success"],
				["NUnit console runner is not present; compile and run the reflection runner instead."]),
			Verify("pass", "reflection runner passed", ["Runner.exe => EXECUTED=7 FAILED=0"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 2);

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Contains("verifier-attempt-2", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ReviewerValidationConcernIsDeferredToVerifierWithoutJuniorRepair()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("fail", "package resolution was not verified", ["Restore NuGet packages because no packages folder exists at the repository root."]),
			Verify("pass", "native build and focused tests pass", ["msbuild => success", "runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal(["reviewer", "verifier"], subagents.Labels.Skip(4));
	}

	[Fact]
	public async Task InvalidReviewerHandoffRetriesBeforeVerification()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			string.Empty,
			Review("pass", "fresh review passed"),
			Verify("pass", "native validation passed", ["runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add focused tests");

		Assert.Equal("pass", result.Status);
		Assert.Equal(["reviewer", "reviewer-attempt-2", "verifier"], subagents.Labels.Skip(4));
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ExhaustedInvalidReviewerHandoffsCannotBypassReview()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			string.Empty,
			string.Empty,
			string.Empty);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add focused tests");

		Assert.Equal("fail", result.Status);
		Assert.Equal(3, subagents.Labels.Count(label => label.StartsWith("reviewer", StringComparison.Ordinal)));
		Assert.DoesNotContain("verifier", subagents.Labels);
	}

	[Fact]
	public async Task FailedVerificationRoutesFindingsToJuniorAndReverifies()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("fail", "test failed", ["dotnet test => 1 failed"], ["Fix the null branch in App.cs."]),
			Junior("fixed null branch"),
			Review("pass", "repair reviewed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(1, result.RepairRounds);
		Assert.Equal(2, subagents.Labels.Count(label => label == "reviewer"));
		Assert.Equal(2, subagents.Labels.Count(label => label == "verifier"));
		var repairIndex = subagents.Labels.IndexOf("junior-developer-repair-1-1");
		Assert.Contains("Fix the null branch in App.cs.", subagents.Prompts[repairIndex]);
		Assert.Equal(AgentToolProfile.WorkflowRepair, subagents.Options[repairIndex].ToolProfile);
		Assert.Equal(new ModelSelection("openrouter", "google/gemini-3.7-flash"), subagents.Options[repairIndex].ModelOverride);
		Assert.True(subagents.Labels.LastIndexOf("verifier") > repairIndex);
	}

	[Fact]
	public async Task VerificationFindingsAreReverifiedOneAtATime()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("fail", "two compile defects", ["build => FAILED"], ["Fix defect A in src/App.cs line 10.", "Fix defect B in src/App.cs line 20."]),
			Junior("fixed A"),
			Review("pass", "A reviewed"),
			Verify("fail", "one defect remains", ["build => FAILED"], ["Fix defect B in src/App.cs line 20."]),
			Junior("fixed B"),
			Review("pass", "B reviewed"),
			Verify("pass", "all green", ["runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 2);

		var result = await workflow.RunAsync("implement the feature and add tests");

		Assert.Equal("pass", result.Status);
		var firstRepair = subagents.Prompts[subagents.Labels.IndexOf("junior-developer-repair-1-1")];
		var secondRepair = subagents.Prompts[subagents.Labels.IndexOf("junior-developer-repair-2-1")];
		Assert.Contains("defect A", firstRepair);
		Assert.DoesNotContain("defect B", firstRepair);
		Assert.Contains("defect B", secondRepair);
		Assert.Equal(3, subagents.Labels.Count(label => label == "verifier"));
	}

	[Fact]
	public async Task VerifierPositiveConfirmationDoesNotBecomeSecondRepair()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify(
				"fail",
				"compile error",
				["MSBuild App.csproj => FAILED: App.cs(12,4) CS1056 unexpected character"],
				[
					"src/App.cs line 12: fix the invalid string literal.",
					"The tests in tests/AppTests.cs are correctly written and registered; they will run once the compile error is fixed.",
					"The build failure blocks the focused tests in tests/AppTests.cs, so no executed-test count is available and the tests cannot run until App.Core compiles.",
					"The implementation (src/App.cs:1-20) and tests (tests/AppTests.cs:1-40) appear correct, but code review cannot substitute for actual test execution evidence.",
					"src/App.cs:1-20 implements the requested behavior with case-insensitive matching and malformed-input fallback.",
				]),
			Junior("fixed syntax"),
			Review("pass", "repair reviewed"),
			Verify("pass", "all green", ["MSBuild App.csproj => Build succeeded", "runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add tests");

		Assert.Equal("pass", result.Status);
		Assert.Single(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal(2, subagents.Labels.Count(label => label == "verifier"));
	}

	[Fact]
	public async Task MissingJuniorHandoffBecomesOneBoundedRepairWithoutRepeatingTheMutation()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			string.Empty,
			Junior("implementation is present"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Single(subagents.Labels, label => label == "junior-developer-1");
		Assert.Single(subagents.Labels, label => label == "junior-developer-1-attempt-2");
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal(0, result.RepairRounds);
	}

	[Fact]
	public async Task CompletedJuniorWithoutMutationRetriesTheActualTask()
	{
		var noMutation = Json(new
		{
			status = "completed",
			summary = "described work without editing",
			filesChanged = Array.Empty<string>(),
			validation = Array.Empty<string>(),
			findings = Array.Empty<string>(),
		});
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			noMutation,
			Junior("actual mutation"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-1-attempt-2", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task DuplicateSeniorFieldsCannotDropImplementationTask()
	{
		var malformedSenior = """
			{"summary":"duplicate fields","microTasks":[{"id":"1","instruction":"Implement src/App.cs behavior.","acceptanceCriteria":["works"],"id":"2","instruction":"Add focused tests and run them.","acceptanceCriteria":["tests pass"]}]}
			""";
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			malformedSenior,
			Junior("implemented"),
			Junior("tests added"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("Implement src/App.cs behavior. Add focused tests in tests/AppTests.cs.");

		Assert.Equal("pass", result.Status);
		Assert.Equal(["junior-developer-1", "junior-developer-2"], subagents.Labels.Skip(3).Take(2));
		Assert.Contains("[Implementation task]", subagents.Prompts[3]);
		Assert.DoesNotContain("tests/AppTests.cs", subagents.Prompts[3]);
		Assert.Contains("[Test-authoring task]", subagents.Prompts[4]);
		Assert.DoesNotContain("run them", subagents.Prompts[4], StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task InitialNoOpResponsesRetryWithoutConsumingRepairRounds()
	{
		var noOpRepair = Json(new
		{
			status = "completed",
			summary = "formatted handoff only",
			filesChanged = Array.Empty<string>(),
			validation = Array.Empty<string>(),
			findings = Array.Empty<string>(),
		});
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			string.Empty,
			noOpRepair,
			Junior("actual mutation"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 2);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-1-attempt-2", subagents.Labels);
		Assert.Contains("junior-developer-1-attempt-3", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal(0, result.RepairRounds);
	}

	[Fact]
	public async Task BlockedJuniorFindingsAreRepairedBeforeReviewAndVerification()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Json(new
			{
				status = "blocked",
				summary = "quoted rel parsing is incomplete",
				filesChanged = new[] { "src/App.cs" },
				validation = Array.Empty<string>(),
				findings = new[] { "Handle multiple tokens inside quoted rel values." },
			}),
			Junior("fixed quoted rel parsing"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(1, result.RepairRounds);
		var repairIndex = subagents.Labels.IndexOf("junior-developer-repair-1-1");
		Assert.True(repairIndex > subagents.Labels.IndexOf("junior-developer-1"));
		Assert.Contains("Handle multiple tokens inside quoted rel values.", subagents.Prompts[repairIndex]);
		Assert.True(subagents.Labels.IndexOf("reviewer") > repairIndex);
		Assert.True(subagents.Labels.IndexOf("verifier") > repairIndex);
	}

	[Fact]
	public async Task TransportEndedRoleUsesHarnessOwnedHandoff()
	{
		var subagents = new ScriptedSubagents(
			"__ERROR__:connection reset",
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(["explorer", "planner"], subagents.Labels.Take(2));
		Assert.Contains("connection reset", result.Stages[0].Summary);
	}

	[Fact]
	public async Task TransientReviewAndVerificationFailuresRetryWithoutRepairRounds()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			"__ERROR__:HTTP 502 invalid_response_error: malformed_tool_call",
			Review("pass", "review passed"),
			"__ERROR__:An error occurred while sending the request.",
			Verify("pass", "all green", ["Framework runner => EXECUTED=6 FAILED=0"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Contains("reviewer", subagents.Labels);
		Assert.Contains("reviewer-attempt-2", subagents.Labels);
		Assert.Contains("verifier", subagents.Labels);
		Assert.Contains("verifier-attempt-2", subagents.Labels);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal(AgentToolProfile.WorkflowReadOnly, subagents.Options[subagents.Labels.IndexOf("reviewer-attempt-2")].ToolProfile);
		Assert.Equal(AgentToolProfile.WorkflowVerify, subagents.Options[subagents.Labels.IndexOf("verifier-attempt-2")].ToolProfile);
		Assert.Null(subagents.Options[subagents.Labels.IndexOf("verifier-attempt-2")].ModelOverride);
	}

	[Fact]
	public async Task MalformedSeniorHandoffProducesSelfContainedJuniorTasks()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			"not json",
			Junior("implemented"),
			Junior("tests added"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("Implement HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior in src/App.cs. Add focused tests in tests/AppTests.cs.");

		Assert.Equal("pass", result.Status);
		Assert.Equal(["junior-developer-1", "junior-developer-2"], subagents.Labels.Skip(3).Take(2));
		Assert.Contains("[Implementation task]", subagents.Prompts[3]);
		Assert.Contains("src/App.cs", subagents.Prompts[3]);
		Assert.DoesNotContain("tests/AppTests.cs", subagents.Prompts[3]);
		Assert.DoesNotContain("supplied plan", subagents.Prompts[3]);
		Assert.Contains("untouched prefix + complete replacement", subagents.Prompts[3]);
		Assert.Contains("exactly one '='", subagents.Prompts[3]);
		Assert.Contains("[Test-authoring task]", subagents.Prompts[4]);
		Assert.Contains("tests/AppTests.cs", subagents.Prompts[4]);
		Assert.Contains("Test scenarios must be independent", subagents.Prompts[4]);
		Assert.Contains("exact complete transformed markup", subagents.Prompts[4]);
	}

	[Fact]
	public async Task MalformedLeadingBraceHandoffSynthesizesWithoutParserCrash()
	{
		var subagents = new ScriptedSubagents(
			"{malformed}",
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal("{malformed}", result.Stages[0].Summary);
	}

	[Fact]
	public async Task UnstructuredJuniorWithWorkspaceMutationAdvancesToReview()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			"<tool_call>write completed but handoff was malformed</tool_call>",
			Review("pass", "review passed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		subagents.Evidence[4] = new SubagentExecutionEvidence(
			["src/App.cs", "tests/AppTests.cs"],
			["dotnet test => Passed: 5, Failed: 0"]);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task TransportFailedJuniorWithWorkspaceMutationAdvancesToReview()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			"__ERROR__:HTTP 503 service unavailable after tool execution",
			Review("pass", "workspace mutation is complete"),
			Verify("pass", "all green", ["test runner => 5 passed"]));
		subagents.Evidence[4] = new SubagentExecutionEvidence(["tests/AppTests.cs"], []);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("add focused tests");

		Assert.Equal("pass", result.Status);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
		Assert.Equal("reviewer", subagents.Labels[4]);
	}

	[Fact]
	public async Task JuniorHandoffMayOmitValidationArrayWhenWorkspaceEvidenceExists()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Json(new
			{
				status = "completed",
				summary = "test file added",
				filesChanged = new[] { "tests/AppTests.cs" },
				findings = Array.Empty<string>(),
			}),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		subagents.Evidence[4] = new SubagentExecutionEvidence(["tests/AppTests.cs"], []);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("add focused tests");

		Assert.Equal("pass", result.Status);
		Assert.Equal("reviewer", subagents.Labels[4]);
	}

	[Fact]
	public async Task UnstructuredVerifierWithSuccessfulValidationPasses()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			"<tool_call>test execution completed but handoff was malformed</tool_call>");
		subagents.Evidence[6] = new SubagentExecutionEvidence(
			[],
			["test runner => Total Tests: 5, Passed: 5, Failed: 0"]);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal("pass", result.Stages.Last().Status);
	}

	[Fact]
	public async Task VerifierWorkspaceMutationRoutesThroughJuniorAndFreshReview()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "tests passed", ["test runner => 2 passed"]),
			Junior("owned verifier correction"),
			Review("pass", "correction reviewed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		subagents.Evidence[6] = new SubagentExecutionEvidence(
			["tests/AppTests.cs"],
			["test runner => Passed: 2, Failed: 0"]);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-repair-1-1", subagents.Labels);
		Assert.Contains("Verifier modified it during validation", subagents.Prompts[6]);
		Assert.Equal(2, subagents.Labels.Count(label => label == "reviewer"));
		Assert.Equal(2, subagents.Labels.Count(label => label == "verifier"));
	}

	[Fact]
	public async Task ValidatedBlockedJuniorAdvancesToIndependentReview()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Json(new
			{
				status = "blocked",
				summary = "implementation built but focused runner was unavailable",
				filesChanged = new[] { "src/App.cs", "tests/AppTests.cs" },
				validation = Array.Empty<string>(),
				findings = new[] { "Run focused tests." },
			}),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 5 passed"]));
		subagents.Evidence[4] = new SubagentExecutionEvidence(
			["src/App.cs", "tests/AppTests.cs"],
			["msbuild App.Tests.csproj => succeeded"]);
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(0, result.RepairRounds);
		Assert.Equal("reviewer", subagents.Labels[4]);
		Assert.DoesNotContain(subagents.Labels, label => label.StartsWith("junior-developer-repair", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SeniorOverDecompositionBecomesTwoMutationTasks()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			Json(new
			{
				summary = "over-decomposed",
				microTasks = new[]
				{
					new { id = "1", instruction = "Open src/App.cs and locate the TODO.", acceptanceCriteria = new[] { "TODO found" } },
					new { id = "2", instruction = "Implement the primary behavior in src/App.cs.", acceptanceCriteria = new[] { "primary behavior works" } },
					new { id = "3", instruction = "Fix mixed-case handling in the same method.", acceptanceCriteria = new[] { "mixed case works" } },
					new { id = "4", instruction = "Add focused tests in tests/AppTests.cs.", acceptanceCriteria = new[] { "tests exist" } },
					new { id = "5", instruction = "Build the project and run focused tests.", acceptanceCriteria = new[] { "tests pass" } },
				},
			}),
			Junior("implemented"),
			Junior("tests added"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["dotnet test => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(2, subagents.Labels.Count(label => label.StartsWith("junior-developer-", StringComparison.Ordinal)));
		Assert.DoesNotContain("locate the TODO", subagents.Prompts[3], StringComparison.OrdinalIgnoreCase);
		Assert.Contains("primary behavior", subagents.Prompts[3]);
		Assert.Contains("mixed-case handling", subagents.Prompts[3]);
		Assert.Contains("focused tests", subagents.Prompts[4]);
	}

	[Fact]
	public async Task FailedRepairTransportRetriesWithinTheRepairBudget()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("fail", "test assertion contradicts request", ["Use a rel value without nofollow in the append test."]),
			"__ERROR__:provider connection failed",
			Junior("corrected test input"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 5 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 2);

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Equal(1, result.RepairRounds);
		Assert.Contains("junior-developer-repair-1-1", subagents.Labels);
		Assert.Contains("junior-developer-repair-1-1-attempt-2", subagents.Labels);
		Assert.True(subagents.Labels.LastIndexOf("reviewer") > subagents.Labels.IndexOf("junior-developer-repair-1-1-attempt-2"));
	}

	[Fact]
	public async Task TestAuthoringTaskRequiresIndependentScenarios()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			Json(new
			{
				summary = "tests",
				microTasks = new[] { new { id = "1", instruction = "Add focused tests for appending and existing values.", acceptanceCriteria = new[] { "tests added" } } },
			}),
			Junior("tests added"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		await workflow.RunAsync("Add focused tests for HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior.");

		Assert.Contains("Test scenarios must be independent", subagents.Prompts[3]);
		Assert.Contains("existing-values case must omit the token being appended", subagents.Prompts[3]);
		Assert.Contains("NoFoLlOw", subagents.Prompts[3]);
		Assert.Contains("do not use a misspelling such as NoFoLoW", subagents.Prompts[3]);
		Assert.Contains("For an HTML transformation, enable HTML processing", subagents.Prompts[3]);
		Assert.Contains("unclosed anchor and <p>paragraph", subagents.Prompts[3]);
		Assert.Contains("must remain unchanged", subagents.Prompts[3]);
		Assert.Contains("include using Nop.Core.Html", subagents.Prompts[3]);
		Assert.Contains("include using Nop.Tests", subagents.Prompts[3]);
		Assert.Contains("lists explicit Compile items", subagents.Prompts[3]);
	}

	[Fact]
	public async Task VerifierCannotPassAfterRequiredProjectBuildFailure()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "static review passed"),
			Verify(
				"pass",
				"scratch copy tests passed",
				["MSBuild App.Tests.csproj => FAILED: AppTests.cs(2,1) CS1061 missing extension", "scratch runner => 2 passed, 0 failed"],
				["AppTests.cs is missing the namespace import for the assertion extension method."]),
			Junior("added namespace import"),
			Review("pass", "review passed"),
			Verify("pass", "project and tests pass", ["MSBuild App.Tests.csproj => Build succeeded", "runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement and test the feature");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-repair-1-1", subagents.Labels);
		Assert.Equal(2, subagents.Labels.Count(label => label == "verifier"));
	}

	[Fact]
	public async Task VerifierCannotPassTestRequestWithoutPositiveExecutedCount()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "static review passed"),
			Verify("pass", "project built", ["MSBuild App.Tests.csproj => Build succeeded"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 0);

		var result = await workflow.RunAsync("implement the feature and add focused tests");

		Assert.Equal("fail", result.Status);
		Assert.Equal("fail", result.Stages.Last().Status);
	}

	[Fact]
	public async Task VerifierTrailingJsonAfterBraceNoiseRoutesCompilerFindingToRepair()
	{
		var noisyVerifier = "probe output {not json}\n" + Verify(
			"fail",
			"source does not compile",
			["MSBuild App.csproj => FAILED: App.cs(12,4) CS1056 unexpected character"],
			["Line 12: invalid C# verbatim string syntax causes the build failure; double the embedded quote."]);
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			noisyVerifier,
			Junior("fixed syntax"),
			Review("pass", "repair reviewed"),
			Verify("pass", "all green", ["MSBuild App.csproj => Build succeeded", "runner => Executed: 2, Passed: 2, Failed: 0"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add tests");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-repair-1-1", subagents.Labels);
		Assert.Contains("Line 12: invalid C# verbatim string syntax", subagents.Prompts[6]);
	}

	[Fact]
	public async Task ImplementationTaskIncludesSafeReconstructionInvariant()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid());

		await workflow.RunAsync("Implement HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior.");

		Assert.Contains("untouched prefix + complete replacement", subagents.Prompts[3]);
		Assert.Contains("full match length", subagents.Prompts[3]);
		Assert.Contains("exactly one attribute name and exactly one '='", subagents.Prompts[3]);
		Assert.Contains("replace the full match exactly once", subagents.Prompts[3]);
		Assert.Contains("begin the suffix at end + 1", subagents.Prompts[3]);
		Assert.Contains("never retain and emit both closing quotes", subagents.Prompts[3]);
		Assert.Contains("immediately before the opening tag's final `>`", subagents.Prompts[3]);
		Assert.Contains("never directly after the `<a` name", subagents.Prompts[3]);
		Assert.Contains("use an ordinary string, not a verbatim string", subagents.Prompts[3]);
		Assert.Contains("double every regex backslash", subagents.Prompts[3]);
		Assert.Contains("escape each embedded double quote", subagents.Prompts[3]);
		Assert.Contains("compatible with C# 5", subagents.Prompts[3]);
		Assert.Contains("no local functions", subagents.Prompts[3]);
		Assert.Contains("IndexOf(char) and LastIndexOf(char) with one argument only", subagents.Prompts[3]);
		Assert.Contains("rel as the first attribute", subagents.Prompts[3]);
		Assert.Contains("must never consume another `<`", subagents.Prompts[3]);
		Assert.Contains("character class that excludes both `<` and `>`", subagents.Prompts[3]);
		Assert.Contains("returns match.Value unchanged", subagents.Prompts[3]);
	}

	[Fact]
	public async Task RepairPromptForbidsBroadRediscoveryAndValidation()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("fail", "replacement span is wrong", ["In src/App.cs, advance the suffix by the full match length."]),
			Junior("fixed replacement span"),
			Review("pass", "review passed"),
			Verify("pass", "all green", ["test runner => 2 passed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		await workflow.RunAsync("Implement HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior and all tests.");

		var repairPrompt = subagents.Prompts[subagents.Labels.IndexOf("junior-developer-repair-1-1")];
		Assert.Contains("finding is your complete assignment", repairPrompt);
		Assert.Contains("do not build, run tests, invoke a compiler", repairPrompt, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("ordinary C# string instead of a verbatim string", repairPrompt);
		Assert.Contains("compatible with C# 5", repairPrompt);
		Assert.Contains("update every reference to the old parameter", repairPrompt);
		Assert.Contains("char overloads do not accept StringComparison", repairPrompt);
		Assert.Contains("start the untouched suffix at end + 1", repairPrompt);
		Assert.Contains("immediately before that opening tag's final `>`", repairPrompt);
		Assert.DoesNotContain("Implement HtmlHelper.FormatText addNoFollowTag anchor nofollow behavior and all tests.", repairPrompt);
	}

	[Fact]
	public async Task VerifierRoutesUnsupportedLocalFunctionToSourceRepair()
	{
		var subagents = new ScriptedSubagents(
			BaselineExplorer(),
			BaselinePlan(),
			SingleTaskPlan(),
			Junior("implemented"),
			Review("pass", "review passed"),
			Verify(
				"fail",
				"legacy compiler rejects local functions",
				["MSBuild App.csproj => FAILED: App.cs(12,4) CS1513"],
				["Root cause: toolchain incompatibility. src/App.cs lines 12-30 use a C# 7 local function, but the compiler only supports C# 5. Rewrite it as a private class-level method."]),
			Junior("rewrote helper"),
			Review("pass", "repair reviewed"),
			Verify("pass", "all green", ["MSBuild App.csproj => Build succeeded", "runner => 2 passed, 0 failed"]));
		var workflow = new CodingWorkflow(subagents, Guid.NewGuid(), Guid.NewGuid(), maxRepairRounds: 1);

		var result = await workflow.RunAsync("implement the feature and add tests");

		Assert.Equal("pass", result.Status);
		Assert.Contains("junior-developer-repair-1-1", subagents.Labels);
	}

	private static string BaselineExplorer() =>
		Json(new { summary = "found", files = Array.Empty<string>(), constraints = Array.Empty<string>(), risks = Array.Empty<string>() });

	private static string BaselinePlan() =>
		Json(new { summary = "plan", steps = new[] { "edit" }, acceptanceCriteria = new[] { "works" } });

	private static string SingleTaskPlan() =>
		Json(new
		{
			summary = "split",
			microTasks = new[] { new { id = "1", instruction = "implement", acceptanceCriteria = new[] { "done" } } },
		});

	private static string Junior(string summary) =>
		Json(new
		{
			status = "completed",
			summary,
			filesChanged = new[] { "src/App.cs" },
			validation = Array.Empty<string>(),
			findings = Array.Empty<string>(),
		});

	private static string Review(string status, string summary, string[]? findings = null) =>
		Json(new { status, summary, findings = findings ?? [] });

	private static string Verify(string status, string summary, string[] commands, string[]? findings = null) =>
		Json(new { status, summary, commands, findings = findings ?? [] });

	private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

	private sealed class ScriptedSubagents(params string[] outputs) : ISubagentService
	{
		private readonly Queue<string> _outputs = new(outputs);

		public int MaxDepth => 4;
		public int StartCalls { get; private set; }
		public int ForkCalls { get; private set; }
		public List<string> Labels { get; } = [];
		public List<string> Prompts { get; } = [];
		public List<SubagentRunOptions> Options { get; } = [];
		public Dictionary<int, SubagentExecutionEvidence> Evidence { get; } = [];
		public event Action<SubagentEntry>? StatusChanged { add { } remove { } }

		public int DepthOf(Guid sessionId) => 0;

		public Task<SubagentRun> StartAsync(
			string label,
			string prompt,
			Guid parentSessionId,
			Guid parentWorkspaceSessionId,
			bool runInBackground = false,
			CancellationToken cancellationToken = default,
			SubagentRunOptions? options = null)
		{
			StartCalls++;
			Labels.Add(label);
			Prompts.Add(prompt);
			Options.Add(options ?? new SubagentRunOptions());
			var output = _outputs.Dequeue();
			var evidence = Evidence.GetValueOrDefault(StartCalls) ?? InferEvidence(output, options);
			var result = output.StartsWith("__ERROR__:", StringComparison.Ordinal)
				? new SubagentResult(
					SubagentStopReason.Error,
					string.Empty,
					output[10..],
					evidence)
				: new SubagentResult(
					SubagentStopReason.Completed,
					output,
					Evidence: evidence);
			return Task.FromResult(new SubagentRun(
				Guid.NewGuid().ToString("N"),
				label,
				parentSessionId,
				1,
				Task.FromResult(result),
				() => ValueTask.CompletedTask));
		}

		public Task<SubagentRun> ForkAsync(
			string label,
			string prompt,
			Guid parentSessionId,
			Guid parentWorkspaceSessionId,
			IReadOnlyList<SessionEvent> parentHistory,
			bool runInBackground = false,
			CancellationToken cancellationToken = default,
			SubagentRunOptions? options = null)
		{
			ForkCalls++;
			throw new NotSupportedException();
		}

		public IReadOnlyList<SubagentEntry> ListChildren(Guid parentSessionId, bool descendants = false) => [];
		public Task<string> SendMessageAsync(Guid childSessionId, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public bool Interrupt(Guid sessionId) => false;
		public SubagentEntry? Get(Guid sessionId) => null;

		private static SubagentExecutionEvidence? InferEvidence(string output, SubagentRunOptions? options)
		{
			if (options?.ToolProfile is not (AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair)) return null;
			try
			{
				using var document = System.Text.Json.JsonDocument.Parse(output);
				if (!document.RootElement.TryGetProperty("filesChanged", out var files)
					|| files.ValueKind != System.Text.Json.JsonValueKind.Array)
					return null;
				var paths = files.EnumerateArray()
					.Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
					.Select(item => item.GetString()!)
					.Where(path => path.Length > 0)
					.ToArray();
				return paths.Length > 0 ? new SubagentExecutionEvidence(paths, []) : null;
			}
			catch (System.Text.Json.JsonException)
			{
				return null;
			}
		}
	}
}

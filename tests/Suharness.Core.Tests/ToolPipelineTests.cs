using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the tool policy pipeline: an allow/deny/ask gate before a call, a
/// declared deadline around it, a decision that can replace or withhold its result, and an
/// observe-only audit point that always sees the text the model actually got.
/// </summary>
public sealed class ToolPipelineTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-pipeline-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	// ─── Decision algebra ────────────────────────────────────────────────

	/// <summary>
	/// Policy combines by strictness, not by registration order. A handler registered later must not
	/// be able to hand back permission that an earlier one refused.
	/// </summary>
	[Fact]
	public async Task ARefusalCannotBeUndoneByALaterHandler()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Deny("no"));
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Allow);

		var decision = await lifecycle.FireBeforeToolCallAsync(Turn(), "bash", new Dictionary<string, object?>());
		Assert.Equal(ToolGateKind.Deny, decision.Kind);
	}

	/// <summary>An escalation outranks permission, so any handler can force the question to a human.</summary>
	[Fact]
	public async Task AnEscalationOutranksPermission()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Allow);
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Ask("this writes to disk"));

		var decision = await lifecycle.FireBeforeToolCallAsync(Turn(), "write", new Dictionary<string, object?>());
		Assert.Equal(ToolGateKind.Ask, decision.Kind);
	}

	/// <summary>Post-call handlers compose: each sees what the previous one produced.</summary>
	[Fact]
	public async Task ResultTransformationsCompose()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.AfterToolCall += (_, _, result) => ValueTask.FromResult(PostToolDecision.Replace(result + " one"));
		lifecycle.AfterToolCall += (_, _, result) => ValueTask.FromResult(PostToolDecision.Replace(result + " two"));

		var decision = await lifecycle.FireAfterToolCallAsync(Turn(), "glob", "base");
		Assert.Equal("base one two", decision.Content);
	}

	/// <summary>A blocked result is final: nothing downstream may transform text the model never sees.</summary>
	[Fact]
	public async Task ABlockedResultStopsTheChain()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.AfterToolCall += (_, _, _) => ValueTask.FromResult(PostToolDecision.Block("withheld"));
		lifecycle.AfterToolCall += (_, _, result) => ValueTask.FromResult(PostToolDecision.Replace(result + " reached"));

		var decision = await lifecycle.FireAfterToolCallAsync(Turn(), "read", "secret");
		Assert.Equal(PostToolOutcome.Block, decision.Outcome);
		Assert.Equal("withheld", decision.Content);
	}

	// ─── The pipeline around a call ──────────────────────────────────────

	/// <summary>An approved escalation runs the call.</summary>
	[Fact]
	public async Task AnApprovedCallRuns()
	{
		var questions = new UserQuestionBroker();
		AnswerWith(questions, "Allow");
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Ask("writes to disk"));

		// A factory-created function hands its return value back already serialized, so the pipeline
		// passes a JSON string through rather than a CLR one.
		var result = await InvokeAsync(lifecycle, questions, () => "ran");
		Assert.Equal("ran", result?.ToString());
	}

	/// <summary>Approving all runs the current call and skips later prompts in the same session.</summary>
	[Fact]
	public async Task ApprovingAllSkipsLaterPromptsInTheSession()
	{
		var questions = new UserQuestionBroker();
		var prompts = 0;
		questions.Changed += sessionId =>
		{
			if (questions.Pending(sessionId) is not { } pending) return;
			prompts++;
			questions.TryAnswer(sessionId, [.. pending.Questions.Select(item =>
				new UserQuestionAnswerItem(item.Id, [item.ApproveAllOption!]))]);
		};
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Ask());
		var approvals = new ToolApprovalScope();
		var turn = Turn();
		var first = Tool(() => "first", lifecycle, questions, approvals, turn);
		var second = Tool(() => "second", lifecycle, questions, approvals, turn);

		Assert.Equal("first", (await first.InvokeAsync(new AIFunctionArguments()))?.ToString());
		Assert.Equal("second", (await second.InvokeAsync(new AIFunctionArguments()))?.ToString());
		Assert.Equal(1, prompts);
	}

	/// <summary>
	/// A refused escalation never runs the call, and the model is told why rather than being left to
	/// wait on a result that is not coming.
	/// </summary>
	[Fact]
	public async Task ARefusedCallDoesNotRunAndSaysSo()
	{
		var questions = new UserQuestionBroker();
		AnswerWith(questions, "Deny");
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Ask());
		var ran = false;

		var result = await InvokeAsync(lifecycle, questions, () => { ran = true; return "ran"; });

		Assert.False(ran);
		Assert.Contains("did not approve", result?.ToString());
	}

	/// <summary>
	/// With nobody to ask, an escalation fails closed. An approval that cannot be presented must never
	/// become a silent yes.
	/// </summary>
	[Fact]
	public async Task AnUnanswerableEscalationFailsClosed()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.BeforeToolCall += (_, _, _) => ValueTask.FromResult(PreToolDecision.Ask());
		var ran = false;

		var result = await InvokeAsync(lifecycle, questions: null, () => { ran = true; return "ran"; });

		Assert.False(ran);
		Assert.Contains("no interactive user", result?.ToString());
	}

	/// <summary>A tool that overruns its declared deadline is stopped and reported as a result.</summary>
	[Fact]
	public async Task AToolThatOverrunsItsDeadlineIsStopped()
	{
		var lifecycle = new AgentLifecycle();
		var tool = new LifecycleTool(
			AIFunctionFactory.Create(
				async (CancellationToken token) => { await Task.Delay(Timeout.Infinite, token); return "never"; },
				new AIFunctionFactoryOptions { Name = "slow" }),
			lifecycle,
			Turn,
			timeouts: _ => 50);

		var result = await tool.InvokeAsync(new AIFunctionArguments());
		Assert.Contains("did not finish within 50 ms", result?.ToString());
	}

	/// <summary>
	/// The audit point sees the text the model actually got, not the text the tool produced. An audit
	/// that reported the pre-transformation result would describe a conversation that never happened.
	/// </summary>
	[Fact]
	public async Task TheAuditPointSeesTheFinalText()
	{
		var lifecycle = new AgentLifecycle();
		lifecycle.AfterToolCall += (_, _, _) => ValueTask.FromResult(PostToolDecision.Replace("rewritten"));
		var observed = new List<string>();
		lifecycle.ToolResult += (_, _, text) => observed.Add(text);

		var result = await InvokeAsync(lifecycle, questions: null, () => "original");

		Assert.Equal("rewritten", result);
		Assert.Equal(["rewritten"], observed);
	}

	/// <summary>Only tools whose work is bounded by the machine carry a deadline.</summary>
	[Fact]
	public void OnlyMachineBoundToolsDeclareADeadline()
	{
		Assert.NotNull(ToolTimeouts.For("read"));
		Assert.NotNull(ToolTimeouts.For("grep"));
		// These legitimately wait on a person, another agent, or their own timeout parameter.
		Assert.Null(ToolTimeouts.For("ask_user_question"));
		Assert.Null(ToolTimeouts.For("subagent"));
		Assert.Null(ToolTimeouts.For("bash"));
	}

	// ─── Step boundaries, end to end ─────────────────────────────────────

	/// <summary>
	/// Step numbers count model round-trips, including the last one that produces the answer. That
	/// final request runs no tools, so a harness that counted only tool dispatches would never see it.
	/// </summary>
	[Fact]
	public async Task StepNumbersCountEveryModelRoundTrip()
	{
		var lifecycle = new AgentLifecycle();
		var steps = new List<int>();
		lifecycle.BeforeStep += context => steps.Add(context.StepNumber);

		var (runtime, session) = await StartAsync(
			lifecycle,
			ToolCall("glob", "{\"pattern\":\"**/*\"}", "call_1"),
			ToolCall("glob", "{\"pattern\":\"*.txt\"}", "call_2"),
			Completion("found them"));

		await runtime.SubmitAsync(session.Id, "find the files");

		// Three requests: two that asked for a tool and one that answered.
		Assert.Equal([1, 2, 3], steps);
	}

	/// <summary>
	/// A turn that spends its whole round-trip budget says so. The framework withholds the tools on the
	/// final request, so the model answers in prose — an answer indistinguishable from a finished task
	/// unless the transcript records the constraint it was written under.
	/// </summary>
	[Fact]
	public async Task ATurnThatExhaustsItsRoundTripBudgetSaysSo()
	{
		var lifecycle = new AgentLifecycle();
		// The scripted model repeats its last response, so this asks for a tool call forever.
		var (runtime, session) = await StartAsync(lifecycle, ToolCall("glob", "{\"pattern\":\"**/*\"}", "call_x"));

		await runtime.SubmitAsync(session.Id, "loop forever");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var notice = Assert.Single(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("round-trips"));
		Assert.Contains("limit of 3", notice.Content);
		Assert.Contains("without tools available", notice.Content);
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("failed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.Equal("turn_budget_exhausted", summaryJson.RootElement.GetProperty("stopReason").GetString());
		Assert.Equal("not_required", summaryJson.RootElement.GetProperty("validationStatus").GetString());
	}

	[Fact]
	public async Task AProductiveToolSegmentContinuesWithToolsAndCompletes()
	{
		File.WriteAllText(Path.Combine(Workspace, "marker.txt"), "present");
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 6,
			maxSegmentIterations: 3,
			ToolCall("glob", "{\"pattern\":\"*.txt\"}", "call_1"),
			ToolCall("write", "{\"file_path\":\"changed.txt\",\"content\":\"changed\"}", "call_2"),
			Completion("I will continue."),
			ToolCall("bash", "{\"command\":\"dotnet build --help\",\"description\":\"Validate the change\"}", "call_3"),
			Completion("done"));

		await runtime.SubmitAsync(session.Id, "inspect the marker and finish");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("validation pass"));
		Assert.Contains(transcript.Events, item => item.Type == "continuation" && item.Content.Contains("validation_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "tool_result" && item.Content.Contains("changed.txt"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "done");
		Assert.DoesNotContain(transcript.Events, item => item.Type == "notice" && item.Content.Contains("stalled"));
	}

	[Fact]
	public async Task AToolSegmentWithoutNewProgressStopsBeforeTheTurnBudget()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 12,
			maxSegmentIterations: 3,
			ToolCall("glob", "{\"pattern\":\"missing-*\"}", "call_x"));

		await runtime.SubmitAsync(session.Id, "keep looking forever");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("stalled"));
		Assert.True(lifecycle.MaxStep(session.Id) < 12);
	}

	[Fact]
	public async Task ADiscoveryOnlySegmentGetsOneForcedImplementationPassThenStalls()
	{
		File.WriteAllText(Path.Combine(Workspace, "marker.txt"), "present");
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 9,
			maxSegmentIterations: 3,
			ToolCall("glob", "{\"pattern\":\"*.txt\"}", "call_1"),
			ToolCall("glob", "{\"pattern\":\"*.cs\"}", "call_2"),
			Completion("still looking"),
			ToolCall("read", "{\"file_path\":\"marker.txt\"}", "call_3"),
			Completion("still no edit"));

		await runtime.SubmitAsync(session.Id, "implement the requested change");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("implementation_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "error" && item.Content.Contains("workspace still did not change"));
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("failed", summaryJson.RootElement.GetProperty("outcome").GetString());
	}

	[Fact]
	public async Task BudgetExhaustionWithAMutationStillGetsReservedValidation()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 3,
			maxSegmentIterations: 3,
			ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"value\"}", "call_1"),
			ToolCall("glob", "{\"pattern\":\"*.txt\"}", "call_2"),
			Completion("budget used"),
			ToolCall("bash", "{\"command\":\"dotnet build --help\",\"description\":\"Validate\"}", "call_3"),
			Completion("validated"));

		await runtime.SubmitAsync(session.Id, "create and validate the file");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("validation_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "validated");
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("completed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.Equal("satisfied", summaryJson.RootElement.GetProperty("validationStatus").GetString());
	}

	[Fact]
	public async Task ASecondUnfinishedContinuationRequiresMutationProgress()
	{
		File.WriteAllText(Path.Combine(Workspace, "marker.txt"), "present");
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 12,
			maxSegmentIterations: 6,
			autoContinueUnfinished: true,
			ToolCall("read", "{\"file_path\":\"marker.txt\"}", "call_0"),
			Completion("Found three call sites. Next, I will update each one."),
			ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"value\"}", "call_1"),
			ToolCall("bash", "{\"command\":\"dotnet build --help\",\"description\":\"Validate\"}", "call_2"),
			Truncated("The mutation is applied; validation remains."),
			Completion("done"));

		await runtime.SubmitAsync(session.Id, "create and validate the file");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Equal(2, transcript!.Events.Count(item => item.Type == "continuation" && item.Content.Contains("automatic_continuation")));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "done");
	}

	[Fact]
	public async Task ASecondUnfinishedContinuationIsDeniedWithoutMutationProgress()
	{
		File.WriteAllText(Path.Combine(Workspace, "marker.txt"), "present");
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 12,
			maxSegmentIterations: 6,
			autoContinueUnfinished: true,
			ToolCall("read", "{\"file_path\":\"marker.txt\"}", "call_1"),
			Completion("Found three call sites. Next, I will update each one."),
			ToolCall("read", "{\"file_path\":\"marker.txt\"}", "call_2"),
			Completion("Two tests still fail. Let me re-read the assertion."));

		await runtime.SubmitAsync(session.Id, "inspect and update the implementation");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("automatic_continuation"));
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal(1, summaryJson.RootElement.GetProperty("unfinishedContinuations").GetInt32());
	}

	[Fact]
	public async Task ValidatedBudgetEdgeGetsAToolFreeFinalResponse()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 3,
			maxSegmentIterations: 3,
			ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"value\"}", "call_1"),
			ToolCall("bash", "{\"command\":\"dotnet build --help\",\"description\":\"Validate\"}", "call_2"),
			Completion("validation complete"),
			Completion("final validated summary"));

		await runtime.SubmitAsync(session.Id, "create, validate, and summarize the file");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("finalization_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "final validated summary");
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("completed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.Equal(1, summaryJson.RootElement.GetProperty("finalizationContinuations").GetInt32());
	}

	[Fact]
	public async Task RequestedTestsGetReservedAuthoringAndValidationPasses()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 3,
			maxSegmentIterations: 3,
			ToolCall("write", "{\"file_path\":\"src/Service.cs\",\"content\":\"service\"}", "call_1"),
			ToolCall("glob", "{\"pattern\":\"tests/*.cs\"}", "call_2"),
			Completion("implementation done"),
			ToolCall("write", "{\"file_path\":\"tests/ServiceTests.cs\",\"content\":\"tests\"}", "call_3"),
			Completion("tests added"),
			ToolCall("powershell", "{\"command\":\"Write-Output 'Passed: 1, Failed: 0'\",\"description\":\"Run focused tests\"}", "call_4"),
			Completion("done"));

		await runtime.SubmitAsync(session.Id, "Implement the service and add unit tests covering it.");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("requested_tests_continuation"));
		Assert.Single(transcript.Events, item => item.Type == "continuation" && item.Content.Contains("validation_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "done");
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("completed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.True(summaryJson.RootElement.GetProperty("testsChanged").GetBoolean());
		Assert.True(summaryJson.RootElement.GetProperty("successfulTests").GetInt32() > 0);
		Assert.Equal("satisfied", summaryJson.RootElement.GetProperty("validationStatus").GetString());
	}

	[Fact]
	public async Task ASuccessfulBuildAfterTheLatestSourceMutationSatisfiesValidation()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var edit = new Dictionary<string, object?> { ["file_path"] = "src/Service.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "edit", edit);
		lifecycle.FireToolResult(turn, "edit", edit, "Updated src/Service.cs");

		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);

		var build = new Dictionary<string, object?> { ["command"] = "dotnet build App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", build);
		lifecycle.FireToolResult(turn, "bash", build, "Build succeeded");

		Assert.True(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task ExternalScratchWritesDoNotCountAsWorkspaceMutations()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = Path.Combine(Path.GetTempPath(), "runner.cs") };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created file");

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulMutations);
	}

	[Fact]
	public async Task PassingCodingWorkflowCarriesCumulativeMutationAndValidationEvidence()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var arguments = new Dictionary<string, object?> { ["request"] = "implement and test" };
		await lifecycle.FireBeforeToolCallAsync(turn, "coding_workflow", arguments);
		var result = JsonSerializer.Serialize(new
		{
			status = "pass",
			stages = new object[]
			{
				new { role = "junior-developer:1", status = "completed", handoff = new { filesChanged = new[] { "src/App.cs", "tests/AppTests.cs" } } },
				new { role = "verifier", status = "pass", handoff = new { commands = new[] { "dotnet test => 5 passed" } } },
			},
		});
		lifecycle.FireToolResult(turn, "coding_workflow", arguments, result);

		var progress = lifecycle.Progress(turn.SessionId);
		Assert.Equal(2, progress.SuccessfulMutations);
		Assert.True(progress.TestsChanged);
		Assert.True(progress.ValidationSatisfied);
	}

	[Theory]
	[InlineData("Build FAILED.\n1 Error(s)")]
	[InlineData("src/Service.cs(1,1): error CS1002: ; expected")]
	[InlineData("[exit code: 1]")]
	public async Task LaterFailedBuildRevokesEarlierBuildCredit(string laterOutput)
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var edit = new Dictionary<string, object?> { ["file_path"] = "src/Service.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "edit", edit);
		lifecycle.FireToolResult(turn, "edit", edit, "Updated src/Service.cs");

		var build = new Dictionary<string, object?> { ["command"] = "dotnet build App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", build);
		lifecycle.FireToolResult(turn, "bash", build, "Build succeeded");
		Assert.True(lifecycle.Progress(turn.SessionId).ValidationSatisfied);

		await lifecycle.FireBeforeToolCallAsync(turn, "bash", build);
		lifecycle.FireToolResult(turn, "bash", build, laterOutput);

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulBuildsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task ChangedTestsRequireATestRunAfterTheLatestMutation()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var build = new Dictionary<string, object?> { ["command"] = "dotnet build App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", build);
		lifecycle.FireToolResult(turn, "bash", build, "Build succeeded");
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);

		var test = new Dictionary<string, object?> { ["command"] = "dotnet test App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", test);
		lifecycle.FireToolResult(turn, "bash", test, "Passed: 5, Failed: 0");
		Assert.True(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task RestoreOnlyTestOutputDoesNotSatisfyValidation()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var run = new Dictionary<string, object?> { ["command"] = "dotnet test App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "powershell", run);
		lifecycle.FireToolResult(turn, "powershell", run,
			"Determining projects to restore...\nNothing to do. None of the projects specified contain packages to restore.");

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task AZeroTestRunnerDoesNotSatisfyValidation()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var run = new Dictionary<string, object?> { ["command"] = "Invoke-NUnitReflectionRunner" };
		await lifecycle.FireBeforeToolCallAsync(turn, "powershell", run);
		lifecycle.FireToolResult(turn, "powershell", run, "Total Tests: 0, Passed: , Failed:");

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Theory]
	[InlineData("Passed: 5, Failed: 0")]
	[InlineData("Test Run Completed: 16 Passed, 0 Failed.")]
	public async Task PositiveCustomRunnerOutputSatisfiesValidation(string output)
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var run = new Dictionary<string, object?> { ["command"] = "& $runnerExe tests/bin/ServiceTests.dll" };
		await lifecycle.FireBeforeToolCallAsync(turn, "powershell", run);
		lifecycle.FireToolResult(turn, "powershell", run, output);

		Assert.Equal(1, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.True(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task PositivePassCountDoesNotOverrideReportedFailures()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var run = new Dictionary<string, object?> { ["command"] = "dotnet test App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", run);
		lifecycle.FireToolResult(turn, "bash", run, "Passed: 5, Failed: 1");

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Theory]
	[InlineData("Total: Passed=0 Failed=0 Skipped=0")]
	[InlineData("TYPE NOT FOUND!\nPassed: 0, Failed: 0")]
	[InlineData("Passed: 5, Failed: 1")]
	[InlineData("Error: test host failed\n[exit code: 1]")]
	public async Task LaterUnsuccessfulTestAttemptRevokesEarlierTestCredit(string laterOutput)
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var write = new Dictionary<string, object?> { ["file_path"] = "tests/ServiceTests.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "write", write);
		lifecycle.FireToolResult(turn, "write", write, "Created tests/ServiceTests.cs");

		var run = new Dictionary<string, object?> { ["command"] = "dotnet test App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "powershell", run);
		lifecycle.FireToolResult(turn, "powershell", run, "Passed: 5, Failed: 0");
		Assert.True(lifecycle.Progress(turn.SessionId).ValidationSatisfied);

		await lifecycle.FireBeforeToolCallAsync(turn, "powershell", run);
		lifecycle.FireToolResult(turn, "powershell", run, laterOutput);

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task FailedTestAttemptAlsoRevokesEarlierBuildCredit()
	{
		var lifecycle = new AgentLifecycle();
		var turn = Turn();
		await lifecycle.FireBeforeTurnAsync(turn);
		var edit = new Dictionary<string, object?> { ["file_path"] = "src/Service.cs" };
		await lifecycle.FireBeforeToolCallAsync(turn, "edit", edit);
		lifecycle.FireToolResult(turn, "edit", edit, "Updated src/Service.cs");

		var build = new Dictionary<string, object?> { ["command"] = "dotnet build App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", build);
		lifecycle.FireToolResult(turn, "bash", build, "Build succeeded");

		var test = new Dictionary<string, object?> { ["command"] = "dotnet test App.sln" };
		await lifecycle.FireBeforeToolCallAsync(turn, "bash", test);
		lifecycle.FireToolResult(turn, "bash", test, "Passed: 4, Failed: 1");

		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulBuildsAfterLastMutation);
		Assert.Equal(0, lifecycle.Progress(turn.SessionId).SuccessfulTestsAfterLastMutation);
		Assert.False(lifecycle.Progress(turn.SessionId).ValidationSatisfied);
	}

	[Fact]
	public async Task AnUnvalidatedMutationGetsOneCorrectionThenEndsWithAnError()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 6,
			maxSegmentIterations: 3,
			ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"value\"}", "call_1"),
			Completion("done without validation"));

		await runtime.SubmitAsync(session.Id, "create and verify the file");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("validation_continuation"));
		Assert.Contains(transcript.Events, item => item.Type == "error" && item.Content.Contains("without a successful build or test"));
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = JsonDocument.Parse(summary.Content);
		Assert.Equal("failed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.Equal("validation_missing", summaryJson.RootElement.GetProperty("stopReason").GetString());
		Assert.Equal(1, summaryJson.RootElement.GetProperty("mutations").GetInt32());
		Assert.False(summaryJson.RootElement.GetProperty("validationSatisfied").GetBoolean());
	}

	[Fact]
	public async Task ATemporaryRootRunnerGetsOneCleanupPass()
	{
		var lifecycle = new AgentLifecycle();
		var (runtime, session) = await StartAsync(
			lifecycle,
			maxTurnIterations: 9,
			maxSegmentIterations: 3,
			ToolCall("write", "{\"file_path\":\"run-tests.ps1\",\"content\":\"Write-Output test\"}", "call_1"),
			Completion("done"),
			ToolCall("bash", "{\"command\":\"rm run-tests.ps1\",\"description\":\"Remove temporary runner\"}", "call_2"),
			Completion("cleaned"));

		await runtime.SubmitAsync(session.Id, "use a temporary runner and clean up");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation" && item.Content.Contains("scratch_hygiene_continuation"));
		Assert.DoesNotContain(transcript.Events, item => item.Type == "notice" && item.Content.Contains("remain in the workspace"));
		Assert.False(File.Exists(Path.Combine(Workspace, "run-tests.ps1")));
	}

	// ─── Fixtures ────────────────────────────────────────────────────────

	private static TurnContext Turn() => new(
		Guid.NewGuid(),
		Path.Combine(Path.GetTempPath(), "suharness-tool-pipeline-workspace"),
		1,
		1,
		CancellationToken.None);

	/// <summary>Answer the next question this session raises, the way the composer's panel would.</summary>
	private static void AnswerWith(UserQuestionBroker questions, string choice) =>
		questions.Changed += sessionId =>
		{
			if (questions.Pending(sessionId) is not { } pending) return;
			questions.TryAnswer(sessionId, [.. pending.Questions.Select(item => new UserQuestionAnswerItem(item.Id, [choice]))]);
		};

	/// <summary>Run one wrapped tool through the pipeline without a model behind it.</summary>
	private static async Task<object?> InvokeAsync(
		AgentLifecycle lifecycle,
		IUserQuestionBroker? questions,
		Func<string> body)
	{
		var turn = Turn();
		var tool = Tool(body, lifecycle, questions, new ToolApprovalScope(), turn);
		return await tool.InvokeAsync(new AIFunctionArguments());
	}

	private static LifecycleTool Tool(
		Func<string> body,
		AgentLifecycle lifecycle,
		IUserQuestionBroker? questions,
		ToolApprovalScope approvals,
		TurnContext turn) =>
		new(
			AIFunctionFactory.Create(body, new AIFunctionFactoryOptions { Name = "probe" }),
			lifecycle,
			() => turn,
			questions,
			approvals);

	private string Workspace
	{
		get
		{
			var path = Path.Combine(_root, "workspace");
			Directory.CreateDirectory(path);
			return path;
		}
	}

	private static string Completion(string content) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
		+ JsonSerializer.Serialize(content)
		+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private static string Truncated(string content) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
		+ JsonSerializer.Serialize(content)
		+ "},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private static string ToolCall(string name, string argumentsJson, string id) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":["
		+ "{\"id\":\"" + id + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
		+ JsonSerializer.Serialize(argumentsJson)
		+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private async Task<(HarnessRuntime Runtime, HarnessSession Session)> StartAsync(
		IAgentLifecycle lifecycle,
		params string[] responses) => await StartAsync(lifecycle, 3, 3, responses);

	private async Task<(HarnessRuntime Runtime, HarnessSession Session)> StartAsync(
		IAgentLifecycle lifecycle,
		int maxTurnIterations,
		int maxSegmentIterations,
		params string[] responses) => await StartAsync(
			lifecycle,
			maxTurnIterations,
			maxSegmentIterations,
			autoContinueUnfinished: false,
			responses);

	private async Task<(HarnessRuntime Runtime, HarnessSession Session)> StartAsync(
		IAgentLifecycle lifecycle,
		int maxTurnIterations,
		int maxSegmentIterations,
		bool autoContinueUnfinished,
		params string[] responses)
	{
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(new Sequence(responses))),
			lifecycle: lifecycle);
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted",
				"Scripted",
				"https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")],
				MaxToolIterationsPerTurn: maxTurnIterations,
				MaxToolIterationsPerSegment: maxSegmentIterations,
				AutoContinueUnfinishedTurns: autoContinueUnfinished),
			"test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(
			store,
			service,
			lifecycle: lifecycle,
			toolIterationPolicies: service,
			behaviorPolicies: service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return (runtime, session);
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>Replays scripted completions, repeating the last one and minting fresh call ids.</summary>
	private sealed class Sequence(string[] responses) : HttpMessageHandler
	{
		private int _index;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = responses[Math.Min(_index, responses.Length - 1)]
				.Replace("\"call_", $"\"c{_index}_", StringComparison.Ordinal);
			_index++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
		}
	}
}

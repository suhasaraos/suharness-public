using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// These integration tests drive complete runtimes with real persistence and cancellation handshakes.
/// Running them beside the rest of the suite can starve their bounded coordination waits on hosted CI.
/// </summary>
[CollectionDefinition(OrchestrationTests.CollectionName, DisableParallelization = true)]
public sealed class OrchestrationCollection;

/// <summary>
/// Tests of the multi-agent and orchestration surface: the agent inbox, same-session goals and
/// their round driver, background jobs, the subagent fabric, lifecycle interception, and turn
/// cancellation. These cover the behaviours the harness previously declared but did not deliver —
/// tools that were advertised to the model while nothing behind them actually ran.
/// </summary>
[Collection(CollectionName)]
public sealed class OrchestrationTests : IDisposable
{
	internal const string CollectionName = "orchestration";

	private sealed record FinalHandoff(string Status, string Summary);

	private readonly string _root = Directory.CreateTempSubdirectory("suharness-orch-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private string Workspace
	{
		get
		{
			var path = Path.Combine(_root, "workspace");
			Directory.CreateDirectory(path);
			return path;
		}
	}

	private static ModelProviderProfile Profile() => new(
		"scripted",
		"Scripted",
		"https://scripted.test/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor("scripted-model", "Scripted Model")]);

	/// <summary>Compose the full orchestration graph over a scripted model, as Program.cs does.</summary>
	private async Task<Harness> StartAsync(params string[] responses)
	{
		var model = new ScriptedModel(responses);
		var sessions = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var goals = new GoalService(sessions);
		var jobs = new JobService();
		var inbox = new AgentInbox();
		var lifecycle = new AgentLifecycle();

		ModelConfigurationService? service = null;
		var subagents = new SubagentService(() => service!, sessions, inbox, retryPolicies: () => service!);
		service = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(model)),
			userQuestions: null,
			planModeStore: null,
			goalService: goals,
			jobService: jobs,
			lifecycle: lifecycle,
			subagentService: subagents,
			sessionStoreRef: sessions);

		await service.SaveProviderAsync(Profile(), "test-key");
		var runtime = new HarnessRuntime(sessions, service, goals, inbox, lifecycle);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return new Harness(runtime, session, model, sessions, goals, jobs, inbox, lifecycle, subagents, service);
	}

	private sealed record Harness(
		HarnessRuntime Runtime,
		HarnessSession Session,
		ScriptedModel Model,
		JsonlSessionStore Sessions,
		GoalService Goals,
		JobService Jobs,
		AgentInbox Inbox,
		AgentLifecycle Lifecycle,
		SubagentService Subagents,
		IAgentFactory AgentFactory);

	// ── Inbox ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task QueuedInboxMessagesReachTheModel()
	{
		var harness = await StartAsync(ScriptedModel.Text("understood"));
		harness.Inbox.Enqueue(harness.Session.Id, new AgentInboxMessage(
			"m1", InboxMessageKind.SubagentReport, "the audit subagent found 3 issues"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "what did you find?");

		// A queued report that only lands in the transcript is invisible to the model; it has to
		// arrive as model input on the very turn that claims it.
		Assert.Contains("the audit subagent found 3 issues", harness.Model.Requests[0]);
	}

	[Fact]
	public async Task AnInboxMessageStaysInHistoryOnLaterTurns()
	{
		var harness = await StartAsync(ScriptedModel.Text("noted"));
		harness.Inbox.Enqueue(harness.Session.Id, new AgentInboxMessage(
			"m1", InboxMessageKind.SubagentReport, "the audit subagent found 3 issues"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "first");
		await harness.Runtime.SubmitAsync(harness.Session.Id, "second");

		// The second turn rebuilds history from the transcript, so an injected report has to replay
		// rather than being dropped as a presentation-only notice.
		Assert.Contains("the audit subagent found 3 issues", harness.Model.Requests[1]);
	}

	[Fact]
	public async Task ClaimedInboxMessagesAreNotDeliveredTwice()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		harness.Inbox.Enqueue(harness.Session.Id, new AgentInboxMessage("m1", InboxMessageKind.Notice, "one-shot notice"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "go");

		Assert.Empty(harness.Inbox.Peek(harness.Session.Id));
	}

	// ── Goals and the round driver ─────────────────────────────────────────

	[Fact]
	public async Task AnArmedGoalDrivesRoundsUntilTheModelCompletesIt()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("create_goal", "{\"objective\":\"ship the feature\"}"),
			ScriptedModel.Text("goal created"),
			ScriptedModel.ToolCall("update_goal", "{\"goal_id\":\"g\",\"revision\":1,\"action\":\"complete\"}"),
			ScriptedModel.Text("objective achieved"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "ship the feature");

		// The human turn is followed by one automatic round, which the model ends by completing the
		// goal. Without a round driver a goal is just a note in the transcript.
		Assert.Equal(4, harness.Model.Requests.Count);
		Assert.Contains("<goal_round>", harness.Model.Requests[2]);
		Assert.Contains("ship the feature", harness.Model.Requests[2]);
		Assert.Equal(GoalPhase.Complete, harness.Goals.Get(harness.Session.Id)!.Phase);
	}

	[Fact]
	public async Task GoalRoundsStopAtTheRoundCap()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("create_goal", "{\"objective\":\"keep going\",\"max_goal_rounds\":2}"),
			ScriptedModel.Text("goal created"),
			ScriptedModel.Text("still working"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "keep going");

		// Two model requests for the human turn, then exactly two automatic rounds.
		Assert.Equal(4, harness.Model.Requests.Count);
		var goal = harness.Goals.Get(harness.Session.Id)!;
		Assert.Equal(2, goal.RoundsStarted);
		Assert.Equal(GoalPhase.Active, goal.Phase);
	}

	[Fact]
	public async Task AGoalWithNoExplicitCapIsStillBounded()
	{
		var harness = await StartAsync(ScriptedModel.Text("hi"));
		var goal = harness.Goals.Create(harness.Session.Id, "open ended");

		// An uncapped goal would let the round driver re-enter the agent loop forever.
		Assert.Equal(GoalService.DefaultMaxGoalRounds, goal.MaxGoalRounds);
		Assert.NotEqual(int.MaxValue, goal.MaxGoalRounds);
	}

	[Fact]
	public async Task CancellingAGoalRoundDisarmsTheGoal()
	{
		var harness = await StartAsync(ScriptedModel.Text("working"));
		harness.Goals.Create(harness.Session.Id, "long running objective", maxGoalRounds: 2);
		var roundStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var turns = 0;
		harness.Lifecycle.BeforeTurn += async context =>
		{
			if (Interlocked.Increment(ref turns) == 2)
			{
				roundStarted.TrySetResult();
				await Task.Delay(Timeout.Infinite, context.CancellationToken);
			}
			return true;
		};
		using var cancellation = new CancellationTokenSource();

		var running = harness.Runtime.SubmitAsync(harness.Session.Id, "start", cancellationToken: cancellation.Token);
		await roundStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
		Assert.False(harness.Goals.ShouldContinue(harness.Session.Id));
		Assert.Equal(GoalActivation.Disarmed, harness.Goals.Get(harness.Session.Id)!.Activation);
	}

	[Fact]
	public async Task CancellingTheHumanTurnDisarmsAnArmedGoal()
	{
		var harness = await StartAsync(ScriptedModel.Text("working"));
		harness.Goals.Create(harness.Session.Id, "long running objective", maxGoalRounds: 2);
		var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Lifecycle.BeforeTurn += async context =>
		{
			turnStarted.TrySetResult();
			await Task.Delay(Timeout.Infinite, context.CancellationToken);
			return true;
		};
		using var cancellation = new CancellationTokenSource();

		var running = harness.Runtime.SubmitAsync(harness.Session.Id, "start", cancellationToken: cancellation.Token);
		await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
		Assert.False(harness.Goals.ShouldContinue(harness.Session.Id));
	}

	[Fact]
	public async Task AUserMessageQueuedDuringARunReachesTheNextGoalRound()
	{
		var harness = await StartAsync(ScriptedModel.Text("working"));
		harness.Goals.Create(harness.Session.Id, "one more round", maxGoalRounds: 1);
		var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Lifecycle.BeforeTurn += async _ =>
		{
			turnStarted.TrySetResult();
			await release.Task;
			return true;
		};

		var running = harness.Runtime.SubmitAsync(harness.Session.Id, "start");
		await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(harness.Runtime.QueueMessage(harness.Session.Id, "use the queued constraint"));
		release.TrySetResult();
		await running;

		Assert.Equal(2, harness.Model.Requests.Count);
		Assert.Contains("use the queued constraint", harness.Model.Requests[1]);
		Assert.False(harness.Runtime.QueueMessage(harness.Session.Id, "too late"));
	}

	[Fact]
	public async Task AGoalSurvivesARestartAndComesBackDisarmed()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		harness.Goals.Create(harness.Session.Id, "long running objective");
		await harness.Goals.DrainAsync(harness.Session.Id);

		// A fresh service over the same store has no cache, so this can only come from the log.
		var restarted = new GoalService(new JsonlSessionStore(Path.Combine(_root, "sessions")));
		await restarted.RehydrateAsync(harness.Session.Id);

		var goal = restarted.Get(harness.Session.Id);
		Assert.NotNull(goal);
		Assert.Equal("long running objective", goal.Objective);
		Assert.Equal(GoalPhase.Active, goal.Phase);
		// Automatic continuation authority must not survive a restart: a human decides to resume.
		Assert.Equal(GoalActivation.Disarmed, goal.Activation);
		Assert.False(restarted.ShouldContinue(harness.Session.Id));
	}

	[Fact]
	public async Task AStaleRevisionIsRejected()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		harness.Goals.Create(harness.Session.Id, "objective");

		Assert.Throws<InvalidOperationException>(() => harness.Goals.Complete(harness.Session.Id, 99));
	}

	[Fact]
	public async Task AnExhaustedGoalCannotBeResumedWithoutRaisingItsCap()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var goal = harness.Goals.Create(harness.Session.Id, "objective", maxGoalRounds: 1);
		harness.Goals.RecordRound(harness.Session.Id);
		harness.Goals.Pause(harness.Session.Id, goal.Revision);

		// Resuming an exhausted goal would arm it for a continuation that can never run.
		var error = Assert.Throws<InvalidOperationException>(
			() => harness.Goals.Resume(harness.Session.Id, harness.Goals.Get(harness.Session.Id)!.Revision));
		Assert.Contains("exhausted", error.Message);
	}

	// ── Background jobs ────────────────────────────────────────────────────

	[Fact]
	public void KillingAJobWithNoCancelHookStillSettlesIt()
	{
		var jobs = new JobService();
		var owner = Guid.NewGuid();
		var never = new TaskCompletionSource<JobOutcome>();
		var id = jobs.Start(owner, JobKind.Generic, "stuck", _ => new JobHooks(never.Task));

		Assert.True(jobs.Kill(id, owner));

		// Without settlement the job would sit in Stopping forever and job_output would never
		// report a terminal status to the model.
		Assert.Equal(JobStatus.Killed, jobs.Get(id, owner).Status);
	}

	[Fact]
	public async Task AJobIsFencedToItsOwningSession()
	{
		var jobs = new JobService();
		var owner = Guid.NewGuid();
		var id = jobs.Start(owner, JobKind.Generic, "mine", _ => new JobHooks(Task.FromResult(new JobOutcome(JobStatus.Completed))));
		await jobs.WaitAsync(id, 2000, owner);

		Assert.Throws<InvalidOperationException>(() => jobs.Read(id, Guid.NewGuid()));
		Assert.Empty(jobs.List(Guid.NewGuid()));
	}

	[Fact]
	public async Task ABackgroundSubagentIsVisibleAsABackgroundJob()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("subagent", "{\"description\":\"audit deps\",\"prompt\":\"audit the dependencies\"}"),
			ScriptedModel.Text("delegated"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "audit the dependencies");

		// The job tools are advertised to the model, so background delegation has to actually
		// register as a job rather than leaving job_list permanently empty.
		var jobs = harness.Jobs.List(harness.Session.Id);
		Assert.Single(jobs);
		Assert.Equal(JobKind.Subagent, jobs[0].Kind);
		Assert.Equal("audit deps", jobs[0].Label);
	}

	// ── Subagents ──────────────────────────────────────────────────────────

	[Fact]
	public async Task AForegroundSubagentReturnsItsResult()
	{
		var harness = await StartAsync(ScriptedModel.Text("the child's answer"));

		var run = await harness.Subagents.StartAsync("child", "do the work", harness.Session.Id, harness.Session.Id);
		// A result delivered before the starter began awaiting must not be lost; a regression here
		// hangs the calling tool forever rather than failing.
		var result = await run.Result.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(SubagentStopReason.Completed, result.StopReason);
		Assert.Equal("the child's answer", result.Output);
	}

	[Fact]
	public async Task AForegroundSubagentRetriesTransientModelFailure()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "note.txt"), "hello\n");
		var harness = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.HttpServiceUnavailable,
			ScriptedModel.Text("recovered child answer"));

		var run = await harness.Subagents.StartAsync("child", "do the work", harness.Session.Id, harness.Session.Id);
		var result = await run.Result.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(SubagentStopReason.Completed, result.StopReason);
		Assert.Equal("recovered child answer", result.Output);
		Assert.Equal(3, harness.Model.Requests.Count);
		Assert.Contains("1: hello", harness.Model.Requests[1]);
		var child = Assert.Single(await harness.Sessions.ListAsync(), session => session.ParentSessionId == harness.Session.Id);
		var transcript = await harness.Sessions.GetAsync(child.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("being retried"));
	}

	[Fact]
	public async Task WorkflowRoleFinalizesJsonInTheSameChildWithoutTools()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "note.txt"), "hello\n");
		var harness = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.Text("The note is implemented, but this is not JSON."),
			ScriptedModel.Text("{\"status\":\"completed\",\"summary\":\"done\"}"));

		var run = await harness.Subagents.StartAsync(
			"junior-developer-1",
			"Inspect note.txt and return JSON.",
			harness.Session.Id,
			harness.Session.Id,
			runInBackground: false,
			options: new SubagentRunOptions(
				AgentToolProfile.WorkflowCode,
				"Return only the requested JSON object.",
				JsonOutputType: typeof(FinalHandoff)));
		var result = await run.Result;

		Assert.Equal(SubagentStopReason.Completed, result.StopReason);
		Assert.Equal("{\"status\":\"completed\",\"summary\":\"done\"}", result.Output);
		Assert.Equal(3, harness.Model.Requests.Count);
		Assert.Contains("You are a focused SuHarness workflow role", harness.Model.Requests[0]);
		Assert.Contains("\"name\":\"read\"", harness.Model.Requests[0]);
		Assert.Contains("\"name\":\"edit\"", harness.Model.Requests[0]);
		Assert.DoesNotContain("\"name\":\"coding_workflow\"", harness.Model.Requests[0]);
		Assert.Contains("1: hello", harness.Model.Requests[1]);
		Assert.Contains("Return only the requested JSON object", harness.Model.Requests[2]);
		Assert.Contains("The note is implemented", harness.Model.Requests[2]);
		using var finalRequest = System.Text.Json.JsonDocument.Parse(harness.Model.Requests[2]);
		Assert.True(!finalRequest.RootElement.TryGetProperty("tools", out var tools)
			|| tools.ValueKind == System.Text.Json.JsonValueKind.Array && tools.GetArrayLength() == 0);
		Assert.Equal(
			"json_schema",
			finalRequest.RootElement.GetProperty("response_format").GetProperty("type").GetString());
		var schema = finalRequest.RootElement.GetProperty("response_format").GetProperty("json_schema");
		Assert.Equal("handoff", schema.GetProperty("name").GetString());
		Assert.True(schema.GetProperty("schema").GetProperty("properties").TryGetProperty("status", out _));
	}

	[Fact]
	public async Task WorkflowCodeRolePreservesWorkspaceEvidenceWhenProviderFailsAfterWrite()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"written before failure\\n\"}"),
			ScriptedModel.HttpServiceUnavailable);

		var run = await harness.Subagents.StartAsync(
			"junior-developer-1",
			"Create created.txt, then return JSON.",
			harness.Session.Id,
			harness.Session.Id,
			runInBackground: false,
			options: new SubagentRunOptions(AgentToolProfile.WorkflowCode));
		var result = await run.Result;

		Assert.Equal(SubagentStopReason.Error, result.StopReason);
		Assert.True(File.Exists(Path.Combine(Workspace, "created.txt")));
		Assert.Contains("created.txt", result.Evidence!.FilesChanged);
	}

	[Fact]
	public async Task WorkflowRoleDeadlinePreservesWorkspaceEvidenceAfterWrite()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("write", "{\"file_path\":\"created.txt\",\"content\":\"written before timeout\\n\"}"),
			ScriptedModel.Hang);

		var run = await harness.Subagents.StartAsync(
			"junior-developer-1",
			"Create created.txt, then return JSON.",
			harness.Session.Id,
			harness.Session.Id,
			runInBackground: false,
			options: new SubagentRunOptions(AgentToolProfile.WorkflowCode, TimeoutSeconds: 1));
		var result = await run.Result;

		Assert.Equal(SubagentStopReason.Error, result.StopReason);
		Assert.Contains("deadline", result.ErrorDetail);
		Assert.Contains("created.txt", result.Evidence!.FilesChanged);
	}

	[Fact]
	public async Task WorkflowVerifierBlocksSourceMutationsFromItsCommandTool()
	{
		var tool = OperatingSystem.IsWindows() ? "powershell" : "bash";
		var command = OperatingSystem.IsWindows()
			? "Set-Content -Path verifier-change.txt -Value changed; Write-Output 'MSBuild.exe validation'"
			: "printf changed > verifier-change.txt; printf 'msbuild validation'";
		var harness = await StartAsync(
			ScriptedModel.ToolCall(tool, System.Text.Json.JsonSerializer.Serialize(new
			{
				command,
				description = "Create verifier mutation",
			})),
			ScriptedModel.Text("{\"status\":\"pass\",\"summary\":\"done\",\"commands\":[\"check => passed\"],\"findings\":[]}"));

		var run = await harness.Subagents.StartAsync(
			"verifier",
			"Run validation and return JSON.",
			harness.Session.Id,
			harness.Session.Id,
			runInBackground: false,
			options: new SubagentRunOptions(AgentToolProfile.WorkflowVerify));
		var result = await run.Result;

		Assert.Equal(SubagentStopReason.Completed, result.StopReason);
		Assert.False(File.Exists(Path.Combine(Workspace, "verifier-change.txt")));
		Assert.Empty(result.Evidence!.FilesChanged);
	}

	[Fact]
	public async Task ABackgroundSubagentReportsToItsParentInbox()
	{
		var harness = await StartAsync(ScriptedModel.Text("child finished the audit"));

		var run = await harness.Subagents.StartAsync("child", "audit", harness.Session.Id, harness.Session.Id, runInBackground: true);
		await run.Result.WaitAsync(TimeSpan.FromSeconds(30));

		var pending = harness.Inbox.Peek(harness.Session.Id);
		Assert.Single(pending);
		Assert.Equal(InboxMessageKind.SubagentReport, pending[0].Kind);
		Assert.Contains("child finished the audit", pending[0].Content);
	}

	[Fact]
	public async Task ABackgroundSubagentResultOutlivesTheTurnThatStartedIt()
	{
		var harness = await StartAsync(ScriptedModel.Text("done"));
		using var starterTurn = new CancellationTokenSource();

		var run = await harness.Subagents.StartAsync(
			"child", "work", harness.Session.Id, harness.Session.Id, runInBackground: true, cancellationToken: starterTurn.Token);
		// The parent's turn ends the moment the delegation is handed off.
		await starterTurn.CancelAsync();

		var result = await run.Result.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal(SubagentStopReason.Completed, result.StopReason);
	}

	[Fact]
	public async Task AContinuedSubagentReplaysItsToolHistoryAfterRestart()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("glob", "{\"pattern\":\"**/*\"}"),
			ScriptedModel.Text("inspection complete"),
			ScriptedModel.Text("continued"));
		var firstRun = await harness.Subagents.StartAsync(
			"child", "inspect", harness.Session.Id, harness.Session.Id, runInBackground: true);
		await firstRun.Result.WaitAsync(TimeSpan.FromSeconds(30));
		harness.Inbox.Dequeue(harness.Session.Id);

		var restarted = new SubagentService(() => harness.AgentFactory, harness.Sessions, harness.Inbox);
		var reportArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Inbox.Changed += sessionId =>
		{
			if (sessionId == harness.Session.Id && harness.Inbox.Peek(sessionId).Count > 0)
				reportArrived.TrySetResult();
		};
		await restarted.SendMessageAsync(Guid.ParseExact(firstRun.Id, "N"), "continue from the inspection");
		await reportArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(3, harness.Model.Requests.Count);
		Assert.Contains("call_glob", harness.Model.Requests[2]);
		Assert.Contains("inspection complete", harness.Model.Requests[2]);
	}

	[Fact]
	public async Task PersistedDelegationDepthStillAppliesAfterRestart()
	{
		var harness = await StartAsync(ScriptedModel.Text("done"));
		var original = new SubagentService(() => new NullFactory(), harness.Sessions, harness.Inbox, maxDepth: 1);
		var child = await original.StartAsync("child", "work", harness.Session.Id, harness.Session.Id, runInBackground: true);
		await child.Result.WaitAsync(TimeSpan.FromSeconds(30));

		var restarted = new SubagentService(() => new NullFactory(), harness.Sessions, harness.Inbox, maxDepth: 1);
		var childId = Guid.ParseExact(child.Id, "N");
		Assert.Equal(1, restarted.DepthOf(childId));
		Assert.Equal(SubagentStatus.Ready, Assert.Single(restarted.ListChildren(harness.Session.Id)).Status);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => restarted.StartAsync("grandchild", "work", childId, harness.Session.Id, runInBackground: true));
	}

	[Fact]
	public async Task CancellingAForegroundDelegationStopsItsChild()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var factory = new BlockingFactory(Task.Delay(Timeout.Infinite));
		var subagents = new SubagentService(() => factory, harness.Sessions, harness.Inbox);
		using var cancellation = new CancellationTokenSource();
		var starting = subagents.StartAsync(
			"child", "work", harness.Session.Id, harness.Session.Id, cancellationToken: cancellation.Token);
		await factory.Running.Task.WaitAsync(TimeSpan.FromSeconds(30));

		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
		await factory.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
	}

	[Fact]
	public async Task CancellingAWorkflowStopsItsDelegatedAgent()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var factory = new BlockingFactory(Task.Delay(Timeout.Infinite));
		var subagents = new SubagentService(() => factory, harness.Sessions, harness.Inbox);
		var workflow = new WorkflowEngine(subagents, harness.Session.Id, harness.Session.Id);
		using var document = System.Text.Json.JsonDocument.Parse("{}");
		using var cancellation = new CancellationTokenSource();
		var running = workflow.RunAsync("agent(\"work\");", document.RootElement, cancellation.Token);
		await factory.Running.Task.WaitAsync(TimeSpan.FromSeconds(30));

		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
		await factory.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
	}

	[Fact]
	public async Task DelegationIsBoundedByDepth()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var subagents = new SubagentService(() => new NullFactory(), harness.Sessions, harness.Inbox, maxDepth: 1);

		var child = await subagents.StartAsync("child", "work", harness.Session.Id, harness.Session.Id, runInBackground: true);
		var childId = Guid.ParseExact(child.Id, "N");

		// Without a recursion budget an agent that delegates can spawn an unbounded agent tree.
		var error = await Assert.ThrowsAsync<InvalidOperationException>(
			() => subagents.StartAsync("grandchild", "work", childId, harness.Session.Id, runInBackground: true));
		Assert.Contains("depth limit", error.Message);
	}

	[Fact]
	public async Task ListingDescendantsWalksTheWholeTree()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var subagents = new SubagentService(() => new NullFactory(), harness.Sessions, harness.Inbox);

		var child = await subagents.StartAsync("child", "work", harness.Session.Id, harness.Session.Id, runInBackground: true);
		var childId = Guid.ParseExact(child.Id, "N");
		await subagents.StartAsync("grandchild", "work", childId, harness.Session.Id, runInBackground: true);

		Assert.Single(subagents.ListChildren(harness.Session.Id));
		var tree = subagents.ListChildren(harness.Session.Id, descendants: true);
		Assert.Equal(2, tree.Count);
		Assert.Equal([1, 2], tree.Select(entry => entry.Depth));
	}

	[Fact]
	public async Task InterruptingAnAgentEndsItsTurnButKeepsItUsable()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var blocked = new TaskCompletionSource();
		var factory = new BlockingFactory(blocked.Task);
		var subagents = new SubagentService(() => factory, harness.Sessions, harness.Inbox);

		var run = await subagents.StartAsync("child", "work", harness.Session.Id, harness.Session.Id, runInBackground: true);
		var childId = Guid.ParseExact(run.Id, "N");
		await factory.Running.Task.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(subagents.Interrupt(childId));
		var result = await run.Result.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal(SubagentStopReason.Aborted, result.StopReason);

		// interrupt_agent promises the agent survives for follow-ups; cancelling its whole worker
		// would make every later send_message fail.
		Assert.NotNull(subagents.Get(childId));
		Assert.NotEqual(SubagentStatus.Terminal, subagents.Get(childId)!.Status);
		await subagents.SendMessageAsync(childId, "carry on");
	}

	[Fact]
	public async Task InterruptingAnUnknownAgentIsAcceptedAsANoOp()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		Assert.False(harness.Subagents.Interrupt(Guid.NewGuid()));
	}

	// ── Lifecycle and cancellation ─────────────────────────────────────────

	[Fact]
	public async Task TurnAndStepLifecycleEventsFire()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("glob", "{\"pattern\":\"**/*\"}"),
			ScriptedModel.Text("found them"));
		var observed = new List<string>();
		harness.Lifecycle.BeforeTurn += _ => { observed.Add("turn/start"); return ValueTask.FromResult(true); };
		harness.Lifecycle.AfterTurn += (_, _) => observed.Add("turn/end");
		harness.Lifecycle.BeforeStep += context => observed.Add($"step/start:{context.StepNumber}");
		harness.Lifecycle.AfterStep += (context, _) => observed.Add($"step/end:{context.StepNumber}");

		await harness.Runtime.SubmitAsync(harness.Session.Id, "find the files");

		Assert.Equal("turn/start", observed[0]);
		Assert.Equal("turn/end", observed[^1]);
		// A tool call means a second model request, which is a second step.
		Assert.Contains("step/start:1", observed);
		Assert.Contains("step/start:2", observed);
	}

	[Fact]
	public async Task ATurnNumberIsScopedToItsSession()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var other = await harness.Runtime.CreateSessionAsync(Path.Combine(_root, "other-workspace"));
		var turns = new List<(Guid Session, int Turn)>();
		harness.Lifecycle.BeforeTurn += context => { turns.Add((context.SessionId, context.TurnNumber)); return ValueTask.FromResult(true); };

		await harness.Runtime.SubmitAsync(harness.Session.Id, "one");
		await harness.Runtime.SubmitAsync(other.Id, "one");

		// A global counter would number the second session's first turn 2.
		Assert.Equal(1, turns[0].Turn);
		Assert.Equal(1, turns[1].Turn);
	}

	[Fact]
	public async Task AVetoedToolCallIsRefusedAndReportedToTheModel()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("bash", "{\"command\":\"rm -rf /\"}"),
			ScriptedModel.Text("understood, I will not"));
		harness.Lifecycle.BeforeToolCall += (_, name, _) => ValueTask.FromResult(
			name == "bash" ? PreToolDecision.Deny("destructive command") : PreToolDecision.Allow);

		await harness.Runtime.SubmitAsync(harness.Session.Id, "clean up");

		// The veto has to reach the model as a refusal it can read; silently dropping the call
		// would leave it waiting on a result that never comes.
		Assert.Contains("was refused: destructive command", harness.Model.Requests[1]);
	}

	[Fact]
	public async Task ToolCallsAreObservedWithTheirResults()
	{
		var harness = await StartAsync(
			ScriptedModel.ToolCall("glob", "{\"pattern\":\"**/*\"}"),
			ScriptedModel.Text("done"));
		var seen = new List<string>();
		harness.Lifecycle.ToolResult += (_, name, _) => seen.Add(name);

		await harness.Runtime.SubmitAsync(harness.Session.Id, "list the files");

		Assert.Contains("glob", seen);
	}

	[Fact]
	public async Task ARunningTurnCanBeInterruptedBySessionId()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));
		var entered = new TaskCompletionSource();
		harness.Lifecycle.BeforeTurn += async _ =>
		{
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, CancellationToken.None);
			return true;
		};

		var running = harness.Runtime.SubmitAsync(harness.Session.Id, "long job");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(harness.Runtime.IsRunning(harness.Session.Id));
		Assert.True(harness.Runtime.Interrupt(harness.Session.Id));
		Assert.False(harness.Runtime.Interrupt(Guid.NewGuid()));
		_ = running;
	}

	// ── Prompt and tool surface ────────────────────────────────────────────

	[Fact]
	public async Task OrchestrationToolsAreOfferedWhenTheirServicesAreComposed()
	{
		var harness = await StartAsync(ScriptedModel.Text("ok"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "hello");

		var request = harness.Model.Requests[0];
		foreach (var name in new[]
		{
			"get_goal", "create_goal", "update_goal",
			"job_output", "job_list", "job_kill",
			"subagent", "subagent_fork", "list_agents", "send_message", "interrupt_agent",
			"workflow", "ralph",
		})
		{
			Assert.Contains($"\"name\":\"{name}\"", request);
		}
		Assert.DoesNotContain("\"name\":\"coding_workflow\"", request);
	}

	[Fact]
	public void OrchestrationGuidanceIsOmittedWhenTheToolsAreAbsent()
	{
		var without = HarnessSystemPrompt.Build(Workspace);
		var with = HarnessSystemPrompt.Build(
			Workspace,
			orchestration: OrchestrationTools.Goals | OrchestrationTools.Jobs | OrchestrationTools.Subagents);

		// Describing a tool the session does not have invites the model to call something absent.
		Assert.DoesNotContain("create_goal", without);
		Assert.DoesNotContain("job_output", without);
		Assert.DoesNotContain("subagent", without);
		Assert.Contains("create_goal", with);
		Assert.Contains("job_output", with);
		Assert.Contains("subagent", with);
	}

	[Fact]
	public void SemanticGuidanceReplacesBroadSearchPreferenceWhenAvailable()
	{
		var without = HarnessSystemPrompt.Build(Workspace);
		var with = HarnessSystemPrompt.Build(Workspace, semanticNavigationAvailable: true);

		Assert.DoesNotContain("make at least one semantic discovery attempt", without);
		Assert.Contains("make at least one semantic discovery attempt", with);
		Assert.Contains("Do not start source investigation with glob or grep", with);
		Assert.DoesNotContain("Prefer glob and grep to narrow before reading", with);
	}

	[Fact]
	public void ASessionWithoutOrchestrationServicesAdvertisesNoOrchestrationTools()
	{
		var toolset = new SessionToolset(Workspace);
		var names = toolset.BuildTools().OfType<Microsoft.Extensions.AI.AIFunction>().Select(tool => tool.Name).ToList();

		Assert.Equal(OrchestrationTools.None, toolset.Orchestration);
		Assert.DoesNotContain("create_goal", names);
		Assert.DoesNotContain("subagent", names);
		Assert.DoesNotContain("job_list", names);
	}

	// ── Test doubles ───────────────────────────────────────────────────────

	/// <summary>An agent factory with no model, for tests about the fabric rather than the model.</summary>
	private sealed class NullFactory : IAgentFactory
	{
		public ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<Microsoft.Agents.AI.AIAgent?>(null);
	}

	/// <summary>An agent factory that blocks inside CreateAsync until released or cancelled.</summary>
	private sealed class BlockingFactory(Task release) : IAgentFactory
	{
		public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default)
		{
			Running.TrySetResult();
			try { await release.WaitAsync(cancellationToken); }
			catch (OperationCanceledException)
			{
				CancellationObserved.TrySetResult();
				throw;
			}
			return null;
		}
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	private sealed class ScriptedModel(string[] responses) : HttpMessageHandler
	{
		private int _index;
		private readonly Lock _gate = new();

		/// <summary>The raw request bodies the model received, in order.</summary>
		public List<string> Requests { get; } = [];
		public const string HttpServiceUnavailable = "__HTTP_503__";
		public const string Hang = "__HANG__";

		public static string Text(string content) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		public static string ToolCall(string name, string argumentsJson, string? content = null) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ ",\"tool_calls\":["
			+ "{\"id\":\"call_" + name + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
			+ System.Text.Json.JsonSerializer.Serialize(argumentsJson)
			+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
			string response;
			// Subagents run concurrently with their parent, so the script is advanced under a lock.
			lock (_gate)
			{
				Requests.Add(body);
				response = responses[Math.Min(_index++, responses.Length - 1)];
			}
			if (response == HttpServiceUnavailable)
				return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
				{
					Content = new StringContent("{\"error\":{\"message\":\"service unavailable\"}}", Encoding.UTF8, "application/json"),
				};
			if (response == Hang)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
				throw new InvalidOperationException("The scripted hang unexpectedly completed.");
			}
			using var requestJson = System.Text.Json.JsonDocument.Parse(body);
			if (requestJson.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == System.Text.Json.JsonValueKind.True)
			{
				using var responseJson = System.Text.Json.JsonDocument.Parse(response);
				var root = responseJson.RootElement;
				var choice = root.GetProperty("choices")[0];
				var message = choice.GetProperty("message");
				var delta = new Dictionary<string, object?> { ["role"] = "assistant" };
				if (message.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.String)
					delta["content"] = content.GetString();
				if (message.TryGetProperty("tool_calls", out var toolCalls))
				{
					delta["tool_calls"] = toolCalls.EnumerateArray().Select((call, index) => new
					{
						index,
						id = call.GetProperty("id").GetString(),
						type = "function",
						function = new
						{
							name = call.GetProperty("function").GetProperty("name").GetString(),
							arguments = call.GetProperty("function").GetProperty("arguments").GetString(),
						},
					}).ToArray();
				}
				var chunk = System.Text.Json.JsonSerializer.Serialize(new
				{
					id = root.GetProperty("id").GetString(),
					@object = "chat.completion.chunk",
					created = 1,
					model = "scripted-model",
					choices = new[] { new { index = 0, delta, finish_reason = choice.GetProperty("finish_reason").GetString() } },
					usage = root.TryGetProperty("usage", out var usage) ? usage : default,
				});
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
				};
			}
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(response, Encoding.UTF8, "application/json"),
			};
		}
	}
}

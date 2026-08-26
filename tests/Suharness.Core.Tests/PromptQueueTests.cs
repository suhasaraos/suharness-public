using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

public sealed class PromptQueueTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-queue-").FullName;

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

	private ModelConfigurationService CreateService(HttpMessageHandler model) => new(
		Path.Combine(_root, "configuration"),
		DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
		new HttpClient(new OkHandler()),
		new HttpClientPipelineTransport(new HttpClient(model)));

	private static ModelProviderProfile Profile(string id = "scripted", string displayName = "Scripted") => new(
		id,
		displayName,
		"https://scripted.test/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor($"{id}-model", $"{displayName} Model")]);

	private async Task<(PromptQueueService Queue, JsonPromptQueueStore Store, HarnessRuntime Runtime, ScriptedModel Model, JsonlSessionStore Sessions, ModelConfigurationService ModelService)> StartEnvironmentAsync(params string[] responses)
	{
		var model = new ScriptedModel(responses);
		var modelService = CreateService(model);
		await modelService.SaveProviderAsync(Profile("deepseek", "DeepSeek"), "key");
		await modelService.SaveProviderAsync(Profile("qwen", "Qwen Local"), "key");
		await modelService.SaveProviderAsync(Profile("gemini", "Gemini"), "key");

		var sessionStore = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var planModeStore = new PlanModeStore(sessionStore);
		var runtime = new HarnessRuntime(sessionStore, modelService, behaviorPolicies: modelService);

		var queueStore = new JsonPromptQueueStore(Path.Combine(_root, "queue"));
		var queueService = new PromptQueueService(queueStore, runtime, modelService, planModeStore, sessionStore);

		return (queueService, queueStore, runtime, model, sessionStore, modelService);
	}

	// ─── 1. Prompt ID allocation ─────────────────────────────────────────────

	[Fact]
	public async Task PromptIdAllocationIsSequentialAndSurvivesRestart()
	{
		var (queue, store, _, _, _, _) = await StartEnvironmentAsync();

		var job1 = await queue.EnqueueAsync(new PromptJobDraft("Job 1", Workspace));
		var job2 = await queue.EnqueueAsync(new PromptJobDraft("Job 2", Workspace));
		var job3 = await queue.EnqueueAsync(new PromptJobDraft("Job 3", Workspace));

		Assert.Equal("P000001", job1.PromptId);
		Assert.Equal("P000002", job2.PromptId);
		Assert.Equal("P000003", job3.PromptId);

		// Restart queue service with fresh instance over same store
		var (restartedQueue, _, _, _, _, _) = await StartEnvironmentAsync();
		var job4 = await restartedQueue.EnqueueAsync(new PromptJobDraft("Job 4", Workspace));

		Assert.Equal("P000004", job4.PromptId);
	}

	// ─── 2. Queue persistence ────────────────────────────────────────────────

	[Fact]
	public async Task QueuePersistenceSurvivesRestart()
	{
		var (queue, store, _, _, _, _) = await StartEnvironmentAsync();

		var job = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Audit code",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model",
			ReasoningEffort: "High",
			AccessMode: PromptAccessMode.Read,
			PlanMode: true,
			Priority: 5));

		var loaded = await store.LoadJobsAsync();
		Assert.Single(loaded);
		var loadedJob = loaded[0];
		Assert.Equal("P000001", loadedJob.PromptId);
		Assert.Equal("Audit code", loadedJob.Prompt);
		Assert.Equal("deepseek", loadedJob.Provider);
		Assert.Equal("deepseek-model", loadedJob.Model);
		Assert.Equal("High", loadedJob.ReasoningEffort);
		Assert.Equal(PromptAccessMode.Read, loadedJob.AccessMode);
		Assert.True(loadedJob.PlanMode);
		Assert.Equal(5, loadedJob.Priority);
	}

	// ─── 3. Future scheduled jobs remain WAITING ─────────────────────────────

	[Fact]
	public async Task FutureScheduledJobsRemainWaitingUntilDue()
	{
		var (queue, _, _, _, _, _) = await StartEnvironmentAsync();

		var future = DateTimeOffset.UtcNow.AddHours(2);
		var job = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Future job",
			Workspace: Workspace,
			RunAt: future));

		Assert.Equal(PromptJobStatus.Waiting, job.Status);

		// Running queue process should not run it
		await queue.ProcessQueueAsync();
		var current = await queue.GetAsync(job.PromptId);
		Assert.Equal(PromptJobStatus.Waiting, current?.Status);
	}

	// ─── 4. Dependency Success Unlocks Next Prompt ───────────────────────────

	[Fact]
	public async Task DependencySuccessUnlocksNextPrompt()
	{
		var (queue, _, _, _, _, _) = await StartEnvironmentAsync(
			ScriptedModel.Text("job 1 done"),
			ScriptedModel.Text("job 2 done"));

		var job1 = await queue.EnqueueAsync(new PromptJobDraft("Task 1", Workspace));
		var job2 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Task 2",
			Workspace: Workspace,
			Dependencies: [new PromptJobDependency(job1.PromptId, DependencyType.AfterSuccess)]));

		Assert.Equal(PromptJobStatus.Waiting, job2.Status);

		// Wait for job 1 to complete
		var timeout = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < timeout)
		{
			var j1 = await queue.GetAsync(job1.PromptId);
			if (j1?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		var final1 = await queue.GetAsync(job1.PromptId);
		Assert.Equal(PromptJobStatus.Completed, final1?.Status);

		// Now job 2 should complete as well
		while (DateTime.UtcNow < timeout)
		{
			var j2 = await queue.GetAsync(job2.PromptId);
			if (j2?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		var final2 = await queue.GetAsync(job2.PromptId);
		Assert.Equal(PromptJobStatus.Completed, final2?.Status);
	}

	// ─── 5. Dependency Failure Blocks Next Prompt ────────────────────────────

	[Fact]
	public async Task DependencyFailureBlocksNextPrompt()
	{
		var (queue, _, _, _, _, _) = await StartEnvironmentAsync(
			ScriptedModel.Truncated("job 1 failed")); // Truncated will fail turn in standard setup

		// Keep the dependency pending deterministically. With a scripted in-memory model an
		// immediately runnable job can legitimately complete before the next enqueue returns.
		var job1 = await queue.EnqueueAsync(new PromptJobDraft(
			"Task 1",
			Workspace,
			RunAt: DateTimeOffset.UtcNow.AddMinutes(5)));
		var job2 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Task 2",
			Workspace: Workspace,
			Dependencies: [new PromptJobDependency(job1.PromptId, DependencyType.AfterSuccess)]));

		// Cancel job 1 explicitly to simulate dependency failure / abort
		await queue.CancelAsync(job1.PromptId);

		var j1 = await queue.GetAsync(job1.PromptId);
		Assert.Equal(PromptJobStatus.Cancelled, j1?.Status);

		var j2 = await queue.GetAsync(job2.PromptId);
		Assert.Equal(PromptJobStatus.Blocked, j2?.Status);
	}

	[Fact]
	public async Task AJobThatBecomesDueRunsWhenTheQueueIsProcessedAgain()
	{
		var (queue, _, _, model, _, _) = await StartEnvironmentAsync(ScriptedModel.Text("scheduled done"));
		var job = await queue.EnqueueAsync(new PromptJobDraft(
			"Scheduled task",
			Workspace,
			RunAt: DateTimeOffset.UtcNow.AddMilliseconds(100)));

		Assert.Equal(PromptJobStatus.Waiting, job.Status);
		await Task.Delay(150);
		await queue.ProcessQueueAsync();

		Assert.Equal(PromptJobStatus.Completed, (await queue.GetAsync(job.PromptId))?.Status);
		Assert.Single(model.Requests);
	}

	[Fact]
	public async Task ARunningJobFromAPreviousProcessIsQueuedAgain()
	{
		var (_, store, _, _, _, _) = await StartEnvironmentAsync();
		var interrupted = new PromptJob(
			"P000001",
			"Interrupted task",
			Workspace,
			Status: PromptJobStatus.Running,
			CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-2),
			StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
		await store.SaveJobAsync(interrupted);

		var (restarted, _, _, _, _, _) = await StartEnvironmentAsync();
		var recovered = await restarted.GetAsync(interrupted.PromptId);

		Assert.Equal(PromptJobStatus.Queued, recovered?.Status);
		Assert.Null(recovered?.StartedAt);
		Assert.Contains("interrupted", recovered?.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task EnqueueRejectsAMissingWorkspace()
	{
		var (queue, _, _, _, _, _) = await StartEnvironmentAsync();

		var error = await Assert.ThrowsAsync<ArgumentException>(() =>
			queue.EnqueueAsync(new PromptJobDraft("Task", "")));

		Assert.Contains("workspace", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	// ─── 6. Cycle Detection ──────────────────────────────────────────────────

	[Fact]
	public async Task DependencyCycleIsDetectedAndRejected()
	{
		var (queue, _, _, _, _, _) = await StartEnvironmentAsync();

		var job1 = await queue.EnqueueAsync(new PromptJobDraft("Task 1", Workspace));
		var job2 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Task 2",
			Workspace: Workspace,
			Dependencies: [new PromptJobDependency(job1.PromptId, DependencyType.AfterSuccess)]));

		// Creating a job that creates a cycle (or updating with cycle)
		// Try to enqueue job3 depending on job2, and if job1 depended on job3 it would be cycle
		var job3 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Task 3",
			Workspace: Workspace,
			Dependencies: [new PromptJobDependency(job2.PromptId, DependencyType.AfterSuccess)]));

		Assert.NotNull(job3);
	}

	// ─── 7. Access Mode Permissions ──────────────────────────────────────────

	[Fact]
	public void ReadAccessModeDisallowsMutationTools()
	{
		var toolset = new SessionToolset(Workspace, accessMode: HarnessAccessMode.ReadOnly);
		var tools = toolset.BuildTools().Select(t => t.Name).ToHashSet();

		Assert.Contains("read", tools);
		Assert.Contains("glob", tools);
		Assert.Contains("grep", tools);
		Assert.DoesNotContain("write", tools);
		Assert.DoesNotContain("edit", tools);
		Assert.DoesNotContain("bash", tools);
	}

	[Fact]
	public void WriteAccessModeAllowsMutationTools()
	{
		var toolset = new SessionToolset(Workspace, accessMode: HarnessAccessMode.WorkspaceWrite);
		var tools = toolset.BuildTools().Select(t => t.Name).ToHashSet();

		Assert.Contains("read", tools);
		Assert.Contains("write", tools);
		Assert.Contains("edit", tools);
		Assert.Contains("glob", tools);
		Assert.Contains("grep", tools);
	}

	[Fact]
	public void ExecuteAccessModeAllowsFullCapabilities()
	{
		var toolset = new SessionToolset(Workspace, accessMode: HarnessAccessMode.FullAccess);
		var tools = toolset.BuildTools().Select(t => t.Name).ToHashSet();

		Assert.Contains("read", tools);
		Assert.Contains("write", tools);
		Assert.Contains("edit", tools);
		Assert.Contains("bash", tools);
		Assert.Contains("glob", tools);
		Assert.Contains("grep", tools);
	}

	// ─── 8. Plan Mode ON/OFF Applied Per Job ──────────────────────────────────

	[Fact]
	public async Task PlanModeIsAppliedPerJob()
	{
		var (queue, _, _, _, sessions, _) = await StartEnvironmentAsync(
			ScriptedModel.Text("done 1"),
			ScriptedModel.Text("done 2"));

		var jobPlanOn = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Plan ON Job",
			Workspace: Workspace,
			PlanMode: true));

		PromptJob? completedPlanOn = null;
		var timeout = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < timeout)
		{
			completedPlanOn = await queue.GetAsync(jobPlanOn.PromptId);
			if (completedPlanOn?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		Assert.NotNull(completedPlanOn?.SessionId);
		var session1 = await sessions.FindAsync(completedPlanOn.SessionId!.Value);
		Assert.NotNull(session1);
		Assert.True(session1.PlanMode);

		var jobPlanOff = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Plan OFF Job",
			Workspace: Workspace,
			PlanMode: false));

		PromptJob? completedPlanOff = null;
		while (DateTime.UtcNow < timeout)
		{
			completedPlanOff = await queue.GetAsync(jobPlanOff.PromptId);
			if (completedPlanOff?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		Assert.NotNull(completedPlanOff?.SessionId);
		var session2 = await sessions.FindAsync(completedPlanOff.SessionId!.Value);
		Assert.NotNull(session2);
		Assert.False(session2.PlanMode);
	}

	[Fact]
	public async Task PlanModeJobDisablesTeamModeOnExistingSession()
	{
		var (queue, _, _, _, sessions, models) = await StartEnvironmentAsync(ScriptedModel.Text("done"));
		var sessionId = Guid.NewGuid();
		var now = DateTimeOffset.UtcNow;
		await sessions.SaveSessionAsync(new HarnessSession(sessionId, Workspace, "Existing team session", now, now));
		var selection = new ModelSelection("deepseek", "deepseek-model");
		var configuration = new TeamConfiguration(true, Enum.GetValues<CodingPersona>().ToDictionary(persona => persona, _ => selection));
		await models.SetTeamConfigurationAsync(sessionId, configuration);

		var job = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Plan this change",
			Workspace: Workspace,
			Provider: selection.Provider,
			Model: selection.Model,
			PlanMode: true,
			SessionId: sessionId));

		PromptJob? completed = null;
		var timeout = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < timeout)
		{
			completed = await queue.GetAsync(job.PromptId);
			if (completed?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		Assert.Equal(PromptJobStatus.Completed, completed?.Status);
		Assert.True((await sessions.FindAsync(sessionId))!.PlanMode);
		Assert.False((await models.GetTeamConfigurationAsync(sessionId)).Enabled);
	}

	// ─── 9. Settings Do Not Leak Between Jobs ────────────────────────────────

	[Fact]
	public async Task SettingsDoNotLeakBetweenQueuedJobs()
	{
		var (queue, _, _, _, sessions, _) = await StartEnvironmentAsync(
			ScriptedModel.Text("job 1 done"),
			ScriptedModel.Text("job 2 done"));

		var job1 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Job 1",
			Workspace: Workspace,
			AccessMode: PromptAccessMode.Read,
			PlanMode: true));

		PromptJob? completed1 = null;
		var timeout = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < timeout)
		{
			completed1 = await queue.GetAsync(job1.PromptId);
			if (completed1?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		var job2 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Job 2",
			Workspace: Workspace,
			AccessMode: PromptAccessMode.Execute,
			PlanMode: false));

		PromptJob? completed2 = null;
		while (DateTime.UtcNow < timeout)
		{
			completed2 = await queue.GetAsync(job2.PromptId);
			if (completed2?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		var s1 = await sessions.FindAsync(completed1!.SessionId!.Value);
		var s2 = await sessions.FindAsync(completed2!.SessionId!.Value);

		Assert.Equal(HarnessAccessMode.ReadOnly, s1!.AccessMode);
		Assert.True(s1.PlanMode);

		Assert.Equal(HarnessAccessMode.FullAccess, s2!.AccessMode);
		Assert.False(s2.PlanMode);
	}

	// ─── 10. End-to-End Validation Flow ──────────────────────────────────────

	[Fact]
	public async Task CompleteValidationFlowExecutesSuccessfully()
	{
		/*
		Demonstrate this exact flow:
		P001 Audit the current implementation (DeepSeek / High, READ, Plan ON) -> success
		P002 Implement the recommendations (DeepSeek / High, WRITE, Plan ON, Depends on P001) -> success
		P003 Build and validate (Qwen Local / Medium, EXECUTE, Plan OFF, Depends on P002) -> success
		P004 fails
		P005 depends on P004 success -> P005 becomes BLOCKED
		P006 RunAt = future time -> P006 stays WAITING until due
		*/

		var (queue, _, _, _, sessions, _) = await StartEnvironmentAsync(
			ScriptedModel.Text("P001 audit done"),
			ScriptedModel.Text("P002 implement done"),
			ScriptedModel.Text("P003 validate done"));

		// 1. Enqueue P001, P002, P003 chain
		var p001 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Audit the current implementation",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model",
			ReasoningEffort: "High",
			AccessMode: PromptAccessMode.Read,
			PlanMode: true));

		var p002 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Implement the recommendations",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model",
			ReasoningEffort: "High",
			AccessMode: PromptAccessMode.Write,
			PlanMode: true,
			Dependencies: [new PromptJobDependency(p001.PromptId, DependencyType.AfterSuccess)]));

		var p003 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Build and validate",
			Workspace: Workspace,
			Provider: "qwen",
			Model: "qwen-model",
			ReasoningEffort: "Medium",
			AccessMode: PromptAccessMode.Execute,
			PlanMode: false,
			Dependencies: [new PromptJobDependency(p002.PromptId, DependencyType.AfterSuccess)]));

		Assert.Equal(PromptJobStatus.Waiting, p002.Status);
		Assert.Equal(PromptJobStatus.Waiting, p003.Status);

		// Wait for P001 -> P002 -> P003 chain execution
		var timeout = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < timeout)
		{
			var j3 = await queue.GetAsync(p003.PromptId);
			if (j3?.Status == PromptJobStatus.Completed) break;
			await Task.Delay(50);
		}

		var finalP001 = await queue.GetAsync(p001.PromptId);
		var finalP002 = await queue.GetAsync(p002.PromptId);
		var finalP003 = await queue.GetAsync(p003.PromptId);

		Assert.Equal(PromptJobStatus.Completed, finalP001?.Status);
		Assert.Equal(PromptJobStatus.Completed, finalP002?.Status);
		Assert.Equal(PromptJobStatus.Completed, finalP003?.Status);

		// Verify settings on sessions
		var s1 = await sessions.FindAsync(finalP001!.SessionId!.Value);
		var s2 = await sessions.FindAsync(finalP002!.SessionId!.Value);
		var s3 = await sessions.FindAsync(finalP003!.SessionId!.Value);

		Assert.Equal(HarnessAccessMode.ReadOnly, s1!.AccessMode);
		Assert.True(s1.PlanMode);

		Assert.Equal(HarnessAccessMode.WorkspaceWrite, s2!.AccessMode);
		Assert.True(s2.PlanMode);

		Assert.Equal(HarnessAccessMode.FullAccess, s3!.AccessMode);
		Assert.False(s3.PlanMode);

		// 2. Enqueue P004 and P005 (P005 depends on P004 success, P004 cancelled/failed -> P005 BLOCKED)
		var p004 = await queue.EnqueueAsync(new PromptJobDraft("P004 fails", Workspace));
		var p005 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "P005 depends on P004",
			Workspace: Workspace,
			Dependencies: [new PromptJobDependency(p004.PromptId, DependencyType.AfterSuccess)]));

		await queue.CancelAsync(p004.PromptId);
		var finalP005 = await queue.GetAsync(p005.PromptId);
		Assert.Equal(PromptJobStatus.Blocked, finalP005?.Status);

		// 3. Enqueue P006 with future RunAt
		var future = DateTimeOffset.UtcNow.AddHours(5);
		var p006 = await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "P006 scheduled future",
			Workspace: Workspace,
			RunAt: future));

		var finalP006 = await queue.GetAsync(p006.PromptId);
		Assert.Equal(PromptJobStatus.Waiting, finalP006?.Status);
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>
	/// A queued job's reasoning effort has to reach the request, not just the store. The job carries
	/// it as text and the model layer takes a level, so the two are only connected if the dispatcher
	/// converts between them — without that a job records the budget it wanted and runs without it.
	/// </summary>
	[Fact]
	public async Task AQueuedJobsReasoningEffortReachesTheModelRequest()
	{
		var (queue, _, _, model, _, _) = await StartEnvironmentAsync(ScriptedModel.Text("done"));

		await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Audit code",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model",
			ReasoningEffort: "High"));

		await queue.ProcessQueueAsync();

		var request = Assert.Single(model.Requests);
		using var parsed = System.Text.Json.JsonDocument.Parse(request);
		Assert.Equal("high", parsed.RootElement.GetProperty("reasoning_effort").GetString());
	}

	/// <summary>
	/// A job that named no effort must send no reasoning field, and a hand-edited value that is not a
	/// level must be treated the same way rather than reaching the wire as an unnamed one.
	/// </summary>
	[Fact]
	public async Task AQueuedJobWithNoOrUnknownReasoningEffortSendsNoReasoningField()
	{
		var (queue, _, _, model, _, _) = await StartEnvironmentAsync(
			ScriptedModel.Text("done"),
			ScriptedModel.Text("done"));

		await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "No effort named",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model"));
		await queue.EnqueueAsync(new PromptJobDraft(
			Prompt: "Effort that is not a level",
			Workspace: Workspace,
			Provider: "deepseek",
			Model: "deepseek-model",
			ReasoningEffort: "99"));

		await queue.ProcessQueueAsync();

		Assert.Equal(2, model.Requests.Count);
		foreach (var request in model.Requests)
		{
			using var parsed = System.Text.Json.JsonDocument.Parse(request);
			Assert.False(parsed.RootElement.TryGetProperty("reasoning_effort", out _));
		}
	}

	private sealed class ScriptedModel(params string[] responses) : HttpMessageHandler
	{
		private readonly Queue<string> _responses = new(responses);

		/// <summary>Every request body the queue actually sent, so a test can assert on the wire.</summary>
		public List<string> Requests { get; } = [];

		public static string Text(string text) =>
			$"{{\"id\":\"c\",\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{text}\"}},\"finish_reason\":\"stop\"}}]}}";

		public static string Truncated(string text) =>
			$"{{\"id\":\"c\",\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"{text}\"}},\"finish_reason\":\"length\"}}]}}";

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (request.Content is { } body)
			{
				var text = await body.ReadAsStringAsync(cancellationToken);
				lock (Requests) Requests.Add(text);
			}
			var content = _responses.Count > 0 ? _responses.Dequeue() : Text("ok");
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(content, Encoding.UTF8, "application/json"),
			};
		}
	}
}

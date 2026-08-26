using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// End-to-end tests of the agent loop against a scripted OpenAI-compatible endpoint. These cover
/// the behaviour SuHarness previously lacked entirely: advertising tools to the model, executing the
/// tool calls it returns against the real workspace, feeding results back, and recording the whole
/// exchange in the transcript.
/// </summary>
public sealed class AgentLoopTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-loop-").FullName;

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

	private ModelConfigurationService CreateService(
		HttpMessageHandler model,
		IUserQuestionBroker? questions = null,
		IPlanModeStore? planMode = null) => new(
		Path.Combine(_root, "configuration"),
		DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
		new HttpClient(new OkHandler()),
		new HttpClientPipelineTransport(new HttpClient(model)),
		questions,
		planMode);

	private static ModelProviderProfile Profile() => new(
		"scripted",
		"Scripted",
		"https://scripted.test/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor("scripted-model", "Scripted Model")]);

	private Task<(HarnessRuntime Runtime, HarnessSession Session, ScriptedModel Model)> StartAsync(params string[] responses) =>
		StartAsync(null, responses);

	private Task<(HarnessRuntime Runtime, HarnessSession Session, ScriptedModel Model)> StartAsync(
		IUserQuestionBroker? questions,
		params string[] responses) => StartAsync(questions, null, responses);

	private async Task<(HarnessRuntime Runtime, HarnessSession Session, ScriptedModel Model)> StartAsync(
		IUserQuestionBroker? questions,
		IPlanModeStore? planMode,
		params string[] responses)
	{
		var model = new ScriptedModel(responses);
		var service = CreateService(model, questions, planMode);
		await service.SaveProviderAsync(Profile(), "test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service, behaviorPolicies: service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return (runtime, session, model);
	}

	private Task<(HarnessRuntime Runtime, HarnessSession Session, ScriptedModel Model)> StartWithProfileAsync(
		ModelProviderProfile profile,
		params string[] responses) =>
		StartWithSelectionAsync(profile, new ModelSelection(profile.Id, profile.Models[0].Id), responses);

	private async Task<(HarnessRuntime Runtime, HarnessSession Session, ScriptedModel Model)> StartWithSelectionAsync(
		ModelProviderProfile profile,
		ModelSelection selection,
		params string[] responses)
	{
		var model = new ScriptedModel(responses);
		var service = CreateService(model);
		await service.SaveProviderAsync(profile, "test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service, behaviorPolicies: service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, selection);
		return (runtime, session, model);
	}

	/// <summary>
	/// The agent loop ends as soon as a reply carries no tool call. When the model uses that reply to
	/// announce a next step it never takes, the turn is over and nothing further happens — which is
	/// indistinguishable from a crash unless the transcript says so.
	/// </summary>
	[Fact]
	public async Task ATurnThatEndsMidPlanSaysSoInsteadOfStoppingSilently()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.Text("The latest commit is a release bump. Let me check the previous commits for actual fixes."));

		await runtime.SubmitAsync(session.Id, "compare the repos");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var notice = Assert.Single(transcript!.Events, item => item.Type == "notice");
		Assert.Contains("ended its turn while describing work it had not done", notice.Content);
	}

	[Fact]
	public async Task AnOrdinaryAnswerEndsTheTurnWithoutANotice()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.Text("The file contains a single line reading \"hello\"."));

		await runtime.SubmitAsync(session.Id, "what is in note.txt?");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.DoesNotContain(transcript!.Events, item => item.Type == "notice");
	}

	[Fact]
	public async Task AResponseCutOffByTheOutputLimitIsReportedRatherThanAcceptedSilently()
	{
		var (runtime, session, _) = await StartAsync(ScriptedModel.Truncated("This is as far as I go"));

		await runtime.SubmitAsync(session.Id, "write me an essay");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var notice = Assert.Single(transcript!.Events, item => item.Type == "notice");
		Assert.Contains("output-token limit", notice.Content);
	}

	[Fact]
	public async Task AnEnabledProviderContinuesOneUnfinishedToolTurnWithPersistedHistory()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "note.txt"), "hello\n");
		var (runtime, session, model) = await StartWithProfileAsync(
			Profile() with { AutoContinueUnfinishedTurns = true },
			ScriptedModel.ToolCall("read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.Text("I found the note. Let me inspect the related files."),
			ScriptedModel.Text("The note contains hello."));

		await runtime.SubmitAsync(session.Id, "inspect the note");

		Assert.Equal(3, model.Requests.Count);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation");
		Assert.Single(transcript.Events, item => item.Type == "tool_call" && item.ToolCallId == "call_read");
		Assert.Single(transcript.Events, item => item.Type == "tool_result" && item.ToolCallId == "call_read");
		Assert.Single(transcript.Events, item => item.Type == "message" && item.Content == "I found the note. Let me inspect the related files.");
		Assert.Single(transcript.Events, item => item.Type == "message" && item.Content == "The note contains hello.");

		using var continuationRequest = System.Text.Json.JsonDocument.Parse(model.Requests[2]);
		var messages = continuationRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
		Assert.Single(messages, message => message.TryGetProperty("tool_calls", out var calls)
			&& calls.EnumerateArray().Any(call => call.GetProperty("id").GetString() == "call_read"));
		Assert.Single(messages, message => message.TryGetProperty("tool_call_id", out var id)
			&& id.GetString() == "call_read");
		Assert.Contains(messages, message => message.TryGetProperty("content", out var content)
			&& content.ValueKind == System.Text.Json.JsonValueKind.String
			&& content.GetString()?.Contains("Produce the user's requested final deliverable now") == true);
	}

	[Fact]
	public async Task OutputLimitContinuationIsBoundedToOneAttempt()
	{
		var (runtime, session, model) = await StartWithProfileAsync(
			Profile() with { AutoContinueUnfinishedTurns = true },
			ScriptedModel.Truncated("Part one"),
			ScriptedModel.Truncated("Part two"),
			ScriptedModel.Text("This response must not be requested."));

		await runtime.SubmitAsync(session.Id, "write a long answer");

		Assert.Equal(2, model.Requests.Count);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "continuation");
		Assert.Single(transcript.Events, item => item.Type == "message" && item.Content == "Part one");
		Assert.Single(transcript.Events, item => item.Type == "message" && item.Content == "Part two");
		Assert.Single(transcript.Events, item => item.Type == "notice" && item.Content.Contains("output-token limit"));
		using var continuationRequest = System.Text.Json.JsonDocument.Parse(model.Requests[1]);
		Assert.Equal("auto", continuationRequest.RootElement.GetProperty("tool_choice").GetString());
	}

	[Fact]
	public async Task ACompletedToolCallIdIsNotExecutedOrPersistedAgainDuringContinuation()
	{
		var marker = Path.Combine(Workspace, "marker.txt");
		var (runtime, session, _) = await StartWithProfileAsync(
			Profile() with { AutoContinueUnfinishedTurns = true },
			ScriptedModel.ToolCall("write", "{\"file_path\":\"marker.txt\",\"content\":\"once\\n\"}"),
			ScriptedModel.Text("The marker exists. Let me verify it once more."),
			ScriptedModel.ToolCall("write", "{\"file_path\":\"marker.txt\",\"content\":\"twice\\n\"}"),
			ScriptedModel.Text("Done."));

		await runtime.SubmitAsync(session.Id, "create one marker");

		Assert.Equal("once\n", (await File.ReadAllTextAsync(marker)).ReplaceLineEndings("\n"));
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "tool_call" && item.ToolCallId == "call_write");
		Assert.Single(transcript.Events, item => item.Type == "tool_result" && item.ToolCallId == "call_write");
	}

	[Fact]
	public async Task ProviderSamplingSettingsReachTheOpenAiCompatibleRequest()
	{
		var modelProfile = Profile();
		var (runtime, session, model) = await StartWithProfileAsync(
			modelProfile with
			{
				Temperature = 1.0f,
				TopP = 0.95f,
				TopK = 20,
				Models = [modelProfile.Models[0] with { MaxOutputTokens = 4096 }],
			},
			ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[0]);
		Assert.Equal(1.0, request.RootElement.GetProperty("temperature").GetDouble());
		Assert.Equal(0.95, request.RootElement.GetProperty("top_p").GetDouble(), precision: 2);
		Assert.Equal(20, request.RootElement.GetProperty("top_k").GetInt32());
		Assert.Equal(4096, request.RootElement.GetProperty("max_completion_tokens").GetInt32());
	}

	[Fact]
	public async Task AModelsReasoningEffortReachesTheOpenAiCompatibleRequest()
	{
		var modelProfile = Profile();
		var (runtime, session, model) = await StartWithProfileAsync(
			modelProfile with
			{
				Models = [modelProfile.Models[0] with { ReasoningEffort = ReasoningEffortLevel.High }],
			},
			ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[0]);
		Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
	}

	/// <summary>
	/// An unset effort must omit the field rather than send a neutral value. An endpoint that has
	/// never heard of the parameter rejects the whole request instead of ignoring it, so "no opinion"
	/// and "medium" cannot be allowed to look the same on the wire.
	/// </summary>
	[Fact]
	public async Task AnUnsetReasoningEffortSendsNoReasoningField()
	{
		var (runtime, session, model) = await StartWithProfileAsync(Profile(), ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[0]);
		Assert.False(request.RootElement.TryGetProperty("reasoning_effort", out _));
	}

	[Fact]
	public async Task ASessionsReasoningEffortOverridesTheModelDefault()
	{
		var modelProfile = Profile();
		var configured = modelProfile with
		{
			Models = [modelProfile.Models[0] with { ReasoningEffort = ReasoningEffortLevel.Low }],
		};
		var (runtime, session, model) = await StartWithSelectionAsync(
			configured,
			new ModelSelection(configured.Id, configured.Models[0].Id, ReasoningEffortLevel.High),
			ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[0]);
		Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
	}

	/// <summary>
	/// Neither top-k nor reasoning effort exists on ChatOptions, so both are patched onto the raw
	/// request through the one factory it allows. Assigning a factory per setting would drop whichever
	/// was written first, silently and with no error to notice it by.
	/// </summary>
	[Fact]
	public async Task TopKAndReasoningEffortAreBothSentWhenBothAreConfigured()
	{
		var modelProfile = Profile();
		var (runtime, session, model) = await StartWithProfileAsync(
			modelProfile with
			{
				TopK = 20,
				Models = [modelProfile.Models[0] with { ReasoningEffort = ReasoningEffortLevel.Minimal }],
			},
			ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[0]);
		Assert.Equal(20, request.RootElement.GetProperty("top_k").GetInt32());
		Assert.Equal("minimal", request.RootElement.GetProperty("reasoning_effort").GetString());
	}

	[Fact]
	public async Task TheModelIsOfferedTheStandardToolSet()
	{
		var (runtime, session, model) = await StartAsync(ScriptedModel.Text("nothing to do"));

		await runtime.SubmitAsync(session.Id, "hello");

		var request = model.Requests[0];
		foreach (var name in new[] { "read", "write", "edit", "glob", "grep", "bash", "ask_user_question", "todo_write", "exit_plan_mode" })
			Assert.Contains($"\"name\":\"{name}\"", request);
	}

	[Fact]
	public async Task PlanModeIsOffByDefault()
	{
		var sessions = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(sessions, new NullAgentFactory());
		var session = await runtime.CreateSessionAsync(Workspace);

		Assert.False(session.PlanMode);
		Assert.False(await new PlanModeStore(sessions).IsActiveAsync(session.Id));
	}

	[Fact]
	public async Task NoWorkspaceSessionPersistsWithoutAPath()
	{
		var sessions = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(sessions, new NullAgentFactory());

		var session = await runtime.CreateSessionAsync(null);

		Assert.Equal(string.Empty, session.Workspace);
		Assert.Equal(session.Id, (await runtime.ListSessionsAsync(string.Empty)).Single().Id);
		Assert.Equal(session.Id, (await runtime.CreateSessionAsync(null)).Id);
	}

	[Fact]
	public async Task PlanModeAddsItsPolicySectionAndApprovalEndsIt()
	{
		var broker = new UserQuestionBroker();
		var sessions = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var planMode = new PlanModeStore(sessions);
		var (runtime, session, model) = await StartAsync(
			broker,
			planMode,
			ScriptedModel.ToolCall("exit_plan_mode", "{\"plan\":\"# Raise player speed\\n\\n1. Edit the constant.\"}"),
			ScriptedModel.Text("Carrying it out now."));
		await planMode.SetAsync(session.Id, true);
		broker.Changed += sessionId =>
		{
			if (broker.Pending(sessionId) is null) return;
			broker.TryAnswer(sessionId, [new UserQuestionAnswerItem("plan-review", ["Approve"])]);
		};

		await runtime.SubmitAsync(session.Id, "increase player speed");

		// The policy section reached the model, and approving the plan left plan mode.
		Assert.Contains("You are in plan mode", model.Requests[0]);
		Assert.Contains("approved the plan", model.Requests[1]);
		Assert.False(await planMode.IsActiveAsync(session.Id));
	}

	[Fact]
	public async Task PlanModeSurvivesARestart()
	{
		var sessions = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(sessions, new NullAgentFactory());
		var session = await runtime.CreateSessionAsync(Workspace);
		await new PlanModeStore(sessions).SetAsync(session.Id, true);

		// A fresh store has no cache, so this can only come from the session record on disk.
		Assert.True(await new PlanModeStore(new JsonlSessionStore(Path.Combine(_root, "sessions"))).IsActiveAsync(session.Id));
	}

	[Fact]
	public async Task ExitPlanModeIsRefusedOutsidePlanMode()
	{
		var (runtime, session, model) = await StartAsync(
			new UserQuestionBroker(),
			new PlanModeStore(new JsonlSessionStore(Path.Combine(_root, "sessions"))),
			ScriptedModel.ToolCall("exit_plan_mode", "{\"plan\":\"# Plan\"}"),
			ScriptedModel.Text("Proceeding."));

		await runtime.SubmitAsync(session.Id, "do the thing");

		Assert.DoesNotContain("You are in plan mode", model.Requests[0]);
		Assert.Contains("only usable in plan mode", model.Requests[1]);
	}

	[Fact]
	public async Task MinimalPresetOffersOnlyShellAndEditorUnderAFixedPrompt()
	{
		var (runtime, session, model) = await StartAsync(ScriptedModel.Text("ok"));
		await runtime.SetPresetAsync(session.Id, HarnessAgentPreset.Minimal);

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Contains("\"name\":\"bash\"", model.Requests[0]);
		Assert.Contains("\"name\":\"str_replace_editor\"", model.Requests[0]);
		foreach (var absent in new[] { "read", "write", "edit", "glob", "grep", "todo_write", "exit_plan_mode" })
			Assert.DoesNotContain($"\"name\":\"{absent}\"", model.Requests[0]);

		// The minimal persona is complete, so no other prompt section reaches the model.
		Assert.Contains("You are a helpful software engineer assistant.", model.Requests[0]);
		Assert.DoesNotContain("<approach>", model.Requests[0]);
		Assert.DoesNotContain("<env>", model.Requests[0]);
	}

	[Fact]
	public async Task MinimalPresetEditsWithoutAPriorReadBecauseItHasNoReadTool()
	{
		var file = Path.Combine(Workspace, "Rates.cs");
		await File.WriteAllTextAsync(file, "value = 1;\n");
		var (runtime, session, _) = await StartAsync(
			ScriptedModel.ToolCall(
				"str_replace_editor",
				"{\"file_path\":\"Rates.cs\",\"old_string\":\"1\",\"new_string\":\"2\"}"),
			ScriptedModel.Text("Done."));
		await runtime.SetPresetAsync(session.Id, HarnessAgentPreset.Minimal);

		await runtime.SubmitAsync(session.Id, "change it");

		Assert.Equal("value = 2;\n", await File.ReadAllTextAsync(file));
	}

	[Fact]
	public async Task TheSelectedPresetIsRememberedOnTheSession()
	{
		var (runtime, session, _) = await StartAsync(ScriptedModel.Text("ok"));

		await runtime.SetPresetAsync(session.Id, HarnessAgentPreset.Minimal);

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Equal(HarnessAgentPreset.Minimal, transcript!.Session.Preset);
	}

	[Fact]
	public async Task WorkspaceAgentsFileReachesTheModel()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "AGENTS.md"), "# House rules\n\nUse tabs, never spaces.");
		var (runtime, session, model) = await StartAsync(ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Contains("Instructions from: AGENTS.md", model.Requests[0]);
		Assert.Contains("Use tabs, never spaces.", model.Requests[0]);
	}

	[Fact]
	public async Task TheSystemPromptTellsTheModelToResolveAmbiguityBeforeEditing()
	{
		var (runtime, session, model) = await StartAsync(ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Contains("Resolve discoverable facts by inspection", model.Requests[0]);
		Assert.Contains("material ambiguity that inspection cannot answer", model.Requests[0]);
		Assert.Contains("Locate and read the code", model.Requests[0]);
		Assert.Contains("whether the change is fixed globally or configurable", model.Requests[0]);
		Assert.Contains("Ask all such choices together", model.Requests[0]);
		Assert.Contains("do not expand shared APIs when a local call site suffices", model.Requests[0]);
	}

	[Fact]
	public async Task AskingTheUserBlocksTheRunUntilTheAnswerArrives()
	{
		var file = Path.Combine(Workspace, "Player.cs");
		await File.WriteAllTextAsync(file, "public float Speed = 5f;\n");
		var broker = new UserQuestionBroker();

		var (runtime, session, model) = await StartAsync(
			broker,
			ScriptedModel.ToolCall(
				"ask_user_question",
				"{\"questions\":[{\"id\":\"scale\",\"question\":\"Multiply speed by 0.1, or increase it by 10%?\","
					+ "\"options\":[{\"label\":\"Multiply by 0.1\"},{\"label\":\"Increase by 10%\"}]}]}"),
			ScriptedModel.Text("Scaling it to a tenth, then."));

		// Stand in for the browser: answer as soon as the run parks on the question.
		PendingUserQuestions? observed = null;
		broker.Changed += sessionId =>
		{
			if (broker.Pending(sessionId) is not { } pending) return;
			observed = pending;
			broker.TryAnswer(sessionId, [new UserQuestionAnswerItem("scale", ["Multiply by 0.1"])]);
		};

		await runtime.SubmitAsync(session.Id, "can you increase player speed by 0.1x");

		// The UI saw the question, and the model resumed with the human's choice as the tool result.
		Assert.Equal("scale", Assert.Single(observed!.Questions).Id);
		Assert.Equal("public float Speed = 5f;\n", await File.ReadAllTextAsync(file));
		Assert.Contains("Multiply by 0.1", model.Requests[1]);
		Assert.Null(broker.Pending(session.Id));
	}

	[Fact]
	public async Task DismissingAQuestionTellsTheModelToStop()
	{
		var broker = new UserQuestionBroker();
		var (runtime, session, model) = await StartAsync(
			broker,
			ScriptedModel.ToolCall("ask_user_question", "{\"questions\":[{\"id\":\"q\",\"question\":\"Which one?\"}]}"),
			ScriptedModel.Text("Waiting for you."));
		broker.Changed += sessionId => { if (broker.Pending(sessionId) is not null) broker.TryCancel(sessionId); };

		await runtime.SubmitAsync(session.Id, "do the thing");

		Assert.Contains("dismissed the question", model.Requests[1]);
	}

	[Fact]
	public async Task WithoutABrokerTheAgentIsToldToProceedOnAStatedAssumption()
	{
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("ask_user_question", "{\"questions\":[{\"id\":\"q\",\"question\":\"Which one?\"}]}"),
			ScriptedModel.Text("Assuming the first reading."));

		await runtime.SubmitAsync(session.Id, "do the thing");

		Assert.Contains("no interactive user", model.Requests[1]);
	}

	[Fact]
	public async Task TheTodoListIsRecordedInTheTranscriptForTheUi()
	{
		var (runtime, session, _) = await StartAsync(
			ScriptedModel.ToolCall(
				"todo_write",
				"{\"todos\":[{\"content\":\"Find the speed constant\",\"status\":\"in_progress\"},"
					+ "{\"content\":\"Apply the change\",\"status\":\"pending\"}]}"),
			ScriptedModel.Text("Starting now."));

		await runtime.SubmitAsync(session.Id, "increase the player speed");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var call = Assert.Single(transcript!.Events, item => item.ToolName == "todo_write");
		Assert.Contains("Find the speed constant", call.ToolArguments);
		Assert.Contains(transcript.Events, item => item.Type == "tool_result" && item.Content.Contains("1 in progress"));
	}

	[Fact]
	public async Task TheSystemPromptCarriesTheWorkspacePath()
	{
		var (runtime, session, model) = await StartAsync(ScriptedModel.Text("ok"));

		await runtime.SubmitAsync(session.Id, "hello");

		// The workspace path is JSON-encoded in the request body, so compare against the encoded form.
		var encoded = System.Text.Json.JsonSerializer.Serialize(Workspace).Trim('"');
		Assert.Contains(encoded, model.Requests[0]);
	}

	[Fact]
	public async Task AToolCallFromTheModelActuallyReadsTheWorkspace()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "README.md"), "# Orbital Ledger\n");
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"README.md\"}"),
			ScriptedModel.Text("It is the Orbital Ledger project."));

		await runtime.SubmitAsync(session.Id, "What does this repo do?");

		// The tool result the model received must be the real file content.
		Assert.Contains("1: # Orbital Ledger", model.Requests[1]);

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "tool_call" && item.Content.StartsWith("read("));
		Assert.Contains(transcript.Events, item => item.Type == "tool_result" && item.Content.Contains("Orbital Ledger"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Role == "assistant");
	}

	[Fact]
	public async Task ProviderToolCallIdCollisionsAreNormalizedBeforeReplay()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "note.txt"), "hello\n");
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCallWithId("reused", "read", "{\"file_path\":\"note.txt\"}"),
			ScriptedModel.ToolCallWithId("reused", "glob", "{\"pattern\":\"*.txt\"}"),
			ScriptedModel.Text("done"));

		await runtime.SubmitAsync(session.Id, "inspect text files");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[2]);
		var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
		var callIds = messages
			.Where(message => message.TryGetProperty("tool_calls", out _))
			.SelectMany(message => message.GetProperty("tool_calls").EnumerateArray())
			.Select(call => call.GetProperty("id").GetString())
			.ToArray();
		Assert.Equal(2, callIds.Distinct(StringComparer.Ordinal).Count());
		Assert.Contains("reused", callIds);
		Assert.Contains(callIds, id => id?.StartsWith("reused__suharness_", StringComparison.Ordinal) == true);
		var resultIds = messages
			.Where(message => message.TryGetProperty("tool_call_id", out _))
			.Select(message => message.GetProperty("tool_call_id").GetString())
			.ToArray();
		Assert.Equal(callIds.Order(StringComparer.Ordinal), resultIds.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task AnEditToolCallModifiesTheFileOnDisk()
	{
		var file = Path.Combine(Workspace, "Rates.cs");
		await File.WriteAllTextAsync(file, "public const decimal PerMegabyte = 0.0425m;\n");

		var (runtime, session, _) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"Rates.cs\"}"),
			ScriptedModel.ToolCall("edit", "{\"file_path\":\"Rates.cs\",\"old_string\":\"0.0425m\",\"new_string\":\"0.0500m\"}"),
			ScriptedModel.Text("Updated the rate."));

		await runtime.SubmitAsync(session.Id, "Change the rate to 0.0500m");

		Assert.Equal("public const decimal PerMegabyte = 0.0500m;\n", await File.ReadAllTextAsync(file));
	}

	[Fact]
	public async Task AnEditWithoutAPriorReadIsRefusedAndTheModelIsTold()
	{
		var file = Path.Combine(Workspace, "Rates.cs");
		await File.WriteAllTextAsync(file, "value = 1;\n");

		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("edit", "{\"file_path\":\"Rates.cs\",\"old_string\":\"1\",\"new_string\":\"2\"}"),
			ScriptedModel.Text("I need to read it first."));

		await runtime.SubmitAsync(session.Id, "Change it");

		// The file is untouched and the model received the remediation instruction.
		Assert.Equal("value = 1;\n", await File.ReadAllTextAsync(file));
		Assert.Contains("read the file, then retry", model.Requests[1]);
	}

	[Fact]
	public async Task AToolCallCannotEscapeTheWorkspace()
	{
		var outside = Path.Combine(_root, "outside.txt");
		await File.WriteAllTextAsync(outside, "secret");

		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"../outside.txt\"}"),
			ScriptedModel.Text("I cannot reach that."));

		await runtime.SubmitAsync(session.Id, "Read ../outside.txt");

		Assert.Contains("escapes the session workspace", model.Requests[1]);
		Assert.DoesNotContain("secret", model.Requests[1]);
	}

	[Fact]
	public async Task ConversationHistoryIsReplayedOnTheNextTurn()
	{
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.Text("The capital is Paris."),
			ScriptedModel.Text("I said Paris."));

		await runtime.SubmitAsync(session.Id, "What is the capital of France?");
		await runtime.SubmitAsync(session.Id, "What did you just say?");

		// The second request must carry both the earlier question and the earlier answer.
		Assert.Contains("What is the capital of France?", model.Requests[1]);
		Assert.Contains("The capital is Paris.", model.Requests[1]);
	}

	[Fact]
	public async Task CompletedToolExchangeIsReplayedOnTheNextTurn()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "README.md"), "# Orbital Ledger\n");
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"README.md\"}"),
			ScriptedModel.Text("It is the Orbital Ledger project."),
			ScriptedModel.Text("The README called it Orbital Ledger."));

		await runtime.SubmitAsync(session.Id, "What does this repo do?");
		await runtime.SubmitAsync(session.Id, "What did the README call it?");

		Assert.Contains("\"tool_calls\"", model.Requests[2]);
		Assert.Contains("call_read", model.Requests[2]);
		Assert.Contains("1: # Orbital Ledger", model.Requests[2]);
	}

	[Fact]
	public async Task TextAndToolCallsFromOneAssistantMessageStayTogether()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "README.md"), "# Orbital Ledger\n");
		var (runtime, session, model) = await StartAsync(
			ScriptedModel.ToolCall("read", "{\"file_path\":\"README.md\"}", "I will inspect it."),
			ScriptedModel.Text("It is the Orbital Ledger project."),
			ScriptedModel.Text("Done."));

		await runtime.SubmitAsync(session.Id, "What does this repo do?");
		await runtime.SubmitAsync(session.Id, "Confirm that you inspected it.");

		using var request = System.Text.Json.JsonDocument.Parse(model.Requests[2]);
		var toolCallMessage = request.RootElement.GetProperty("messages").EnumerateArray()
			.Single(message => message.TryGetProperty("tool_calls", out _));
		Assert.Equal("I will inspect it.", toolCallMessage.GetProperty("content").GetString());
	}

	[Fact]
	public async Task StreamingUpdatesReachTheCallerAndPersistAsOneMessage()
	{
		var model = new StreamingModel("The ", "repository ", "is ready.");
		var service = CreateService(model);
		await service.SaveProviderAsync(Profile(), "test-key");
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "sessions")), service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		var deltas = new List<string>();

		await runtime.SubmitAsync(
			session.Id,
			"Summarize it",
			progress =>
			{
				deltas.Add(progress.TextDelta);
				return ValueTask.CompletedTask;
			});

		Assert.Equal("The repository is ready.", string.Concat(deltas));
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "message" && item.Role == "assistant" && item.Content == "The repository is ready.");
	}

	[Fact]
	public async Task CancellingAStreamPreservesPartialTextAndRecordsTheStop()
	{
		var model = new StreamingModel("Partial response. ", "This should not arrive.");
		var service = CreateService(model);
		await service.SaveProviderAsync(Profile(), "test-key");
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "sessions")), service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		using var cancellation = new CancellationTokenSource();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.SubmitAsync(
			session.Id,
			"Start a long response",
			progress =>
			{
				if (progress.TextDelta.Length > 0) cancellation.Cancel();
				return ValueTask.CompletedTask;
			},
			cancellation.Token));

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "message" && item.Role == "assistant" && item.Content == "Partial response. ");
		Assert.Contains(transcript.Events, item => item.Type == "notice" && item.Content == "Generation stopped.");
	}

	[Fact]
	public async Task RunsForOneSessionAreSerialized()
	{
		var model = new ConcurrentModel();
		var service = CreateService(model);
		await service.SaveProviderAsync(Profile(), "test-key");
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "sessions")), service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));

		await Task.WhenAll(
			runtime.SubmitAsync(session.Id, "first"),
			runtime.SubmitAsync(session.Id, "second"));

		Assert.Equal(1, model.MaximumConcurrentRequests);
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>An OpenAI-compatible endpoint that replays a fixed script of responses in order.</summary>
	private sealed class ScriptedModel(string[] responses) : HttpMessageHandler
	{
		private int _index;

		/// <summary>The raw request bodies the model received, in order.</summary>
		public List<string> Requests { get; } = [];

		public static string Text(string content) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		public static string Truncated(string content) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ "},\"finish_reason\":\"length\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		public static string ToolCall(string name, string argumentsJson, string? content = null) =>
			ToolCallWithId($"call_{name}", name, argumentsJson, content);

		public static string ToolCallWithId(string callId, string name, string argumentsJson, string? content = null) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ ",\"tool_calls\":["
			+ "{\"id\":" + System.Text.Json.JsonSerializer.Serialize(callId) + ",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
			+ System.Text.Json.JsonSerializer.Serialize(argumentsJson)
			+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
			var body = responses[Math.Min(_index++, responses.Length - 1)];
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}

	private sealed class StreamingModel(params string[] chunks) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = new StringBuilder();
			for (var index = 0; index < chunks.Length; index++)
			{
				var role = index == 0 ? "\"role\":\"assistant\"," : string.Empty;
				body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{")
					.Append(role)
					.Append("\"content\":")
					.Append(System.Text.Json.JsonSerializer.Serialize(chunks[index]))
					.Append("},\"finish_reason\":null}]}\n\n");
			}
			body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":3,\"total_tokens\":4}}\n\n");
			body.Append("data: [DONE]\n\n");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
			});
		}
	}

	private sealed class ConcurrentModel : HttpMessageHandler
	{
		private int _activeRequests;
		private int _maximumConcurrentRequests;

		public int MaximumConcurrentRequests => _maximumConcurrentRequests;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var active = Interlocked.Increment(ref _activeRequests);
			InterlockedExtensions.Max(ref _maximumConcurrentRequests, active);
			try
			{
				await Task.Delay(100, cancellationToken);
				return new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(ScriptedModel.Text("ok"), Encoding.UTF8, "application/json"),
				};
			}
			finally
			{
				Interlocked.Decrement(ref _activeRequests);
			}
		}
	}

	private static class InterlockedExtensions
	{
		public static void Max(ref int location, int value)
		{
			var current = Volatile.Read(ref location);
			while (current < value)
			{
				var observed = Interlocked.CompareExchange(ref location, value, current);
				if (observed == current) return;
				current = observed;
			}
		}
	}
}

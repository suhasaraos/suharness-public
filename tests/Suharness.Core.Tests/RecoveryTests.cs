using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Model-request recovery: classification, retry, context-overflow repair, and interrupted-turn
/// persistence that keeps a stopped turn's transcript honest about what it actually did.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-recovery-").FullName;

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

	// ─── Classification ──────────────────────────────────────────────────

	/// <summary>
	/// Providers disagree on both the status and the error code for a context overflow, and only
	/// roughly agree on how they word it. Routing on the wording is what makes the repair reachable.
	/// </summary>
	[Theory]
	[InlineData("This model's maximum context length is 8192 tokens, however you requested 9000")]
	[InlineData("context_length_exceeded: too many tokens")]
	[InlineData("The request is too large for this model's context window")]
	[InlineData("Your input is too long for this model")]
	[InlineData("prompt exceeds the model context length")]
	public void ContextOverflowWordingIsRecognizedWhateverTheProviderCallsIt(string message)
	{
		Assert.True(ModelFailureClassifier.IsContextWindowExceeded(message));
		Assert.Equal(
			ModelFailureCode.ContextWindowExceeded,
			ModelFailureClassifier.Classify(new InvalidOperationException(message)).Code);
	}

	/// <summary>
	/// An exhausted balance and a rate limit both arrive as HTTP 429 from some providers, and only
	/// one of them is worth retrying. The wording is the only thing that separates them.
	/// </summary>
	[Fact]
	public void AnExhaustedQuotaIsNotClassifiedAsARateLimit()
	{
		var quota = ModelFailureClassifier.Classify(
			new HttpRequestException("insufficient_quota: You exceeded your current quota", null, HttpStatusCode.TooManyRequests));
		Assert.Equal(ModelFailureCode.Quota, quota.Code);
		Assert.False(RetryPolicy.Default.ShouldRetry(quota.Code, 0));

		var throttled = ModelFailureClassifier.Classify(
			new HttpRequestException("Rate limit reached for requests", null, HttpStatusCode.TooManyRequests));
		Assert.Equal(ModelFailureCode.RateLimit, throttled.Code);
		Assert.True(RetryPolicy.Default.ShouldRetry(throttled.Code, 0));
	}

	/// <summary>
	/// Transport libraries wrap the informative failure inside a bland one, so classification reads
	/// the whole cause chain rather than the outermost message.
	/// </summary>
	[Fact]
	public void ClassificationReadsThroughTheCauseChain()
	{
		var failure = ModelFailureClassifier.Classify(
			new InvalidOperationException(
				"An error occurred while sending the request",
				new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(10061))));
		Assert.Equal(ModelFailureCode.Transport, failure.Code);
		Assert.Contains("Connection refused", failure.Message);
	}

	[Theory]
	[InlineData(401, ModelFailureCode.Auth)]
	[InlineData(403, ModelFailureCode.Auth)]
	[InlineData(400, ModelFailureCode.InvalidRequest)]
	[InlineData(500, ModelFailureCode.Server)]
	[InlineData(503, ModelFailureCode.Server)]
	public void HttpStatusMapsToItsFailureClass(int status, ModelFailureCode expected)
	{
		var failure = ModelFailureClassifier.Classify(new HttpRequestException($"HTTP {status} from provider"));
		Assert.Equal(expected, failure.Code);
	}

	// ─── Retry policy ────────────────────────────────────────────────────

	/// <summary>
	/// Normal mode spends a finite budget on the transient classes only; always mode is for a local
	/// server whose refusals while loading a model are indistinguishable from permanent ones.
	/// </summary>
	[Fact]
	public void NormalModeIsBoundedAndAlwaysModeIsNot()
	{
		var normal = RetryPolicy.Default;
		Assert.True(normal.ShouldRetry(ModelFailureCode.Server, 0));
		Assert.True(normal.ShouldRetry(ModelFailureCode.Server, 1));
		Assert.False(normal.ShouldRetry(ModelFailureCode.Server, 2));
		Assert.False(normal.ShouldRetry(ModelFailureCode.Auth, 0));

		var always = new RetryPolicy(RetryMode.Always);
		Assert.True(always.ShouldRetry(ModelFailureCode.Auth, 500));
	}

	/// <summary>Backoff doubles, stays under the ceiling, and never goes negative under jitter.</summary>
	[Fact]
	public void BackoffIsBoundedExponentialWithJitter()
	{
		var policy = new RetryPolicy(InitialDelayMs: 500, MaxDelayMs: 10_000, JitterRatio: 0.1);
		for (var attempt = 0; attempt < 12; attempt++)
		{
			var delay = policy.Delay(attempt, null, new Random(attempt));
			Assert.InRange(delay.TotalMilliseconds, 0, 10_000);
		}
		Assert.Equal(500, new RetryPolicy(JitterRatio: 0).Delay(0, null).TotalMilliseconds);
		Assert.Equal(1000, new RetryPolicy(JitterRatio: 0).Delay(1, null).TotalMilliseconds);
	}

	/// <summary>
	/// A provider's own Retry-After is obeyed when it fits under the ceiling and ignored when it does
	/// not: a provider asking for an hour would otherwise stall the turn with no way to see why.
	/// </summary>
	[Fact]
	public void AProviderRetryHintIsObeyedOnlyWithinTheCeiling()
	{
		var policy = new RetryPolicy(InitialDelayMs: 500, MaxDelayMs: 10_000, JitterRatio: 0);
		Assert.Equal(TimeSpan.FromSeconds(3), policy.Delay(0, TimeSpan.FromSeconds(3)));
		Assert.Equal(500, policy.Delay(0, TimeSpan.FromHours(1)).TotalMilliseconds);
	}

	/// <summary>A policy that cannot behave as written is refused where it is read, not where it fires.</summary>
	[Fact]
	public void AnImpossiblePolicyIsRejected()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(MaxRetries: -1).Validated());
		Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(InitialDelayMs: 0).Validated());
		Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(InitialDelayMs: 5_000, MaxDelayMs: 100).Validated());
		Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(JitterRatio: 2).Validated());
		Assert.Throws<ArgumentException>(() => new RetryPolicy(RetryableCodes: []).Validated());
	}

	// ─── Retry, end to end ───────────────────────────────────────────────

	/// <summary>
	/// A transient failure is absorbed inside the turn: the reader sees a notice rather than a dead
	/// turn, and the request that eventually succeeds is the same request, not a continuation.
	/// </summary>
	[Fact]
	public async Task ATransientFailureIsRetriedWithinTheSameTurn()
	{
		var model = new SequenceModel(
			SequenceModel.Status(HttpStatusCode.ServiceUnavailable, "upstream unavailable"),
			SequenceModel.Ok(Completion("recovered")));
		var (runtime, session) = await StartAsync(model, FastPolicy());

		await runtime.SubmitAsync(session.Id, "hello");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Equal(2, model.RequestCount);
		var notice = Assert.Single(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("retried"));
		Assert.Contains("server error", notice.Content);
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "recovered");
		Assert.DoesNotContain(transcript.Events, item => item.Type == "error");
	}

	/// <summary>
	/// A completion carrying no content at all is a failure, not a silent turn. Local servers emit it
	/// often enough that treating it as success ends turns with nothing to act on.
	/// </summary>
	[Fact]
	public async Task AnEmptyCompletionIsRetriedAndThenReported()
	{
		var model = new SequenceModel(SequenceModel.Ok(Completion(null)));
		var (runtime, session) = await StartAsync(model, FastPolicy());

		await runtime.SubmitAsync(session.Id, "hello");

		var transcript = await runtime.GetSessionAsync(session.Id);
		// One initial attempt plus the policy's single retry.
		Assert.Equal(2, model.RequestCount);
		var error = Assert.Single(transcript!.Events, item => item.Type == "error");
		Assert.Contains("empty completion", error.Content);
	}

	/// <summary>
	/// Agent Framework returns the whole tool exchange as one aggregate response. An empty provider
	/// reply after a tool result must not be hidden by the earlier call and accepted as completion.
	/// </summary>
	[Fact]
	public async Task AnEmptyCompletionAfterAToolResultIsReported()
	{
		await File.WriteAllTextAsync(Path.Combine(Workspace, "note.txt"), "hello\n");
		var model = new SequenceModel(
			SequenceModel.Ok(ToolCall("read", "{\"file_path\":\"note.txt\"}")),
			SequenceModel.Ok(Completion(null)));
		var (runtime, session) = await StartAsync(model, FastPolicy());

		await runtime.SubmitAsync(session.Id, "read the note and summarize it");

		Assert.Equal(2, model.RequestCount);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "tool_result" && item.Content.Contains("hello"));
		var error = Assert.Single(transcript.Events, item => item.Type == "error");
		Assert.Contains("after using a tool", error.Content);
		var summary = Assert.Single(transcript.Events, item => item.Type == "turn_summary");
		using var summaryJson = System.Text.Json.JsonDocument.Parse(summary.Content);
		Assert.Equal("failed", summaryJson.RootElement.GetProperty("outcome").GetString());
		Assert.Equal("model_empty_response", summaryJson.RootElement.GetProperty("stopReason").GetString());
	}

	/// <summary>
	/// OpenRouter can end an otherwise successful HTTP response with its nonstandard <c>error</c>
	/// finish reason. The OpenAI adapter must surface that as a retryable provider failure rather
	/// than throwing while converting the finish-reason enum and killing the turn.
	/// </summary>
	[Fact]
	public async Task AnOpenRouterErrorFinishIsRetriedWithoutPersistingPartialOutput()
	{
		var model = new SequenceModel(
			SequenceModel.Ok(ErrorCompletion()),
			SequenceModel.Ok(Completion("recovered")));
		var (runtime, session) = await StartAsync(model, FastPolicy());

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Equal(2, model.RequestCount);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("retried"));
		Assert.Single(transcript.Events, item => item.Type == "message" && item.Content == "recovered");
		Assert.DoesNotContain(transcript.Events, item => item.Type == "error");
	}

	/// <summary>A failure class no retry can fix ends the turn on its first occurrence.</summary>
	[Fact]
	public async Task APermanentFailureIsNotRetried()
	{
		var model = new SequenceModel(SequenceModel.Status(HttpStatusCode.Unauthorized, "invalid api key"));
		var (runtime, session) = await StartAsync(model, FastPolicy());

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Equal(1, model.RequestCount);
		var transcript = await runtime.GetSessionAsync(session.Id);
		var error = Assert.Single(transcript!.Events, item => item.Type == "error");
		Assert.Contains("API key", error.Content);
	}

	// ─── Context-overflow repair ─────────────────────────────────────────

	/// <summary>
	/// When the provider rejects a request as too large, the transcript is condensed and the request
	/// sent again inside the same turn. Without this the session is stuck: every later turn replays
	/// the same oversized transcript and fails the same way.
	/// </summary>
	[Fact]
	public async Task AContextOverflowIsRepairedByCompactingAndRetrying()
	{
		var model = new SequenceModel(
			SequenceModel.Status(HttpStatusCode.BadRequest, "This model's maximum context length is 8192 tokens"),
			SequenceModel.Ok(Completion("answer after the checkpoint")));
		var compaction = new StubCompaction();
		var (runtime, session) = await StartAsync(model, FastPolicy(), compaction);

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Equal(2, model.RequestCount);
		Assert.Equal(1, compaction.OverflowCalls);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Contains(transcript!.Events, item => item.Type == "compaction");
		Assert.Contains(transcript.Events, item => item.Type == "notice" && item.Content.Contains("context window"));
		Assert.Contains(transcript.Events, item => item.Type == "message" && item.Content == "answer after the checkpoint");
	}

	/// <summary>
	/// One repair, not a loop. A second overflow after a checkpoint landed means the transcript is not
	/// what is oversized, so repeating would summarize the session away for nothing.
	/// </summary>
	[Fact]
	public async Task ASecondOverflowAfterARepairEndsTheTurn()
	{
		var model = new SequenceModel(
			SequenceModel.Status(HttpStatusCode.BadRequest, "This model's maximum context length is 8192 tokens"));
		var compaction = new StubCompaction();
		var (runtime, session) = await StartAsync(model, FastPolicy(), compaction);

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Equal(1, compaction.OverflowCalls);
		Assert.Equal(2, model.RequestCount);
		var transcript = await runtime.GetSessionAsync(session.Id);
		var error = Assert.Single(transcript!.Events, item => item.Type == "error");
		Assert.Contains("context window", error.Content);
	}

	/// <summary>
	/// An overflow the compactor cannot reduce any further is terminal immediately: retrying an
	/// unchanged transcript would send the identical oversized request.
	/// </summary>
	[Fact]
	public async Task AnUnreducibleOverflowIsNotRetried()
	{
		var model = new SequenceModel(
			SequenceModel.Status(HttpStatusCode.BadRequest, "This model's maximum context length is 8192 tokens"));
		var compaction = new StubCompaction { Reducible = false };
		var (runtime, session) = await StartAsync(model, FastPolicy(), compaction);

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.Equal(1, model.RequestCount);
		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == "error");
	}

	// ─── Interrupted-turn fidelity ───────────────────────────────────────

	/// <summary>
	/// A turn stopped after the model called a tool still records that call and its result. The call
	/// already changed the workspace, so dropping it leaves a transcript that disagrees with the files
	/// on disk and a model that redoes the work on the next turn because it cannot see it.
	/// </summary>
	[Fact]
	public async Task AnInterruptedTurnRecordsTheToolCallItAlreadyMade()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session) = await StartAsync(new StreamingToolCallModel("read", "{\"file_path\":\"note.txt\"}"), FastPolicy());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.SubmitAsync(
			session.Id,
			"read the note",
			progress =>
			{
				if (progress.Events?.Any(item => item.Type == "tool_call") == true) runtime.Interrupt(session.Id);
				return ValueTask.CompletedTask;
			}));

		var transcript = await runtime.GetSessionAsync(session.Id);
		var call = Assert.Single(transcript!.Events, item => item.Type == "tool_call");
		Assert.Equal("read", call.ToolName);
		AssertReplayIsBalanced(transcript);
	}

	/// <summary>
	/// A call the interrupt left unanswered is closed with an explicit aborted result. Leaving it open
	/// would break every later turn in the session: providers reject a conversation carrying a tool
	/// call with no matching result.
	/// </summary>
	[Fact]
	public async Task AToolCallLeftUnansweredByAnInterruptIsClosed()
	{
		// ask_user_question blocks until it is answered or cancelled, so the interrupt is guaranteed to
		// land while the call is still outstanding.
		var questions = new UserQuestionBroker();
		var model = new StreamingToolCallModel(
			"ask_user_question",
			"{\"questions\":[{\"id\":\"q1\",\"question\":\"Which one?\",\"header\":\"Pick\"}]}");
		var (runtime, session) = await StartAsync(model, FastPolicy(), questions: questions);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.SubmitAsync(
			session.Id,
			"ask me",
			progress =>
			{
				if (progress.Events?.Any(item => item.Type == "tool_call") == true) runtime.Interrupt(session.Id);
				return ValueTask.CompletedTask;
			}));

		var transcript = await runtime.GetSessionAsync(session.Id);
		var call = Assert.Single(transcript!.Events, item => item.Type == "tool_call");
		var result = Assert.Single(transcript.Events, item => item.Type == "tool_result");
		Assert.Equal(call.ToolCallId, result.ToolCallId);
		Assert.Contains("aborted", result.Content);
		AssertReplayIsBalanced(transcript);
	}

	/// <summary>Every tool call the next turn replays is answered exactly once.</summary>
	private static void AssertReplayIsBalanced(SessionTranscript transcript)
	{
		var history = HarnessRuntime.BuildHistory(transcript.Events);
		var calls = history
			.SelectMany(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>())
			.Select(item => item.CallId)
			.ToHashSet(StringComparer.Ordinal);
		var results = history
			.SelectMany(message => message.Contents.OfType<Microsoft.Extensions.AI.FunctionResultContent>())
			.Select(item => item.CallId)
			.ToHashSet(StringComparer.Ordinal);
		Assert.Equal(calls, results);
	}

	// ─── Fixtures ────────────────────────────────────────────────────────

	/// <summary>One retry with a negligible delay, so a retry test measures behaviour rather than backoff.</summary>
	private static RetryPolicy FastPolicy() => new(MaxRetries: 1, InitialDelayMs: 1, MaxDelayMs: 2, JitterRatio: 0);

	private static string Completion(string? content) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
		+ System.Text.Json.JsonSerializer.Serialize(content)
		+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private static string ToolCall(string name, string argumentsJson) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":["
		+ "{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":"
		+ System.Text.Json.JsonSerializer.Serialize(name)
		+ ",\"arguments\":" + System.Text.Json.JsonSerializer.Serialize(argumentsJson)
		+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private static string ErrorCompletion() =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null},"
		+ "\"finish_reason\":\"error\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":0,\"total_tokens\":1}}";

	private async Task<(HarnessRuntime Runtime, HarnessSession Session)> StartAsync(
		HttpMessageHandler model,
		RetryPolicy policy,
		ICompactionService? compaction = null,
		IUserQuestionBroker? questions = null)
	{
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(model)),
			userQuestions: questions);
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted",
				"Scripted",
				"https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")],
				RetryPolicy: policy),
			"test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service, compaction: compaction, retryPolicies: service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return (runtime, session);
	}

	/// <summary>Answers the model-discovery probe the configuration service makes before a save.</summary>
	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>Replays a scripted sequence of responses, repeating the last one once exhausted.</summary>
	private sealed class SequenceModel(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
	{
		private int _index;

		public int RequestCount { get; private set; }

		public static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

		public static (HttpStatusCode, string) Status(HttpStatusCode status, string message) =>
			(status, "{\"error\":{\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + ",\"type\":\"invalid_request_error\"}}");

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			var (status, body) = responses[Math.Min(_index++, responses.Length - 1)];
			return Task.FromResult(new HttpResponseMessage(status)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
		}
	}

	/// <summary>
	/// Streams one tool call and nothing else. A second request would only arrive if the interrupt was
	/// missed, so it blocks on the token rather than letting a test pass for the wrong reason.
	/// </summary>
	private sealed class StreamingToolCallModel(string tool, string argumentsJson) : HttpMessageHandler
	{
		private int _requests;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (_requests++ > 0) await Task.Delay(Timeout.Infinite, cancellationToken);

			var call = "{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":"
				+ System.Text.Json.JsonSerializer.Serialize(tool)
				+ ",\"arguments\":" + System.Text.Json.JsonSerializer.Serialize(argumentsJson) + "}}";
			var body = new StringBuilder();
			body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\","
				+ "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[" + call + "]},"
				+ "\"finish_reason\":null}]}\n\n");
			body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\","
				+ "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
			body.Append("data: [DONE]\n\n");
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
			};
		}
	}

	/// <summary>
	/// A compactor that produces a checkpoint on demand without a model behind it, so an overflow test
	/// exercises the runtime's repair path rather than the summarizer.
	/// </summary>
	private sealed class StubCompaction : ICompactionService
	{
		public int OverflowCalls { get; private set; }

		/// <summary>False stands for a transcript that cannot be reduced any further.</summary>
		public bool Reducible { get; init; } = true;

		public Task<SessionEvent?> MaybeCompactAsync(
			Guid sessionId,
			IReadOnlyList<SessionEvent> events,
			Microsoft.Agents.AI.AIAgent agent,
			CancellationToken cancellationToken = default) => Task.FromResult<SessionEvent?>(null);

		public Task<SessionEvent?> CompactForOverflowAsync(
			Guid sessionId,
			IReadOnlyList<SessionEvent> events,
			Microsoft.Agents.AI.AIAgent agent,
			CancellationToken cancellationToken = default)
		{
			OverflowCalls++;
			if (!Reducible || events.Count == 0) return Task.FromResult<SessionEvent?>(null);
			return Task.FromResult<SessionEvent?>(new SessionEvent(
				Guid.NewGuid(),
				sessionId,
				"compaction",
				"system",
				"<compacted-summary>earlier work</compacted-summary>",
				DateTimeOffset.UtcNow,
				CompactedThroughId: events[^1].Id));
		}
	}
}

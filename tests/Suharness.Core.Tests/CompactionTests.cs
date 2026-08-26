using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for replay-aware compaction. The thresholds asserted here are the production defaults, so
/// a drift in when a session compacts fails here
/// rather than silently diverging from the harness SuHarness is meant to match.
/// </summary>
public sealed class CompactionTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-compaction-").FullName;
	private readonly Guid _session = Guid.NewGuid();

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private SessionEvent Event(string type, string role, string content, string? callId = null) =>
		new(Guid.NewGuid(), _session, type, role, content, DateTimeOffset.UtcNow, ToolCallId: callId);

	[Fact]
	public void ThePolicyKeepsConfiguredDefaults()
	{
		var policy = new CompactionPolicy();

		Assert.Equal(0.8, policy.ThresholdRatio);
		Assert.Equal(0.16, policy.RetainRatio);
		Assert.Equal(8192, policy.SummaryMaxTokens);
		Assert.Equal(80_000, policy.ThresholdTokens(100_000));
		Assert.Equal(16_000, policy.RetainTokens(100_000));
	}

	[Fact]
	public void NothingIsCompactedWhenTheRetentionBudgetCoversEverything()
	{
		List<SessionEvent> events = [Event("message", "user", "hello"), Event("message", "assistant", "hi")];

		Assert.Null(CompactionPlanner.SelectCutoff(events, retainTokens: 100_000));
	}

	[Fact]
	public void TheCutoffLeavesTheRetentionBudgetIntact()
	{
		var events = Enumerable.Range(0, 100)
			.Select(index => Event("message", index % 2 == 0 ? "user" : "assistant", new string('x', 400)))
			.ToList();

		var cutoff = CompactionPlanner.SelectCutoff(events, retainTokens: 1000);

		Assert.NotNull(cutoff);
		var retained = events.SkipWhile(item => item.Id != cutoff!.Id).Skip(1).ToList();
		Assert.True(CompactionPlanner.EstimateTokens(retained) >= 1000);
		Assert.True(retained.Count < events.Count, "compaction kept the whole transcript");
	}

	/// <summary>
	/// A tool call whose result was compacted away leaves the model holding an unanswered call, which
	/// providers reject outright. The cut walks back until every call in the shadowed span is answered.
	/// </summary>
	[Fact]
	public void TheCutNeverSeparatesAToolCallFromItsResult()
	{
		var filler = Enumerable.Range(0, 40).Select(_ => Event("message", "user", new string('x', 400))).ToList();
		List<SessionEvent> events =
		[
			.. filler,
			Event("tool_call", "assistant", "read(...)", "call-1"),
			Event("tool_result", "tool", new string('y', 4000), "call-1"),
			Event("message", "assistant", "done"),
		];

		// A budget that would otherwise land the cut between the call and its result.
		var cutoff = CompactionPlanner.SelectCutoff(events, retainTokens: 1010);

		Assert.NotNull(cutoff);
		var shadowed = events.TakeWhile(item => item.Id != cutoff!.Id).Append(cutoff!).ToList();
		Assert.Equal(
			shadowed.Count(item => item.Type == "tool_call"),
			shadowed.Count(item => item.Type == "tool_result"));
	}

	[Fact]
	public void AReplayedTranscriptStartsFromTheCheckpointAndKeepsTheRetainedTail()
	{
		var first = Event("message", "user", "the original request");
		var shadowed = Event("message", "assistant", "an early answer nobody needs replayed");
		var checkpoint = Compaction("CHECKPOINT TEXT", shadowed.Id);
		var retained = Event("message", "user", "the newest request");

		var history = HarnessRuntime.BuildHistory([first, shadowed, retained, checkpoint]);

		Assert.Equal(2, history.Count);
		Assert.Equal("CHECKPOINT TEXT", history[0].Text);
		Assert.Equal("the newest request", history[1].Text);
	}

	/// <summary>
	/// A checkpoint whose boundary is missing cannot say which prefix it stands in for. Replaying the
	/// whole transcript wastes context; dropping an unknown span would lose the conversation.
	/// </summary>
	[Fact]
	public void ACheckpointWithAMissingBoundaryFallsBackToTheFullTranscript()
	{
		var kept = Event("message", "user", "the original request");
		var checkpoint = Compaction("CHECKPOINT", Guid.NewGuid());

		var history = HarnessRuntime.BuildHistory([kept, checkpoint]);

		Assert.Single(history);
		Assert.Equal("the original request", history[0].Text);
	}

	[Fact]
	public void TheLastCheckpointWins()
	{
		var one = Event("message", "user", "first");
		var older = Compaction("OLD", one.Id);
		var two = Event("message", "user", "second");
		var newer = Compaction("NEW", two.Id);
		var three = Event("message", "user", "third");

		var history = HarnessRuntime.BuildHistory([one, older, two, newer, three]);

		Assert.Equal(2, history.Count);
		Assert.Equal("NEW", history[0].Text);
		Assert.Equal("third", history[1].Text);
	}

	[Fact]
	public void TheSummarizerNeverReplaysDanglingFunctionContent()
	{
		List<SessionEvent> region =
		[
			Event("message", "user", "find it"),
			Event("tool_call", "assistant", "grep({\"pattern\":\"x\"})", "call-1"),
			Event("tool_result", "tool", "one match", "call-1"),
		];

		var history = HarnessCompaction.ReplayForSummary(region);

		Assert.Equal(3, history.Count);
		Assert.DoesNotContain(history, message =>
			message.Contents.Any(content => content is FunctionCallContent or FunctionResultContent));
		Assert.Contains("[tool result] one match", history[2].Text);
	}

	/// <summary>
	/// A model whose endpoint advertises no context window still compacts, against a conservative
	/// fallback. Requiring the number to be configured meant compaction stayed off for local servers
	/// — the small models that need it most and the only ones that fail to report a window.
	/// </summary>
	[Fact]
	public async Task AModelWithNoAdvertisedContextWindowCompactsAgainstTheFallback()
	{
		var (service, agent) = await StartAsync(contextWindow: null, Summary("- ship it"));

		var checkpoint = await service.MaybeCompactAsync(_session, Oversized(120), agent, CancellationToken.None);

		Assert.NotNull(checkpoint);
		Assert.Equal(32_000, CompactionPolicy.FallbackContextWindow);
	}

	/// <summary>
	/// The estimate has to include what every request carries before any conversation — the system
	/// prompt and tool schemas. Leaving them out was a fixed under-count worth thousands of tokens,
	/// which on a small-context model is the whole margin.
	/// </summary>
	[Fact]
	public void TheEstimateCountsFixedRequestOverhead()
	{
		List<SessionEvent> events = [Event("message", "user", new string('x', 350))];

		Assert.Equal(104, CompactionPlanner.EstimateTokens(events));
		Assert.Equal(104 + CompactionPlanner.RequestOverheadTokens, CompactionPlanner.EstimateRequestTokens(events));
	}

	/// <summary>
	/// Calibrated against a real session: 199,428 replayed characters that KoboldCpp counted as
	/// 64,960 tokens. Four characters per token read 23% low; the estimate must not be optimistic,
	/// because an under-count is what lets a transcript sail past the threshold.
	/// </summary>
	[Fact]
	public void TheEstimateIsNotOptimisticOnARealTranscript()
	{
		var estimate = CompactionPlanner.EstimateTokens(new string('x', 199_428)) + CompactionPlanner.RequestOverheadTokens;

		Assert.InRange(estimate, 57_000, 71_500);
	}

	[Fact]
	public async Task ASessionUnderTheThresholdIsNotCompacted()
	{
		var (service, agent) = await StartAsync(contextWindow: 100_000, Summary("- ship it"));
		List<SessionEvent> events = [Event("message", "user", "short")];

		Assert.Null(await service.MaybeCompactAsync(_session, events, agent, CancellationToken.None));
	}

	[Fact]
	public async Task ASessionOverTheThresholdIsCondensedIntoACheckpointEvent()
	{
		var (service, agent) = await StartAsync(contextWindow: 10_000, Summary("## Primary Request and Intent\\n- ship it"));
		var events = Oversized();

		var checkpoint = await service.MaybeCompactAsync(_session, events, agent, CancellationToken.None);

		Assert.NotNull(checkpoint);
		Assert.Equal("compaction", checkpoint!.Type);
		Assert.Contains(HarnessCompaction.OpenTag, checkpoint.Content);
		Assert.Contains("ship it", checkpoint.Content);
		Assert.Contains("automatically generated checkpoint", checkpoint.Content);
		Assert.NotNull(checkpoint.CompactedThroughId);
		Assert.Contains(events, item => item.Id == checkpoint.CompactedThroughId);
	}

	/// <summary>
	/// A summary cut off at the token cap describes only part of the span it would delete, so a
	/// truncated summarization fails closed and the transcript is left exactly as it was.
	/// </summary>
	[Fact]
	public async Task ATruncatedSummaryIsNotLanded()
	{
		var (service, agent) = await StartAsync(contextWindow: 10_000, Summary("half a summ", finish: "length"));

		Assert.Null(await service.MaybeCompactAsync(_session, Oversized(), agent, CancellationToken.None));
	}

	[Fact]
	public async Task AFailedSummarizationLeavesTheTranscriptAlone()
	{
		var (service, agent) = await StartAsync(contextWindow: 10_000, response: null);

		Assert.Null(await service.MaybeCompactAsync(_session, Oversized(), agent, CancellationToken.None));
	}

	/// <summary>
	/// The summarizing call keeps the conversation's system prompt and tool schemas, preserving
	/// them: an auxiliary request that is a genuine prefix of the last one reuses the provider's warm
	/// cache rather than invalidating it. Tool choice is withheld instead, so it can describe but not
	/// act — the directive alone would only ask.
	/// </summary>
	[Fact]
	public async Task TheSummarizingCallKeepsThePrefixButCannotCallATool()
	{
		var handler = new ScriptedSummarizer(Summary("- ship it"));
		var (service, agent) = await StartAsync(contextWindow: 10_000, handler: handler);

		await service.MaybeCompactAsync(_session, Oversized(), agent, CancellationToken.None);

		var request = Assert.Single(handler.Requests);
		Assert.Contains("acting as a compaction engine", request);
		Assert.Contains("\"tools\":[{", request);
		Assert.Contains("\"tool_choice\":\"none\"", request);
	}

	private SessionEvent Compaction(string content, Guid through) =>
		new(Guid.NewGuid(), _session, "compaction", "system", content, DateTimeOffset.UtcNow, CompactedThroughId: through);

	private List<SessionEvent> Oversized(int count = 60) => [.. Enumerable.Range(0, count)
		.Select(index => Event("message", index % 2 == 0 ? "user" : "assistant", new string('x', 1000)))];

	private static string Summary(string content, string finish = "stop") =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\""
		+ content
		+ "\"},\"finish_reason\":\"" + finish + "\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private Task<(CompactionService Service, AIAgent Agent)> StartAsync(int? contextWindow, string? response) =>
		StartAsync(contextWindow, new ScriptedSummarizer(response));

	private async Task<(CompactionService Service, AIAgent Agent)> StartAsync(
		int? contextWindow,
		ScriptedSummarizer handler)
	{
		var models = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(handler),
			new HttpClientPipelineTransport(new HttpClient(handler)));
		await models.SaveProviderAsync(
			new ModelProviderProfile(
				"local",
				"Local",
				"http://127.0.0.1:5001/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("m", "M", ContextWindow: contextWindow)]),
			null);
		await models.SelectAsync(_session, new ModelSelection("local", "m"));
		var agent = await models.CreateAsync(new AgentContext(_session, _root));
		return (new CompactionService(models), agent!);
	}

	/// <summary>Answers the summarizing call, or fails it when no response is scripted.</summary>
	private sealed class ScriptedSummarizer(string? response) : HttpMessageHandler
	{
		public List<string> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			if (request.Content is not null) Requests.Add(await request.Content.ReadAsStringAsync(cancellationToken));
			return response is null
				? new HttpResponseMessage(HttpStatusCode.InternalServerError)
				{
					Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json"),
				}
				: new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(response, Encoding.UTF8, "application/json"),
				};
		}
	}
}

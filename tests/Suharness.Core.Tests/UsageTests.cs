using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Core;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// Token accounting end to end: what a provider reports, what the harness records against a session,
/// and what that costs. The scenarios are deliberately uneven — complete usage, partial usage, none
/// at all, several models in one session — because the harness talks to gateways and local servers
/// that each answer differently, and the accounting has to stay honest about what it does not know.
/// </summary>
public sealed class UsageTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-usage-").FullName;

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

	// ─── Model metadata import ───────────────────────────────────────────────

	/// <summary>An OpenRouter catalog entry: per-token decimal strings become USD per million.</summary>
	[Fact]
	public async Task OpenRouterPricingIsImportedAndNormalizedToUsdPerMillion()
	{
		var service = CreateService(new CatalogEndpoint("""
			{"data":[{"id":"openai/gpt-4o","name":"OpenAI: GPT-4o","context_length":128000,
			"pricing":{"prompt":"0.0000025","completion":"0.00001","input_cache_read":"0.00000125","request":"0"},
			"top_provider":{"context_length":128000,"max_completion_tokens":16384}}]}
			"""));

		var models = await service.DiscoverModelsAsync(Profile("openrouter", "https://openrouter.ai/api/v1"), "sk-or-test");

		var model = Assert.Single(models);
		Assert.Equal("openai/gpt-4o", model.Id);
		Assert.Equal(128_000, model.ContextWindow);
		Assert.Equal(16_384, model.MaxOutputTokens);
		Assert.Equal(2.5m, model.InputCostPerMillion);
		Assert.Equal(10m, model.OutputCostPerMillion);
		Assert.Equal(1.25m, model.CachedInputCostPerMillion);
		Assert.Equal("openrouter.ai", model.PricingSource);
		Assert.NotNull(model.PricingFetchedAt);
	}

	/// <summary>Half a pricing block leaves the other half unknown rather than zero.</summary>
	[Fact]
	public async Task PartialPricingImportsWhatIsThereAndLeavesTheRestUnknown()
	{
		var service = CreateService(new CatalogEndpoint("""
			{"data":[{"id":"partial","context_length":8192,"pricing":{"prompt":"0.000001"}}]}
			"""));

		var model = Assert.Single(await service.DiscoverModelsAsync(Profile(), null));

		Assert.Equal(1m, model.InputCostPerMillion);
		Assert.Null(model.OutputCostPerMillion);
		Assert.Null(model.CachedInputCostPerMillion);
		Assert.Equal(8192, model.ContextWindow);
	}

	/// <summary>A catalog with a context window and no prices is imported as it stands.</summary>
	[Fact]
	public async Task AModelWithContextButNoPricingKeepsItsContextAndNoRates()
	{
		var service = CreateService(new CatalogEndpoint("""{"data":[{"id":"ctx-only","context_length":262144}]}"""));

		var model = Assert.Single(await service.DiscoverModelsAsync(Profile(), null));

		Assert.Equal(262_144, model.ContextWindow);
		Assert.False(model.HasPricing);
		Assert.Null(model.PricingSource);
		Assert.Null(model.PricingFetchedAt);
	}

	/// <summary>
	/// The minimum a local server can answer with. It must survive discovery, saving, selection, and
	/// a turn — a missing price is not a reason to refuse to run a model.
	/// </summary>
	[Fact]
	public async Task AModelThatIsNothingButAnIdRemainsUsable()
	{
		var scripted = new ScriptedEndpoint(Completion("done", prompt: 12, completion: 3, total: 15));
		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var usage = new SessionUsageTracker(store);
		var service = CreateService(new CatalogEndpoint("""{"data":[{"id":"my-local-model"}]}"""), scripted, usage);

		var discovered = Assert.Single(await service.DiscoverModelsAsync(Profile(), null));
		Assert.Equal("my-local-model", discovered.Id);
		Assert.Equal("my-local-model", discovered.Name);
		Assert.Null(discovered.ContextWindow);
		Assert.False(discovered.HasPricing);

		await service.SaveProviderAsync(Profile() with { Models = [discovered] }, "key");
		var runtime = new HarnessRuntime(store, service, usage: usage);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "my-local-model"));

		await runtime.SubmitAsync(session.Id, "hello");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.DoesNotContain(transcript!.Events, item => item.Type == "error");
		Assert.Equal(15, transcript.Session.Usage?.Totals.TotalTokens);
	}

	/// <summary>A listing that is not shaped like a catalog yields no models rather than an error.</summary>
	[Fact]
	public async Task AnUnrecognizedCatalogShapeReturnsNoModelsInsteadOfFailing()
	{
		var service = CreateService(new CatalogEndpoint("""{"models":["a","b"]}"""));

		Assert.Empty(await service.DiscoverModelsAsync(Profile(), null));
	}

	/// <summary>A refresh may fill blanks; it may never overwrite what an operator configured.</summary>
	[Fact]
	public void RefreshingMetadataFillsGapsAndLeavesConfiguredValuesAlone()
	{
		var configured = new ModelDescriptor(
			"local",
			"Local",
			ContextWindow: 262_144,
			InputCostPerMillion: 0.5m,
			PricingSource: ModelDescriptor.ManualPricingSource);
		var fetched = new ModelDescriptor(
			"local",
			"Fetched name",
			ContextWindow: 8_192,
			InputCostPerMillion: 99m,
			OutputCostPerMillion: 1.5m,
			PricingSource: "example.test",
			PricingFetchedAt: DateTimeOffset.UtcNow);

		var merged = configured.FillGapsFrom(fetched);

		Assert.Equal(262_144, merged.ContextWindow);
		Assert.Equal(0.5m, merged.InputCostPerMillion);
		Assert.Equal(1.5m, merged.OutputCostPerMillion);
		Assert.Equal("Local", merged.Name);
		Assert.Equal(ModelDescriptor.ManualPricingSource, merged.PricingSource);
	}

	/// <summary>A model that had no rates of its own adopts both the fetched rates and their source.</summary>
	[Fact]
	public void AModelWithNoRatesAdoptsFetchedPricingAndItsSource()
	{
		var fetchedAt = DateTimeOffset.UtcNow;
		var merged = new ModelDescriptor("m", "M").FillGapsFrom(
			new ModelDescriptor("m", "M", InputCostPerMillion: 3m, PricingSource: "example.test", PricingFetchedAt: fetchedAt));

		Assert.Equal(3m, merged.InputCostPerMillion);
		Assert.Equal("example.test", merged.PricingSource);
		Assert.Equal(fetchedAt, merged.PricingFetchedAt);
	}

	// ─── Usage normalization ─────────────────────────────────────────────────

	[Fact]
	public async Task CompleteUsageIsRecordedAsReported()
	{
		var usage = await RunAsync(Completion("done", prompt: 120, completion: 30, total: 150));

		Assert.Equal(120, usage.Totals.InputTokens);
		Assert.Equal(30, usage.Totals.OutputTokens);
		Assert.Equal(150, usage.Totals.TotalTokens);
		Assert.Equal(1, usage.Invocations);
	}

	/// <summary>Input and output with no total: the total is derived rather than left blank.</summary>
	[Fact]
	public async Task AMissingTotalIsDerivedFromInputAndOutput()
	{
		var usage = await RunAsync(Completion("done", prompt: 120, completion: 30, total: null));

		Assert.Equal(120, usage.Totals.InputTokens);
		Assert.Equal(30, usage.Totals.OutputTokens);
		Assert.Equal(150, usage.Totals.TotalTokens);
	}

	/// <summary>A total on its own is kept; the split it does not report is not invented.</summary>
	[Fact]
	public async Task ATotalWithNoSplitLeavesInputAndOutputUnknown()
	{
		var usage = await RunAsync(
			"""{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"total_tokens":150}}""");

		Assert.Null(usage.Totals.InputTokens);
		Assert.Null(usage.Totals.OutputTokens);
		Assert.Equal(150, usage.Totals.TotalTokens);
	}

	/// <summary>
	/// No usage block at all. The request is still counted, the tokens stay unknown, and nothing
	/// invents a number from a tokenizer the harness cannot vouch for on an arbitrary endpoint.
	/// </summary>
	[Fact]
	public async Task AProviderThatReportsNoUsageLeavesTokensUnknownRatherThanZero()
	{
		var usage = await RunAsync(
			"""{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}""");

		Assert.False(usage.Totals.HasTokens);
		Assert.Null(usage.Totals.TotalTokens);
		Assert.Equal(1, usage.Invocations);
		Assert.Equal(1, usage.InvocationsWithoutUsage);
		Assert.True(usage.UsageUnreported);
		Assert.Equal("—", UsageFormat.Tokens(usage.Totals.TotalTokens));
	}

	/// <summary>Reasoning and cache counts are captured when a provider breaks them out.</summary>
	[Fact]
	public async Task ReasoningAndCacheCountsAreCapturedWhenReported()
	{
		var usage = await RunAsync(
			"""
			{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],
			"usage":{"prompt_tokens":1000,"completion_tokens":200,"total_tokens":1200,
			"prompt_tokens_details":{"cached_tokens":400,"cache_creation_tokens":50},
			"completion_tokens_details":{"reasoning_tokens":120}}}
			""");

		Assert.Equal(400, usage.Totals.CachedInputTokens);
		Assert.Equal(50, usage.Totals.CachedWriteTokens);
		Assert.Equal(120, usage.Totals.ReasoningTokens);
	}

	/// <summary>Streaming reports usage on its trailing chunk, and it is counted exactly once.</summary>
	[Fact]
	public async Task StreamedUsageIsCountedOncePerRequestRatherThanPerChunk()
	{
		var (runtime, session, _, tracker, store) = await StartAsync(new StreamingEndpoint());

		await runtime.SubmitAsync(session.Id, "hello", progress: _ => ValueTask.CompletedTask);

		var usage = tracker.Get(session.Id)!;
		Assert.Equal(1, usage.Invocations);
		Assert.Equal(11, usage.Totals.InputTokens);
		Assert.Equal(3, usage.Totals.OutputTokens);

		// One usage line per request, whatever the chunk count: streaming must not turn into a write
		// per token, and the session record is written once at the turn boundary.
		var transcript = await store.GetAsync(session.Id);
		Assert.Single(transcript!.Events, item => item.Type == SessionUsageEvent.Type);
	}

	// ─── Session aggregation ─────────────────────────────────────────────────

	/// <summary>
	/// A tool loop is several requests inside one turn. Each is billed, so each is counted, and the
	/// session total is their sum rather than the last one.
	/// </summary>
	[Fact]
	public async Task EveryRoundTripOfAToolLoopIsCountedOnce()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _, tracker, _) = await StartAsync(new ScriptedEndpoint(
			ToolCall("read", """{"file_path":"note.txt"}""", prompt: 100, completion: 10, total: 110),
			Completion("the file says hello", prompt: 200, completion: 20, total: 220)));

		await runtime.SubmitAsync(session.Id, "what is in note.txt?");

		var usage = tracker.Get(session.Id)!;
		Assert.Equal(2, usage.Invocations);
		Assert.Equal(300, usage.Totals.InputTokens);
		Assert.Equal(30, usage.Totals.OutputTokens);
		Assert.Equal(330, usage.Totals.TotalTokens);
	}

	/// <summary>
	/// A retried request was still served and still billed, so both attempts are counted. A failure
	/// that never produced a response is not counted at all.
	/// </summary>
	[Fact]
	public async Task ARetriedTurnCountsWhatTheProviderActuallyServed()
	{
		var (runtime, session, _, tracker, _) = await StartAsync(new ScriptedEndpoint(
			// An empty completion is treated as a failure and repeated, but the provider still
			// reported usage for it.
			Completion(null, prompt: 40, completion: 1, total: 41),
			Completion("second time", prompt: 60, completion: 5, total: 65)));

		await runtime.SubmitAsync(session.Id, "hello");

		var usage = tracker.Get(session.Id)!;
		Assert.Equal(2, usage.Invocations);
		Assert.Equal(106, usage.Totals.TotalTokens);
	}

	/// <summary>Turn totals accumulate across turns instead of restarting each time.</summary>
	[Fact]
	public async Task UsageAccumulatesAcrossTurns()
	{
		var (runtime, session, _, tracker, _) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)));

		await runtime.SubmitAsync(session.Id, "one");
		await runtime.SubmitAsync(session.Id, "two");

		Assert.Equal(24, tracker.Get(session.Id)!.Totals.TotalTokens);
	}

	/// <summary>
	/// A session may use more than one model. Usage is kept per model so the total is right and the
	/// cost can be worked out against each model's own rates.
	/// </summary>
	[Fact]
	public async Task ASessionThatUsesTwoModelsKeepsThemApart()
	{
		var (runtime, session, service, tracker, _) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)),
			[new ModelDescriptor("model-a", "Model A"), new ModelDescriptor("model-b", "Model B")]);

		await service.SelectAsync(session.Id, new ModelSelection("scripted", "model-a"));
		await runtime.SubmitAsync(session.Id, "one");
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "model-b"));
		await runtime.SubmitAsync(session.Id, "two");

		var usage = tracker.Get(session.Id)!;
		Assert.Equal(24, usage.Totals.TotalTokens);
		Assert.Equal(2, usage.ByModel.Count);
		Assert.All(usage.ByModel, entry => Assert.Equal(12, entry.Usage.TotalTokens));
		Assert.Contains(usage.ByModel, entry => entry.Model == "model-a");
		Assert.Contains(usage.ByModel, entry => entry.Model == "model-b");
	}

	/// <summary>Each completed request leaves one auditable line naming the model that served it.</summary>
	[Fact]
	public async Task EachInvocationIsRecordedOnTheTranscriptForAudit()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _, _, store) = await StartAsync(new ScriptedEndpoint(
			ToolCall("read", """{"file_path":"note.txt"}""", prompt: 100, completion: 10, total: 110),
			Completion("done", prompt: 200, completion: 20, total: 220)));

		await runtime.SubmitAsync(session.Id, "read it");

		var transcript = await store.GetAsync(session.Id);
		var entries = transcript!.Events.Where(item => item.Type == SessionUsageEvent.Type).ToArray();
		Assert.Equal(2, entries.Length);
		var first = JsonSerializer.Deserialize<ModelInvocationUsage>(entries[0].Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
		Assert.Equal("scripted", first.Provider);
		Assert.Equal("scripted-model", first.Model);
		Assert.Equal(110, first.Usage.TotalTokens);
	}

	/// <summary>A usage line is bookkeeping, so neither replay nor compaction treats it as conversation.</summary>
	[Fact]
	public async Task UsageLinesAreNotReplayedToTheModel()
	{
		var (runtime, session, _, _, store) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)));

		await runtime.SubmitAsync(session.Id, "one");
		await runtime.SubmitAsync(session.Id, "two");

		var transcript = await store.GetAsync(session.Id);
		var history = HarnessRuntime.BuildHistory(transcript!.Events);
		Assert.DoesNotContain(history, message => message.Text.Contains("\"Usage\"", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(history, message => message.Text.Contains("totalTokens", StringComparison.OrdinalIgnoreCase));
	}

	// ─── Turn and context accounting ─────────────────────────────────────────

	/// <summary>
	/// The shape the whole feature exists to represent: one user turn containing several model
	/// requests separated by tool execution. Three requests here are one turn, not three turns.
	/// </summary>
	[Fact]
	public async Task AToolLoopIsOneTurnContainingSeveralModelCalls()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		File.WriteAllText(Path.Combine(Workspace, "other.txt"), "world");
		var (runtime, session, _, tracker, _) = await StartAsync(new ScriptedEndpoint(
			ToolCall("read", """{"file_path":"note.txt"}""", prompt: 100, completion: 10, total: 110, callId: "one"),
			ToolCall("read", """{"file_path":"other.txt"}""", prompt: 200, completion: 10, total: 210, callId: "two"),
			Completion("both read", prompt: 300, completion: 20, total: 320)));

		await runtime.SubmitAsync(session.Id, "read both files");

		var view = tracker.Snapshot(session.Id)!;
		Assert.Equal(1, view.Turn!.TurnNumber);
		Assert.Equal(3, view.Turn.ModelCalls);
		Assert.Equal(600, view.Turn.Totals.InputTokens);
		Assert.Equal(40, view.Turn.Totals.OutputTokens);
		Assert.Equal(640, view.Turn.Totals.TotalTokens);
		Assert.Equal(3, view.Session.Invocations);
	}

	/// <summary>
	/// A turn boundary resets what the turn has spent and leaves the session's running total alone.
	/// Conflating the two is what turns "this turn cost 12K" into "this session cost 12K".
	/// </summary>
	[Fact]
	public async Task ASecondTurnCountsOnItsOwnWhileTheSessionKeepsAccumulating()
	{
		var (runtime, session, _, tracker, _) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)));

		await runtime.SubmitAsync(session.Id, "one");
		await runtime.SubmitAsync(session.Id, "two");

		var view = tracker.Snapshot(session.Id)!;
		Assert.Equal(2, view.Turn!.TurnNumber);
		Assert.Equal(1, view.Turn.ModelCalls);
		Assert.Equal(12, view.Turn.Totals.TotalTokens);
		Assert.Equal(2, view.Session.Invocations);
		Assert.Equal(24, view.Session.Totals.TotalTokens);
	}

	/// <summary>
	/// Context occupancy is the last request's prompt, never the sum of every prompt the session has
	/// sent. Each request replays the conversation, so the cumulative figure is a multiple of what the
	/// model was ever actually holding, and using it for a window percentage reports a session as full
	/// long before it is.
	/// </summary>
	[Fact]
	public async Task CurrentContextIsTheLastRequestRatherThanEverythingTheSessionProcessed()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _, tracker, _) = await StartAsync(
			new ScriptedEndpoint(
				ToolCall("read", """{"file_path":"note.txt"}""", prompt: 100, completion: 10, total: 110, callId: "one"),
				Completion("done", prompt: 250, completion: 20, total: 270)),
			[new ModelDescriptor("windowed", "Windowed", ContextWindow: 1_000, MaxOutputTokens: 200)]);

		await runtime.SubmitAsync(session.Id, "read it");

		var view = tracker.Snapshot(session.Id)!;
		Assert.Equal(350, view.Session.Totals.InputTokens);

		var context = view.Context!;
		Assert.Equal(250, context.InputTokens);
		Assert.Equal(1_000, context.ContextWindow);
		Assert.Equal(750, context.Remaining);
		Assert.Equal(550, context.SafeRemaining);
		Assert.Equal("25%", UsageFormat.Percent(context.UsedFraction));
		Assert.False(context.Estimated);
	}

	/// <summary>
	/// Cached prompt tokens are part of the input count, not context on top of it. A request reported
	/// as 20K input of which 15K was cached carried 20K, and 5K of that was new.
	/// </summary>
	[Fact]
	public async Task CachedInputIsASubsetOfTheInputCountRatherThanContextOnTopOfIt()
	{
		var (runtime, session, _, tracker, _) = await StartAsync(new ScriptedEndpoint(
			"""
			{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],
			"usage":{"prompt_tokens":20000,"completion_tokens":100,"total_tokens":20100,"prompt_tokens_details":{"cached_tokens":15000}}}
			"""));

		await runtime.SubmitAsync(session.Id, "hello");

		var context = tracker.Snapshot(session.Id)!.Context!;
		Assert.Equal(20_000, context.InputTokens);
		Assert.Equal(15_000, context.CachedInputTokens);
		Assert.Equal(5_000, context.NonCachedInputTokens);
	}

	/// <summary>
	/// Every usage line says which turn it belongs to, where in that turn it fell, and what the model
	/// was configured with when it ran — which is what makes the audit trail answer a question about a
	/// past turn rather than only about the session as a whole.
	/// </summary>
	[Fact]
	public async Task AUsageLineCarriesTheTurnCallAndConfigurationBehindIt()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var (runtime, session, _, _, store) = await StartAsync(
			new ScriptedEndpoint(
				ToolCall("read", """{"file_path":"note.txt"}""", prompt: 100, completion: 10, total: 110, callId: "one"),
				Completion("done", prompt: 200, completion: 20, total: 220)),
			[new ModelDescriptor("windowed", "Windowed", ContextWindow: 8_192, MaxOutputTokens: 1_024)]);

		await runtime.SubmitAsync(session.Id, "read it");

		var transcript = await store.GetAsync(session.Id);
		var lines = transcript!.Events
			.Where(item => item.Type == SessionUsageEvent.Type)
			.Select(item => JsonSerializer.Deserialize<ModelInvocationUsage>(item.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
			.ToArray();

		Assert.Equal(2, lines.Length);
		Assert.All(lines, line => Assert.Equal(1, line.TurnNumber));
		Assert.Equal(new[] { 1, 2 }, lines.Select(line => line.CallNumber));
		Assert.All(lines, line => Assert.NotEqual(Guid.Empty, line.CallId));
		Assert.All(lines, line => Assert.Equal(8_192, line.ContextWindow));
		Assert.All(lines, line => Assert.Equal(1_024, line.MaxOutputTokens));
		Assert.Equal("tool_calls", lines[0].FinishReason);
		Assert.Equal("stop", lines[1].FinishReason);
	}

	/// <summary>A usage line written before turn accounting existed loads with those fields unset.</summary>
	[Fact]
	public void AUsageLineWrittenBeforeTurnAccountingStillLoads()
	{
		var line = JsonSerializer.Deserialize<ModelInvocationUsage>(
			"""{"provider":"p","model":"m","usage":{"inputTokens":10,"outputTokens":2,"totalTokens":12},"timestamp":"2024-01-01T00:00:00+00:00"}""",
			new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

		Assert.Equal(12, line.Usage.TotalTokens);
		Assert.Equal(0, line.TurnNumber);
		Assert.Equal(Guid.Empty, line.CallId);
		Assert.Null(line.ContextWindow);
		Assert.Null(line.FinishReason);
		Assert.Null(line.EstimatedInputTokens);
	}

	/// <summary>
	/// The context reading is carried on the session record, so reopening a session after a restart
	/// still shows how full the model's window was. Turn totals are not, and correctly start again at
	/// the next prompt rather than being reconstructed from a turn nothing is still running.
	/// </summary>
	[Fact]
	public async Task TheContextReadingSurvivesARestartAndTheTurnReadingDoesNot()
	{
		var (runtime, session, _, _, _) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 4_000, completion: 20, total: 4_020)),
			[new ModelDescriptor("windowed", "Windowed", ContextWindow: 16_000, MaxOutputTokens: 2_000)]);
		await runtime.SubmitAsync(session.Id, "one");

		var restartedStore = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var view = UsageSnapshot.FromPersisted((await restartedStore.FindAsync(session.Id))!.Usage)!;

		Assert.Null(view.Turn);
		Assert.Equal(4_000, view.Context!.InputTokens);
		Assert.Equal(16_000, view.Context.ContextWindow);
		Assert.Equal(12_000, view.Context.Remaining);
		Assert.Equal(10_000, view.Context.SafeRemaining);
	}

	/// <summary>
	/// A provider that reports no prompt count gets an estimate for the context reading and nothing
	/// else. The estimate explains why a small local model has started forgetting things; it is never
	/// added to a total and never priced, so the session stays honestly unmeasured.
	/// </summary>
	[Fact]
	public async Task AnUnreportedPromptCountFallsBackToAMarkedEstimateThatEntersNoTotal()
	{
		var (runtime, session, _, tracker, _) = await StartAsync(
			new ScriptedEndpoint(
				"""{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}"""),
			[new ModelDescriptor("windowed", "Windowed", ContextWindow: 32_000)]);

		await runtime.SubmitAsync(session.Id, "hello");

		var view = tracker.Snapshot(session.Id)!;
		Assert.True(view.Context!.Estimated);
		Assert.True(view.Context.InputTokens > 0);
		Assert.Equal(32_000, view.Context.ContextWindow);

		Assert.False(view.Session.Totals.HasTokens);
		Assert.Null(view.Session.Totals.InputTokens);
		Assert.True(view.Session.UsageUnreported);
		Assert.False(view.Turn!.HasTokens);
		Assert.Equal(1, view.Turn.ModelCalls);
		Assert.Equal(1, view.Turn.CallsWithoutUsage);
	}

	/// <summary>
	/// The estimate is taken over the request as it was sent, so it includes the system prompt and the
	/// tool schemas — the part a transcript-based count cannot see, and the part that dominates once an
	/// MCP server is connected.
	/// </summary>
	[Fact]
	public void TheRequestEstimateCountsTheInstructionsAndToolSchemasAsWellAsTheMessages()
	{
		ChatMessage[] messages = [new(ChatRole.User, "hello")];
		var bare = RequestTokenEstimator.Estimate(messages, null);
		var full = RequestTokenEstimator.Estimate(messages, new ChatOptions
		{
			Instructions = new string('x', 10_000),
			Tools = [AIFunctionFactory.Create(() => "ok", "a_tool", new string('y', 400))],
		});

		Assert.True(bare > 0);
		Assert.True(full > bare + 2_500, $"expected the instructions and schema to be counted, got {full} against {bare}");
	}

	/// <summary>A request the estimator cannot read is worth less than the turn it would fail.</summary>
	[Fact]
	public void AnEstimateThatCannotBeTakenReturnsZeroRatherThanThrowing() =>
		Assert.Equal(0, RequestTokenEstimator.Estimate([], null));

	/// <summary>Where the provider was silent the arithmetic stays silent, rather than reading zero.</summary>
	[Fact]
	public void ContextArithmeticStaysUnknownWhereTheProviderWasSilent()
	{
		var context = ContextUsage.From(new ModelInvocationUsage(
			"p", "m", new TokenUsage(OutputTokens: 30), DateTimeOffset.UtcNow, ContextWindow: 1_000))!;

		Assert.False(context.HasInput);
		Assert.Null(context.NonCachedInputTokens);
		Assert.Null(context.UsedFraction);
		Assert.Null(context.Remaining);
		Assert.Null(context.SafeRemaining);
		Assert.Equal("—", UsageFormat.Percent(context.UsedFraction));
	}

	/// <summary>
	/// An unreported cache count leaves the non-cached split unknown. Reading it as "all of it" would
	/// be a claim the provider never made.
	/// </summary>
	[Fact]
	public void UnreportedCacheUsageLeavesTheNonCachedSplitUnknownRatherThanEqualToInput()
	{
		var context = ContextUsage.From(new ModelInvocationUsage(
			"p", "m", new TokenUsage(InputTokens: 5_000), DateTimeOffset.UtcNow))!;

		Assert.Equal(5_000, context.InputTokens);
		Assert.Null(context.CachedInputTokens);
		Assert.Null(context.NonCachedInputTokens);
		Assert.Equal("—", UsageFormat.Exact(context.NonCachedInputTokens));
	}

	/// <summary>
	/// A window configured smaller than the one the provider actually served reads over 100% rather
	/// than being clamped to it: it is a configuration error, and hiding it hides the fix.
	/// </summary>
	[Fact]
	public void AWindowSmallerThanTheRequestReadsOverOneHundredPercentRatherThanBeingHidden()
	{
		var context = ContextUsage.From(new ModelInvocationUsage(
			"p",
			"m",
			new TokenUsage(InputTokens: 12_000),
			DateTimeOffset.UtcNow,
			ContextWindow: 8_000,
			MaxOutputTokens: 1_000))!;

		Assert.Equal("150%", UsageFormat.Percent(context.UsedFraction));
		Assert.Equal(-4_000, context.Remaining);
		Assert.Equal(-5_000, context.SafeRemaining);
	}

	// ─── Persistence ─────────────────────────────────────────────────────────

	/// <summary>Totals survive a restart: a fresh process reads them back and keeps counting.</summary>
	[Fact]
	public async Task AccumulatedUsageSurvivesARestart()
	{
		var (runtime, session, _, _, _) = await StartAsync(
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)));
		await runtime.SubmitAsync(session.Id, "one");

		// A second process: everything rebuilt from the same files on disk, as a restart would.
		var restartedStore = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var restartedTracker = new SessionUsageTracker(restartedStore);
		var restartedService = CreateService(
			new CatalogEndpoint("""{"data":[]}"""),
			new ScriptedEndpoint(Completion("ok", prompt: 10, completion: 2, total: 12)),
			restartedTracker);
		var restarted = new HarnessRuntime(restartedStore, restartedService, usage: restartedTracker);
		Assert.Equal(12, (await restartedStore.FindAsync(session.Id))!.Usage!.Totals.TotalTokens);

		await restarted.SubmitAsync(session.Id, "two");

		Assert.Equal(24, (await restartedStore.FindAsync(session.Id))!.Usage!.Totals.TotalTokens);
	}

	/// <summary>A session written before this feature loads with no usage, and reads as a dash.</summary>
	[Fact]
	public async Task ASessionRecordWrittenBeforeUsageAccountingStillLoads()
	{
		var directory = Path.Combine(_root, "sessions");
		Directory.CreateDirectory(directory);
		var id = Guid.NewGuid();
		await File.WriteAllTextAsync(
			Path.Combine(directory, $"{id:N}.session.json"),
			$$"""
			{"id":"{{id}}","workspace":"","title":"Historical","createdAt":"2024-01-01T00:00:00+00:00",
			"updatedAt":"2024-01-02T00:00:00+00:00","archived":false,"accessMode":1,"planMode":false}
			""");
		var store = new JsonlSessionStore(directory);

		var session = Assert.Single(await store.ListAsync());

		Assert.Equal("Historical", session.Title);
		Assert.Null(session.Usage);
		Assert.Equal("—", UsageFormat.Tokens(session.Usage?.Totals.TotalTokens));
	}

	/// <summary>
	/// A streaming turn writes its usage once, at the turn boundary — not once per chunk and not once
	/// per token.
	/// </summary>
	[Fact]
	public async Task StreamingDoesNotWriteTheSessionRecordPerChunk()
	{
		var store = new CountingStore(new JsonlSessionStore(Path.Combine(_root, "sessions")));
		var tracker = new SessionUsageTracker(store);
		var service = CreateService(new CatalogEndpoint("""{"data":[]}"""), new StreamingEndpoint(), tracker);
		await service.SaveProviderAsync(Profile(), "key");
		var runtime = new HarnessRuntime(store, service, usage: tracker);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		var before = store.SessionWrites;

		await runtime.SubmitAsync(session.Id, "hello", progress: _ => ValueTask.CompletedTask);

		// The turn writes the record for its title and timestamp, and the usage flush writes it once
		// more. Anything proportional to the stream would show up here immediately.
		Assert.True(store.SessionWrites - before <= 3, $"session record written {store.SessionWrites - before} times");
	}

	// ─── Cost ────────────────────────────────────────────────────────────────

	/// <summary>A provider that reports what it billed is believed, and labelled as reported.</summary>
	[Fact]
	public async Task AProviderReportedCostIsPreferredOverAnEstimate()
	{
		var (runtime, session, service, tracker, _) = await StartAsync(new ScriptedEndpoint(
			"""
			{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],
			"usage":{"prompt_tokens":1000,"completion_tokens":500,"total_tokens":1500,"cost":2.17}}
			"""));
		await runtime.SubmitAsync(session.Id, "hello");

		var snapshot = await service.GetSnapshotAsync();
		var cost = SessionCosting.Calculate(tracker.Get(session.Id), Pricing(snapshot));

		Assert.Equal(SessionCostKind.Actual, cost.Kind);
		Assert.Equal(2.17m, cost.Amount);
		Assert.Equal("$2.17", UsageFormat.Money(cost.Amount));
	}

	/// <summary>With rates configured and no reported cost, the figure is derived and marked estimated.</summary>
	[Fact]
	public async Task AnEstimateIsDerivedFromConfiguredRatesWhenNoCostIsReported()
	{
		var (runtime, session, service, tracker, _) = await StartAsync(
			new ScriptedEndpoint(Completion("done", prompt: 132_210, completion: 16_182, total: 148_392)),
			[new ModelDescriptor("scripted-model", "Scripted", InputCostPerMillion: 2m, OutputCostPerMillion: 12.5m)]);
		await runtime.SubmitAsync(session.Id, "hello");

		var snapshot = await service.GetSnapshotAsync();
		var cost = SessionCosting.Calculate(tracker.Get(session.Id), Pricing(snapshot));

		Assert.Equal(SessionCostKind.Estimated, cost.Kind);
		Assert.Equal(0.264420m + 0.202275m, cost.Amount);
		Assert.Equal("$0.47", UsageFormat.Money(cost.Amount));
		Assert.Equal(2, cost.Components.Count);
	}

	/// <summary>Usage but no rates: the reader is told cost is unavailable, never shown a fake zero.</summary>
	[Fact]
	public async Task WithoutPricingTheCostIsUnavailableRatherThanZero()
	{
		var (runtime, session, service, tracker, _) = await StartAsync(
			new ScriptedEndpoint(Completion("done", prompt: 500_000, completion: 23_000, total: 523_000)));
		await runtime.SubmitAsync(session.Id, "hello");

		var usage = tracker.Get(session.Id)!;
		var cost = SessionCosting.Calculate(usage, Pricing(await service.GetSnapshotAsync()));

		Assert.Equal(SessionCostKind.Unavailable, cost.Kind);
		Assert.Null(cost.Amount);
		Assert.Equal("scripted-model", Assert.Single(cost.UnpricedModels));
		// The session still reports what it spent in tokens, which is the metric that never depends
		// on anyone having configured a price.
		Assert.Equal("523K", UsageFormat.Tokens(usage.Totals.TotalTokens));
	}

	/// <summary>
	/// Cached prompt tokens are already inside the provider's input count, so the input rate applies
	/// to the remainder. Charging both in full would overstate exactly the sessions caching helped.
	/// </summary>
	[Fact]
	public void CachedInputIsPricedOnceAtItsOwnRate()
	{
		var usage = SessionUsage.None.Add(new ModelInvocationUsage(
			"p",
			"m",
			new TokenUsage(InputTokens: 1_000_000, OutputTokens: 0, TotalTokens: 1_000_000, CachedInputTokens: 400_000),
			DateTimeOffset.UtcNow));

		var cost = SessionCosting.Calculate(usage, (_, _) => new ModelDescriptor(
			"m",
			"M",
			InputCostPerMillion: 10m,
			OutputCostPerMillion: 30m,
			CachedInputCostPerMillion: 1m));

		// 600,000 uncached at $10/1M plus 400,000 cached at $1/1M.
		Assert.Equal(6.4m, cost.Amount);
	}

	/// <summary>A session mixing a priced model with an unpriced one is an estimate, and says so.</summary>
	[Fact]
	public void AMixOfPricedAndUnpricedModelsEstimatesWhatItCanAndNamesTheRest()
	{
		var now = DateTimeOffset.UtcNow;
		var usage = SessionUsage.None
			.Add(new ModelInvocationUsage("p", "priced", new TokenUsage(1_000_000, 0, 1_000_000), now))
			.Add(new ModelInvocationUsage("p", "unpriced", new TokenUsage(2_000_000, 0, 2_000_000), now));

		var cost = SessionCosting.Calculate(usage, (_, model) =>
			model == "priced" ? new ModelDescriptor("priced", "Priced", InputCostPerMillion: 4m) : null);

		Assert.Equal(SessionCostKind.Estimated, cost.Kind);
		Assert.Equal(4m, cost.Amount);
		Assert.Equal("unpriced", Assert.Single(cost.UnpricedModels));
	}

	/// <summary>A session with no usage at all has nothing to price.</summary>
	[Fact]
	public void ASessionWithNoUsageHasNoCost()
	{
		Assert.Equal(SessionCostKind.Unavailable, SessionCosting.Calculate(null, (_, _) => null).Kind);
		Assert.Equal(SessionCostKind.Unavailable, SessionCosting.Calculate(SessionUsage.None, (_, _) => null).Kind);
	}

	/// <summary>A configured rate of zero is a real price and produces a real, free, estimate.</summary>
	[Fact]
	public void AZeroRateIsPricedAsFreeRatherThanAsUnknown()
	{
		var usage = SessionUsage.None.Add(new ModelInvocationUsage(
			"p", "m", new TokenUsage(1_000, 100, 1_100), DateTimeOffset.UtcNow));

		var cost = SessionCosting.Calculate(usage, (_, _) =>
			new ModelDescriptor("m", "M", InputCostPerMillion: 0m, OutputCostPerMillion: 0m));

		Assert.Equal(SessionCostKind.Estimated, cost.Kind);
		Assert.Equal(0m, cost.Amount);
		Assert.Empty(cost.UnpricedModels);
	}

	// ─── Formatting ──────────────────────────────────────────────────────────

	[Theory]
	[InlineData(null, "—")]
	[InlineData(823L, "823")]
	[InlineData(12_400L, "12.4K")]
	[InlineData(872_000L, "872K")]
	[InlineData(1_210_000L, "1.21M")]
	[InlineData(148_392L, "148K")]
	public void TokenCountsAreWrittenCompactly(long? value, string expected) =>
		Assert.Equal(expected, UsageFormat.Tokens(value));

	[Theory]
	[InlineData(null, "—")]
	[InlineData(0.277, "27.7%")]
	[InlineData(0.5, "50%")]
	[InlineData(0.0004, "0%")]
	[InlineData(1.5, "150%")]
	public void ContextOccupancyIsWrittenAsAPercentage(double? value, string expected) =>
		Assert.Equal(expected, UsageFormat.Percent(value));

	[Theory]
	[InlineData(null, "—")]
	[InlineData(0.466, "$0.47")]
	[InlineData(2.17, "$2.17")]
	[InlineData(0.0043, "$0.0043")]
	public void MoneyIsWrittenWithEnoughPrecisionToBeHonest(double? value, string expected) =>
		Assert.Equal(expected, UsageFormat.Money((decimal?)value));

	// ─── Harness ─────────────────────────────────────────────────────────────

	private static Func<string?, string?, ModelDescriptor?> Pricing(ModelConfigurationSnapshot snapshot) =>
		(providerId, modelId) => snapshot.Providers
			.FirstOrDefault(item => item.Profile.Id == providerId)?.Profile.Models
			.FirstOrDefault(model => model.Id == modelId);

	private static ModelProviderProfile Profile(string id = "scripted", string baseUrl = "https://scripted.test/v1") =>
		new(id, "Scripted", baseUrl, ModelProtocol.OpenAIChatCompletions, [new ModelDescriptor("scripted-model", "Scripted Model")]);

	private ModelConfigurationService CreateService(
		HttpMessageHandler catalog,
		HttpMessageHandler? model = null,
		ISessionUsageTracker? tracker = null) => new(
		Path.Combine(_root, "configuration"),
		DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
		new HttpClient(catalog),
		model is null ? null : new HttpClientPipelineTransport(new HttpClient(model)),
		usageTracker: tracker);

	/// <summary>Run one turn against a scripted response and return the session's accumulated usage.</summary>
	private async Task<SessionUsage> RunAsync(string response)
	{
		var (runtime, session, _, tracker, _) = await StartAsync(new ScriptedEndpoint(response));
		await runtime.SubmitAsync(session.Id, "hello");
		return tracker.Get(session.Id)!;
	}

	private async Task<(HarnessRuntime Runtime, HarnessSession Session, ModelConfigurationService Service, SessionUsageTracker Tracker, ISessionStore Store)> StartAsync(
		HttpMessageHandler model,
		IReadOnlyList<ModelDescriptor>? models = null)
	{
		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var tracker = new SessionUsageTracker(store);
		var service = CreateService(new CatalogEndpoint("""{"data":[]}"""), model, tracker);
		await service.SaveProviderAsync(
			models is null ? Profile() : Profile() with { Models = models },
			"key");
		var runtime = new HarnessRuntime(store, service, usage: tracker);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", models is null ? "scripted-model" : models[0].Id));
		return (runtime, session, service, tracker, store);
	}

	private static string Completion(string? content, long prompt, long completion, long? total) =>
		"""{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":"""
		+ JsonSerializer.Serialize(content)
		+ """},"finish_reason":"stop"}],"usage":{"prompt_tokens":"""
		+ prompt
		+ ",\"completion_tokens\":" + completion
		+ (total is null ? string.Empty : ",\"total_tokens\":" + total)
		+ "}}";

	private static string ToolCall(
		string name,
		string argumentsJson,
		long prompt,
		long completion,
		long total,
		string? callId = null) =>
		"""{"id":"c","object":"chat.completion","created":1,"model":"scripted-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_"""
		+ (callId ?? name) + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
		+ JsonSerializer.Serialize(argumentsJson)
		+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":" + prompt
		+ ",\"completion_tokens\":" + completion + ",\"total_tokens\":" + total + "}}";

	/// <summary>Answers the model-catalog request with a fixed body.</summary>
	private sealed class CatalogEndpoint(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>Answers each chat request with the next scripted body, repeating the last one.</summary>
	private sealed class ScriptedEndpoint(params string[] bodies) : HttpMessageHandler
	{
		private int _index;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(bodies[Math.Min(_index++, bodies.Length - 1)], Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>Streams several content chunks and reports usage on the trailing one.</summary>
	private sealed class StreamingEndpoint : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var body = new StringBuilder();
			foreach (var (chunk, index) in new[] { "one ", "two ", "three" }.Select((text, index) => (text, index)))
			{
				body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{")
					.Append(index == 0 ? "\"role\":\"assistant\"," : string.Empty)
					.Append("\"content\":")
					.Append(JsonSerializer.Serialize(chunk))
					.Append("},\"finish_reason\":null}]}\n\n");
			}
			body.Append("data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"scripted-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":3,\"total_tokens\":14}}\n\n");
			body.Append("data: [DONE]\n\n");
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
			});
		}
	}

	/// <summary>Counts how often the session record is written, to keep the write path honest.</summary>
	private sealed class CountingStore(ISessionStore inner) : ISessionStore
	{
		public int SessionWrites { get; private set; }

		public Task<IReadOnlyList<HarnessSession>> ListAsync(CancellationToken cancellationToken = default) =>
			inner.ListAsync(cancellationToken);

		public Task<SessionTranscript?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
			inner.GetAsync(sessionId, cancellationToken);

		public Task<HarnessSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
			inner.FindAsync(sessionId, cancellationToken);

		public Task SaveSessionAsync(HarnessSession session, CancellationToken cancellationToken = default)
		{
			SessionWrites++;
			return inner.SaveSessionAsync(session, cancellationToken);
		}

		public Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default) =>
			inner.AppendAsync(sessionEvent, cancellationToken);

		public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
			inner.DeleteAsync(sessionId, cancellationToken);
	}
}

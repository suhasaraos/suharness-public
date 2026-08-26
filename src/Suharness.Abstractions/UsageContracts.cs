namespace Suharness.Abstractions;

/// <summary>
/// Normalized token usage for one model invocation, or an accumulated total over many. Every field
/// is nullable because a provider reports what it chooses to report, and the harness talks to
/// OpenRouter, unknown OpenAI-compatible gateways, and local servers alike.
/// </summary>
/// <remarks>
/// Null and zero mean different things here and are never conflated. Null is "the provider did not
/// say"; zero is "the provider said none". A harness that stored an unreported count as zero would
/// present a confident total that nothing measured, and the difference matters most exactly where
/// the metadata is thinnest — local models and custom endpoints.
/// </remarks>
public sealed record TokenUsage(
	long? InputTokens = null,
	long? OutputTokens = null,
	long? TotalTokens = null,
	long? ReasoningTokens = null,
	long? CachedInputTokens = null,
	long? CachedWriteTokens = null,
	decimal? ActualCost = null)
{
	/// <summary>Usage that reports nothing at all.</summary>
	public static readonly TokenUsage None = new();

	/// <summary>Whether the provider reported any token count at all.</summary>
	public bool HasTokens => InputTokens is not null || OutputTokens is not null || TotalTokens is not null;

	/// <summary>Whether the provider reported nothing this record could carry.</summary>
	public bool IsEmpty =>
		!HasTokens && ReasoningTokens is null && CachedInputTokens is null && CachedWriteTokens is null && ActualCost is null;

	/// <summary>
	/// The same usage with a total filled in when the provider gave input and output but no total.
	/// A provider that reports only a total leaves input and output unknown rather than inventing a
	/// split for them.
	/// </summary>
	public TokenUsage Normalized() =>
		TotalTokens is null && (InputTokens is not null || OutputTokens is not null)
			? this with { TotalTokens = (InputTokens ?? 0) + (OutputTokens ?? 0) }
			: this;

	/// <summary>Add another record's counts. A count absent from both sides stays absent.</summary>
	public TokenUsage Add(TokenUsage other) => new(
		Sum(InputTokens, other.InputTokens),
		Sum(OutputTokens, other.OutputTokens),
		Sum(TotalTokens, other.TotalTokens),
		Sum(ReasoningTokens, other.ReasoningTokens),
		Sum(CachedInputTokens, other.CachedInputTokens),
		Sum(CachedWriteTokens, other.CachedWriteTokens),
		Sum(ActualCost, other.ActualCost));

	private static long? Sum(long? left, long? right) =>
		left is null ? right : right is null ? left : left.Value + right.Value;

	private static decimal? Sum(decimal? left, decimal? right) =>
		left is null ? right : right is null ? left : left.Value + right.Value;
}

/// <summary>
/// One model invocation's usage, attributed to the model and route that served it, and to the place
/// in the conversation it was made from. A session may call several models — a planner, a fallback, a
/// retry that landed on another provider — so usage is recorded per invocation and aggregated
/// afterwards rather than assumed to belong to one price.
/// </summary>
/// <remarks>
/// Everything past <paramref name="Timestamp"/> is optional and defaulted, so a <c>usage</c> line
/// written before this record carried turn identity still loads: the fields it never had read as
/// zero-or-null, which is what they are.
/// </remarks>
/// <param name="TurnNumber">
/// The user turn this request belongs to. One turn is a user prompt and everything the agent does to
/// answer it, so a turn that calls tools five times carries five invocations with the same number
/// here. Zero means the request was made outside a runtime turn — a subagent's own run, or a session
/// resumed by a process that never opened one.
/// </param>
/// <param name="CallNumber">Which request this is within its turn, from one.</param>
/// <param name="StepNumber">
/// The agent loop's own round-trip counter for this turn, as reported through
/// <c>IAgentLifecycle.ObserveStep</c>. It tracks <paramref name="CallNumber"/> for an ordinary turn
/// and diverges when a request is retried or an attempt is thrown away, which is exactly when the
/// difference is worth having.
/// </param>
/// <param name="CallId">This request's own identity, for correlating a usage line with a diagnosis.</param>
/// <param name="ResponseId">The provider's id for the response, when it gave one.</param>
/// <param name="ContextWindow">
/// The context window configured for the model that served this request, or null when nothing has
/// said what it is. It is recorded per request rather than read at display time because a session may
/// change models, and a window read later would be the wrong one for an earlier request.
/// </param>
/// <param name="MaxOutputTokens">The completion cap configured for that model, or null when unset.</param>
/// <param name="FinishReason">
/// Why the model stopped — <c>stop</c>, <c>length</c>, <c>tool_calls</c>, <c>content_filter</c>, or
/// whatever else the provider reported. Null when it reported nothing.
/// </param>
/// <param name="EstimatedInputTokens">
/// What the harness measured the request at when the provider did not report a prompt count. It is
/// deliberately kept out of <see cref="TokenUsage"/>: an estimate may show a reader roughly how full
/// the context is, but it must never be added to a session total or priced as though it were
/// measured. Null whenever the provider reported the real figure, which is the normal case.
/// </param>
public sealed record ModelInvocationUsage(
	string? Provider,
	string? Model,
	TokenUsage Usage,
	DateTimeOffset Timestamp,
	int TurnNumber = 0,
	int CallNumber = 0,
	int StepNumber = 0,
	Guid CallId = default,
	string? ResponseId = null,
	int? ContextWindow = null,
	int? MaxOutputTokens = null,
	string? FinishReason = null,
	long? EstimatedInputTokens = null);

/// <summary>Everything one (provider, model) pair accumulated within a session.</summary>
public sealed record ModelUsageTotal(
	string? Provider,
	string? Model,
	TokenUsage Usage,
	int Invocations,
	DateTimeOffset UpdatedAt);

/// <summary>
/// A session's accumulated usage. The totals are what the session list shows; the per-model
/// breakdown is what makes a cost estimate correct when a session used more than one model.
/// </summary>
/// <param name="Invocations">Model requests this session has made, whether or not usage came back.</param>
/// <param name="InvocationsWithoutUsage">
/// Requests the provider answered without reporting usage. A session with invocations but no counts
/// is reported as unmeasured rather than as zero, because the harness has no tokenizer it can trust
/// for an arbitrary endpoint and will not present a guess as a measurement.
/// </param>
/// <param name="LastCall">
/// The most recent request this session made, kept so the context indicator survives a restart. It
/// is one record, not a history: the per-request audit trail is on the transcript, and duplicating it
/// on the session record would make the cost of the feature grow with the length of the session.
/// </param>
public sealed record SessionUsage(
	TokenUsage Totals,
	IReadOnlyList<ModelUsageTotal> ByModel,
	int Invocations = 0,
	int InvocationsWithoutUsage = 0,
	ModelInvocationUsage? LastCall = null)
{
	public static readonly SessionUsage None = new(TokenUsage.None, []);

	/// <summary>Whether anything was measured. False keeps the session list showing a dash.</summary>
	public bool HasTokens => Totals.HasTokens;

	/// <summary>Whether every request this session made went unmeasured by its provider.</summary>
	public bool UsageUnreported => Invocations > 0 && !Totals.HasTokens;

	/// <summary>Fold one invocation into these totals.</summary>
	public SessionUsage Add(ModelInvocationUsage invocation)
	{
		var usage = invocation.Usage.Normalized();
		var models = new List<ModelUsageTotal>(ByModel);
		var index = models.FindIndex(item =>
			string.Equals(item.Provider, invocation.Provider, StringComparison.Ordinal)
			&& string.Equals(item.Model, invocation.Model, StringComparison.Ordinal));
		if (index < 0)
		{
			models.Add(new ModelUsageTotal(invocation.Provider, invocation.Model, usage, 1, invocation.Timestamp));
		}
		else
		{
			models[index] = models[index] with
			{
				Usage = models[index].Usage.Add(usage),
				Invocations = models[index].Invocations + 1,
				UpdatedAt = invocation.Timestamp,
			};
		}

		return new SessionUsage(
			Totals.Add(usage),
			models,
			Invocations + 1,
			InvocationsWithoutUsage + (usage.IsEmpty ? 1 : 0),
			invocation with { Usage = usage });
	}
}

/// <summary>
/// What one user turn spent. A turn is a user prompt and everything the agent did to answer it, so
/// these totals cover every request the turn made — the first one, each round-trip after a tool ran,
/// a retried attempt, and the one that produced the final answer.
/// </summary>
/// <remarks>
/// The processed totals are exactly that: processed. A turn of four requests over the same growing
/// conversation reprocesses most of its history each time, so the input total here is several times
/// what any single request actually carried. It answers what the turn cost, never how full the
/// context is — that is <see cref="ContextUsage"/>, and conflating the two is the mistake this
/// separation exists to prevent.
/// </remarks>
/// <param name="ModelCalls">Requests this turn has made, whether or not usage came back.</param>
public sealed record TurnUsage(
	int TurnNumber,
	TokenUsage Totals,
	int ModelCalls = 0,
	int CallsWithoutUsage = 0)
{
	/// <summary>Whether anything this turn spent was measured.</summary>
	public bool HasTokens => Totals.HasTokens;

	/// <summary>Fold one of this turn's requests into its totals.</summary>
	public TurnUsage Add(ModelInvocationUsage invocation)
	{
		var usage = invocation.Usage.Normalized();
		return this with
		{
			Totals = Totals.Add(usage),
			ModelCalls = ModelCalls + 1,
			CallsWithoutUsage = CallsWithoutUsage + (usage.IsEmpty ? 1 : 0),
		};
	}
}

/// <summary>
/// How full the model's context was on the latest request — the prompt it was actually sent, against
/// the window it has to hold it.
/// </summary>
/// <remarks>
/// This is one request's measurement, never a running total. The prompt of a single request already
/// contains the whole context: the system prompt, the harness instructions, the tool schemas, the
/// replayed conversation, every tool call and result, injected file context, checkpoints, and the
/// user's own message. Summing prompts across a session would answer a different question — how many
/// tokens the session has processed — and answering it here would report a session as well past a
/// window it never came close to filling.
/// </remarks>
/// <param name="InputTokens">
/// The prompt this request carried, as the provider measured it, or as the harness estimated it when
/// the provider reported nothing — in which case <paramref name="Estimated"/> is set.
/// </param>
/// <param name="Estimated">
/// Whether the input figure is the harness's own measurement rather than the provider's. It is shown
/// labelled as an estimate and is never used for cost.
/// </param>
public sealed record ContextUsage(
	long? InputTokens,
	long? CachedInputTokens,
	int? ContextWindow,
	int? MaxOutputTokens,
	bool Estimated,
	string? Provider,
	string? Model,
	string? FinishReason,
	DateTimeOffset Timestamp)
{
	/// <summary>
	/// The part of the prompt the provider did not serve from cache. Cached tokens are a subset of the
	/// input count and a billing characteristic of it, never context on top of it: a request reported
	/// as 20K input of which 15K was cached carried 20K, and 5K of that was new.
	/// </summary>
	public long? NonCachedInputTokens =>
		InputTokens is { } input && CachedInputTokens is { } cached ? Math.Max(0, input - cached) : null;

	/// <summary>How much of the window this request filled, from 0 to 1, or null when either is unknown.</summary>
	/// <remarks>
	/// Not clamped. A figure over 1 means the configured window is smaller than the one the provider
	/// actually served, which is a configuration error worth showing rather than hiding at 100%.
	/// </remarks>
	public double? UsedFraction =>
		InputTokens is { } input && ContextWindow is > 0 ? (double)input / ContextWindow.Value : null;

	/// <summary>Window left after this prompt, or null when either figure is unknown.</summary>
	public long? Remaining =>
		InputTokens is { } input && ContextWindow is > 0 ? ContextWindow.Value - input : null;

	/// <summary>
	/// Window left once room is reserved for a full-length answer. Negative means the next response
	/// could be cut off before the model finishes it, which is the case worth seeing coming.
	/// </summary>
	public long? SafeRemaining =>
		Remaining is { } remaining && MaxOutputTokens is > 0 ? remaining - MaxOutputTokens.Value : null;

	/// <summary>Whether anything is known about the prompt this request carried.</summary>
	public bool HasInput => InputTokens is not null;

	/// <summary>
	/// The context view of one request, or null when there was no request. A provider that reported no
	/// prompt count and gave the harness nothing to estimate from still yields a record — its input is
	/// unknown, but the model and finish reason it carries are worth showing.
	/// </summary>
	public static ContextUsage? From(ModelInvocationUsage? call) => call is null
		? null
		: new ContextUsage(
			call.Usage.InputTokens ?? call.EstimatedInputTokens,
			call.Usage.CachedInputTokens,
			call.ContextWindow,
			call.MaxOutputTokens,
			Estimated: call.Usage.InputTokens is null && call.EstimatedInputTokens is not null,
			call.Provider,
			call.Model,
			call.FinishReason,
			call.Timestamp);
}

/// <summary>
/// The three levels of token accounting a reader needs at once: what the last request carried, what
/// the turn in progress has spent, and what the session has spent altogether.
/// </summary>
/// <param name="Turn">
/// The turn in progress, or the one that just finished. Null for a session this process has not run a
/// turn for — a session reopened after a restart shows its context and its session totals, and starts
/// counting turn totals again at its next prompt.
/// </param>
/// <param name="LastCall">The most recently completed request, or null when the session has made none.</param>
public sealed record UsageSnapshot(
	SessionUsage Session,
	TurnUsage? Turn,
	ModelInvocationUsage? LastCall,
	ContextUsage? Context)
{
	/// <summary>The view of a session whose only record is what was persisted for it.</summary>
	public static UsageSnapshot? FromPersisted(SessionUsage? session) => session is null
		? null
		: new UsageSnapshot(session, null, session.LastCall, ContextUsage.From(session.LastCall));
}

/// <summary>
/// Accumulates per-session token usage as model invocations complete, and persists it at a turn
/// boundary. Counters are incremental: nothing here re-reads a transcript to work out a total.
/// </summary>
public interface ISessionUsageTracker
{
	/// <summary>
	/// Seed a session's counters from what was persisted, once per process. Called before a turn
	/// starts, so a second turn adds to the running total instead of replacing it.
	/// </summary>
	ValueTask EnsureLoadedAsync(Guid sessionId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Open a user turn, so the requests that follow are attributed to it. Called by the runtime at the
	/// point it assigns a turn number, which is the only place that knows where one turn ends and the
	/// next begins — a tool loop makes many requests inside one turn, and no property of the request
	/// stream distinguishes the last request of one turn from the first request of the next.
	/// </summary>
	void BeginTurn(Guid sessionId, int turnNumber);

	/// <summary>
	/// Fold one completed model request into the session's counters, and return the record as it was
	/// filed — stamped with the turn it belongs to and its place within that turn. Called from the model
	/// pipeline, so it must not block: persistence happens at the next flush.
	/// </summary>
	ModelInvocationUsage Record(Guid sessionId, ModelInvocationUsage invocation);

	/// <summary>The session's accumulated usage as it currently stands, or null when nothing is known.</summary>
	SessionUsage? Get(Guid sessionId);

	/// <summary>
	/// All three levels of accounting for a session at once — last request, current turn, session — or
	/// null when this process holds nothing for it. Cheap enough to call from a render: it reads counters
	/// that are already maintained and derives nothing from a transcript.
	/// </summary>
	UsageSnapshot? Snapshot(Guid sessionId);

	/// <summary>Write the accumulated totals through to storage, if anything changed since the last flush.</summary>
	ValueTask FlushAsync(Guid sessionId, CancellationToken cancellationToken = default);

	/// <summary>Raised when a session's counters change, so a view can refresh without polling.</summary>
	event Action<Guid>? Changed;
}

/// <summary>How one model invocation's usage is recorded on a session transcript.</summary>
/// <remarks>
/// The rolled-up counters on the session record are what the harness reads; this line is the audit
/// trail behind them, so a session that used several models can be examined invocation by invocation
/// after the fact. It is bookkeeping rather than conversation, so replay and the transcript view both
/// skip it.
/// </remarks>
public static class SessionUsageEvent
{
	/// <summary>The transcript event type one model invocation's usage is recorded under.</summary>
	public const string Type = "usage";
}

/// <summary>Which of the three cost states a session is in.</summary>
public enum SessionCostKind
{
	/// <summary>No pricing and no provider-reported cost, so no figure can honestly be shown.</summary>
	Unavailable,

	/// <summary>Derived from the model rates configured in the harness.</summary>
	Estimated,

	/// <summary>Reported by the provider as what the requests actually cost.</summary>
	Actual,
}

/// <summary>One priced line of a cost breakdown.</summary>
/// <param name="PricePerMillion">Null on a provider-reported line, which carries no rate of its own.</param>
public sealed record CostComponent(
	string Label,
	long? Tokens,
	decimal? PricePerMillion,
	decimal Cost);

/// <summary>
/// What a session cost, and how that figure was arrived at. Computed on demand — the session list
/// never prices a session the reader has not asked about.
/// </summary>
public sealed record SessionCost(
	SessionCostKind Kind,
	decimal? Amount,
	IReadOnlyList<CostComponent> Components,
	IReadOnlyList<string> UnpricedModels)
{
	public static readonly SessionCost Unavailable = new(SessionCostKind.Unavailable, null, [], []);
}

/// <summary>
/// Turns accumulated usage and configured model rates into a cost. Kept as a pure function so the
/// arithmetic is testable on its own and the popup that calls it stays presentation only.
/// </summary>
public static class SessionCosting
{
	/// <summary>
	/// Price a session. <paramref name="pricing"/> resolves the rates configured for one
	/// (provider, model) pair, or null when that model is not configured any more.
	/// </summary>
	/// <remarks>
	/// A provider-reported cost wins for the model it covers: it is what the account was actually
	/// billed, where an estimate is only what the configured rates imply. A session mixing a model
	/// that reports cost with one that does not is reported as an estimate, because half of the
	/// figure is still derived.
	/// </remarks>
	public static SessionCost Calculate(SessionUsage? usage, Func<string?, string?, ModelDescriptor?> pricing)
	{
		if (usage is null || usage.ByModel.Count == 0) return SessionCost.Unavailable;

		var components = new List<CostComponent>();
		var unpriced = new List<string>();
		var total = 0m;
		var anyEstimated = false;
		var anyActual = false;
		var multipleModels = usage.ByModel.Count > 1;

		foreach (var entry in usage.ByModel)
		{
			var name = entry.Model ?? "unknown model";
			if (entry.Usage.ActualCost is { } actual)
			{
				anyActual = true;
				total += actual;
				components.Add(new CostComponent(
					multipleModels ? $"Provider cost · {name}" : "Provider cost",
					entry.Usage.TotalTokens,
					null,
					actual));
				continue;
			}

			var rates = pricing(entry.Provider, entry.Model);
			var priced = false;

			// Cached input tokens are already counted inside the provider's input total, so the
			// uncached remainder is what the input rate applies to. Charging both in full would
			// overstate exactly the sessions where caching saved the most.
			var cached = entry.Usage.CachedInputTokens;
			var cachedPrice = rates?.CachedInputCostPerMillion;
			if (cached is > 0 && cachedPrice is not null)
			{
				priced = true;
				var cost = Cost(cached.Value, cachedPrice.Value);
				total += cost;
				components.Add(new CostComponent(
					multipleModels ? $"Cached input · {name}" : "Cached input",
					cached,
					cachedPrice,
					cost));
			}

			if (entry.Usage.InputTokens is { } input && rates?.InputCostPerMillion is { } inputRate)
			{
				var billable = cachedPrice is null || cached is null ? input : Math.Max(0, input - cached.Value);
				priced = true;
				var cost = Cost(billable, inputRate);
				total += cost;
				components.Add(new CostComponent(
					multipleModels ? $"Input · {name}" : "Input",
					billable,
					inputRate,
					cost));
			}

			// Reasoning tokens are billed inside the completion count on every OpenAI-compatible
			// provider the harness has seen, so they are shown but never priced a second time.
			if (entry.Usage.OutputTokens is { } output && rates?.OutputCostPerMillion is { } outputRate)
			{
				priced = true;
				var cost = Cost(output, outputRate);
				total += cost;
				components.Add(new CostComponent(
					multipleModels ? $"Output · {name}" : "Output",
					output,
					outputRate,
					cost));
			}

			if (priced) anyEstimated = true;
			else if (!entry.Usage.IsEmpty) unpriced.Add(name);
		}

		if (components.Count == 0) return SessionCost.Unavailable with { UnpricedModels = unpriced };
		return new SessionCost(
			anyEstimated || !anyActual ? SessionCostKind.Estimated : SessionCostKind.Actual,
			total,
			components,
			unpriced);
	}

	private static decimal Cost(long tokens, decimal pricePerMillion) => tokens * pricePerMillion / 1_000_000m;
}

/// <summary>
/// How usage figures are written for a reader. The session list is narrow, so a count is shortened
/// rather than wrapped, and an unknown value is a dash rather than a zero.
/// </summary>
public static class UsageFormat
{
	/// <summary>The dash shown wherever a figure is genuinely unknown.</summary>
	public const string Unknown = "—";

	/// <summary>A token count as "823", "12.4K", "872K", or "1.21M".</summary>
	public static string Tokens(long? value)
	{
		if (value is not { } count) return Unknown;
		if (count < 0) return Unknown;
		if (count < 1_000) return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		if (count < 1_000_000)
		{
			var thousands = count / 1_000d;
			return thousands < 100
				? thousands.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K"
				: Math.Round(thousands).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "K";
		}

		var millions = count / 1_000_000d;
		var format = millions < 10 ? "0.##" : millions < 100 ? "0.#" : "0";
		return millions.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + "M";
	}

	/// <summary>
	/// A fraction as a percentage, as it appears beside a context window. One decimal place is enough to
	/// see a window filling without the figure jittering on every request.
	/// </summary>
	public static string Percent(double? fraction) =>
		fraction is { } value
			? (value * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%"
			: Unknown;

	/// <summary>A full token count with thousands separators, for the breakdown popup.</summary>
	public static string Exact(long? value) =>
		value is { } count ? count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : Unknown;

	/// <summary>
	/// A money amount. Two decimals is what a reader expects, but an amount under a cent is written
	/// with more precision rather than displayed as the "$0.00" this feature exists to avoid.
	/// </summary>
	public static string Money(decimal? value)
	{
		if (value is not { } amount) return Unknown;
		var magnitude = Math.Abs(amount);
		var format = magnitude >= 0.01m || magnitude == 0m ? "0.00" : magnitude >= 0.0001m ? "0.0000" : "0.000000";
		return "$" + amount.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>A per-million rate, as it appears in a cost breakdown line.</summary>
	public static string Rate(decimal? value) =>
		value is { } rate
			? "$" + rate.ToString(rate >= 0.01m || rate == 0m ? "0.00" : "0.0000", System.Globalization.CultureInfo.InvariantCulture) + " / 1M"
			: Unknown;

	/// <summary>The disclaimer shown with every estimated figure, and with no other kind.</summary>
	public const string EstimateDisclaimer =
		"Pricing is an estimate based on the model rates currently configured in SU Harness. Actual provider "
		+ "charges may vary due to provider routing, caching, service tiers, pricing changes, or other "
		+ "provider-specific billing rules.";
}

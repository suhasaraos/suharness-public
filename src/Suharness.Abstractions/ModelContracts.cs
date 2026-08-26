using Microsoft.Agents.AI;

namespace Suharness.Abstractions;


public enum ModelProtocol
{
	OpenAIChatCompletions,
}

/// <summary>
/// How much internal reasoning a model is asked to spend before it answers. The set is closed so a
/// typo cannot reach the provider as a silently accepted value; a model or endpoint that does not
/// understand the parameter is served by leaving the setting unset, which omits the field entirely.
/// </summary>
public enum ReasoningEffortLevel
{
	Minimal,
	Low,
	Medium,
	High,
}

/// <summary>
/// One model on a provider route. Everything past the id is optional: a discovery endpoint may
/// publish a full catalog with prices, or answer with nothing but an id, and both must yield a model
/// the operator can select and use.
/// </summary>
/// <param name="ContextWindow">Usable context in tokens, or null when nothing has said.</param>
/// <param name="InputCostPerMillion">
/// USD per million prompt tokens. Prices are normalized to this unit whatever the upstream reports,
/// so a per-token rate and a per-million rate are never mistaken for each other. Null means unknown;
/// zero means genuinely free, which is a different claim and is preserved as one.
/// </param>
/// <param name="OutputCostPerMillion">USD per million completion tokens, or null when unknown.</param>
/// <param name="CachedInputCostPerMillion">USD per million cached prompt tokens, or null when unknown.</param>
/// <param name="PricingSource">
/// Where the rates came from — a provider id or host for fetched metadata, <see cref="ManualPricingSource"/>
/// for rates an operator typed in. It is what lets a refresh leave hand-entered values alone.
/// </param>
/// <param name="PricingFetchedAt">When the rates were imported, or null for hand-entered ones.</param>
/// <param name="ReasoningEffort">
/// The reasoning budget requested for this model, or null to send no reasoning parameter at all.
/// It is per-model rather than per-route because one provider — OpenRouter most of all — serves
/// reasoning and non-reasoning models side by side, and an endpoint that does not know the
/// parameter rejects the whole request rather than ignoring it. A session may override this for
/// itself; see <see cref="ModelSelection.ReasoningEffort"/>.
/// </param>
public sealed record ModelDescriptor(
	string Id,
	string Name,
	int? ContextWindow = null,
	int? MaxOutputTokens = null,
	decimal? InputCostPerMillion = null,
	decimal? OutputCostPerMillion = null,
	decimal? CachedInputCostPerMillion = null,
	string? PricingSource = null,
	DateTimeOffset? PricingFetchedAt = null,
	ReasoningEffortLevel? ReasoningEffort = null)
{
	/// <summary>The pricing source recorded for rates an operator entered by hand.</summary>
	public const string ManualPricingSource = "manual";

	/// <summary>Whether any rate is configured for this model, which is what a cost estimate needs.</summary>
	public bool HasPricing =>
		InputCostPerMillion is not null || OutputCostPerMillion is not null || CachedInputCostPerMillion is not null;

	/// <summary>Whether an operator entered these rates, in which case a refresh must not touch them.</summary>
	public bool PricingIsManual =>
		string.Equals(PricingSource, ManualPricingSource, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// This model with any metadata it is missing filled in from <paramref name="fetched"/>. A value
	/// already present always wins, so refreshing a catalog can only add what was unknown — an
	/// operator who corrected a rate by hand keeps their correction.
	/// </summary>
	public ModelDescriptor FillGapsFrom(ModelDescriptor fetched)
	{
		var filled = this with
		{
			Name = string.IsNullOrWhiteSpace(Name) ? fetched.Name : Name,
			ContextWindow = ContextWindow ?? fetched.ContextWindow,
			MaxOutputTokens = MaxOutputTokens ?? fetched.MaxOutputTokens,
			InputCostPerMillion = InputCostPerMillion ?? fetched.InputCostPerMillion,
			OutputCostPerMillion = OutputCostPerMillion ?? fetched.OutputCostPerMillion,
			CachedInputCostPerMillion = CachedInputCostPerMillion ?? fetched.CachedInputCostPerMillion,
			ReasoningEffort = ReasoningEffort ?? fetched.ReasoningEffort,
		};

		// The source describes where the rates that are actually in force came from. It only moves to
		// the fetched one when this model had no rates of its own for the fetch to defer to.
		var adopted = !HasPricing && fetched.HasPricing;
		return adopted
			? filled with { PricingSource = fetched.PricingSource, PricingFetchedAt = fetched.PricingFetchedAt }
			: filled with { PricingSource = PricingSource ?? fetched.PricingSource, PricingFetchedAt = PricingFetchedAt ?? fetched.PricingFetchedAt };
	}
}

/// <param name="IdleTimeoutSeconds">
/// How long one model request may go without producing a byte before it is abandoned. This bounds
/// silence, not total duration, so a long answer streamed slowly is never cut off — but a local
/// model that spends minutes on prompt processing before its first token needs a value large
/// enough to cover that gap. Null uses <see cref="DefaultIdleTimeout"/>.
/// </param>
/// <param name="RetryPolicy">
/// How this route absorbs a failed model request. Null uses <see cref="Abstractions.RetryPolicy.Default"/>.
/// A local server that refuses requests while it
/// loads a model into VRAM is the case for setting <see cref="RetryMode.Always"/> here.
/// </param>
/// <param name="MaxToolIterationsPerTurn">
/// How many model round-trips one turn may spend while calling tools. Null uses
/// <see cref="DefaultMaxToolIterationsPerTurn"/>.
/// </param>
/// <param name="MaxToolIterationsPerSegment">
/// How many round-trips one inner agent run may spend before SuHarness checkpoints progress. The
/// turn-wide cap still bounds the total across every productive segment.
/// </param>
/// <param name="AutoContinueUnfinishedTurns">
/// Whether one completed response that announces unfinished work should be persisted and followed
/// by one automatic continuation request. Disabled by default.
/// </param>
public sealed record ModelProviderProfile(
	string Id,
	string DisplayName,
	string BaseUrl,
	ModelProtocol Protocol,
	IReadOnlyList<ModelDescriptor> Models,
	int? IdleTimeoutSeconds = null,
	int? MaxInlineToolBytes = null,
	RetryPolicy? RetryPolicy = null,
	int? MaxToolIterationsPerTurn = null,
	bool AutoContinueUnfinishedTurns = false,
	float? Temperature = null,
	float? TopP = null,
	int? TopK = null,
	int? MaxToolIterationsPerSegment = null)
{
	/// <summary>Remote OpenAI-compatible routes require a credential; loopback servers may be unauthenticated.</summary>
	public bool RequiresApiKey => !new Uri(BaseUrl).IsLoopback;

	/// <summary>The validated retry policy this provider enforces.</summary>
	public RetryPolicy EffectiveRetryPolicy => (RetryPolicy ?? Abstractions.RetryPolicy.Default).Validated();

	/// <summary>
	/// Idle-timeout default. The client library's own default is 100 seconds, which a slow local
	/// model exceeds during prompt processing alone — and because the failed request is then retried,
	/// the whole prompt is reprocessed each time. Ten minutes of complete silence still means the
	/// endpoint is wedged, and the session's Stop button ends a turn before then either way.
	/// </summary>
	public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

	/// <summary>The idle timeout this provider enforces.</summary>
	public TimeSpan IdleTimeout =>
		IdleTimeoutSeconds is > 0 ? TimeSpan.FromSeconds(IdleTimeoutSeconds.Value) : DefaultIdleTimeout;

	/// <summary>
	/// Default model-facing cap on one plain-text tool result, in UTF-8 bytes.
	/// </summary>
	public const int DefaultMaxInlineToolBytes = 50_000;

	/// <summary>
	/// The spill cap this provider enforces, in UTF-8 bytes. How much tool output a model can carry
	/// is a property of the model, so it is configured with the provider that serves it: a local
	/// model with a small usable context wants a far lower value than a frontier one.
	/// </summary>
	public int InlineToolByteCap =>
		MaxInlineToolBytes is > 0 ? MaxInlineToolBytes.Value : DefaultMaxInlineToolBytes;

	/// <summary>Default number of model round-trips one turn may spend on tool calls.</summary>
	public const int DefaultMaxToolIterationsPerTurn = 100;

	/// <summary>Default checkpoint interval inside one tool-calling turn.</summary>
	public const int DefaultMaxToolIterationsPerSegment = 32;

	/// <summary>The tool-calling round-trip budget this provider enforces for one turn.</summary>
	public int ToolIterationCap =>
		MaxToolIterationsPerTurn is > 0 ? MaxToolIterationsPerTurn.Value : DefaultMaxToolIterationsPerTurn;

	/// <summary>The bounded inner-loop cap, never larger than the turn-wide budget.</summary>
	public int ToolSegmentCap => Math.Min(
		ToolIterationCap,
		MaxToolIterationsPerSegment is > 0 ? MaxToolIterationsPerSegment.Value : DefaultMaxToolIterationsPerSegment);
}

public sealed record ConfiguredModelProvider(ModelProviderProfile Profile, bool CredentialConfigured);

/// <param name="ReasoningEffort">
/// The reasoning budget this session asked for, overriding the model catalog's default for as long
/// as the selection stands. Null means the session states no preference and the model's own default
/// applies — which is not the same as asking for no reasoning, so it cannot be expressed by a level.
/// </param>
public sealed record ModelSelection(string Provider, string Model, ReasoningEffortLevel? ReasoningEffort = null);

public enum CodingPersona
{
	Explorer,
	Planner,
	SeniorDeveloper,
	JuniorDeveloper,
	Reviewer,
	Verifier,
}

public sealed record TeamConfiguration(
	bool Enabled,
	Dictionary<CodingPersona, ModelSelection> Assignments)
{
	public static TeamConfiguration Disabled { get; } = new(false, []);

	public ModelSelection? ModelFor(CodingPersona persona) => Assignments.GetValueOrDefault(persona);
}

public sealed record ModelConfigurationSnapshot(
	IReadOnlyList<ConfiguredModelProvider> Providers,
	ModelSelection? DefaultSelection);



public interface IAgentFactory
{
	ValueTask<AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the tool-calling round-trip budget configured for a session's provider.</summary>
public interface IToolIterationPolicyProvider
{
	ValueTask<int> GetMaxIterationsAsync(Guid sessionId, CancellationToken cancellationToken = default);

	ValueTask<int> GetMaxSegmentIterationsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves provider behavior that the runtime, rather than the model client, owns.</summary>
public interface IModelBehaviorPolicyProvider
{
	ValueTask<bool> AutoContinueUnfinishedTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}


public interface IModelConfigurationService
{
	Task<ModelConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
	Task SaveProviderAsync(ModelProviderProfile profile, string? apiKey, CancellationToken cancellationToken = default);
	Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<ModelDescriptor>> DiscoverModelsAsync(ModelProviderProfile draft, string? apiKey, CancellationToken cancellationToken = default);
	Task<ModelSelection?> GetSelectionAsync(Guid sessionId, CancellationToken cancellationToken = default);
	Task SelectAsync(Guid sessionId, ModelSelection selection, CancellationToken cancellationToken = default);
	Task<TeamConfiguration> GetTeamConfigurationAsync(Guid sessionId, CancellationToken cancellationToken = default);
	Task SetTeamConfigurationAsync(Guid sessionId, TeamConfiguration configuration, CancellationToken cancellationToken = default);
}

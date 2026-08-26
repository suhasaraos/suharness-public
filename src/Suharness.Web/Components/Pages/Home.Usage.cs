using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// Token and cost reporting: the per-session spend popover, the context-window indicator,
/// and the per-turn token totals shown beside each turn.
/// </summary>
public partial class Home
{
	// ─── Session usage and the $ cost control ────────────────────────────────

	/// <summary>The id the $ button points its description at while its popover is open.</summary>
	private const string CostPopoverId = "session-cost-popover";

	private Guid? CostSession { get; set; }
	private SessionUsage? CostUsage { get; set; }
	private SessionCost? Cost { get; set; }
	private string CostSessionTitle { get; set; } = string.Empty;

	/// <summary>Whether the popover was opened by a click, so moving the pointer away leaves it up.</summary>
	private bool CostPinned { get; set; }

	/// <summary>
	/// A session's usage: the live counters while a turn is running, and the persisted total
	/// otherwise. Both are lookups — neither reads a transcript.
	/// </summary>
	private SessionUsage? UsageFor(HarnessSession session) => Usage.Get(session.Id) ?? session.Usage;

	/// <summary>The token count shown under a session, or a dash when nothing measured it.</summary>
	private string TokenLabel(HarnessSession session) =>
		UsageFor(session)?.Totals.TotalTokens is { } total ? $"{UsageFormat.Tokens(total)} tokens" : UsageFormat.Unknown;

	/// <summary>
	/// Whether the session on screen has anything to say about cost, which is what the $ control's
	/// muted styling reflects. It stays reachable either way: "why is there no cost here" is a
	/// question the control exists to answer.
	/// </summary>
	private bool ActiveSessionHasUsage =>
		ActiveSession is not null && UsageFor(ActiveSession.Session)?.HasTokens == true;

	/// <summary>
	/// Price the session on screen, on demand. Nothing calculates a cost until the reader asks for
	/// one, so the composer costs a dictionary lookup to render and no arithmetic at all.
	/// </summary>
	private void OpenActiveCost()
	{
		if (ActiveSession is null || CostSession == ActiveSession.Session.Id) return;
		var session = ActiveSession.Session;
		CostSession = session.Id;
		CostSessionTitle = session.Title;
		CostUsage = UsageFor(session);
		Cost = SessionCosting.Calculate(CostUsage, PricingFor);
		CostPinned = false;
	}

	private void PinActiveCost()
	{
		OpenActiveCost();
		CostPinned = !CostPinned;
		if (!CostPinned) CloseCost();
	}

	/// <summary>Pointer or focus left the control. A pinned popover stays until it is dismissed.</summary>
	private void LeaveCost()
	{
		if (!CostPinned) CloseCost();
	}

	private void CloseCost()
	{
		CostSession = null;
		CostUsage = null;
		Cost = null;
		CostPinned = false;
	}

	// ─── Token and context usage ─────────────────────────────────

	private const string ContextPopoverId = "session-context-popover";

	private bool ContextOpen { get; set; }
	private bool ContextPinned { get; set; }

	/// <summary>
	/// All three levels of accounting for the session on screen. The live counters are preferred, and a
	/// session this process has not run falls back to what was persisted for it — which is why the
	/// context figure survives a restart, and why the turn section is the one part that does not.
	/// </summary>
	/// <remarks>
	/// Read during render. It costs a dictionary lookup and one small record, and never touches a
	/// transcript, so a streaming turn re-reading it on every frame is not a cost worth caching around.
	/// </remarks>
	private UsageSnapshot? UsageView => ActiveSession is { } transcript
		? Usage.Snapshot(transcript.Session.Id) ?? UsageSnapshot.FromPersisted(transcript.Session.Usage)
		: null;

	/// <summary>Whether the prompt size of the last request is known, which is what the control shows.</summary>
	private bool ContextUsageKnown => UsageView?.Context is { HasInput: true };

	/// <summary>
	/// The compact figure in the control: the last prompt against the window that has to hold it. A model
	/// whose window nothing has published shows the prompt alone rather than a fraction of a guess.
	/// </summary>
	private string ContextLabel
	{
		get
		{
			if (UsageView?.Context is not { InputTokens: { } input } context) return UsageFormat.Unknown;
			var used = UsageFormat.Tokens(input);
			return context.ContextWindow is > 0 ? $"{used} / {UsageFormat.Tokens(context.ContextWindow)}" : used;
		}
	}

	/// <summary>Which model the figures describe, since a session may have used more than one.</summary>
	private string ContextModelLabel => UsageView?.Context?.Model ?? ActiveSession?.Session.Title ?? string.Empty;

	/// <summary>
	/// Why no context figure is shown. A session that has not called a model and one whose provider
	/// reported nothing are different situations, and only the second is worth explaining.
	/// </summary>
	private string ContextUnavailableReason => UsageView?.LastCall is null
		? "No model request has been made in this session yet."
		: "This provider reported no prompt token count for the last request.";

	private void OpenContext() => ContextOpen = true;

	private void PinContext()
	{
		ContextPinned = !ContextPinned;
		ContextOpen = ContextPinned || ContextOpen;
		if (!ContextPinned) CloseContext();
	}

	private void LeaveContext()
	{
		if (!ContextPinned) CloseContext();
	}

	private void CloseContext()
	{
		ContextOpen = false;
		ContextPinned = false;
	}

	private void ContextKeyDown(KeyboardEventArgs eventArgs)
	{
		if (eventArgs.Key == "Escape") CloseContext();
	}

	private void CostKeyDown(KeyboardEventArgs eventArgs)
	{
		if (eventArgs.Key == "Escape") CloseCost();
	}

	/// <summary>
	/// Tokens spent per turn, mapped to the message the count is shown against. A turn opens at a
	/// user prompt or an automatic goal round and closes at the next one, and its total lands on the
	/// last assistant message it produced — the one whose timestamp ends the turn.
	/// </summary>
	/// <remarks>
	/// Derived from the usage lines already on the transcript rather than from anything the view
	/// counts itself, and cached against the transcript it was built from, so scrolling a long
	/// session does not re-read it. Requests are logged before the response they belong to is
	/// persisted, so summing within the span between prompts attributes each one to its own turn.
	/// </remarks>
	private IReadOnlyDictionary<Guid, long> TurnTokens
	{
		get
		{
			if (ActiveSession is not { } transcript) return System.Collections.Immutable.ImmutableDictionary<Guid, long>.Empty;
			if (TurnTokenSession == transcript.Session.Id && TurnTokenEventCount == transcript.Events.Count) return TurnTokenTotals;
			TurnTokenTotals = ComputeTurnTokens(transcript.Events);
			TurnTokenSession = transcript.Session.Id;
			TurnTokenEventCount = transcript.Events.Count;
			return TurnTokenTotals;
		}
	}

	private Guid TurnTokenSession { get; set; }
	private int TurnTokenEventCount { get; set; } = -1;
	private IReadOnlyDictionary<Guid, long> TurnTokenTotals { get; set; } =
		System.Collections.Immutable.ImmutableDictionary<Guid, long>.Empty;

	private static IReadOnlyDictionary<Guid, long> ComputeTurnTokens(IReadOnlyList<SessionEvent> events)
	{
		var totals = new Dictionary<Guid, long>();
		long spent = 0;
		var hasUsage = false;
		Guid? lastAssistantMessage = null;

		void Close()
		{
			if (hasUsage && lastAssistantMessage is { } anchor) totals[anchor] = spent;
			spent = 0;
			hasUsage = false;
			lastAssistantMessage = null;
		}

		foreach (var item in events)
		{
			if (item.Type is "message" && item.Role == "user" && item.ModelMessageId is null || item.Type == "goal_round")
			{
				Close();
				continue;
			}
			if (item.Type == SessionUsageEvent.Type)
			{
				if (ReadInvocation(item)?.Usage.TotalTokens is { } tokens)
				{
					spent += tokens;
					hasUsage = true;
				}
				continue;
			}
			if (item.Type == "message" && item.Role == "assistant") lastAssistantMessage = item.Id;
		}
		Close();
		return totals;
	}

	private static ModelInvocationUsage? ReadInvocation(SessionEvent item)
	{
		try { return JsonSerializer.Deserialize<ModelInvocationUsage>(item.Content, UsageJson); }
		catch (JsonException) { return null; }
	}

	private static readonly JsonSerializerOptions UsageJson = new(JsonSerializerDefaults.Web);

	/// <summary>What a message's timestamp adds about the turn it ended, or nothing when unmeasured.</summary>
	private string? TurnTokenSuffix(SessionEvent item) =>
		TurnTokens.TryGetValue(item.Id, out var tokens) ? $" · {UsageFormat.Tokens(tokens)} tokens" : null;

	/// <summary>The rates configured for one model, or null when it is no longer configured.</summary>
	private ModelDescriptor? PricingFor(string? providerId, string? modelId) =>
		modelId is null
			? null
			: ModelSnapshot.Providers
				.FirstOrDefault(item => item.Profile.Id == providerId)?.Profile.Models
				.FirstOrDefault(model => model.Id == modelId);

	private string CostHeading => Cost?.Kind switch
	{
		SessionCostKind.Actual => "Actual provider cost",
		SessionCostKind.Estimated => "Estimated session cost",
		_ => "Cost unavailable",
	};

	/// <summary>
	/// Why no figure is shown. A session whose models simply have no rates configured is told exactly
	/// that, because it is fixable; a session that has not called a model yet is told so rather than
	/// being shown a fabricated zero.
	/// </summary>
	private string CostUnavailableReason =>
		Cost is { UnpricedModels.Count: > 0 } unpriced
			? $"No pricing is configured for {string.Join(", ", unpriced.UnpricedModels)}."
			: CostUsage?.HasTokens == true
				? "Configure model pricing to enable cost estimates for this session."
				: "No model usage has been recorded for this session yet.";
}

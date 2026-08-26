using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Text;

namespace Suharness.Infrastructure;

/// <summary>
/// When a session compacts and how much of it survives. The defaults provide enough retained
/// context for continuity while reserving room for the summary and the next model response.
/// </summary>
public sealed record CompactionPolicy(
	double ThresholdRatio = 0.8,
	double RetainRatio = 0.16,
	int SummaryMaxTokens = 8192)
{
	/// <summary>
	/// Context window assumed for a model whose endpoint does not advertise one. The window is normally
	/// from configuration an operator writes; here the model list comes from discovery, and a model
	/// that reports no window is in practice a local server. Those are the small models that need
	/// compaction most, so leaving it off for them meant it never ran where it mattered. Compacting a
	/// roomier model earlier than necessary costs one summarization; not compacting a small one
	/// crowds the model out of its own context, which is the failure this exists to prevent.
	/// </summary>
	public const int FallbackContextWindow = 32_000;

	/// <summary>Pressure at which a session compacts, in tokens.</summary>
	public int ThresholdTokens(int contextWindow) => (int)(contextWindow * ThresholdRatio);

	/// <summary>Recent conversation kept out of the checkpoint, in tokens.</summary>
	public int RetainTokens(int contextWindow) => (int)(contextWindow * RetainRatio);
}

/// <summary>
/// The pure parts of compaction: what a span costs, and where the conversation may be cut. Kept
/// separate from the service so both can be tested without a model.
/// </summary>
public static class CompactionPlanner
{
	/// <summary>
	/// Characters per token. Production deployments may use a real token meter; a harness talking to
	/// arbitrary OpenAI-compatible endpoints has no tokenizer it can trust. Measured against a real
	/// session — 199,428 replayed characters that KoboldCpp counted as 64,960 tokens — the usual
	/// four-characters-per-token rule read 23% low, because transcripts are mostly code and JSON.
	/// </summary>
	private const double CharactersPerToken = 3.5;

	/// <summary>
	/// Tokens every request carries before any conversation: the system prompt and the tool schemas.
	/// Measured at roughly 10,700 characters for the standard preset. The transcript estimate cannot
	/// see them, and leaving them out is a large fixed under-count on exactly the small-context
	/// models where the margin matters.
	/// </summary>
	public const int RequestOverheadTokens = 3_000;

	/// <summary>Estimated tokens for a piece of text.</summary>
	public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / CharactersPerToken);

	/// <summary>Estimated tokens for one event as it will be replayed, including its role framing.</summary>
	public static int EstimateTokens(SessionEvent item) => EstimateTokens(item.Content) + 4;

	/// <summary>Estimated tokens for a whole replayed span, excluding fixed request overhead.</summary>
	public static int EstimateTokens(IEnumerable<SessionEvent> events) => events.Sum(EstimateTokens);

	/// <summary>
	/// What one request for this transcript is estimated to cost, overhead included. Still reads a
	/// little low on code-heavy sessions; the threshold's 20% headroom absorbs the remainder.
	/// </summary>
	public static int EstimateRequestTokens(IEnumerable<SessionEvent> events) =>
		EstimateTokens(events) + RequestOverheadTokens;

	/// <summary>
	/// The last event a checkpoint may cover, or null when there is nothing worth compacting.
	/// Walks back from the end until the retention budget is met, then keeps walking back until the
	/// cut no longer splits a tool call from its result — a call whose result was compacted away
	/// leaves the model holding a dangling call, which providers reject outright.
	/// </summary>
	public static SessionEvent? SelectCutoff(IReadOnlyList<SessionEvent> events, int retainTokens)
	{
		var replayable = events.Where(Replayable).ToList();
		if (replayable.Count == 0) return null;

		var accumulated = 0;
		var keepFrom = replayable.Count;
		for (var index = replayable.Count - 1; index >= 0; index--)
		{
			accumulated += EstimateTokens(replayable[index]);
			keepFrom = index;
			if (accumulated >= retainTokens) break;
		}
		if (keepFrom == 0) return null;

		while (keepFrom > 0 && !BalancedBefore(replayable, keepFrom)) keepFrom--;
		if (keepFrom == 0) return null;

		return replayable[keepFrom - 1];
	}

	/// <summary>Events that reach the model, and so are the ones worth pricing and cutting.</summary>
	private static bool Replayable(SessionEvent item) =>
		item.Type is "message" or "tool_call" or "tool_result" or "inbox" or "goal_round" or "compaction";

	/// <summary>
	/// Whether cutting immediately before <paramref name="index"/> leaves every tool call answered.
	/// Balance is counted over the prefix: each call opens one, each result closes one.
	/// </summary>
	private static bool BalancedBefore(IReadOnlyList<SessionEvent> events, int index)
	{
		var open = 0;
		for (var scan = 0; scan < index; scan++)
		{
			if (events[scan].Type == "tool_call") open++;
			else if (events[scan].Type == "tool_result") open--;
		}
		return open == 0;
	}
}

/// <summary>
/// Replay-aware compaction for preserving the useful tail of a conversation. When the
/// replayed transcript passes the pressure threshold, the span before the retained tail is condensed
/// into a checkpoint and recorded as one event; later turns replay the checkpoint in place of that
/// span. The summarizing call reuses the conversation's own agent — its system prompt and tools —
/// without synthetic messages, so the auxiliary request stays a prefix of the conversation and the
/// provider's cache is reused rather than invalidated.
/// </summary>
public sealed class CompactionService(
	IModelConfigurationService models,
	CompactionPolicy? policy = null) : ICompactionService
{
	private readonly CompactionPolicy _policy = policy ?? new CompactionPolicy();

	public async Task<SessionEvent?> MaybeCompactAsync(
		Guid sessionId,
		IReadOnlyList<SessionEvent> events,
		AIAgent agent,
		CancellationToken cancellationToken = default)
	{
		var contextWindow = await ContextWindowAsync(sessionId, cancellationToken);
		if (contextWindow is not > 0) return null;

		var live = Live(events);
		if (CompactionPlanner.EstimateRequestTokens(live) < _policy.ThresholdTokens(contextWindow.Value)) return null;

		return await CompactAsync(sessionId, live, _policy.RetainTokens(contextWindow.Value), agent, cancellationToken);
	}

	/// <summary>
	/// Compact after the provider itself rejected the request as too large. The threshold check is
	/// skipped — the provider has already answered the question the threshold was estimating — and
	/// the retained tail drops to nothing, so the reduction is as large as the transcript allows.
	/// Retaining a tail here would risk compacting, still overflowing, and retrying against a
	/// transcript that never got small enough.
	/// </summary>
	public Task<SessionEvent?> CompactForOverflowAsync(
		Guid sessionId,
		IReadOnlyList<SessionEvent> events,
		AIAgent agent,
		CancellationToken cancellationToken = default) =>
		CompactAsync(sessionId, Live(events), retainTokens: 0, agent, cancellationToken);

	/// <summary>
	/// Summarize everything before the retention budget into one checkpoint event. The caller has
	/// already decided that compaction is warranted; this decides where the cut falls and what the
	/// checkpoint says.
	/// </summary>
	private async Task<SessionEvent?> CompactAsync(
		Guid sessionId,
		List<SessionEvent> live,
		int retainTokens,
		AIAgent agent,
		CancellationToken cancellationToken)
	{
		var cutoff = CompactionPlanner.SelectCutoff(live, retainTokens);
		if (cutoff is null) return null;

		var region = live.TakeWhile(item => item.Id != cutoff.Id).Append(cutoff).ToList();
		var summary = await SummarizeAsync(region, agent, cancellationToken);
		if (string.IsNullOrWhiteSpace(summary)) return null;

		return new SessionEvent(
			Guid.NewGuid(),
			sessionId,
			"compaction",
			"system",
			Checkpoint(summary),
			DateTimeOffset.UtcNow,
			CompactedThroughId: cutoff.Id);
	}

	/// <summary>The events a turn would actually replay, with any prior checkpoint's span removed.</summary>
	private static List<SessionEvent> Live(IReadOnlyList<SessionEvent> events)
	{
		for (var index = events.Count - 1; index >= 0; index--)
		{
			if (events[index].Type != "compaction" || events[index].CompactedThroughId is not { } boundary) continue;
			var position = 0;
			while (position < events.Count && events[position].Id != boundary) position++;
			if (position == events.Count) break;
			return [events[index], .. events.Skip(position + 1).Where(item => item.Type != "compaction")];
		}
		return [.. events];
	}

	private async Task<int?> ContextWindowAsync(Guid sessionId, CancellationToken cancellationToken)
	{
		var selection = await models.GetSelectionAsync(sessionId, cancellationToken);
		if (selection is null) return null;
		var snapshot = await models.GetSnapshotAsync(cancellationToken);
		var configured = snapshot.Providers
			.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile.Models
			.SingleOrDefault(model => model.Id == selection.Model)?.ContextWindow;
		return configured is > 0 ? configured : CompactionPolicy.FallbackContextWindow;
	}

	/// <summary>
	/// Ask the conversation's own agent to condense the region. The region is replayed as ordinary
	/// history and the directive arrives as the final user message, which is what keeps the request
	/// a genuine prefix of the conversation.
	/// </summary>
	private async Task<string?> SummarizeAsync(
		IReadOnlyList<SessionEvent> region,
		AIAgent agent,
		CancellationToken cancellationToken)
	{
		try
		{
			var history = HarnessCompaction.ReplayForSummary(region);
			history.Add(new ChatMessage(ChatRole.User, HarnessCompaction.Instruction));
			var session = await agent.CreateSessionAsync(cancellationToken);
			var response = await agent.RunAsync(
				history,
				session,
				new ChatClientAgentRunOptions(new ChatOptions
				{
					MaxOutputTokens = _policy.SummaryMaxTokens,
					// The conversation's own tool schemas stay on the request: an
					// auxiliary call that is a genuine prefix of the last one reuses the provider's warm
					// cache instead of invalidating it, which on a slow local model is most of the cost.
					// Tool *choice* is withheld instead, so the summarizer can describe but not act.
					ToolMode = ChatToolMode.None,
				}),
				cancellationToken);

			// A summary cut off at the token cap is an incomplete checkpoint. Landing one would delete
			// the region it failed to describe, so a truncated summarization fails closed.
			return response.FinishReason == ChatFinishReason.Length ? null : response.Text;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// Compaction is an optimization. A failed attempt leaves the transcript exactly as it was.
			return null;
		}
	}

	private static string Checkpoint(string summary) =>
		$"{HarnessCompaction.Preamble}\n\n{HarnessCompaction.OpenTag}\n{summary.Trim()}\n{HarnessCompaction.CloseTag}";
}

/// <summary>The stable wording used to identify a compacted conversation checkpoint.</summary>
public static class HarnessCompaction
{
	public const string OpenTag = "<compacted-summary>";

	public const string CloseTag = "</compacted-summary>";

	/// <summary>Framing that makes the replacement message read as established context.</summary>
	public static string Preamble => PromptCatalog.Get("compaction/preamble");

	/// <summary>The compaction directive delivered as the final user message after the region.</summary>
	public static string Instruction => PromptCatalog.Render(
		"compaction/instruction",
		PromptNewline.Lf,
		("openTag", OpenTag));

	/// <summary>
	/// Replay the region for the summarizer as plain text. Tool calls and results are flattened
	/// rather than replayed as function content: the summarizing request carries no tools, and a
	/// call whose result sits outside the region would otherwise dangle.
	/// </summary>
	public static List<ChatMessage> ReplayForSummary(IReadOnlyList<SessionEvent> region)
	{
		var history = new List<ChatMessage>();
		foreach (var item in region)
		{
			if (item.Content.Length == 0) continue;
			var text = item.Type switch
			{
				"tool_call" => $"[tool call: {item.ToolName}] {item.Content}",
				"tool_result" => $"[tool result] {item.Content}",
				_ => item.Content,
			};
			history.Add(new ChatMessage(item.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, text));
		}
		return history;
	}
}

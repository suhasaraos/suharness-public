using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Runtime.CompilerServices;

namespace Suharness.Infrastructure;

/// <summary>
/// Records the token usage of every model request made through it, attributed to the session, the
/// model that served it, and the turn it was made from.
/// </summary>
/// <remarks>
/// This sits underneath the function-calling loop, so it sees each model round-trip separately
/// rather than the loop's aggregate. That placement is the whole design: a turn that calls tools
/// eleven times is eleven requests the provider billed for, a retry is another one, and the
/// summarizing request compaction makes is another still. Counting the aggregate the agent returns
/// would collapse them, and counting a persisted response would count nothing at all for an attempt
/// that was thrown away after the provider had already charged for it.
/// </remarks>
/// <param name="contextWindow">
/// The window configured for this model, recorded against each request rather than looked up when the
/// figure is displayed. A session that changes model mid-conversation would otherwise have its earlier
/// requests measured against a window that never held them.
/// </param>
public sealed class UsageTrackingChatClient(
	IChatClient inner,
	ISessionUsageTracker tracker,
	Guid sessionId,
	string? providerId,
	string? modelId,
	IAgentLifecycle? lifecycle = null,
	int? contextWindow = null,
	int? maxOutputTokens = null) : DelegatingChatClient(inner)
{
	public override async Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		// Materialized once: the estimate below has to read the same request that was sent, and a
		// caller is entitled to hand this a sequence that can only be enumerated once.
		var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
		var response = await base.GetResponseAsync(request, options, cancellationToken);
		Record(
			UsageNormalizer.Read(response.Usage, response.RawRepresentation),
			response.FinishReason?.Value,
			response.ResponseId,
			request,
			options);
		return response;
	}

	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var request = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

		// Usage arrives on a trailing chunk, so it is collected as the stream passes through and
		// reported once at the end. Recording per chunk would multiply one request into hundreds.
		UsageDetails? usage = null;
		object? raw = null;
		string? finishReason = null;
		string? responseId = null;
		var answered = false;
		try
		{
			await foreach (var update in base.GetStreamingResponseAsync(request, options, cancellationToken))
			{
				// The reason and the id arrive on their own chunks, ahead of the usage one, so each is
				// kept as it passes rather than read off whichever chunk happened to carry the counts.
				finishReason = update.FinishReason?.Value ?? finishReason;
				responseId ??= update.ResponseId;
				foreach (var content in update.Contents)
				{
					if (content is not UsageContent reported) continue;
					usage = usage is null ? reported.Details : Merge(usage, reported.Details);
					raw = content.RawRepresentation ?? update.RawRepresentation ?? raw;
				}
				yield return update;
			}
			answered = true;
		}
		finally
		{
			// A stream abandoned partway — an interrupted turn — still consumed whatever the provider
			// had already reported, so what was seen is recorded rather than discarded. A request that
			// failed before reporting anything is not recorded at all: it is not an invocation whose
			// usage went unreported, it is one that never produced a response.
			if (answered || usage is not null)
				Record(UsageNormalizer.Read(usage, raw), finishReason, responseId, request, options);
		}
	}

	/// <summary>
	/// Fold two usage blocks from the same stream together, into a record of our own rather than
	/// into the provider's — the caller of the stream may still be holding it.
	/// </summary>
	private static UsageDetails Merge(UsageDetails first, UsageDetails second)
	{
		var merged = new UsageDetails();
		merged.Add(first);
		merged.Add(second);
		return merged;
	}

	private void Record(
		TokenUsage usage,
		string? finishReason,
		string? responseId,
		IReadOnlyList<ChatMessage> request,
		ChatOptions? options) =>
		tracker.Record(sessionId, new ModelInvocationUsage(
			providerId,
			modelId,
			usage,
			DateTimeOffset.UtcNow,
			// The turn number is stamped by the tracker, which owns the boundary. The step is the agent
			// loop's own round-trip counter, which only the lifecycle knows, and is carried here so a
			// usage line can be lined up against the loop diagnostics for the same request.
			StepNumber: lifecycle?.Current(sessionId)?.StepNumber ?? 0,
			ResponseId: responseId,
			ContextWindow: contextWindow,
			MaxOutputTokens: maxOutputTokens,
			FinishReason: finishReason,
			// Measured only when the provider reported no prompt count, and kept apart from the usage
			// record so it can never be summed into a total or priced as though it were measured.
			EstimatedInputTokens: usage.InputTokens is null ? RequestTokenEstimator.Estimate(request, options) : null));
}

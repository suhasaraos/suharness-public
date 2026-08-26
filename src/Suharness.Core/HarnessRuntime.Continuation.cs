using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

/// <summary>
/// Deciding whether a response actually finished. A model that stops mid-task usually says so in
/// its last sentence — it announces the next action rather than delivering a result — and a turn
/// that ran out of tool iterations stops without saying anything at all. These heuristics read
/// those endings so the harness can continue once rather than hand back a half-finished turn.
/// </summary>
public sealed partial class HarnessRuntime
{
	/// <summary>A short name for a failure class, for the retry notice the reader sees.</summary>
	private static string FailureLabel(ModelFailureCode code) => code switch
	{
		ModelFailureCode.Transport => "connection failure",
		ModelFailureCode.Timeout => "timeout",
		ModelFailureCode.Server => "server error",
		ModelFailureCode.RateLimit => "rate limit",
		ModelFailureCode.EmptyResponse => "empty response",
		ModelFailureCode.ContextWindowExceeded => "context window exceeded",
		ModelFailureCode.Auth => "authentication failure",
		ModelFailureCode.Quota => "quota exhausted",
		ModelFailureCode.InvalidRequest => "rejected request",
		_ => "error",
	};

	/// <summary>
	/// Whether the turn used up its tool-calling budget, and what to say about it. On its final
	/// iteration the function-calling loop withholds the tool definitions so the model has to answer
	/// in prose. That answer looks like any other, which is the problem: without a notice the reader
	/// cannot tell a finished task from one abandoned at the budget with a confident summary.
	/// </summary>
	/// <remarks>
	/// Each dispatched tool call opens the step of the request that follows it, so a step numbered at
	/// the budget is exactly the evidence that the final, tool-less request was the one that answered.
	/// </remarks>
	private static string? ExhaustedToolIterations(IAgentLifecycle? lifecycle, Guid sessionId, int maxToolIterations)
	{
		if (lifecycle is null || maxToolIterations <= 1) return null;
		return lifecycle.MaxStep(sessionId) >= maxToolIterations
			? $"This turn reached its limit of {maxToolIterations} model round-trips, so the last "
				+ "response was produced without tools available. Treat any claim of completed work in it "
				+ "with suspicion, and send a follow-up such as \"continue\" to carry on."
			: null;
	}

	private static bool ExhaustedSegment(
		IAgentLifecycle? lifecycle,
		Guid sessionId,
		int openingStep,
		int segmentIterations) =>
		lifecycle is not null
		&& segmentIterations > 1
		&& lifecycle.MaxStep(sessionId) >= openingStep + segmentIterations - 1;

	/// <summary>
	/// What to tell the reader about how a turn ended, or null when it ended the ordinary way. A turn
	/// that stops early otherwise leaves nothing in the transcript at all: the agent loop ends as soon
	/// as the model answers without calling a tool, so a model that abandons a half-finished task looks
	/// exactly like one that crashed. Naming the reason is what separates the two.
	/// </summary>
	private static string? StopNotice(AgentResponse response)
	{
		var reason = response.FinishReason;
		if (reason == ChatFinishReason.Length)
		{
			return "The model stopped because it reached its output-token limit, so this response is cut off. "
				+ "Ask it to continue, or raise the model's maximum output tokens.";
		}
		if (reason == ChatFinishReason.ContentFilter)
		{
			return "The model provider's content filter stopped this response before it finished.";
		}

		// The loop ends the moment a reply carries no tool call. That is the normal ending after the
		// model has answered, but when it is still narrating work it has not done, the turn is over
		// and nothing else will happen unless the reader asks again.
		return IsUnfinishedToolTurn(response)
			? "The model ended its turn while describing work it had not done yet, so the run stopped here. "
				+ "Send a short follow-up such as \"continue\" to pick it back up."
			: null;
	}

	private static bool IsUnfinishedToolTurn(AgentResponse response)
	{
		var messages = response.Messages;
		var usedTools = messages.Any(message => message.Contents.OfType<FunctionCallContent>().Any());
		var endedWithToolCall = messages.Count > 0
			&& messages[^1].Contents.OfType<FunctionCallContent>().Any();
		return usedTools && !endedWithToolCall && ReadsAsUnfinished(response.Text);
	}

	private static bool ShouldContinueUnfinished(AgentResponse response) =>
		response.FinishReason == ChatFinishReason.Length || IsUnfinishedToolTurn(response);

	/// <summary>
	/// Phrases that open an announcement of work the model is about to do: "Let me check the other
	/// file", "Now I'll update the config".
	/// </summary>
	private static readonly string[] UnfinishedOpenings =
	[
		"let me ", "let's ", "i'll ", "i will ", "i'm going to ", "i am going to ", "continuing ",
	];

	/// <summary>
	/// Phrases that make the same wording mean the opposite. The first group is contingent on the
	/// reader replying — an offer, not work in progress — and the second is the model saying plainly
	/// that it is parking the work. Both routinely contain "I'll".
	/// </summary>
	private static readonly string[] FinishedHedges =
	[
		"if you", "if that", "if there", "if anything", "unless you", "when you", "once you",
		"let me know", "just tell me", "tell me ", "would you like", "want me to", "shall i", "should i",
		"i'll stop", "i will stop", "i'll wait", "i will wait", "i'll hold", "i'll pause", "i'll leave",
		"i'll stand by", "i'll be here",
	];

	/// <summary>
	/// Leading discourse markers and list bullets, stripped so the announcement itself can be matched
	/// at the start of the sentence.
	/// </summary>
	private static readonly string[] LeadIns =
	[
		"now, ", "now ", "next, ", "next ", "then, ", "then ", "so, ", "so ", "also, ",
		"finally, ", "ok, ", "okay, ", "alright, ",
	];

	/// <summary>
	/// Whether a closing message reads as an announcement of the next step rather than an answer.
	/// A model that trails off mid-plan writes "let me...", "now I'll..." and then stops.
	/// </summary>
	/// <remarks>
	/// Only the final sentence is examined, and the phrase has to open it. Scanning the whole tail for
	/// the phrase anywhere reported a stalled turn for ordinary sign-offs — "Understood, I'll stop
	/// here" and "tell me the server name and I'll write it out" both contain "I'll" while saying the
	/// model is finished and waiting. A closing question is treated the same way: a model asking the
	/// reader something is waiting on them, not abandoning a step.
	/// </remarks>
	internal static bool ReadsAsUnfinished(string text)
	{
		var trimmed = text.TrimEnd();
		if (trimmed.Length == 0 || trimmed.EndsWith('?')) return false;

		var sentence = LastSentence(trimmed);
		if (sentence.Length == 0) return false;
		if (FinishedHedges.Any(hedge => sentence.Contains(hedge, StringComparison.OrdinalIgnoreCase))) return false;

		var announcement = StripLeadIn(sentence);
		return UnfinishedOpenings.Any(opening => announcement.StartsWith(opening, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// The final sentence of the final non-empty line. A terminator only ends a sentence when
	/// whitespace follows it, so a file name such as "mcp.json" or a version such as "3.5" does not
	/// split one.
	/// </summary>
	private static string LastSentence(string text)
	{
		var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (lines.Length == 0) return string.Empty;

		var line = lines[^1];
		for (var index = line.Length - 2; index > 0; index--)
		{
			if (line[index] is ('.' or '!' or '?') && char.IsWhiteSpace(line[index + 1]))
				return line[(index + 1)..].TrimStart();
		}
		return line;
	}

	/// <summary>Drop a bullet and one leading discourse marker, so the announcement starts the text.</summary>
	private static string StripLeadIn(string sentence)
	{
		var text = sentence.TrimStart('-', '*', '•', ' ', '\t');
		foreach (var marker in LeadIns)
		{
			if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) return text[marker.Length..].TrimStart();
		}
		return text;
	}
}

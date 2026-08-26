using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Measures what one model request carried, for the case where the provider did not say.
/// </summary>
/// <remarks>
/// This is a fallback and is treated as one everywhere it surfaces. The harness has no tokenizer it
/// can trust for an arbitrary OpenAI-compatible endpoint, so what it produces is labelled an estimate,
/// is never added to a session or turn total, and is never priced. It exists because a reader whose
/// local server reports no prompt count is better served by "roughly 40K of 32K, estimated" — which
/// explains why the model has started forgetting things — than by a dash.
/// </remarks>
/// <remarks>
/// The measurement is taken over the request as it was actually assembled: the messages handed to the
/// client, the instructions the harness set, and the tool schemas it published. That is the same
/// content the provider would have counted, which is what makes the estimate worth showing at all —
/// estimating from the transcript instead would miss the system prompt and the tool catalog, and the
/// tool catalog alone runs to tens of thousands of tokens once an MCP server is connected.
/// </remarks>
public static class RequestTokenEstimator
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	/// <summary>Tokens added for the framing around one message or one tool schema.</summary>
	private const int FramingTokens = 4;

	/// <summary>
	/// What this request is estimated to have carried, in tokens. Never throws: an estimate that
	/// cannot be taken is worth less than the turn it would otherwise fail.
	/// </summary>
	public static long Estimate(IEnumerable<ChatMessage> messages, ChatOptions? options)
	{
		try
		{
			long total = 0;
			foreach (var message in messages) total += EstimateMessage(message);
			if (options?.Instructions is { Length: > 0 } instructions)
				total += CompactionPlanner.EstimateTokens(instructions);
			foreach (var tool in options?.Tools ?? []) total += EstimateTool(tool);
			return total;
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			return 0;
		}
	}

	private static long EstimateMessage(ChatMessage message)
	{
		long total = FramingTokens;
		foreach (var content in message.Contents)
		{
			total += content switch
			{
				TextContent text => CompactionPlanner.EstimateTokens(text.Text),
				TextReasoningContent reasoning => CompactionPlanner.EstimateTokens(reasoning.Text),
				FunctionCallContent call =>
					CompactionPlanner.EstimateTokens(call.Name) + EstimateArguments(call.Arguments) + FramingTokens,
				FunctionResultContent result => EstimateValue(result.Result) + FramingTokens,
				// Images are billed by tile geometry rather than by text length, and the arithmetic
				// differs per provider. Counting their bytes as characters would be worse than leaving
				// them out, so an estimate for a request carrying one reads low and is labelled an
				// estimate either way.
				_ => 0,
			};
		}
		return total;
	}

	private static long EstimateArguments(IDictionary<string, object?>? arguments)
	{
		if (arguments is not { Count: > 0 }) return 0;
		long total = 0;
		foreach (var (key, value) in arguments)
			total += CompactionPlanner.EstimateTokens(key) + EstimateValue(value) + 2;
		return total;
	}

	/// <summary>
	/// A value's size in tokens. The common shapes are measured from their own text rather than
	/// serialized, because a tool result in this harness is a string and re-encoding a large one to
	/// count it would cost more than the request it is measuring.
	/// </summary>
	private static long EstimateValue(object? value) => value switch
	{
		null => 0,
		string text => CompactionPlanner.EstimateTokens(text),
		JsonElement { ValueKind: JsonValueKind.String } json => CompactionPlanner.EstimateTokens(json.GetString() ?? string.Empty),
		JsonElement json => CompactionPlanner.EstimateTokens(json.GetRawText()),
		bool or int or long or double or float or decimal => 2,
		_ => Serialized(value),
	};

	private static long Serialized(object value)
	{
		try { return CompactionPlanner.EstimateTokens(JsonSerializer.Serialize(value, JsonOptions)); }
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			return CompactionPlanner.EstimateTokens(value.ToString() ?? string.Empty);
		}
	}

	/// <summary>
	/// One published tool's schema. This is the part a transcript-based estimate cannot see and the
	/// part that dominates on a session with MCP servers connected.
	/// </summary>
	private static long EstimateTool(AITool tool)
	{
		var total = CompactionPlanner.EstimateTokens(tool.Name)
			+ CompactionPlanner.EstimateTokens(tool.Description)
			+ FramingTokens;
		if (tool is AIFunction function) total += CompactionPlanner.EstimateTokens(function.JsonSchema.GetRawText());
		return total;
	}
}

using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Turns whatever a provider reported about one request into the harness's single usage record.
/// </summary>
/// <remarks>
/// The client library already maps the standard OpenAI usage block, so the work here is what it
/// cannot express. Two things: the counts a provider never sent, which the typed model flattens into
/// zeros because its fields are not nullable; and the fields it has no place for at all — cache-write
/// counts, and the billed cost OpenRouter returns alongside the token counts. The first is repaired
/// by reading zeros in context, the second by inspecting the provider's own payload. Anything
/// unreadable stays unknown, because an unmeasured request must never be recorded as a measured zero.
/// </remarks>
public static class UsageNormalizer
{
	/// <summary>Additional-count keys providers use for cache-write (cache-creation) tokens.</summary>
	private static readonly string[] CacheWriteKeys =
	[
		"cache_creation_input_tokens",
		"cacheCreationInputTokens",
		"cache_write_input_tokens",
		"InputTokenDetails.CacheWriteTokenCount",
		"cache_creation_tokens",
	];

	/// <summary>Additional-count keys providers use for cached (cache-read) prompt tokens.</summary>
	private static readonly string[] CacheReadKeys =
	[
		"cache_read_input_tokens",
		"cached_tokens",
		"InputTokenDetails.CachedTokenCount",
	];

	/// <summary>Usage-object fields that carry an authoritative billed cost for the request.</summary>
	private static readonly string[] CostKeys = ["cost", "total_cost", "cost_usd", "upstream_inference_cost"];

	/// <summary>
	/// The normalized record for one model request. <paramref name="raw"/> is the provider's own
	/// response object when the client exposes one; it is inspected only for the fields the typed
	/// usage model cannot carry.
	/// </summary>
	public static TokenUsage Read(UsageDetails? details, object? raw)
	{
		var usage = FromDetails(details);
		var extra = FromRaw(raw);
		if (extra is not null)
		{
			usage = usage with
			{
				CachedInputTokens = usage.CachedInputTokens ?? extra.CachedInputTokens,
				CachedWriteTokens = usage.CachedWriteTokens ?? extra.CachedWriteTokens,
				ActualCost = usage.ActualCost ?? extra.ActualCost,
			};
		}
		return usage.Normalized();
	}

	/// <summary>The counts the client library mapped, with its zeros read for what they mean.</summary>
	/// <remarks>
	/// The typed usage model has non-nullable counts, so a provider that omitted one is
	/// indistinguishable there from one that reported none of it. Two readings recover the
	/// difference, and both are safe because neither shape can occur as a genuine measurement: a
	/// served request always has a prompt, so a zero prompt and zero completion beside a real total
	/// is a provider that reported only a total; and a zero total beside real components is a
	/// provider that left the harness to add them up.
	/// </remarks>
	private static TokenUsage FromDetails(UsageDetails? details)
	{
		if (details is null) return TokenUsage.None;

		var input = NonNegative(details.InputTokenCount);
		var output = NonNegative(details.OutputTokenCount);
		var total = NonNegative(details.TotalTokenCount);

		if (input is 0 && output is 0 && total is > 0) (input, output) = (null, null);
		else if (total is 0 && (input is > 0 || output is > 0)) total = null;

		return new TokenUsage(
			InputTokens: input,
			OutputTokens: output,
			TotalTokens: total,
			// The detail counts carry no information at zero — a model that did no reasoning and a
			// provider that does not report reasoning look identical — so zero is read as unreported
			// rather than shown as a measured nothing.
			ReasoningTokens: Reported(details.ReasoningTokenCount),
			CachedInputTokens: Reported(details.CachedInputTokenCount) ?? AdditionalCount(details, CacheReadKeys),
			CachedWriteTokens: AdditionalCount(details, CacheWriteKeys));
	}

	/// <summary>A count as reported, treating a negative value as unknown.</summary>
	private static long? NonNegative(long? value) => value is >= 0 ? value : null;

	/// <summary>An optional detail count, where zero and absent are the same claim.</summary>
	private static long? Reported(long? value) => value is > 0 ? value : null;

	private static long? AdditionalCount(UsageDetails details, string[] keys)
	{
		if (details.AdditionalCounts is not { Count: > 0 } counts) return null;
		foreach (var key in keys)
		{
			if (counts.TryGetValue(key, out var value) && value > 0) return value;
		}
		return null;
	}

	/// <summary>
	/// What the provider's own payload adds beyond the typed model. Returns null when there is no
	/// payload, when it cannot be serialized, or when it carries no usage block.
	/// </summary>
	/// <remarks>
	/// The client models round-trip properties they do not know about, which is what makes a
	/// provider-specific field such as OpenRouter's <c>usage.cost</c> reachable at all without the
	/// harness taking a dependency on that provider.
	/// </remarks>
	private static TokenUsage? FromRaw(object? raw)
	{
		for (var depth = 0; raw is not null && depth < 4; depth++)
		{
			if (raw is IPersistableModel<object> model)
			{
				try
				{
					using var document = JsonDocument.Parse(ModelReaderWriter.Write(model));
					return FromPayload(document.RootElement);
				}
				catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException or InvalidOperationException)
				{
					// The payload is not readable as JSON. Extra fields stay unknown, which is the
					// same outcome as a provider that never reported them.
					return null;
				}
			}

			// Responses arrive wrapped: the agent's response holds the chat response, which holds the
			// provider's own object. Walk down until the provider model is reached.
			raw = raw.GetType().GetProperty("RawRepresentation")?.GetValue(raw);
		}
		return null;
	}

	private static TokenUsage? FromPayload(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object) return null;
		if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

		return new TokenUsage(
			CachedInputTokens: Count(usage, "prompt_tokens_details", "cached_tokens") ?? Count(usage, null, "cache_read_input_tokens"),
			CachedWriteTokens: Count(usage, null, "cache_creation_input_tokens") ?? Count(usage, "prompt_tokens_details", "cache_creation_tokens"),
			ActualCost: Cost(usage));
	}

	/// <summary>A count from the usage object, optionally one level down inside a details object.</summary>
	private static long? Count(JsonElement usage, string? container, string name)
	{
		var owner = usage;
		if (container is not null)
		{
			if (!usage.TryGetProperty(container, out owner) || owner.ValueKind != JsonValueKind.Object) return null;
		}
		return owner.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out var count)
			&& count > 0
			? count
			: null;
	}

	/// <summary>
	/// The billed cost the provider reported, if any. A zero is kept: a free route billing nothing is
	/// a real answer, and it is the provider's answer rather than the harness inventing one.
	/// </summary>
	private static decimal? Cost(JsonElement usage)
	{
		foreach (var key in CostKeys)
		{
			if (Read(usage, key) is { } value) return value;
		}
		if (usage.TryGetProperty("cost_details", out var details) && details.ValueKind == JsonValueKind.Object)
		{
			foreach (var key in CostKeys)
			{
				if (Read(details, key) is { } value) return value;
			}
		}
		return null;

		static decimal? Read(JsonElement owner, string key)
		{
			if (!owner.TryGetProperty(key, out var value)) return null;
			var parsed = value.ValueKind switch
			{
				JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
				JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var text) => text,
				_ => (decimal?)null,
			};
			return parsed is >= 0 ? parsed : null;
		}
	}
}

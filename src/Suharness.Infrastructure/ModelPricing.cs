using Suharness.Abstractions;
using System.Globalization;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Reads optional pricing and context metadata out of a provider's model listing. Every field is
/// treated as absent until something readable proves otherwise, because the same endpoint shape is
/// served by OpenRouter, by gateways that copy parts of its schema, and by local servers that answer
/// with an id and nothing else.
/// </summary>
/// <remarks>
/// Prices are normalized to USD per million tokens on the way in. OpenRouter publishes a per-token
/// rate as a decimal string — "0.0000025" — and other listings publish per-million numbers under
/// similar names. Storing the raw number and remembering which unit it was would put a
/// thousand-fold error one forgotten conversion away, so the unit is resolved once, here.
/// </remarks>
public static class ModelPricing
{
	/// <summary>Property names that carry a prompt-token rate.</summary>
	private static readonly string[] InputNames =
		["prompt", "input", "input_tokens", "prompt_tokens", "input_cost_per_token", "input_cost_per_million_tokens", "input_per_million"];

	/// <summary>Property names that carry a completion-token rate.</summary>
	private static readonly string[] OutputNames =
		["completion", "output", "output_tokens", "completion_tokens", "output_cost_per_token", "output_cost_per_million_tokens", "output_per_million"];

	/// <summary>Property names that carry a cached prompt-token rate.</summary>
	private static readonly string[] CachedInputNames =
		["input_cache_read", "cache_read", "cached_input", "cached", "cache_read_input_token_cost", "cached_input_tokens", "prompt_cache_read"];

	/// <summary>Objects a listing may nest its rates inside.</summary>
	private static readonly string[] PricingContainers = ["pricing", "cost", "costs", "price", "prices"];

	/// <summary>Property names that carry a usable context length.</summary>
	private static readonly string[] ContextNames =
		["context_length", "max_context_length", "context_window", "max_model_len", "max_input_tokens"];

	/// <summary>Property names that carry a maximum completion length.</summary>
	private static readonly string[] OutputLimitNames =
		["max_output_tokens", "max_completion_tokens", "max_tokens"];

	/// <summary>
	/// Build a descriptor from one entry of a <c>/models</c> response. Returns null only when the
	/// entry has no usable id; an entry carrying nothing but an id is a valid model.
	/// </summary>
	public static ModelDescriptor? Read(JsonElement item, string sourceLabel, DateTimeOffset fetchedAt)
	{
		if (item.ValueKind != JsonValueKind.Object) return null;
		if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) return null;
		var id = idElement.GetString();
		if (string.IsNullOrWhiteSpace(id)) return null;

		var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(nameElement.GetString())
			? nameElement.GetString()!
			: id;

		// OpenRouter reports the routed provider's real limits under top_provider, which are the ones
		// a request is actually held to when they differ from the headline numbers.
		var topProvider = item.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object
			? top
			: default;

		var input = FindRate(item, InputNames);
		var output = FindRate(item, OutputNames);
		var cached = FindRate(item, CachedInputNames);
		var hasPricing = input is not null || output is not null || cached is not null;

		return new ModelDescriptor(
			id!,
			name,
			ContextWindow: FindInteger(item, ContextNames) ?? FindInteger(topProvider, ContextNames),
			MaxOutputTokens: FindInteger(item, OutputLimitNames) ?? FindInteger(topProvider, OutputLimitNames),
			InputCostPerMillion: input,
			OutputCostPerMillion: output,
			CachedInputCostPerMillion: cached,
			PricingSource: hasPricing ? sourceLabel : null,
			PricingFetchedAt: hasPricing ? fetchedAt : null);
	}

	/// <summary>A label naming where a catalog came from, for the pricing metadata it supplies.</summary>
	public static string SourceLabel(string providerId, string baseUrl) =>
		Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint.Host)
			? endpoint.Host
			: providerId;

	/// <summary>
	/// A rate in USD per million tokens, looked for both at the top level and inside whichever
	/// pricing object the listing nests. Returns null unless a value is present, readable, and not
	/// negative — OpenRouter marks a variable rate as -1, which is "unknown", not "free".
	/// </summary>
	private static decimal? FindRate(JsonElement item, string[] names)
	{
		if (item.ValueKind != JsonValueKind.Object) return null;
		if (ReadRate(item, names) is { } direct) return direct;
		foreach (var container in PricingContainers)
		{
			if (item.TryGetProperty(container, out var nested)
				&& nested.ValueKind == JsonValueKind.Object
				&& ReadRate(nested, names) is { } value)
			{
				return value;
			}
		}
		return null;
	}

	private static decimal? ReadRate(JsonElement owner, string[] names)
	{
		foreach (var name in names)
		{
			if (!owner.TryGetProperty(name, out var value)) continue;
			if (ReadDecimal(value) is not { } rate || rate < 0) continue;
			return PerMillion(name, rate);
		}
		return null;
	}

	/// <summary>
	/// A rate converted to USD per million tokens. A name that says which unit it is in is believed;
	/// everything else is read as the per-token rate every listing observed so far publishes.
	/// </summary>
	private static decimal PerMillion(string name, decimal rate) =>
		name.Contains("million", StringComparison.OrdinalIgnoreCase) || name.Contains("_1m", StringComparison.OrdinalIgnoreCase)
			? rate
			: rate * 1_000_000m;

	/// <summary>A decimal from a JSON number or from the decimal string providers often use instead.</summary>
	private static decimal? ReadDecimal(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : null,
		JsonValueKind.String => decimal.TryParse(
			value.GetString(),
			NumberStyles.Float,
			CultureInfo.InvariantCulture,
			out var parsed) ? parsed : null,
		_ => null,
	};

	/// <summary>The first positive integer among these property names, tolerating absent or odd values.</summary>
	private static int? FindInteger(JsonElement owner, string[] names)
	{
		if (owner.ValueKind != JsonValueKind.Object) return null;
		foreach (var name in names)
		{
			if (!owner.TryGetProperty(name, out var value)) continue;
			var parsed = value.ValueKind switch
			{
				JsonValueKind.Number when value.TryGetInt32(out var number) => number,
				JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) => text,
				_ => 0,
			};
			if (parsed > 0) return parsed;
		}
		return null;
	}
}

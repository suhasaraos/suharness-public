using Suharness.Abstractions;
using System.Text.Json;

namespace Suharness.Tools;

/// <summary>
/// Fixed-orchestration Ralph loop: each round spawns a fresh structured-report
/// subagent that receives the immutable objective and the previous round's
/// handoff, and returns a structured JSON report.
/// </summary>
public static class RalphTool
{
	private const int DefaultMaxRounds = 32;
	private const int MaxHandoffChars = 8000;

	public static async Task<string> RunAsync(
		ISubagentService subagents,
		Guid parentSessionId,
		string objective,
		int? maxRounds,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(objective))
			return "Error: ralph requires a non-empty objective.";

		var cap = Math.Min(maxRounds ?? DefaultMaxRounds, 256);
		var handoff = string.Empty;
		var roundsStarted = 0;

		for (var round = 1; round <= cap; round++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			roundsStarted = round;

			var prompt = BuildPrompt(objective, handoff);
			var run = await subagents.StartAsync(
				$"Ralph round {round}",
				prompt,
				parentSessionId,
				parentSessionId,
				runInBackground: false,
				cancellationToken: cancellationToken);

			SubagentResult result;
			try { result = await run.Result; }
			finally { await run.Dispose(); }

			if (result.StopReason != SubagentStopReason.Completed)
			{
				return $"Ralph round {round} failed: {result.StopReason}. {roundsStarted} rounds started. Last report: {Truncate(handoff, 4000)}";
			}

			var report = ParseReport(result.Output.Substring(0, Math.Min(result.Output.Length, MaxHandoffChars)));
			if (report is null)
				return $"Ralph round {round} returned no structured report. Output: {Truncate(result.Output, 2000)}";

			if (report.Status == "complete")
				return $"Ralph completed after {roundsStarted} round(s). Final report:\nSummary: {report.Summary}\nEvidence: {string.Join(", ", report.Evidence)}\nNext steps: {string.Join(", ", report.NextSteps)}";

			if (report.Status == "blocked")
				return $"Ralph blocked after {roundsStarted} round(s). Blocker: {report.Blocker}. Last summary: {report.Summary}";

			// Continue to next round with handoff.
			handoff = $"Previous round summary: {report.Summary}\nEvidence: {string.Join(", ", report.Evidence)}\nNext steps: {string.Join(", ", report.NextSteps)}";
			if (!string.IsNullOrEmpty(report.Blocker))
				handoff += $"\nPotential blocker noted: {report.Blocker}";
		}

		return $"Ralph reached the round cap ({cap}) after {roundsStarted} round(s). Current handoff:\n{Truncate(handoff, 4000)}";
	}

	private static string BuildPrompt(string objective, string handoff)
	{
		var prompt = PromptCatalog.RenderSection(
			"ralph/round",
			"round",
			PromptNewline.Lf,
			("objective", objective));

		if (handoff.Length > 0)
		{
			prompt += "\n\n" + PromptCatalog.RenderSection(
				"ralph/round",
				"handoff",
				PromptNewline.Lf,
				("handoff", Truncate(handoff, MaxHandoffChars)));
		}

		return prompt;
	}

	private sealed record RalphReport(string Status, string Summary, string[] Evidence, string[] NextSteps, string Blocker);

	private static RalphReport? ParseReport(string text)
	{
		text = text.Trim();
		// Strip markdown fences if present.
		if (text.StartsWith("```"))
		{
			var closeIndex = text.IndexOf('\n');
			if (closeIndex >= 0)
			{
				var startContent = closeIndex + 1;
				var endFence = text.LastIndexOf("```", StringComparison.Ordinal);
				text = endFence > startContent ? text[startContent..endFence] : text[startContent..];
			}
		}

		try
		{
			using var doc = JsonDocument.Parse(text);
			var root = doc.RootElement;
			var status = Prop(root, "status") ?? "continue";
			var summary = Prop(root, "summary") ?? "";
			var evidence = ArrayProp(root, "evidence");
			var nextSteps = ArrayProp(root, "nextSteps");
			var blocker = Prop(root, "blocker") ?? "";
			return new RalphReport(status, summary, evidence, nextSteps, blocker);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? Prop(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static string[] ArrayProp(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
			? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
			: [];

	private static string Truncate(string text, int max) =>
		text.Length <= max ? text : text[..max];
}

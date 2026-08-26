using Suharness.Abstractions;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// The tools through which the agent talks to the person running it: asking a question and
/// waiting for the answer, tracking a task list, and submitting a plan for approval. Each of
/// these blocks the turn on a human, so each has to behave sensibly when there is no
/// interactive user attached to the session.
/// </summary>
public sealed partial class SessionToolset
{
	/// <summary>
	/// Put questions to the user and block until they answer. The run stays alive on its own task
	/// while the browser collects the answer, which arrives back as this tool's result.
	/// </summary>
	private async Task<string> AskUserQuestion(
		[Description("The questions to ask before continuing. Ask everything you need in one call.")] UserQuestion[] questions,
		CancellationToken cancellationToken = default)
	{
		if (questions.Length == 0) return "Error: ask_user_question requires at least one question.";
		if (HasWorkspace && questions.Any(question => LooksLikeDiscoverableLocationQuestion(question.Question)))
			return $"Error: the selected repository is already '{WorkspaceRoot}'. Do not ask the user where existing source, tests, plugins, files, or symbols are located. Use symbol, lsp, glob, grep, and read within that workspace, then continue the task.";
		if (_questions is null)
			return "Error: this session has no interactive user, so questions cannot be answered. Choose the most reasonable interpretation, state the assumption you made, and continue.";

		try
		{
			var answers = await _questions.AskAsync(
				_sessionId,
				[.. questions.Select(question => new UserQuestionItem(
					question.Id,
					question.Question,
					question.Header,
					question.Options?.Select(option => new UserQuestionOption(option.Label, option.Description)).ToArray(),
					question.MultiSelect ?? false))],
				cancellationToken);
			return JsonSerializer.Serialize(new { answers });
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return "Error: the user dismissed the question without answering. Stop and wait for their next instruction.";
		}
		catch (InvalidOperationException exception)
		{
			return $"Error: {exception.Message}";
		}
	}

	internal static bool LooksLikeDiscoverableLocationQuestion(string question) =>
		Regex.IsMatch(
			question,
			@"\bwhere\b[\s\S]{0,120}\b(find|located?|source|repository|repo|codebase|plugins?|files?)\b|\bwhich\b[\s\S]{0,80}\b(path|folder|directory)\b[\s\S]{0,80}\b(contains?|has|holds?|source|repository|repo|code|plugins?)\b",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>One question offered to the user through the ask_user_question schema.</summary>
	public sealed record UserQuestion(
		[property: Description("Stable id for this question; echoed when the user answers.")] string Id,
		[property: Description("The specific question to ask the user.")] string Question,
		[property: Description("Optional short heading, such as \"Confirm\" or \"Choose scaling\".")] string? Header = null,
		[property: Description("Optional choices to offer. Put the option you recommend first.")] UserQuestionChoice[]? Options = null,
		[property: Description("Whether the user may pick more than one option. Defaults to false.")] bool? MultiSelect = null);

	/// <summary>One choice shown under a question.</summary>
	public sealed record UserQuestionChoice(
		[property: Description("Short user-facing option label.")] string Label,
		[property: Description("One sentence explaining the tradeoff or impact.")] string? Description = null);

	private static string TodoWrite(
		[Description("The COMPLETE task list, replacing any previous list.")] TodoItem[] todos) =>
		Guard(() => TodoTool.Write(todos));

	/// <summary>Submit the plan for review; approval is what ends plan mode.</summary>
	private async Task<string> ExitPlanMode(
		[Description("The COMPLETE plan as markdown, starting with a # heading that names it.")] string plan,
		CancellationToken cancellationToken = default)
	{
		if (_planMode is null || !await _planMode.IsActiveAsync(_sessionId, cancellationToken))
			return "Error: exit_plan_mode is only usable in plan mode. This session is not in plan mode, so proceed normally.";
		if (string.IsNullOrWhiteSpace(plan)) return "Error: exit_plan_mode requires the complete plan as markdown.";
		if (_questions is null)
			return "Error: this session has no interactive user, so the plan cannot be reviewed. Present the plan in your reply instead.";

		try
		{
			var answers = await _questions.AskAsync(
				_sessionId,
				[new UserQuestionItem(
					"plan-review",
					FirstHeading(plan) is { } heading ? $"Ready to implement: {heading}?" : "Ready to implement this plan?",
					"Plan review",
					[new UserQuestionOption("Approve", "Leave plan mode and carry out this plan."),
					 new UserQuestionOption("Keep planning", "Stay in plan mode and revise.")],
					MultiSelect: false,
					Detail: plan)],
				cancellationToken);

			var answer = answers.FirstOrDefault();
			if (answer is not null && answer.Selected.Contains("Approve"))
			{
				await _planMode.SetAsync(_sessionId, false, cancellationToken);
				return "The user approved the plan. Plan mode is off; carry it out from your next step.";
			}
			return string.IsNullOrWhiteSpace(answer?.Custom)
				? "The user chose to keep planning. Stay in plan mode, revise the plan, and present it again."
				: $"The user chose to keep planning. Their feedback: {answer.Custom}. Stay in plan mode, revise the plan, and present it again.";
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return "Error: the user dismissed the plan review. Stay in plan mode and wait for their next instruction.";
		}
		catch (InvalidOperationException exception)
		{
			return $"Error: {exception.Message}";
		}
	}

	private static string? FirstHeading(string plan)
	{
		foreach (var line in plan.Split('\n'))
		{
			var trimmed = line.Trim();
			if (!trimmed.StartsWith('#')) continue;
			var heading = trimmed.TrimStart('#').Trim();
			if (heading.Length > 0) return heading;
		}
		return null;
	}
}

using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Text.Json;

namespace Suharness.Tools;

/// <summary>
/// Per-tool deadlines. The timeout policy reads each budget
/// from the tool's own declaration rather than from a global setting.
/// </summary>
/// <remarks>
/// Only tools whose work is bounded by the machine get a budget. A tool that legitimately waits on a
/// person or on another agent — <c>ask_user_question</c>, <c>subagent</c>, <c>workflow</c>,
/// <c>job_output</c> — has no deadline, because killing it at an arbitrary time would report a
/// failure that never happened. <c>bash</c> is absent for a different reason: it takes a timeout as
/// a parameter and enforces its own, so a second one here could kill a command mid-write.
/// </remarks>
public static class ToolTimeouts
{
	private static readonly Dictionary<string, int> Budgets = new(StringComparer.Ordinal)
	{
		["read"] = 120_000,
		["write"] = 120_000,
		["edit"] = 120_000,
		["str_replace_editor"] = 120_000,
		["glob"] = 120_000,
		["grep"] = 120_000,
	};

	/// <summary>The deadline for one tool in milliseconds, or null when it has none.</summary>
	public static int? For(string toolName) => Budgets.TryGetValue(toolName, out var budget) ? budget : null;
}

/// <summary>Session-scoped consent to bypass later human approval prompts.</summary>
public sealed class ToolApprovalScope
{
	private int _approveAll;

	public bool ApprovesAll => Volatile.Read(ref _approveAll) != 0;

	public void ApproveAll() => Interlocked.Exchange(ref _approveAll, 1);
}

/// <summary>
/// Wraps one tool so every invocation passes through the policy pipeline: handlers decide whether the
/// call may run, the call runs under its declared deadline, and handlers then decide what the model
/// is shown. Interception has to happen at the function boundary rather than in the runtime loop,
/// because the tool loop itself lives inside the Agent Framework — by the time the runtime observes a
/// call in the response stream it has already run.
/// </summary>
/// <remarks>
/// A refused, escalated, or timed-out call returns text rather than throwing. The model needs a
/// result for every call it made, and one it can read is what lets it choose a different approach.
/// An <see cref="ToolGateKind.Ask"/> decision is put to the human through the ordinary question
/// broker, so approval reuses the composer's existing prompt instead of a second parallel surface;
/// with no interactive human on the session it fails closed.
/// </remarks>
public sealed class LifecycleTool(
	AIFunction inner,
	IAgentLifecycle lifecycle,
	Func<TurnContext> context,
	IUserQuestionBroker? questions = null,
	ToolApprovalScope? approvals = null,
	Func<string, int?>? timeouts = null) : DelegatingAIFunction(inner)
{
	/// <summary>Wrap a tool list, leaving it untouched when no lifecycle is composed in.</summary>
	public static IList<AITool> Wrap(
		IList<AITool> tools,
		IAgentLifecycle? lifecycle,
		Func<TurnContext> context,
		IUserQuestionBroker? questions = null,
		ToolApprovalScope? approvals = null,
		Func<string, int?>? timeouts = null)
	{
		if (lifecycle is null) return tools;
		for (var index = 0; index < tools.Count; index++)
		{
			if (tools[index] is AIFunction function)
				tools[index] = new LifecycleTool(function, lifecycle, context, questions, approvals, timeouts);
		}
		return tools;
	}

	protected override async ValueTask<object?> InvokeCoreAsync(
		AIFunctionArguments arguments,
		CancellationToken cancellationToken)
	{
		var turn = context();

		var gate = await lifecycle.FireBeforeToolCallAsync(turn, Name, arguments);
		if (gate.Kind == ToolGateKind.Ask)
			gate = approvals?.ApprovesAll == true ? PreToolDecision.Allow : await EscalateAsync(turn, gate, cancellationToken);
		if (gate.Kind != ToolGateKind.Allow) return Final(turn, arguments, Refusal(gate.Reason));

		object? result;
		try
		{
			result = await InvokeWithDeadlineAsync(arguments, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			// The failure still reaches the caller, but the audit hook sees it first: a tool that threw
			// is part of what happened on this turn whether or not the loop recovers from it.
			lifecycle.FireToolResult(turn, Name, arguments, $"Error: {exception.Message}");
			throw;
		}

		var rendered = Render(result);
		var decision = await lifecycle.FireAfterToolCallAsync(turn, Name, rendered);
		return decision.Outcome switch
		{
			PostToolOutcome.Block => Final(turn, arguments, decision.Content ?? "Error: the harness withheld this tool's result."),
			PostToolOutcome.Replace => Final(turn, arguments, decision.Content ?? rendered),
			// Nothing changed the result, so the original object is returned rather than its rendering:
			// a structured result stays structured for whatever wraps this tool further out.
			_ => Observed(turn, arguments, result, rendered),
		};
	}

	/// <summary>Run the tool under its declared deadline, if it has one.</summary>
	private async ValueTask<object?> InvokeWithDeadlineAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
	{
		if ((timeouts ?? ToolTimeouts.For)(Name) is not { } budget) return await base.InvokeCoreAsync(arguments, cancellationToken);

		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(budget);
		try
		{
			return await base.InvokeCoreAsync(arguments, deadline.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// The deadline won, not the user. Reported as a result so the model can narrow the request
			// and try again rather than losing the turn.
			return $"Error: the {Name} tool did not finish within {budget} ms and was stopped.";
		}
	}

	/// <summary>
	/// Put an escalated call to the human. An answer of anything but approval refuses the call, and
	/// so does the absence of anyone to ask — an unanswerable approval must never become a silent yes.
	/// </summary>
	private async ValueTask<PreToolDecision> EscalateAsync(
		TurnContext turn,
		PreToolDecision gate,
		CancellationToken cancellationToken)
	{
		const string Approve = "Allow";
		const string ApproveAll = "Allow all";
		const string Refuse = "Deny";

		if (questions is null)
			return PreToolDecision.Deny("this session has no interactive user, so a call that needs approval cannot be approved");

		// Escalating the question tool itself would ask the user for permission to ask the user.
		if (Name == "ask_user_question") return PreToolDecision.Allow;

		var detail = gate.Reason is { Length: > 0 } reason ? $" {reason}" : string.Empty;
		try
		{
			var answers = await questions.AskAsync(
				turn.SessionId,
				[new UserQuestionItem(
					$"approve-{Name}",
					$"Allow the agent to run {Name}?{detail}",
					Header: "Approve tool",
					Options: [new UserQuestionOption(Approve, "Run the call once."), new UserQuestionOption(Refuse, "Refuse the call and tell the agent why."), new UserQuestionOption(ApproveAll)],
					Detail: Summarize(turn),
					ApproveAllOption: ApproveAll)],
				cancellationToken);

			if (answers.Any(answer => answer.Selected.Contains(ApproveAll, StringComparer.Ordinal)))
			{
				approvals?.ApproveAll();
				return PreToolDecision.Allow;
			}
			return answers.Any(answer => answer.Selected.Contains(Approve, StringComparer.Ordinal))
				? PreToolDecision.Allow
				: PreToolDecision.Deny("the user did not approve this call");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return PreToolDecision.Deny("the user dismissed the approval request without answering");
		}
		catch (InvalidOperationException)
		{
			// The session is already waiting on a question, so this approval cannot be presented.
			return PreToolDecision.Deny("another question is already awaiting an answer in this session");
		}
	}

	private string Summarize(TurnContext turn) => $"Session {turn.SessionId:N}, turn {turn.TurnNumber}, step {turn.StepNumber}.";

	private string Refusal(string? reason) =>
		reason is { Length: > 0 }
			? $"Error: the {Name} tool call was refused: {reason}."
			: $"Error: the {Name} tool call was refused by the harness before it ran.";

	/// <summary>Publish the final model-facing text and return it.</summary>
	private string Final(TurnContext turn, IReadOnlyDictionary<string, object?> arguments, string text)
	{
		lifecycle.FireToolResult(turn, Name, arguments, text);
		return text;
	}

	/// <summary>Publish the final model-facing text and return the tool's own result unchanged.</summary>
	private object? Observed(
		TurnContext turn,
		IReadOnlyDictionary<string, object?> arguments,
		object? result,
		string rendered)
	{
		lifecycle.FireToolResult(turn, Name, arguments, rendered);
		return result;
	}

	private static string Render(object? result) => result switch
	{
		null => string.Empty,
		string text => text,
		JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
		JsonElement json => json.ToString(),
		_ => result.ToString() ?? string.Empty,
	};
}

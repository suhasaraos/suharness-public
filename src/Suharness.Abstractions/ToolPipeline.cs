namespace Suharness.Abstractions;

/// <summary>What a policy handler decided about a tool call before it ran.</summary>
public enum ToolGateKind
{
	/// <summary>Run the call.</summary>
	Allow,

	/// <summary>Refuse the call. The model is told why, in place of a result.</summary>
	Deny,

	/// <summary>Put the call to the human, and run it only if they agree.</summary>
	Ask,
}

/// <summary>
/// The decision a <see cref="IAgentLifecycle.BeforeToolCall"/> handler returns. A handler may refuse or
/// escalate a call, but never rewrite its arguments. A policy that silently changed what the model
/// asked for would leave the transcript describing a call that never happened.
/// </summary>
public sealed record PreToolDecision
{
	private PreToolDecision(ToolGateKind kind, string? reason)
	{
		Kind = kind;
		Reason = reason;
	}

	public ToolGateKind Kind { get; }

	/// <summary>Why the call was refused or escalated. Shown to the model on a refusal.</summary>
	public string? Reason { get; }

	public static PreToolDecision Allow { get; } = new(ToolGateKind.Allow, null);

	public static PreToolDecision Deny(string reason) => new(ToolGateKind.Deny, reason);

	public static PreToolDecision Ask(string? reason = null) => new(ToolGateKind.Ask, reason);

	/// <summary>
	/// The stricter of two decisions. Combining this way rather than letting the last handler win is
	/// what makes policy monotonic: a refusal cannot be undone by a handler that happened to register
	/// after it, so registration order is not a policy priority mechanism.
	/// </summary>
	public static PreToolDecision Strictest(PreToolDecision left, PreToolDecision right) =>
		left.Kind >= right.Kind ? left : right;
}

/// <summary>What a policy handler decided about a tool call's result.</summary>
public enum PostToolOutcome
{
	/// <summary>Keep the result as the tool produced it.</summary>
	Accept,

	/// <summary>Show the model different text in place of the result.</summary>
	Replace,

	/// <summary>Withhold the result and tell the model why instead.</summary>
	Block,
}

/// <summary>
/// The decision an <see cref="IAgentLifecycle.AfterToolCall"/> handler returns.
/// </summary>
/// <remarks>
/// Replacing content is a presentation change, not a confidentiality boundary: the tool still ran and
/// its effects still happened. Use <see cref="Block"/> when the model must not act on what came back.
/// </remarks>
public sealed record PostToolDecision
{
	private PostToolDecision(PostToolOutcome outcome, string? content)
	{
		Outcome = outcome;
		Content = content;
	}

	public PostToolOutcome Outcome { get; }

	/// <summary>The replacement text, or the feedback shown in place of a blocked result.</summary>
	public string? Content { get; }

	public static PostToolDecision Accept { get; } = new(PostToolOutcome.Accept, null);

	public static PostToolDecision Replace(string content) => new(PostToolOutcome.Replace, content);

	public static PostToolDecision Block(string feedback) => new(PostToolOutcome.Block, feedback);
}

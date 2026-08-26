namespace Suharness.Abstractions;

/// <summary>One selectable answer offered to the user.</summary>
public sealed record UserQuestionOption(string Label, string? Description = null);

/// <summary>One question put to the user by the ask_user_question tool.</summary>
public sealed record UserQuestionItem(
	string Id,
	string Question,
	string? Header = null,
	IReadOnlyList<UserQuestionOption>? Options = null,
	bool MultiSelect = false,
	string? Detail = null,
	string? ApproveAllOption = null);

/// <summary>The user's answer to one question.</summary>
public sealed record UserQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom = null);

/// <summary>Questions currently waiting on the user in one session.</summary>
public sealed record PendingUserQuestions(Guid SessionId, IReadOnlyList<UserQuestionItem> Questions);

/// <summary>
/// Pauses a tool call until a human answers. The agent loop runs on a background task while the UI
/// keeps its own thread, so the tool awaits <see cref="AskAsync"/> and the UI completes it through
/// <see cref="TryAnswer"/>. A session may hold only one outstanding request at a time.
/// </summary>
public interface IUserQuestionBroker
{
	/// <summary>Raised when a session's pending questions appear or are resolved.</summary>
	event Action<Guid>? Changed;

	/// <summary>Present <paramref name="questions"/> and wait for the user's answer.</summary>
	Task<IReadOnlyList<UserQuestionAnswerItem>> AskAsync(
		Guid sessionId,
		IReadOnlyList<UserQuestionItem> questions,
		CancellationToken cancellationToken = default);

	/// <summary>The questions awaiting an answer in <paramref name="sessionId"/>, or null.</summary>
	PendingUserQuestions? Pending(Guid sessionId);

	/// <summary>Complete the pending request. Returns false when nothing was waiting.</summary>
	bool TryAnswer(Guid sessionId, IReadOnlyList<UserQuestionAnswerItem> answers);

	/// <summary>Abandon the pending request, failing the tool call rather than answering it.</summary>
	bool TryCancel(Guid sessionId);
}

/// <summary>
/// Whether a session is in plan mode. The state is durable on the session record, so a restart
/// returns to the mode the session was left in.
/// </summary>
public interface IPlanModeStore
{
	/// <summary>Raised when a session enters or leaves plan mode.</summary>
	event Action<Guid>? Changed;

	ValueTask<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

	ValueTask SetAsync(Guid sessionId, bool active, CancellationToken cancellationToken = default);
}

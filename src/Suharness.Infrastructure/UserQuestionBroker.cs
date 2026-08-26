using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// In-memory rendezvous between the agent's ask_user_question call and the browser. The pending
/// request is deliberately not persisted: it is only answerable while the run that raised it is
/// still awaiting, so a restart must fail the call rather than resurrect it.
/// </summary>
public sealed class UserQuestionBroker : IUserQuestionBroker
{
	private sealed record Request(
		IReadOnlyList<UserQuestionItem> Questions,
		TaskCompletionSource<IReadOnlyList<UserQuestionAnswerItem>> Completion);

	private readonly ConcurrentDictionary<Guid, Request> _pending = new();

	public event Action<Guid>? Changed;

	public async Task<IReadOnlyList<UserQuestionAnswerItem>> AskAsync(
		Guid sessionId,
		IReadOnlyList<UserQuestionItem> questions,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfZero(questions.Count);
		cancellationToken.ThrowIfCancellationRequested();

		var request = new Request(
			questions,
			new TaskCompletionSource<IReadOnlyList<UserQuestionAnswerItem>>(TaskCreationOptions.RunContinuationsAsynchronously));
		if (!_pending.TryAdd(sessionId, request))
			throw new InvalidOperationException("This session is already waiting for an answer to an earlier question.");

		Changed?.Invoke(sessionId);
		try
		{
			await using var registration = cancellationToken.Register(
				static state => ((TaskCompletionSource<IReadOnlyList<UserQuestionAnswerItem>>)state!).TrySetCanceled(),
				request.Completion);
			return await request.Completion.Task;
		}
		finally
		{
			_pending.TryRemove(sessionId, out _);
			Changed?.Invoke(sessionId);
		}
	}

	public PendingUserQuestions? Pending(Guid sessionId) =>
		_pending.TryGetValue(sessionId, out var request) ? new PendingUserQuestions(sessionId, request.Questions) : null;

	public bool TryAnswer(Guid sessionId, IReadOnlyList<UserQuestionAnswerItem> answers) =>
		_pending.TryGetValue(sessionId, out var request) && request.Completion.TrySetResult(answers);

	public bool TryCancel(Guid sessionId) =>
		_pending.TryGetValue(sessionId, out var request) && request.Completion.TrySetCanceled();
}

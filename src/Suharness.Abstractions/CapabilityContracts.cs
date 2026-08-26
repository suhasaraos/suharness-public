namespace Suharness.Abstractions;

/// <summary>
/// Remembers the capability groups an agent asked for when a narrowed tool catalog did not contain
/// what it needed.
/// </summary>
/// <remarks>
/// Narrowing a catalog is only safe if the agent has a way out. Without one, a filter that guessed
/// wrong is indistinguishable to the model from a server that cannot do the job, and the turn ends
/// in a false blocker. The request lands here and is honoured when the next turn composes its tools,
/// because the catalog handed to a model cannot change while it is mid-request.
/// </remarks>
public interface ICapabilityEscalation
{
	/// <summary>Record a group request for a session. Returns false when the group name is empty.</summary>
	bool Request(Guid sessionId, string group, string reason);

	/// <summary>The groups this session has asked for, in the order they were requested.</summary>
	IReadOnlyList<string> Requested(Guid sessionId);

	/// <summary>Forget a session's requests, so a new task starts from the ordinary selection.</summary>
	void Clear(Guid sessionId);

	/// <summary>Raised when a session's requests change, so a view can show what was asked for.</summary>
	event Action<Guid>? Changed;
}

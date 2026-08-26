using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Per-session inbox for injected context, goal updates, and queued messages.
/// Messages wait until the owning session's next turn claims them.
/// </summary>
public sealed class AgentInbox : IAgentInbox
{
	private readonly ConcurrentDictionary<Guid, ConcurrentQueue<AgentInboxMessage>> _queues = new();

	public event Action<Guid>? Changed;

	public void Enqueue(Guid sessionId, AgentInboxMessage message)
	{
		_queues.GetOrAdd(sessionId, _ => new ConcurrentQueue<AgentInboxMessage>()).Enqueue(message);
		Changed?.Invoke(sessionId);
	}

	public IReadOnlyList<AgentInboxMessage> Dequeue(Guid sessionId)
	{
		if (!_queues.TryGetValue(sessionId, out var queue)) return [];
		var messages = new List<AgentInboxMessage>();
		while (queue.TryDequeue(out var message)) messages.Add(message);
		return messages;
	}

	public IReadOnlyList<AgentInboxMessage> Peek(Guid sessionId) =>
		_queues.TryGetValue(sessionId, out var queue) ? [.. queue] : [];
}

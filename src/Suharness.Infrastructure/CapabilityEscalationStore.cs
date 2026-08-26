using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Per-session record of the capability groups an agent asked for. Held in memory: the request is a
/// hint about the task in front of the agent right now, and a restart starts from a fresh task.
/// </summary>
public sealed class CapabilityEscalationStore : ICapabilityEscalation
{
	private readonly ConcurrentDictionary<Guid, List<string>> _requests = new();

	public event Action<Guid>? Changed;

	public bool Request(Guid sessionId, string group, string reason)
	{
		var normalized = group.Trim().ToLowerInvariant();
		if (normalized.Length == 0) return false;

		var groups = _requests.GetOrAdd(sessionId, _ => []);
		lock (groups)
		{
			if (!groups.Contains(normalized, StringComparer.Ordinal)) groups.Add(normalized);
		}
		Changed?.Invoke(sessionId);
		return true;
	}

	public IReadOnlyList<string> Requested(Guid sessionId)
	{
		if (!_requests.TryGetValue(sessionId, out var groups)) return [];
		lock (groups) return [.. groups];
	}

	public void Clear(Guid sessionId)
	{
		if (_requests.TryRemove(sessionId, out _)) Changed?.Invoke(sessionId);
	}
}

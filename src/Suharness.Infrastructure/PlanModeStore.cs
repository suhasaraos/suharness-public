using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Plan mode per session, held on the session record so it survives a restart. The cache exists
/// because the agent factory reads the mode on every turn; the session store stays authoritative.
/// </summary>
public sealed class PlanModeStore(ISessionStore sessions) : IPlanModeStore
{
	private readonly ConcurrentDictionary<Guid, bool> _cache = new();

	public event Action<Guid>? Changed;

	public async ValueTask<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		if (_cache.TryGetValue(sessionId, out var cached)) return cached;
		var transcript = await sessions.GetAsync(sessionId, cancellationToken);
		var active = transcript?.Session.PlanMode ?? false;
		_cache[sessionId] = active;
		return active;
	}

	public async ValueTask SetAsync(Guid sessionId, bool active, CancellationToken cancellationToken = default)
	{
		_cache[sessionId] = active;
		var transcript = await sessions.GetAsync(sessionId, cancellationToken);
		if (transcript is not null)
		{
			await sessions.SaveSessionAsync(
				transcript.Session with { PlanMode = active, UpdatedAt = DateTimeOffset.UtcNow },
				cancellationToken);
		}
		Changed?.Invoke(sessionId);
	}
}

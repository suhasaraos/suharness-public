using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Keeps each session's running token totals in memory and writes them through to the session record
/// at a turn boundary.
/// </summary>
/// <remarks>
/// The totals are counters, never a derivation: nothing here walks a transcript to work out what a
/// session has spent, so the cost of the feature does not grow with the length of the conversation
/// it is measuring. Persisting once per turn rather than once per request is the other half of that
/// bargain — a streaming answer must not turn into a file write per token, and a turn is the
/// smallest boundary at which losing the last few counts would matter.
/// </remarks>
public sealed class SessionUsageTracker(ISessionStore store, bool recordInvocationEvents = true) : ISessionUsageTracker
{
	/// <summary>The transcript event type one model invocation's usage is recorded under.</summary>
	public const string EventType = SessionUsageEvent.Type;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly ConcurrentDictionary<Guid, SessionCounters> _sessions = new();

	public event Action<Guid>? Changed;

	public async ValueTask EnsureLoadedAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var counters = _sessions.GetOrAdd(sessionId, _ => new SessionCounters());
		if (counters.Loaded) return;

		// Read outside the lock: the store is async, and a second turn arriving while the first is
		// still loading simply finds the work already done.
		var persisted = await store.FindAsync(sessionId, cancellationToken);
		counters.Seed(persisted?.Usage);
	}

	public void BeginTurn(Guid sessionId, int turnNumber) =>
		_sessions.GetOrAdd(sessionId, _ => new SessionCounters()).BeginTurn(turnNumber);

	public ModelInvocationUsage Record(Guid sessionId, ModelInvocationUsage invocation)
	{
		var counters = _sessions.GetOrAdd(sessionId, _ => new SessionCounters());
		// The turn and the request's place within it are stamped here rather than by the caller, because
		// the counters are what know them. The model pipeline sees one request at a time and has no way
		// to say which of a turn's requests it is.
		var filed = counters.Add(invocation);
		Changed?.Invoke(sessionId);

		// The per-invocation record is what makes a session that used several models auditable after
		// the fact. It is one small append per completed request, never per streamed token, and it is
		// queued rather than awaited because this runs inside the model pipeline.
		if (recordInvocationEvents) counters.Queue(() => AppendInvocationAsync(sessionId, filed));
		return filed;
	}

	public SessionUsage? Get(Guid sessionId) =>
		_sessions.TryGetValue(sessionId, out var counters) ? counters.Snapshot() : null;

	public UsageSnapshot? Snapshot(Guid sessionId) =>
		_sessions.TryGetValue(sessionId, out var counters) ? counters.View() : null;

	public async ValueTask FlushAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		if (!_sessions.TryGetValue(sessionId, out var counters)) return;
		// The queued audit lines belong to the turn that is ending, so they land before it is reported
		// as flushed. Without this a reader — or a test — could see the totals before the lines behind
		// them exist.
		await counters.DrainAsync();
		if (!counters.TakeDirty()) return;
		var usage = counters.Snapshot();
		try
		{
			var session = await store.FindAsync(sessionId, cancellationToken);
			if (session is null) return;
			// The counters hold the whole total, not a delta, so a write that raced another save of
			// the same record converges on the next flush rather than compounding an error.
			await store.SaveSessionAsync(session with { Usage = usage }, cancellationToken);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Usage accounting is bookkeeping around the work, never a precondition for it. The
			// counters stay marked dirty so the next flush tries again.
			counters.MarkDirty();
		}
	}

	/// <summary>Append one invocation to the transcript, best effort.</summary>
	private async Task AppendInvocationAsync(Guid sessionId, ModelInvocationUsage invocation)
	{
		try
		{
			await store.AppendAsync(
				new SessionEvent(
					Guid.NewGuid(),
					sessionId,
					EventType,
					"system",
					JsonSerializer.Serialize(invocation, JsonOptions),
					invocation.Timestamp),
				CancellationToken.None);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// The in-memory counters and the session record still carry the total; only the audit
			// line is lost, and it is not worth failing a turn over.
		}
	}

	/// <summary>One session's counters. Small enough that a lock is cheaper than anything clever.</summary>
	private sealed class SessionCounters
	{
		private readonly Lock _gate = new();
		private SessionUsage _usage = SessionUsage.None;
		private bool _dirty;

		/// <summary>The turn in progress, or the last one to have run. Null until a turn opens.</summary>
		private TurnUsage? _turn;

		/// <summary>Queued audit appends, chained so a session's lines are written in order.</summary>
		private Task _appends = Task.CompletedTask;

		public bool Loaded { get; private set; }

		public void Seed(SessionUsage? persisted)
		{
			lock (_gate)
			{
				// Only an untouched session adopts the persisted total. A turn that started while the
				// load was in flight has already counted requests that the persisted record predates.
				if (!Loaded && _usage.Invocations == 0 && persisted is not null) _usage = persisted;
				Loaded = true;
			}
		}

		/// <summary>
		/// Start counting a new turn. The session totals are untouched: a turn boundary resets what the
		/// turn has spent, never what the session has.
		/// </summary>
		public void BeginTurn(int turnNumber)
		{
			lock (_gate) _turn = new TurnUsage(turnNumber, TokenUsage.None);
		}

		/// <summary>
		/// Fold one request into both counters and return it stamped with where it landed. A request
		/// arriving with no turn open — a subagent's run, which drives an agent directly rather than
		/// through the runtime — opens an implicit turn rather than being dropped or merged into the
		/// previous one.
		/// </summary>
		public ModelInvocationUsage Add(ModelInvocationUsage invocation)
		{
			lock (_gate)
			{
				_turn ??= new TurnUsage(invocation.TurnNumber, TokenUsage.None);
				var filed = invocation with
				{
					TurnNumber = _turn.TurnNumber,
					CallNumber = _turn.ModelCalls + 1,
					CallId = invocation.CallId == Guid.Empty ? Guid.NewGuid() : invocation.CallId,
				};
				_turn = _turn.Add(filed);
				_usage = _usage.Add(filed);
				_dirty = true;
				return filed;
			}
		}

		public SessionUsage Snapshot()
		{
			lock (_gate) return _usage;
		}

		/// <summary>All three levels as one consistent reading, taken under a single lock.</summary>
		public UsageSnapshot View()
		{
			lock (_gate)
			{
				var last = _usage.LastCall;
				return new UsageSnapshot(_usage, _turn, last, ContextUsage.From(last));
			}
		}

		/// <summary>Whether anything has changed since the last flush, clearing the flag if so.</summary>
		public bool TakeDirty()
		{
			lock (_gate)
			{
				if (!_dirty) return false;
				_dirty = false;
				return true;
			}
		}

		public void MarkDirty()
		{
			lock (_gate) _dirty = true;
		}

		/// <summary>Queue an append behind whatever this session has already queued.</summary>
		public void Queue(Func<Task> append)
		{
			lock (_gate) _appends = _appends.ContinueWith(_ => append(), TaskScheduler.Default).Unwrap();
		}

		/// <summary>Wait for the appends queued so far.</summary>
		public Task DrainAsync()
		{
			lock (_gate) return _appends;
		}
	}
}

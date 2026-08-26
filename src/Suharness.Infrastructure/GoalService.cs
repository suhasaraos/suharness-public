using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// In-memory goal service keyed by session id. Goals are persisted as JSONL
/// goal/change events so they survive restarts, and the process-local activation
/// flag is re-disarmed on resume.
/// </summary>
public sealed class GoalService(ISessionStore sessions) : IGoalService
{
	/// <summary>Automatic continuation rounds allowed when create_goal does not set a cap.</summary>
	public const int DefaultMaxGoalRounds = 256;

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private sealed record GoalRecord(GoalSnapshot Snapshot, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int RoundsStarted)
	{
		public GoalRecord Mutate(GoalSnapshot snapshot, DateTimeOffset now) =>
			new(snapshot, CreatedAt, now, RoundsStarted);
		public GoalRecord IncrementRounds() =>
			new(Snapshot, CreatedAt, UpdatedAt, RoundsStarted + 1);
	}

	private readonly ConcurrentDictionary<Guid, GoalRecord> _goals = new();
	private readonly ConcurrentDictionary<Guid, bool> _armed = new();
	private readonly ConcurrentDictionary<Guid, bool> _rehydrated = new();
	private readonly ConcurrentDictionary<Guid, Task> _writes = new();

	public event Action<Guid>? Changed;

	public GoalView? Get(Guid sessionId)
	{
		if (!_goals.TryGetValue(sessionId, out var record)) return null;
		var snapshot = record.Snapshot;
		return new GoalView(
			snapshot.Id, snapshot.Revision, snapshot.Objective, snapshot.Phase,
			record.RoundsStarted, snapshot.MaxGoalRounds,
			record.CreatedAt, record.UpdatedAt,
			_armed.TryGetValue(sessionId, out var armed) && armed ? GoalActivation.Armed : GoalActivation.Disarmed,
			snapshot.BlockedReason);
	}

	public GoalView Create(Guid sessionId, string objective, int? maxGoalRounds = null)
	{
		if (_goals.ContainsKey(sessionId))
			throw new InvalidOperationException("A goal already exists for this session.");

		if (string.IsNullOrWhiteSpace(objective))
			throw new ArgumentException("objective must be non-empty.", nameof(objective));

		// Every goal carries a finite round cap of 256: an uncapped goal
		// would let the round driver re-enter the agent loop forever with no human checkpoint.
		var max = maxGoalRounds is > 0 ? maxGoalRounds.Value : DefaultMaxGoalRounds;
		var now = DateTimeOffset.UtcNow;
		var snapshot = new GoalSnapshot(
			Guid.NewGuid().ToString("N"), 1, objective.Trim(),
			GoalPhase.Active, max, now, now);
		_goals[sessionId] = new GoalRecord(snapshot, now, now, 0);
		_armed[sessionId] = true;
		Persist(sessionId, GoalOperation.Create, snapshot, now, 0);
		Changed?.Invoke(sessionId);
		return Get(sessionId)!;
	}

	public GoalView Edit(Guid sessionId, int expectedRevision, string? objective, int? maxGoalRounds)
	{
		var record = RequireGoal(sessionId, expectedRevision);
		if (record.Snapshot.Phase is GoalPhase.Complete or GoalPhase.Blocked)
			throw new InvalidOperationException($"Cannot edit a goal in phase '{record.Snapshot.Phase}'.");
		var hasObjective = !string.IsNullOrWhiteSpace(objective);
		var hasRounds = maxGoalRounds is > 0;
		if (!hasObjective && !hasRounds)
			throw new ArgumentException("At least one of objective or max_goal_rounds must be provided for edit.");

		var now = DateTimeOffset.UtcNow;
		var snapshot = record.Snapshot with
		{
			Revision = record.Snapshot.Revision + 1,
			Objective = hasObjective ? objective!.Trim() : record.Snapshot.Objective,
			MaxGoalRounds = hasRounds ? maxGoalRounds!.Value : record.Snapshot.MaxGoalRounds,
			UpdatedAt = now,
		};
		_goals[sessionId] = record.Mutate(snapshot, now);
		Persist(sessionId, GoalOperation.Edit, snapshot, now, record.RoundsStarted);
		Changed?.Invoke(sessionId);
		return Get(sessionId)!;
	}

	public GoalView Pause(Guid sessionId, int expectedRevision)
	{
		var record = RequireGoal(sessionId, expectedRevision);
		if (record.Snapshot.Phase != GoalPhase.Active)
			throw new InvalidOperationException($"Cannot pause a goal in phase '{record.Snapshot.Phase}'.");
		return Transition(sessionId, record, GoalPhase.Paused, GoalOperation.Pause);
	}

	public GoalView Resume(Guid sessionId, int expectedRevision)
	{
		var record = RequireGoal(sessionId, expectedRevision);
		if (record.Snapshot.Phase != GoalPhase.Paused)
			throw new InvalidOperationException($"Cannot resume a goal in phase '{record.Snapshot.Phase}'.");
		if (record.RoundsStarted >= record.Snapshot.MaxGoalRounds)
			throw new InvalidOperationException(
				$"Goal '{record.Snapshot.Id}' exhausted {record.Snapshot.MaxGoalRounds} goal rounds; raise max_goal_rounds with action edit before resuming.");
		_armed[sessionId] = true;
		return Transition(sessionId, record, GoalPhase.Active, GoalOperation.Resume);
	}

	public GoalView Complete(Guid sessionId, int expectedRevision)
	{
		var record = RequireGoal(sessionId, expectedRevision);
		if (record.Snapshot.Phase == GoalPhase.Complete)
			throw new InvalidOperationException("Goal is already complete.");
		_armed[sessionId] = false;
		return Transition(sessionId, record, GoalPhase.Complete, GoalOperation.Complete);
	}

	public GoalView Block(Guid sessionId, int expectedRevision, GoalBlockReason reason)
	{
		var record = RequireGoal(sessionId, expectedRevision);
		if (record.Snapshot.Phase == GoalPhase.Blocked)
			throw new InvalidOperationException("Goal is already blocked.");
		if (string.IsNullOrWhiteSpace(reason.Code) || string.IsNullOrWhiteSpace(reason.Message))
			throw new ArgumentException("Block reason must have a non-empty code and message.");
		_armed[sessionId] = false;
		return Transition(sessionId, record, GoalPhase.Blocked, GoalOperation.Block, reason);
	}

	public bool IsArmed(Guid sessionId) =>
		_armed.TryGetValue(sessionId, out var armed) && armed;

	public void Disarm(Guid sessionId)
	{
		_armed[sessionId] = false;
		Changed?.Invoke(sessionId);
	}

	private GoalView Transition(Guid sessionId, GoalRecord record, GoalPhase phase, GoalOperation operation, GoalBlockReason? reason = null)
	{
		var now = DateTimeOffset.UtcNow;
		var snapshot = record.Snapshot with
		{
			Revision = record.Snapshot.Revision + 1,
			Phase = phase,
			BlockedReason = reason,
			UpdatedAt = now,
		};
		_goals[sessionId] = record.Mutate(snapshot, now);
		Persist(sessionId, operation, snapshot, now, record.RoundsStarted);
		Changed?.Invoke(sessionId);
		return Get(sessionId)!;
	}

	private GoalRecord RequireGoal(Guid sessionId, int expectedRevision)
	{
		if (!_goals.TryGetValue(sessionId, out var record))
			throw new InvalidOperationException("No goal exists for this session.");
		if (record.Snapshot.Revision != expectedRevision)
			throw new InvalidOperationException($"Stale revision: expected {expectedRevision}, current is {record.Snapshot.Revision}.");
		return record;
	}

	/// <summary>Count a started goal-continuation round.</summary>
	public void RecordRound(Guid sessionId)
	{
		if (!_goals.TryGetValue(sessionId, out var record)) return;
		_goals[sessionId] = record.IncrementRounds();
		Changed?.Invoke(sessionId);
	}

	/// <summary>
	/// Whether the runtime should drive another automatic round: the goal is active, activation is
	/// still armed, and the round cap has not been reached. A goal that hits its cap is left active
	/// and disarmed so a human can inspect it and resume.
	/// </summary>
	public bool ShouldContinue(Guid sessionId)
	{
		if (!_goals.TryGetValue(sessionId, out var record)) return false;
		if (record.Snapshot.Phase != GoalPhase.Active) return false;
		if (!IsArmed(sessionId)) return false;
		return record.RoundsStarted < record.Snapshot.MaxGoalRounds;
	}

	/// <summary>
	/// Rebuild a session's goal by folding its persisted goal/change events. Rehydration always
	/// lands disarmed: automatic continuation authority does not survive a restart or a fork, so a
	/// resumed goal waits for a direct human resume before it drives rounds again.
	/// </summary>
	public async ValueTask RehydrateAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		if (_goals.ContainsKey(sessionId) || !_rehydrated.TryAdd(sessionId, true)) return;
		try
		{
			var transcript = await sessions.GetAsync(sessionId, cancellationToken);
			if (transcript is null) return;

			GoalRecord? folded = null;
			foreach (var item in transcript.Events)
			{
				if (item.Type != "goal/change") continue;
				var change = ReadChange(item.Content);
				if (change is null) continue;
				if (change.Operation == GoalOperation.Clear) { folded = null; continue; }
				folded = new GoalRecord(change.Snapshot, change.Snapshot.CreatedAt, change.UpdatedAt, change.RoundsStarted);
			}

			if (folded is null) return;
			_goals[sessionId] = folded;
			_armed[sessionId] = false;
			Changed?.Invoke(sessionId);
		}
		catch (Exception exception) when (exception is IOException or JsonException)
		{
			// A damaged transcript must not make the session unopenable; the goal is simply absent.
		}
	}

	private sealed record GoalChange(GoalOperation Operation, GoalSnapshot Snapshot, DateTimeOffset UpdatedAt, int RoundsStarted);

	/// <summary>Parse one persisted goal/change payload, returning null when it is not readable.</summary>
	private static GoalChange? ReadChange(string content)
	{
		try
		{
			using var document = JsonDocument.Parse(content);
			var root = document.RootElement;
			if (!root.TryGetProperty("goal", out var goal)) return null;
			if (!Enum.TryParse<GoalOperation>(root.GetProperty("operation").GetString(), ignoreCase: true, out var operation))
				return null;
			if (!Enum.TryParse<GoalPhase>(goal.GetProperty("phase").GetString(), ignoreCase: true, out var phase))
				return null;

			GoalBlockReason? blocked = null;
			if (goal.TryGetProperty("blockedReason", out var reason) && reason.ValueKind == JsonValueKind.Object)
			{
				blocked = new GoalBlockReason(
					reason.GetProperty("code").GetString() ?? "unknown",
					reason.GetProperty("message").GetString() ?? string.Empty);
			}

			var createdAt = root.TryGetProperty("createdAt", out var created) ? created.GetDateTimeOffset() : DateTimeOffset.UtcNow;
			var updatedAt = root.TryGetProperty("updatedAt", out var updated) ? updated.GetDateTimeOffset() : createdAt;
			var snapshot = new GoalSnapshot(
				goal.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
				goal.GetProperty("revision").GetInt32(),
				goal.GetProperty("objective").GetString() ?? string.Empty,
				phase,
				goal.TryGetProperty("maxGoalRounds", out var max) ? max.GetInt32() : int.MaxValue,
				createdAt,
				updatedAt,
				blocked);
			var rounds = root.TryGetProperty("roundsStarted", out var started) ? started.GetInt32() : 0;
			return new GoalChange(operation, snapshot, updatedAt, rounds);
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
		{
			return null;
		}
	}

	private void Persist(Guid sessionId, GoalOperation op, GoalSnapshot snapshot, DateTimeOffset now, int rounds)
	{
		var write = PersistAsync(sessionId, op, snapshot, now, rounds);
		// Serialize writes so folding the log on rehydrate replays mutations in commit order.
		_writes.AddOrUpdate(sessionId, write, (_, pending) => pending.ContinueWith(_ => write, TaskScheduler.Default).Unwrap());
	}

	/// <summary>Flush any goal writes still in flight for a session.</summary>
	public Task DrainAsync(Guid sessionId) =>
		_writes.TryGetValue(sessionId, out var pending) ? pending : Task.CompletedTask;

	private async Task PersistAsync(Guid sessionId, GoalOperation op, GoalSnapshot snapshot, DateTimeOffset now, int rounds)
	{
		try
		{
			var meta = JsonSerializer.Serialize(new
			{
				kind = "goal/change",
				version = 1,
				operation = op.ToString().ToLowerInvariant(),
				goal = new
				{
					id = snapshot.Id,
					revision = snapshot.Revision,
					objective = snapshot.Objective,
					phase = snapshot.Phase.ToString().ToLowerInvariant(),
					maxGoalRounds = snapshot.MaxGoalRounds,
					blockedReason = snapshot.BlockedReason,
				},
				roundsStarted = rounds,
				createdAt = snapshot.CreatedAt,
				updatedAt = now,
			}, JsonOptions);
			await sessions.AppendAsync(new SessionEvent(
				Guid.NewGuid(), sessionId, "goal/change", "system", meta, now), CancellationToken.None);
		}
		catch (Exception exception) when (exception is IOException or JsonException)
		{
			// Persistence is best-effort: the in-memory goal remains authoritative for this process.
		}
	}
}

using Suharness.Abstractions;
using Suharness.Core;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Scheduler and dispatcher for prompt jobs. Evaluates dependency states and schedules,
/// updates statuses, and executes ready prompt jobs through the existing HarnessRuntime execution path.
/// </summary>
public sealed class PromptQueueService : IPromptQueueService, IDisposable
{
	private readonly IPromptQueueStore _store;
	private readonly HarnessRuntime _runtime;
	private readonly IModelConfigurationService? _modelService;
	private readonly IPlanModeStore? _planModeStore;
	private readonly ISessionStore? _sessionStore;
	private readonly ConcurrentDictionary<string, PromptJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly SemaphoreSlim _processorGate = new(1, 1);
	private readonly CancellationTokenSource _cts = new();
	private bool _initialized;

	public event Action? Changed;

	public PromptQueueService(
		IPromptQueueStore store,
		HarnessRuntime runtime,
		IModelConfigurationService? modelService = null,
		IPlanModeStore? planModeStore = null,
		ISessionStore? sessionStore = null)
	{
		_store = store;
		_runtime = runtime;
		_modelService = modelService;
		_planModeStore = planModeStore;
		_sessionStore = sessionStore;
	}

	private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
	{
		if (_initialized) return;
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (_initialized) return;
			var loaded = await _store.LoadJobsAsync(cancellationToken);
			var recovered = new List<PromptJob>();
			foreach (var storedJob in loaded)
			{
				// A persisted RUNNING state belongs to the previous process. Nothing can still be
				// executing it after a restart, so put it back into evaluation instead of leaving it
				// stuck forever.
				var job = storedJob.Status == PromptJobStatus.Running
					? storedJob with
					{
						Status = PromptJobStatus.Queued,
						StartedAt = null,
						Error = "The previous run was interrupted; the job was queued again."
					}
					: storedJob;
				_jobs[job.PromptId] = job;
				if (!ReferenceEquals(job, storedJob)) recovered.Add(job);
			}
			if (recovered.Count > 0)
				await _store.SaveJobsAsync(recovered, cancellationToken);
			_initialized = true;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<PromptJob>> ListAsync(string? workspace = null, CancellationToken cancellationToken = default)
	{
		await EnsureLoadedAsync(cancellationToken);
		var all = _jobs.Values.AsEnumerable();
		if (!string.IsNullOrWhiteSpace(workspace))
		{
			var canonical = Path.GetFullPath(workspace);
			all = all.Where(j => string.Equals(Path.GetFullPath(j.Workspace), canonical, StringComparison.OrdinalIgnoreCase));
		}
		return all.OrderByDescending(j => j.Priority)
			.ThenBy(j => j.CreatedAt)
			.ThenBy(j => j.PromptId)
			.ToList();
	}

	public async Task<PromptJob?> GetAsync(string promptId, CancellationToken cancellationToken = default)
	{
		await EnsureLoadedAsync(cancellationToken);
		return _jobs.TryGetValue(promptId, out var job) ? job : null;
	}

	public async Task<PromptJob> EnqueueAsync(PromptJobDraft draft, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(draft.Prompt))
			throw new ArgumentException("Prompt cannot be empty.", nameof(draft));
		if (string.IsNullOrWhiteSpace(draft.Workspace))
			throw new ArgumentException("A workspace is required.", nameof(draft));

		// EnsureLoadedAsync owns the state gate. Calling it after taking the same non-reentrant
		// semaphore deadlocks the first enqueue (and used to freeze the queue modal).
		await EnsureLoadedAsync(cancellationToken);
		await _gate.WaitAsync(cancellationToken);
		try
		{
			// Validate dependencies exist and no cycles
			if (draft.Dependencies is { Count: > 0 })
			{
				foreach (var dep in draft.Dependencies)
				{
					if (!_jobs.ContainsKey(dep.DependsOnPromptId))
						throw new InvalidOperationException($"Dependency job '{dep.DependsOnPromptId}' does not exist.");
				}
			}

			var seq = await _store.NextIdSequenceAsync(cancellationToken);
			var promptId = $"P{seq:D6}";
			var now = DateTimeOffset.UtcNow;

			var status = EvaluateInitialStatus(draft, now);

			var job = new PromptJob(
				PromptId: promptId,
				Prompt: draft.Prompt.Trim(),
				Workspace: string.IsNullOrWhiteSpace(draft.Workspace) ? string.Empty : Path.GetFullPath(draft.Workspace),
				Provider: draft.Provider,
				Model: draft.Model,
				ReasoningEffort: draft.ReasoningEffort,
				AccessMode: draft.AccessMode,
				PlanMode: draft.PlanMode,
				Priority: draft.Priority,
				RunAt: draft.RunAt,
				Status: status,
				Dependencies: draft.Dependencies,
				CreatedAt: now,
				SessionId: draft.SessionId);

			// Check for cycle with the new job hypothetically added
			ValidateCycles(job);

			_jobs[promptId] = job;
			await _store.SaveJobAsync(job, cancellationToken);
			Changed?.Invoke();

			TriggerProcessing();

			return job;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<bool> CancelAsync(string promptId, CancellationToken cancellationToken = default)
	{
		await EnsureLoadedAsync(cancellationToken);
		CancellationTokenSource? runningCancellation = null;
		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!_jobs.TryGetValue(promptId, out var job)) return false;

			if (job.Status is PromptJobStatus.Completed or PromptJobStatus.Failed or PromptJobStatus.Cancelled)
				return false;

			var updated = job with
			{
				Status = PromptJobStatus.Cancelled,
				CompletedAt = DateTimeOffset.UtcNow,
				Error = "Job was cancelled."
			};

			_jobs[promptId] = updated;
			await _store.SaveJobAsync(updated, cancellationToken);

			// Dependents of cancelled job should become BLOCKED
			await UpdateDependentStatusesAsync(cancellationToken);
			_runningJobs.TryGetValue(promptId, out runningCancellation);

			Changed?.Invoke();
		}
		finally
		{
			_gate.Release();
		}

		// Cancel outside the state gate: cancellation callbacks are allowed to complete work and
		// may need that gate to record the final state.
		try { runningCancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
		TriggerProcessing();
		return true;
	}

	private PromptJobStatus EvaluateInitialStatus(PromptJobDraft draft, DateTimeOffset now)
	{
		var hasPendingDependencies = false;
		if (draft.Dependencies is { Count: > 0 })
		{
			foreach (var dep in draft.Dependencies)
			{
				if (_jobs.TryGetValue(dep.DependsOnPromptId, out var depJob))
				{
					if (dep.Type == DependencyType.AfterSuccess)
					{
						if (depJob.Status is PromptJobStatus.Failed or PromptJobStatus.Cancelled or PromptJobStatus.Blocked)
							return PromptJobStatus.Blocked;
						if (depJob.Status != PromptJobStatus.Completed)
							hasPendingDependencies = true;
					}
				}
			}
		}

		if (hasPendingDependencies) return PromptJobStatus.Waiting;

		if (draft.RunAt is { } runAt && runAt > now)
			return PromptJobStatus.Waiting;

		return PromptJobStatus.Ready;
	}

	private void ValidateCycles(PromptJob newJob)
	{
		var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		foreach (var job in _jobs.Values)
		{
			adjacency[job.PromptId] = job.Dependencies?.Select(d => d.DependsOnPromptId).ToList() ?? [];
		}
		adjacency[newJob.PromptId] = newJob.Dependencies?.Select(d => d.DependsOnPromptId).ToList() ?? [];

		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var recStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		bool HasCycle(string node)
		{
			visited.Add(node);
			recStack.Add(node);

			if (adjacency.TryGetValue(node, out var neighbors))
			{
				foreach (var neighbor in neighbors)
				{
					if (!visited.Contains(neighbor) && HasCycle(neighbor))
						return true;
					if (recStack.Contains(neighbor))
						return true;
				}
			}

			recStack.Remove(node);
			return false;
		}

		foreach (var node in adjacency.Keys)
		{
			if (!visited.Contains(node) && HasCycle(node))
			{
				throw new InvalidOperationException("Dependency cycle detected.");
			}
		}
	}

	private async Task UpdateDependentStatusesAsync(CancellationToken cancellationToken)
	{
		var now = DateTimeOffset.UtcNow;
		var modified = new List<PromptJob>();

		foreach (var job in _jobs.Values)
		{
			if (job.Status is PromptJobStatus.Completed or PromptJobStatus.Running or PromptJobStatus.Cancelled or PromptJobStatus.Failed)
				continue;

			var hasBlockedDependency = false;
			var allDependenciesSucceeded = true;

			if (job.Dependencies is { Count: > 0 })
			{
				foreach (var dep in job.Dependencies)
				{
					if (_jobs.TryGetValue(dep.DependsOnPromptId, out var depJob))
					{
						if (dep.Type == DependencyType.AfterSuccess)
						{
							if (depJob.Status is PromptJobStatus.Failed or PromptJobStatus.Cancelled or PromptJobStatus.Blocked)
							{
								hasBlockedDependency = true;
								break;
							}
							if (depJob.Status != PromptJobStatus.Completed)
							{
								allDependenciesSucceeded = false;
							}
						}
					}
					else
					{
						hasBlockedDependency = true;
						break;
					}
				}
			}

			if (hasBlockedDependency)
			{
				if (job.Status != PromptJobStatus.Blocked)
				{
					var updated = job with { Status = PromptJobStatus.Blocked, Error = "Dependency failed or was cancelled." };
					_jobs[job.PromptId] = updated;
					modified.Add(updated);
				}
			}
			else if (allDependenciesSucceeded)
			{
				var isDue = job.RunAt is null || job.RunAt.Value <= now;
				if (isDue && job.Status != PromptJobStatus.Ready)
				{
					var updated = job with { Status = PromptJobStatus.Ready };
					_jobs[job.PromptId] = updated;
					modified.Add(updated);
				}
				else if (!isDue && job.Status != PromptJobStatus.Waiting)
				{
					var updated = job with { Status = PromptJobStatus.Waiting };
					_jobs[job.PromptId] = updated;
					modified.Add(updated);
				}
			}
		}

		if (modified.Count > 0)
		{
			await _store.SaveJobsAsync(modified, cancellationToken);
			Changed?.Invoke();
		}
	}

	public async Task ProcessQueueAsync(CancellationToken cancellationToken = default)
	{
		await EnsureLoadedAsync(cancellationToken);
		await _processorGate.WaitAsync(cancellationToken);
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				PromptJob? runningJob = null;
				CancellationTokenSource? jobCancellation = null;

				await _gate.WaitAsync(cancellationToken);
				try
				{
					await UpdateDependentStatusesAsync(cancellationToken);
					var readyJob = _jobs.Values
						.Where(j => j.Status == PromptJobStatus.Ready)
						.OrderByDescending(j => j.Priority)
						.ThenBy(j => j.CreatedAt)
						.ThenBy(j => j.PromptId)
						.FirstOrDefault();

					if (readyJob is null) return;

					var sessionId = readyJob.SessionId ?? Guid.NewGuid();
					runningJob = readyJob with
					{
						Status = PromptJobStatus.Running,
						StartedAt = DateTimeOffset.UtcNow,
						CompletedAt = null,
						Error = null,
						SessionId = sessionId
					};
					_jobs[runningJob.PromptId] = runningJob;
					await _store.SaveJobAsync(runningJob, cancellationToken);
					jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
					_runningJobs[runningJob.PromptId] = jobCancellation;
					Changed?.Invoke();
				}
				finally
				{
					_gate.Release();
				}

				try
				{
					await ExecuteJobAsync(runningJob, jobCancellation.Token);
				}
				finally
				{
					_runningJobs.TryRemove(runningJob.PromptId, out _);
					jobCancellation.Dispose();
				}
			}
		}
		finally
		{
			_processorGate.Release();
		}
	}

	private async Task ExecuteJobAsync(PromptJob job, CancellationToken cancellationToken)
	{
		var startTime = DateTimeOffset.UtcNow;
		try
		{
			var sessionId = job.SessionId ?? Guid.NewGuid();

			// Ensure session exists or create it
			if (_sessionStore is not null)
			{
				var existingTranscript = await _sessionStore.GetAsync(sessionId, cancellationToken);
				if (existingTranscript is null)
				{
					var newSession = new HarnessSession(
						sessionId,
						job.Workspace,
						$"Job {job.PromptId}",
						startTime,
						startTime,
						AccessMode: MapAccessMode(job.AccessMode),
						PlanMode: job.PlanMode);
					await _sessionStore.SaveSessionAsync(newSession, cancellationToken);
				}
				else
				{
					var updatedSession = existingTranscript.Session with
					{
						AccessMode = MapAccessMode(job.AccessMode),
						PlanMode = job.PlanMode,
						UpdatedAt = startTime
					};
					await _sessionStore.SaveSessionAsync(updatedSession, cancellationToken);
				}
			}

			if (job.PlanMode && _modelService is not null)
			{
				var team = await _modelService.GetTeamConfigurationAsync(sessionId, cancellationToken);
				if (team.Enabled)
					await _modelService.SetTeamConfigurationAsync(sessionId, team with { Enabled = false }, cancellationToken);
			}

			// Configure PlanMode in store
			if (_planModeStore is not null)
			{
				await _planModeStore.SetAsync(sessionId, job.PlanMode, cancellationToken);
			}

			// Configure ModelSelection if model is specified. The job's reasoning effort is carried
			// onto the selection, which is what actually reaches the request: a queued job that asked
			// for a reasoning budget would otherwise store the choice and run without it.
			if (_modelService is not null && !string.IsNullOrWhiteSpace(job.Provider) && !string.IsNullOrWhiteSpace(job.Model))
			{
				await _modelService.SelectAsync(
					sessionId,
					new ModelSelection(job.Provider, job.Model, ParseReasoningEffort(job.ReasoningEffort)),
					cancellationToken);
			}

			// Execute prompt
			await _runtime.SubmitAsync(sessionId, job.Prompt, cancellationToken: cancellationToken);

			// Check final transcript for results or errors
			string? resultSummary = null;
			if (_sessionStore is not null)
			{
				var finalTranscript = await _sessionStore.GetAsync(sessionId, cancellationToken);
				var lastMessage = finalTranscript?.Events
					.LastOrDefault(e => e.Type == "message" && e.Role == "assistant");
				resultSummary = lastMessage?.Content;
			}

			await FinishJobAsync(job.PromptId, current => current with
			{
				Status = PromptJobStatus.Completed,
				CompletedAt = DateTimeOffset.UtcNow,
				Result = resultSummary ?? "Completed successfully.",
				Error = null,
				SessionId = sessionId
			});
		}
		catch (OperationCanceledException)
		{
			await FinishJobAsync(job.PromptId, current => current.Status == PromptJobStatus.Cancelled
				? current
				: current with
				{
					Status = PromptJobStatus.Queued,
					StartedAt = null,
					Error = "Execution was interrupted; the job was queued again."
				});
		}
		catch (Exception ex)
		{
			await FinishJobAsync(job.PromptId, current => current with
			{
				Status = PromptJobStatus.Failed,
				CompletedAt = DateTimeOffset.UtcNow,
				Error = ex.Message
			});
		}
	}

	private async Task FinishJobAsync(string promptId, Func<PromptJob, PromptJob> update)
	{
		await _gate.WaitAsync(CancellationToken.None);
		try
		{
			if (!_jobs.TryGetValue(promptId, out var current)) return;
			// A user's cancellation wins a race with a response arriving from the model.
			var updated = current.Status == PromptJobStatus.Cancelled ? current : update(current);
			_jobs[promptId] = updated;
			if (!ReferenceEquals(updated, current))
				await _store.SaveJobAsync(updated, CancellationToken.None);
			await UpdateDependentStatusesAsync(CancellationToken.None);
			Changed?.Invoke();
		}
		finally
		{
			_gate.Release();
		}
	}

	private void TriggerProcessing()
	{
		_ = Task.Run(async () =>
		{
			try { await ProcessQueueAsync(_cts.Token); }
			catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
		});
	}

	private static HarnessAccessMode MapAccessMode(PromptAccessMode mode) => mode switch
	{
		PromptAccessMode.Read => HarnessAccessMode.ReadOnly,
		PromptAccessMode.Write => HarnessAccessMode.WorkspaceWrite,
		PromptAccessMode.Execute => HarnessAccessMode.FullAccess,
		_ => HarnessAccessMode.WorkspaceWrite,
	};

	public void Dispose()
	{
		_cts.Cancel();
		foreach (var cancellation in _runningJobs.Values)
		{
			try { cancellation.Cancel(); }
			catch (ObjectDisposedException) { }
		}
		_cts.Dispose();
	}

	/// <summary>
	/// The reasoning budget a job asked for, as a level the model layer understands. A job is
	/// persisted as text so an unrecognised value survives a round-trip rather than failing to load;
	/// anything that is not a defined level means no reasoning parameter is sent, which is the same
	/// as asking for none. The IsDefined check is what rejects a hand-edited numeric value: TryParse
	/// accepts "99" as a valid enum, and it would reach the wire mapping as an unnamed level.
	/// </summary>
	private static ReasoningEffortLevel? ParseReasoningEffort(string? effort) =>
		Enum.TryParse<ReasoningEffortLevel>(effort, ignoreCase: true, out var level)
		&& Enum.IsDefined(level)
			? level
			: null;
}

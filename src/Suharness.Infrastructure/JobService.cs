using Suharness.Abstractions;
using System.Collections.Concurrent;

namespace Suharness.Infrastructure;

/// <summary>
/// Per-session background job registry. Jobs are owned by a session id; the
/// runtime fences read/kill/wait by that ownership. Completion is delivered via
/// the Changed event so the inbox can surface it to the owner.
/// </summary>
public sealed class JobService : IJobService
{
	private sealed class Job
	{
		public string Id { get; }
		public JobKind Kind { get; }
		public string Label { get; }
		public Guid OwnerSessionId { get; }
		public int? OutputLimitBytes { get; }
		public DateTimeOffset StartedAt { get; }
		public JobStatus Status;
		public string? Detail;
		public DateTimeOffset? FinishedAt;
		public Func<string?, bool>? Cancel;
		public Func<string>? ReadOutput;
		public Task<JobOutcome> Done;
		public readonly StringBuilderAccumulator Output = new();

		public Job(string id, JobKind kind, string label, Guid ownerSessionId, int? outputLimitBytes, DateTimeOffset startedAt,
			Func<string?, bool>? cancel, Func<string>? readOutput, Task<JobOutcome> done)
		{
			Id = id; Kind = kind; Label = label; OwnerSessionId = ownerSessionId;
			OutputLimitBytes = outputLimitBytes; StartedAt = startedAt;
			Cancel = cancel; ReadOutput = readOutput; Done = done;
		}
	}

	/// <summary>Thread-safe ring buffer for streamed output.</summary>
	private sealed class StringBuilderAccumulator
	{
		private readonly System.Text.StringBuilder _buffer = new();
		private int _readIndex;
		public void Append(string text) { lock (this) { _buffer.Append(text); } }
		public string Consume()
		{
			lock (this)
			{
				var unread = _buffer.ToString(_readIndex, _buffer.Length - _readIndex);
				_readIndex = _buffer.Length;
				return unread;
			}
		}
	}

	private readonly ConcurrentDictionary<string, Job> _jobs = new();
	private int _counter;

	public event Action<Guid>? Changed;

	public string Start(Guid ownerSessionId, JobKind kind, string label, Func<CancellationToken, JobHooks> factory)
	{
		var id = $"{kind.ToString().ToLowerInvariant()}-{Interlocked.Increment(ref _counter)}";
		var job = new Job(id, kind, label, ownerSessionId, null, DateTimeOffset.UtcNow, null, null, Task.FromResult(new JobOutcome(JobStatus.Failed, "not started")));
		_jobs[id] = job;
		_ = RunAsync(job, factory);
		return id;
	}

	private async Task RunAsync(Job job, Func<CancellationToken, JobHooks> factory)
	{
		var cts = new CancellationTokenSource();
		JobHooks hooks;
		try
		{
			hooks = factory(cts.Token);
		}
		catch (Exception exception)
		{
			Settle(job, JobStatus.Failed, exception.Message);
			return;
		}

		job.Cancel = hooks.Cancel;
		job.ReadOutput = hooks.ReadOutput;
		job.Done = hooks.Done;

		// Only jobs that stream output need the poller; a subagent job has none, and spinning a
		// 50 ms timer for its whole lifetime would burn a thread-pool slot doing nothing.
		if (hooks.ReadOutput is not null) _ = MonitorAsync(job, hooks);

		try
		{
			var outcome = await hooks.Done;
			Settle(job, outcome.Status, outcome.Detail, outcome.Output);
		}
		catch (OperationCanceledException) when (cts.IsCancellationRequested)
		{
			Settle(job, JobStatus.Killed);
		}
		catch (Exception exception)
		{
			Settle(job, JobStatus.Failed, exception.Message);
		}
	}

	private async Task MonitorAsync(Job job, JobHooks hooks)
	{
		// Poll readOutput periodically so job_output sees streamed content.
		while (job.Status is JobStatus.Running or JobStatus.Stopping)
		{
			try
			{
				var text = hooks.ReadOutput?.Invoke();
				if (!string.IsNullOrEmpty(text)) job.Output.Append(text);
			}
			catch { /* best-effort */ }
			await Task.Delay(50);
		}
	}

	private void Settle(Job job, JobStatus status, string? detail = null, string? output = null)
	{
		lock (job)
		{
			if (job.FinishedAt is not null) return;
			job.FinishedAt = DateTimeOffset.UtcNow;
		}
		job.Status = status;
		job.Detail = detail;
		if (output is not null) job.Output.Append(output);
		Changed?.Invoke(job.OwnerSessionId);
	}

	public IReadOnlyList<JobSnapshot> List(Guid ownerSessionId) =>
		[.. _jobs.Values
			.Where(job => job.OwnerSessionId == ownerSessionId)
			.Select(SnapshotOf)
			.OrderBy(job => job.StartedAt)];

	public JobRead Read(string jobId, Guid ownerSessionId)
	{
		var job = Require(jobId, ownerSessionId);
		return new JobRead(job.Output.Consume(), SnapshotOf(job));
	}

	public bool Kill(string jobId, Guid ownerSessionId, string? reason = null)
	{
		var job = Require(jobId, ownerSessionId);
		if (job.Status is JobStatus.Completed or JobStatus.Killed or JobStatus.Failed)
			return false;
		job.Status = JobStatus.Stopping;
		Changed?.Invoke(job.OwnerSessionId);
		// Without a cancel hook nothing will ever stop the work, so the job would sit in Stopping
		// forever and job_output would never report a terminal status. Settle it here instead.
		if (job.Cancel is null || !job.Cancel.Invoke(reason))
			Settle(job, JobStatus.Killed, reason ?? "killed");
		return true;
	}

	public async Task WaitAsync(string jobId, int timeoutMs, Guid ownerSessionId, CancellationToken cancellationToken = default)
	{
		var job = Require(jobId, ownerSessionId);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		linked.CancelAfter(Math.Max(1, timeoutMs));
		try
		{
			while (job.Status is JobStatus.Running or JobStatus.Stopping)
			{
				linked.Token.ThrowIfCancellationRequested();
				await Task.Delay(50, linked.Token);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// A timed-out wait returns the current state; leave the job alive.
		}
	}

	public JobSnapshot Get(string jobId, Guid ownerSessionId) => SnapshotOf(Require(jobId, ownerSessionId));

	private Job Require(string jobId, Guid ownerSessionId)
	{
		if (!_jobs.TryGetValue(jobId, out var job))
			throw new InvalidOperationException($"No background job with id '{jobId}'.");
		if (job.OwnerSessionId != ownerSessionId)
			throw new InvalidOperationException($"Job '{jobId}' is not owned by this session.");
		return job;
	}

	private static JobSnapshot SnapshotOf(Job job) => new(
		job.Id, job.Kind, job.Label, job.Status, job.StartedAt,
		job.OutputLimitBytes, job.Detail, job.FinishedAt);
}

namespace Suharness.Abstractions;

/// <summary>Prompt job lifecycle status.</summary>
public enum PromptJobStatus
{
	Queued,
	Waiting,
	Ready,
	Running,
	Completed,
	Failed,
	Blocked,
	Cancelled,
}

/// <summary>Granular access mode defining tool permissions for a prompt job.</summary>
public enum PromptAccessMode
{
	Read,
	Write,
	Execute,
}

/// <summary>Dependency execution constraint between prompt jobs.</summary>
public enum DependencyType
{
	AfterSuccess,
}

/// <summary>One dependency specification between prompt jobs.</summary>
public sealed record PromptJobDependency(
	string DependsOnPromptId,
	DependencyType Type = DependencyType.AfterSuccess);

/// <summary>A persisted prompt job concept in the prompt queue.</summary>
public sealed record PromptJob(
	string PromptId,
	string Prompt,
	string Workspace,
	string? Provider = null,
	string? Model = null,
	string? ReasoningEffort = null,
	PromptAccessMode AccessMode = PromptAccessMode.Write,
	bool PlanMode = false,
	int Priority = 0,
	DateTimeOffset? RunAt = null,
	PromptJobStatus Status = PromptJobStatus.Queued,
	IReadOnlyList<PromptJobDependency>? Dependencies = null,
	string? Result = null,
	string? Error = null,
	DateTimeOffset CreatedAt = default,
	DateTimeOffset? StartedAt = null,
	DateTimeOffset? CompletedAt = null,
	Guid? SessionId = null);

/// <summary>Draft model for enqueuing a prompt job.</summary>
public sealed record PromptJobDraft(
	string Prompt,
	string Workspace,
	string? Provider = null,
	string? Model = null,
	string? ReasoningEffort = null,
	PromptAccessMode AccessMode = PromptAccessMode.Write,
	bool PlanMode = false,
	int Priority = 0,
	DateTimeOffset? RunAt = null,
	IReadOnlyList<PromptJobDependency>? Dependencies = null,
	Guid? SessionId = null);

/// <summary>Persistence store contract for prompt jobs and dependencies.</summary>
public interface IPromptQueueStore
{
	Task<IReadOnlyList<PromptJob>> LoadJobsAsync(CancellationToken cancellationToken = default);
	Task SaveJobAsync(PromptJob job, CancellationToken cancellationToken = default);
	Task SaveJobsAsync(IEnumerable<PromptJob> jobs, CancellationToken cancellationToken = default);
	Task<int> NextIdSequenceAsync(CancellationToken cancellationToken = default);
}

/// <summary>Service contract for the prompt queue scheduler and dispatcher.</summary>
public interface IPromptQueueService
{
	event Action? Changed;

	Task<PromptJob> EnqueueAsync(PromptJobDraft draft, CancellationToken cancellationToken = default);
	Task<PromptJob?> GetAsync(string promptId, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<PromptJob>> ListAsync(string? workspace = null, CancellationToken cancellationToken = default);
	Task<bool> CancelAsync(string promptId, CancellationToken cancellationToken = default);
	Task ProcessQueueAsync(CancellationToken cancellationToken = default);
}

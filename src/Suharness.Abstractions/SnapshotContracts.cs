namespace Suharness.Abstractions;

/// <summary>What a restore does to one path, expressed as the change the restore reverses.</summary>
public enum SnapshotChangeKind
{
	/// <summary>The path exists now but not in the target snapshot, so a restore deletes it.</summary>
	Added,

	/// <summary>The path differs from the target snapshot, so a restore rewrites it.</summary>
	Modified,

	/// <summary>The path is missing now but present in the target snapshot, so a restore recreates it.</summary>
	Deleted,

	/// <summary>The path moved after the target snapshot, so a restore moves it back.</summary>
	Renamed,
}

/// <summary>One workspace-relative path and what a restore would do to it.</summary>
public sealed record SnapshotChange(string Path, SnapshotChangeKind Kind, string? OriginalPath = null);

/// <summary>A changed file and its unified diff from a snapshot to the current workspace.</summary>
public sealed record WorkspaceFileDiff(SnapshotChange Change, string Patch);

/// <param name="TreeHash">Content address of the captured worktree. Identical states capture to the same hash.</param>
/// <param name="Skipped">Paths deliberately left out of the capture because they exceed the size cap.</param>
public sealed record SnapshotCapture(string TreeHash, IReadOnlyList<string> Skipped, long ElapsedMs);

/// <summary>
/// Captures and restores the workspace so a turn's file changes can be undone. The store is a Git
/// object database of the harness's own, held outside the workspace: the user's index, branches, and
/// history are never touched, and a workspace that is not a Git repository at all is still covered.
/// </summary>
/// <remarks>
/// Capture is best-effort by design. A workspace large enough to overrun the capture deadline gets a
/// turn recorded as uncovered rather than a turn that refuses to run, because a snapshot is a safety
/// net and a safety net that blocks the work is worse than none.
/// </remarks>
public interface ISnapshotService
{
	/// <summary>Whether capture is switched on. A disabled service returns null from every capture.</summary>
	bool Enabled { get; }

	/// <summary>
	/// Whether this workspace has been indexed and its captures are therefore cheap. Until it is, a
	/// capture may decline rather than make the turn wait.
	/// </summary>
	bool IsReady(string workspaceRoot);

	/// <summary>
	/// Index a workspace so later captures are deltas rather than full copies. The first pass reads and
	/// stores every file, which on a large tree costs minutes where a delta costs milliseconds, so it
	/// belongs on workspace selection rather than inside the first turn the user runs.
	/// </summary>
	/// <remarks>Idempotent and shared: concurrent callers await the same pass.</remarks>
	Task PrimeAsync(string workspaceRoot);

	/// <summary>Capture the workspace, or null when capture is disabled, deadlined out, or failed.</summary>
	Task<SnapshotCapture?> CaptureAsync(string workspaceRoot, CancellationToken cancellationToken = default);

	/// <summary>The changes a restore to <paramref name="targetTree"/> would make, without making them.</summary>
	Task<IReadOnlyList<SnapshotChange>> PreviewAsync(
		string workspaceRoot,
		string targetTree,
		CancellationToken cancellationToken = default);

	/// <summary>Get one changed path's unified diff from the target snapshot to the workspace.</summary>
	Task<WorkspaceFileDiff?> GetFileDiffAsync(
		string workspaceRoot,
		string targetTree,
		string path,
		CancellationToken cancellationToken = default);

	/// <summary>Restore only one changed path to the target snapshot.</summary>
	Task<SnapshotChange?> RestoreFileAsync(
		string workspaceRoot,
		string targetTree,
		string path,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Restore the workspace to <paramref name="targetTree"/> and report what changed. Only paths that
	/// differ are touched, so an unchanged file keeps its modification time and does not provoke an
	/// editor reload.
	/// </summary>
	Task<IReadOnlyList<SnapshotChange>> RestoreAsync(
		string workspaceRoot,
		string targetTree,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// The payload of a <c>snapshot</c> session event. The transcript stays append-only and its schema
/// unchanged, so a capture is recorded as JSON content rather than as new columns.
/// </summary>
/// <param name="Kind"><c>turn-start</c> or <c>turn-end</c>.</param>
/// <param name="BoundaryEventId">The event this snapshot sits immediately before, for a turn-start.</param>
public sealed record SnapshotRecord(
	string Hash,
	string Kind,
	Guid? BoundaryEventId,
	long ElapsedMs,
	IReadOnlyList<string>? Skipped = null);

/// <summary>
/// The payload of a <c>revert</c> session event: the span of transcript it hides, the snapshot the
/// workspace was restored to, and the snapshot of the state that was replaced.
/// </summary>
/// <param name="RedoHash">
/// The workspace as it stood immediately before the restore. Captured first precisely so a revert is
/// itself reversible; without it an undo would be a one-way loss of whatever it rolled back.
/// </param>
public sealed record RevertRecord(
	Guid FromEventId,
	Guid ToEventId,
	string TargetHash,
	string RedoHash,
	IReadOnlyList<SnapshotChange> Changes);

/// <summary>The payload of a <c>redo</c> session event: which revert it cancels.</summary>
public sealed record RedoRecord(Guid RevertEventId, string Hash, IReadOnlyList<SnapshotChange> Changes);

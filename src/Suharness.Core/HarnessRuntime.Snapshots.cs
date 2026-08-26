using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

/// <summary>
/// Undo. A turn's file changes are captured into a snapshot store the harness owns, so a turn
/// can be inspected, a single file reverted, or the whole turn rolled back and redone. Without
/// this a tool call is permanent the moment it runs, whatever the transcript says.
/// </summary>
public sealed partial class HarnessRuntime
{
	/// <summary>Whether this harness can roll a workspace back at all.</summary>
	public bool SnapshotsEnabled => snapshots is { Enabled: true };

	/// <summary>Files changed since this session first allowed the agent to touch its workspace.</summary>
	public async Task<IReadOnlyList<SnapshotChange>> GetModifiedFilesAsync(
		Guid sessionId,
		CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) return [];
		var transcript = await store.GetAsync(sessionId, cancellationToken);
		var baseline = SessionBaseline(transcript);
		return transcript is null || baseline is null || string.IsNullOrWhiteSpace(transcript.Session.Workspace)
			? []
			: await snapshots.PreviewAsync(transcript.Session.Workspace, baseline, cancellationToken);
	}

	/// <summary>Unified diff for one file changed since the session baseline.</summary>
	public async Task<WorkspaceFileDiff?> GetModifiedFileDiffAsync(
		Guid sessionId,
		string path,
		CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) return null;
		var transcript = await store.GetAsync(sessionId, cancellationToken);
		var baseline = SessionBaseline(transcript);
		return transcript is null || baseline is null || string.IsNullOrWhiteSpace(transcript.Session.Workspace)
			? null
			: await snapshots.GetFileDiffAsync(transcript.Session.Workspace, baseline, path, cancellationToken);
	}

	/// <summary>Restore one file to its session-start state without touching any other path.</summary>
	public async Task<SnapshotChange?> RevertModifiedFileAsync(
		Guid sessionId,
		string path,
		CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) throw new InvalidOperationException("Workspace snapshots are disabled.");
		if (IsRunning(sessionId)) throw new InvalidOperationException("Stop the turn in flight before reverting a file.");
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		var baseline = SessionBaseline(transcript)
			?? throw new InvalidOperationException("This session has no workspace baseline to restore.");
		return await snapshots.RestoreFileAsync(transcript.Session.Workspace, baseline, path, cancellationToken);
	}

	private static string? SessionBaseline(SessionTranscript? transcript) => transcript?.Events
		.Select(item => SessionRevert.Read<SnapshotRecord>(item, SessionRevert.SnapshotType))
		.FirstOrDefault(record => record is { Kind: SessionRevert.TurnStart, Hash.Length: > 0 })
		?.Hash;

	/// <summary>
	/// Roll the workspace back to the state it was in immediately before <paramref name="boundaryEventId"/>,
	/// and hide that message and everything after it from replay.
	/// </summary>
	/// <remarks>
	/// The state being replaced is captured before anything is written, so the revert is itself
	/// reversible. Without that an undo would be a one-way loss of whatever it rolled back, which is
	/// the opposite of what the feature is for.
	/// </remarks>
	public async Task<RevertRecord> RevertAsync(Guid sessionId, Guid boundaryEventId, CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) throw new InvalidOperationException("Workspace snapshots are disabled.");
		if (IsRunning(sessionId)) throw new InvalidOperationException("Stop the turn in flight before undoing it.");

		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		if (string.IsNullOrWhiteSpace(transcript.Session.Workspace))
			throw new InvalidOperationException("This session has no workspace to roll back.");
		if (!SessionRevert.RevertPoints(transcript.Events).TryGetValue(boundaryEventId, out var target))
			throw new InvalidOperationException("That message has no snapshot, so the workspace cannot be rolled back to it.");
		if (transcript.Events.Count == 0) throw new InvalidOperationException("This session has nothing to undo.");
		if (SessionRevert.Hidden(transcript.Events).Contains(boundaryEventId))
			throw new InvalidOperationException("That message has already been undone.");

		var before = await snapshots.CaptureAsync(transcript.Session.Workspace, cancellationToken)
			?? throw new InvalidOperationException(
				"The workspace could not be captured, so it was left alone rather than rolled back into a state nothing could recover.");

		var changes = await snapshots.RestoreAsync(transcript.Session.Workspace, target, cancellationToken);

		// The span ends at the log's tail as it stands now, so the revert event itself — and every
		// message sent afterwards — falls outside it.
		var record = new RevertRecord(
			boundaryEventId,
			transcript.Events[^1].Id,
			target,
			before.TreeHash,
			changes);
		var revertEvent = NewEvent(sessionId, SessionRevert.RevertType, "system", SessionRevert.Serialize(record));
		await store.AppendAsync(revertEvent, cancellationToken);
		await store.AppendAsync(
			NewEvent(sessionId, "notice", "system", $"Rolled the workspace back and undid the conversation from here ({SessionRevert.Describe(changes)})."),
			cancellationToken);
		await store.SaveSessionAsync(transcript.Session with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
		return record;
	}

	/// <summary>Reinstate the most recent undo: restore the files it replaced and unhide its span.</summary>
	public async Task<RedoRecord> RedoAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) throw new InvalidOperationException("Workspace snapshots are disabled.");
		if (IsRunning(sessionId)) throw new InvalidOperationException("Stop the turn in flight before redoing.");

		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		var active = SessionRevert.Active(transcript.Events);
		if (active.Count == 0) throw new InvalidOperationException("There is nothing to redo.");

		var (revertEvent, reverted) = active[^1];
		var changes = await snapshots.RestoreAsync(transcript.Session.Workspace, reverted.RedoHash, cancellationToken);
		var record = new RedoRecord(revertEvent.Id, reverted.RedoHash, changes);
		await store.AppendAsync(
			NewEvent(sessionId, SessionRevert.RedoType, "system", SessionRevert.Serialize(record)),
			cancellationToken);
		await store.AppendAsync(
			NewEvent(sessionId, "notice", "system", $"Reinstated the undone changes ({SessionRevert.Describe(changes)})."),
			cancellationToken);
		await store.SaveSessionAsync(transcript.Session with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
		return record;
	}

	/// <summary>What a revert to <paramref name="boundaryEventId"/> would change, without changing it.</summary>
	public async Task<IReadOnlyList<SnapshotChange>> PreviewRevertAsync(
		Guid sessionId,
		Guid boundaryEventId,
		CancellationToken cancellationToken = default)
	{
		if (snapshots is not { Enabled: true }) return [];
		var transcript = await store.GetAsync(sessionId, cancellationToken);
		if (transcript is null || string.IsNullOrWhiteSpace(transcript.Session.Workspace)) return [];
		return SessionRevert.RevertPoints(transcript.Events).TryGetValue(boundaryEventId, out var target)
			? await snapshots.PreviewAsync(transcript.Session.Workspace, target, cancellationToken)
			: [];
	}
}

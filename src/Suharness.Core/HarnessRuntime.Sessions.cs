using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

/// <summary>
/// Creating, listing, renaming, archiving, and deleting sessions, and the per-session settings
/// that change how the next turn is built. All of it is transcript metadata: none of these
/// touch the workspace or start a turn.
/// </summary>
public sealed partial class HarnessRuntime
{
	public async Task<IReadOnlyList<HarnessSession>> ListSessionsAsync(string? workspace = null, CancellationToken cancellationToken = default)
	{
		var sessions = await store.ListAsync(cancellationToken);
		return sessions.Where(session => !session.Archived && (workspace is null || PathsEqual(session.Workspace, workspace))).ToArray();
	}

	public Task<SessionTranscript?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default) =>
		store.GetAsync(id, cancellationToken);

	public async Task<HarnessSession> CreateSessionAsync(string? workspace, CancellationToken cancellationToken = default)
	{
		await _sessionCreateGate.WaitAsync(cancellationToken);
		try
		{
			var canonicalWorkspace = string.IsNullOrWhiteSpace(workspace) ? string.Empty : Path.GetFullPath(workspace);
			if (canonicalWorkspace.Length > 0 && workspaceAgentState is not null)
			{
				try
				{
					await workspaceAgentState.EnsureAsync(canonicalWorkspace, cancellationToken);
					await workspaceAgentState.ProjectRepositoryAsync(canonicalWorkspace, cancellationToken);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
				{
					// Workspace projections are recoverable metadata, never a precondition for opening a session.
				}
			}
			// Indexing a workspace for undo costs one full read of it, so it starts as soon as the
			// workspace is opened rather than inside the first turn the user is waiting on.
			if (canonicalWorkspace.Length > 0 && snapshots is { Enabled: true }) _ = snapshots.PrimeAsync(canonicalWorkspace);
			foreach (var existing in await ListSessionsAsync(canonicalWorkspace, cancellationToken))
			{
				var transcript = await store.GetAsync(existing.Id, cancellationToken);
				if (transcript?.Events.Count == 0) return existing;
			}
			var now = DateTimeOffset.UtcNow;
			var session = new HarnessSession(Guid.NewGuid(), canonicalWorkspace, "New session", now, now);
			await store.SaveSessionAsync(session, cancellationToken);
			return session;
		}
		finally { _sessionCreateGate.Release(); }
	}

	public async Task RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		var normalized = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		if (normalized.Length == 0) throw new InvalidOperationException("Session title cannot be empty.");
		await store.SaveSessionAsync(transcript.Session with { Title = normalized[..Math.Min(normalized.Length, 100)], UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
	}

	public async Task ArchiveSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		await store.SaveSessionAsync(transcript.Session with { Archived = true, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
	}

	public async Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken);
		if (transcript is null)
			throw new InvalidOperationException($"Session {sessionId} does not exist.");
		await store.DeleteAsync(sessionId, cancellationToken);
		_sessionRunGates.TryRemove(sessionId, out _);
		scratch?.Delete(sessionId);
		if (workspaceAgentState is not null && !string.IsNullOrWhiteSpace(transcript.Session.Workspace))
			await workspaceAgentState.DeleteSessionAsync(transcript.Session.Workspace, sessionId, cancellationToken);
	}


	public async Task SetAccessModeAsync(Guid sessionId, HarnessAccessMode accessMode, CancellationToken cancellationToken = default)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		await store.SaveSessionAsync(transcript.Session with { AccessMode = accessMode, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
	}

	public async Task SetPresetAsync(Guid sessionId, HarnessAgentPreset preset, CancellationToken cancellationToken = default)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		await store.SaveSessionAsync(transcript.Session with { Preset = preset, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
	}
}

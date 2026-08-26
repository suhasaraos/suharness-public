using Microsoft.Agents.AI;

namespace Suharness.Abstractions;

public enum HarnessAccessMode
{
	ReadOnly,
	WorkspaceWrite,
	FullAccess,
}

/// <param name="Usage">
/// Token usage accumulated across every model invocation this session has made, or null for a
/// session that predates usage accounting or has not called a model yet. It is a rolled-up counter
/// rather than something derived from the transcript, so showing it in a session list costs one
/// record read and no history scan.
/// </param>
public sealed record HarnessSession(
	Guid Id,
	string Workspace,
	string Title,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	bool Archived = false,
	HarnessAccessMode AccessMode = HarnessAccessMode.WorkspaceWrite,
	bool PlanMode = false,
	HarnessAgentPreset Preset = HarnessAgentPreset.Standard,
	Guid? ParentSessionId = null,
	int DelegationDepth = 0,
	SessionUsage? Usage = null);

/// <summary>
/// Which agent composition a session runs. A preset chooses the tool catalog and the persona
/// together.
/// </summary>
public enum HarnessAgentPreset
{
	/// <summary>The full coding agent: files, search, shell, questions, todos, and plan mode.</summary>
	Standard,

	/// <summary>A fixed-prompt two-tool agent: shell plus literal string replacement.</summary>
	Minimal,
}

public sealed record HarnessWorkspace(Guid Id, string Path, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record WorkspaceSelection(Guid? WorkspaceId);

public sealed record DirectoryEntry(string Name, string Path, bool CanBrowse);

public sealed record DirectoryListing(string? CurrentPath, string? ParentPath, IReadOnlyList<DirectoryEntry> Entries);

/// <param name="CompactedThroughId">
/// Set only on a <c>compaction</c> event: the id of the last event its checkpoint stands in for.
/// The log stays append-only, so a checkpoint written at the end still has to say which prefix it
/// shadows — everything up to and including this event replays as the checkpoint instead of itself.
/// </param>
public sealed record SessionEvent(
	Guid Id,
	Guid SessionId,
	string Type,
	string Role,
	string Content,
	DateTimeOffset CreatedAt,
	string? ModelMessageId = null,
	string? ToolCallId = null,
	string? ToolName = null,
	string? ToolArguments = null,
	Guid? CompactedThroughId = null,
	IReadOnlyList<ImageAttachmentRef>? Attachments = null);

/// <summary>
/// Condenses an earlier span of a session into a checkpoint when the conversation approaches the
/// model's context window. Without it a
/// session replays its whole transcript on every turn and eventually crowds the model out of its
/// own context, which shows up as a model that stops calling tools rather than as an error.
/// </summary>
public interface ICompactionService
{
	/// <summary>
	/// Compact the session when it is over its pressure threshold. Returns the checkpoint event to
	/// append, or null when nothing needed compacting or the attempt did not produce a usable one.
	/// </summary>
	Task<SessionEvent?> MaybeCompactAsync(
		Guid sessionId,
		IReadOnlyList<SessionEvent> events,
		AIAgent agent,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Compact after the provider has already rejected a request for exceeding its context window.
	/// Unlike the pressure path this ignores the threshold and the retained tail: the estimate that
	/// was supposed to keep the session under the limit has just been proven wrong, so the only
	/// useful move is the largest reduction the transcript allows. Returns the checkpoint to append,
	/// or null when the transcript cannot be reduced any further.
	/// </summary>
	/// <remarks>
	/// This is the context-overflow compaction trigger. A harness with only the
	/// pressure path fails the turn outright on the first bad estimate, and every later turn in that
	/// session fails the same way, because nothing ever shrinks the transcript that caused it.
	/// </remarks>
	Task<SessionEvent?> CompactForOverflowAsync(
		Guid sessionId,
		IReadOnlyList<SessionEvent> events,
		AIAgent agent,
		CancellationToken cancellationToken = default);
}

public sealed record SessionTranscript(HarnessSession Session, IReadOnlyList<SessionEvent> Events);

/// <param name="Intent">
/// What this turn was asked to do, in the requester's own words. It is composition input, not model
/// input: a server publishing a very large tool catalog is narrowed to the capabilities the wording
/// implies, because sending all of them costs more context than the task itself. Null or empty
/// widens rather than narrows.
/// </param>
public sealed record AgentContext(
	Guid SessionId,
	string Workspace,
	HarnessAccessMode AccessMode = HarnessAccessMode.WorkspaceWrite,
	HarnessAgentPreset Preset = HarnessAgentPreset.Standard,
	bool Interactive = true,
	string? Intent = null,
	AgentToolProfile ToolProfile = AgentToolProfile.Standard,
	ModelSelection? ModelOverride = null,
	int? MaxOutputTokensOverride = null,
	bool JsonOutput = false,
	Type? JsonOutputType = null);

/// <summary>Turn-scoped tool catalog for specialized workflow roles.</summary>
public enum AgentToolProfile
{
	Standard,
	WorkflowReadOnly,
	WorkflowCode,
	WorkflowRepair,
	WorkflowVerify,
	None,
}

/// <summary>Harness-owned temporary workspace for one session's probes and generated runners.</summary>
public interface ISessionScratchStore
{
	string GetOrCreate(Guid sessionId);

	void Delete(Guid sessionId);
}



public interface ISessionStore
{
	Task<IReadOnlyList<HarnessSession>> ListAsync(CancellationToken cancellationToken = default);
	Task<SessionTranscript?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

	/// <summary>
	/// The session record on its own. The default reads the whole transcript, which is correct but
	/// wasteful; a store that can read the record without its events should override it. Usage
	/// accounting flushes through this on every turn, and reading a long transcript to update a
	/// counter would put the cost of the feature on exactly the longest sessions.
	/// </summary>
	async Task<HarnessSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
		(await GetAsync(sessionId, cancellationToken))?.Session;

	Task SaveSessionAsync(HarnessSession session, CancellationToken cancellationToken = default);
	Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default);
	Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IWorkspaceService
{
	Task<IReadOnlyList<HarnessWorkspace>> ListAsync(CancellationToken cancellationToken = default);
	Task<WorkspaceSelection?> GetSelectionAsync(CancellationToken cancellationToken = default);
	Task SelectAsync(Guid? workspaceId, CancellationToken cancellationToken = default);
	Task<HarnessWorkspace> AddAsync(string path, CancellationToken cancellationToken = default);
	Task RemoveAsync(Guid workspaceId, CancellationToken cancellationToken = default);
	Task<DirectoryListing> BrowseAsync(string? path, CancellationToken cancellationToken = default);
}

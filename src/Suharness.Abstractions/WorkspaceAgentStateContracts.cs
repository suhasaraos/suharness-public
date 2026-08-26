namespace Suharness.Abstractions;

public sealed record WorkspaceAgentStatus(
	string Root,
	bool Initialized,
	string? InstructionsPath,
	int SkillCount,
	DateTimeOffset? MetadataGeneratedAt,
	int KnowledgeNoteCount,
	int SessionCount,
	IReadOnlyList<string> Warnings);

public sealed record WorkspaceAgentImportResult(
	bool ImportedInstructions,
	int ImportedSkills,
	int ImportedKnowledgeNotes,
	int ImportedSessions,
	IReadOnlyList<string> Warnings);

public interface IWorkspaceAgentStateStore
{
	Task EnsureAsync(string workspaceRoot, CancellationToken cancellationToken = default);

	Task ProjectRepositoryAsync(string workspaceRoot, CancellationToken cancellationToken = default);

	Task ProjectSessionAsync(SessionTranscript transcript, CancellationToken cancellationToken = default);

	Task ProjectWorkflowProgressAsync(
		SessionTranscript transcript,
		string workflowJson,
		CancellationToken cancellationToken = default);

	Task DeleteSessionAsync(string workspaceRoot, Guid sessionId, CancellationToken cancellationToken = default);

	Task<string?> MirrorAttachmentAsync(
		string workspaceRoot,
		Guid sessionId,
		ImageAttachmentData attachment,
		CancellationToken cancellationToken = default);

	Task<WorkspaceAgentStatus> GetStatusAsync(string workspaceRoot, CancellationToken cancellationToken = default);

	Task<WorkspaceAgentImportResult> ImportSuAgentAsync(string workspaceRoot, CancellationToken cancellationToken = default);
}

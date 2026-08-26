namespace Suharness.Abstractions;

/// <summary>
/// What a search tool would have decided about one file while walking. The index walks once with
/// the widest policy — no gitignore, hidden files included — and records these instead, so a single
/// table can serve <c>glob</c> (which wants everything) and <c>grep</c> (which wants neither)
/// without a second traversal.
/// </summary>
[Flags]
public enum IndexedFileFlags
{
	None = 0,

	/// <summary>The file, or one of its ancestor directories, is dot-prefixed.</summary>
	Hidden = 1,

	/// <summary>A <c>.gitignore</c> rule excludes the file, or excludes a directory above it.</summary>
	Ignored = 2,
}

/// <summary>One file in the index: enough to answer a path query without touching the filesystem.</summary>
/// <param name="RelativePath">Workspace-relative, always '/'-separated.</param>
/// <param name="Size">Length in bytes at index time.</param>
/// <param name="ModifiedTicks">UTC <see cref="DateTime.Ticks"/> of the last write at index time.</param>
public sealed record IndexedFile(string RelativePath, long Size, long ModifiedTicks, IndexedFileFlags Flags)
{
	public bool Hidden => (Flags & IndexedFileFlags.Hidden) != 0;

	public bool Ignored => (Flags & IndexedFileFlags.Ignored) != 0;

	/// <summary>True when <c>grep</c>'s walk would have reached this file.</summary>
	public bool Searchable => Flags == IndexedFileFlags.None;
}

/// <summary>Why an index holds fewer files than the tree does.</summary>
public enum IndexTruncation
{
	None,
	Budget,
	Deadline,
}

/// <summary>
/// The file table for one workspace at one moment. Handed to the search tools in place of a walk.
/// </summary>
public sealed record WorkspaceIndexSnapshot(
	string Root,
	IReadOnlyList<IndexedFile> Files,
	IndexTruncation Truncation,
	DateTimeOffset BuiltAtUtc)
{
	public bool Partial => Truncation != IndexTruncation.None;

	public static WorkspaceIndexSnapshot Empty(string root) =>
		new(root, [], IndexTruncation.None, DateTimeOffset.UtcNow);
}

/// <summary>
/// One walk of a workspace, retained. The index is the harness's memory of a tree it has already
/// read: it survives the turn that built it, the session, and the process, so the second question
/// about a workspace does not pay for the traversal the first one already made.
/// </summary>
public interface IWorkspaceIndex
{
	/// <summary>
	/// The current file table, building or revalidating it as needed. Callers must treat the result
	/// as a snapshot: it is not updated in place.
	/// </summary>
	WorkspaceIndexSnapshot Snapshot(string workspaceRoot, CancellationToken cancellationToken = default);

	/// <summary>
	/// Fold one known file change into the table, so the index is never stale for a mutation the
	/// harness made itself. A file that no longer exists is removed.
	/// </summary>
	void Record(string workspaceRoot, string fullPath);

	/// <summary>
	/// Drop the freshness window, so the next <see cref="Snapshot"/> revalidates against the
	/// filesystem. Called after a tool that can change the tree in ways the harness cannot see,
	/// such as an arbitrary shell command.
	/// </summary>
	void Invalidate(string workspaceRoot);
}

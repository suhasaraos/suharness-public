using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;

namespace Suharness.Tools;

/// <summary>
/// One walk of a workspace, retained in memory and on disk.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed every <c>glob</c> and <c>grep</c> constructed its own <see cref="FileWalker"/>
/// and traversed the tree from the root, so a turn that issued a dozen searches paid for a dozen
/// full traversals of a tree that had not changed between them, and the next turn — and the next
/// session on the same workspace — started again from nothing.
/// </para>
/// <para>
/// Freshness is enforced in two stages, because the two failure modes have very different costs. A
/// short in-process window absorbs the burst case: repeated searches inside one turn are served from
/// the table with no filesystem access at all. Past that window a directory pass compares each
/// directory's write stamp against the recorded one and re-reads the file list only for directories
/// whose stamp moved, which is the signal a filesystem gives when a child is added or removed. File
/// content edits do not move a directory stamp, and do not need to: size and write time are recorded
/// per file, and the tools that change files report those changes through <see cref="Record"/>.
/// </para>
/// </remarks>
public sealed class WorkspaceIndex : IWorkspaceIndex
{
	/// <summary>Format version of the persisted table. A mismatch is discarded rather than migrated.</summary>
	public const int FormatVersion = 1;

	/// <summary>
	/// How long a validated table is served without consulting the filesystem. It exists to collapse
	/// a burst of searches within one turn into a single traversal; it is deliberately short, because
	/// the cost of being briefly stale is a missing new file and the cost of being slow is the
	/// behaviour this class was written to remove.
	/// </summary>
	public static readonly TimeSpan DefaultFreshness = TimeSpan.FromSeconds(2);

	/// <summary>
	/// Wall-clock budget for a full build. Longer than a search's own budget because the cost is
	/// amortised over every later search rather than charged to the call that triggered it.
	/// </summary>
	public static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromSeconds(60);

	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private readonly string? _storeRoot;
	private readonly TimeSpan _freshness;
	private readonly TimeSpan _buildTimeout;
	private readonly ConcurrentDictionary<string, Entry> _entries = new(PathComparer);

	/// <param name="storeRoot">
	/// Directory the tables are persisted under, or null to keep the index in memory only. It is a
	/// harness-owned path: the index is never written into the user's own workspace, for the same
	/// reason the snapshot store is not.
	/// </param>
	public WorkspaceIndex(string? storeRoot = null, TimeSpan? freshness = null, TimeSpan? buildTimeout = null)
	{
		_storeRoot = string.IsNullOrWhiteSpace(storeRoot) ? null : Path.GetFullPath(storeRoot);
		_freshness = freshness ?? DefaultFreshness;
		_buildTimeout = buildTimeout ?? DefaultBuildTimeout;
	}

	/// <summary>One workspace's table. Every field is guarded by the entry's own lock.</summary>
	private sealed class Entry
	{
		public readonly Lock Gate = new();
		public readonly SemaphoreSlim PersistGate = new(1, 1);
		public string Root = string.Empty;
		public Dictionary<string, DirectoryNode>? Directories;
		public IndexTruncation Truncation;
		public DateTimeOffset BuiltAtUtc;
		public long ValidatedTick;
		public bool NeedsRevalidation = true;
		public WorkspaceIndexSnapshot? Materialized;
	}

	/// <summary>
	/// One directory's contents, keyed by file name. Grouping by directory is what makes revalidation
	/// incremental: a changed stamp replaces exactly one node's file list.
	/// </summary>
	private sealed class DirectoryNode
	{
		public bool Hidden;
		public bool Ignored;
		public Dictionary<string, IndexedFile> Files = new(StringComparer.Ordinal);
	}

	public WorkspaceIndexSnapshot Snapshot(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(workspaceRoot)) return WorkspaceIndexSnapshot.Empty(string.Empty);
		string root;
		try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return WorkspaceIndexSnapshot.Empty(workspaceRoot);
		}
		if (!Directory.Exists(root)) return WorkspaceIndexSnapshot.Empty(root);

		var entry = _entries.GetOrAdd(root, key => new Entry { Root = key });
		lock (entry.Gate)
		{
			if (entry.Directories is null && _storeRoot is not null) LoadFromDisk(entry);
			if (entry.Directories is null)
			{
				Build(entry, cancellationToken);
			}
			else if (entry.NeedsRevalidation || Environment.TickCount64 - entry.ValidatedTick >= _freshness.TotalMilliseconds)
			{
				Revalidate(entry, cancellationToken);
			}
			return Materialize(entry);
		}
	}

	public void Record(string workspaceRoot, string fullPath)
	{
		if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(fullPath)) return;
		string root;
		string full;
		try
		{
			root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
			full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return;
		}
		if (!_entries.TryGetValue(root, out var entry)) return;

		var relative = Relative(root, full);
		if (relative is null) return;
		var separator = relative.LastIndexOf('/');
		var directory = separator < 0 ? string.Empty : relative[..separator];
		var name = separator < 0 ? relative : relative[(separator + 1)..];

		lock (entry.Gate)
		{
			// A directory the table has never seen means the tree grew in a way this update cannot
			// describe on its own, so the cheap path is abandoned for a revalidation.
			if (entry.Directories is null || !entry.Directories.TryGetValue(directory, out var node))
			{
				entry.NeedsRevalidation = true;
				return;
			}

			var info = new FileInfo(full);
			if (!info.Exists)
			{
				if (node.Files.Remove(name)) entry.Materialized = null;
				return;
			}

			long size;
			long ticks;
			try { size = info.Length; ticks = info.LastWriteTimeUtc.Ticks; }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }

			var flags = Flags(node.Hidden || name.StartsWith('.'), node.Ignored);
			node.Files[name] = new IndexedFile(relative, size, ticks, flags);
			entry.Materialized = null;
		}
	}

	public void Invalidate(string workspaceRoot)
	{
		if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
		string root;
		try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)); }
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return;
		}
		if (!_entries.TryGetValue(root, out var entry)) return;
		lock (entry.Gate) entry.NeedsRevalidation = true;
	}

	// ---------------------------------------------------------------- building

	/// <summary>
	/// Read the tree into a fresh directory map. This is the only path that touches the filesystem in
	/// bulk, and both the initial build and every later confirmation go through it.
	/// </summary>
	/// <remarks>
	/// An earlier version confirmed the table by comparing directory write stamps and re-reading only
	/// the directories whose stamp had moved. That is unsound on a real filesystem: the system clock
	/// advances in roughly fifteen-millisecond steps, so a file created and another deleted within one
	/// step leave the directory reporting the same write time it had before, and the change is missed
	/// entirely. Discovering the subdirectories to descend into requires enumerating each directory
	/// anyway, so skipping the file half of that enumeration saved very little and cost correctness.
	/// </remarks>
	private (Dictionary<string, DirectoryNode> Directories, WalkTruncation Truncation) Scan(
		string root,
		CancellationToken cancellationToken)
	{
		var walker = new FileWalker(respectGitIgnore: false, includeHidden: true, timeout: _buildTimeout);
		var directories = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal)
		{
			[string.Empty] = new(),
		};

		foreach (var item in walker.WalkIndexed(root, cancellationToken))
		{
			if (item.IsDirectory)
			{
				directories[item.RelativePath] = new DirectoryNode { Hidden = item.Hidden, Ignored = item.Ignored };
				continue;
			}

			var separator = item.RelativePath.LastIndexOf('/');
			var parent = separator < 0 ? string.Empty : item.RelativePath[..separator];
			var name = separator < 0 ? item.RelativePath : item.RelativePath[(separator + 1)..];
			if (!directories.TryGetValue(parent, out var node))
			{
				// The walk yields a directory before its children, so this only happens if the parent
				// was skipped as a reparse point; the file is unreachable by the same rule.
				continue;
			}
			node.Files[name] = new IndexedFile(
				item.RelativePath,
				item.Size,
				item.ModifiedTicks,
				Flags(item.Hidden, item.Ignored));
		}

		return (directories, walker.Truncation);
	}

	private void Build(Entry entry, CancellationToken cancellationToken)
	{
		var (directories, truncation) = Scan(entry.Root, cancellationToken);
		entry.Directories = directories;
		entry.Truncation = Map(truncation);
		entry.BuiltAtUtc = DateTimeOffset.UtcNow;
		entry.ValidatedTick = Environment.TickCount64;
		entry.NeedsRevalidation = false;
		entry.Materialized = null;
		SchedulePersist(entry);
	}

	/// <summary>
	/// Confirm the table against the filesystem once the freshness window has lapsed. A tree that has
	/// not changed keeps its existing table object, which matters beyond saving an allocation: the
	/// symbol index and the search tools both key their own caches on the identity of the snapshot
	/// this produces, so replacing an equal table would silently discard their work.
	/// </summary>
	private void Revalidate(Entry entry, CancellationToken cancellationToken)
	{
		var (directories, truncation) = Scan(entry.Root, cancellationToken);
		entry.Truncation = Map(truncation);
		entry.ValidatedTick = Environment.TickCount64;
		entry.NeedsRevalidation = false;

		if (Equivalent(entry.Directories, directories)) return;

		entry.Directories = directories;
		entry.BuiltAtUtc = DateTimeOffset.UtcNow;
		entry.Materialized = null;
		SchedulePersist(entry);
	}

	/// <summary>True when two directory maps describe the same tree, file versions included.</summary>
	private static bool Equivalent(Dictionary<string, DirectoryNode>? left, Dictionary<string, DirectoryNode> right)
	{
		if (left is null || left.Count != right.Count) return false;
		foreach (var (relative, node) in left)
		{
			if (!right.TryGetValue(relative, out var other)
				|| node.Hidden != other.Hidden
				|| node.Ignored != other.Ignored
				|| node.Files.Count != other.Files.Count)
			{
				return false;
			}
			foreach (var (name, file) in node.Files)
			{
				if (!other.Files.TryGetValue(name, out var candidate)
					|| candidate.Size != file.Size
					|| candidate.ModifiedTicks != file.ModifiedTicks
					|| candidate.Flags != file.Flags)
				{
					return false;
				}
			}
		}
		return true;
	}

	/// <summary>
	/// Flatten the directory map into the list callers see, sorted once. Sorting here rather than per
	/// search matters at scale: a hundred-thousand-file workspace would otherwise be re-sorted by
	/// every <c>grep</c>, which needs a stable order only so that repeated calls group their output
	/// identically.
	/// </summary>
	private static WorkspaceIndexSnapshot Materialize(Entry entry)
	{
		if (entry.Materialized is { } cached) return cached;
		var files = new List<IndexedFile>();
		foreach (var node in entry.Directories!.Values) files.AddRange(node.Files.Values);
		files.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
		var snapshot = new WorkspaceIndexSnapshot(entry.Root, files, entry.Truncation, entry.BuiltAtUtc);
		entry.Materialized = snapshot;
		return snapshot;
	}

	// ---------------------------------------------------------------- helpers

	private static IndexedFileFlags Flags(bool hidden, bool ignored) =>
		(hidden ? IndexedFileFlags.Hidden : IndexedFileFlags.None)
		| (ignored ? IndexedFileFlags.Ignored : IndexedFileFlags.None);

	private static IndexTruncation Map(WalkTruncation truncation) => truncation switch
	{
		WalkTruncation.Budget => IndexTruncation.Budget,
		WalkTruncation.Deadline => IndexTruncation.Deadline,
		_ => IndexTruncation.None,
	};

	private static string? Relative(string root, string full)
	{
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (full.Length <= root.Length
			|| !full.AsSpan(0, root.Length).Equals(root, comparison)
			|| full[root.Length] != Path.DirectorySeparatorChar)
		{
			return null;
		}
		return full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
	}

	// ---------------------------------------------------------------- persistence

	private string StoreDirectory(string root) => IndexStore.DirectoryFor(_storeRoot!, root);

	private void LoadFromDisk(Entry entry)
	{
		try
		{
			var directory = StoreDirectory(entry.Root);
			var metaPath = Path.Combine(directory, "meta.tsv");
			var tablePath = Path.Combine(directory, "files.tsv");
			if (!File.Exists(metaPath) || !File.Exists(tablePath)) return;

			string? storedRoot = null;
			var version = 0;
			var builtAt = DateTimeOffset.UtcNow;
			foreach (var line in File.ReadLines(metaPath))
			{
				var parts = line.Split('\t');
				if (parts.Length != 2) continue;
				switch (parts[0])
				{
					case "version": _ = int.TryParse(parts[1], CultureInfo.InvariantCulture, out version); break;
					case "root": storedRoot = parts[1]; break;
					case "builtAt": _ = DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, out builtAt); break;
				}
			}
			// A hash collision or a moved workspace must never be served as this workspace's tree.
			if (version != FormatVersion || storedRoot is null || !PathComparer.Equals(storedRoot, entry.Root)) return;

			var directories = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal);
			foreach (var line in File.ReadLines(tablePath))
			{
				var parts = line.Split('\t');
				if (parts.Length < 5) continue;
				var kind = parts[0];
				if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var size)) continue;
				if (!long.TryParse(parts[2], CultureInfo.InvariantCulture, out var ticks)) continue;
				if (!int.TryParse(parts[3], CultureInfo.InvariantCulture, out var rawFlags)) continue;
				var relative = string.Join('\t', parts[4..]);
				var flags = (IndexedFileFlags)rawFlags;

				if (kind == "d")
				{
					directories[relative] = new DirectoryNode
					{
						Hidden = (flags & IndexedFileFlags.Hidden) != 0,
						Ignored = (flags & IndexedFileFlags.Ignored) != 0,
					};
					continue;
				}
				if (kind != "f") continue;
				var separator = relative.LastIndexOf('/');
				var parent = separator < 0 ? string.Empty : relative[..separator];
				var name = separator < 0 ? relative : relative[(separator + 1)..];
				if (!directories.TryGetValue(parent, out var node)) continue;
				node.Files[name] = new IndexedFile(relative, size, ticks, flags);
			}
			if (!directories.ContainsKey(string.Empty)) return;

			entry.Directories = directories;
			entry.BuiltAtUtc = builtAt;
			entry.Truncation = IndexTruncation.None;
			// A table read from disk describes the tree as some earlier process last saw it, so it is
			// served immediately and confirmed before the next answer relies on it.
			entry.NeedsRevalidation = true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
		{
			entry.Directories = null;
		}
	}

	/// <summary>
	/// Write the table out without making a tool call wait for it. A search that has already produced
	/// its answer should not pay for the bookkeeping that makes the next one fast.
	/// </summary>
	private void SchedulePersist(Entry entry)
	{
		if (_storeRoot is null || entry.Truncation != IndexTruncation.None) return;
		var directories = entry.Directories;
		if (directories is null) return;

		// The rows are built on the caller's thread while the entry lock is held, because the table
		// itself is not safe to read once the lock is released.
		var rows = new List<string>(directories.Count * 8);
		foreach (var (relative, node) in directories)
		{
			if (IndexStore.Unwritable(relative)) continue;
			rows.Add(string.Create(CultureInfo.InvariantCulture,
				$"d\t0\t0\t{(int)Flags(node.Hidden, node.Ignored)}\t{relative}"));
			foreach (var file in node.Files.Values)
			{
				if (IndexStore.Unwritable(file.RelativePath)) continue;
				rows.Add(string.Create(CultureInfo.InvariantCulture,
					$"f\t{file.Size}\t{file.ModifiedTicks}\t{(int)file.Flags}\t{file.RelativePath}"));
			}
		}
		var root = entry.Root;
		var builtAt = entry.BuiltAtUtc;

		_ = Task.Run(async () =>
		{
			await entry.PersistGate.WaitAsync();
			try
			{
				var directory = StoreDirectory(root);
				Directory.CreateDirectory(directory);
				IndexStore.WriteAtomic(Path.Combine(directory, "files.tsv"), rows);
				IndexStore.WriteAtomic(Path.Combine(directory, "meta.tsv"),
				[
					$"version\t{FormatVersion}",
					$"root\t{root}",
					$"builtAt\t{builtAt:O}",
				]);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				// Best effort. A workspace whose index cannot be persisted still gets the in-memory
				// table; it just rebuilds on the next process.
			}
			finally { entry.PersistGate.Release(); }
		});
	}

}

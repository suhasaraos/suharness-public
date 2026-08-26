namespace Suharness.Tools;

/// <summary>One <c>.gitignore</c> rule, compiled against paths relative to the file that declared it.</summary>
internal sealed record IgnorePattern(GlobPattern Pattern, bool DirectoryOnly, bool Negated);

/// <summary>The rules from one directory's <c>.gitignore</c>, plus that directory's path from the root.</summary>
internal sealed record IgnoreLayer(string BaseRelative, IReadOnlyList<IgnorePattern> Patterns);

/// <summary>One entry from an annotating walk, carrying what both search policies would decide.</summary>
public readonly record struct IndexedWalkEntry(
	string RelativePath,
	string FullPath,
	bool IsDirectory,
	bool Hidden,
	bool Ignored,
	long Size,
	long ModifiedTicks);

/// <summary>Why a walk returned fewer paths than the tree holds.</summary>
public enum WalkTruncation
{
	/// <summary>The tree was exhausted.</summary>
	None,

	/// <summary>The entry budget stopped traversal.</summary>
	Budget,

	/// <summary>The wall-clock deadline stopped traversal.</summary>
	Deadline,
}

/// <summary>
/// Depth-first file enumeration with the pruning both search tools depend on. Walking a 15 GB tree
/// is only viable because directories are pruned before descent rather than filtered afterwards,
/// because symlinks are never followed, and because entry and wall-clock budgets bound the worst case.
/// </summary>
public sealed class FileWalker
{
	/// <summary>VCS metadata directories the file walker never descends into.</summary>
	public static readonly string[] VcsExcludes = [".git", ".svn", ".hg", ".bzr", ".jj", ".sl"];

	/// <summary>How often the wall clock is consulted, in entries. Reading it per entry costs more than the walk.</summary>
	private const int DeadlineCheckInterval = 512;

	private readonly bool _respectGitIgnore;
	private readonly bool _includeHidden;
	private readonly int _budget;
	private readonly TimeSpan _timeout;

	/// <param name="respectGitIgnore">Honour <c>.gitignore</c>, as ripgrep does by default (grep).</param>
	/// <param name="includeHidden">Descend into dot-prefixed entries, as <c>--hidden</c> does (glob).</param>
	/// <param name="budget">Maximum entries examined before traversal stops; bounds huge trees.</param>
	/// <param name="timeout">Wall-clock budget for one walk; null uses <see cref="SearchTools.SearchTimeout"/>.</param>
	public FileWalker(bool respectGitIgnore, bool includeHidden, int budget = 2_000_000, TimeSpan? timeout = null)
	{
		_respectGitIgnore = respectGitIgnore;
		_includeHidden = includeHidden;
		_budget = budget;
		_timeout = timeout ?? SearchTools.SearchTimeout;
	}

	/// <summary>The wall-clock budget this walker enforces.</summary>
	public TimeSpan Timeout => _timeout;

	/// <summary>Why traversal stopped, once enumeration has finished.</summary>
	public WalkTruncation Truncation { get; private set; }

	/// <summary>True when the entry budget stopped traversal before the tree was exhausted.</summary>
	public bool BudgetExhausted => Truncation == WalkTruncation.Budget;

	/// <summary>True when the wall-clock budget stopped traversal before the tree was exhausted.</summary>
	public bool DeadlineExpired => Truncation == WalkTruncation.Deadline;

	/// <summary>Enumerate files beneath <paramref name="root"/>, yielding root-relative '/' paths.</summary>
	public IEnumerable<(string RelativePath, string FullPath)> Walk(string root, CancellationToken cancellationToken)
	{
		Truncation = WalkTruncation.None;
		var deadline = Environment.TickCount64 + (long)_timeout.TotalMilliseconds;
		var examined = 0;
		var stack = new Stack<(string Directory, string Relative, IReadOnlyList<IgnoreLayer> ParentLayers)>();
		stack.Push((root, string.Empty, []));

		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (directory, relative, parentLayers) = stack.Pop();
			var layers = parentLayers;
			if (_respectGitIgnore && TryLoadLayer(directory, relative) is { } layer) layers = [.. parentLayers, layer];

			// The entries carry the attributes the directory scan already returned, so the directory
			// and symlink tests below cost no extra syscall. The per-entry Directory.Exists this
			// replaces stat'ed every file a second time, which is what made a large tree take minutes.
			FileSystemInfo[] entries;
			try { entries = Ordered(directory); }
			catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
			{
				continue;
			}

			foreach (var entry in entries)
			{
				if (++examined > _budget) { Truncation = WalkTruncation.Budget; yield break; }
				if (examined % DeadlineCheckInterval == 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (Environment.TickCount64 >= deadline) { Truncation = WalkTruncation.Deadline; yield break; }
				}

				var name = entry.Name;
				var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
				var attributes = SafeAttributes(entry);
				var isDirectory = attributes.HasFlag(FileAttributes.Directory);

				// Symlinks and junctions are never followed, matching ripgrep's default. A pnpm or npm
				// workspace links packages back into its own store, so following them turns a 70k-file
				// tree into an unbounded cyclic graph that no entry budget rescues in useful time.
				if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
				if (isDirectory && VcsExcludes.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
				if (!_includeHidden && name.StartsWith('.')) continue;
				if (_respectGitIgnore && IsIgnored(layers, childRelative, isDirectory)) continue;

				if (isDirectory) stack.Push((entry.FullName, childRelative, layers));
				else yield return (childRelative, entry.FullName);
			}
		}
	}

	/// <summary>Enumerate one directory in a stable order, so repeated walks agree.</summary>
	private static FileSystemInfo[] Ordered(string directory)
	{
		var entries = new DirectoryInfo(directory).GetFileSystemInfos();
		Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.FullName, right.FullName));
		return entries;
	}

	/// <summary>
	/// Attributes for one entry, already cached from the directory scan. An entry deleted or made
	/// unreadable since the scan is reported as a plain file, so it is skipped rather than descended.
	/// </summary>
	private static FileAttributes SafeAttributes(FileSystemInfo entry)
	{
		try { return entry.Attributes; }
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return FileAttributes.Normal;
		}
	}

	/// <summary>
	/// Compile the <c>.gitignore</c> rules declared in one directory, if it has any. Exposed so the
	/// workspace index can reuse the walker's gitignore semantics during revalidation instead of
	/// keeping a second, drifting copy of them.
	/// </summary>
	internal static IgnoreLayer? LoadIgnoreLayerFor(string directory, string relative) =>
		TryLoadLayer(directory, relative);

	/// <summary>Apply a stack of compiled rule layers to one path, last matching rule winning.</summary>
	internal static bool IsIgnoredBy(IReadOnlyList<IgnoreLayer> layers, string relativePath, bool isDirectory) =>
		IsIgnored(layers, relativePath, isDirectory);

	private static IgnoreLayer? TryLoadLayer(string directory, string relative)
	{
		var path = Path.Combine(directory, ".gitignore");
		if (!File.Exists(path)) return null;

		string[] lines;
		try { lines = File.ReadAllLines(path); }
		catch (IOException) { return null; }
		catch (UnauthorizedAccessException) { return null; }

		var patterns = new List<IgnorePattern>();
		foreach (var raw in lines)
		{
			var line = raw.Trim();
			if (line.Length == 0 || line.StartsWith('#')) continue;

			var negated = line.StartsWith('!');
			if (negated) line = line[1..];

			var directoryOnly = line.EndsWith('/');
			if (directoryOnly) line = line[..^1];
			if (line.Length == 0) continue;

			// A leading slash anchors to this .gitignore's directory; any other interior slash
			// anchors too. Otherwise the pattern matches a basename at any depth below.
			if (line.StartsWith('/')) line = line[1..];
			else if (!line.Contains('/')) line = "**/" + line;

			// Git matches a directory pattern against everything beneath it.
			GlobPattern compiled;
			try { compiled = GlobPattern.Compile(line + "{,/**}", caseInsensitive: OperatingSystem.IsWindows()); }
			catch (ToolInputException) { continue; }
			patterns.Add(new IgnorePattern(compiled, directoryOnly, negated));
		}

		return patterns.Count > 0 ? new IgnoreLayer(relative, patterns) : null;
	}

	private static bool IsIgnored(IReadOnlyList<IgnoreLayer> layers, string relativePath, bool isDirectory)
	{
		var ignored = false;
		foreach (var layer in layers)
		{
			var scoped = layer.BaseRelative.Length == 0
				? relativePath
				: relativePath.StartsWith(layer.BaseRelative + "/", StringComparison.OrdinalIgnoreCase)
					? relativePath[(layer.BaseRelative.Length + 1)..]
					: null;
			if (scoped is null) continue;

			// Last matching rule wins, so a later negation can re-include a path.
			foreach (var pattern in layer.Patterns)
			{
				if (pattern.DirectoryOnly && !isDirectory) continue;
				if (pattern.Pattern.IsMatch(scoped)) ignored = !pattern.Negated;
			}
		}
		return ignored;
	}

	/// <summary>
	/// Enumerate every file and directory beneath <paramref name="root"/>, annotating each entry with
	/// what the two search policies would have decided about it rather than acting on either. One
	/// pass therefore serves <c>glob</c>, which wants hidden and ignored files, and <c>grep</c>, which
	/// wants neither. Hidden and ignored both propagate downward, matching git: everything beneath an
	/// ignored directory is ignored, and everything beneath a dot-directory is hidden.
	/// </summary>
	/// <remarks>
	/// Unlike <see cref="Walk"/> this never prunes on those two rules, so it descends into
	/// <c>node_modules</c> and the like exactly as today's <c>glob</c> already does. The cost is paid
	/// once and retained, which is the whole point of indexing it.
	/// </remarks>
	public IEnumerable<IndexedWalkEntry> WalkIndexed(string root, CancellationToken cancellationToken)
	{
		Truncation = WalkTruncation.None;
		var deadline = Environment.TickCount64 + (long)_timeout.TotalMilliseconds;
		var examined = 0;
		var stack = new Stack<(string Directory, string Relative, bool Hidden, bool Ignored, IReadOnlyList<IgnoreLayer> ParentLayers)>();
		stack.Push((root, string.Empty, false, false, []));

		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (directory, relative, parentHidden, parentIgnored, parentLayers) = stack.Pop();
			var layers = parentLayers;
			if (TryLoadLayer(directory, relative) is { } layer) layers = [.. parentLayers, layer];

			FileSystemInfo[] entries;
			try { entries = Ordered(directory); }
			catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
			{
				continue;
			}

			foreach (var entry in entries)
			{
				if (++examined > _budget) { Truncation = WalkTruncation.Budget; yield break; }
				if (examined % DeadlineCheckInterval == 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (Environment.TickCount64 >= deadline) { Truncation = WalkTruncation.Deadline; yield break; }
				}

				var name = entry.Name;
				var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
				var attributes = SafeAttributes(entry);
				var isDirectory = attributes.HasFlag(FileAttributes.Directory);

				// Same two hard exclusions as the pruning walk: a reparse point turns a package
				// workspace into a cyclic graph, and VCS metadata is never a search result.
				if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
				if (isDirectory && VcsExcludes.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

				var hidden = parentHidden || name.StartsWith('.');
				var ignored = parentIgnored || IsIgnored(layers, childRelative, isDirectory);

				long size = 0;
				long ticks = 0;
				try
				{
					if (entry is FileInfo file) size = file.Length;
					ticks = entry.LastWriteTimeUtc.Ticks;
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// An entry that vanished between the scan and the read is reported with zeroes; the
					// next revalidation drops it.
				}

				yield return new IndexedWalkEntry(childRelative, entry.FullName, isDirectory, hidden, ignored, size, ticks);
				if (isDirectory) stack.Push((entry.FullName, childRelative, hidden, ignored, layers));
			}
		}
	}
}

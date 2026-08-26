using Suharness.Abstractions;
using System.Text;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// The model-facing <c>glob</c> and <c>grep</c> tools. SuHarness implements their contract natively,
/// including ripgrep's differing defaults between the two tools: <c>glob</c>
/// runs <c>--no-ignore --hidden</c>, while <c>grep</c> honours <c>.gitignore</c> and skips hidden
/// and binary files.
/// </summary>
/// <remarks>
/// Both tools prefer the <see cref="IWorkspaceIndex"/> when one is supplied and the search target is
/// inside the session workspace, which is what stops a turn issuing a dozen searches from paying for
/// a dozen traversals. The walker remains the fallback — for a full-access session searching outside
/// its workspace, and for any caller that has no index — so the observable contract of both tools is
/// identical either way.
/// </remarks>
public static class SearchTools
{
	/// <summary>Maximum paths one <c>glob</c> call returns inline.</summary>
	public const int GlobMaxResults = 100;

	/// <summary>Maximum matches one <c>grep</c> call returns inline.</summary>
	public const int GrepMaxMatches = 250;

	/// <summary>Maximum bytes of any single previewed match line.</summary>
	public const int GrepMaxLineBytes = 2000;

	/// <summary>
	/// Wall-clock budget for one search. SuHarness walks in-process, so the walker
	/// enforces it itself. Without a bound, one search over a large or link-heavy tree never returns
	/// a result and the whole turn hangs, because no tool result reaches the model until every call
	/// in the batch has completed.
	/// </summary>
	public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// One candidate file. <paramref name="Relative"/> is the path a pattern is matched against —
	/// relative to the search target, which is not the workspace root when a <c>path</c> was given —
	/// while <paramref name="Display"/> is the workspace-relative path the model sees.
	/// </summary>
	/// <remarks>
	/// The absolute path is deliberately absent. <c>glob</c> never needs one, and materialising it for
	/// every indexed file would allocate a string per file per search on a workspace where that is
	/// exactly the cost the index exists to avoid.
	/// </remarks>
	private readonly record struct Candidate(string Relative, string Display, long ModifiedTicks);

	/// <summary>Execute one <c>glob</c> call: discover files whose paths match a glob pattern.</summary>
	/// <param name="timeout">Wall-clock budget for the traversal; null uses <see cref="SearchTimeout"/>.</param>
	/// <param name="index">The retained workspace table, or null to walk the tree for this call.</param>
	public static string Glob(
		WorkspaceScope scope,
		string pattern,
		string? path,
		CancellationToken cancellationToken,
		TimeSpan? timeout = null,
		IWorkspaceIndex? index = null)
	{
		var root = scope.ResolveDirectoryOrRoot(path, "path");
		if (!Directory.Exists(root))
			throw new ToolInputException($"cannot list \"{scope.Display(root)}\": not a directory");

		var compiled = GlobPattern.Compile(pattern, caseInsensitive: OperatingSystem.IsWindows());
		var matches = new List<Candidate>();
		string? incomplete;

		if (FromIndex(scope, root, index, searchableOnly: false, cancellationToken) is { } indexed)
		{
			foreach (var candidate in indexed.Items)
			{
				if (PrivateAgentState(candidate.Display)) continue;
				if (compiled.IsMatch(candidate.Relative)) matches.Add(candidate);
			}
			incomplete = IndexNotice(indexed.Truncation);
		}
		else
		{
			var walker = new FileWalker(respectGitIgnore: false, includeHidden: true, timeout: timeout);
			foreach (var (relative, full) in walker.Walk(root, cancellationToken))
			{
				if (PrivateAgentState(scope.Display(full))) continue;
				if (!compiled.IsMatch(relative)) continue;
				DateTime modified;
				try { modified = File.GetLastWriteTimeUtc(full); }
				catch (IOException) { continue; }
				matches.Add(new Candidate(relative, scope.Display(full), modified.Ticks));
			}
			incomplete = TruncationNotice(walker);
		}

		if (matches.Count == 0) return incomplete is null ? "No files found" : $"No files found\n\n{incomplete}";

		// ripgrep's `--sort=modified` is ascending, so oldest first — matched here deliberately. Paths
		// break a tie so two files written in the same tick cannot swap places between calls.
		matches.Sort(static (left, right) => left.ModifiedTicks != right.ModifiedTicks
			? left.ModifiedTicks.CompareTo(right.ModifiedTicks)
			: string.CompareOrdinal(left.Display, right.Display));
		var paths = matches.Select(match => match.Display).ToList();

		var body = paths.Count <= GlobMaxResults
			? string.Join('\n', paths)
			: $"{string.Join('\n', paths.Take(GlobMaxResults))}\n\n(Showing {GlobMaxResults} of {paths.Count} paths. "
				+ "The complete result could not be saved; narrow pattern or path to see more.)";
		return incomplete is null ? body : $"{body}\n\n{incomplete}";
	}

	/// <summary>
	/// The line appended when traversal stopped early. A partial result the model can act on beats a
	/// call that never returns, but the model has to be told the listing is not the whole tree.
	/// </summary>
	private static bool PrivateAgentState(string path)
	{
		var normalized = path.Replace('\\', '/').TrimStart('.', '/');
		return normalized.Equals("suharness", StringComparison.OrdinalIgnoreCase)
			|| normalized.StartsWith("suharness/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/.suharness/", StringComparison.OrdinalIgnoreCase)
			|| normalized.EndsWith("/.suharness", StringComparison.OrdinalIgnoreCase);
	}

	private static string? TruncationNotice(FileWalker walker) => walker.Truncation switch
	{
		WalkTruncation.Deadline =>
			$"(Search stopped after {walker.Timeout.TotalSeconds:F0}s and these results are partial. "
			+ "Narrow path to a specific subdirectory to search the rest.)",
		WalkTruncation.Budget =>
			"(Search stopped at the traversal limit and these results are partial. "
			+ "Narrow path to a specific subdirectory to search the rest.)",
		_ => null,
	};

	/// <summary>
	/// The same warning for a search served from a table that is itself incomplete. It is worded
	/// differently because nothing was cut short by this call: the tree was already too large to
	/// finish scanning, and every search will be partial until the target is narrowed.
	/// </summary>
	private static string? IndexNotice(IndexTruncation truncation) => truncation switch
	{
		IndexTruncation.Deadline or IndexTruncation.Budget =>
			"(The workspace index could not cover the whole tree, so these results are partial. "
			+ "Narrow path to a specific subdirectory to search the rest.)",
		_ => null,
	};

	/// <summary>Execute one <c>grep</c> call: search file contents with a regular expression.</summary>
	public static string Grep(
		WorkspaceScope scope,
		string pattern,
		string? path,
		string? include,
		CancellationToken cancellationToken,
		TimeSpan? timeout = null,
		IWorkspaceIndex? index = null)
	{
		Regex regex;
		try { regex = new Regex(pattern, RegexOptions.CultureInvariant); }
		catch (ArgumentException exception)
		{
			throw new ToolInputException($"pattern is not a valid regular expression: {exception.Message}");
		}

		var target = scope.ResolveDirectoryOrRoot(path, "path");
		var includeFilter = string.IsNullOrWhiteSpace(include)
			? null
			: GlobPattern.Compile(include, caseInsensitive: OperatingSystem.IsWindows());

		var retained = new List<(string Path, int LineNumber, string Line)>();
		var seen = 0;

		void Search(string relative, string full, string display)
		{
			if (includeFilter is not null && !includeFilter.IsMatch(relative)) return;
			if (IsBinary(full)) return;

			var lineNumber = 0;
			try
			{
				using var reader = new StreamReader(
					new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
					Encoding.UTF8,
					detectEncodingFromByteOrderMarks: true);
				while (reader.ReadLine() is { } line)
				{
					cancellationToken.ThrowIfCancellationRequested();
					lineNumber++;
					if (!regex.IsMatch(line)) continue;
					seen++;
					if (retained.Count < GrepMaxMatches) retained.Add((display, lineNumber, Preview(line)));
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// An unreadable file is skipped, exactly as ripgrep skips it.
			}
		}

		string? incomplete;
		if (File.Exists(target))
		{
			Search(Path.GetFileName(target), target, scope.Display(target));
			incomplete = null;
		}
		else if (!Directory.Exists(target))
		{
			throw new ToolInputException($"cannot read \"{scope.Display(target)}\": not found");
		}
		else if (FromIndex(scope, target, index, searchableOnly: true, cancellationToken) is { } indexed)
		{
			// The index has already applied both of grep's exclusions, so only files its walk would
			// have reached are opened at all — the discovery pass is what the index removes here.
			foreach (var candidate in indexed.Items)
			{
				cancellationToken.ThrowIfCancellationRequested();
				Search(candidate.Relative, Absolute(scope.Root, candidate.Display), candidate.Display);
			}
			incomplete = IndexNotice(indexed.Truncation);
		}
		else
		{
			// The walk is buffered and ordered before anything is read, so this path visits files in
			// the same order the indexed one does. Order is not cosmetic here: grep stops retaining
			// after a fixed number of matches, so the traversal order decides which matches survive.
			// Reading every candidate's contents dominates the cost of holding their paths.
			var walker = new FileWalker(respectGitIgnore: true, includeHidden: false, timeout: timeout);
			var candidates = new List<(string Relative, string Full, string Display)>();
			foreach (var (relative, full) in walker.Walk(target, cancellationToken))
			{
				candidates.Add((relative, full, scope.Display(full)));
			}
			candidates.Sort(static (left, right) => string.CompareOrdinal(left.Display, right.Display));
			foreach (var (relative, full, display) in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				Search(relative, full, display);
			}
			incomplete = TruncationNotice(walker);
		}

		if (seen == 0) return incomplete is null ? "No matches found" : $"No matches found\n\n{incomplete}";

		var truncated = seen > retained.Count;
		var header = truncated ? $"Found {retained.Count} of {seen} matches" : $"Found {seen} {(seen == 1 ? "match" : "matches")}";
		var body = FormatMatches(retained);
		if (truncated)
		{
			body += "\n\n(The complete result could not be saved; narrow pattern, path, or include to see more.)";
		}
		return incomplete is null ? $"{header}\n\n{body}" : $"{header}\n\n{body}\n\n{incomplete}";
	}

	/// <summary>
	/// The candidate files under <paramref name="target"/> according to the retained table, or null
	/// when this search cannot be served from it — no index, or a full-access session reaching
	/// outside the workspace the index describes.
	/// </summary>
	private static (IReadOnlyList<Candidate> Items, IndexTruncation Truncation)? FromIndex(
		WorkspaceScope scope,
		string target,
		IWorkspaceIndex? index,
		bool searchableOnly,
		CancellationToken cancellationToken)
	{
		if (index is null) return null;
		if (RelativeTo(scope.Root, target) is not { } prefix) return null;

		var snapshot = index.Snapshot(scope.Root, cancellationToken);
		if (!PathsEqual(snapshot.Root, scope.Root)) return null;

		var items = new List<Candidate>();
		foreach (var file in snapshot.Files)
		{
			if (searchableOnly && !file.Searchable) continue;
			string relative;
			if (prefix.Length == 0)
			{
				relative = file.RelativePath;
			}
			else if (file.RelativePath.Length > prefix.Length
				&& file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)
				&& file.RelativePath[prefix.Length] == '/')
			{
				relative = file.RelativePath[(prefix.Length + 1)..];
			}
			else
			{
				continue;
			}

			items.Add(new Candidate(relative, file.RelativePath, file.ModifiedTicks));
		}

		// The snapshot is already ordered by path, so the candidates inherit the stable order grep
		// needs to group its output identically across calls without sorting again here.
		return (items, snapshot.Truncation);
	}

	/// <summary>Rebuild the absolute path of a workspace-relative display path.</summary>
	private static string Absolute(string root, string display) =>
		Path.Combine(root, display.Replace('/', Path.DirectorySeparatorChar));

	/// <summary>The '/'-separated path from <paramref name="root"/> to <paramref name="target"/>, or null when outside it.</summary>
	private static string? RelativeTo(string root, string target)
	{
		if (PathsEqual(root, target)) return string.Empty;
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		if (target.Length <= root.Length
			|| !target.AsSpan(0, root.Length).Equals(root, comparison)
			|| target[root.Length] != Path.DirectorySeparatorChar)
		{
			return null;
		}
		return target[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
	}

	private static bool PathsEqual(string left, string right) => string.Equals(
		Path.TrimEndingDirectorySeparator(left),
		Path.TrimEndingDirectorySeparator(right),
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	/// <summary>Group flat matches by file in first-seen order.</summary>
	private static string FormatMatches(List<(string Path, int LineNumber, string Line)> matches)
	{
		var sections = new List<string>();
		var byFile = new Dictionary<string, List<(int LineNumber, string Line)>>(StringComparer.Ordinal);
		var order = new List<string>();

		foreach (var (path, lineNumber, line) in matches)
		{
			if (!byFile.TryGetValue(path, out var group))
			{
				byFile[path] = group = [];
				order.Add(path);
			}
			group.Add((lineNumber, line));
		}

		foreach (var path in order)
		{
			var rows = byFile[path].Select(row => $"Line {row.LineNumber}: {row.Line}");
			sections.Add($"{path}\n{string.Join('\n', rows)}");
		}
		return string.Join("\n\n", sections);
	}

	private static string Preview(string line) =>
		Encoding.UTF8.GetByteCount(line) <= GrepMaxLineBytes ? line
			: line[..Math.Min(line.Length, GrepMaxLineBytes)];

	/// <summary>Detect a binary file the way ripgrep does: a NUL byte in the leading window.</summary>
	private static bool IsBinary(string fullPath)
	{
		try
		{
			using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			Span<byte> probe = stackalloc byte[8192];
			var read = stream.Read(probe);
			return probe[..read].IndexOf((byte)0) >= 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}
}

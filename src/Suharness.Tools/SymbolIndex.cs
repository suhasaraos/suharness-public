using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;

namespace Suharness.Tools;

/// <summary>
/// A name-to-declaration table for one workspace, built on top of <see cref="IWorkspaceIndex"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the entry point the semantic tools did not have. <c>definition</c>, <c>references</c>,
/// and <c>hover</c> can only answer questions about a position, so reaching them required knowing a
/// file, a line, and a column first — which the model could only get by grepping, the step the
/// symbol lookup was supposed to replace. A name is what an agent actually starts from.
/// </para>
/// <para>
/// Building on the file index rather than on its own walk is what makes it cheap to keep current:
/// the file table already carries every source file with its size and write time, so a refresh
/// rescans only files whose stamp moved and leaves the rest alone. Files that <c>grep</c> would skip
/// — ignored and hidden — are skipped here too, which is what keeps a vendored dependency tree out
/// of the symbol table.
/// </para>
/// </remarks>
public sealed class SymbolIndex : ISymbolIndex
{
	/// <summary>Format version of the persisted table. A mismatch is discarded rather than migrated.</summary>
	public const int FormatVersion = 1;

	/// <summary>Cap on declarations held for one workspace, so an enormous tree cannot exhaust memory.</summary>
	public const int MaxSymbols = 400_000;

	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private readonly IWorkspaceIndex _files;
	private readonly string? _storeRoot;
	private readonly ConcurrentDictionary<string, Table> _tables = new(PathComparer);

	public SymbolIndex(IWorkspaceIndex files, string? storeRoot = null)
	{
		_files = files;
		_storeRoot = string.IsNullOrWhiteSpace(storeRoot) ? null : Path.GetFullPath(storeRoot);
	}

	/// <summary>One workspace's declarations, keyed by the file they came from.</summary>
	private sealed class Table
	{
		public readonly Lock Gate = new();
		public readonly SemaphoreSlim PersistGate = new(1, 1);
		public string Root = string.Empty;
		public Dictionary<string, ScannedFile> ByFile = new(StringComparer.Ordinal);
		public List<SymbolHit> Flat = [];
		public bool Loaded;
		public object? LastSnapshot;
	}

	/// <summary>One file's declarations, stamped with the version they were read from.</summary>
	private sealed record ScannedFile(long Size, long ModifiedTicks, IReadOnlyList<SymbolHit> Symbols);

	public bool IsAvailable(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		var snapshot = _files.Snapshot(workspaceRoot, cancellationToken);
		return snapshot.Files.Any(file => file.Searchable && DeclarationScanner.Handles(file.RelativePath));
	}

	public IReadOnlyList<SymbolHit> Find(
		string workspaceRoot,
		string query,
		string? kind = null,
		int limit = 50,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(query)) return [];
		var table = Refresh(workspaceRoot, cancellationToken);
		if (table is null) return [];

		// A dotted query names a symbol inside something: the last segment is the symbol and the rest
		// constrains its container, so "PlayerController.Move" does not also return every other Move.
		var trimmed = query.Trim();
		var separator = trimmed.LastIndexOfAny(['.', ':', '/', '#']);
		var name = separator < 0 ? trimmed : trimmed[(separator + 1)..];
		var containerHint = separator < 0 ? null : trimmed[..separator].Trim(['.', ':', '/', '#']);
		if (name.Length == 0) return [];

		var scored = new List<(int Score, int ContainerScore, SymbolHit Hit)>();
		lock (table.Gate)
		{
			foreach (var hit in table.Flat)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (kind is { Length: > 0 } && !string.Equals(hit.Kind, kind, StringComparison.OrdinalIgnoreCase)) continue;

				// A container the scanner never inferred is missing evidence, not contrary evidence, so
				// the hit is ranked below a confirmed container rather than dropped. Dropping it would
				// hide the answer whenever the scan could not attribute the declaration.
				var containerScore = 0;
				if (containerHint is { Length: > 0 })
				{
					if (hit.Container is null) containerScore = 1;
					else if (!hit.Container.Contains(containerHint, StringComparison.OrdinalIgnoreCase)) continue;
				}

				var score = Score(hit.Name, name);
				if (score < 0) continue;
				scored.Add((score, containerScore, hit));
			}
		}

		scored.Sort(static (left, right) =>
		{
			if (left.Score != right.Score) return left.Score.CompareTo(right.Score);
			if (left.ContainerScore != right.ContainerScore) return left.ContainerScore.CompareTo(right.ContainerScore);
			// A shorter name that matched the same way is the more specific answer.
			if (left.Hit.Name.Length != right.Hit.Name.Length) return left.Hit.Name.Length.CompareTo(right.Hit.Name.Length);
			var path = string.CompareOrdinal(left.Hit.RelativePath, right.Hit.RelativePath);
			return path != 0 ? path : left.Hit.Line.CompareTo(right.Hit.Line);
		});

		return [.. scored.Take(Math.Max(1, limit)).Select(entry => entry.Hit)];
	}

	public IReadOnlyList<SymbolHit> InFile(
		string workspaceRoot,
		string relativePath,
		CancellationToken cancellationToken = default)
	{
		var table = Refresh(workspaceRoot, cancellationToken);
		if (table is null) return [];
		var normalized = relativePath.Replace('\\', '/').TrimStart('.', '/');
		lock (table.Gate)
		{
			if (table.ByFile.TryGetValue(normalized, out var scanned)) return scanned.Symbols;
			// Path comparison is case-insensitive on Windows, so a differently-cased path still resolves.
			var match = table.ByFile.FirstOrDefault(pair => PathComparer.Equals(pair.Key, normalized));
			return match.Value?.Symbols ?? [];
		}
	}

	/// <summary>
	/// How well a declaration name answers a query, lower being better, or -1 for no match at all.
	/// The ladder runs from an exact match down to a subsequence, which is what lets one call replace
	/// a series of guessed wildcard patterns.
	/// </summary>
	private static int Score(string name, string query)
	{
		if (string.Equals(name, query, StringComparison.Ordinal)) return 0;
		if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 1;
		if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 2;
		if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
		return IsSubsequence(name, query) ? 4 : -1;
	}

	/// <summary>True when every character of <paramref name="query"/> appears in order in <paramref name="name"/>.</summary>
	private static bool IsSubsequence(string name, string query)
	{
		if (query.Length == 0 || query.Length > name.Length) return false;
		var index = 0;
		foreach (var character in name)
		{
			if (char.ToLowerInvariant(character) != char.ToLowerInvariant(query[index])) continue;
			if (++index == query.Length) return true;
		}
		return false;
	}

	// ---------------------------------------------------------------- refreshing

	/// <summary>
	/// Bring the table in line with the file index, rescanning only what changed. Returns null when
	/// the workspace has no files the scanner understands.
	/// </summary>
	private Table? Refresh(string workspaceRoot, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;
		var snapshot = _files.Snapshot(workspaceRoot, cancellationToken);
		if (snapshot.Files.Count == 0) return null;

		var table = _tables.GetOrAdd(snapshot.Root, key => new Table { Root = key });
		lock (table.Gate)
		{
			// The file index hands back the same instance while its own table is unchanged, so an
			// unchanged tree costs one reference comparison rather than a pass over every file.
			if (ReferenceEquals(table.LastSnapshot, snapshot)) return table;

			if (!table.Loaded)
			{
				if (_storeRoot is not null) LoadFromDisk(table);
				table.Loaded = true;
			}

			var scanned = new Dictionary<string, ScannedFile>(StringComparer.Ordinal);
			var total = 0;
			var changed = false;

			foreach (var file in snapshot.Files)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!file.Searchable || !DeclarationScanner.Handles(file.RelativePath)) continue;
				if (total >= MaxSymbols) break;

				if (table.ByFile.TryGetValue(file.RelativePath, out var existing)
					&& existing.Size == file.Size
					&& existing.ModifiedTicks == file.ModifiedTicks)
				{
					scanned[file.RelativePath] = existing;
					total += existing.Symbols.Count;
					continue;
				}

				var full = Path.Combine(snapshot.Root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
				var declarations = DeclarationScanner.ScanFile(full, cancellationToken);
				var symbols = declarations
					.Select(declaration => new SymbolHit(
						declaration.Name,
						declaration.Kind,
						file.RelativePath,
						declaration.Line,
						declaration.Container,
						SymbolSource.Declarations))
					.ToArray();
				scanned[file.RelativePath] = new ScannedFile(file.Size, file.ModifiedTicks, symbols);
				total += symbols.Length;
				changed = true;
			}

			if (scanned.Count != table.ByFile.Count) changed = true;
			table.LastSnapshot = snapshot;
			if (!changed) return table;

			table.ByFile = scanned;
			table.Flat = [.. scanned.Values.SelectMany(entry => entry.Symbols)];
			SchedulePersist(table);
			return table;
		}
	}

	// ---------------------------------------------------------------- persistence

	private void LoadFromDisk(Table table)
	{
		try
		{
			var path = Path.Combine(IndexStore.DirectoryFor(_storeRoot!, table.Root), "symbols.tsv");
			if (!File.Exists(path)) return;

			var byFile = new Dictionary<string, ScannedFile>(StringComparer.Ordinal);
			string? currentPath = null;
			long currentSize = 0;
			long currentTicks = 0;
			List<SymbolHit>? current = null;
			var header = true;

			void Flush()
			{
				if (currentPath is not null && current is not null)
				{
					byFile[currentPath] = new ScannedFile(currentSize, currentTicks, current);
				}
			}

			foreach (var line in File.ReadLines(path))
			{
				var parts = line.Split('\t');
				if (header)
				{
					header = false;
					if (parts.Length != 3
						|| parts[0] != "version"
						|| !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var version)
						|| version != FormatVersion
						|| !PathComparer.Equals(parts[2], table.Root))
					{
						// A hash collision, a moved workspace, or an older format: start clean.
						return;
					}
					continue;
				}

				if (parts.Length == 4 && parts[0] == "f")
				{
					Flush();
					if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out currentSize)
						|| !long.TryParse(parts[2], CultureInfo.InvariantCulture, out currentTicks))
					{
						currentPath = null;
						current = null;
						continue;
					}
					currentPath = parts[3];
					current = [];
					continue;
				}

				if (parts.Length == 5 && parts[0] == "s" && currentPath is not null && current is not null)
				{
					if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out var symbolLine)) continue;
					current.Add(new SymbolHit(
						parts[3],
						parts[2],
						currentPath,
						symbolLine,
						parts[4].Length == 0 ? null : parts[4],
						SymbolSource.Declarations));
				}
			}
			Flush();

			table.ByFile = byFile;
			table.Flat = [.. byFile.Values.SelectMany(entry => entry.Symbols)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
		{
			table.ByFile = new Dictionary<string, ScannedFile>(StringComparer.Ordinal);
			table.Flat = [];
		}
	}

	/// <summary>
	/// Write the table out off the calling thread, for the same reason the file index does: the query
	/// that triggered the rescan already has its answer.
	/// </summary>
	private void SchedulePersist(Table table)
	{
		if (_storeRoot is null) return;
		var rows = new List<string>(table.Flat.Count + table.ByFile.Count + 1)
		{
			string.Create(CultureInfo.InvariantCulture, $"version\t{FormatVersion}\t{table.Root}"),
		};
		foreach (var (relative, scanned) in table.ByFile)
		{
			if (IndexStore.Unwritable(relative)) continue;
			rows.Add(string.Create(CultureInfo.InvariantCulture, $"f\t{scanned.Size}\t{scanned.ModifiedTicks}\t{relative}"));
			foreach (var symbol in scanned.Symbols)
			{
				if (IndexStore.Unwritable(symbol.Name) || IndexStore.Unwritable(symbol.Container ?? string.Empty)) continue;
				rows.Add(string.Create(CultureInfo.InvariantCulture,
					$"s\t{symbol.Line}\t{symbol.Kind}\t{symbol.Name}\t{symbol.Container}"));
			}
		}
		var root = table.Root;

		_ = Task.Run(async () =>
		{
			await table.PersistGate.WaitAsync();
			try
			{
				var directory = IndexStore.DirectoryFor(_storeRoot, root);
				Directory.CreateDirectory(directory);
				IndexStore.WriteAtomic(Path.Combine(directory, "symbols.tsv"), rows);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				// Best effort, exactly as for the file table: an unpersisted symbol table is rebuilt.
			}
			finally { table.PersistGate.Release(); }
		});
	}
}

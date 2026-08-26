using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <param name="MaxFileBytes">
/// Files larger than this are left out of a capture. The cap keeps a workspace full of build output
/// or model weights from turning the snapshot store into a second copy of the disk. It is applied
/// when a file first enters a snapshot; a tracked file that later grows past it keeps being captured.
/// </param>
/// <param name="CaptureTimeoutMs">
/// The deadline one capture may take. An overrun records the turn as uncovered rather than delaying
/// or failing it. It also bounds how long a turn will wait for a workspace that is still being
/// indexed, so a small workspace is covered from its first turn and a large one is not held up by it.
/// </param>
/// <param name="PrimeTimeoutMs">
/// The deadline for indexing a workspace for the first time. It is generous because the pass runs in
/// the background: reading and storing every file in a large tree is minutes of work that happens
/// once, and abandoning it early would mean paying most of the cost and keeping none of the benefit.
/// </param>
/// <param name="Exclude">
/// Extra gitignore-syntax patterns, applied on top of the workspace's own <c>.gitignore</c> files.
/// The default covers the one directory that is always regenerated rather than authored.
/// </param>
public sealed record SnapshotOptions(
	bool Enabled = true,
	long MaxFileBytes = 2L * 1024 * 1024,
	int CaptureTimeoutMs = 30_000,
	int PrimeTimeoutMs = 900_000,
	IReadOnlyList<string>? Exclude = null)
{
	public static readonly string[] DefaultExclude = ["node_modules/"];

	public IReadOnlyList<string> EffectiveExclude => Exclude ?? DefaultExclude;
}

/// <summary>
/// Workspace snapshots backed by a Git object database the harness owns. The database lives under the
/// SuHarness home directory and is addressed with an explicit <c>--git-dir</c>/<c>--work-tree</c>
/// pair, so nothing is ever written inside the workspace: the user's own index, branches, reflog, and
/// hooks are untouched, and a workspace that is not a Git repository is snapshotted just the same.
/// </summary>
/// <remarks>
/// Capture stages the worktree and writes a tree object; the tree hash is the snapshot handle, so two
/// identical states share one handle for free. Restore diffs the target tree against a freshly
/// captured tree of the present state and touches only the paths that differ, which is what keeps an
/// undo from rewriting every file in the workspace and forcing an editor reload of all of them.
/// </remarks>
public sealed class GitSnapshotService : ISnapshotService
{
	/// <summary>The ref every capture is chained onto, so no tree is ever unreachable.</summary>
	private const string SnapshotRef = "refs/suharness/snapshots";

	/// <summary>Git's null object id, which a raw diff uses for the side a path is absent from.</summary>
	private const string NullSha = "0000000000000000000000000000000000000000";

	private readonly string _storeRoot;
	private readonly SnapshotOptions _options;
	private readonly Action<string>? _log;
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Task> _priming = new(StringComparer.OrdinalIgnoreCase);

	public GitSnapshotService(string storeRoot, SnapshotOptions? options = null, Action<string>? log = null)
	{
		_storeRoot = Path.GetFullPath(storeRoot);
		_options = options ?? new SnapshotOptions();
		_log = log;
	}

	public bool Enabled => _options.Enabled;

	public SnapshotOptions Options => _options;

	/// <summary>
	/// Read <c>snapshots.json</c> from the configuration directory. A missing or unreadable file uses
	/// the defaults: a snapshot store is a safety feature, so a typo in its configuration must not be
	/// what silently switches it off.
	/// </summary>
	public static SnapshotOptions LoadOptions(string configurationDirectory, Action<string>? log = null)
	{
		var path = Path.Combine(configurationDirectory, "snapshots.json");
		if (!File.Exists(path)) return new SnapshotOptions();
		try
		{
			var options = JsonSerializer.Deserialize<SnapshotOptions>(
				File.ReadAllText(path),
				new JsonSerializerOptions(JsonSerializerDefaults.Web));
			return options is null ? new SnapshotOptions() : options with
			{
				MaxFileBytes = Math.Max(0, options.MaxFileBytes),
				CaptureTimeoutMs = Math.Clamp(options.CaptureTimeoutMs, 1_000, 600_000),
				PrimeTimeoutMs = Math.Clamp(options.PrimeTimeoutMs, 1_000, 3_600_000),
			};
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			log?.Invoke($"snapshots: {path} could not be read ({exception.Message}); using defaults.");
			return new SnapshotOptions();
		}
	}

	/// <summary>The object database backing one workspace. Stable across runs, derived from its path.</summary>
	public string StoreFor(string workspaceRoot)
	{
		var full = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..16];
		var leaf = new string([.. Path.GetFileName(full).Select(character =>
			char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')]);
		if (leaf.Length == 0) leaf = "workspace";
		return Path.Combine(_storeRoot, $"{leaf}-{digest}");
	}

	/// <summary>The marker written once a workspace has been indexed, so readiness survives a restart.</summary>
	private static string PrimedMarker(string store) => Path.Combine(store, "suharness-primed");

	public bool IsReady(string workspaceRoot) =>
		!_options.Enabled || !Usable(workspaceRoot) || File.Exists(PrimedMarker(StoreFor(workspaceRoot)));

	/// <summary>
	/// Index the workspace once. Every later capture is then a delta against the index, which is the
	/// difference between roughly a hundred milliseconds per turn and minutes of it.
	/// </summary>
	public Task PrimeAsync(string workspaceRoot)
	{
		if (!_options.Enabled || !Usable(workspaceRoot)) return Task.CompletedTask;
		var store = StoreFor(workspaceRoot);
		if (File.Exists(PrimedMarker(store))) return Task.CompletedTask;

		// One pass per store, shared by every caller that arrives while it runs.
		return _priming.GetOrAdd(store, _ => Task.Run(async () =>
		{
			try
			{
				using var deadline = new CancellationTokenSource(_options.PrimeTimeoutMs);
				var gate = _gates.GetOrAdd(store, _ => new SemaphoreSlim(1, 1));
				await gate.WaitAsync(deadline.Token);
				try
				{
					var stopwatch = Stopwatch.StartNew();
					await CaptureCoreAsync(workspaceRoot, deadline.Token);
					await File.WriteAllTextAsync(PrimedMarker(store), workspaceRoot, CancellationToken.None);
					_log?.Invoke($"snapshots: indexed {workspaceRoot} for undo in {stopwatch.ElapsedMilliseconds} ms.");
				}
				finally
				{
					gate.Release();
				}
			}
			catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
			{
				// Leaving the marker unwritten and the entry cleared means the next turn tries again,
				// rather than the workspace being permanently uncoverable because of one bad moment.
				_priming.TryRemove(store, out Task? _);
				_log?.Invoke($"snapshots: indexing {workspaceRoot} failed ({exception.Message}); undo is unavailable until it succeeds.");
			}
		}));
	}

	public async Task<SnapshotCapture?> CaptureAsync(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		if (!_options.Enabled || !Usable(workspaceRoot)) return null;

		var store = StoreFor(workspaceRoot);
		if (!File.Exists(PrimedMarker(store)))
		{
			// A small workspace indexes in well under the capture deadline, so waiting covers its very
			// first turn. A large one keeps indexing in the background and this turn goes uncovered
			// rather than stalling behind minutes of work the user never asked for.
			var priming = PrimeAsync(workspaceRoot);
			using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			var elapsed = Task.Delay(_options.CaptureTimeoutMs, wait.Token);
			var finished = await Task.WhenAny(priming, elapsed);
			await wait.CancelAsync();
			if (finished != priming || !File.Exists(PrimedMarker(store))) return null;
		}

		var stopwatch = Stopwatch.StartNew();
		var gate = _gates.GetOrAdd(StoreFor(workspaceRoot), _ => new SemaphoreSlim(1, 1));
		using var deadline = Deadline(cancellationToken);
		try
		{
			await gate.WaitAsync(deadline.Token);
		}
		catch (OperationCanceledException)
		{
			_log?.Invoke("snapshots: capture skipped, another capture held the workspace past the deadline.");
			return null;
		}

		try
		{
			var capture = await CaptureCoreAsync(workspaceRoot, deadline.Token);
			return capture is null ? null : capture with { ElapsedMs = stopwatch.ElapsedMilliseconds };
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			_log?.Invoke($"snapshots: capture exceeded {_options.CaptureTimeoutMs} ms and was abandoned.");
			return null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			_log?.Invoke($"snapshots: capture failed ({exception.Message}).");
			return null;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<IReadOnlyList<SnapshotChange>> PreviewAsync(
		string workspaceRoot,
		string targetTree,
		CancellationToken cancellationToken = default)
	{
		if (!IsHash(targetTree)) return [];
		var current = await CaptureAsync(workspaceRoot, cancellationToken);
		if (current is null) return [];
		var store = StoreFor(workspaceRoot);
		targetTree = await NormalizeLegacyGitlinksAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
		var raw = await DiffAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
		return [.. raw.Select(ToChange)];
	}

	public async Task<WorkspaceFileDiff?> GetFileDiffAsync(
		string workspaceRoot,
		string targetTree,
		string path,
		CancellationToken cancellationToken = default)
	{
		if (!IsHash(targetTree)) return null;
		var requested = NormalizeRelativePath(path);
		var current = await CaptureAsync(workspaceRoot, cancellationToken);
		if (current is null) return null;
		var store = StoreFor(workspaceRoot);
		var legacyTarget = targetTree;
		targetTree = await NormalizeLegacyGitlinksAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
		var change = (await DiffAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken))
			.FirstOrDefault(item => PathMatches(item, requested));
		if (change is null) return null;
		if (change.Kind != SnapshotChangeKind.Added
			&& await LegacyNestedPatchAsync(store, workspaceRoot, legacyTarget, requested, cancellationToken) is { Length: > 0 } legacyPatch)
		{
			return new WorkspaceFileDiff(ToChange(change), legacyPatch);
		}

		var arguments = new List<string>
		{
			"diff-tree", "-r", "--no-commit-id", "--find-renames", "--patch", "--text", "--no-ext-diff",
			"--src-prefix=a/", "--dst-prefix=b/", targetTree, current.TreeHash, "--",
		};
		if (change.OriginalPath is not null) arguments.Add(change.OriginalPath);
		arguments.Add(change.Path);
		var patch = (await GitAsync(store, workspaceRoot, [.. arguments], cancellationToken)).Stdout;
		return new WorkspaceFileDiff(ToChange(change), patch);
	}

	private static async Task<string?> LegacyNestedPatchAsync(
		string store,
		string workspaceRoot,
		string targetTree,
		string requested,
		CancellationToken cancellationToken)
	{
		var listing = await GitAsync(store, workspaceRoot, ["ls-tree", "-r", "-z", targetTree], cancellationToken);
		var gitlink = Gitlinks(listing.Stdout)
			.Where(item => requested.StartsWith(item.Path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(item => item.Path.Length)
			.FirstOrDefault();
		if (gitlink is null) return null;

		var nestedRoot = Path.GetFullPath(Path.Combine(workspaceRoot, gitlink.Path.Replace('/', Path.DirectorySeparatorChar)));
		if (!Directory.Exists(nestedRoot)) return null;
		var nestedPath = requested[(gitlink.Path.TrimEnd('/').Length + 1)..];
		var patch = await GitAsync(store, nestedRoot,
			["diff", "--no-ext-diff", "--text", $"--src-prefix=a/{gitlink.Path.TrimEnd('/')}/", $"--dst-prefix=b/{gitlink.Path.TrimEnd('/')}/", gitlink.Sha, "--", nestedPath],
			cancellationToken, useGitDir: false);
		return patch.Stdout;
	}

	public async Task<SnapshotChange?> RestoreFileAsync(
		string workspaceRoot,
		string targetTree,
		string path,
		CancellationToken cancellationToken = default)
	{
		if (!Usable(workspaceRoot)) throw new InvalidOperationException("This session has no workspace to restore.");
		if (!IsHash(targetTree)) throw new ArgumentException("Not a snapshot hash.", nameof(targetTree));
		var requested = NormalizeRelativePath(path);
		var current = await CaptureAsync(workspaceRoot, cancellationToken)
			?? throw new InvalidOperationException("The workspace could not be captured, so it was not restored.");
		var store = StoreFor(workspaceRoot);
		var gate = _gates.GetOrAdd(store, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken);
		try
		{
			targetTree = await NormalizeLegacyGitlinksAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
			var change = (await DiffAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken))
				.FirstOrDefault(item => PathMatches(item, requested));
			if (change is null) return null;
			await RestoreChangeAsync(store, workspaceRoot, change, cancellationToken);
			await StageAsync(store, workspaceRoot, cancellationToken);
			await SealAsync(store, workspaceRoot, cancellationToken);
			return ToChange(change);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<IReadOnlyList<SnapshotChange>> RestoreAsync(
		string workspaceRoot,
		string targetTree,
		CancellationToken cancellationToken = default)
	{
		if (!Usable(workspaceRoot)) throw new InvalidOperationException("This session has no workspace to restore.");
		if (!IsHash(targetTree)) throw new ArgumentException("Not a snapshot hash.", nameof(targetTree));

		// The present state is captured first, so the diff is against what is actually on disk rather
		// than against the last snapshot, which is stale if anything changed the files since.
		var current = await CaptureAsync(workspaceRoot, cancellationToken)
			?? throw new InvalidOperationException("The workspace could not be captured, so it was not restored.");

		var store = StoreFor(workspaceRoot);
		var gate = _gates.GetOrAdd(store, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(cancellationToken);
		try
		{
			targetTree = await NormalizeLegacyGitlinksAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
			var raw = await DiffAsync(store, workspaceRoot, targetTree, current.TreeHash, cancellationToken);
			var changes = raw.Select(ToChange).ToArray();
			if (raw.Count == 0) return changes;

			// Each differing file is written from its blob individually rather than by pointing the
			// index at the target tree and checking it back out. Repointing the index rewrites an
			// entry for every file in the workspace and discards the stat cache for all of them, so on
			// a large tree undoing three files costs seconds and leaves the next capture re-hashing
			// everything. Writing the blobs leaves the other entries — and their stat cache — alone.
			var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var change in raw)
			{
				foreach (var directory in await RestoreChangeAsync(store, workspaceRoot, change, cancellationToken))
					emptied.Add(directory);
			}

			PruneEmptyDirectories(workspaceRoot, emptied);

			// Leave the index describing what is now on disk, so the next capture is a plain delta.
			await StageAsync(store, workspaceRoot, cancellationToken);
			await SealAsync(store, workspaceRoot, cancellationToken);
			return changes;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>A workspace path that can be snapshotted: named and present. A chat session has none.</summary>
	private static bool Usable(string workspaceRoot) =>
		!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot);

	private CancellationTokenSource Deadline(CancellationToken cancellationToken)
	{
		var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		source.CancelAfter(_options.CaptureTimeoutMs);
		return source;
	}

	private async Task<SnapshotCapture?> CaptureCoreAsync(string workspaceRoot, CancellationToken cancellationToken)
	{
		var store = StoreFor(workspaceRoot);
		await EnsureStoreAsync(store, workspaceRoot, cancellationToken);

		var skipped = await WriteExcludesAsync(store, workspaceRoot, cancellationToken);
		await StageAsync(store, workspaceRoot, cancellationToken);
		var tree = await SealAsync(store, workspaceRoot, cancellationToken);
		return tree is null ? null : new SnapshotCapture(tree, skipped, 0);
	}

	/// <summary>
	/// Stage the whole worktree. Embedded repositories are flattened into ordinary files: otherwise
	/// Git records only a 160000 gitlink, making every uncommitted file inside it invisible.
	/// </summary>
	private static async Task StageAsync(string store, string workspaceRoot, CancellationToken cancellationToken)
	{
		await GitAsync(store, workspaceRoot, ["add", "-A", "--", "."], cancellationToken);
		while (true)
		{
			var staged = await GitAsync(store, workspaceRoot, ["ls-files", "--stage", "-z"], cancellationToken);
			var gitlinks = Gitlinks(staged.Stdout);
			if (gitlinks.Length == 0) return;

			foreach (var gitlink in gitlinks)
			{
				var nestedRoot = Path.GetFullPath(Path.Combine(workspaceRoot, gitlink.Path.Replace('/', Path.DirectorySeparatorChar)));
				var nestedIndex = NestedIndexPath(store, gitlink.Path);
				await PrepareNestedIndexAsync(store, nestedRoot, nestedIndex, cancellationToken);
				await GitAsync(store, nestedRoot, ["add", "-A", "--", "."], cancellationToken, indexFile: nestedIndex);
				var tree = (await GitAsync(store, nestedRoot, ["write-tree"], cancellationToken, indexFile: nestedIndex)).Stdout.Trim();
				if (!IsHash(tree)) throw new InvalidOperationException($"git could not snapshot nested repository '{gitlink.Path}'.");

				await GitAsync(store, workspaceRoot, ["rm", "--cached", "--quiet", "--force", "--", gitlink.Path], cancellationToken);
				await GitAsync(store, workspaceRoot, ["read-tree", $"--prefix={gitlink.Path.TrimEnd('/')}/", tree], cancellationToken);
			}
		}
	}

	/// <summary>
	/// Seed a harness-owned nested index from the user's stat cache when it is clean. Importing HEAD
	/// first makes every copied blob available in our object store; <c>add -A</c> then hashes only the
	/// files whose worktree state actually differs. A staged user index is never copied, because its
	/// dangling blobs are not guaranteed to be reachable from HEAD.
	/// </summary>
	private static async Task PrepareNestedIndexAsync(
		string store,
		string nestedRoot,
		string nestedIndex,
		CancellationToken cancellationToken)
	{
		if (File.Exists(nestedIndex)) return;
		try { File.Delete(nestedIndex + ".lock"); } catch (IOException) { }

		var gitDirectory = (await GitAsync(store, nestedRoot, ["rev-parse", "--absolute-git-dir"], cancellationToken, check: false, useGitDir: false)).Stdout.Trim();
		var head = (await GitAsync(store, nestedRoot, ["rev-parse", "--verify", "HEAD"], cancellationToken, check: false, useGitDir: false)).Stdout.Trim();
		if (Directory.Exists(gitDirectory) && IsHash(head))
		{
			await GitAsync(store, nestedRoot, ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", gitDirectory, head], cancellationToken);
			var staged = await GitAsync(store, nestedRoot, ["diff", "--cached", "--quiet"], cancellationToken, check: false, useGitDir: false);
			var sourceIndex = Path.Combine(gitDirectory, "index");
			if (staged.ExitCode == 0 && File.Exists(sourceIndex))
			{
				File.Copy(sourceIndex, nestedIndex, overwrite: false);
				var readable = await GitAsync(store, nestedRoot, ["ls-files", "--stage"], cancellationToken, check: false, indexFile: nestedIndex);
				if (readable.ExitCode == 0) return;
				File.Delete(nestedIndex);
			}

			await GitAsync(store, nestedRoot, ["read-tree", head], cancellationToken, indexFile: nestedIndex);
			return;
		}

		await GitAsync(store, nestedRoot, ["read-tree", "--empty"], cancellationToken, indexFile: nestedIndex);
	}

	private static string NestedIndexPath(string store, string path)
	{
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
		return Path.Combine(store, $"nested-{hash}.index");
	}

	/// <summary>
	/// Normalize snapshots created before nested repositories were flattened. A legacy gitlink only
	/// recorded the nested commit, not its worktree bytes. Starting from the current raw tree and
	/// rewinding only paths Git reports changed avoids making every CRLF/filter conversion look edited.
	/// </summary>
	private static async Task<string> NormalizeLegacyGitlinksAsync(
		string store,
		string workspaceRoot,
		string tree,
		string currentTree,
		CancellationToken cancellationToken)
	{
		var temporaryIndex = Path.Combine(store, $"legacy-{Guid.NewGuid():N}.index");
		try
		{
			var listing = await GitAsync(store, workspaceRoot, ["ls-tree", "-r", "-z", tree], cancellationToken);
			var gitlinks = Gitlinks(listing.Stdout);
			if (gitlinks.Length == 0) return tree;
			await GitAsync(store, workspaceRoot, ["read-tree", currentTree], cancellationToken, indexFile: temporaryIndex);

			foreach (var gitlink in gitlinks)
			{
				var nestedRoot = Path.GetFullPath(Path.Combine(workspaceRoot, gitlink.Path.Replace('/', Path.DirectorySeparatorChar)));
				if (!Directory.Exists(nestedRoot)) continue;
				var nestedGit = (await GitAsync(store, nestedRoot, ["rev-parse", "--absolute-git-dir"], cancellationToken, check: false, useGitDir: false)).Stdout.Trim();
				if (!Directory.Exists(nestedGit)) continue;

				await GitAsync(store, workspaceRoot, ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", nestedGit, gitlink.Sha], cancellationToken);
				var tracked = (await GitAsync(store, nestedRoot, ["diff", "--name-status", "-z", "--no-renames", gitlink.Sha, "--"], cancellationToken, useGitDir: false)).Stdout
					.Split('\0', StringSplitOptions.RemoveEmptyEntries);
				for (var index = 0; index + 1 < tracked.Length; index += 2)
				{
					var status = tracked[index][0];
					var nestedPath = tracked[index + 1];
					var workspacePath = $"{gitlink.Path.TrimEnd('/')}/{nestedPath}";
					if (status == 'A')
					{
						await GitAsync(store, workspaceRoot, ["update-index", "--force-remove", "--", workspacePath], cancellationToken, indexFile: temporaryIndex);
						continue;
					}

					var baseline = (await GitAsync(store, workspaceRoot, ["ls-tree", "-z", gitlink.Sha, "--", nestedPath], cancellationToken)).Stdout;
					var entry = baseline.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
					if (entry is null) continue;
					var tab = entry.IndexOf('\t');
					var metadata = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
					await GitAsync(store, workspaceRoot, ["update-index", "--add", "--cacheinfo", $"{metadata[0]},{metadata[2]},{workspacePath}"], cancellationToken, indexFile: temporaryIndex);
				}

				var untracked = (await GitAsync(store, nestedRoot, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken, useGitDir: false)).Stdout
					.Split('\0', StringSplitOptions.RemoveEmptyEntries);
				foreach (var nestedPath in untracked)
				{
					var workspacePath = $"{gitlink.Path.TrimEnd('/')}/{nestedPath}";
					await GitAsync(store, workspaceRoot, ["update-index", "--force-remove", "--", workspacePath], cancellationToken, indexFile: temporaryIndex);
				}
			}

			return (await GitAsync(store, workspaceRoot, ["write-tree"], cancellationToken, indexFile: temporaryIndex)).Stdout.Trim();
		}
		finally
		{
			try { File.Delete(temporaryIndex); } catch (IOException) { }
			try { File.Delete(temporaryIndex + ".lock"); } catch (IOException) { }
		}
	}

	private sealed record Gitlink(string Sha, string Path);

	private static Gitlink[] Gitlinks(string stagedOrTree) =>
		[.. stagedOrTree.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.Where(entry => entry.StartsWith("160000 ", StringComparison.Ordinal))
			.Select(entry =>
			{
				var tab = entry.IndexOf('\t');
				var metadata = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
				return new Gitlink(metadata.Length == 3 ? metadata[2] : metadata[1], entry[(tab + 1)..]);
			})];

	/// <summary>
	/// Write the tree and chain it onto the snapshot ref. The ref is what makes every tree reachable,
	/// so a stray <c>git gc</c> inside the store cannot prune the state an undo depends on.
	/// </summary>
	private static async Task<string?> SealAsync(string store, string workspaceRoot, CancellationToken cancellationToken)
	{
		var tree = (await GitAsync(store, workspaceRoot, ["write-tree"], cancellationToken)).Stdout.Trim();
		if (!IsHash(tree)) return null;

		var parent = (await GitAsync(
			store, workspaceRoot, ["rev-parse", "--verify", "--quiet", SnapshotRef], cancellationToken, check: false))
			.Stdout.Trim();
		if (parent.Length > 0)
		{
			// An unchanged worktree writes the same tree, so nothing new is recorded for it.
			var parentTree = (await GitAsync(
				store, workspaceRoot, ["rev-parse", "--verify", "--quiet", parent + "^{tree}"], cancellationToken, check: false))
				.Stdout.Trim();
			if (string.Equals(parentTree, tree, StringComparison.OrdinalIgnoreCase)) return tree;
		}

		string[] commit = parent.Length > 0
			? ["commit-tree", tree, "-p", parent, "-m", "suharness snapshot"]
			: ["commit-tree", tree, "-m", "suharness snapshot"];
		var created = (await GitAsync(store, workspaceRoot, commit, cancellationToken)).Stdout.Trim();
		if (created.Length > 0) await GitAsync(store, workspaceRoot, ["update-ref", SnapshotRef, created], cancellationToken);
		return tree;
	}

	/// <param name="Sha">The blob on the target side, which is the content a restore writes back.</param>
	/// <param name="Mode">The target side's file mode, so an executable bit survives the round trip.</param>
	private sealed record RawChange(string Path, SnapshotChangeKind Kind, string Sha, string Mode, string? OriginalPath = null);

	private static SnapshotChange ToChange(RawChange change) =>
		new(change.Path, change.Kind, change.OriginalPath);

	/// <summary>
	/// The changes that turn <paramref name="target"/> into <paramref name="current"/>, which is what a
	/// restore has to undo. <c>Added</c> therefore means present now and absent in the target.
	/// </summary>
	private static async Task<IReadOnlyList<RawChange>> DiffAsync(
		string store,
		string workspaceRoot,
		string target,
		string current,
		CancellationToken cancellationToken)
	{
		if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase)) return [];
		var result = await GitAsync(
			store,
			workspaceRoot,
			["diff-tree", "-r", "-z", "--raw", "--find-renames", target, current],
			cancellationToken);

		// Raw -z records read ":<srcmode> <dstmode> <srcsha> <dstsha> <status>\0<path>\0", where the
		// source side is the target tree — the side a restore is putting back.
		var fields = result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		var changes = new List<RawChange>();
		for (var index = 0; index + 1 < fields.Length;)
		{
			var meta = fields[index];
			if (meta.Length == 0 || meta[0] != ':') break;
			var parts = meta[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length < 5) break;
			var status = parts[4][0];
			var renamed = status is 'R' or 'C';
			if (renamed && index + 2 >= fields.Length) break;
			var originalPath = renamed ? fields[index + 1] : null;
			var path = renamed ? fields[index + 2] : fields[index + 1];
			changes.Add(new RawChange(
				path,
				status switch
				{
					'A' => SnapshotChangeKind.Added,
					'D' => SnapshotChangeKind.Deleted,
					'R' => SnapshotChangeKind.Renamed,
					_ => SnapshotChangeKind.Modified,
				},
				parts[2],
				parts[0],
				originalPath));
			index += renamed ? 3 : 2;
		}
		return changes;
	}

	private static bool PathMatches(RawChange change, string requested) =>
		string.Equals(change.Path, requested, StringComparison.OrdinalIgnoreCase)
		|| string.Equals(change.OriginalPath, requested, StringComparison.OrdinalIgnoreCase);

	private static string NormalizeRelativePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
			throw new ArgumentException("A workspace-relative path is required.", nameof(path));
		var normalized = path.Replace('\\', '/').Trim('/');
		if (normalized.Length == 0 || normalized.Split('/').Any(part => part is "" or "." or ".."))
			throw new ArgumentException("The path must stay inside the workspace.", nameof(path));
		return normalized;
	}

	private static async Task<IReadOnlyList<string>> RestoreChangeAsync(
		string store,
		string workspaceRoot,
		RawChange change,
		CancellationToken cancellationToken)
	{
		var emptied = new List<string>();
		var full = Path.Combine(workspaceRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));
		if (change.Kind is SnapshotChangeKind.Added or SnapshotChangeKind.Renamed)
		{
			if (File.Exists(full))
			{
				File.SetAttributes(full, FileAttributes.Normal);
				File.Delete(full);
			}
			if (Path.GetDirectoryName(full) is { Length: > 0 } parent) emptied.Add(parent);
			if (change.Kind == SnapshotChangeKind.Added) return emptied;
		}

		var targetPath = change.OriginalPath ?? change.Path;
		var target = Path.Combine(workspaceRoot, targetPath.Replace('/', Path.DirectorySeparatorChar));
		if (Path.GetDirectoryName(target) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
		await WriteBlobAsync(store, workspaceRoot, change, target, cancellationToken);
		return emptied;
	}

	/// <summary>
	/// Write one file back from its blob. The content lands in a temporary file first, so a restore
	/// interrupted mid-write leaves the original in place rather than a half-written replacement.
	/// </summary>
	private static async Task WriteBlobAsync(
		string store,
		string workspaceRoot,
		RawChange change,
		string destination,
		CancellationToken cancellationToken)
	{
		if (change.Sha.Length == 0 || change.Sha.TrimEnd('0').Length == 0 || change.Sha == NullSha) return;

		var temporary = destination + ".suharness-restore";
		try
		{
			await GitToFileAsync(store, workspaceRoot, ["cat-file", "blob", change.Sha], temporary, cancellationToken);
			if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
			File.Move(temporary, destination, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}

		// The executable bit is part of the state being restored everywhere it means anything.
		if (!OperatingSystem.IsWindows() && change.Mode.EndsWith("755", StringComparison.Ordinal))
		{
			File.SetUnixFileMode(
				destination,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
					| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
					| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		}
	}

	/// <summary>
	/// Rewrite the store's exclude file and report the oversized paths it now holds. Only untracked
	/// files are measured: they are the ones a capture would newly copy into the object database.
	/// </summary>
	private async Task<IReadOnlyList<string>> WriteExcludesAsync(
		string store,
		string workspaceRoot,
		CancellationToken cancellationToken)
	{
		var excludePath = Path.Combine(store, "info", "exclude");
		Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

		// Written in two passes. The scan below asks git which files are untracked, and git answers
		// that question through this very file — so leaving the last capture's size exclusions in
		// place would hide exactly the files that need measuring again. They would then look like
		// ordinary untracked files to the staging step, enter the snapshot, and a later restore would
		// delete a file the snapshot before it had never actually held.
		await File.WriteAllTextAsync(excludePath, RenderExcludes(_options.EffectiveExclude, []), cancellationToken);

		var oversized = new List<string>();
		if (_options.MaxFileBytes > 0)
		{
			var untracked = await GitAsync(
				store,
				workspaceRoot,
				["ls-files", "-o", "-z", "--exclude-standard"],
				cancellationToken,
				check: false);
			foreach (var relative in untracked.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
			{
				var full = Path.Combine(workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
				try
				{
					var info = new FileInfo(full);
					if (info.Exists && info.Length > _options.MaxFileBytes) oversized.Add(relative);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					// Unreadable now means uncapturable now; the next capture measures it again.
					oversized.Add(relative);
				}
			}
		}

		if (oversized.Count > 0)
			await File.WriteAllTextAsync(excludePath, RenderExcludes(_options.EffectiveExclude, oversized), cancellationToken);
		return oversized;
	}

	private static string RenderExcludes(IReadOnlyList<string> patterns, IReadOnlyList<string> oversized)
	{
		var builder = new StringBuilder();
		builder.Append("# Written by SuHarness on every capture. Edits are lost.\n");
		builder.Append(".git/\n");
		foreach (var pattern in patterns) builder.Append(pattern).Append('\n');
		// Anchored and quoted, so an oversized path matches itself and nothing else.
		foreach (var path in oversized) builder.Append('/').Append(EscapeGitignore(path)).Append('\n');
		return builder.ToString();
	}

	/// <summary>Quote the characters gitignore treats as syntax, so a path matches only itself.</summary>
	internal static string EscapeGitignore(string path)
	{
		var builder = new StringBuilder(path.Length + 8);
		foreach (var character in path)
		{
			if (character is '*' or '?' or '[' or ']' or '\\' or '!' or '#' or ' ') builder.Append('\\');
			builder.Append(character);
		}
		return builder.ToString();
	}

	/// <summary>
	/// Create the object database once and pin the settings a byte-exact round trip depends on. Line
	/// endings and clean/smudge filters are disabled outright: a snapshot that normalises what it
	/// stores restores something the user never wrote.
	/// </summary>
	private async Task EnsureStoreAsync(string store, string workspaceRoot, CancellationToken cancellationToken)
	{
		if (_initialized.ContainsKey(store) && Directory.Exists(store)) return;

		if (!File.Exists(Path.Combine(store, "HEAD")))
		{
			Directory.CreateDirectory(store);
			await GitAsync(store, workspaceRoot, ["init", "--bare", "--quiet", store], cancellationToken, useGitDir: false);
		}

		string[][] settings =
		[
			["core.bare", "false"],
			// The store round-trips bytes, not text: no CRLF translation, no filters, no attributes.
			["core.autocrlf", "false"],
			["core.safecrlf", "false"],
			["core.longpaths", "true"],
			// Nothing prunes a snapshot behind the harness's back.
			["gc.auto", "0"],
			["gc.autoDetach", "false"],
		];
		foreach (var setting in settings)
			await GitAsync(store, workspaceRoot, ["config", setting[0], setting[1]], cancellationToken);

		// info/attributes outranks any .gitattributes in the workspace, which is the only way to be
		// sure a repository that normalises its own text does not normalise our snapshot too.
		var infoDirectory = Path.Combine(store, "info");
		Directory.CreateDirectory(infoDirectory);
		await File.WriteAllTextAsync(
			Path.Combine(infoDirectory, "attributes"),
			"# Written by SuHarness. Snapshots store bytes exactly as they are on disk.\n* -text -filter -ident -diff -merge\n",
			cancellationToken);

		_initialized[store] = true;
	}

	/// <summary>Remove directories a restore emptied, so an undone turn leaves no hollow tree behind.</summary>
	private static void PruneEmptyDirectories(string workspaceRoot, IEnumerable<string> directories)
	{
		var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
		foreach (var directory in directories)
		{
			var current = directory;
			while (current.Length > root.Length
				&& current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
				&& Directory.Exists(current))
			{
				try
				{
					if (Directory.EnumerateFileSystemEntries(current).Any()) break;
					Directory.Delete(current);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					break;
				}
				current = Path.GetDirectoryName(current) ?? root;
			}
		}
	}

	private static bool IsHash(string? value) => value is { Length: >= 40 } && value.All(Uri.IsHexDigit);

	private sealed record GitResult(int ExitCode, string Stdout, string Stderr);

	/// <summary>
	/// Run git and stream its stdout to a file as raw bytes. Blob content is not text and must never
	/// be decoded on the way through: a snapshot that round-trips a binary file through a string is
	/// not a snapshot of that file.
	/// </summary>
	private static async Task GitToFileAsync(
		string store,
		string workspaceRoot,
		string[] arguments,
		string destination,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workspaceRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		startInfo.ArgumentList.Add("--git-dir");
		startInfo.ArgumentList.Add(store);
		startInfo.ArgumentList.Add("--work-tree");
		startInfo.ArgumentList.Add(workspaceRoot);
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		startInfo.Environment.Remove("GIT_DIR");
		startInfo.Environment.Remove("GIT_WORK_TREE");
		startInfo.Environment.Remove("GIT_INDEX_FILE");
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

		using var process = new Process { StartInfo = startInfo };
		process.Start();
		var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
		await using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
		{
			await process.StandardOutput.BaseStream.CopyToAsync(file, cancellationToken);
		}

		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (SystemException) { }
			throw;
		}

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"git {arguments[0]} failed with exit code {process.ExitCode}: {(await stderrTask).Trim()}");
		}
	}

	private static async Task<GitResult> GitAsync(
		string store,
		string workspaceRoot,
		string[] arguments,
		CancellationToken cancellationToken,
		string? stdin = null,
		bool check = true,
		bool useGitDir = true,
		string? indexFile = null)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workspaceRoot,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		if (useGitDir)
		{
			startInfo.ArgumentList.Add("--git-dir");
			startInfo.ArgumentList.Add(store);
			startInfo.ArgumentList.Add("--work-tree");
			startInfo.ArgumentList.Add(workspaceRoot);
		}
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

		// The store is addressed explicitly, so no ambient Git state may redirect it, and nothing here
		// ever reaches the network or a terminal.
		startInfo.Environment.Remove("GIT_DIR");
		startInfo.Environment.Remove("GIT_WORK_TREE");
		if (indexFile is null) startInfo.Environment.Remove("GIT_INDEX_FILE");
		else startInfo.Environment["GIT_INDEX_FILE"] = indexFile;
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		startInfo.Environment["GIT_AUTHOR_NAME"] = "SuHarness";
		startInfo.Environment["GIT_AUTHOR_EMAIL"] = "snapshots@suharness.invalid";
		startInfo.Environment["GIT_COMMITTER_NAME"] = "SuHarness";
		startInfo.Environment["GIT_COMMITTER_EMAIL"] = "snapshots@suharness.invalid";

		using var process = new Process { StartInfo = startInfo };
		try
		{
			process.Start();
		}
		catch (SystemException exception)
		{
			throw new InvalidOperationException(
				"git was not found on this system; install Git to use workspace snapshots.", exception);
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
		if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
		process.StandardInput.Close();

		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (SystemException) { }
			throw;
		}

		var result = new GitResult(process.ExitCode, await stdoutTask, await stderrTask);
		if (check && result.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"git {arguments[0]} failed with exit code {result.ExitCode}: {result.Stderr.Trim()}");
		}
		return result;
	}
}

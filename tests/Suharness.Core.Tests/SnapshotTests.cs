using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.Diagnostics;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the workspace snapshot store. These run against a real <c>git</c> rather than a fake,
/// because every property worth having here — that the user's own repository is untouched, that
/// bytes round-trip unchanged, that an ignored file is never captured — is a property of git's
/// behaviour and not of an interface SuHarness controls.
/// </summary>
public sealed class SnapshotTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-snapshot-").FullName;

	public void Dispose()
	{
		// A store holds read-only objects, so a plain recursive delete can fail on Windows.
		try { ForceDelete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static void ForceDelete(string path)
	{
		if (!Directory.Exists(path)) return;
		foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			try { File.SetAttributes(file, FileAttributes.Normal); } catch (SystemException) { }
		}
		Directory.Delete(path, recursive: true);
	}

	private string Workspace([System.Runtime.CompilerServices.CallerMemberName] string name = "w")
	{
		var path = Path.Combine(_root, "ws-" + name);
		Directory.CreateDirectory(path);
		return path;
	}

	private GitSnapshotService Service(SnapshotOptions? options = null) =>
		new(Path.Combine(_root, "snapshots"), options);

	private static void Write(string root, string relative, string content)
	{
		var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	private static string Read(string root, string relative) =>
		File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

	private static bool Exists(string root, string relative) =>
		File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

	private static string Git(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo)!;
		var stdout = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return stdout;
	}

	// ----- capture -------------------------------------------------------------------------------

	[Fact]
	public async Task AnUnchangedWorkspaceCapturesToTheSameHash()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		var service = Service();

		var first = await service.CaptureAsync(workspace);
		var second = await service.CaptureAsync(workspace);

		Assert.NotNull(first);
		Assert.NotNull(second);
		// The handle is the content address of the worktree, so an idle turn costs no new state.
		Assert.Equal(first!.TreeHash, second!.TreeHash);
	}

	[Fact]
	public async Task AChangedFileChangesTheHash()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		var service = Service();

		var before = await service.CaptureAsync(workspace);
		Write(workspace, "a.txt", "two");
		var after = await service.CaptureAsync(workspace);

		Assert.NotEqual(before!.TreeHash, after!.TreeHash);
	}

	[Fact]
	public async Task ANestedRepositoryIsCapturedAsFilesInsteadOfAGitlink()
	{
		var workspace = Workspace();
		var nested = Path.Combine(workspace, "project");
		Directory.CreateDirectory(nested);
		Git(nested, "init", "--quiet");
		Git(nested, "config", "user.email", "test@example.com");
		Git(nested, "config", "user.name", "Test");
		Write(nested, ".gitignore", "*.tmp\n");
		Write(nested, "tracked.txt", "before");
		Git(nested, "add", ".gitignore", "tracked.txt");
		Git(nested, "commit", "-m", "initial", "--quiet");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(nested, "tracked.txt", "after");
		Write(nested, "new.txt", "new");
		Write(nested, "ignored.tmp", "ignored");
		var preview = await service.PreviewAsync(workspace, snapshot!.TreeHash);

		Assert.Contains(preview, change => change.Path == "project/tracked.txt" && change.Kind == SnapshotChangeKind.Modified);
		Assert.Contains(preview, change => change.Path == "project/new.txt" && change.Kind == SnapshotChangeKind.Added);
		Assert.DoesNotContain(preview, change => change.Path.Contains(".git", StringComparison.Ordinal) || change.Path.EndsWith(".tmp", StringComparison.Ordinal));
		var diff = await service.GetFileDiffAsync(workspace, snapshot.TreeHash, "project/tracked.txt");
		Assert.Contains("-before", diff!.Patch);
		Assert.Contains("+after", diff.Patch);

		await service.RestoreAsync(workspace, snapshot.TreeHash);
		Assert.Equal("before", Read(nested, "tracked.txt"));
		Assert.False(Exists(nested, "new.txt"));
		Assert.Equal("ignored", Read(nested, "ignored.tmp"));
		Assert.True(File.Exists(Path.Combine(nested, ".git", "HEAD")));
	}

	[Fact]
	public async Task ALinkedWorktreePreviewIncludesChangesButNotIgnoredFiles()
	{
		var repository = Workspace();
		Git(repository, "init", "--quiet");
		Git(repository, "config", "user.email", "test@example.com");
		Git(repository, "config", "user.name", "Test");
		Write(repository, ".gitignore", "*.tmp\n");
		Write(repository, "tracked.txt", "before");
		Git(repository, "add", ".gitignore", "tracked.txt");
		Git(repository, "commit", "-m", "initial", "--quiet");
		var worktree = Path.Combine(_root, "linked-worktree");
		Git(repository, "worktree", "add", "--detach", worktree, "HEAD");
		var service = Service();
		var baseline = await service.CaptureAsync(worktree);

		Write(worktree, "tracked.txt", "after");
		Write(worktree, "added.txt", "new");
		Write(worktree, "ignored.tmp", "ignored");
		var preview = await service.PreviewAsync(worktree, baseline!.TreeHash);

		Assert.Contains(preview, change => change.Path == "tracked.txt" && change.Kind == SnapshotChangeKind.Modified);
		Assert.Contains(preview, change => change.Path == "added.txt" && change.Kind == SnapshotChangeKind.Added);
		Assert.DoesNotContain(preview, change => change.Path == "ignored.tmp" || change.Path.StartsWith(".git", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ADisabledServiceCapturesNothing()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		var service = Service(new SnapshotOptions(Enabled: false));

		Assert.False(service.Enabled);
		Assert.Null(await service.CaptureAsync(workspace));
	}

	[Fact]
	public async Task AMissingWorkspaceCapturesNothing()
	{
		var service = Service();

		Assert.Null(await service.CaptureAsync(string.Empty));
		Assert.Null(await service.CaptureAsync(Path.Combine(_root, "does-not-exist")));
	}

	// ----- restore -------------------------------------------------------------------------------

	[Fact]
	public async Task RestoreRewritesAModifiedFile()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "original");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "a.txt", "clobbered");
		var changes = await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal("original", Read(workspace, "a.txt"));
		Assert.Equal(SnapshotChangeKind.Modified, Assert.Single(changes).Kind);
	}

	[Fact]
	public async Task RestoreDeletesAFileCreatedAfterTheSnapshot()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "kept");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "new.txt", "written by the agent");
		var changes = await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.False(Exists(workspace, "new.txt"));
		Assert.True(Exists(workspace, "a.txt"));
		Assert.Equal(SnapshotChangeKind.Added, Assert.Single(changes).Kind);
	}

	[Fact]
	public async Task RestoreRecreatesADeletedFile()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "do not lose me");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		File.Delete(Path.Combine(workspace, "a.txt"));
		var changes = await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal("do not lose me", Read(workspace, "a.txt"));
		Assert.Equal(SnapshotChangeKind.Deleted, Assert.Single(changes).Kind);
	}

	/// <summary>
	/// A restore that rewrites every file, rather than only the ones that differ, resets modification
	/// times across the workspace and makes every open editor prompt to reload. Touching only the
	/// diff is what keeps an undo a local event.
	/// </summary>
	[Fact]
	public async Task RestoreLeavesFilesThatDidNotChangeUntouched()
	{
		var workspace = Workspace();
		Write(workspace, "changed.txt", "before");
		Write(workspace, "untouched.txt", "stable");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		var untouched = Path.Combine(workspace, "untouched.txt");
		var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(untouched, stamp);
		Write(workspace, "changed.txt", "after");

		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal(stamp, File.GetLastWriteTimeUtc(untouched));
		Assert.Equal("before", Read(workspace, "changed.txt"));
	}

	[Fact]
	public async Task RestoreRemovesADirectoryItEmptied()
	{
		var workspace = Workspace();
		Write(workspace, "keep.txt", "x");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "generated/nested/output.txt", "scaffolding");
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.False(Directory.Exists(Path.Combine(workspace, "generated")));
	}

	[Fact]
	public async Task RestoreIsANoOpWhenNothingDiffers()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "same");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Assert.Empty(await service.RestoreAsync(workspace, snapshot!.TreeHash));
		Assert.Equal("same", Read(workspace, "a.txt"));
	}

	[Fact]
	public async Task RestoreRejectsSomethingThatIsNotASnapshot()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "x");
		var service = Service();

		await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(workspace, "not-a-hash"));
	}

	/// <summary>A revert has to be reversible, which means the round trip has to land exactly back.</summary>
	[Fact]
	public async Task AnUndoneChangeCanBeRedoneExactly()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "v1");
		var service = Service();
		var first = await service.CaptureAsync(workspace);

		Write(workspace, "a.txt", "v2");
		Write(workspace, "added.txt", "new");
		var second = await service.CaptureAsync(workspace);

		await service.RestoreAsync(workspace, first!.TreeHash);
		Assert.Equal("v1", Read(workspace, "a.txt"));
		Assert.False(Exists(workspace, "added.txt"));

		await service.RestoreAsync(workspace, second!.TreeHash);
		Assert.Equal("v2", Read(workspace, "a.txt"));
		Assert.Equal("new", Read(workspace, "added.txt"));
	}

	[Fact]
	public async Task PreviewReportsTheChangesWithoutMakingThem()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "original");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "a.txt", "changed");
		Write(workspace, "b.txt", "added");

		var preview = await service.PreviewAsync(workspace, snapshot!.TreeHash);

		Assert.Equal(2, preview.Count);
		Assert.Equal("changed", Read(workspace, "a.txt"));
		Assert.True(Exists(workspace, "b.txt"));
	}

	[Fact]
	public async Task FileRestorePreservesTheSessionBaselineAndOtherFiles()
	{
		var workspace = Workspace();
		Write(workspace, "shared.txt", "user edit before session\n");
		Write(workspace, "other.txt", "before\n");
		var service = Service();
		var baseline = await service.CaptureAsync(workspace);

		Write(workspace, "shared.txt", "user edit before session\nagent edit\n");
		Write(workspace, "other.txt", "agent also edited this\n");
		var restored = await service.RestoreFileAsync(workspace, baseline!.TreeHash, "shared.txt");

		Assert.Equal(SnapshotChangeKind.Modified, restored!.Kind);
		Assert.Equal("user edit before session\n", Read(workspace, "shared.txt"));
		Assert.Equal("agent also edited this\n", Read(workspace, "other.txt"));
	}

	[Fact]
	public async Task FileDiffShowsAddedContentAndRejectsTraversal()
	{
		var workspace = Workspace();
		Write(workspace, "kept.txt", "stable\n");
		var service = Service();
		var baseline = await service.CaptureAsync(workspace);
		Write(workspace, "created.txt", "new line\n");

		var diff = await service.GetFileDiffAsync(workspace, baseline!.TreeHash, "created.txt");

		Assert.Equal(SnapshotChangeKind.Added, diff!.Change.Kind);
		Assert.Contains("+new line", diff.Patch);
		await Assert.ThrowsAsync<ArgumentException>(() =>
			service.RestoreFileAsync(workspace, baseline.TreeHash, "../outside.txt"));
	}

	[Fact]
	public async Task FileRestoreReversesARename()
	{
		var workspace = Workspace();
		Write(workspace, "old.txt", "content\n");
		var service = Service();
		var baseline = await service.CaptureAsync(workspace);
		File.Move(Path.Combine(workspace, "old.txt"), Path.Combine(workspace, "new.txt"));

		var changes = await service.PreviewAsync(workspace, baseline!.TreeHash);
		var renamed = Assert.Single(changes);
		Assert.Equal(SnapshotChangeKind.Renamed, renamed.Kind);
		Assert.Equal("old.txt", renamed.OriginalPath);

		await service.RestoreFileAsync(workspace, baseline.TreeHash, "new.txt");
		Assert.False(Exists(workspace, "new.txt"));
		Assert.Equal("content\n", Read(workspace, "old.txt"));
	}

	// ----- what is and is not captured -----------------------------------------------------------

	[Fact]
	public async Task IgnoredFilesAreNeverCapturedAndSurviveARestore()
	{
		var workspace = Workspace();
		Write(workspace, ".gitignore", "secrets.env\nbuild/\n");
		Write(workspace, "a.txt", "source");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		// Written after the snapshot: a restore must neither delete nor rewrite an ignored path.
		Write(workspace, "secrets.env", "TOKEN=abc");
		Write(workspace, "build/artifact.bin", "compiled");
		Write(workspace, "a.txt", "edited");

		var changes = await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal("TOKEN=abc", Read(workspace, "secrets.env"));
		Assert.Equal("compiled", Read(workspace, "build/artifact.bin"));
		Assert.Equal("source", Read(workspace, "a.txt"));
		Assert.Equal("a.txt", Assert.Single(changes).Path);
	}

	[Fact]
	public async Task ANestedGitignoreIsHonoured()
	{
		var workspace = Workspace();
		Write(workspace, "src/.gitignore", "*.tmp\n");
		Write(workspace, "src/a.txt", "source");
		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "src/scratch.tmp", "scratch");
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.True(Exists(workspace, "src/scratch.tmp"));
	}

	[Fact]
	public async Task ConfiguredExcludesAreHonoured()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "source");
		var service = Service(new SnapshotOptions(Exclude: ["node_modules/", "*.log"]));
		var snapshot = await service.CaptureAsync(workspace);

		Write(workspace, "node_modules/pkg/index.js", "module");
		Write(workspace, "run.log", "noise");
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.True(Exists(workspace, "node_modules/pkg/index.js"));
		Assert.True(Exists(workspace, "run.log"));
	}

	[Fact]
	public async Task OversizedFilesAreReportedAndLeftAlone()
	{
		var workspace = Workspace();
		Write(workspace, "small.txt", "small");
		var service = Service(new SnapshotOptions(MaxFileBytes: 64));

		File.WriteAllBytes(Path.Combine(workspace, "big.bin"), new byte[512]);
		var snapshot = await service.CaptureAsync(workspace);

		Assert.Contains("big.bin", snapshot!.Skipped);

		// Uncaptured means untouched: a restore must not delete a file it never held a copy of.
		Write(workspace, "small.txt", "edited");
		await service.RestoreAsync(workspace, snapshot.TreeHash);

		Assert.True(Exists(workspace, "big.bin"));
		Assert.Equal("small", Read(workspace, "small.txt"));
	}

	[Fact]
	public void OversizedPathsWithGlobCharactersAreEscapedIntoLiterals()
	{
		Assert.Equal(@"a\[1\]/b\*c\?d", GitSnapshotService.EscapeGitignore("a[1]/b*c?d"));
		Assert.Equal(@"\#notacomment", GitSnapshotService.EscapeGitignore("#notacomment"));
		Assert.Equal("plain/path.txt", GitSnapshotService.EscapeGitignore("plain/path.txt"));
	}

	// ----- fidelity ------------------------------------------------------------------------------

	/// <summary>
	/// Git normalises line endings and runs clean filters by default, and on Windows the system
	/// configuration turns that on. A store that normalises what it holds restores a file the user
	/// never wrote, so the round trip is asserted on bytes rather than on text.
	/// </summary>
	[Fact]
	public async Task BytesRoundTripExactlyThroughTheStore()
	{
		var workspace = Workspace();
		var crlf = Encoding.UTF8.GetBytes("line one\r\nline two\r\n");
		var binary = new byte[] { 0x00, 0xFF, 0x1B, 0x0A, 0x0D, 0x0A, 0x7F, 0x00 };
		File.WriteAllBytes(Path.Combine(workspace, "crlf.txt"), crlf);
		File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), binary);

		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);

		File.WriteAllText(Path.Combine(workspace, "crlf.txt"), "clobbered");
		File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [1, 2, 3]);
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal(crlf, File.ReadAllBytes(Path.Combine(workspace, "crlf.txt")));
		Assert.Equal(binary, File.ReadAllBytes(Path.Combine(workspace, "blob.bin")));
	}

	/// <summary>
	/// A workspace carrying <c>* text=auto eol=lf</c> would have its CRLF files rewritten on the way
	/// through git. The store's own attributes file outranks the workspace's, which is what stops it.
	/// </summary>
	[Fact]
	public async Task AWorkspaceGitattributesCannotNormaliseTheSnapshot()
	{
		var workspace = Workspace();
		Write(workspace, ".gitattributes", "* text=auto eol=lf\n");
		var crlf = Encoding.UTF8.GetBytes("alpha\r\nbeta\r\n");
		File.WriteAllBytes(Path.Combine(workspace, "crlf.txt"), crlf);

		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);
		File.WriteAllText(Path.Combine(workspace, "crlf.txt"), "clobbered");
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal(crlf, File.ReadAllBytes(Path.Combine(workspace, "crlf.txt")));
	}

	// ----- isolation from the user's repository --------------------------------------------------

	/// <summary>
	/// The whole design rests on this: the snapshot store is the harness's, and the repository the
	/// user is working in never learns it exists.
	/// </summary>
	[Fact]
	public async Task TheWorkspaceOwnRepositoryIsNeverTouched()
	{
		var workspace = Workspace();
		Git(workspace, "init", "--quiet");
		Git(workspace, "config", "user.email", "test@test.invalid");
		Git(workspace, "config", "user.name", "Test");
		Write(workspace, "a.txt", "committed");
		Git(workspace, "add", "-A");
		Git(workspace, "commit", "-m", "initial", "--quiet");

		var headBefore = Git(workspace, "rev-parse", "HEAD").Trim();
		var refsBefore = Git(workspace, "for-each-ref");
		var statusBefore = Git(workspace, "status", "--porcelain");

		var service = Service();
		var snapshot = await service.CaptureAsync(workspace);
		Write(workspace, "a.txt", "edited by the agent");
		Write(workspace, "b.txt", "added by the agent");
		await service.CaptureAsync(workspace);
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal(headBefore, Git(workspace, "rev-parse", "HEAD").Trim());
		Assert.Equal(refsBefore, Git(workspace, "for-each-ref"));
		Assert.Equal(statusBefore, Git(workspace, "status", "--porcelain"));
		Assert.Equal("committed", Read(workspace, "a.txt"));
		Assert.False(Exists(workspace, "b.txt"));
	}

	[Fact]
	public async Task TheStoreLivesOutsideTheWorkspace()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "x");
		var service = Service();
		await service.CaptureAsync(workspace);

		var store = service.StoreFor(workspace);
		Assert.True(Directory.Exists(store));
		Assert.False(store.StartsWith(workspace, StringComparison.OrdinalIgnoreCase));
		// Nothing was written into the workspace itself.
		Assert.False(Directory.Exists(Path.Combine(workspace, ".git")));
		Assert.Equal(["a.txt"], Directory.GetFileSystemEntries(workspace).Select(entry => Path.GetFileName(entry)!).ToArray());
	}

	/// <summary>
	/// OpenCode requires the project to be a Git repository for undo to work at all. Owning the object
	/// database instead lifts that requirement, so the case is worth pinning down.
	/// </summary>
	[Fact]
	public async Task AWorkspaceThatIsNotAGitRepositoryIsStillSnapshotted()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "plain directory, no repository");
		var service = Service();

		var snapshot = await service.CaptureAsync(workspace);
		Write(workspace, "a.txt", "changed");
		await service.RestoreAsync(workspace, snapshot!.TreeHash);

		Assert.Equal("plain directory, no repository", Read(workspace, "a.txt"));
	}

	[Fact]
	public async Task TwoWorkspacesGetSeparateStores()
	{
		var first = Path.Combine(_root, "ws-one");
		var second = Path.Combine(_root, "ws-two");
		Directory.CreateDirectory(first);
		Directory.CreateDirectory(second);
		Write(first, "a.txt", "one");
		Write(second, "a.txt", "two");
		var service = Service();

		await service.CaptureAsync(first);
		await service.CaptureAsync(second);

		Assert.NotEqual(service.StoreFor(first), service.StoreFor(second));
		Assert.Equal(service.StoreFor(first), service.StoreFor(first + Path.DirectorySeparatorChar));
	}

	[Fact]
	public async Task ConcurrentCapturesOfOneWorkspaceDoNotCorruptTheIndex()
	{
		var workspace = Workspace();
		for (var index = 0; index < 20; index++) Write(workspace, $"file{index}.txt", $"content {index}");
		var service = Service();

		var captures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CaptureAsync(workspace)));

		Assert.All(captures, capture => Assert.NotNull(capture));
		Assert.Single(captures.Select(capture => capture!.TreeHash).Distinct());
	}

	// ----- indexing ------------------------------------------------------------------------------

	/// <summary>
	/// The first capture in a workspace reads and stores every file in it, which on a large tree is
	/// minutes of work where a later capture is milliseconds. A workspace small enough to index inside
	/// the capture deadline is therefore covered from its very first turn.
	/// </summary>
	[Fact]
	public async Task ASmallWorkspaceIsCoveredFromItsFirstCapture()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		var service = Service();

		Assert.False(service.IsReady(workspace));
		Assert.NotNull(await service.CaptureAsync(workspace));
		Assert.True(service.IsReady(workspace));
	}

	[Fact]
	public async Task IndexingIsDoneOnceAndSurvivesANewService()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		await Service().PrimeAsync(workspace);

		// A different instance over the same store must not pay the indexing cost again.
		var second = Service();
		Assert.True(second.IsReady(workspace));
		Assert.NotNull(await second.CaptureAsync(workspace));
	}

	[Fact]
	public async Task ConcurrentPrimingRunsOnce()
	{
		var workspace = Workspace();
		for (var index = 0; index < 40; index++) Write(workspace, $"file{index}.txt", $"content {index}");
		var service = Service();

		await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.PrimeAsync(workspace)));

		Assert.True(service.IsReady(workspace));
		Assert.NotNull(await service.CaptureAsync(workspace));
	}

	/// <summary>
	/// A workspace that cannot be indexed inside the deadline leaves the turn uncovered and keeps
	/// indexing behind it, rather than holding the turn up for as long as the indexing takes.
	/// </summary>
	[Fact]
	public async Task AWorkspaceStillBeingIndexedDeclinesRatherThanWaits()
	{
		var workspace = Workspace();
		for (var index = 0; index < 60; index++) Write(workspace, $"file{index}.txt", new string('x', 2048));
		// One millisecond is not enough to spawn git, so the wait always expires before indexing lands.
		var service = Service(new SnapshotOptions(CaptureTimeoutMs: 1));

		Assert.Null(await service.CaptureAsync(workspace));
	}

	[Fact]
	public async Task ADisabledServiceIsNeverIndexed()
	{
		var workspace = Workspace();
		Write(workspace, "a.txt", "one");
		var service = Service(new SnapshotOptions(Enabled: false));

		await service.PrimeAsync(workspace);

		Assert.False(Directory.Exists(service.StoreFor(workspace)));
	}

	// ----- configuration -------------------------------------------------------------------------

	[Fact]
	public void MissingConfigurationLeavesSnapshotsOn()
	{
		var options = GitSnapshotService.LoadOptions(Path.Combine(_root, "no-configuration"));

		Assert.True(options.Enabled);
		Assert.Equal(2L * 1024 * 1024, options.MaxFileBytes);
		Assert.Equal(["node_modules/"], options.EffectiveExclude);
	}

	[Fact]
	public void ConfigurationIsRead()
	{
		var configuration = Path.Combine(_root, "configuration");
		Directory.CreateDirectory(configuration);
		File.WriteAllText(
			Path.Combine(configuration, "snapshots.json"),
			"""{ "enabled": false, "maxFileBytes": 4096, "captureTimeoutMs": 5000, "exclude": ["dist/"] }""");

		var options = GitSnapshotService.LoadOptions(configuration);

		Assert.False(options.Enabled);
		Assert.Equal(4096, options.MaxFileBytes);
		Assert.Equal(5000, options.CaptureTimeoutMs);
		Assert.Equal(["dist/"], options.EffectiveExclude);
	}

	/// <summary>
	/// A safety feature must not be what a typo switches off, so unreadable configuration falls back
	/// to the defaults rather than to "disabled".
	/// </summary>
	[Fact]
	public void UnreadableConfigurationFallsBackToTheDefaults()
	{
		var configuration = Path.Combine(_root, "broken-configuration");
		Directory.CreateDirectory(configuration);
		File.WriteAllText(Path.Combine(configuration, "snapshots.json"), "{ not json");

		var options = GitSnapshotService.LoadOptions(configuration);

		Assert.True(options.Enabled);
	}

	[Fact]
	public void AnAbsurdCaptureTimeoutIsClamped()
	{
		var configuration = Path.Combine(_root, "clamped-configuration");
		Directory.CreateDirectory(configuration);
		File.WriteAllText(Path.Combine(configuration, "snapshots.json"), """{ "captureTimeoutMs": 1 }""");

		Assert.Equal(1_000, GitSnapshotService.LoadOptions(configuration).CaptureTimeoutMs);
	}
}

using Suharness.Abstractions;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the retained workspace table and the declaration index built on it.
/// </summary>
/// <remarks>
/// The central obligation is equivalence: a search served from the index must return exactly what
/// the same search returns when it walks the tree, because the index is meant to change what a
/// search costs and nothing about what it answers. Most tests here therefore assert the two paths
/// against each other rather than against a hand-written expectation.
/// </remarks>
public sealed class WorkspaceIndexTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-index-").FullName;
	private readonly string _store = Directory.CreateTempSubdirectory("suharness-index-store-").FullName;

	public void Dispose()
	{
		foreach (var directory in new[] { _root, _store })
		{
			try { Directory.Delete(directory, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}
	}

	private string Write(string relative, string content)
	{
		var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
		return full;
	}

	private WorkspaceScope Scope() => new(_root);

	/// <summary>A tree with every case the two search policies disagree about.</summary>
	private void WriteMixedTree()
	{
		Write(".gitignore", "ignored/\n*.generated.cs\n");
		Write("src/alpha.cs", "class Alpha { }\n");
		Write("src/deep/beta.cs", "class Beta { }\n");
		Write("src/notes.txt", "beta lives here\n");
		Write("root.cs", "class Root { }\n");
		Write("ignored/skipped.cs", "class Skipped { }\n");
		Write("src/thing.generated.cs", "class Generated { }\n");
		Write(".hidden/secret.cs", "class Secret { }\n");
	}

	[Theory]
	[InlineData("*.cs")]
	[InlineData("**/*.cs")]
	[InlineData("src/**/*.cs")]
	[InlineData("*/*.cs")]
	[InlineData("**/*eta*")]
	public void IndexedGlobReturnsExactlyWhatTheWalkerReturns(string pattern)
	{
		WriteMixedTree();
		var scope = Scope();

		var walked = SearchTools.Glob(scope, pattern, null, CancellationToken.None);
		var indexed = SearchTools.Glob(scope, pattern, null, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Equal(walked, indexed);
	}

	[Fact]
	public void IndexedGlobStillReportsIgnoredAndHiddenFilesTheWayGlobAlwaysHas()
	{
		WriteMixedTree();

		var result = SearchTools.Glob(Scope(), "*.cs", null, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Contains("ignored/skipped.cs", result);
		Assert.Contains(".hidden/secret.cs", result);
		Assert.Contains("src/thing.generated.cs", result);
	}

	[Theory]
	[InlineData("class", null)]
	[InlineData("beta", null)]
	[InlineData("class", "*.cs")]
	public void IndexedGrepReturnsExactlyWhatTheWalkerReturns(string pattern, string? include)
	{
		WriteMixedTree();
		var scope = Scope();

		var walked = SearchTools.Grep(scope, pattern, null, include, CancellationToken.None);
		var indexed = SearchTools.Grep(scope, pattern, null, include, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Equal(walked, indexed);
	}

	/// <summary>
	/// A handful of files is not enough to hold the two paths to account: they can visit files in
	/// different orders and still agree by luck. This tree is wide enough that traversal order shows
	/// up in the grouping, and deep enough that it is not alphabetical by accident.
	/// </summary>
	[Fact]
	public void IndexedGrepAgreesWithTheWalkerAcrossAWideTree()
	{
		for (var index = 0; index < 60; index++)
		{
			Write($"zone{index % 7}/module{index}/source.cs", $"// needle {index}\n");
			Write($"zone{index % 7}/module{index}/notes.md", $"needle in markdown {index}\n");
		}
		var scope = Scope();

		var walked = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None);
		var indexed = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Equal(walked, indexed);
		Assert.Contains("Found 120 matches", walked);
	}

	/// <summary>
	/// Beyond the match cap the traversal order decides which matches survive, so the two paths have
	/// to agree on the order as well as on the set.
	/// </summary>
	[Fact]
	public void IndexedGrepTruncatesToTheSameMatchesAsTheWalker()
	{
		for (var index = 0; index < 400; index++) Write($"pack{index % 9}/file{index}.cs", "needle\nneedle\n");
		var scope = Scope();

		var walked = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None);
		var indexed = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Contains($"Found {SearchTools.GrepMaxMatches} of 800 matches", walked);
		Assert.Equal(walked, indexed);
	}

	[Fact]
	public void IndexedGrepKeepsGrepsExclusions()
	{
		WriteMixedTree();

		var result = SearchTools.Grep(Scope(), "class", null, null, CancellationToken.None, index: new WorkspaceIndex());

		Assert.Contains("src/alpha.cs", result);
		Assert.DoesNotContain("ignored/skipped.cs", result);
		Assert.DoesNotContain("thing.generated.cs", result);
		Assert.DoesNotContain("secret.cs", result);
	}

	[Fact]
	public void IndexedSearchHonoursASubdirectoryPath()
	{
		WriteMixedTree();
		var scope = Scope();

		var walked = SearchTools.Glob(scope, "*.cs", "src/deep", CancellationToken.None);
		var indexed = SearchTools.Glob(scope, "*.cs", "src/deep", CancellationToken.None, index: new WorkspaceIndex());

		Assert.Equal(walked, indexed);
		Assert.Contains("src/deep/beta.cs", indexed);
		Assert.DoesNotContain("src/alpha.cs", indexed);
	}

	/// <summary>
	/// The freshness window is the whole point of the index: a burst of searches inside one turn must
	/// not each pay for a traversal. Observing that from outside means observing that a change made
	/// behind the index's back is not picked up until the window lapses.
	/// </summary>
	[Fact]
	public void AValidatedTableIsServedWithoutTouchingTheFilesystemAgain()
	{
		Write("one.cs", "//");
		var index = new WorkspaceIndex(freshness: TimeSpan.FromMinutes(5));
		var scope = Scope();

		var first = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);
		Write("two.cs", "//");
		var second = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		Assert.Equal(first, second);
		Assert.DoesNotContain("two.cs", second);
	}

	[Fact]
	public void InvalidateForcesTheNextSearchToConfirmAgainstTheFilesystem()
	{
		Write("one.cs", "//");
		var index = new WorkspaceIndex(freshness: TimeSpan.FromMinutes(5));
		var scope = Scope();
		SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		Write("two.cs", "//");
		index.Invalidate(_root);
		var result = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		Assert.Contains("two.cs", result);
	}

	[Fact]
	public void RecordFoldsAKnownChangeInWithoutARevalidation()
	{
		Write("one.cs", "//");
		var index = new WorkspaceIndex(freshness: TimeSpan.FromMinutes(5));
		var scope = Scope();
		SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		var added = Write("two.cs", "//");
		index.Record(_root, added);
		var afterAdd = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		File.Delete(added);
		index.Record(_root, added);
		var afterDelete = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		Assert.Contains("two.cs", afterAdd);
		Assert.DoesNotContain("two.cs", afterDelete);
	}

	[Fact]
	public void ARevalidationPicksUpFilesAddedAndRemovedBehindTheIndex()
	{
		Write("keep.cs", "//");
		var removed = Write("gone.cs", "//");
		var index = new WorkspaceIndex(freshness: TimeSpan.Zero);
		var scope = Scope();
		Assert.Contains("gone.cs", SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index));

		File.Delete(removed);
		Write("nested/added.cs", "//");
		var result = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: index);

		Assert.DoesNotContain("gone.cs", result);
		Assert.Contains("nested/added.cs", result);
		Assert.Contains("keep.cs", result);
	}

	/// <summary>
	/// An edited <c>.gitignore</c> moves no directory stamp, so nothing else in the table would show
	/// that a file's ignore status flipped. The rule stamp is what catches it.
	/// </summary>
	[Fact]
	public void AnEditedGitIgnoreIsNoticedEvenThoughNoDirectoryChanged()
	{
		Write(".gitignore", "nothing\n");
		Write("secret.cs", "needle");
		var index = new WorkspaceIndex(freshness: TimeSpan.Zero);
		var scope = Scope();
		Assert.Contains("secret.cs", SearchTools.Grep(scope, "needle", null, null, CancellationToken.None, index: index));

		File.WriteAllText(Path.Combine(_root, ".gitignore"), "secret.cs\n");
		var result = SearchTools.Grep(scope, "needle", null, null, CancellationToken.None, index: index);

		Assert.Equal("No matches found", result);
	}

	[Fact]
	public async Task ATablePersistedByOneProcessIsReusedByTheNext()
	{
		WriteMixedTree();
		var scope = Scope();
		var first = new WorkspaceIndex(_store);
		var expected = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: first);

		var table = Path.Combine(IndexStore.DirectoryFor(_store, _root), "files.tsv");
		await WaitForFile(table);

		// A second instance shares nothing in memory with the first, so anything it answers correctly
		// before walking the tree came off disk.
		var second = new WorkspaceIndex(_store);
		var reused = SearchTools.Glob(scope, "*.cs", null, CancellationToken.None, index: second);

		Assert.True(File.Exists(table));
		Assert.Contains(_root, await File.ReadAllTextAsync(Path.Combine(IndexStore.DirectoryFor(_store, _root), "meta.tsv")));
		Assert.Equal(expected, reused);
	}

	[Fact]
	public void APersistedTableForADifferentWorkspaceIsNeverServed()
	{
		Write("one.cs", "//");
		var directory = IndexStore.DirectoryFor(_store, _root);
		Directory.CreateDirectory(directory);
		File.WriteAllLines(Path.Combine(directory, "meta.tsv"),
		[
			$"version\t{WorkspaceIndex.FormatVersion}",
			"root\tC:\\somewhere\\else",
			"ignoreStamp\t0",
		]);
		File.WriteAllLines(Path.Combine(directory, "files.tsv"), ["d\t0\t0\t0\t", "f\t3\t0\t0\tphantom.cs"]);

		var result = SearchTools.Glob(Scope(), "*.cs", null, CancellationToken.None, index: new WorkspaceIndex(_store));

		Assert.DoesNotContain("phantom.cs", result);
		Assert.Contains("one.cs", result);
	}

	[Fact]
	public void SearchingOutsideTheIndexedWorkspaceFallsBackToTheWalker()
	{
		Write("inside.cs", "//");
		var outside = Directory.CreateTempSubdirectory("suharness-outside-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(outside, "external.cs"), "//");
			var scope = new WorkspaceScope(_root, allowOutsideRoot: true);

			var result = SearchTools.Glob(scope, "*.cs", outside, CancellationToken.None, index: new WorkspaceIndex());

			Assert.Contains("external.cs", result);
			Assert.DoesNotContain("inside.cs", result);
		}
		finally { Directory.Delete(outside, recursive: true); }
	}

	[Fact]
	public void AnIndexThatCouldNotCoverTheTreeSaysSoInsteadOfLookingComplete()
	{
		// The walker only consults the clock every few hundred entries, so the tree has to be large
		// enough to reach that check before the deadline can stop it.
		for (var index = 0; index < 400; index++) Write($"pack{index}/file.cs", "//");

		var result = SearchTools.Glob(
			Scope(),
			"*.cs",
			null,
			CancellationToken.None,
			index: new WorkspaceIndex(buildTimeout: TimeSpan.Zero));

		Assert.Contains("The workspace index could not cover the whole tree", result);
	}

	private static async Task WaitForFile(string path)
	{
		for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++) await Task.Delay(20);
	}
}

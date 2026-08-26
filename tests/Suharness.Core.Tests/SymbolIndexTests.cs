using Suharness.Abstractions;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the compiler-free declaration scanner and the name lookup built on it.
/// </summary>
/// <remarks>
/// The scanner is a heuristic, so these tests fix the behaviour that is actually relied on — a
/// declaration is found, attributed to the right enclosing type, and reported at the right line —
/// rather than pinning every pattern it happens to match today.
/// </remarks>
public sealed class SymbolIndexTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-symbols-").FullName;
	private readonly string _store = Directory.CreateTempSubdirectory("suharness-symbols-store-").FullName;

	public void Dispose()
	{
		foreach (var directory in new[] { _root, _store })
		{
			try { Directory.Delete(directory, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}
	}

	private void Write(string relative, string content)
	{
		var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	private static IReadOnlyList<Declaration> Scan(string path, string source) =>
		DeclarationScanner.Scan(path, new StringReader(source));

	// ---------------------------------------------------------------- scanner

	[Fact]
	public void ScannerFindsCSharpTypesMembersAndTheirNamespace()
	{
		var found = Scan("Sample.cs", """
			namespace Contoso.Billing;

			public sealed class InvoiceService
			{
				public decimal Total { get; set; }

				public async Task<int> ChargeAsync(string customer)
				{
					return 0;
				}
			}

			internal record struct Money(decimal Amount);
			""");

		Assert.Contains(found, item => item.Name == "Contoso.Billing" && item.Kind == "namespace");
		var service = Assert.Single(found, item => item.Name == "InvoiceService");
		Assert.Equal("class", service.Kind);
		Assert.Equal("Contoso.Billing", service.Container);
		Assert.Equal(3, service.Line);

		var charge = Assert.Single(found, item => item.Name == "ChargeAsync");
		Assert.Equal("Contoso.Billing.InvoiceService", charge.Container);

		var money = Assert.Single(found, item => item.Name == "Money");
		Assert.Equal("record struct", money.Kind);
		// A file-scoped namespace encloses declarations at its own indent, so the second type is a
		// sibling of the first rather than nested inside it.
		Assert.Equal("Contoso.Billing", money.Container);
	}

	[Fact]
	public void ScannerDoesNotMistakeATypeDeclarationForAMember()
	{
		var found = Scan("Sample.cs", "public sealed class Widget {\n}\n");

		var widget = Assert.Single(found);
		Assert.Equal("class", widget.Kind);
	}

	[Fact]
	public void ScannerUsesIndentationToAttributePythonFunctions()
	{
		var found = Scan("sample.py", """
			class Loader:
			    def load(self):
			        pass

			    async def reload(self):
			        pass

			def main():
			    pass
			""");

		Assert.Equal("Loader", Assert.Single(found, item => item.Name == "load").Container);
		Assert.Equal("Loader", Assert.Single(found, item => item.Name == "reload").Container);
		Assert.Null(Assert.Single(found, item => item.Name == "main").Container);
	}

	[Fact]
	public void ScannerReadsTypeScriptDeclarationsIncludingArrowFunctions()
	{
		var found = Scan("sample.ts", """
			export interface Shape { }
			export type Id = string;
			export const render = (shape: Shape) => shape;
			export default function boot() { }
			""");

		Assert.Equal("interface", Assert.Single(found, item => item.Name == "Shape").Kind);
		Assert.Equal("type", Assert.Single(found, item => item.Name == "Id").Kind);
		Assert.Equal("function", Assert.Single(found, item => item.Name == "render").Kind);
		Assert.Equal("function", Assert.Single(found, item => item.Name == "boot").Kind);
	}

	[Theory]
	[InlineData("Sample.java", "public class Order { public void ship() { } }", "Order")]
	[InlineData("sample.go", "func Serve(addr string) error {\n\treturn nil\n}\n", "Serve")]
	[InlineData("sample.rs", "pub fn parse(input: &str) -> Result<()> { }", "parse")]
	[InlineData("sample.rb", "class Basket\n  def add(item)\n  end\nend\n", "Basket")]
	[InlineData("sample.kt", "class Repo {\n    fun findAll(): List<String> = emptyList()\n}", "findAll")]
	// One declaration per line is the scanner's contract, so the Swift case is written the way Swift
	// actually is rather than folded onto a single line.
	[InlineData("sample.swift", "struct Point {\n    func distance() -> Double { 0 }\n}", "distance")]
	public void ScannerHandlesEveryLanguageItClaims(string path, string source, string expected) =>
		Assert.Contains(Scan(path, source), item => item.Name == expected);

	[Fact]
	public void ScannerIgnoresCommentedOutDeclarationsButKeepsPreprocessorDefines()
	{
		var commented = Scan("Sample.cs", "// public class Ghost { }\n");
		var defined = Scan("sample.h", "#define MAX_ITEMS 64\n");

		Assert.Empty(commented);
		Assert.Equal("define", Assert.Single(defined, item => item.Name == "MAX_ITEMS").Kind);
	}

	// ---------------------------------------------------------------- lookup

	private SymbolIndex NewIndex(string? store = null) => new(new WorkspaceIndex(store), store);

	[Fact]
	public void FindRanksAnExactMatchAboveALooserOne()
	{
		Write("src/Player.cs", "public class PlayerController { }\n");
		Write("src/Other.cs", "public class PlayerControllerFactory { }\npublic class Placeholder { }\n");

		var hits = NewIndex().Find(_root, "PlayerController");

		Assert.Equal("PlayerController", hits[0].Name);
		Assert.Equal("src/Player.cs", hits[0].RelativePath);
		Assert.Equal(SymbolSource.Declarations, hits[0].Source);
		Assert.Contains(hits, hit => hit.Name == "PlayerControllerFactory");
	}

	/// <summary>
	/// The behaviour that replaces a run of guessed glob patterns: one query, no wildcards, and
	/// capitalisation cannot make it miss.
	/// </summary>
	[Fact]
	public void FindIsCaseInsensitiveAndMatchesOnASubsequence()
	{
		Write("src/Lights.cs", "public class MeadowGlideNightLighting { }\n");
		var index = NewIndex();

		Assert.NotEmpty(index.Find(_root, "meadowglidenightlighting"));
		Assert.NotEmpty(index.Find(_root, "GlideNight"));
		Assert.NotEmpty(index.Find(_root, "MGNL"));
		Assert.Empty(index.Find(_root, "Terrain"));
	}

	[Fact]
	public void FindNarrowsByContainerWhenTheQueryIsQualified()
	{
		Write("src/Player.cs", "public class Player\n{\n\tpublic void Move() { }\n}\n");
		Write("src/Camera.cs", "public class Camera\n{\n\tpublic void Move() { }\n}\n");

		var hits = NewIndex().Find(_root, "Camera.Move");

		Assert.Equal("src/Camera.cs", hits[0].RelativePath);
		Assert.Equal("Camera", hits[0].Container);
	}

	[Fact]
	public void FindCanBeRestrictedToOneKind()
	{
		Write("src/Thing.cs", "public class Report { }\n");
		Write("src/Maker.cs", "public class Maker\n{\n\tpublic void Report() { }\n}\n");

		var hits = NewIndex().Find(_root, "Report", kind: "class");

		Assert.All(hits, hit => Assert.Equal("class", hit.Kind));
		Assert.Contains(hits, hit => hit.RelativePath == "src/Thing.cs");
	}

	[Fact]
	public void SymbolsFromIgnoredAndHiddenFilesAreNotIndexed()
	{
		Write(".gitignore", "vendor/\n");
		Write("vendor/Bundled.cs", "public class BundledThing { }\n");
		Write(".cache/Cached.cs", "public class CachedThing { }\n");
		Write("src/Real.cs", "public class RealThing { }\n");
		var index = NewIndex();

		Assert.NotEmpty(index.Find(_root, "RealThing"));
		Assert.Empty(index.Find(_root, "BundledThing"));
		Assert.Empty(index.Find(_root, "CachedThing"));
	}

	[Fact]
	public void EditingAFileUpdatesTheSymbolsItContributes()
	{
		Write("src/Thing.cs", "public class OldName { }\n");
		var files = new WorkspaceIndex(freshness: TimeSpan.Zero);
		var index = new SymbolIndex(files);
		Assert.NotEmpty(index.Find(_root, "OldName"));

		Write("src/Thing.cs", "public class NewName { }\npublic class AlsoNew { }\n");

		Assert.Empty(index.Find(_root, "OldName"));
		Assert.NotEmpty(index.Find(_root, "NewName"));
		Assert.NotEmpty(index.Find(_root, "AlsoNew"));
	}

	[Fact]
	public void InFileReturnsOneFilesDeclarationsForResolvingAPosition()
	{
		Write("src/Thing.cs", "public class Thing\n{\n\tpublic void Run() { }\n}\n");

		var declarations = NewIndex().InFile(_root, "src/Thing.cs");

		Assert.Equal(1, Assert.Single(declarations, item => item.Name == "Thing").Line);
		Assert.Equal(3, Assert.Single(declarations, item => item.Name == "Run").Line);
	}

	[Fact]
	public async Task ASymbolTablePersistedByOneProcessIsReusedByTheNext()
	{
		Write("src/Thing.cs", "public class Persisted { }\n");
		Assert.NotEmpty(NewIndex(_store).Find(_root, "Persisted"));

		var table = Path.Combine(IndexStore.DirectoryFor(_store, _root), "symbols.tsv");
		for (var attempt = 0; attempt < 100 && !File.Exists(table); attempt++) await Task.Delay(20);

		Assert.True(File.Exists(table));
		Assert.Contains("Persisted", await File.ReadAllTextAsync(table));
		Assert.NotEmpty(NewIndex(_store).Find(_root, "Persisted"));
	}

	[Fact]
	public void AWorkspaceWithNoRecognisedSourceReportsNoSymbolsRatherThanFailing()
	{
		Write("notes.txt", "nothing to see");
		var index = NewIndex();

		Assert.False(index.IsAvailable(_root));
		Assert.Empty(index.Find(_root, "anything"));
	}
}

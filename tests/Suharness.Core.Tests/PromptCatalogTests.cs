using Suharness.Abstractions;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// Overriding prompts is process-wide state — an environment variable and a static cache — so this
/// collection may not run alongside anything else that composes a prompt.
/// </summary>
[CollectionDefinition(PromptCatalogTests.CollectionName, DisableParallelization = true)]
public sealed class PromptCatalogCollection;

/// <summary>
/// The prompt loader. Prompt text is the agent's behaviour, so the failure modes that matter here
/// are the quiet ones: text that silently goes missing, a placeholder that never gets filled, and
/// caller data being rewritten on its way into a prompt.
/// </summary>
[Collection(CollectionName)]
public sealed class PromptCatalogTests : IDisposable
{
	internal const string CollectionName = "prompt-catalog";

	private readonly string _override = Path.Combine(
		Path.GetTempPath(),
		"suharness-prompts-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(PromptCatalog.OverrideVariable, null);
		PromptCatalog.Reload();
		if (Directory.Exists(_override)) Directory.Delete(_override, recursive: true);
	}

	[Fact]
	public void EveryPromptFileLoads()
	{
		var root = PromptCatalog.SourceDirectory;
		Assert.NotNull(root);
		var files = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
			.Where(path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.Ordinal))
			.ToArray();
		Assert.NotEmpty(files);

		foreach (var file in files)
		{
			var id = Path.GetRelativePath(root, file)[..^".md".Length].Replace(Path.DirectorySeparatorChar, '/');
			var text = PromptCatalog.Get(id);
			Assert.False(string.IsNullOrWhiteSpace(text), $"prompt '{id}' is empty");
			Assert.DoesNotContain("<!--", text, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void AnUnknownPromptNamesTheIdAndWhereItLooked()
	{
		var error = Assert.Throws<InvalidOperationException>(() => PromptCatalog.Get("system/not-a-prompt"));

		Assert.Contains("system/not-a-prompt", error.Message, StringComparison.Ordinal);
		Assert.Contains(PromptCatalog.OverrideVariable, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnknownSectionNamesTheSectionAndFile()
	{
		var error = Assert.Throws<InvalidOperationException>(() => PromptCatalog.Section("system/approach", "no-such-section"));

		Assert.Contains("no-such-section", error.Message, StringComparison.Ordinal);
		Assert.Contains("system/approach", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ASectionStopsAtTheNextHeading()
	{
		var section = PromptCatalog.Section("system/approach", "todo-list");

		Assert.StartsWith("For work spanning several steps", section, StringComparison.Ordinal);
		Assert.DoesNotContain("##", section, StringComparison.Ordinal);
		Assert.DoesNotContain("After a change, verify it", section, StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnsuppliedPlaceholderThrowsRatherThanReachingTheModel()
	{
		var error = Assert.Throws<InvalidOperationException>(() =>
			PromptCatalog.Fill("Repository: {{workspaceRoot}}, model: {{modelName}}.", ("workspaceRoot", "/repo")));

		Assert.Contains("{{modelName}}", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AValueWithNoPlaceholderThrowsRatherThanBeingDropped()
	{
		var error = Assert.Throws<InvalidOperationException>(() =>
			PromptCatalog.Fill("Repository: {{workspaceRoot}}.", ("workspaceRoot", "/repo"), ("modelName", "test")));

		Assert.Contains("{{modelName}}", error.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// A user's own prompt is substituted into continuation templates. Braces they happen to type
	/// are their text, not an unfilled placeholder, and must not fail their turn.
	/// </summary>
	[Fact]
	public void BracesInsideASuppliedValueAreNotTreatedAsPlaceholders()
	{
		var filled = PromptCatalog.Fill("Task:\n{{request}}", ("request", "render {{name}} in the template"));

		Assert.Equal("Task:\nrender {{name}} in the template", filled);
	}

	/// <summary>
	/// Only the file's own line endings belong to the loader. Rewriting a caller's line endings
	/// silently altered task instructions the first time this was built the other way round.
	/// </summary>
	[Fact]
	public void SuppliedValuesKeepTheirOwnLineEndings()
	{
		Directory.CreateDirectory(_override);
		File.WriteAllText(Path.Combine(_override, "sample.md"), "Header\n{{body}}\nFooter\n");
		UseOverride();

		var rendered = PromptCatalog.Render("sample", PromptNewline.Crlf, ("body", "first\nsecond"));

		Assert.Equal("Header\r\nfirst\nsecond\r\nFooter", rendered);
	}

	[Fact]
	public void NewlineModeConvertsOnlyTheTemplate()
	{
		Directory.CreateDirectory(_override);
		File.WriteAllText(Path.Combine(_override, "sample.md"), "one\ntwo\n");
		UseOverride();

		Assert.Equal("one\ntwo", PromptCatalog.Get("sample", PromptNewline.Lf));
		Assert.Equal("one\r\ntwo", PromptCatalog.Get("sample", PromptNewline.Crlf));
		Assert.Equal($"one{Environment.NewLine}two", PromptCatalog.Get("sample", PromptNewline.Environment));
	}

	[Fact]
	public void CommentsAreStrippedIncludingOnesThatWrapAcrossLines()
	{
		Directory.CreateDirectory(_override);
		File.WriteAllText(
			Path.Combine(_override, "sample.md"),
			"<!-- a note that wraps\n     onto a second line -->\nKept text.\n<!-- one line -->\nAlso kept.\n");
		UseOverride();

		Assert.Equal("Kept text.\nAlso kept.", PromptCatalog.Get("sample"));
	}

	/// <summary>Prompts teach models about HTML, so a comment opener mid-sentence is prompt text.</summary>
	[Fact]
	public void AnHtmlCommentInsideASentenceIsKept()
	{
		Directory.CreateDirectory(_override);
		File.WriteAllText(Path.Combine(_override, "sample.md"), "Strip a <!-- comment --> from the markup.\n");
		UseOverride();

		Assert.Equal("Strip a <!-- comment --> from the markup.", PromptCatalog.Get("sample"));
	}

	[Fact]
	public void TheOverrideDirectoryWinsOverTheShippedPrompt()
	{
		Directory.CreateDirectory(Path.Combine(_override, "system"));
		File.WriteAllText(Path.Combine(_override, "system", "minimal.md"), "You are a tuned assistant.\n");
		UseOverride();

		Assert.Equal("You are a tuned assistant.", PromptCatalog.Get("system/minimal"));
		// An id the override does not define still resolves from the shipped files.
		Assert.Contains("Locate and read the code", PromptCatalog.Section("system/approach", "locate-first"), StringComparison.Ordinal);
	}

	[Fact]
	public void AnOverrideDirectoryThatDoesNotExistIsAnErrorRatherThanASilentFallback()
	{
		Environment.SetEnvironmentVariable(PromptCatalog.OverrideVariable, _override + "-missing");
		PromptCatalog.Reload();

		var error = Assert.Throws<InvalidOperationException>(() => PromptCatalog.Get("system/minimal"));

		Assert.Contains(PromptCatalog.OverrideVariable, error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("../secrets")]
	[InlineData("system\\agent")]
	[InlineData("/etc/passwd")]
	public void AnIdThatIsNotARelativeForwardSlashedPathIsRejected(string id) =>
		Assert.Throws<ArgumentException>(() => PromptCatalog.Get(id));

	/// <summary>
	/// The loop guard slices its repair prompt with literal CRLF markers, so that prompt has to come
	/// back with CRLF even though the file on disk is stored with LF.
	/// </summary>
	[Fact]
	public void TheWorkflowRepairPromptKeepsTheCrlfMarkersItIsSlicedWith()
	{
		var prompt = PromptCatalog.Render(
			"workflow/junior-developer-repair",
			PromptNewline.Crlf,
			("source", "reviewer"),
			("finding", "a finding"));

		Assert.Contains("\r\nWhen an anchor has no rel attribute", prompt, StringComparison.Ordinal);
		Assert.Contains("\r\nIf the finding requires adding tests", prompt, StringComparison.Ordinal);
	}

	private void UseOverride()
	{
		Environment.SetEnvironmentVariable(PromptCatalog.OverrideVariable, _override);
		PromptCatalog.Reload();
	}
}

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Suharness.Abstractions;

/// <summary>
/// How a loaded prompt's line endings are normalized. Prompt text used to live in C# literals whose
/// newlines differed by construction, and the difference is load-bearing: the coding workflow slices
/// prompts with literal CRLF markers, while raw string literals in this tree are always LF because
/// .gitattributes normalizes *.cs. A prompt moved into a file must keep the endings it had.
/// </summary>
public enum PromptNewline
{
	/// <summary>LF — matches a C# raw string literal in an LF-normalized source file.</summary>
	Lf,

	/// <summary>CRLF — matches a literal that spelled out <c>\r\n</c>.</summary>
	Crlf,

	/// <summary>Environment.NewLine — matches text assembled with StringBuilder.AppendLine.</summary>
	Environment,
}

/// <summary>
/// The agent's prompt text, kept in editable Markdown files under the repository's Prompts folder
/// rather than in C# literals.
/// </summary>
/// <remarks>
/// A prompt is addressed by id: the path under Prompts without the extension, always forward-slashed,
/// so <c>workflow/reviewer</c> is <c>Prompts/workflow/reviewer.md</c>. Files are resolved in order —
/// the <see cref="OverrideVariable"/> directory, then a Prompts folder beside the running binary, then
/// the copy embedded in this assembly. The first two let a prompt be edited and picked up on restart
/// without a rebuild; the embedded copy guarantees a published binary is never missing its prompts.
/// Text is cached on first use, so an edit takes effect on the next process start.
/// </remarks>
public static class PromptCatalog
{
	/// <summary>Environment variable naming a directory whose prompt files win over every other source.</summary>
	public const string OverrideVariable = "SUHARNESS_PROMPTS";

	private const string FolderName = "Prompts";
	private const string Extension = ".md";
	private const string ResourceInfix = "." + FolderName + ".";

	/// <summary>A placeholder name is deliberately narrow so prose braces and JSON schemas pass through.</summary>
	private static readonly Regex Placeholder = new(@"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant);

	private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);
	private static readonly Lazy<IReadOnlyDictionary<string, string>> EmbeddedNames = new(MapEmbeddedResources);
	private static readonly Lock DirectoryGate = new();
	private static string? _folder;
	private static bool _folderResolved;

	/// <summary>
	/// The directory loose prompt files are being read from, or null when the embedded copies are in
	/// use. Surfaced so a diagnostic can say which prompts a run actually used.
	/// </summary>
	public static string? SourceDirectory => Folder();

	private static string? Folder()
	{
		lock (DirectoryGate)
		{
			if (_folderResolved) return _folder;
			_folder = ResolveDirectory();
			_folderResolved = true;
			return _folder;
		}
	}

	/// <summary>Load one whole prompt file.</summary>
	public static string Get(string id, PromptNewline newline = PromptNewline.Lf) =>
		Normalize(Load(id), newline);

	/// <summary>
	/// Load one <c>## name</c> section of a prompt file. Sections keep the conditional pieces of a
	/// composed prompt — the plan-mode block, one tool's guidance — together in one editable file
	/// instead of scattering them across dozens of single-paragraph files.
	/// </summary>
	public static string Section(string id, string section, PromptNewline newline = PromptNewline.Lf)
	{
		var heading = "## " + section;
		var captured = new List<string>();
		var found = false;
		foreach (var raw in Load(id).Split('\n'))
		{
			if (raw.StartsWith("## ", StringComparison.Ordinal))
			{
				if (found) break;
				found = string.Equals(raw.TrimEnd(), heading, StringComparison.Ordinal);
				continue;
			}
			if (found) captured.Add(raw);
		}
		if (!found)
			throw new InvalidOperationException(
				$"Prompt '{id}' has no section '{section}'. Add a '{heading}' heading to {id}{Extension}.");
		return Normalize(string.Join('\n', captured).Trim('\n'), newline);
	}

	/// <summary>
	/// Substitute <c>{{name}}</c> placeholders. Both directions are checked: a placeholder with no
	/// value and a value with no placeholder are equally a sign that the file and its call site have
	/// drifted apart, and either would otherwise reach the model as a malformed prompt.
	/// </summary>
	/// <remarks>
	/// The template is scanned for placeholders <em>before</em> anything is substituted. Substituted
	/// values include the user's own prompt text, so scanning afterwards would report a user who
	/// happened to type braces as a drifted call site and fail their turn.
	/// </remarks>
	public static string Fill(string template, params (string Name, string Value)[] values)
	{
		var required = Placeholder.Matches(template).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
		foreach (var (name, _) in values)
		{
			if (!required.Remove(name))
				throw new InvalidOperationException($"Prompt placeholder '{{{{{name}}}}}' was supplied but does not appear in the template.");
		}
		if (required.Count > 0)
			throw new InvalidOperationException(
				$"Prompt placeholders were not supplied a value: {string.Join(", ", required.Order().Select(name => "{{" + name + "}}"))}.");

		var builder = new StringBuilder(template);
		foreach (var (name, value) in values) builder.Replace("{{" + name + "}}", value);
		return builder.ToString();
	}

	/// <summary>Load, normalize, and fill in one call — the common shape at a call site.</summary>
	/// <remarks>
	/// The template is normalized <em>before</em> substitution, never after. Substituted values are
	/// caller data — a user's prompt, a task instruction, a captured tool result — and carry whatever
	/// line endings they already had; rewriting those would silently alter content this class does
	/// not own. Only the prompt file's own line endings are ours to normalize.
	/// </remarks>
	public static string Render(string id, PromptNewline newline, params (string Name, string Value)[] values) =>
		Fill(Normalize(Load(id), newline), values);

	/// <summary>Load one section, normalize it, and fill it.</summary>
	public static string RenderSection(string id, string section, PromptNewline newline, params (string Name, string Value)[] values) =>
		Fill(Section(id, section, newline), values);

	/// <summary>
	/// Drop cached text and re-resolve the prompt directory, so the next read picks up an edited
	/// file or a changed <see cref="OverrideVariable"/>. Intended for tests and tooling.
	/// </summary>
	public static void Reload()
	{
		Texts.Clear();
		lock (DirectoryGate)
		{
			_folder = null;
			_folderResolved = false;
		}
	}

	private static string Load(string id) => Texts.GetOrAdd(id, static key => Read(key));

	private static string Read(string id)
	{
		if (string.IsNullOrWhiteSpace(id) || id.StartsWith('/') || id.Contains('\\', StringComparison.Ordinal)
			|| id.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(id))
			throw new ArgumentException(
				$"'{id}' is not a valid prompt id: use a forward-slashed relative path such as 'workflow/reviewer'.",
				nameof(id));

		var directory = Folder();
		if (directory is not null)
		{
			var path = Path.Combine(directory, Path.Combine(id.Split('/')) + Extension);
			if (File.Exists(path)) return Clean(File.ReadAllText(path));
		}

		if (EmbeddedNames.Value.TryGetValue(id, out var resource))
		{
			using var stream = typeof(PromptCatalog).Assembly.GetManifestResourceStream(resource)
				?? throw new InvalidOperationException($"Embedded prompt resource '{resource}' could not be opened.");
			using var reader = new StreamReader(stream, Encoding.UTF8);
			return Clean(reader.ReadToEnd());
		}

		throw new InvalidOperationException(
			$"Prompt '{id}' was not found. Looked for '{id}{Extension}' under "
			+ $"{directory ?? "(no prompt directory resolved)"} and in this assembly's embedded prompts. "
			+ $"Set {OverrideVariable} to a directory of prompt files to override them.");
	}

	/// <summary>
	/// Strip editor-only comment blocks and settle on LF so every later step works on one shape.
	/// </summary>
	/// <remarks>
	/// A comment must begin its own line and runs to the line that closes it, so a note may wrap
	/// across several lines. It is deliberately never recognized mid-line: prompts teach the model
	/// about HTML, and a rule that could fire inside a sentence would silently eat prompt text.
	/// </remarks>
	private static string Clean(string text)
	{
		var normalized = text.ReplaceLineEndings("\n");
		if (!normalized.Contains("<!--", StringComparison.Ordinal)) return normalized.Trim('\n');
		var kept = new List<string>();
		var inComment = false;
		foreach (var line in normalized.Split('\n'))
		{
			var trimmed = line.Trim();
			if (inComment)
			{
				if (trimmed.EndsWith("-->", StringComparison.Ordinal)) inComment = false;
				continue;
			}
			if (!trimmed.StartsWith("<!--", StringComparison.Ordinal))
			{
				kept.Add(line);
				continue;
			}
			// A comment that does not close on its own line continues onto the following ones.
			if (!trimmed.EndsWith("-->", StringComparison.Ordinal) || trimmed.Length < "<!---->".Length) inComment = true;
		}
		if (inComment)
			throw new InvalidOperationException("A prompt file has an unterminated '<!--' comment; close it with '-->'.");
		return string.Join('\n', kept).Trim('\n');
	}

	private static string Normalize(string text, PromptNewline newline) => newline switch
	{
		PromptNewline.Lf => text,
		PromptNewline.Crlf => text.Replace("\n", "\r\n", StringComparison.Ordinal),
		_ => System.Environment.NewLine == "\n" ? text : text.Replace("\n", System.Environment.NewLine, StringComparison.Ordinal),
	};

	private static string? ResolveDirectory()
	{
		var configured = System.Environment.GetEnvironmentVariable(OverrideVariable);
		if (!string.IsNullOrWhiteSpace(configured))
		{
			var trimmed = configured.Trim();
			if (Directory.Exists(trimmed)) return Path.GetFullPath(trimmed);
			throw new InvalidOperationException(
				$"{OverrideVariable} is set to '{trimmed}', which is not an existing directory. "
				+ "Point it at a directory of prompt files or unset it.");
		}
		var beside = Path.Combine(AppContext.BaseDirectory, FolderName);
		return Directory.Exists(beside) ? beside : null;
	}

	private static IReadOnlyDictionary<string, string> MapEmbeddedResources()
	{
		var map = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var name in typeof(PromptCatalog).Assembly.GetManifestResourceNames())
		{
			if (!name.EndsWith(Extension, StringComparison.Ordinal)) continue;
			var start = name.IndexOf(ResourceInfix, StringComparison.Ordinal);
			if (start < 0) continue;
			var relative = name[(start + ResourceInfix.Length)..^Extension.Length];
			map[relative.Replace('.', '/')] = name;
		}
		return map;
	}
}

using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>One declaration found by scanning source text.</summary>
/// <param name="Line">One-based line the declaration appears on.</param>
public readonly record struct Declaration(string Name, string Kind, int Line, string? Container);

/// <summary>
/// Extracts declarations from source text with a tolerant, line-based scan.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a scanner and not a parser, and the trade is worth stating plainly. It
/// cannot disambiguate overloads, resolve a receiver, or follow a type alias, so it is not a
/// substitute for a language server. What it has instead is that it always works: it needs no
/// build, no project file, and no toolchain, it answers on code that does not compile — which is
/// frequently why an agent is reading the code at all — and it costs one pass over text the index
/// is already enumerating.
/// </para>
/// <para>
/// Containers are inferred, not resolved. A declaration is attributed to the nearest preceding
/// type-like declaration, and in indentation-structured languages to the nearest one at a shallower
/// indent. That is right often enough to disambiguate two methods with the same name and wrong
/// often enough that it is reported as context rather than as part of the symbol's identity.
/// </para>
/// <para>
/// At most one declaration is taken from a line. Trying for more would mean re-matching the rules
/// against the middle of a line with their start anchors removed, which finds a second declaration
/// on the rare one-line type at the cost of far more false positives everywhere else. A declaration
/// that shares a line with another is therefore reachable through the enclosing one, not directly.
/// </para>
/// </remarks>
public static class DeclarationScanner
{
	/// <summary>Files above this size are skipped: a multi-megabyte source file is generated, not written.</summary>
	public const long MaxFileBytes = 4 * 1024 * 1024;

	/// <summary>Lines above this length are skipped, which is what excludes minified and bundled output.</summary>
	public const int MaxLineLength = 512;

	/// <summary>Cap on declarations taken from one file, so a generated table cannot dominate the index.</summary>
	public const int MaxDeclarationsPerFile = 5000;

	private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.ExplicitCapture;

	/// <summary>Kinds that can enclose other declarations, and so become the container for what follows.</summary>
	private static readonly HashSet<string> ContainerKinds = new(StringComparer.Ordinal)
	{
		"namespace", "class", "struct", "interface", "enum", "record", "module", "trait", "object",
		"protocol", "extension", "impl", "actor", "union",
	};

	/// <summary>One extraction rule: a pattern with a <c>name</c> group, and a <c>kind</c> group or a fixed kind.</summary>
	private sealed record Rule(Regex Pattern, string? Kind = null);

	private sealed record Language(string Name, Rule[] Rules);

	// ---- shared fragments -------------------------------------------------

	private const string DotNetModifiers =
		@"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|virtual|override|async|unsafe|extern|new|file|required|const|ref|volatile)\s+)";

	private const string JavaModifiers =
		@"(?:(?:public|private|protected|static|final|abstract|synchronized|native|transient|volatile|strictfp|default)\s+)";

	// A return type must not be a declaration keyword, or "public class Foo {" would be read twice:
	// once as the type it is, and once as a method returning something called "class".
	private const string NotAKeyword =
		@"(?!(?:class|struct|interface|enum|record|namespace|delegate|using|return|new|throw|if|for|foreach|while|switch|catch|lock|fixed|do|else|case|yield|await|typedef|extends|implements|package|import)\b)";

	private static readonly Language CSharp = new("csharp",
	[
		new(new Regex(@"^\s*namespace\s+(?<name>[A-Za-z_][\w\.]*)", Options), "namespace"),
		new(new Regex($@"^\s*(?:\[[^\]]*\]\s*)*{DotNetModifiers}*(?<kind>record\s+struct|record\s+class|record|class|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)", Options)),
		new(new Regex($@"^\s*(?:\[[^\]]*\]\s*)*{DotNetModifiers}*delegate\s+[\w<>\[\],\.\?]+\s+(?<name>[A-Za-z_]\w*)\s*[(<]", Options), "delegate"),
		// Members need at least one modifier, so a local variable or a call cannot be mistaken for one.
		new(new Regex($@"^\s*(?:\[[^\]]*\]\s*)*{DotNetModifiers}+{NotAKeyword}[\w<>\[\],\.\?\(\)]+\s+(?<name>[A-Za-z_]\w*)\s*(?:<[^>=()]*>)?\s*(?:\(|\{{|=>)", Options), "member"),
	]);

	private static readonly Language Java = new("java",
	[
		new(new Regex(@"^\s*package\s+(?<name>[A-Za-z_][\w\.]*)", Options), "package"),
		new(new Regex($@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*{JavaModifiers}*(?<kind>@interface|class|interface|enum|record)\s+(?<name>[A-Za-z_]\w*)", Options)),
		new(new Regex($@"^\s*(?:@\w+(?:\([^)]*\))?\s*)*{JavaModifiers}+(?:<[^>]*>\s*)?{NotAKeyword}[\w<>\[\],\.\?]+\s+(?<name>[A-Za-z_]\w*)\s*\(", Options), "method"),
	]);

	private static readonly Language Python = new("python",
	[
		new(new Regex(@"^[ \t]*class\s+(?<name>[A-Za-z_]\w*)", Options), "class"),
		new(new Regex(@"^[ \t]*(?:async\s+)?def\s+(?<name>[A-Za-z_]\w*)", Options), "function"),
	]);

	private static readonly Language TypeScript = new("typescript",
	[
		new(new Regex(@"^\s*(?:export\s+(?:default\s+)?)?(?:declare\s+)?(?:abstract\s+)?(?<kind>class|interface|enum|namespace|module)\s+(?<name>[A-Za-z_$][\w$]*)", Options)),
		new(new Regex(@"^\s*(?:export\s+(?:default\s+)?)?(?:declare\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[A-Za-z_$][\w$]*)", Options), "function"),
		new(new Regex(@"^\s*(?:export\s+)?(?:declare\s+)?type\s+(?<name>[A-Za-z_$][\w$]*)\s*[=<]", Options), "type"),
		// An arrow function bound to a name is a declaration in every sense that matters to a reader.
		new(new Regex(@"^\s*(?:export\s+(?:default\s+)?)?(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*(?::[^=]+?)?=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>", Options), "function"),
		new(new Regex(@"^\s{2,}(?:(?:public|private|protected|readonly|static|abstract|override|async)\s+)+(?<name>[A-Za-z_$][\w$]*)\s*(?:<[^>]*>)?\s*\(", Options), "method"),
	]);

	private static readonly Language Go = new("go",
	[
		new(new Regex(@"^func\s*(?:\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)", Options), "func"),
		new(new Regex(@"^\s*type\s+(?<name>[A-Za-z_]\w*)", Options), "type"),
	]);

	private static readonly Language Rust = new("rust",
	[
		new(new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+""[^""]*""\s+)?fn\s+(?<name>\w+)", Options), "fn"),
		new(new Regex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?<kind>struct|enum|trait|union|mod|type|const|static)\s+(?<name>\w+)", Options)),
		new(new Regex(@"^\s*impl(?:<[^>]*>)?\s+(?:[\w:<>, ]+?\s+for\s+)?(?<name>[A-Za-z_]\w*)", Options), "impl"),
		new(new Regex(@"^\s*macro_rules!\s*(?<name>\w+)", Options), "macro"),
	]);

	private static readonly Language CFamily = new("c",
	[
		new(new Regex(@"^\s*#\s*define\s+(?<name>[A-Za-z_]\w*)", Options), "define"),
		new(new Regex(@"^\s*(?:typedef\s+)?(?<kind>class|struct|union|enum)\s+(?<name>[A-Za-z_]\w*)", Options)),
		// A definition, not a declaration: the body has to open on the same line, which is what keeps
		// prototypes and calls out of the table.
		new(new Regex($@"^[A-Za-z_][\w\s\*&:<>,]*?[\s\*&]{NotAKeyword}(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*(?:const\s*)?\{{\s*$", Options), "function"),
	]);

	private static readonly Language Ruby = new("ruby",
	[
		new(new Regex(@"^\s*(?<kind>class|module)\s+(?<name>[A-Za-z_][\w:]*)", Options)),
		new(new Regex(@"^\s*def\s+(?:self\.)?(?<name>[A-Za-z_]\w*[?!=]?)", Options), "def"),
	]);

	private static readonly Language Php = new("php",
	[
		new(new Regex(@"^\s*(?:abstract\s+|final\s+)?(?<kind>class|interface|trait|enum)\s+(?<name>\w+)", Options)),
		new(new Regex(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(?<name>\w+)\s*\(", Options), "function"),
	]);

	private static readonly Language Kotlin = new("kotlin",
	[
		new(new Regex(@"^\s*(?:(?:public|private|protected|internal|open|sealed|abstract|data|inner|value|annotation)\s+)*(?<kind>class|interface|object)\s+(?<name>\w+)", Options)),
		new(new Regex(@"^\s*(?:(?:public|private|protected|internal|open|override|suspend|inline|operator|infix|tailrec)\s+)*fun\s+(?:<[^>]*>\s*)?(?:[\w.<>]+\.)?(?<name>\w+)\s*\(", Options), "fun"),
	]);

	private static readonly Language Swift = new("swift",
	[
		new(new Regex(@"^\s*(?:(?:public|private|internal|fileprivate|open|final|indirect)\s+)*(?<kind>class|struct|enum|protocol|extension|actor)\s+(?<name>\w+)", Options)),
		new(new Regex(@"^\s*(?:(?:public|private|internal|fileprivate|open|final|static|class|override|mutating)\s+)*func\s+(?<name>\w+)", Options), "func"),
	]);

	private static readonly Language Scala = new("scala",
	[
		new(new Regex(@"^\s*(?:(?:private|protected|final|sealed|abstract|implicit|case)\s+)*(?<kind>class|object|trait)\s+(?<name>\w+)", Options)),
		new(new Regex(@"^\s*(?:(?:private|protected|final|override|implicit)\s+)*def\s+(?<name>\w+)", Options), "def"),
	]);

	/// <summary>
	/// Which rule set an extension belongs to. Related languages share a set where their declaration
	/// syntax agrees closely enough that a separate table would only duplicate it.
	/// </summary>
	private static readonly Dictionary<string, Language> ByExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		[".cs"] = CSharp,
		[".java"] = Java,
		[".py"] = Python,
		[".pyi"] = Python,
		[".ts"] = TypeScript,
		[".tsx"] = TypeScript,
		[".mts"] = TypeScript,
		[".cts"] = TypeScript,
		[".js"] = TypeScript,
		[".jsx"] = TypeScript,
		[".mjs"] = TypeScript,
		[".cjs"] = TypeScript,
		[".go"] = Go,
		[".rs"] = Rust,
		[".c"] = CFamily,
		[".h"] = CFamily,
		[".cc"] = CFamily,
		[".cpp"] = CFamily,
		[".cxx"] = CFamily,
		[".hpp"] = CFamily,
		[".hh"] = CFamily,
		[".hxx"] = CFamily,
		[".m"] = CFamily,
		[".mm"] = CFamily,
		[".rb"] = Ruby,
		[".php"] = Php,
		[".kt"] = Kotlin,
		[".kts"] = Kotlin,
		[".swift"] = Swift,
		[".scala"] = Scala,
		[".sc"] = Scala,
	};

	/// <summary>Extensions the scanner has rules for, which is also what the index bothers to read.</summary>
	public static IReadOnlyCollection<string> Extensions => ByExtension.Keys;

	/// <summary>True when this path has a language the scanner understands.</summary>
	public static bool Handles(string path) => ByExtension.ContainsKey(Path.GetExtension(path));

	/// <summary>The language name for a path, or null when there is no rule set for it.</summary>
	public static string? LanguageOf(string path) =>
		ByExtension.TryGetValue(Path.GetExtension(path), out var language) ? language.Name : null;

	/// <summary>Scan one file. An unreadable or binary file yields nothing rather than failing.</summary>
	public static IReadOnlyList<Declaration> ScanFile(string fullPath, CancellationToken cancellationToken = default)
	{
		if (!ByExtension.TryGetValue(Path.GetExtension(fullPath), out var language)) return [];
		try
		{
			var info = new FileInfo(fullPath);
			if (!info.Exists || info.Length > MaxFileBytes) return [];
			using var reader = new StreamReader(
				new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
				System.Text.Encoding.UTF8,
				detectEncodingFromByteOrderMarks: true);
			return Scan(language, reader, cancellationToken);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return [];
		}
	}

	/// <summary>Scan already-open source text for one path's language. Exposed for testing.</summary>
	public static IReadOnlyList<Declaration> Scan(string path, TextReader reader, CancellationToken cancellationToken = default) =>
		ByExtension.TryGetValue(Path.GetExtension(path), out var language)
			? Scan(language, reader, cancellationToken)
			: [];

	private static IReadOnlyList<Declaration> Scan(Language language, TextReader reader, CancellationToken cancellationToken)
	{
		var found = new List<Declaration>();
		var lineNumber = 0;
		// The enclosing declarations still open at the current indent, outermost first.
		var open = new List<(string Name, int Indent, bool IsNamespace)>();

		while (reader.ReadLine() is { } line)
		{
			lineNumber++;
			if (found.Count >= MaxDeclarationsPerFile) break;
			if ((lineNumber & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
			if (line.Length == 0 || line.Length > MaxLineLength) continue;
			if (IsComment(line)) continue;

			foreach (var rule in language.Rules)
			{
				var match = rule.Pattern.Match(line);
				if (!match.Success) continue;
				var name = match.Groups["name"].Value;
				if (name.Length == 0) continue;
				var kind = rule.Kind ?? Normalize(match.Groups["kind"].Value);
				if (kind.Length == 0) continue;

				// Indentation closes a scope in every language the scanner handles, whether or not the
				// language gives it meaning: brace languages indent nested declarations by convention,
				// and the convention is reliable enough to attribute a member to the right type. A
				// file-scoped namespace is the exception — it encloses declarations at its own indent —
				// so it survives an equal indent where a type does not.
				var indent = Indent(line);
				var isNamespace = kind is "namespace" or "package";
				while (open.Count > 0 && (indent < open[^1].Indent || (indent == open[^1].Indent && !open[^1].IsNamespace)))
				{
					open.RemoveAt(open.Count - 1);
				}

				var container = open.Count == 0 ? null : open[^1].Name;
				found.Add(new Declaration(name, kind, lineNumber, container));

				if (ContainerKinds.Contains(kind) || isNamespace)
				{
					open.Add((container is null ? name : $"{container}.{name}", indent, isNamespace));
				}
				break;
			}
		}
		return found;
	}

	private static string Normalize(string kind) =>
		kind.Length == 0 ? string.Empty : Regex.Replace(kind.Trim(), @"\s+", " ");

	private static int Indent(string line)
	{
		var index = 0;
		while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
		return index;
	}

	/// <summary>
	/// A cheap comment guard. It deliberately does not treat <c>#</c> as a comment: that character
	/// starts a C preprocessor directive, and <c>#define</c> is a declaration worth having.
	/// </summary>
	private static bool IsComment(string line)
	{
		var index = 0;
		while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
		if (index >= line.Length) return true;
		if (line[index] == '*') return true;
		if (index + 1 >= line.Length) return false;
		return (line[index], line[index + 1]) is ('/', '/') or ('/', '*') or ('-', '-');
	}
}

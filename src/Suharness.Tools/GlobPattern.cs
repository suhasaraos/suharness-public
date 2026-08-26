using System.Text;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// Translates a ripgrep/gitignore-style glob into a <see cref="Regex"/> over forward-slash relative
/// paths, so SuHarness reproduces ripgrep-style matching without shipping a binary.
/// Supports <c>*</c>, <c>**</c>, <c>?</c>, <c>[...]</c> classes, and <c>{a,b}</c> alternation.
/// </summary>
public sealed class GlobPattern
{
	private readonly Regex _regex;

	private GlobPattern(Regex regex) => _regex = regex;

	/// <summary>
	/// Compile <paramref name="pattern"/>. A pattern containing no '/' matches the basename at any
	/// depth — ripgrep's rule, which is why "*.ts" searches the whole tree.
	/// </summary>
	public static GlobPattern Compile(string pattern, bool caseInsensitive)
	{
		var normalized = pattern.Replace('\\', '/').Trim();
		if (normalized.Length == 0) throw new ToolInputException("pattern must be a non-empty string");
		if (!normalized.Contains('/')) normalized = "**/" + normalized;
		else if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];

		var options = RegexOptions.CultureInvariant;
		if (caseInsensitive) options |= RegexOptions.IgnoreCase;
		return new GlobPattern(new Regex("^" + Translate(normalized) + "$", options));
	}

	/// <summary>Match a path already expressed relative to the search root with '/' separators.</summary>
	public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath);

	private static string Translate(string pattern)
	{
		var builder = new StringBuilder();
		var index = 0;
		while (index < pattern.Length)
		{
			var current = pattern[index];
			switch (current)
			{
				case '*':
					// "**/" spans any number of directories (including none); a bare "**" spans
					// anything; a single "*" stops at the separator.
					if (index + 1 < pattern.Length && pattern[index + 1] == '*')
					{
						if (index + 2 < pattern.Length && pattern[index + 2] == '/')
						{
							builder.Append("(?:.*/)?");
							index += 3;
						}
						else
						{
							builder.Append(".*");
							index += 2;
						}
					}
					else
					{
						builder.Append("[^/]*");
						index++;
					}
					break;

				case '?':
					builder.Append("[^/]");
					index++;
					break;

				case '[':
					index = TranslateCharacterClass(pattern, index, builder);
					break;

				case '{':
					index = TranslateAlternation(pattern, index, builder);
					break;

				default:
					builder.Append(Regex.Escape(current.ToString()));
					index++;
					break;
			}
		}
		return builder.ToString();
	}

	private static int TranslateCharacterClass(string pattern, int index, StringBuilder builder)
	{
		var close = pattern.IndexOf(']', index + 1);
		if (close == -1)
		{
			builder.Append("\\[");
			return index + 1;
		}
		var body = pattern[(index + 1)..close];
		if (body.StartsWith('!')) body = "^" + body[1..];
		builder.Append('[').Append(body).Append(']');
		return close + 1;
	}

	private static int TranslateAlternation(string pattern, int index, StringBuilder builder)
	{
		var close = pattern.IndexOf('}', index + 1);
		if (close == -1)
		{
			builder.Append("\\{");
			return index + 1;
		}
		var alternatives = pattern[(index + 1)..close].Split(',');
		builder.Append("(?:")
			.Append(string.Join('|', alternatives.Select(Translate)))
			.Append(')');
		return close + 1;
	}
}

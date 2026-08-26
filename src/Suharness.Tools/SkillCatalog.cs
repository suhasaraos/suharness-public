using System.Text;

namespace Suharness.Tools;

public sealed record SkillSummary(string Name, string Description, string Path);

/// <summary>Discovers bounded, workspace-sensitive instruction bundles and loads them on demand.</summary>
public static class SkillCatalog
{
	public const int MaxCatalogEntries = 100;
	public const int MaxDescriptionChars = 500;
	public const int MaxContentBytes = 256 * 1024;

	public static IReadOnlyList<SkillSummary> Discover(string workspaceRoot)
	{
		if (string.IsNullOrWhiteSpace(workspaceRoot)) return [];
		var roots = new[]
		{
			Path.Combine(Path.GetFullPath(workspaceRoot), ".suharness", "skills"),
			Path.Combine(Path.GetFullPath(workspaceRoot), ".agents", "skills"),
			Path.Combine(SuharnessHome(), "skills"),
		};
		var skills = new Dictionary<string, SkillSummary>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots)
		{
			if (!Directory.Exists(root)) continue;
			foreach (var path in CandidateFiles(root))
			{
				if (skills.Count >= MaxCatalogEntries) break;
				var summary = ReadSummary(path);
				if (summary is not null) skills.TryAdd(summary.Name, summary);
			}
		}
		return [.. skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)];
	}

	public static string Load(string workspaceRoot, string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return "Error: skill name must not be empty.";
		var skill = Discover(workspaceRoot).FirstOrDefault(candidate =>
			candidate.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
		if (skill is null) return $"Error: unknown skill '{name.Trim()}'.";
		try
		{
			var info = new FileInfo(skill.Path);
			if (info.Length > MaxContentBytes)
				return $"Error: skill '{skill.Name}' exceeds the {MaxContentBytes}-byte content limit.";
			var content = File.ReadAllText(skill.Path, Encoding.UTF8);
			return $"<skill_content name=\"{EscapeAttribute(skill.Name)}\">\n{StripFrontMatter(content).Trim()}\n</skill_content>";
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return $"Error: could not load skill '{skill.Name}': {exception.Message}";
		}
	}

	public static string Describe(IReadOnlyList<SkillSummary> skills) => skills.Count == 0
		? "Load reusable workspace instructions by name. No skills are currently available."
		: "Load reusable workspace instructions by name. Available skills: "
			+ string.Join("; ", skills.Select(skill => $"{skill.Name} — {skill.Description}"));

	private static IEnumerable<string> CandidateFiles(string root)
	{
		IEnumerable<string> flat;
		IEnumerable<string> bundles;
		try
		{
			flat = Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase);
			bundles = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
				.Select(directory => Path.Combine(directory, "SKILL.md"))
				.Where(File.Exists)
				.Order(StringComparer.OrdinalIgnoreCase);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			yield break;
		}
		foreach (var path in flat.Concat(bundles)) yield return path;
	}

	private static SkillSummary? ReadSummary(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (info.Length == 0 || info.Length > MaxContentBytes) return null;
			using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			var text = new StringBuilder();
			for (var index = 0; index < 80 && reader.ReadLine() is { } line; index++) text.AppendLine(line);
			var values = FrontMatter(text.ToString());
			var fallback = Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
				? Directory.GetParent(path)?.Name
				: Path.GetFileNameWithoutExtension(path);
			var name = values.GetValueOrDefault("name") ?? fallback;
			if (name is null || !ValidName(name)) return null;
			if (values.GetValueOrDefault("disable-model-invocation")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
				return null;
			var description = values.GetValueOrDefault("description") ?? FirstContentLine(text.ToString()) ?? "Reusable workspace instructions.";
			description = description.ReplaceLineEndings(" ").Trim();
			if (description.Length > MaxDescriptionChars) description = description[..MaxDescriptionChars];
			return new SkillSummary(name, description, Path.GetFullPath(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static Dictionary<string, string> FrontMatter(string text)
	{
		var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		using var reader = new StringReader(text);
		if (reader.ReadLine()?.Trim() != "---") return values;
		while (reader.ReadLine() is { } line && line.Trim() != "---")
		{
			var separator = line.IndexOf(':');
			if (separator <= 0) continue;
			var key = line[..separator].Trim();
			var value = line[(separator + 1)..].Trim().Trim('"', '\'');
			if (key.Length > 0 && value.Length > 0) values[key] = value;
		}
		return values;
	}

	private static string StripFrontMatter(string text)
	{
		if (!text.StartsWith("---", StringComparison.Ordinal)) return text;
		var normalized = text.ReplaceLineEndings("\n");
		var end = normalized.IndexOf("\n---\n", 3, StringComparison.Ordinal);
		return end < 0 ? text : normalized[(end + 5)..];
	}

	private static string? FirstContentLine(string text) => StripFrontMatter(text)
		.ReplaceLineEndings("\n")
		.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Select(line => line.TrimStart('#', ' ', '\t'))
		.FirstOrDefault(line => line.Length > 0);

	private static bool ValidName(string name) => name is { Length: > 0 and <= 100 }
		&& name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

	private static string SuharnessHome() => Environment.GetEnvironmentVariable("SUHARNESS_HOME")
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".suharness");

	private static string EscapeAttribute(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
		.Replace("\"", "&quot;", StringComparison.Ordinal)
		.Replace("<", "&lt;", StringComparison.Ordinal)
		.Replace(">", "&gt;", StringComparison.Ordinal);
}

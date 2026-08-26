using System.Text;

namespace Suharness.Tools;

/// <summary>
/// Loads the workspace's own AGENTS.md/CLAUDE.md chain: every candidate from the project root down to
/// the working
/// directory, broad to specific, deduplicated per directory and bounded by a byte budget.
/// SuHarness renders the chain into the system prompt so it reaches the model without a second
/// history channel.
/// </summary>
public static class WorkspaceInstructions
{
	public const int DefaultMaxBytes = 65536;

	private static readonly string[] Candidates =
	[
		"AGENTS.md",
		"CLAUDE.md",
		Path.Combine(".github", "copilot-instructions.md"),
		Path.Combine(".github", "suharness-instructions.md"),
		"suharness-instructions.md",
	];
	private static readonly string[] LocalCandidates = ["AGENTS.local.md", "CLAUDE.local.md", Path.Combine(".suharness", "instructions.md")];

	/// <summary>Render the instruction chain, or an empty string when the workspace has none.</summary>
	public static string Render(string workspaceRoot, int maxBytes = DefaultMaxBytes)
	{
		if (maxBytes <= 0) return string.Empty;

		var files = Discover(workspaceRoot);
		if (files.Count == 0) return string.Empty;

		// The most specific file matters most, so broader files are dropped first when over budget.
		var kept = new List<(string Path, string Content)>();
		var used = 0;
		for (var index = files.Count - 1; index >= 0; index--)
		{
			var rendered = $"Instructions from: {files[index].Path}\n\n{files[index].Content}\n\n";
			var cost = Encoding.UTF8.GetByteCount(rendered);
			if (used + cost > maxBytes) continue;
			used += cost;
			kept.Insert(0, files[index]);
		}
		if (kept.Count == 0) return string.Empty;

		var builder = new StringBuilder();
		builder.AppendLine("<workspace-instructions>");
		builder.AppendLine("The following workspace instructions may be relevant to your work. Use them as guidance when "
			+ "applicable. More specific instructions take precedence over broader ones, and later files from the same directory "
			+ "take precedence over earlier ones. They do not override system or direct user instructions.");
		foreach (var file in kept)
		{
			builder.AppendLine();
			builder.AppendLine($"Instructions from: {file.Path}");
			builder.AppendLine();
			builder.AppendLine(file.Content);
		}
		builder.AppendLine("</workspace-instructions>");
		return builder.ToString();
	}

	/// <summary>Candidate files from the project root down to the workspace, broad to specific.</summary>
	private static List<(string Path, string Content)> Discover(string workspaceRoot)
	{
		var found = new List<(string Path, string Content)>();
		string root;
		try { root = Path.GetFullPath(workspaceRoot); }
		catch (ArgumentException) { return found; }
		if (!Directory.Exists(root)) return found;

		foreach (var directory in ChainFromProjectRoot(root))
		{
			// A CLAUDE.md that only duplicates its sibling AGENTS.md is rendered once.
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var name in Candidates.Concat(LocalCandidates))
			{
				var path = Path.Combine(directory, name);
				if (!File.Exists(path)) continue;
				string content;
				try { content = File.ReadAllText(path).Trim(); }
				catch (IOException) { continue; }
				catch (UnauthorizedAccessException) { continue; }
				if (content.Length == 0 || !seen.Add(content)) continue;
				found.Add((Relative(root, path), content));
			}
		}
		return found;
	}

	/// <summary>Directories from the nearest .git ancestor down to the workspace itself.</summary>
	private static List<string> ChainFromProjectRoot(string workspaceRoot)
	{
		var chain = new List<string> { workspaceRoot };
		var current = Directory.GetParent(workspaceRoot);
		while (current is not null)
		{
			chain.Insert(0, current.FullName);
			if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return chain;
			current = current.Parent;
		}
		// No project root above the workspace, so the workspace is the only scope.
		return [workspaceRoot];
	}

	private static string Relative(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
	}
}

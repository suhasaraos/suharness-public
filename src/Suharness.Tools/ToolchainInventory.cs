using System.Collections.Concurrent;
using System.Text;

namespace Suharness.Tools;

/// <summary>Cheap, cached discovery of build tools and project entry points a coding agent otherwise re-probes.</summary>
public static class ToolchainInventory
{
	private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

	public static string Render(string workspaceRoot) => Cache.GetOrAdd(Path.GetFullPath(workspaceRoot), Discover);

	internal static void Clear() => Cache.Clear();

	private static string Discover(string root)
	{
		var builder = new StringBuilder();
		builder.AppendLine("<toolchain>");
		AppendExecutable(builder, "dotnet", FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
		AppendExecutable(builder, "git", FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git"));
		AppendExecutable(builder, "bash", FindBash());
		if (OperatingSystem.IsWindows())
		{
			AppendExecutable(builder, "powershell", FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe")
				?? Existing(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"));
			AppendExecutable(builder, "framework-msbuild", Existing(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe")
				?? Existing(@"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"));
			var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			AppendExecutable(builder, "vswhere", Existing(Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe")));
		}

		var solutions = EntryPoints(root, "*.sln", "*.slnx");
		builder.Append("solutions: ").AppendLine(solutions.Count == 0 ? "none found at workspace root or src/" : string.Join(", ", solutions));
		var projects = EntryPoints(root, "*.csproj");
		builder.Append("top-level-projects: ").AppendLine(projects.Count == 0 ? "none found at workspace root or src/" : string.Join(", ", projects.Take(12)));
		builder.AppendLine("Use these paths directly. Do not recursively search the drive for a tool already listed here.");
		builder.Append("</toolchain>");
		return builder.ToString();
	}

	private static void AppendExecutable(StringBuilder builder, string name, string? path) =>
		builder.Append(name).Append(": ").AppendLine(path ?? "not found");

	private static List<string> EntryPoints(string root, params string[] patterns)
	{
		var result = new List<string>();
		foreach (var directory in new[] { root, Path.Combine(root, "src") }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			foreach (var pattern in patterns)
			{
				try
				{
					result.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
						.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')));
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
			}
		}
		return result.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string? FindBash() => FindOnPath(OperatingSystem.IsWindows() ? "bash.exe" : "bash")
		?? (OperatingSystem.IsWindows() ? Existing(@"C:\Program Files\Git\bin\bash.exe") : Existing("/bin/bash"));

	private static string? FindOnPath(string executable)
	{
		foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			try
			{
				var candidate = Path.Combine(entry.Trim('"'), executable);
				if (File.Exists(candidate)) return candidate;
			}
			catch (ArgumentException) { }
		}
		return null;
	}

	private static string? Existing(string path) => File.Exists(path) ? path : null;
}

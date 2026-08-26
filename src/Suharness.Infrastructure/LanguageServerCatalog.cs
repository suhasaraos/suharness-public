using Suharness.Abstractions;

namespace Suharness.Infrastructure;

/// <summary>
/// The language servers SuHarness knows how to launch without being told, and the rules for
/// combining them with whatever the user declared in <c>lsp.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here downloads or installs anything. A preset is a launch recipe: a command name, its
/// arguments, and the extensions it serves. A preset becomes a real server only if its command
/// already resolves on <c>PATH</c>, so the catalog degrades to nothing on a machine with no
/// toolchains rather than producing servers that fail on first use.
/// </para>
/// <para>
/// Several presets can serve the same language. They are ordered by preference and the first one
/// that resolves wins, so a machine with both <c>pyright</c> and <c>pylsp</c> gets one Python server
/// rather than two competing ones. Built-ins are the floor, never the ceiling: a server declared in
/// <c>lsp.json</c> with the same id replaces the preset outright, any other id is added alongside
/// it, and <c>"useBuiltIns": false</c> turns the whole set off.
/// </para>
/// </remarks>
public static class LanguageServerCatalog
{
	/// <summary>Substituted in preset arguments with a per-workspace directory the server may write to.</summary>
	public const string DataDirectoryToken = "{dataDir}";

	private static readonly Dictionary<string, string> DotNetExtensions = new() { [".cs"] = "csharp" };
	private static readonly Dictionary<string, string> JavaExtensions = new() { [".java"] = "java" };
	private static readonly Dictionary<string, string> PythonExtensions = new()
	{
		[".py"] = "python",
		[".pyi"] = "python",
	};

	/// <param name="Language">
	/// Presets sharing a language are alternatives, tried in listed order. Only the first that
	/// resolves is used.
	/// </param>
	public sealed record Preset(
		string Id,
		string Language,
		string Command,
		IReadOnlyList<string> Args,
		IReadOnlyDictionary<string, string> Extensions,
		int InitializeTimeoutMs = 90_000);

	/// <summary>
	/// The shipped recipes, most preferred first within each language. .NET, Java, and Python are
	/// covered here; every other language is reached by declaring a server in <c>lsp.json</c>, which
	/// is the same mechanism these are built on.
	/// </summary>
	public static IReadOnlyList<Preset> BuiltIns { get; } =
	[
		// .NET. csharp-ls is the lightest to install (`dotnet tool install --global csharp-ls`);
		// the Roslyn server is what Visual Studio Code ships and is the most accurate; OmniSharp is
		// the long-standing fallback still present on many machines.
		new("dotnet-csharp-ls", "dotnet", "csharp-ls", [], DotNetExtensions),
		new("dotnet-roslyn", "dotnet", "Microsoft.CodeAnalysis.LanguageServer",
			["--logLevel", "Warning", "--stdio"], DotNetExtensions, InitializeTimeoutMs: 180_000),
		new("dotnet-omnisharp", "dotnet", "OmniSharp", ["-lsp"], DotNetExtensions),

		// Java. Eclipse JDT needs a writable workspace directory of its own, and it must not be the
		// project directory — it fills it with derived state. The token is replaced with a
		// harness-owned path keyed to the workspace.
		new("java-jdtls", "java", "jdtls", ["-data", DataDirectoryToken], JavaExtensions, InitializeTimeoutMs: 180_000),

		// Python. Pyright is fastest and needs no project configuration; pylsp and jedi are the
		// common alternatives already installed alongside many Python toolchains.
		new("python-pyright", "python", "pyright-langserver", ["--stdio"], PythonExtensions),
		new("python-pylsp", "python", "pylsp", [], PythonExtensions),
		new("python-jedi", "python", "jedi-language-server", [], PythonExtensions),
	];

	/// <summary>
	/// Combine declared servers with the built-in presets. Declared servers always win on id, and a
	/// built-in whose language a declared server already covers is dropped, so adding a hand-written
	/// Python server does not leave a shipped one competing with it.
	/// </summary>
	/// <param name="dataRoot">Harness-owned directory for per-server scratch state, or null to skip presets that need one.</param>
	public static IReadOnlyList<LanguageServerConfig> Resolve(
		IReadOnlyList<LanguageServerConfig> declared,
		bool useBuiltIns,
		string? dataRoot,
		string workspaceRoot)
	{
		var resolved = new List<LanguageServerConfig>(declared.Where(server => server.Enabled));
		if (!useBuiltIns) return resolved;

		var declaredIds = new HashSet<string>(declared.Select(server => server.Id), StringComparer.OrdinalIgnoreCase);
		var claimedExtensions = new HashSet<string>(
			resolved.SelectMany(server => server.Extensions?.Keys ?? []).Select(Normalize),
			StringComparer.OrdinalIgnoreCase);
		var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var preset in BuiltIns)
		{
			// An explicit "enabled": false for a preset id is how a user turns one off, so a declared
			// entry suppresses the preset whether it is enabled or not.
			if (declaredIds.Contains(preset.Id)) continue;
			if (satisfied.Contains(preset.Language)) continue;
			if (preset.Extensions.Keys.Any(extension => claimedExtensions.Contains(Normalize(extension)))) continue;
			if (ResolveExecutable(preset.Command) is null) continue;

			var args = new List<string>();
			var needsData = false;
			foreach (var argument in preset.Args)
			{
				if (!argument.Contains(DataDirectoryToken, StringComparison.Ordinal))
				{
					args.Add(argument);
					continue;
				}
				needsData = true;
				if (dataRoot is null) break;
				args.Add(argument.Replace(DataDirectoryToken, DataDirectory(dataRoot, preset.Id, workspaceRoot), StringComparison.Ordinal));
			}
			if (needsData && dataRoot is null) continue;

			satisfied.Add(preset.Language);
			resolved.Add(new LanguageServerConfig(
				preset.Id,
				preset.Command,
				args,
				preset.Extensions,
				Cwd: workspaceRoot,
				InitializeTimeoutMs: preset.InitializeTimeoutMs));
		}

		return resolved;
	}

	/// <summary>True when this id names a shipped preset rather than a hand-written server.</summary>
	public static bool IsBuiltIn(string id) =>
		BuiltIns.Any(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Resolved command locations, memoised for the life of the process.
	/// </summary>
	/// <remarks>
	/// The catalogue is consulted whenever the tool list is built, which is once per turn, and each
	/// consultation probes every preset command against every <c>PATH</c> directory and every
	/// <c>PATHEXT</c> suffix. That is several hundred filesystem probes per turn to answer a question
	/// whose answer effectively never changes while the process runs.
	/// </remarks>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> Resolved =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Find <paramref name="command"/> on <c>PATH</c>, applying <c>PATHEXT</c> on Windows so that a
	/// server installed as a <c>.cmd</c> shim resolves the same as a native executable. A path with a
	/// directory separator is taken as given.
	/// </summary>
	public static string? ResolveExecutable(string command) =>
		string.IsNullOrWhiteSpace(command) ? null : Resolved.GetOrAdd(command, Probe);

	private static string? Probe(string command)
	{
		try
		{
			if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
			{
				var direct = Path.GetFullPath(command);
				return File.Exists(direct) ? direct : null;
			}

			var extensions = OperatingSystem.IsWindows()
				? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
				: [string.Empty];

			foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
				.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
			{
				foreach (var extension in extensions)
				{
					string candidate;
					try { candidate = Path.Combine(directory.Trim('"'), command + extension); }
					catch (ArgumentException) { continue; }
					if (File.Exists(candidate)) return candidate;
				}
				// A command that already carries its own extension is still a valid candidate.
				string exact;
				try { exact = Path.Combine(directory.Trim('"'), command); }
				catch (ArgumentException) { continue; }
				if (File.Exists(exact)) return exact;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
		return null;
	}

	/// <summary>
	/// A stable per-server, per-workspace scratch directory. It is named, not created: resolving the
	/// catalogue is a pure function, and every server that asks for a data directory creates its own.
	/// </summary>
	private static string DataDirectory(string dataRoot, string presetId, string workspaceRoot) =>
		Path.Combine(Path.GetFullPath(dataRoot), presetId, IndexKey(workspaceRoot));

	private static string IndexKey(string workspaceRoot)
	{
		var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		var normalized = OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical;
		var hash = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
		var leaf = new string([.. Path.GetFileName(canonical).Where(char.IsAsciiLetterOrDigit)]);
		return leaf.Length == 0 ? hash.ToLowerInvariant() : $"{leaf}-{hash.ToLowerInvariant()}";
	}

	private static string Normalize(string extension)
	{
		var value = extension.Trim().ToLowerInvariant();
		return value.StartsWith('.') ? value : "." + value;
	}
}

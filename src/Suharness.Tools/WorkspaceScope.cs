namespace Suharness.Tools;

/// <summary>
/// Resolves model-supplied paths against the session workspace and refuses any path that escapes it.
/// Defines the filesystem resolution and session-working-directory contract: a relative path resolves
/// against the session workspace, and the model always sees workspace-relative display paths.
/// </summary>
public sealed class WorkspaceScope
{
	private static readonly StringComparison PathComparison =
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	private readonly string[] _additionalRoots;

	/// <param name="additionalRoots">
	/// Harness-owned directories that are readable even though they sit outside the workspace. The
	/// spill store is one: an oversized result is only recoverable if the model can read it back.
	/// </param>
	public WorkspaceScope(string root, bool allowOutsideRoot = false, params string[] additionalRoots)
	{
		Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		AllowOutsideRoot = allowOutsideRoot;
		_additionalRoots = [.. additionalRoots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))];
	}

	/// <summary>Canonical absolute workspace root, without a trailing separator.</summary>
	public string Root { get; }

	public bool AllowOutsideRoot { get; }

	/// <summary>
	/// Resolve <paramref name="path"/> to a canonical absolute path inside the workspace.
	/// </summary>
	/// <exception cref="ToolInputException">The path is blank or resolves outside the workspace.</exception>
	public string Resolve(string? path, string parameterName)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ToolInputException($"{parameterName} must be a non-empty string");

		var combined = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
		string full;
		try
		{
			full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new ToolInputException($"{parameterName} is not a valid path: {path}");
		}

		if (!AllowOutsideRoot && !Permitted(full))
			throw new ToolInputException($"cannot access \"{path}\": path escapes the session workspace {Root}");

		// Containment is checked again against what the path actually resolves to. GetFullPath
		// normalises "..", but it does not follow a symlink or an NTFS junction, so a link committed
		// into a repository — or left behind by an earlier command — otherwise reads and writes
		// straight through the workspace boundary. This matters most where there is no shell to fall
		// back on: a ReadOnly session, and the WorkflowCode and WorkflowRepair subagent profiles,
		// have this check and nothing else standing between the model and the rest of the disk.
		if (!AllowOutsideRoot && ResolveLinks(full) is { } target && !Permitted(target))
			throw new ToolInputException($"cannot access \"{path}\": it links outside the session workspace {Root}");

		return full;
	}

	private bool Permitted(string fullPath) =>
		IsInside(fullPath) || _additionalRoots.Any(allowed => IsUnder(fullPath, allowed));

	/// <summary>
	/// Where <paramref name="fullPath"/> really lands, or null when no link redirects it.
	/// </summary>
	/// <remarks>
	/// The walk goes down from the permitted root rather than up from the leaf, which keeps it inside
	/// the paths this scope is responsible for and terminates after one step per segment. Every
	/// segment is resolved, not just the last: a link on an intermediate directory redirects
	/// everything below it, and a file inside a linked directory is not itself a link — so
	/// <c>vendor/nested/secret.txt</c> has to be checked against wherever <c>vendor</c> points.
	/// An entry that cannot be read resolves to nothing, leaving the plain containment check above as
	/// the decision.
	/// </remarks>
	private string? ResolveLinks(string fullPath)
	{
		var baseRoot = IsInside(fullPath)
			? Root
			: _additionalRoots.FirstOrDefault(allowed => IsUnder(fullPath, allowed));
		if (baseRoot is null) return null;

		var resolved = false;
		var current = baseRoot;
		var remainder = fullPath[baseRoot.Length..]
			.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

		foreach (var segment in remainder)
		{
			current = Path.Combine(current, segment);
			string? target;
			try
			{
				target = (Directory.Exists(current)
					? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
					: new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true))?.FullName;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				return null;
			}

			if (target is null) continue;
			resolved = true;
			current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
		}

		return resolved ? current : null;
	}

	/// <summary>Resolve an optional directory argument, defaulting to the workspace root.</summary>
	public string ResolveDirectoryOrRoot(string? path, string parameterName) =>
		string.IsNullOrWhiteSpace(path) ? Root : Resolve(path, parameterName);

	/// <summary>Render a canonical path the way the model should see it: relative to the workspace.</summary>
	public string Display(string fullPath)
	{
		var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
		if (string.Equals(trimmed, Root, PathComparison)) return ".";
		if (!IsInside(trimmed)) return trimmed;
		return trimmed[(Root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
	}

	private bool IsInside(string fullPath) => IsUnder(fullPath, Root);

	private static bool IsUnder(string fullPath, string root) =>
		string.Equals(fullPath, root, PathComparison)
		|| (fullPath.Length > root.Length
			&& fullPath[root.Length] == Path.DirectorySeparatorChar
			&& string.Equals(fullPath[..root.Length], root, PathComparison));
}

/// <summary>
/// A model-facing tool failure. The message is written for the model and is returned as the tool
/// result rather than escaping as an infrastructure error.
/// </summary>
public sealed class ToolInputException(string message) : Exception(message);

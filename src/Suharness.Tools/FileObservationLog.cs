using System.Collections.Concurrent;

namespace Suharness.Tools;

/// <summary>
/// Per-session record of which files the agent has read, and at which version.
/// Enforces the default file observation policy: a mutation
/// is refused unless this session observed the target, and refused again if the file changed since
/// that observation. Both refusals fail closed so a blind overwrite can never silently discard work.
/// </summary>
public sealed class FileObservationLog(bool enforced = true)
{
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private readonly ConcurrentDictionary<string, string> _observed = new(PathComparer);

	/// <summary>False when the session has no read tool, so the policy would refuse every mutation.</summary>
	public bool Enforced { get; } = enforced;

	/// <summary>
	/// A file's observable version. Mirrors the provider's mtime+size version stamp; a missing file
	/// has no version, so a deleted target reads as stale rather than as unobserved.
	/// </summary>
	public static string? VersionOf(string fullPath)
	{
		var info = new FileInfo(fullPath);
		if (!info.Exists) return null;
		return $"{info.LastWriteTimeUtc.Ticks:x}:{info.Length:x}";
	}

	/// <summary>Record that the session has seen <paramref name="fullPath"/> at its present version.</summary>
	public void RecordObserved(string fullPath)
	{
		var version = VersionOf(fullPath);
		if (version is null) _observed.TryRemove(fullPath, out _);
		else _observed[fullPath] = version;
	}

	/// <summary>Forget an observation, so the next mutation must re-read.</summary>
	public void Forget(string fullPath) => _observed.TryRemove(fullPath, out _);

	/// <summary>True when the session has read this exact file version.</summary>
	public bool HasObserved(string fullPath) => _observed.ContainsKey(fullPath);

	/// <summary>
	/// Gate a mutation of an existing file. Throws a model-facing remediation message —
	/// including its remediation clause — when the file was never read or has since changed.
	/// </summary>
	public void RequireFresh(string fullPath, string displayPath, string verb)
	{
		if (!Enforced) return;
		if (!_observed.TryGetValue(fullPath, out var observedVersion))
		{
			throw new ToolInputException(verb == "edit"
				? $"edit requires reading \"{displayPath}\" first — read the file, then retry"
				: $"cannot overwrite existing \"{displayPath}\" without reading it first — read the file, then retry");
		}

		var current = VersionOf(fullPath);
		if (current is null || current != observedVersion)
		{
			throw new ToolInputException(
				$"cannot {verb} \"{displayPath}\": file changed since it was read — re-read the file, then retry");
		}
	}
}

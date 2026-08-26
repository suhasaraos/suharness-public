using System.Text;

namespace Suharness.Tools;

/// <summary>
/// The model-facing <c>write</c> and <c>edit</c> tools, with
/// <c>tool-fs/write</c> and <c>tool-fs/edit</c>. Both are gated by the session observation log, so
/// an existing file cannot be overwritten or edited before this session has read it.
/// </summary>
public static class MutationTools
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>Execute one <c>write</c> call: create or fully replace a UTF-8 text file.</summary>
	public static string Write(WorkspaceScope scope, FileObservationLog observations, string file_path, string content)
	{
		var full = scope.Resolve(file_path, "file_path");
		var display = scope.Display(full);

		if (Directory.Exists(full)) throw new ToolInputException($"cannot write \"{display}\": not a regular file");

		var existed = File.Exists(full);
		if (existed) observations.RequireFresh(full, display, "write");

		var parent = Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
		WriteAtomic(full, content);

		observations.RecordObserved(full);
		var verb = existed ? "Updated" : "Created";
		return $"<path>{display}</path>\n<type>file</type>\n<content>\n{verb} file\n</content>";
	}

	/// <summary>Execute one <c>edit</c> call: replace literal text, unique-match by default.</summary>
	public static string Edit(
		WorkspaceScope scope,
		FileObservationLog observations,
		string file_path,
		string old_string,
		string new_string,
		bool? replace_all)
	{
		if (old_string.Length == 0) throw new ToolInputException("old_string must be a non-empty string");
		if (old_string == new_string) throw new ToolInputException("old_string and new_string must differ");
		var replaceAll = replace_all ?? false;

		var full = scope.Resolve(file_path, "file_path");
		var display = scope.Display(full);

		if (Directory.Exists(full)) throw new ToolInputException($"cannot edit \"{display}\": not a regular file");
		if (!File.Exists(full)) throw new ToolInputException($"cannot edit \"{display}\": not found");

		observations.RequireFresh(full, display, "edit");

		var rawBefore = File.ReadAllText(full);
		if (rawBefore.Contains('\0')) throw new ToolInputException($"cannot edit \"{display}\": binary file");
		var useCrLf = UsesCrLf(rawBefore);
		var before = NormalizeLineEndings(rawBefore);

		var occurrences = CountOccurrences(before, old_string);
		if (occurrences == 0) throw new ToolInputException($"old_string was not found in \"{display}\"");
		if (occurrences > 1 && !replaceAll)
		{
			throw new ToolInputException(
				$"old_string matched {occurrences} times in \"{display}\"; provide a more specific old_string or set replace_all to true");
		}

		var after = replaceAll
			? before.Replace(old_string, new_string, StringComparison.Ordinal)
			: ReplaceFirst(before, old_string, new_string);

		WriteAtomic(full, useCrLf ? NormalizeLineEndings(after).Replace("\n", "\r\n", StringComparison.Ordinal) : after);
		observations.RecordObserved(full);

		return replaceAll
			? $"The file {display} has been updated. All occurrences were successfully replaced."
			: $"The file {display} has been updated successfully.";
	}

	private static int CountOccurrences(string haystack, string needle)
	{
		var count = 0;
		var index = 0;
		while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
		{
			count++;
			index += needle.Length;
		}
		return count;
	}

	private static string ReplaceFirst(string source, string needle, string replacement)
	{
		var index = source.IndexOf(needle, StringComparison.Ordinal);
		return string.Concat(source.AsSpan(0, index), replacement, source.AsSpan(index + needle.Length));
	}

	private static string NormalizeLineEndings(string content) =>
		content.Replace("\r\n", "\n", StringComparison.Ordinal);

	private static bool UsesCrLf(string content)
	{
		var sample = content.AsSpan(0, Math.Min(content.Length, 4096));
		var crLfCount = 0;
		var lfCount = 0;
		for (var index = 0; index < sample.Length; index++)
		{
			if (sample[index] != '\n') continue;
			if (index > 0 && sample[index - 1] == '\r') crLfCount++;
			else lfCount++;
		}
		return crLfCount > lfCount;
	}

	/// <summary>
	/// Write via a sibling temporary file and one move, so a crash mid-write leaves the original
	/// intact rather than a half-written file.
	/// </summary>
	private static void WriteAtomic(string fullPath, string content)
	{
		var temporary = fullPath + ".suharness.tmp";
		try
		{
			File.WriteAllText(temporary, content, Utf8NoBom);
			File.Move(temporary, fullPath, overwrite: true);
		}
		catch
		{
			if (File.Exists(temporary)) { try { File.Delete(temporary); } catch { /* best effort */ } }
			throw;
		}
	}
}

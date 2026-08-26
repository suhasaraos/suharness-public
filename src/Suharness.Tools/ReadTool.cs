using System.Text;

namespace Suharness.Tools;

/// <summary>One numbered line in a read window.</summary>
public readonly record struct ReadLine(int Number, string Text);

/// <summary>The bounded window one <c>read</c> call returns.</summary>
public sealed record ReadWindow(IReadOnlyList<ReadLine> Lines, int TotalLines, bool TruncatedByBytes);

/// <summary>
/// The model-facing <c>read</c> tool, with stable argument names, caps, a line-numbered envelope,
/// and a continuation footer.
/// </summary>
public static class ReadTool
{
	/// <summary>Default and maximum lines returned by one call.</summary>
	public const int Limit = 2000;

	/// <summary>Maximum characters returned for a single line.</summary>
	public const int MaxLineLength = 2000;

	/// <summary>Maximum bytes returned for the selected lines.</summary>
	public const int MaxBytes = 50 * 1024;

	/// <summary>Validate the constraints the JSON schema cannot express and apply defaults.</summary>
	public static (int Offset, int Limit) ParseArgs(int? offset, int? limit)
	{
		var resolvedOffset = offset is null ? 1 : PositiveInteger(offset.Value, "offset");
		var resolvedLimit = limit is null ? Limit : PositiveInteger(limit.Value, "limit");
		if (resolvedLimit > Limit)
			throw new ToolInputException($"limit must be less than or equal to {Limit}");
		return (resolvedOffset, resolvedLimit);
	}

	private static int PositiveInteger(int value, string name) =>
		value >= 1 ? value : throw new ToolInputException($"{name} must be a positive integer");

	/// <summary>
	/// Build one window from a file, enforcing the line and byte caps while still scanning to an
	/// exact total line count. Splits on '\n' and strips a trailing '\r' for consistent line endings.
	/// </summary>
	public static ReadWindow BuildWindow(TextReader reader, int offset, int limit, string displayPath)
	{
		var lines = new List<ReadLine>();
		var totalLines = 0;
		var outputBytes = 0;
		var truncatedByBytes = false;
		var lineBuffer = new StringBuilder();
		var lineBufferCap = MaxLineLength + 1;

		void Consume(string rawLine)
		{
			totalLines++;
			if (truncatedByBytes || totalLines < offset || lines.Count >= limit) return;

			var text = rawLine.Length > MaxLineLength
				? $"{rawLine[..MaxLineLength]}... (line truncated to {MaxLineLength} chars)"
				: rawLine;
			var bytes = Encoding.UTF8.GetByteCount(text) + (lines.Count > 0 ? 1 : 0);
			if (outputBytes + bytes > MaxBytes) { truncatedByBytes = true; return; }
			outputBytes += bytes;
			lines.Add(new ReadLine(totalLines, text));
		}

		void Append(ReadOnlySpan<char> segment)
		{
			if (lineBuffer.Length >= lineBufferCap) return;
			var room = lineBufferCap - lineBuffer.Length;
			lineBuffer.Append(segment.Length > room ? segment[..room] : segment);
		}

		void Flush()
		{
			var raw = lineBuffer.ToString();
			if (raw.EndsWith('\r')) raw = raw[..^1];
			Consume(raw);
			lineBuffer.Clear();
		}

		var buffer = new char[16 * 1024];
		int read;
		while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
		{
			var chunk = buffer.AsSpan(0, read);
			var start = 0;
			int newline;
			while ((newline = chunk[start..].IndexOf('\n')) != -1)
			{
				Append(chunk.Slice(start, newline));
				Flush();
				start += newline + 1;
			}
			Append(chunk[start..]);
		}
		if (lineBuffer.Length > 0) Flush();

		if (!truncatedByBytes && offset > totalLines && !(totalLines == 0 && offset == 1))
			throw new ToolInputException($"offset {offset} is out of range for \"{displayPath}\" ({totalLines} lines)");

		return new ReadWindow(lines, totalLines, truncatedByBytes);
	}

	/// <summary>Render the stable model-facing envelope.</summary>
	public static string Format(string displayPath, int offset, ReadWindow window)
	{
		var endLine = window.Lines.Count > 0 ? window.Lines[^1].Number : Math.Max(0, offset - 1);
		var footer = window.TruncatedByBytes
			? $"(Output capped. Showing lines {offset}-{endLine}. Use offset={endLine + 1} to continue.)"
			: endLine < window.TotalLines
				? $"(Showing lines {offset}-{endLine} of {window.TotalLines}. Use offset={endLine + 1} to continue.)"
				: $"(End of file - total {window.TotalLines} lines)";

		var body = window.Lines.Count > 0
			? string.Join('\n', window.Lines.Select(line => $"{line.Number}: {line.Text}")) + "\n\n" + footer
			: footer;

		return $"<path>{displayPath}</path>\n<type>file</type>\n<content>\n{body}\n</content>";
	}

	/// <summary>Execute one <c>read</c> call end to end and record the observation.</summary>
	public static string Execute(WorkspaceScope scope, FileObservationLog observations, string file_path, int? offset, int? limit)
	{
		var (resolvedOffset, resolvedLimit) = ParseArgs(offset, limit);
		var full = scope.Resolve(file_path, "file_path");
		var display = scope.Display(full);

		if (Directory.Exists(full)) throw new ToolInputException($"cannot read \"{display}\": not a regular file");
		if (!File.Exists(full)) throw new ToolInputException($"cannot read \"{display}\": not found");

		using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		var window = BuildWindow(reader, resolvedOffset, resolvedLimit, display);

		observations.RecordObserved(full);
		return Format(display, resolvedOffset, window);
	}
}

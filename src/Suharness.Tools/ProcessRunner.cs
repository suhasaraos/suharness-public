using System.Diagnostics;
using System.Text;

namespace Suharness.Tools;

/// <summary>
/// Runs one child process to completion under a deadline and returns what it wrote.
/// </summary>
/// <remarks>
/// The <c>bash</c> and <c>powershell</c> tools had a copy of this each. They had not drifted, but
/// having two meant the parts that are easy to get subtly wrong — killing the whole process tree on
/// timeout, distinguishing a timeout from the caller cancelling, flushing the async readers before
/// reading the exit code — had to be got right twice and would have to be fixed twice.
/// </remarks>
public static class ProcessRunner
{
	/// <summary>Timeout applied when the caller does not ask for one.</summary>
	public const int DefaultTimeoutMs = 120_000;

	/// <summary>Largest timeout accepted; longer requests are clamped to it.</summary>
	public const int MaxTimeoutMs = 600_000;

	/// <summary>Output kept per stream, counted from the end.</summary>
	public const int MaxStreamCharacters = 30_000;

	/// <param name="TimedOut">
	/// The deadline expired and the process tree was killed. Distinct from a non-zero exit: the
	/// command did not decide to stop, so its exit code says nothing about the work.
	/// </param>
	public sealed record Result(string Stdout, string Stderr, int ExitCode, bool TimedOut, int TimeoutMs);

	/// <summary>
	/// Start <paramml name="executable"/> with <paramref name="arguments"/>, wait for it under the
	/// deadline, and capture both streams.
	/// </summary>
	/// <remarks>
	/// Arguments are passed as a list rather than a command line, so nothing in them is re-parsed by
	/// a shell on the way in. Where a shell is wanted it is the executable, and the script is one
	/// argument to it.
	/// </remarks>
	public static async Task<Result> RunAsync(
		string executable,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		int timeoutMs,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo(executable)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

		using var process = new Process { StartInfo = startInfo };
		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		process.OutputDataReceived += (_, args) => { if (args.Data is not null) stdout.AppendLine(args.Data); };
		process.ErrorDataReceived += (_, args) => { if (args.Data is not null) stderr.AppendLine(args.Data); };

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		// Nothing is written to the child, and leaving the pipe open would hang a command that reads.
		process.StandardInput.Close();

		var timedOut = false;
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeoutMs);
		try
		{
			await process.WaitForExitAsync(deadline.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			timedOut = true;
			TryKill(process);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		// Let the async readers drain: without this the tail of the output can be lost.
		try { process.WaitForExit(2_000); } catch (SystemException) { /* already gone */ }

		return new Result(
			Tail(stdout.ToString()),
			Tail(stderr.ToString()),
			timedOut ? -1 : ExitCodeOf(process),
			timedOut,
			timeoutMs);
	}

	/// <summary>Render a result in the stable, model-facing format both shell tools return.</summary>
	public static string Render(Result result)
	{
		var body = result.Stdout;
		if (result.Stderr.Length > 0)
		{
			if (body.Length > 0 && !body.EndsWith('\n')) body += "\n";
			body += $"[stderr]\n{result.Stderr}";
		}
		if (body.Length == 0) body = "(no output)";

		var markers = new List<string>();
		if (result.TimedOut) markers.Add($"[timed out after {result.TimeoutMs}ms]");
		if (result.ExitCode != 0 && !result.TimedOut) markers.Add($"[exit code: {result.ExitCode}]");
		if (markers.Count == 0) return body;

		if (!body.EndsWith('\n')) body += "\n";
		return body + string.Join('\n', markers);
	}

	/// <summary>
	/// Find an executable on PATH, then at the given fallback locations. The fallbacks exist because
	/// the shells are routinely installed somewhere PATH does not mention.
	/// </summary>
	public static string? Locate(IReadOnlyList<string> executables, IReadOnlyList<string> fallbacks)
	{
		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
		foreach (var executable in executables)
		{
			foreach (var directory in directories)
			{
				try
				{
					var candidate = Path.Combine(directory.Trim('"'), executable);
					if (File.Exists(candidate)) return candidate;
				}
				catch (ArgumentException) { /* an unusable PATH entry */ }
			}
		}
		return fallbacks.FirstOrDefault(File.Exists);
	}

	private static string Tail(string text) =>
		text.Length <= MaxStreamCharacters
			? text
			: $"[output truncated to the last {MaxStreamCharacters} characters]\n{text[^MaxStreamCharacters..]}";

	private static int ExitCodeOf(Process process)
	{
		try { return process.ExitCode; }
		catch (InvalidOperationException) { return -1; }
	}

	private static void TryKill(Process process)
	{
		try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
		catch (SystemException) { /* the process already exited */ }
	}
}

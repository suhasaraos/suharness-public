namespace Suharness.Tools;

/// <summary>
/// The model-facing <c>bash</c> tool. Each call runs in a fresh <c>bash -c</c>, so no state persists
/// between calls, and the result carries stable
/// <c>[exit code: N]</c> / <c>[timed out after Nms]</c> markers the model is taught to check.
/// </summary>
public static class BashTool
{
	/// <summary>Timeout applied when the model does not pass <c>timeoutMs</c>.</summary>
	public const int DefaultTimeoutMs = ProcessRunner.DefaultTimeoutMs;

	/// <summary>Largest timeout accepted; longer requests are clamped to it.</summary>
	public const int MaxTimeoutMs = ProcessRunner.MaxTimeoutMs;

	/// <summary>Output is truncated to this many trailing characters per stream.</summary>
	public const int MaxStreamCharacters = ProcessRunner.MaxStreamCharacters;

	/// <summary>The tool description used for this agent composition.</summary>
	public const string Description =
		"Execute a bash command (`bash -c`) and return its stdout/stderr. "
		+ "Each call runs in a fresh shell: no state (cwd, variables, functions) persists between calls — "
		+ "pass `workdir` instead of using `cd`. Non-zero exits are reported as `[exit code: N]`. "
		+ "Long output is truncated to its tail. "
		+ "Background execution is not available; long-running commands must finish within the timeout.";

	/// <summary>Execute one <c>bash</c> call and render its result in the model-facing format.</summary>
	public static async Task<string> ExecuteAsync(
		WorkspaceScope scope,
		string command,
		int? timeoutMs,
		string? workdir,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(command)) throw new ToolInputException("command must be a non-empty string");

		var directory = scope.ResolveDirectoryOrRoot(workdir, "workdir");
		if (!Directory.Exists(directory))
			throw new ToolInputException($"cannot run in \"{scope.Display(directory)}\": not a directory");

		var shell = ResolveBash()
			?? throw new ToolInputException("bash was not found on this system; install bash or Git for Windows to use the bash tool");

		var result = await ProcessRunner.RunAsync(
			shell,
			["-c", command],
			directory,
			Math.Clamp(timeoutMs ?? DefaultTimeoutMs, 1, MaxTimeoutMs),
			cancellationToken);
		return ProcessRunner.Render(result);
	}

	/// <summary>Render stdout, stderr, and status markers in a stable format.</summary>
	public static string Render(string stdout, string stderr, int exitCode, bool timedOut, int timeoutMs) =>
		ProcessRunner.Render(new ProcessRunner.Result(stdout, stderr, exitCode, timedOut, timeoutMs));

	/// <summary>Locate bash: PATH first, then the standard Git for Windows install locations.</summary>
	private static string? ResolveBash() => OperatingSystem.IsWindows()
		? ProcessRunner.Locate(["bash.exe"], [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe"])
		: ProcessRunner.Locate(["bash"], ["/bin/bash", "/usr/bin/bash"]);
}

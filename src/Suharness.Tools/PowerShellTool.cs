namespace Suharness.Tools;

/// <summary>Native PowerShell execution for Windows workspaces and Windows-only build tooling.</summary>
public static class PowerShellTool
{
	public const string Description =
		"Execute a native PowerShell command (`pwsh -NoProfile -NonInteractive -Command`) and return stdout/stderr. "
		+ "Use this on Windows for MSBuild, Visual Studio, registry, and PowerShell cmdlets; use bash for POSIX commands. "
		+ "Each call runs in a fresh process, so pass workdir instead of relying on a previous Set-Location. "
		+ "Non-zero exits are reported as `[exit code: N]`; long-running commands must finish within the timeout.";

	public static async Task<string> ExecuteAsync(
		WorkspaceScope scope,
		string command,
		int? timeoutMs,
		string? workdir,
		CancellationToken cancellationToken)
	{
		if (!OperatingSystem.IsWindows()) throw new ToolInputException("the powershell tool is available only on Windows");
		if (string.IsNullOrWhiteSpace(command)) throw new ToolInputException("command must be a non-empty string");

		var directory = scope.ResolveDirectoryOrRoot(workdir, "workdir");
		if (!Directory.Exists(directory))
			throw new ToolInputException($"cannot run in \"{scope.Display(directory)}\": not a directory");

		var shell = ResolvePowerShell()
			?? throw new ToolInputException("PowerShell was not found; install PowerShell 7 or ensure powershell.exe is available");

		var result = await ProcessRunner.RunAsync(
			shell,
			["-NoProfile", "-NonInteractive", "-Command", command],
			directory,
			Math.Clamp(timeoutMs ?? ProcessRunner.DefaultTimeoutMs, 1, ProcessRunner.MaxTimeoutMs),
			cancellationToken);
		return ProcessRunner.Render(result);
	}

	/// <summary>Prefer PowerShell 7, then Windows PowerShell at its fixed system location.</summary>
	private static string? ResolvePowerShell() => ProcessRunner.Locate(
		["pwsh.exe", "powershell.exe"],
		[@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"]);
}

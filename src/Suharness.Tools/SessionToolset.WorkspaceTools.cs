using Suharness.Abstractions;
using System.ComponentModel;
using System.Text;

namespace Suharness.Tools;

/// <summary>
/// The tools that act on the session workspace: reading and searching files, mutating them, and
/// running a command. Every path argument is resolved through <see cref="WorkspaceScope"/>, which
/// is what keeps a model-supplied path from escaping the session's workspace.
/// </summary>
public sealed partial class SessionToolset
{
	/// <summary>Declarations returned by one <c>symbol</c> call before the list is cut.</summary>
	private const int SymbolResultLimit = 30;

	private string Read(
		[Description("Path to read, resolved by the filesystem backend.")] string file_path,
		[Description("1-based first line to return. Defaults to 1.")] int? offset = null,
		[Description("Maximum number of lines to return. Defaults to 2000.")] int? limit = null) =>
		CodingWorkflowWorkspaceGate()
		?? WorkflowReviewerReadGate(file_path)
		?? Guard(() => ReadTool.Execute(Scope, _observations, file_path, offset, limit));

	private string Write(
		[Description("Path to write, resolved by the filesystem backend.")] string file_path,
		[Description("Full UTF-8 text content to write.")] string content) =>
		CodingWorkflowWorkspaceGate() ?? Guard(() =>
		{
			if (ToolProfile is AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair
				&& File.Exists(Scope.Resolve(file_path, nameof(file_path))))
				return "Error: Workflow write can create new files only. This file already exists; read it and use edit for an exact replacement.";
			var result = MutationTools.Write(Scope, _observations, file_path, content);
			RecordIndexed(file_path);
			return result;
		});

	private string Edit(
		[Description("Path to edit, resolved by the filesystem backend.")] string file_path,
		[Description("Literal text to replace. Must match exactly.")] string old_string,
		[Description("Literal replacement text. Use an empty string to delete the match.")] string new_string,
		[Description("Replace all matches. Defaults to false; when false, old_string must appear exactly once.")] bool? replace_all = null) =>
		CodingWorkflowWorkspaceGate() ?? Guard(() =>
		{
			var result = MutationTools.Edit(Scope, _observations, file_path, old_string, new_string, replace_all);
			RecordIndexed(file_path);
			return result;
		});

	private string Glob(
		[Description("Glob pattern to match file paths against (e.g. \"**/*.cs\", \"src/**/*.test.js\"). "
			+ "A pattern with no \"/\" matches the basename at any depth, so \"*\" and \"*.cs\" both search the whole tree; "
			+ "include a separator to anchor the depth.")] string pattern,
		[Description("Directory to search in. Defaults to the session workspace; a relative path resolves against it.")] string? path = null,
		CancellationToken cancellationToken = default) =>
		CodingWorkflowWorkspaceGate()
		?? WorkflowReviewerDiscoveryGate()
		?? Guard(() => SearchTools.Glob(Scope, pattern, path, cancellationToken, index: _workspaceIndex));

	private string Grep(
		[Description("Regular expression to search for.")] string pattern,
		[Description("File or directory to search. Defaults to the session workspace; a relative path resolves against it.")] string? path = null,
		[Description("One glob filter for which files to search (e.g. \"*.cs\", \"*.{js,jsx}\"). Not a list; negation is not supported.")] string? include = null,
		CancellationToken cancellationToken = default) =>
		CodingWorkflowWorkspaceGate()
		?? WorkflowReviewerDiscoveryGate()
		?? Guard(() => SearchTools.Grep(Scope, pattern, path, include, cancellationToken, index: _workspaceIndex));

	/// <summary>
	/// Find a declaration by name. This is the tool to reach for when a request names a type, method,
	/// or function: it answers from a table of declarations rather than by matching text, so it does
	/// not depend on guessing how the name is capitalised or where the file sits.
	/// </summary>
	private string Symbol(
		[Description("Symbol name to find. Qualify it (\"PlayerController.Move\") to narrow by the enclosing type. "
			+ "Matching is case-insensitive and tolerates a partial name.")] string name,
		[Description("Optional kind filter, for example \"class\", \"interface\", \"method\", \"function\", \"type\".")] string? kind = null,
		[Description("Maximum results. Defaults to 30.")] int? limit = null,
		CancellationToken cancellationToken = default) =>
		WorkflowReviewerDiscoveryGate() ?? Guard(() =>
		{
			if (_symbols is null) return "Error: this session has no symbol index.";
			if (string.IsNullOrWhiteSpace(name)) return "Error: name must be a non-empty string.";
			var cap = Math.Clamp(limit ?? SymbolResultLimit, 1, 200);
			var hits = _symbols.Find(_workspaceRoot, name, kind, cap, cancellationToken);
			return FormatSymbols(name, kind, hits, cap);
		});

	private static string FormatSymbols(string name, string? kind, IReadOnlyList<SymbolHit> hits, int cap)
	{
		if (hits.Count == 0)
		{
			var filter = string.IsNullOrWhiteSpace(kind) ? string.Empty : $" of kind \"{kind}\"";
			return $"No declarations found matching \"{name}\"{filter}.\n\n"
				+ "This index holds declarations only. Use grep to search for other text, or glob to find files by path.";
		}

		var builder = new StringBuilder();
		builder.Append(hits.Count == 1 ? "Found 1 declaration" : $"Found {hits.Count} declarations");
		builder.Append(" matching \"").Append(name).Append("\"\n\n");
		foreach (var hit in hits)
		{
			builder.Append(hit.RelativePath).Append(':').Append(hit.Line)
				.Append("  ").Append(hit.Kind).Append(' ').Append(hit.Name);
			if (!string.IsNullOrEmpty(hit.Container)) builder.Append("  (in ").Append(hit.Container).Append(')');
			builder.Append('\n');
		}
		if (hits.Count >= cap)
		{
			builder.Append("\n(Showing the first ").Append(cap).Append(" matches; qualify the name or set a kind to narrow.)\n");
		}
		return builder.ToString().TrimEnd('\n');
	}

	private string Skill(
		[Description("Name of an available skill from the tool description.")] string name) =>
		SkillCatalog.Load(_workspaceRoot, name);

	/// <summary>
	/// Semantic navigation through a language server. Unlike <c>symbol</c> this resolves meaning
	/// rather than matching text, so it can distinguish overloads and follow a reference to the
	/// declaration it actually binds to — at the cost of needing a project the server can load.
	/// </summary>
	private async Task<string> Lsp(
		[Description("definition | references | implementation | hover | document_symbols | symbol")] string operation,
		[Description("Source file path, resolved against the session workspace. Required for every operation except "
			+ "\"symbol\", where it is optional and only selects which language server answers.")] string? file_path = null,
		[Description("Name of the symbol to ask about, used instead of line and column. The position is resolved from "
			+ "the declaration index. For operation \"symbol\" this is the name to search the whole project for.")] string? symbol = null,
		[Description("One-based source line. Give this with column, or give symbol instead.")] int? line = null,
		[Description("One-based UTF-16 column.")] int? column = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (WorkflowReviewerDiscoveryGate() is { } reviewerRefusal) return reviewerRefusal;
			if (_languageServers is null) return "Error: this session has no language servers.";
			var mode = operation?.Trim().ToLowerInvariant() ?? string.Empty;

			if (mode == "symbol")
			{
				if (string.IsNullOrWhiteSpace(symbol)) return "Error: symbol must name what to search for.";
				var hint = string.IsNullOrWhiteSpace(file_path) ? null : Scope.Resolve(file_path, nameof(file_path));
				return await _languageServers.SymbolsAsync(Scope.Root, symbol, hint, cancellationToken);
			}

			if (string.IsNullOrWhiteSpace(file_path)) return "Error: file_path is required for this operation.";
			var path = Scope.Resolve(file_path, nameof(file_path));

			int resolvedLine;
			int resolvedColumn;
			if (mode is "document_symbols" or "documentsymbols")
			{
				// The whole document is the subject, so no position is needed or used.
				resolvedLine = 1;
				resolvedColumn = 1;
			}
			else if (line is not null && column is not null)
			{
				if (line < 1 || column < 1) return "Error: line and column must be positive one-based integers.";
				resolvedLine = line.Value;
				resolvedColumn = column.Value;
			}
			else if (!string.IsNullOrWhiteSpace(symbol))
			{
				if (Locate(path, symbol) is not { } position)
				{
					return $"Error: could not find a declaration of \"{symbol}\" in \"{Scope.Display(path)}\". "
						+ "Use the symbol tool to find where it is declared, or pass line and column.";
				}
				(resolvedLine, resolvedColumn) = position;
			}
			else
			{
				return "Error: give either symbol, or both line and column.";
			}

			return await _languageServers.QueryAsync(
				Scope.Root, mode, path, resolvedLine - 1, resolvedColumn - 1, cancellationToken);
		}
		catch (Exception exception) when (exception is ToolInputException or InvalidOperationException
			or IOException or UnauthorizedAccessException or TimeoutException or ArgumentOutOfRangeException)
		{
			return $"Error: {exception.Message}";
		}
	}

	/// <summary>
	/// Turn a symbol name into a position in one file. The declaration index knows the line; the
	/// column comes from finding the name on it, because a language server binds to an identifier and
	/// not to the start of its line.
	/// </summary>
	private (int Line, int Column)? Locate(string fullPath, string symbol)
	{
		var wanted = symbol.Split(['.', ':', '/', '#'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
		if (string.IsNullOrEmpty(wanted)) return null;

		var declarations = _symbols?.InFile(_workspaceRoot, Scope.Display(fullPath));
		var match = declarations?.FirstOrDefault(hit => string.Equals(hit.Name, wanted, StringComparison.Ordinal))
			?? declarations?.FirstOrDefault(hit => string.Equals(hit.Name, wanted, StringComparison.OrdinalIgnoreCase));
		if (match is null) return null;

		try
		{
			var lines = File.ReadLines(fullPath).Skip(match.Line - 1).Take(1).ToArray();
			if (lines.Length == 0) return (match.Line, 1);
			var column = lines[0].IndexOf(match.Name, StringComparison.Ordinal);
			return (match.Line, column < 0 ? 1 : column + 1);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (match.Line, 1);
		}
	}

	/// <summary>
	/// Fold a mutation the harness just made into the workspace table, so the next search sees it
	/// without waiting for the freshness window to lapse.
	/// </summary>
	private void RecordIndexed(string filePath)
	{
		if (_workspaceIndex is null) return;
		try { _workspaceIndex.Record(_workspaceRoot, Scope.Resolve(filePath, nameof(filePath))); }
		catch (ToolInputException)
		{
			// The mutation already succeeded, so a path the scope now refuses is not worth failing on.
		}
	}

	private async Task<string> Bash(
		[Description("The bash command to execute.")] string command,
		[Description("Clear, concise description of what this command does in active voice, 5-10 words (shown in the UI).")] string description,
		[Description("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry.")] int? timeoutMs = null,
		[Description("Working directory for this command. Defaults to the session workspace; a relative path is resolved against it.")] string? workdir = null,
		CancellationToken cancellationToken = default)
	{
		_ = description;
		if (CodingWorkflowWorkspaceGate() is { } blocked) return blocked;
		if (WorkflowVerifyCommandGate(command) is { } refusal) return refusal;
		try
		{
			return await BashTool.ExecuteAsync(Scope, command, timeoutMs, workdir, cancellationToken);
		}
		catch (ToolInputException exception)
		{
			return $"Error: {exception.Message}";
		}
		finally
		{
			// A shell command can change the tree in ways nothing else reports, so the table is marked
			// for confirmation rather than trusted through its freshness window.
			_workspaceIndex?.Invalidate(_workspaceRoot);
		}
	}

	private async Task<string> WorkspaceChanges(CancellationToken cancellationToken = default)
	{
		if (ToolProfile == AgentToolProfile.WorkflowReadOnly
			&& Interlocked.CompareExchange(ref _workflowReviewWorkspaceChangesCalls, 1, 0) != 0)
			return "Error: Workflow Reviewer must call workspace_changes exactly once. Read each untracked source or test path reported by the original evidence directly using its workspace-relative path; do not rediscover files.";
		if (ToolProfile == AgentToolProfile.WorkflowVerify
			&& Interlocked.CompareExchange(ref _workflowVerifyWorkspaceChangesCalls, 1, 0) != 0)
			return "Error: Workflow Verifier must call workspace_changes exactly once. Continue with the native build and focused tests using the evidence already returned.";
		try
		{
			var status = await ProcessRunner.RunAsync(
				"git", ["status", "--short", "--untracked-files=all"], _workspaceRoot, 60_000, cancellationToken);
			if (status.ExitCode != 0 || status.TimedOut)
				return "Error: git status failed.\n" + ProcessRunner.Render(status);

			var diff = await ProcessRunner.RunAsync(
				"git", ["diff", "--no-ext-diff", "--unified=3", "--", "."], _workspaceRoot, 60_000, cancellationToken);
			if (diff.ExitCode != 0 || diff.TimedOut)
				return "Error: git diff failed.\n" + ProcessRunner.Render(diff);

			var output = new StringBuilder(status.Stdout);
			if (output.Length > 0 && output[^1] != '\n') output.AppendLine();
			output.AppendLine("--- TRACKED DIFF ---");
			output.Append(diff.Stdout);
			return output.ToString();
		}
		catch (SystemException exception) when (exception is not OperationCanceledException)
		{
			return $"Error: could not run git: {exception.Message}";
		}
	}

	private string? WorkflowReviewerReadGate(string filePath)
	{
		if (ToolProfile != AgentToolProfile.WorkflowReadOnly
			|| Volatile.Read(ref _workflowReviewWorkspaceChangesCalls) != 1)
			return null;
		return Path.IsPathFullyQualified(filePath)
			? "Error: Workflow Reviewer must read changed untracked files using the workspace-relative path reported by workspace_changes, not a scratch or absolute path. Retry the read with that relative path; do not call glob, grep, symbol, or lsp."
			: null;
	}

	private string? WorkflowReviewerDiscoveryGate() =>
		ToolProfile == AgentToolProfile.WorkflowReadOnly
		&& Volatile.Read(ref _workflowReviewWorkspaceChangesCalls) == 1
			? "Error: Workflow Reviewer discovery is blocked after workspace_changes. Inspect tracked changes from the returned diff and read each untracked source or test file directly using the workspace-relative path already reported there."
			: null;

	private async Task<string> PowerShell(
		[Description("The PowerShell command to execute.")] string command,
		[Description("Clear, concise description of what this command does in active voice, 5-10 words (shown in the UI).")] string description,
		[Description("Timeout in milliseconds. The executor applies its configured default and cap, and kills the command on expiry.")] int? timeoutMs = null,
		[Description("Working directory for this command. Defaults to the session workspace; a relative path is resolved against it.")] string? workdir = null,
		CancellationToken cancellationToken = default)
	{
		_ = description;
		if (CodingWorkflowWorkspaceGate() is { } blocked) return blocked;
		if (WorkflowVerifyCommandGate(command) is { } refusal) return refusal;
		try
		{
			return await PowerShellTool.ExecuteAsync(Scope, command, timeoutMs, workdir, cancellationToken);
		}
		catch (ToolInputException exception)
		{
			return $"Error: {exception.Message}";
		}
		finally
		{
			_workspaceIndex?.Invalidate(_workspaceRoot);
		}
	}

	private string? WorkflowVerifyCommandGate(string command)
	{
		if (ToolProfile != AgentToolProfile.WorkflowVerify) return null;
		if (Interlocked.Increment(ref _workflowVerifyCommandAttempts) > WorkflowVerifyCommandAttemptBudget)
			return WorkflowVerifyCommandBudgetExhausted();
		if (Volatile.Read(ref _workflowVerifyCommandCalls) >= WorkflowVerifyCommandBudget)
			return WorkflowVerifyCommandBudgetExhausted();
		if (Volatile.Read(ref _workflowVerifyWorkspaceChangesCalls) != 1)
			return "Error: Workflow Verifier must call the workspace_changes tool exactly once before any PowerShell validation command. Do not run git status or git diff through PowerShell.";
		var mutationCommand = System.Text.RegularExpressions.Regex.IsMatch(
			command,
			@"\b(?:Set-Content|Add-Content|Out-File|WriteAllText|WriteAllBytes|Move-Item|Rename-Item|New-Item|Copy-Item)\b|(?:^|[;&|]\s*)(?:cp|mv|sed|perl|python)\b",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		var scratchOnlyMutation = !string.IsNullOrWhiteSpace(_scratchRoot)
			&& command.Contains(_scratchRoot, StringComparison.OrdinalIgnoreCase)
			&& !command.Contains(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
			&& !System.Text.RegularExpressions.Regex.IsMatch(command, @"\b(?:Copy-Item|Move-Item|Rename-Item|cp|mv)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (mutationCommand && !scratchOnlyMutation)
			return "Error: Workflow Verifier commands are validation-only and cannot mutate workspace files. Report the concrete source finding for Junior repair.";
		if (mutationCommand && scratchOnlyMutation)
		{
			var runnerSourceCommand = command.Contains("Runner.cs", StringComparison.OrdinalIgnoreCase);
			if (runnerSourceCommand
				&& (System.Text.RegularExpressions.Regex.IsMatch(command, @"\.\s*GetTypes\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
					|| !System.Text.RegularExpressions.Regex.IsMatch(command, @"\.\s*GetType\s*\(\s*(?:\""|'')", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
					|| !command.Contains("Path.Combine(AppDomain.CurrentDomain.BaseDirectory", StringComparison.Ordinal)
					|| !command.Contains("GetCustomAttributes(typeof(TestAttribute)", StringComparison.Ordinal)
					|| command.Contains("?.", StringComparison.Ordinal)))
				return "Error: Runner.cs must target exactly one named fixture with assembly.GetType(\"fully.qualified.Fixture\"), use AppDomain.CurrentDomain.BaseDirectory for the test DLL, select methods with TestAttribute, and use syntax supported by the legacy Framework compiler. Do not call Assembly.GetTypes, enumerate unrelated fixtures, or use modern syntax such as ?.; use ex.GetBaseException().Message instead. Rewrite the complete Runner.cs in scratch before compiling.";
			if (runnerSourceCommand)
				Volatile.Write(ref _workflowVerifyRunnerSourceAccepted, 1);
			return Interlocked.Increment(ref _workflowVerifyCommandCalls) <= WorkflowVerifyCommandBudget
				? null
				: WorkflowVerifyCommandBudgetExhausted();
		}
		if (System.Text.RegularExpressions.Regex.IsMatch(
			command,
			@"\bAdd-Type\b|\[System\.Reflection\.|\bAssembly\]::Load|\bActivator\]::CreateInstance|\bNew-Object\b",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			return "Error: do not execute tests through PowerShell reflection, Add-Type, or New-Object because PowerShell uses the wrong CLR. Compile Runner.exe with Framework csc.exe into the test bin directory and execute that exe.";
		var compilerInvocation = System.Text.RegularExpressions.Regex.IsMatch(command, @"\bcsc(?:\.exe)?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
			|| command.Contains("/target:exe", StringComparison.OrdinalIgnoreCase)
				&& command.Contains("/out:", StringComparison.OrdinalIgnoreCase)
				&& command.Contains("/reference:", StringComparison.OrdinalIgnoreCase);
		if (compilerInvocation)
		{
			if (Volatile.Read(ref _workflowVerifyRunnerSourceAccepted) != 1)
				return "Error: Runner.cs has not been accepted yet. After the native build succeeds, the first post-build command must write the complete fixture-scoped Runner.cs in session scratch; only then may the next command compile Runner.exe.";
			var output = System.Text.RegularExpressions.Regex.Match(
				command,
				@"/out:(?:""(?<path>[^""]+)""|'(?<path>[^']+)'|(?<path>\S+))",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var outputPath = output.Groups["path"].Value;
			var outDirectory = System.Text.RegularExpressions.Regex.Match(
				command,
				@"\$outDir\s*=\s*(?:""(?<path>[^""]+)""|'(?<path>[^']+)')",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups["path"].Value;
			var verifiedOutDirectory = outputPath.StartsWith("$outDir\\", StringComparison.OrdinalIgnoreCase)
				&& outDirectory.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
				&& outDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
				&& !outDirectory.Contains(@"\scratch\", StringComparison.OrdinalIgnoreCase);
			var testDirectory = System.Text.RegularExpressions.Regex.Match(
				command,
				@"\$testDir\s*=\s*(?:""(?<path>[^""]+)""|'(?<path>[^']+)')",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups["path"].Value;
			var runnerVariable = System.Text.RegularExpressions.Regex.IsMatch(
				command,
				@"\$runnerExe\s*=\s*Join-Path\s+\$testDir\s+(?:""Runner\.exe""|'Runner\.exe')",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var verifiedRunnerVariable = outputPath.Equals("$runnerExe", StringComparison.OrdinalIgnoreCase)
				&& runnerVariable
				&& testDirectory.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
				&& testDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
				&& !testDirectory.Contains(@"\scratch\", StringComparison.OrdinalIgnoreCase);
			var joinedOutput = System.Text.RegularExpressions.Regex.Match(
				command,
				@"(?<dirVar>\$\w+)\s*=\s*(?:""(?<dir>[^""]+)""|'(?<dir>[^']+)')[\s\S]*?(?<outVar>\$\w+)\s*=\s*Join-Path\s+\k<dirVar>\s+(?:""Runner\.exe""|'Runner\.exe')",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var joinedDirectory = joinedOutput.Groups["dir"].Value;
			var relativeBinDirectory = !Path.IsPathRooted(joinedDirectory)
				&& !joinedDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal)
				&& joinedDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
			var verifiedJoinedOutput = outputPath.Equals(joinedOutput.Groups["outVar"].Value, StringComparison.OrdinalIgnoreCase)
				&& joinedOutput.Success
				&& (joinedDirectory.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
					&& joinedDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
					|| relativeBinDirectory)
				&& !joinedDirectory.Contains(@"\scratch\", StringComparison.OrdinalIgnoreCase);
			var expandedOutput = System.Text.RegularExpressions.Regex.Match(
				outputPath,
				@"^(?<dirVar>\$\w+)\\Runner\.exe$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var expandedDirectory = expandedOutput.Success
				? System.Text.RegularExpressions.Regex.Match(
					command,
					System.Text.RegularExpressions.Regex.Escape(expandedOutput.Groups["dirVar"].Value) + @"\s*=\s*(?:""(?<path>[^""]+)""|'(?<path>[^']+)')",
					System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups["path"].Value
				: string.Empty;
			var relativeExpandedBin = !Path.IsPathRooted(expandedDirectory)
				&& !expandedDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..", StringComparer.Ordinal)
				&& expandedDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);
			var verifiedExpandedOutput = expandedOutput.Success
				&& (expandedDirectory.StartsWith(_workspaceRoot, StringComparison.OrdinalIgnoreCase)
					&& expandedDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)
					|| relativeExpandedBin)
				&& !expandedDirectory.Contains(@"\scratch\", StringComparison.OrdinalIgnoreCase);
			if (!output.Success
				|| !outputPath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !verifiedOutDirectory && !verifiedRunnerVariable && !verifiedJoinedOutput && !verifiedExpandedOutput
				|| outputPath.Contains(@"\scratch\", StringComparison.OrdinalIgnoreCase))
				return "Error: csc.exe must emit Runner.exe directly into the test project's bin output directory, never into scratch. Keep Runner.cs in scratch and pass its path only as the source argument.";
		}
		if (System.Text.RegularExpressions.Regex.IsMatch(command, @"\bRemove-Item\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
			&& !command.Contains("Runner.exe", StringComparison.OrdinalIgnoreCase))
			return "Error: Workflow Verifier cleanup may remove only Runner.exe. Report any other required workspace mutation for Junior repair.";
		if (OperatingSystem.IsWindows()
			&& command.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)
			&& System.Text.RegularExpressions.Regex.IsMatch(command, @"MSBuild(?:\.exe)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
			&& !command.Contains(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe", StringComparison.OrdinalIgnoreCase))
			return @"Error: use the known native path C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe directly. Do not probe or invoke Visual Studio MSBuild paths.";
		var validation = System.Text.RegularExpressions.Regex.IsMatch(
			command,
			@"\b(?:msbuild(?:\.exe)?|csc(?:\.exe)?|vstest|nunit|pytest|dotnet\s+(?:build|test)|npm\s+(?:run\s+)?test|pnpm\s+test|yarn\s+test|cargo\s+test|go\s+test|runner(?:\.exe)?|Remove-Item)\b|/target:exe[^\r\n]*/out:[^\r\n]*/reference:",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		var inspection = System.Text.RegularExpressions.Regex.IsMatch(
			command,
			@"\bgit\s+(?:-[Cc]\s+\S+\s+)?(?:status|diff)\b",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (inspection)
			return "Error: Workflow Verifier shell Git inspection is blocked. workspace_changes already supplied authoritative status and diff; the next command must run validation or cleanup.";
		if (!validation)
			return "Error: Workflow Verifier discovery command blocked. Use read, glob, or grep for files and source. The next command must run the native build, focused test runner, or cleanup; do not inspect SDKs, packages, logs, or directories through the shell.";
		return Interlocked.Increment(ref _workflowVerifyCommandCalls) <= WorkflowVerifyCommandBudget
			? null
			: WorkflowVerifyCommandBudgetExhausted();
	}

	private static string WorkflowVerifyCommandBudgetExhausted() =>
		$"Error: Workflow Verifier command budget exhausted after {WorkflowVerifyCommandBudget} calls. Do not call more tools. Return the required final JSON now using the build and test evidence already collected; report fail if successful evidence is incomplete.";
}

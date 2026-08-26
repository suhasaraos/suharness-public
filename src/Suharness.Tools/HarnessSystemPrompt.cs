using Suharness.Abstractions;
using System.Text;

namespace Suharness.Tools;

/// <summary>
/// Composes the agent's system prompt. The per-tool guidance is kept in registration order because
/// that guidance is what steers the model toward the tools instead of shell equivalents.
/// The runtime-context block is what SuHarness was missing entirely: without the workspace path,
/// the model answered repository questions by saying it had no access to any repository.
/// The wording itself lives in the repository's Prompts/system folder; this file owns only the
/// order the pieces are assembled in and the conditions each piece is gated on.
/// </summary>
/// <summary>
/// Which orchestration tool families a session has, so the prompt describes only tools that are
/// actually registered on it.
/// </summary>
[Flags]
public enum OrchestrationTools
{
	None = 0,
	Goals = 1,
	Jobs = 2,
	Subagents = 4,
}

public static class HarnessSystemPrompt
{
	private const string AgentPrompt = "system/agent";
	private const string ApproachPrompt = "system/approach";
	private const string ToolGuidancePrompt = "system/tool-guidance";
	private const string OrchestrationPrompt = "system/orchestration";
	private const string ChatPrompt = "system/chat";
	private const string RolePrompt = "system/workflow-role";

	/// <summary>Build the full system prompt for a session rooted at <paramref name="workspaceRoot"/>.</summary>
	public static string Build(
		string workspaceRoot,
		HarnessAccessMode accessMode = HarnessAccessMode.WorkspaceWrite,
		string? modelName = null,
		bool planMode = false,
		HarnessAgentPreset preset = HarnessAgentPreset.Standard,
		OrchestrationTools orchestration = OrchestrationTools.None,
		bool semanticNavigationAvailable = false,
		string? scratchRoot = null,
		AgentToolProfile toolProfile = AgentToolProfile.Standard,
		bool teamMode = false)
	{
		// The minimal preset's persona is complete, so no other section may contribute.
		if (preset == HarnessAgentPreset.Minimal) return PromptCatalog.Get("system/minimal");
		if (toolProfile != AgentToolProfile.Standard)
			return BuildWorkflowPrompt(workspaceRoot, modelName, toolProfile, scratchRoot);
		if (string.IsNullOrWhiteSpace(workspaceRoot)) return BuildChatPrompt(modelName);

		var builder = new StringBuilder();

		builder.AppendLine(Section(AgentPrompt, "intro"));
		builder.AppendLine();
		// Keep the persona to one line and carry the detail in the per-tool sections below.
		builder.AppendLine(modelName is null
			? Fill(AgentPrompt, "persona", ("workspaceRoot", workspaceRoot))
			: Fill(AgentPrompt, "persona-with-model", ("workspaceRoot", workspaceRoot), ("modelName", modelName)));
		builder.AppendLine(Section(AgentPrompt, "repo-reference"));
		builder.AppendLine();

		builder.AppendLine("<env>");
		builder.AppendLine($"Working directory: {workspaceRoot}");
		builder.AppendLine($"Is a git repository: {(Directory.Exists(Path.Combine(workspaceRoot, ".git")) ? "true" : "false")}");
		builder.AppendLine($"Platform: {PlatformName()}");
		builder.AppendLine($"OS version: {Environment.OSVersion.VersionString}");
		builder.AppendLine($"Today's date: {DateTimeOffset.Now:yyyy-MM-dd}");
		if (!string.IsNullOrWhiteSpace(scratchRoot)) builder.AppendLine($"Session scratch directory: {scratchRoot}");
		builder.AppendLine("</env>");
		builder.AppendLine();
		builder.AppendLine(ToolchainInventory.Render(workspaceRoot));
		builder.AppendLine();

		builder.AppendLine("<approach>");
		builder.AppendLine(Section(ApproachPrompt, "locate-first"));
		// Shared wording from the plan-mode policy section.
		builder.AppendLine(Section(ApproachPrompt, "resolve-by-inspection"));
		builder.AppendLine(Section(ApproachPrompt, "material-ambiguity"));
		builder.AppendLine(Section(ApproachPrompt, "orthogonal-scope"));
		builder.AppendLine(Section(ApproachPrompt, "todo-list"));
		builder.AppendLine(Section(ApproachPrompt, "verify"));
		if (!string.IsNullOrWhiteSpace(scratchRoot))
			builder.AppendLine(Section(ApproachPrompt, "scratch"));
		builder.AppendLine(Section(ApproachPrompt, "reuse"));
		builder.AppendLine("</approach>");
		if (teamMode)
		{
			builder.AppendLine();
			builder.AppendLine(PromptCatalog.Get("system/team-mode", PromptNewline.Environment));
		}
		if (IsLocalModel(modelName))
		{
			builder.AppendLine();
			builder.AppendLine(PromptCatalog.Render(
				"system/local-model-execution",
				PromptNewline.Environment,
				("workspaceRoot", workspaceRoot)));
		}
		builder.AppendLine();

		// Tool guidance order follows the preferred discovery route before text/path fallbacks.
		builder.AppendLine(Section(ToolGuidancePrompt, "read"));
		builder.AppendLine();
		if (accessMode != HarnessAccessMode.ReadOnly)
		{
			builder.AppendLine(Section(ToolGuidancePrompt, "write"));
			builder.AppendLine();
			builder.AppendLine(Section(ToolGuidancePrompt, "edit"));
			builder.AppendLine();
		}
		if (semanticNavigationAvailable)
		{
			builder.AppendLine(Section(ToolGuidancePrompt, "semantic-navigation"));
			builder.AppendLine();
		}
		builder.AppendLine(Section(ToolGuidancePrompt, "glob"));
		builder.AppendLine();
		builder.AppendLine(Section(ToolGuidancePrompt, "grep"));
		builder.AppendLine();
		builder.AppendLine(Section(ToolGuidancePrompt, "ask-user-question"));
		builder.AppendLine();
		builder.AppendLine(Section(ToolGuidancePrompt, "todo-write"));
		builder.AppendLine();
		if (accessMode != HarnessAccessMode.ReadOnly)
		{
			builder.AppendLine(Section(ToolGuidancePrompt, "bash-exit-code"));
			if (OperatingSystem.IsWindows())
				builder.AppendLine(Section(ToolGuidancePrompt, "powershell"));
			builder.AppendLine();
		}
		var narrowingGuidance = Section(ToolGuidancePrompt, semanticNavigationAvailable ? "narrowing-semantic" : "narrowing-basic");
		builder.AppendLine(Section(ToolGuidancePrompt, accessMode switch
		{
			HarnessAccessMode.ReadOnly => "access-read-only",
			HarnessAccessMode.FullAccess => "access-full",
			_ => "access-workspace",
		}) + " " + narrowingGuidance);

		// Orchestration guidance matches the registered tool prompt sections. Each section is gated on the
		// tools actually being registered: describing a tool the session does not have invites the
		// model to call something that is not there.
		if (orchestration.HasFlag(OrchestrationTools.Goals))
		{
			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "goals"));
		}

		if (orchestration.HasFlag(OrchestrationTools.Jobs))
		{
			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "jobs"));
		}

		if (orchestration.HasFlag(OrchestrationTools.Subagents))
		{
			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "subagent"));

			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "subagent-fork"));

			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "workflow"));

			builder.AppendLine();
			builder.AppendLine(Section(OrchestrationPrompt, "ralph"));
		}

		if (planMode)
		{
			builder.AppendLine();
			// One AppendLine, so the policy's own line breaks stay LF as they were in source.
			builder.AppendLine(PromptCatalog.Get("system/plan-mode"));
		}

		var instructions = WorkspaceInstructions.Render(workspaceRoot);
		if (instructions.Length > 0)
		{
			builder.AppendLine();
			builder.AppendLine(instructions);
		}
		var agentContext = WorkspaceAgentContext.Render(workspaceRoot);
		if (agentContext.Length > 0)
		{
			builder.AppendLine();
			builder.AppendLine(agentContext);
		}

		return builder.ToString().TrimEnd();
	}

	private static string BuildChatPrompt(string? modelName)
	{
		var builder = new StringBuilder();
		builder.AppendLine(Section(ChatPrompt, "intro"));
		if (modelName is not null) builder.AppendLine(Fill(ChatPrompt, "persona-with-model", ("modelName", modelName)));
		builder.AppendLine();
		builder.AppendLine(Section(ChatPrompt, "no-workspace"));
		builder.AppendLine(Section(ChatPrompt, "remote-tools"));
		builder.AppendLine(Section(ChatPrompt, "ask-only"));
		builder.AppendLine();
		builder.AppendLine("<env>");
		builder.AppendLine($"Platform: {PlatformName()}");
		builder.AppendLine($"OS version: {Environment.OSVersion.VersionString}");
		builder.AppendLine($"Today's date: {DateTimeOffset.Now:yyyy-MM-dd}");
		builder.AppendLine("</env>");
		return builder.ToString().TrimEnd();
	}

	private static string BuildWorkflowPrompt(
		string workspaceRoot,
		string? modelName,
		AgentToolProfile toolProfile,
		string? scratchRoot)
	{
		var tools = toolProfile switch
		{
			AgentToolProfile.WorkflowReadOnly => Section(RolePrompt, "tools-read-only"),
			AgentToolProfile.WorkflowCode => Section(RolePrompt, "tools-code"),
			AgentToolProfile.WorkflowRepair => Section(RolePrompt, "tools-repair"),
			AgentToolProfile.WorkflowVerify => Fill(RolePrompt, "tools-verify", ("shell", WorkflowShellName())),
			_ => Section(RolePrompt, "tools-none"),
		};
		var builder = new StringBuilder();
		builder.AppendLine(modelName is null
			? Section(RolePrompt, "persona")
			: Fill(RolePrompt, "persona-with-model", ("modelName", modelName)));
		builder.AppendLine(Fill(RolePrompt, "selected-repository", ("workspaceRoot", workspaceRoot)));
		builder.AppendLine(Fill(RolePrompt, "platform", ("platform", PlatformName())));
		if (!string.IsNullOrWhiteSpace(scratchRoot))
			builder.AppendLine(Fill(RolePrompt, "scratch-directory", ("scratchRoot", scratchRoot)));
		builder.AppendLine(Fill(RolePrompt, "available-tools", ("tools", tools)));
		builder.AppendLine(Section(RolePrompt, "follow-assignment"));
		builder.AppendLine(Section(RolePrompt, "minimum-evidence"));
		if (toolProfile is AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair or AgentToolProfile.WorkflowVerify)
		{
			builder.AppendLine(Section(RolePrompt, toolProfile == AgentToolProfile.WorkflowVerify
				? "changes-once"
				: "changes-before"));
			if (toolProfile is AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair)
			{
				builder.AppendLine(Section(RolePrompt, "authoritative-finding"));
				builder.AppendLine(Section(RolePrompt, "no-build"));
				if (toolProfile == AgentToolProfile.WorkflowCode)
					builder.AppendLine(Section(RolePrompt, "write-new-only"));
				if (toolProfile == AgentToolProfile.WorkflowRepair)
					builder.AppendLine(Section(RolePrompt, "repair-write"));
			}
			else
				builder.AppendLine(Section(RolePrompt, "reviewer-authoritative"));
			if (toolProfile == AgentToolProfile.WorkflowVerify && OperatingSystem.IsWindows())
			{
				builder.AppendLine(Section(RolePrompt, "windows-shell"));
				builder.AppendLine(Section(RolePrompt, "windows-msbuild"));
				builder.AppendLine(Fill(
					RolePrompt,
					"windows-command-budget",
					("commandBudget", SessionToolset.WorkflowVerifyCommandBudget.ToString())));
				builder.AppendLine(Section(RolePrompt, "windows-narrow-build"));
				builder.AppendLine(Section(RolePrompt, "windows-runner-source"));
				builder.AppendLine(Section(RolePrompt, "windows-runner-compile"));
				builder.AppendLine(Section(RolePrompt, "windows-runner-invoke"));
				builder.AppendLine(Section(RolePrompt, "windows-runner-base"));
				builder.AppendLine(Section(RolePrompt, "windows-runner-imports"));
				builder.AppendLine(Section(RolePrompt, "windows-no-console-runner"));
			}
		}
		builder.AppendLine(Section(RolePrompt, "return-json"));
		if (toolProfile == AgentToolProfile.None)
			builder.AppendLine(Section(RolePrompt, "no-tools"));
		var instructions = WorkspaceInstructions.Render(workspaceRoot);
		if (instructions.Length > 0)
		{
			builder.AppendLine();
			builder.AppendLine(instructions);
		}
		var agentContext = WorkspaceAgentContext.Render(workspaceRoot);
		if (agentContext.Length > 0)
		{
			builder.AppendLine();
			builder.AppendLine(agentContext);
		}
		return builder.ToString().TrimEnd();
	}

	/// <summary>
	/// One section of prompt text. Environment newlines because every caller here hands the result to
	/// AppendLine, which is what produced the interior line breaks before this text lived in a file.
	/// </summary>
	private static string Section(string id, string section) =>
		PromptCatalog.Section(id, section, PromptNewline.Environment);

	private static string Fill(string id, string section, params (string Name, string Value)[] values) =>
		PromptCatalog.RenderSection(id, section, PromptNewline.Environment, values);

	private static string WorkflowShellName() => OperatingSystem.IsWindows() ? "PowerShell" : "Bash";

	private static string PlatformName() =>
		OperatingSystem.IsWindows() ? "win32"
		: OperatingSystem.IsMacOS() ? "darwin"
		: OperatingSystem.IsLinux() ? "linux"
		: "unknown";

	private static bool IsLocalModel(string? modelName) => modelName is not null
		&& (modelName.Contains("koboldcpp", StringComparison.OrdinalIgnoreCase)
			|| modelName.Contains("ornith", StringComparison.OrdinalIgnoreCase));
}

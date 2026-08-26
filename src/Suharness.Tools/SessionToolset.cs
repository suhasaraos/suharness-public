using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.ComponentModel;
using System.Text.Json;

namespace Suharness.Tools;

/// <summary>
/// The tool surface one session exposes to the model. Names, parameter names, descriptions, and
/// result text form a stable model-facing contract. Every tool is workspace-scoped, and the
/// observation log is per session, requiring that
/// a file must be read by <em>this</em> session before it can be mutated.
/// </summary>
public sealed partial class SessionToolset
{
	internal const int WorkflowVerifyCommandBudget = 12;
	internal const int WorkflowVerifyCommandAttemptBudget = 16;
	private readonly WorkspaceScope? _scope;
	private readonly string _workspaceRoot;
	private readonly FileObservationLog _observations;
	private readonly IWorkspaceIndex? _workspaceIndex;
	private readonly ISymbolIndex? _symbols;
	private readonly Guid _sessionId;
	private readonly IUserQuestionBroker? _questions;
	private readonly IPlanModeStore? _planMode;
	private readonly IGoalService? _goals;
	private readonly IJobService? _jobs;
	private readonly ISubagentService? _subagents;
	private readonly ISessionStore? _sessionStore;
	private readonly IAgentLifecycle? _lifecycle;
	private readonly ISpillStore? _spillStore;
	private readonly IMcpToolProvider? _mcp;
	private readonly ICapabilityEscalation? _capabilities;
	private readonly ILanguageServerService? _languageServers;
	private readonly ToolApprovalScope _approvals = new();
	private readonly int _maxInlineBytes;
	private readonly string? _scratchRoot;
	private readonly AgentToolProfile _toolProfile;
	private readonly TeamConfiguration _teamConfiguration;
	private readonly object _codingWorkflowGate = new();
	private int _codingWorkflowTurn = -1;
	private int _workflowVerifyCommandCalls;
	private int _workflowVerifyCommandAttempts;
	private int _workflowReviewWorkspaceChangesCalls;
	private int _workflowVerifyWorkspaceChangesCalls;
	private int _workflowVerifyRunnerSourceAccepted;

	public SessionToolset(
		string workspaceRoot,
		HarnessAccessMode accessMode = HarnessAccessMode.WorkspaceWrite,
		Guid sessionId = default,
		IUserQuestionBroker? questions = null,
		IPlanModeStore? planMode = null,
		HarnessAgentPreset preset = HarnessAgentPreset.Standard,
		IGoalService? goals = null,
		IJobService? jobs = null,
		ISubagentService? subagents = null,
		ISessionStore? sessionStore = null,
		IAgentLifecycle? lifecycle = null,
		ISpillStore? spillStore = null,
		int maxInlineBytes = SpillPolicy.DefaultMaxInlineBytes,
		IMcpToolProvider? mcp = null,
		ICapabilityEscalation? capabilities = null,
		ILanguageServerService? languageServers = null,
		IWorkspaceIndex? workspaceIndex = null,
		ISymbolIndex? symbols = null,
		string? scratchRoot = null,
		AgentToolProfile toolProfile = AgentToolProfile.Standard,
		TeamConfiguration? teamConfiguration = null)
	{
		_workspaceRoot = workspaceRoot;
		AccessMode = accessMode;
		Preset = preset;
		_sessionId = sessionId;
		_questions = questions;
		_planMode = planMode;
		_goals = goals;
		_jobs = jobs;
		_subagents = subagents;
		_sessionStore = sessionStore;
		_lifecycle = lifecycle;
		_spillStore = spillStore;
		_mcp = mcp;
		_capabilities = capabilities;
		_languageServers = languageServers;
		_workspaceIndex = workspaceIndex;
		_symbols = symbols;
		_scratchRoot = scratchRoot;
		_toolProfile = toolProfile;
		_teamConfiguration = teamConfiguration ?? TeamConfiguration.Disabled;
		_maxInlineBytes = maxInlineBytes;
		// Minimal has no read tool, so the read-before-mutate policy would refuse every edit.
		_observations = new FileObservationLog(enforced: preset != HarnessAgentPreset.Minimal);
		// The spill root is readable regardless of access mode: a result the policy moved out of the
		// context window is only recoverable if the model can read it back.
		if (HasWorkspace)
		{
			var additionalRoots = new List<string>();
			if (spillStore is not null) additionalRoots.Add(spillStore.Root);
			if (!string.IsNullOrWhiteSpace(scratchRoot)) additionalRoots.Add(scratchRoot);
			_scope = new WorkspaceScope(
				workspaceRoot,
				allowOutsideRoot: accessMode == HarnessAccessMode.FullAccess,
				additionalRoots: [.. additionalRoots]);
		}
	}

	/// <summary>The orchestration tool families registered on this session.</summary>
	public OrchestrationTools Orchestration =>
		(_goals is null ? OrchestrationTools.None : OrchestrationTools.Goals)
		| (_jobs is null ? OrchestrationTools.None : OrchestrationTools.Jobs)
		| (_subagents is null ? OrchestrationTools.None : OrchestrationTools.Subagents);

	/// <summary>The workspace every path in this session resolves against.</summary>
	public string WorkspaceRoot => _workspaceRoot;

	public bool HasWorkspace => !string.IsNullOrWhiteSpace(_workspaceRoot);

	private WorkspaceScope Scope => _scope ?? throw new InvalidOperationException("This session has no workspace.");

	public HarnessAccessMode AccessMode { get; }

	public HarnessAgentPreset Preset { get; }
	public AgentToolProfile ToolProfile => _toolProfile;
	public bool TeamEnabled => _teamConfiguration.Enabled;

	/// <summary>The spill cap in force for this session, in UTF-8 bytes.</summary>
	public int MaxInlineBytes => _maxInlineBytes;

	public string? ScratchRoot => _scratchRoot;

	/// <summary>Whether this workspace has a language server the model can query.</summary>
	public bool SemanticNavigationAvailable =>
		HasWorkspace && _languageServers?.IsConfiguredFor(_workspaceRoot) == true;

	/// <summary>
	/// Build the tool list handed to the agent in stable registration order.
	/// </summary>
	/// <param name="intent">
	/// What this turn was asked to do. Used only to narrow a very large MCP catalog to the
	/// capabilities the wording implies; it is never sent to the model. Taken per call rather than
	/// per session because the toolset outlives one turn but the task does not.
	/// </param>
	public IList<AITool> BuildTools(string? intent = null)
	{
		if (!HasWorkspace)
		{
			var chatTools = new List<AITool>
			{
				AIFunctionFactory.Create(AskUserQuestion, new AIFunctionFactoryOptions
				{
					Name = "ask_user_question",
					Description = "Ask the user a concise question when you need confirmation, a choice, or missing information before proceeding.",
				}),
			};
			if (_mcp is not null && AccessMode != HarnessAccessMode.ReadOnly) AddMcpTools(chatTools, intent);
			return Spill(LifecycleTool.Wrap(chatTools, _lifecycle, CurrentTurn, _questions, _approvals));
		}

		// The minimal preset composes only a shell and a string-replace editor.
		if (Preset == HarnessAgentPreset.Minimal)
		{
			var minimal = new List<AITool>
			{
				AIFunctionFactory.Create(Edit, new AIFunctionFactoryOptions
				{
					Name = "str_replace_editor",
					Description = "Edit an existing UTF-8 text file by replacing literal text. "
						+ "Inspect files with bash (for example \"sed -n 10,25p path\") before editing.",
				}),
			};
			if (AccessMode != HarnessAccessMode.ReadOnly)
			{
				minimal.Insert(0, AIFunctionFactory.Create(Bash, new AIFunctionFactoryOptions
				{
					Name = "bash",
					Description = BashTool.Description,
				}));
			}
			return minimal;
		}

		if (ToolProfile != AgentToolProfile.Standard) return BuildWorkflowTools();

		var tools = new List<AITool>
		{
			AIFunctionFactory.Create(Read, new AIFunctionFactoryOptions
			{
				Name = "read",
				Description = "Read a UTF-8 text file and return line-numbered content.",
			}),
		};
		if (AccessMode != HarnessAccessMode.ReadOnly)
		{
			tools.Add(AIFunctionFactory.Create(Write, new AIFunctionFactoryOptions
			{
				Name = "write",
				Description = "Create or fully replace a UTF-8 text file.",
			}));
			tools.Add(AIFunctionFactory.Create(Edit, new AIFunctionFactoryOptions
			{
				Name = "edit",
				Description = "Edit an existing UTF-8 text file by replacing literal text.",
			}));
		}
		if (SemanticNavigationAvailable)
		{
			tools.Add(AIFunctionFactory.Create(Lsp, new AIFunctionFactoryOptions
			{
				Name = "lsp",
				Description = "Query a language server for semantic code navigation: definition, references, implementation, "
					+ "hover, document_symbols, and symbol (a project-wide search by name). For source-code investigation, call "
					+ "operation symbol with one or more likely identifiers before glob or grep. It resolves meaning rather than "
					+ "matching text; the first call may pay for project loading. Give a position as one-based line and column, or "
					+ "pass symbol to have the position resolved for you.",
			}));
		}
		if (_symbols?.IsAvailable(_workspaceRoot) == true)
		{
			tools.Add(AIFunctionFactory.Create(Symbol, new AIFunctionFactoryOptions
			{
				Name = "symbol",
				Description = "Find where a type, method, or function is declared, by name. Use this before glob or grep "
					+ "when investigating source code: it answers from an index of declarations rather than by matching text. "
					+ "Matching is case-insensitive and accepts a partial name; qualify it as \"Type.Member\" to narrow. "
					+ "Returns path, line, kind, and enclosing type. It covers declarations only — use grep for literals only after "
					+ "a likely symbol query has returned no declaration.",
			}));
		}
		tools.Add(AIFunctionFactory.Create(Glob, new AIFunctionFactoryOptions
		{
			Name = "glob",
			Description = "Find files whose paths match a glob pattern. Returns matching file paths — never directories — "
				+ "including hidden and ignored files (VCS metadata directories are excluded). "
				+ $"Up to {SearchTools.GlobMaxResults} paths come back in modification-time order. "
				+ "This tool does not enumerate directory entries.",
		}));
		tools.Add(AIFunctionFactory.Create(Grep, new AIFunctionFactoryOptions
		{
			Name = "grep",
			Description = "Search file contents with a regular expression. Returns matching lines with line numbers, grouped by file. "
				+ $"Returns the first {SearchTools.GrepMaxMatches} matches inline. "
				+ "Use read on a matched file for surrounding context.",
		}));
		var skills = SkillCatalog.Discover(_workspaceRoot);
		tools.Add(AIFunctionFactory.Create(Skill, new AIFunctionFactoryOptions
		{
			Name = "skill",
			Description = SkillCatalog.Describe(skills),
		}));
		tools.Add(AIFunctionFactory.Create(AskUserQuestion, new AIFunctionFactoryOptions
		{
			Name = "ask_user_question",
			Description = "Ask the user a concise question when you need confirmation, a choice, or missing information "
				+ "before proceeding. Send one or more questions, each with a stable id that is echoed in the answer. "
				+ "This call blocks until the user answers, so ask everything you need in one call.",
		}));
		tools.Add(AIFunctionFactory.Create(TodoWrite, new AIFunctionFactoryOptions
		{
			Name = "todo_write",
			Description = TodoTool.Description,
		}));
		// Registered in every mode so entering or leaving plan mode does not change the tool catalog.
		tools.Add(AIFunctionFactory.Create(ExitPlanMode, new AIFunctionFactoryOptions
		{
			Name = "exit_plan_mode",
			Description = "Use only in plan mode. Present your plan for the user's review and, on approval, leave plan mode. "
				+ "Send the COMPLETE plan as markdown, starting with a # heading that names it. "
				+ "The user may approve (carry out the plan from your next step) or keep planning — their feedback comes back "
				+ "in the tool result; revise and present again.",
		}));
		if (AccessMode != HarnessAccessMode.ReadOnly)
		{
			tools.Add(AIFunctionFactory.Create(Bash, new AIFunctionFactoryOptions
			{
				Name = "bash",
				Description = BashTool.Description,
			}));
			if (OperatingSystem.IsWindows())
			{
				tools.Add(AIFunctionFactory.Create(PowerShell, new AIFunctionFactoryOptions
				{
					Name = "powershell",
					Description = PowerShellTool.Description,
				}));
			}
		}

		// Orchestration tools — registered only when the backing service is present.
		if (_goals is not null)
		{
			tools.Add(AIFunctionFactory.Create(GetGoal, new AIFunctionFactoryOptions
			{
				Name = "get_goal",
				Description = "Read the current same-session goal, including its exact id/revision, objective, phase, completed "
					+ "continuation rounds, round limit, blocker reason when present, and whether another continuation is armed. "
					+ "Call this before updating a goal.",
			}));
			tools.Add(AIFunctionFactory.Create(CreateGoal, new AIFunctionFactoryOptions
			{
				Name = "create_goal",
				Description = "Create one persisted same-session completion goal when the current direct human request "
					+ "is a long-running objective that should continue across autonomous goal rounds. You may "
					+ "infer that intent without requiring the user to say \"create a goal\". Do not use this for "
					+ "trivial single-turn work. Execution rejects non-human and subagent authority.",
			}));
			tools.Add(AIFunctionFactory.Create(UpdateGoal, new AIFunctionFactoryOptions
			{
				Name = "update_goal",
				Description = "Update the exact current goal revision. edit, pause, and resume require a direct top-level "
					+ "human request. During an automatic continuation of the current goal, complete and blocked are "
					+ "also allowed. blocked is rejected before the configured minimum round count; the model remains "
					+ "responsible for judging that the same condition persisted across those rounds and must explain it in blocked_reason.",
			}));
		}

		if (_jobs is not null)
		{
			tools.Add(AIFunctionFactory.Create(JobOutput, new AIFunctionFactoryOptions
			{
				Name = "job_output",
				Description = "Read a background job. Stream jobs return only output since the previous read; "
					+ "final-output jobs return their result after settlement. Every response ends with "
					+ "`[status: ...]`. Reads are non-blocking unless `wait: true`, which waits up to the configured cap.",
			}));
			tools.Add(AIFunctionFactory.Create(JobList, new AIFunctionFactoryOptions
			{
				Name = "job_list",
				Description = "List your background jobs (running and finished) with their ids, kinds, and statuses.",
			}));
			tools.Add(AIFunctionFactory.Create(JobKill, new AIFunctionFactoryOptions
			{
				Name = "job_kill",
				Description = "Request cancellation of a running background job by job id. Returns immediately; the job settles as killed once its work actually stops.",
			}));
		}

		if (_subagents is not null)
		{
			tools.Add(AIFunctionFactory.Create(Subagent, new AIFunctionFactoryOptions
			{
				Name = "subagent",
				Description = "Delegate a self-contained task to a subagent (a separate agent that works in its own context) "
					+ "to offload focused, independent work — research, a scoped implementation, an analysis — so it does "
					+ "not consume this conversation's context. The subagent returns its result, not its intermediate steps. "
					+ "Give it a complete, standalone prompt: it does not see this conversation. "
					+ "This tool runs in the background by default, immediately returns a durable subagent id, and keeps "
					+ "the child conversation available for later turns. When that run settles, the runtime sends the parent "
					+ "a notice containing its outcome and any final assistant message; `send_message` starts a later turn "
					+ "in the same child conversation. Set `run_in_background: false` only when your next action depends on receiving the result.",
			}));
			tools.Add(AIFunctionFactory.Create(SubagentFork, new AIFunctionFactoryOptions
			{
				Name = "subagent_fork",
				Description = "Delegate a task to a subagent that inherits this conversation: a child agent seeded with all "
					+ "completed turns so far (it does not see the current in-flight turn). Use this when the subtask "
					+ "builds on this conversation's context — a follow-up analysis, a review, a continuation — without "
					+ "consuming this conversation's context for the work itself. You receive its result, not its "
					+ "intermediate steps. This tool runs in the background by default, immediately returns a durable "
					+ "subagent id, and keeps the child conversation available for later turns. Set `run_in_background: false` "
					+ "only when your next action depends on receiving the result.",
			}));
			tools.Add(AIFunctionFactory.Create(ListAgents, new AIFunctionFactoryOptions
			{
				Name = "list_agents",
				Description = "List your continuable background subagents by durable id and label. Use it to recall which "
					+ "ones you started, not to poll for completion — you are told when one finishes. Status comes from "
					+ "the live registry: running means the agent is working right now, idle means it is loaded but "
					+ "between turns (it may be waiting on agents it started), and ready means it exists only in storage — "
					+ "resumable, not terminal, and not a result waiting to be collected. Scope `descendants` walks the "
					+ "whole tree below you in stable pre-order, annotating each entry with its durable direct-parent "
					+ "session id and depth. You may use `send_message` only for depth-1 entries; deeper entries are "
					+ "candidates for `interrupt_agent` only.",
			}));
			tools.Add(AIFunctionFactory.Create(SendMessage, new AIFunctionFactoryOptions
			{
				Name = "send_message",
				Description = "Send a message to a background subagent by its subagent id, continuing the same conversation. "
					+ "It becomes the subagent's next turn: if it is still working, the message waits until its current "
					+ "turn finishes, so it cannot redirect work already underway. This call returns no answer from the "
					+ "subagent — only confirmation that the message was delivered — so use it to give it more work. A "
					+ "failure means the message was NOT delivered.",
			}));
			tools.Add(AIFunctionFactory.Create(InterruptAgent, new AIFunctionFactoryOptions
			{
				Name = "interrupt_agent",
				Description = "Request cancellation of a background agent's current turn by its agent id. The target may be "
					+ "your direct child or a deeper agent created under you. Only the current turn stops: messages already "
					+ "queued for the agent stay parked until a later send_message, agents it started keep running, and "
					+ "the agent itself stays available for follow-ups. This call returns as soon as the stop request is "
					+ "accepted, so the target may keep running briefly; interrupting an agent that already finished is "
					+ "an accepted no-op.",
			}));
			tools.Add(AIFunctionFactory.Create(Workflow, new AIFunctionFactoryOptions
			{
				Name = "workflow",
				Description = "Run a JavaScript workflow script that orchestrates subagents at scale. Use this for work that fans out across many independent pieces — an audit over many files, a migration, multi-angle research, adversarial verification of findings — where you write the orchestration as a script instead of delegating turn by turn. See the tool description for exact script format. Use the workflow tool ONLY when the user explicitly asks for a workflow or for large multi-agent orchestration. "
					+ "For one or two delegations, prefer plain subagent calls.",
			}));
			if (TeamEnabled)
			{
				tools.Add(AIFunctionFactory.Create(CodingWorkflowTool, new AIFunctionFactoryOptions
				{
					Name = "coding_workflow",
					Description = "Run the selected Team's fresh-context coding workflow: Explorer, Planner, Senior Developer, "
						+ "Junior Developer, Reviewer, and Verifier. Team mode requires coding requests to use this tool exactly once.",
				}));
			}
			tools.Add(AIFunctionFactory.Create(Ralph, new AIFunctionFactoryOptions
			{
				Name = "ralph",
				Description = "Run a foreground fresh-agent Ralph loop toward one immutable objective. Use only when the direct human explicitly asks for Ralph or fresh-agent iteration. Each round opens a new child with no parent conversation or prior child session; the shared workspace is long-term memory, and only a bounded structured report crosses rounds. The call returns when a worker reports completion or a concrete blocker, or at the round limit.",
			}));
		}

		// MCP servers contribute last, under server-qualified names, and go through every wrapper the
		// built-in tools do. Read Only deliberately excludes them: that mode promises a fixed
		// read-only catalog, and the harness cannot tell whether a remote tool has side effects.
		if (_mcp is not null && AccessMode != HarnessAccessMode.ReadOnly) AddMcpTools(tools, intent);

		// Every tool runs through the lifecycle waterfall, so subscribers can veto a call before it
		// runs and observe its result — interception the runtime cannot do from outside the agent loop.
		return Spill(LifecycleTool.Wrap(tools, _lifecycle, CurrentTurn, _questions, _approvals));
	}

	private IList<AITool> BuildWorkflowTools()
	{
		if (ToolProfile == AgentToolProfile.None) return [];

		var tools = new List<AITool>
		{
			AIFunctionFactory.Create(Read, new AIFunctionFactoryOptions
			{
				Name = "read",
				Description = "Read a UTF-8 text file and return line-numbered content.",
			}),
		};
		if (SemanticNavigationAvailable)
		{
			tools.Add(AIFunctionFactory.Create(Lsp, new AIFunctionFactoryOptions
			{
				Name = "lsp",
				Description = "Use semantic definition, references, implementation, hover, document_symbols, or project symbol search.",
			}));
		}
		if (_symbols?.IsAvailable(_workspaceRoot) == true)
		{
			tools.Add(AIFunctionFactory.Create(Symbol, new AIFunctionFactoryOptions
			{
				Name = "symbol",
				Description = "Find source declarations by symbol name.",
			}));
		}
		tools.Add(AIFunctionFactory.Create(Glob, new AIFunctionFactoryOptions
		{
			Name = "glob",
			Description = "Find files by path glob.",
		}));
		tools.Add(AIFunctionFactory.Create(Grep, new AIFunctionFactoryOptions
		{
			Name = "grep",
			Description = "Search file contents with a regular expression.",
		}));

		if (ToolProfile is AgentToolProfile.WorkflowReadOnly or AgentToolProfile.WorkflowVerify)
		{
			tools.Add(AIFunctionFactory.Create(WorkspaceChanges, new AIFunctionFactoryOptions
			{
				Name = "workspace_changes",
				Description = "Return authoritative git status and the current tracked diff. Takes no arguments and never modifies the workspace.",
			}));
		}

		if (ToolProfile is AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair)
		{
			tools.Add(AIFunctionFactory.Create(Write, new AIFunctionFactoryOptions
			{
				Name = "write",
				Description = "Create a new UTF-8 text file. Existing files must be changed with edit.",
			}));
			tools.Add(AIFunctionFactory.Create(Edit, new AIFunctionFactoryOptions
			{
				Name = "edit",
				Description = "Replace exact text in an observed UTF-8 file.",
			}));
		}

		if (ToolProfile == AgentToolProfile.WorkflowVerify)
		{
			if (OperatingSystem.IsWindows())
			{
				tools.Add(AIFunctionFactory.Create(PowerShell, new AIFunctionFactoryOptions
				{
					Name = "powershell",
					Description = PowerShellTool.Description,
				}));
			}
			else
			{
				tools.Add(AIFunctionFactory.Create(Bash, new AIFunctionFactoryOptions
				{
					Name = "bash",
					Description = BashTool.Description,
				}));
			}
		}

		return Spill(LifecycleTool.Wrap(tools, _lifecycle, CurrentTurn, questions: null, approvals: _approvals));
	}

	/// <summary>
	/// The capability selection in force for this turn, and the groups the servers actually publish.
	/// Null when nothing needed narrowing, which is the common case: only a server with a very large
	/// catalog is filtered at all.
	/// </summary>
	public CapabilitySelection? Capabilities { get; private set; }

	/// <summary>Every capability group the connected servers publish, for the escalation tool to name.</summary>
	public IReadOnlyList<string> AvailableCapabilities { get; private set; } = [];

	/// <summary>
	/// Append each server's tools, narrowing only the catalogs large enough to be worth narrowing.
	/// Catalog size is a property of one server, so the decision is made per server: a two-tool search
	/// server is never filtered because a hundred-tool editor server happens to be connected too.
	/// </summary>
	private void AddMcpTools(List<AITool> tools, string? intent)
	{
		var published = _mcp!.Published;
		if (published.Count == 0) return;

		var oversized = published.Where(entry => entry.Tools.Count > McpCapabilityCatalog.DefaultFilterThreshold).ToList();
		if (oversized.Count == 0)
		{
			foreach (var entry in published) tools.AddRange(entry.Tools);
			return;
		}

		var available = oversized
			.SelectMany(entry => McpCapabilityCatalog.GroupsIn(entry.Tools, entry.Server))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.Ordinal)
			.ToList();
		AvailableCapabilities = available;
		var selection = McpCapabilityCatalog.Select(intent, available, _capabilities?.Requested(_sessionId));
		Capabilities = selection;

		foreach (var entry in published)
		{
			tools.AddRange(McpCapabilityCatalog.Filter(entry.Tools, selection, entry.Server));
		}

		// The way out of a narrowed catalog. Without it the model cannot tell a filter that guessed
		// wrong from a server that cannot do the job, and reports a blocker that is not real.
		if (_capabilities is not null)
		{
			tools.Add(AIFunctionFactory.Create(RequestCapability, new AIFunctionFactoryOptions
			{
				Name = "request_capability",
				Description = "Request an extra group of project tools when the ones you have cannot do the job. "
					+ $"You currently have: {string.Join(", ", selection.Groups)}. "
					+ $"Groups that exist: {string.Join(", ", available)}. "
					+ "Use this only after checking your current tools, and say exactly what you could not do. "
					+ "The group is added for the next turn, not this one.",
			}));
		}
	}

	/// <summary>
	/// Record a request for a capability group. It cannot take effect immediately: the tool catalog
	/// handed to a model is fixed for the duration of its request, so the honest answer is to say when
	/// it will apply rather than to pretend the tools just appeared.
	/// </summary>
	private string RequestCapability(
		[Description("Capability group to add, for example 'terrain' or 'runtime'.")] string group,
		[Description("One line saying which specific thing you could not do without it.")] string reason)
	{
		var available = string.Join(", ", AvailableCapabilities);
		if (string.IsNullOrWhiteSpace(group)) return $"Error: name a group. Groups that exist: {available}.";
		if (_capabilities is null || !_capabilities.Request(_sessionId, group, reason ?? string.Empty))
			return $"Error: could not record that request. Groups that exist: {available}.";

		return $"Recorded a request for the '{group.Trim()}' capability group. It is added to your tools on the "
			+ "next turn in this session. Finish this turn with the tools you have, or say plainly that you are "
			+ "blocked on that group.";
	}

	/// <summary>
	/// Put every tool result through the spill policy, so it sees the final text a tool produced —
	/// including anything a lifecycle subscriber replaced it with.
	/// </summary>
	private IList<AITool> Spill(IList<AITool> tools)
	{
		if (_spillStore is null || _maxInlineBytes <= 0) return tools;
		return [.. tools.Select(tool => tool is AIFunction function && SpillPolicy.Applies(function.Name)
			? new SpillingFunction(function, _sessionId, _spillStore, _maxInlineBytes)
			: tool)];
	}

	/// <summary>The turn a tool call belongs to, falling back to a turn-less context off-loop.</summary>
	private TurnContext CurrentTurn() =>
		_lifecycle?.Current(_sessionId)
		?? new TurnContext(_sessionId, WorkspaceRoot, 0, 0, CancellationToken.None);

	private bool BeginCodingWorkflow()
	{
		var turn = CurrentTurn().TurnNumber;
		lock (_codingWorkflowGate)
		{
			if (_codingWorkflowTurn == turn) return false;
			_codingWorkflowTurn = turn;
			return true;
		}
	}

	private string? CodingWorkflowWorkspaceGate()
	{
		if (!TeamEnabled) return null;
		var turn = CurrentTurn().TurnNumber;
		lock (_codingWorkflowGate)
			return _codingWorkflowTurn == turn
				? "Error: coding_workflow already ran in this turn. Do not call further workspace tools; report the workflow result now."
				: "Error: Team mode is active. Call coding_workflow with the user's complete coding request before using any workspace tool.";
	}

	/// <summary>
	/// Return a model-facing failure as the tool result instead of throwing. A thrown exception
	/// would abort the run; the model recovers from a returned error by fixing its arguments using
	/// the remediation message.
	/// </summary>
	private static string Guard(Func<string> execute)
	{
		try { return execute(); }
		catch (ToolInputException exception) { return $"Error: {exception.Message}"; }
		catch (UnauthorizedAccessException exception) { return $"Error: {exception.Message}"; }
		catch (IOException exception) { return $"Error: {exception.Message}"; }
	}
}

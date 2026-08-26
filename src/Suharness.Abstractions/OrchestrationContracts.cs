using Microsoft.Agents.AI;

namespace Suharness.Abstractions;

// ─── Goal Domain ────────────────────────────────────────────────────────

/// <summary>Durable lifecycle phase of one goal.</summary>
public enum GoalPhase { Active, Paused, Blocked, Complete }

/// <summary>Process-local continuation eligibility; never persisted.</summary>
public enum GoalActivation { Armed, Disarmed }

/// <summary>Goal state-changing verbs.</summary>
public enum GoalOperation { Create, Edit, Pause, Resume, Complete, Block, Clear }

/// <summary>Machine-routable and human-readable blocking reason.</summary>
public sealed record GoalBlockReason(string Code, string Message);

/// <summary>Full durable state written by every non-clear goal mutation.</summary>
public sealed record GoalSnapshot(
	string Id,
	int Revision,
	string Objective,
	GoalPhase Phase,
	int MaxGoalRounds,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	GoalBlockReason? BlockedReason = null);

/// <summary>Current goal projection including live counters.</summary>
public sealed record GoalView(
	string Id,
	int Revision,
	string Objective,
	GoalPhase Phase,
	int RoundsStarted,
	int MaxGoalRounds,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	GoalActivation Activation,
	GoalBlockReason? BlockedReason = null);

/// <summary>Persisted goal service — maps one goal per session.</summary>
public interface IGoalService
{
	/// <summary>Notification after a durable goal change commits.</summary>
	event Action<Guid>? Changed;

	/// <summary>Read the current goal for <paramref name="sessionId"/>, or null.</summary>
	GoalView? Get(Guid sessionId);

	/// <summary>Create a goal. Rejects when one already exists.</summary>
	GoalView Create(Guid sessionId, string objective, int? maxGoalRounds = null);

	/// <summary>Edit the current goal's objective or round cap. Requires exact ref.</summary>
	GoalView Edit(Guid sessionId, int expectedRevision, string? objective, int? maxGoalRounds);

	/// <summary>Pause or resume an active or paused goal. Direct-human only.</summary>
	GoalView Pause(Guid sessionId, int expectedRevision);
	GoalView Resume(Guid sessionId, int expectedRevision);

	/// <summary>Mark complete when the objective is achieved.</summary>
	GoalView Complete(Guid sessionId, int expectedRevision);

	/// <summary>Mark blocked with a concrete condition code and message.</summary>
	GoalView Block(Guid sessionId, int expectedRevision, GoalBlockReason reason);

	/// <summary>True when this session has an active or paused goal with activation armed.</summary>
	bool IsArmed(Guid sessionId);

	/// <summary>Disarm activation after a session fork or resume.</summary>
	void Disarm(Guid sessionId);

	/// <summary>Record a goal-continuation round in progress.</summary>
	void RecordRound(Guid sessionId);

	/// <summary>
	/// Load persisted goal state for a session from the transcript. A resumed goal is always
	/// rehydrated disarmed: automatic continuation authority does not survive a process restart
	/// or a session fork, and a direct human resume must rearm it.
	/// </summary>
	ValueTask RehydrateAsync(Guid sessionId, CancellationToken cancellationToken = default);

	/// <summary>
	/// True when this session should run another automatic goal round: the goal is active,
	/// activation is armed, and the round cap has not been reached.
	/// </summary>
	bool ShouldContinue(Guid sessionId);
}

// ─── Background Jobs ─────────────────────────────────────────────────────

/// <summary>Job lifecycle status.</summary>
public enum JobStatus { Running, Stopping, Completed, Killed, Failed }

/// <summary>Producer-defined job kinds.</summary>
public enum JobKind { Bash, Subagent, Generic }

/// <summary>Terminal result supplied by a producer.</summary>
public sealed record JobOutcome(JobStatus Status, string? Detail = null, string? Output = null);

/// <summary>Hooks through which the runtime controls and observes a job.</summary>
public sealed record JobHooks(
	Task<JobOutcome> Done,
	Func<string?, bool>? Cancel = null,
	Func<string>? ReadOutput = null);

/// <summary>A read-only projection of one job.</summary>
public sealed record JobSnapshot(
	string Id,
	JobKind Kind,
	string Label,
	JobStatus Status,
	DateTimeOffset StartedAt,
	int? OutputLimitBytes = null,
	string? Detail = null,
	DateTimeOffset? FinishedAt = null);

/// <summary>Output and post-read state returned by a read.</summary>
public sealed record JobRead(string Text, JobSnapshot Snapshot);

/// <summary>Per-owner job registry.</summary>
public interface IJobService
{
	event Action<Guid>? Changed;

	/// <summary>Start a background job owned by a session.</summary>
	string Start(Guid ownerSessionId, JobKind kind, string label, Func<CancellationToken, JobHooks> factory);

	/// <summary>List jobs visible to an owner session.</summary>
	IReadOnlyList<JobSnapshot> List(Guid ownerSessionId);

	/// <summary>Read accumulated output and current state.</summary>
	JobRead Read(string jobId, Guid ownerSessionId);

	/// <summary>Request cancellation. Returns whether the job was still running.</summary>
	bool Kill(string jobId, Guid ownerSessionId, string? reason = null);

	/// <summary>Block until the job reaches a terminal status or timeout.</summary>
	Task WaitAsync(string jobId, int timeoutMs, Guid ownerSessionId, CancellationToken cancellationToken = default);

	/// <summary>Look up a single job.</summary>
	JobSnapshot Get(string jobId, Guid ownerSessionId);
}

// ─── Subagents ────────────────────────────────────────────────────────────

/// <summary>Stop reason for a completed subagent run.</summary>
public enum SubagentStopReason { Completed, Aborted, Error, MaxTokens, Refusal }

/// <summary>Objective workspace changes and successful checks observed during a subagent run.</summary>
public sealed record SubagentExecutionEvidence(
	IReadOnlyList<string> FilesChanged,
	IReadOnlyList<string> SuccessfulValidations);

/// <summary>Result of one finished subagent run.</summary>
public sealed record SubagentResult(
	SubagentStopReason StopReason,
	string Output,
	string? ErrorDetail = null,
	SubagentExecutionEvidence? Evidence = null);

/// <summary>Specialized composition for one subagent turn.</summary>
public sealed record SubagentRunOptions(
	AgentToolProfile ToolProfile = AgentToolProfile.Standard,
	string? FinalizationPrompt = null,
	int? TimeoutSeconds = null,
	ModelSelection? ModelOverride = null,
	int? MaxOutputTokens = null,
	Type? JsonOutputType = null);

/// <summary>A running subagent.</summary>
public sealed class SubagentRun(
	string id,
	string label,
	Guid ownerSessionId,
	int depth,
	Task<SubagentResult> result,
	Func<ValueTask> dispose)
{
	public string Id => id;
	public string Label => label;
	public Guid OwnerSessionId => ownerSessionId;
	public int Depth => depth;
	public Task<SubagentResult> Result => result;
	public Func<ValueTask> Dispose => dispose;
}

/// <summary>Status of a subagent child for listing.</summary>
public enum SubagentStatus { Running, Idle, Ready, Terminal }

/// <summary>One child entry for list_agents.</summary>
public sealed record SubagentEntry(
	string Id,
	string Label,
	SubagentStatus Status,
	int Depth,
	Guid? ParentSessionId = null);

/// <summary>Inbox message kind.</summary>
public enum InboxMessageKind { User, Notice, GoalUpdate, JobCompletion, SubagentReport }

/// <summary>One message queued for an agent's next step or wakeup.</summary>
public sealed record AgentInboxMessage(
	string Id,
	InboxMessageKind Kind,
	string Content,
	string? Summary = null,
	DateTimeOffset CreatedAt = default);

/// <summary>Subagent service — the core multi-agent fabric.</summary>
public interface ISubagentService
{
	/// <summary>
	/// The maximum delegation depth: a child at this depth may not itself delegate. Depth is a
	/// recursion budget, so an unbounded chain of agents spawning agents cannot form.
	/// </summary>
	int MaxDepth { get; }

	/// <summary>The delegation depth of a session, or 0 when it is a top-level agent.</summary>
	int DepthOf(Guid sessionId);

	/// <summary>Start a subagent — fresh context (no parent conversation).</summary>
	Task<SubagentRun> StartAsync(
		string label,
		string prompt,
		Guid parentSessionId,
		Guid parentWorkspaceSessionId,
		bool runInBackground = false,
		CancellationToken cancellationToken = default,
		SubagentRunOptions? options = null);

	/// <summary>Start a forked subagent — inherits the parent's completed turns.</summary>
	Task<SubagentRun> ForkAsync(
		string label,
		string prompt,
		Guid parentSessionId,
		Guid parentWorkspaceSessionId,
		IReadOnlyList<SessionEvent> parentHistory,
		bool runInBackground = false,
		CancellationToken cancellationToken = default,
		SubagentRunOptions? options = null);

	/// <summary>List children of a parent session (direct or full tree).</summary>
	IReadOnlyList<SubagentEntry> ListChildren(Guid parentSessionId, bool descendants = false);

	/// <summary>Send a follow-up message to a running subagent.</summary>
	Task<string> SendMessageAsync(Guid childSessionId, string message, CancellationToken cancellationToken = default);

	/// <summary>Request cancellation of a running agent by its session id.</summary>
	bool Interrupt(Guid sessionId);

	/// <summary>Check in on a subagent's current status by id.</summary>
	SubagentEntry? Get(Guid sessionId);

	/// <summary>Notification when a subagent's status changes.</summary>
	event Action<SubagentEntry>? StatusChanged;
}

// ─── Agent Inbox ──────────────────────────────────────────────────────────

/// <summary>Per-session inbox for injected context and queued messages.</summary>
public interface IAgentInbox
{
	/// <summary>Enqueue a message for the session's next turn.</summary>
	void Enqueue(Guid sessionId, AgentInboxMessage message);

	/// <summary>Dequeue and return all pending messages for a session.</summary>
	IReadOnlyList<AgentInboxMessage> Dequeue(Guid sessionId);

	/// <summary>Messages currently waiting for a session.</summary>
	IReadOnlyList<AgentInboxMessage> Peek(Guid sessionId);

	/// <summary>Raised when a session's inbox changes.</summary>
	event Action<Guid>? Changed;
}

// ─── Waterfall / Lifecycle Events ────────────────────────────────────────

/// <summary>A turn-scoped execution context for interception hooks.</summary>
public sealed record TurnContext(
	Guid SessionId,
	string Workspace,
	int TurnNumber,
	int StepNumber,
	CancellationToken CancellationToken);

/// <summary>Lifecycle event hooks for the agent loop.</summary>
public interface IAgentLifecycle
{
	/// <summary>Called before each turn begins. Return false to cancel the turn.</summary>
	event Func<TurnContext, ValueTask<bool>>? BeforeTurn;

	/// <summary>Called after a turn completes.</summary>
	event Action<TurnContext, string?>? AfterTurn;

	/// <summary>Called before a step executes.</summary>
	event Action<TurnContext>? BeforeStep;

	/// <summary>Called after a step completes.</summary>
	event Action<TurnContext, string?>? AfterStep;

	/// <summary>
	/// Called when a tool call is about to execute. Handlers may allow, refuse, or escalate it to
	/// the human; the strictest decision across every handler wins.
	/// </summary>
	event Func<TurnContext, string, IReadOnlyDictionary<string, object?>, ValueTask<PreToolDecision>>? BeforeToolCall;

	/// <summary>
	/// Called with a tool's result before the model sees it. Handlers may accept it, replace what the
	/// model is shown, or withhold it entirely and substitute feedback. The last handler to change
	/// the result wins, and each handler sees what the previous one produced.
	/// </summary>
	event Func<TurnContext, string, string, ValueTask<PostToolDecision>>? AfterToolCall;

	/// <summary>
	/// Called with the final text the model will see for a tool call, after every policy handler has
	/// had its say. Observation only — this is the audit point, so it cannot change the result.
	/// </summary>
	event Action<TurnContext, string, string>? ToolResult;

	/// <summary>
	/// Called with a partial result while a long-running tool is still executing. This is transient
	/// presentation state; the tool's final result remains the durable transcript record.
	/// </summary>
	event Action<Guid, string, string>? ToolProgress;

	/// <summary>Fire the BeforeTurn pipeline. Returns false if any handler cancels.</summary>
	ValueTask<bool> FireBeforeTurnAsync(TurnContext context);

	/// <summary>Fire the AfterTurn notification.</summary>
	void FireAfterTurn(TurnContext context, string? error);

	/// <summary>Fire the BeforeStep notification for one model round-trip.</summary>
	void FireBeforeStep(TurnContext context);

	/// <summary>Fire the AfterStep notification for one model round-trip.</summary>
	void FireAfterStep(TurnContext context, string? error);

	/// <summary>
	/// Fire the BeforeToolCall waterfall and return the strictest decision any handler reached. A
	/// refusal is surfaced to the model as a tool result explaining it, never silently dropped.
	/// </summary>
	ValueTask<PreToolDecision> FireBeforeToolCallAsync(TurnContext context, string toolName, IReadOnlyDictionary<string, object?> arguments);

	/// <summary>Fire the AfterToolCall waterfall and return the decision that stands.</summary>
	ValueTask<PostToolDecision> FireAfterToolCallAsync(TurnContext context, string toolName, string result);

	/// <summary>Fire the observe-only ToolResult notification with the arguments and model-facing text.</summary>
	void FireToolResult(
		TurnContext context,
		string toolName,
		IReadOnlyDictionary<string, object?> arguments,
		string result);

	/// <summary>Publish transient progress for a running tool.</summary>
	void FireToolProgress(Guid sessionId, string toolName, string result);

	/// <summary>
	/// The turn and step currently executing for a session, or null when it is idle. Tool wrappers
	/// read this to attribute a call to the turn that made it, since the tool loop runs inside the
	/// agent and does not carry the runtime's turn identity.
	/// </summary>
	TurnContext? Current(Guid sessionId);

	/// <summary>
	/// Record which model round-trip a session is on, as reported by the agent's own function-calling
	/// loop. Advancing the number closes the previous step and opens the next, which is what makes
	/// <see cref="BeforeStep"/> and <see cref="AfterStep"/> describe real request boundaries rather
	/// than boundaries inferred from the shape of the response stream.
	/// </summary>
	void ObserveStep(Guid sessionId, int stepNumber);

	/// <summary>
	/// The highest step number reached during the session's current or most recent turn. The runtime
	/// reads it to tell whether a turn ended because it ran out of tool-calling round-trips.
	/// </summary>
	int MaxStep(Guid sessionId);

	/// <summary>Measured tool progress in the current or most recent turn.</summary>
	TurnProgressSnapshot Progress(Guid sessionId);
}

/// <summary>Compact progress evidence used to decide whether a bounded tool segment may continue.</summary>
public sealed record TurnProgressSnapshot(
	int ToolCalls,
	int UniqueResults,
	int SuccessfulMutations,
	bool TestsChanged = false,
	int SuccessfulBuildsAfterLastMutation = 0,
	int SuccessfulTestsAfterLastMutation = 0,
	IReadOnlyList<string>? SuspectPaths = null)
{
	public bool AdvancedFrom(TurnProgressSnapshot earlier) =>
		SuccessfulMutations > earlier.SuccessfulMutations
		|| SuccessfulBuildsAfterLastMutation > earlier.SuccessfulBuildsAfterLastMutation
		|| SuccessfulTestsAfterLastMutation > earlier.SuccessfulTestsAfterLastMutation;

	public bool RequiresValidation => SuccessfulMutations > 0;

	public bool ValidationSatisfied => !RequiresValidation
		|| (TestsChanged ? SuccessfulTestsAfterLastMutation > 0
			: SuccessfulBuildsAfterLastMutation > 0 || SuccessfulTestsAfterLastMutation > 0);
}

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Core;

public sealed partial class HarnessRuntime(
	ISessionStore store,
	IAgentFactory agentFactory,
	IGoalService? goals = null,
	IAgentInbox? inbox = null,
	IAgentLifecycle? lifecycle = null,
	ICompactionService? compaction = null,
	IRetryPolicyProvider? retryPolicies = null,
	IToolIterationPolicyProvider? toolIterationPolicies = null,
	IImageAttachmentStore? imageAttachments = null,
	ISnapshotService? snapshots = null,
	ISessionUsageTracker? usage = null,
	IModelBehaviorPolicyProvider? behaviorPolicies = null,
	ISessionScratchStore? scratch = null,
	IWorkspaceAgentStateStore? workspaceAgentState = null)
{
	/// <summary>
	/// How many context-overflow repairs one turn may attempt. One repair covers a bad token estimate;
	/// a second failure
	/// after a checkpoint landed means the transcript is not what is oversized, so repeating it would
	/// only summarize the session away for no benefit.
	/// </summary>
	private const int MaxOverflowRepairs = 1;
	private const int MaxUnfinishedContinuations = 2;

	/// <summary>
	/// The reserved passes a turn may spend once the model has stopped short of finishing. Each is
	/// capped per turn — all but one at a single use — so a turn that cannot complete its work does
	/// not spend the whole budget re-applying the same remedy, and the turn summary can report which
	/// remedies were spent and how often.
	/// </summary>
	private enum ContinuationKind
	{
		/// <summary>A whole discovery segment changed nothing; force one implementation-or-blocker pass.</summary>
		Implementation,

		/// <summary>The request asked for tests and no test file changed.</summary>
		RequestedTests,

		/// <summary>The workspace changed with no successful build or test after the last mutation.</summary>
		Validation,

		/// <summary>Validation succeeded as the budget ran out; reserve one tool-free final response.</summary>
		Finalization,

		/// <summary>A segment reached its checkpoint while still making progress, so tools continue.</summary>
		ToolSegment,

		/// <summary>Temporary artifacts were left behind in the workspace.</summary>
		ScratchHygiene,

		/// <summary>The model ended mid-task and the provider is configured to continue it.</summary>
		Automatic,
	}

	private static string ContinuationPrompt(ContinuationKind kind) => PromptCatalog.Get(kind switch
	{
		ContinuationKind.Implementation => "continuation/implementation",
		ContinuationKind.RequestedTests => "continuation/requested-tests",
		ContinuationKind.Validation => "continuation/validation",
		ContinuationKind.Finalization => "continuation/finalization",
		ContinuationKind.ToolSegment => "continuation/tool-segment",
		ContinuationKind.ScratchHygiene => "continuation/scratch-hygiene",
		ContinuationKind.Automatic => "continuation/automatic",
		_ => throw new ArgumentOutOfRangeException(nameof(kind)),
	});

	private readonly SemaphoreSlim _sessionCreateGate = new(1, 1);
	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionRunGates = new();

	/// <summary>Turn numbers are per session, so a turn number identifies a place in one transcript.</summary>
	private readonly ConcurrentDictionary<Guid, int> _turnCounters = new();

	/// <summary>The cancellation source of each in-flight turn, keyed by session, for Interrupt.</summary>
	private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

	/// <summary>Cancelled once on shutdown; every in-flight turn is linked to it.</summary>
	private readonly CancellationTokenSource _shutdown = new();

	private static bool RequestsTestChanges(string prompt) => Regex.IsMatch(
		prompt,
		@"\b(add|create|write|include)\b[\s\S]{0,300}\btests?\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static string StatefulContinuation(
		string instruction,
		string workspace,
		string originalPrompt,
		TurnProgressSnapshot progress) => PromptCatalog.Render(
		"continuation/turn-state",
		PromptNewline.Lf,
		("instruction", instruction),
		("workspace", workspace),
		("originalPrompt", originalPrompt),
		("mutations", progress.SuccessfulMutations.ToString(CultureInfo.InvariantCulture)),
		("testsChanged", progress.TestsChanged.ToString().ToLowerInvariant()),
		("builds", progress.SuccessfulBuildsAfterLastMutation.ToString(CultureInfo.InvariantCulture)),
		("tests", progress.SuccessfulTestsAfterLastMutation.ToString(CultureInfo.InvariantCulture)));

	private static IReadOnlyList<string> ExistingSuspectPaths(string workspace, IReadOnlyList<string>? paths)
	{
		if (paths is null || string.IsNullOrWhiteSpace(workspace)) return [];
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
		return paths.Where(path =>
		{
			try
			{
				var full = Path.GetFullPath(Path.Combine(root, path));
				return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
					&& File.Exists(full);
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
				return false;
			}
		}).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	}

	/// <summary>
	/// Request cancellation of the turn currently running for a session. Returns false when the
	/// session is idle. The turn stops at its next cancellation point; partial output is persisted.
	/// </summary>
	public bool Interrupt(Guid sessionId)
	{
		if (!_running.TryGetValue(sessionId, out var cts)) return false;
		try { cts.Cancel(); }
		catch (ObjectDisposedException) { return false; }
		return true;
	}

	/// <summary>True when a turn is running for this session.</summary>
	public bool IsRunning(Guid sessionId) => _running.ContainsKey(sessionId);

	/// <summary>Queue human input for the next round of a running session.</summary>
	public bool QueueMessage(Guid sessionId, string message)
	{
		if (inbox is null || !IsRunning(sessionId) || string.IsNullOrWhiteSpace(message)) return false;
		inbox.Enqueue(sessionId, new AgentInboxMessage(
			Guid.NewGuid().ToString("N"),
			InboxMessageKind.User,
			message.Trim(),
			CreatedAt: DateTimeOffset.UtcNow));
		return true;
	}

	/// <summary>
	/// Stop every in-flight turn and stop accepting new ones. Called on SIGTERM/SIGINT so a
	/// terminating process leaves each transcript with its partial output persisted.
	/// </summary>
	public void Shutdown()
	{
		try { _shutdown.Cancel(); }
		catch (ObjectDisposedException) { /* already shut down */ }
	}
}

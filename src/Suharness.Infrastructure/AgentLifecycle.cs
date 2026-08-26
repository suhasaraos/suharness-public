using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Infrastructure;

/// <summary>
/// In-memory lifecycle event hub. Harness services and the UI subscribe to observe and
/// intercept the agent loop at turn/step/tool boundaries.
/// </summary>
/// <remarks>
/// The policy hooks (<see cref="BeforeTurn"/>, <see cref="BeforeToolCall"/>, <see cref="AfterToolCall"/>)
/// run every handler. A pre-call decision combines by strictness rather than by registration order,
/// so a refusal cannot be undone by a handler that happened to be registered after it. The
/// notification hooks are fire-and-forget: a throwing subscriber must not take down the turn that
/// notified it.
/// </remarks>
public sealed class AgentLifecycle : IAgentLifecycle
{
	/// <summary>The turn in flight per session, published for tool wrappers.</summary>
	private readonly ConcurrentDictionary<Guid, TurnContext> _current = new();

	/// <summary>The highest step reached in the session's current or most recent turn.</summary>
	private readonly ConcurrentDictionary<Guid, int> _maxStep = new();
	private readonly ConcurrentDictionary<Guid, ProgressState> _progress = new();

	private sealed class ProgressState
	{
		public readonly object Gate = new();
		public readonly HashSet<string> Results = new(StringComparer.Ordinal);
		public int ToolCalls;
		public int SuccessfulMutations;
		public int LastMutationCall;
		public bool TestsChanged;
		public int SuccessfulBuildsAfterLastMutation;
		public int SuccessfulTestsAfterLastMutation;
		public readonly HashSet<string> SuspectPaths = new(StringComparer.OrdinalIgnoreCase);
	}

	public event Func<TurnContext, ValueTask<bool>>? BeforeTurn;
	public event Action<TurnContext, string?>? AfterTurn;
	public event Action<TurnContext>? BeforeStep;
	public event Action<TurnContext, string?>? AfterStep;
	public event Func<TurnContext, string, IReadOnlyDictionary<string, object?>, ValueTask<PreToolDecision>>? BeforeToolCall;
	public event Func<TurnContext, string, string, ValueTask<PostToolDecision>>? AfterToolCall;
	public event Action<TurnContext, string, string>? ToolResult;
	public event Action<Guid, string, string>? ToolProgress;

	public TurnContext? Current(Guid sessionId) =>
		_current.TryGetValue(sessionId, out var context) ? context : null;

	public int MaxStep(Guid sessionId) => _maxStep.GetValueOrDefault(sessionId);

	public TurnProgressSnapshot Progress(Guid sessionId)
	{
		if (!_progress.TryGetValue(sessionId, out var state)) return new(0, 0, 0);
		lock (state.Gate)
		{
			return new(
				state.ToolCalls,
				state.Results.Count,
				state.SuccessfulMutations,
				state.TestsChanged,
				state.SuccessfulBuildsAfterLastMutation,
				state.SuccessfulTestsAfterLastMutation,
				[.. state.SuspectPaths]);
		}
	}

	public async ValueTask<bool> FireBeforeTurnAsync(TurnContext context)
	{
		_current[context.SessionId] = context;
		_maxStep[context.SessionId] = context.StepNumber;
		_progress[context.SessionId] = new ProgressState();
		if (BeforeTurn is null) return true;
		foreach (var handler in BeforeTurn.GetInvocationList().Cast<Func<TurnContext, ValueTask<bool>>>())
			if (!await handler(context)) return false;
		return true;
	}

	public void FireAfterTurn(TurnContext context, string? error)
	{
		_current.TryRemove(context.SessionId, out _);
		Notify(() => AfterTurn?.Invoke(context, error));
	}

	public void FireBeforeStep(TurnContext context)
	{
		_current[context.SessionId] = context;
		_maxStep.AddOrUpdate(context.SessionId, context.StepNumber, (_, current) => Math.Max(current, context.StepNumber));
		Notify(() => BeforeStep?.Invoke(context));
	}

	public void FireAfterStep(TurnContext context, string? error) => Notify(() => AfterStep?.Invoke(context, error));

	/// <summary>
	/// Advance the session to a step the agent's own loop reported. A step number that has already
	/// been seen is ignored, because several tool calls in one response all belong to the same
	/// round-trip and must not each open a step of their own.
	/// </summary>
	public void ObserveStep(Guid sessionId, int stepNumber)
	{
		if (!_current.TryGetValue(sessionId, out var context) || stepNumber <= context.StepNumber) return;
		FireAfterStep(context, null);
		FireBeforeStep(context with { StepNumber = stepNumber });
	}

	public async ValueTask<PreToolDecision> FireBeforeToolCallAsync(
		TurnContext context,
		string toolName,
		IReadOnlyDictionary<string, object?> arguments)
	{
		var progress = _progress.GetOrAdd(context.SessionId, _ => new ProgressState());
		lock (progress.Gate) progress.ToolCalls++;
		if (BeforeToolCall is null) return PreToolDecision.Allow;
		var decision = PreToolDecision.Allow;
		foreach (var handler in BeforeToolCall.GetInvocationList()
			.Cast<Func<TurnContext, string, IReadOnlyDictionary<string, object?>, ValueTask<PreToolDecision>>>())
		{
			decision = PreToolDecision.Strictest(decision, await handler(context, toolName, arguments));
			// Nothing is stricter than a refusal, so the remaining handlers cannot change the outcome.
			if (decision.Kind == ToolGateKind.Deny) break;
		}
		return decision;
	}

	public async ValueTask<PostToolDecision> FireAfterToolCallAsync(TurnContext context, string toolName, string result)
	{
		if (AfterToolCall is null) return PostToolDecision.Accept;
		var decision = PostToolDecision.Accept;
		var current = result;
		foreach (var handler in AfterToolCall.GetInvocationList()
			.Cast<Func<TurnContext, string, string, ValueTask<PostToolDecision>>>())
		{
			// Each handler sees what the previous one produced, so transformations compose instead of
			// silently overwriting each other.
			var next = await handler(context, toolName, current);
			if (next.Outcome == PostToolOutcome.Accept) continue;
			decision = next;
			current = next.Content ?? current;
			// A blocked result is withheld outright; letting a later handler transform text the model
			// will never see would only invite confusion about what it actually got.
			if (next.Outcome == PostToolOutcome.Block) break;
		}
		return decision;
	}

	public void FireToolResult(
		TurnContext context,
		string toolName,
		IReadOnlyDictionary<string, object?> arguments,
		string result)
	{
		var progress = _progress.GetOrAdd(context.SessionId, _ => new ProgressState());
		lock (progress.Gate)
		{
			if (Informative(result))
				progress.Results.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result)))[..16]);
			if (SuccessfulMutation(context, toolName, arguments, result))
			{
				progress.SuccessfulMutations++;
				progress.LastMutationCall = progress.ToolCalls;
				progress.TestsChanged |= arguments.TryGetValue("file_path", out var path)
					&& LooksLikeTestPath(path?.ToString());
				if (path?.ToString() is { } changedPath && LooksLikeTemporaryRootArtifact(changedPath))
					progress.SuspectPaths.Add(changedPath);
				progress.SuccessfulBuildsAfterLastMutation = 0;
				progress.SuccessfulTestsAfterLastMutation = 0;
			}
			else if (toolName == "coding_workflow" && ApplyCodingWorkflowEvidence(progress, result))
			{
				progress.LastMutationCall = progress.ToolCalls;
			}
			else if (toolName is "bash" or "powershell"
				&& progress.ToolCalls > progress.LastMutationCall
				&& arguments.TryGetValue("command", out var commandValue))
			{
				var command = commandValue?.ToString() ?? string.Empty;
				if (ValidationEvidence.IsTestAttempt(command, result))
				{
					if (Successful(result) && ValidationEvidence.IsSuccessfulTestRun(result))
						progress.SuccessfulTestsAfterLastMutation++;
					else
					{
						progress.SuccessfulTestsAfterLastMutation = 0;
						progress.SuccessfulBuildsAfterLastMutation = 0;
					}
				}
				else if (ValidationEvidence.IsBuildCommand(command))
				{
					if (Successful(result) && !ValidationEvidence.HasBuildFailure(result))
						progress.SuccessfulBuildsAfterLastMutation++;
					else
						progress.SuccessfulBuildsAfterLastMutation = 0;
				}
			}
		}
		Notify(() => ToolResult?.Invoke(context, toolName, result));
	}

	public void FireToolProgress(Guid sessionId, string toolName, string result) =>
		Notify(() => ToolProgress?.Invoke(sessionId, toolName, result));

	private static bool SuccessfulMutation(
		TurnContext context,
		string toolName,
		IReadOnlyDictionary<string, object?> arguments,
		string result) =>
		toolName is "write" or "edit" or "str_replace_editor"
		&& Successful(result)
		&& arguments.TryGetValue("file_path", out var path)
		&& IsWorkspacePath(context.Workspace, path?.ToString());

	private static bool IsWorkspacePath(string workspace, string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return false;
		try
		{
			var relative = Path.GetRelativePath(workspace, Path.GetFullPath(path, workspace));
			return !Path.IsPathRooted(relative)
				&& relative != ".."
				&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool ApplyCodingWorkflowEvidence(ProgressState progress, string result)
	{
		try
		{
			using var document = JsonDocument.Parse(result);
			var root = document.RootElement;
			if (!root.TryGetProperty("status", out var status) || !string.Equals(status.GetString(), "pass", StringComparison.OrdinalIgnoreCase)
				|| !root.TryGetProperty("stages", out var stages))
				return false;
			var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var verified = false;
			foreach (var stage in stages.EnumerateArray())
			{
				if (!stage.TryGetProperty("handoff", out var handoff)) continue;
				if (handoff.TryGetProperty("filesChanged", out var files))
					foreach (var file in files.EnumerateArray())
						if (file.GetString() is { Length: > 0 } path) changed.Add(path);
				if (stage.TryGetProperty("role", out var role) && role.GetString() == "verifier"
					&& stage.TryGetProperty("status", out var stageStatus)
					&& string.Equals(stageStatus.GetString(), "pass", StringComparison.OrdinalIgnoreCase)
					&& handoff.TryGetProperty("commands", out var commands)
					&& commands.GetArrayLength() > 0)
					verified = true;
			}
			if (changed.Count == 0) return false;
			progress.SuccessfulMutations += changed.Count;
			progress.TestsChanged |= changed.Any(LooksLikeTestPath);
			progress.SuccessfulBuildsAfterLastMutation = verified ? 1 : 0;
			progress.SuccessfulTestsAfterLastMutation = verified ? 1 : 0;
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static bool Successful(string result) =>
		!result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("[exit code:", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("[timed out", StringComparison.OrdinalIgnoreCase);

	private static bool LooksLikeTestPath(string? path) => path is not null
		&& (path.Contains("test", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".spec.js", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase));

	private static bool LooksLikeTemporaryRootArtifact(string path)
	{
		var normalized = path.Replace('\\', '/').TrimStart('.', '/');
		if (normalized.Contains('/')) return false;
		return normalized.StartsWith("debug-", StringComparison.OrdinalIgnoreCase)
			|| normalized.StartsWith("tmp-", StringComparison.OrdinalIgnoreCase)
			|| normalized.StartsWith("temp-", StringComparison.OrdinalIgnoreCase)
			|| normalized.StartsWith("run-tests", StringComparison.OrdinalIgnoreCase)
			|| normalized.StartsWith("test-runner", StringComparison.OrdinalIgnoreCase);
	}

	private static bool Informative(string result) =>
		!string.IsNullOrWhiteSpace(result)
		&& !result.Contains("No-progress search", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("Duplicate tool call blocked", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("Runaway tool cycle blocked", StringComparison.OrdinalIgnoreCase);

	/// <summary>Run a notification so a faulty subscriber cannot fail the turn that notified it.</summary>
	private static void Notify(Action notification)
	{
		try { notification(); }
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException)) { }
	}
}

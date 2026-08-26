using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Suharness.Infrastructure;

/// <summary>
/// In-process subagent fabric. Children run the same agent factory on the parent
/// workspace with their own conversation and toolset, keyed by a child session id.
/// A child's history lives in a channel-fed worker; a fresh child starts empty, a
/// forked child is seeded with the parent's completed turns.
/// </summary>
/// <remarks>
/// Each queued turn carries its own completion source, so a turn that finishes before
/// its starter has begun awaiting still delivers a result. Interruption cancels only the
/// turn in flight: the child, its parked queue, and its descendants survive, which is what
/// <c>interrupt_agent</c> promises the model.
/// </remarks>
public sealed class SubagentService(
	Func<IAgentFactory> agentFactory,
	ISessionStore sessions,
	IAgentInbox inbox,
	int maxDepth = SubagentService.DefaultMaxDepth,
	ISessionUsageTracker? usage = null,
	Func<IRetryPolicyProvider>? retryPolicies = null) : ISubagentService
{
	/// <summary>Delegation budget: a depth-3 child may not itself delegate.</summary>
	public const int DefaultMaxDepth = 3;

	/// <summary>One queued turn: the message to run and the caller waiting on its outcome.</summary>
	private sealed record ChildTurn(
		AgentInboxMessage Message,
		TaskCompletionSource<SubagentResult>? Completion,
		bool NotifyParent,
		SubagentRunOptions Options);

	private sealed class ChildRecord(HarnessSession session, string label, Guid parentSessionId, int depth, List<ChatMessage> history)
	{
		public HarnessSession Session => session;
		public Guid Id => session.Id;
		public string Label => label;
		public Guid ParentSessionId => parentSessionId;
		public int Depth => depth;
		public List<ChatMessage> History => history;
		public Channel<ChildTurn> Queue { get; } = Channel.CreateUnbounded<ChildTurn>();

		/// <summary>Cancelled once, when the child is disposed; ends the worker loop.</summary>
		public CancellationTokenSource Lifetime { get; } = new();

		/// <summary>Replaced per turn; cancelling it stops only the turn in flight.</summary>
		public CancellationTokenSource? Turn;
		public int WorkerStarted;

		public SubagentStatus Status { get; set; } = SubagentStatus.Ready;
		public string? FinalText { get; set; }

		public SubagentEntry Entry() => new(session.Id.ToString("N"), label, Status, depth, parentSessionId);
	}

	private sealed record WorkspaceFileState(long Length, long LastWriteUtcTicks);

	private readonly ConcurrentDictionary<Guid, ChildRecord> _children = new();

	public event Action<SubagentEntry>? StatusChanged;

	public int MaxDepth => maxDepth;

	public int DepthOf(Guid sessionId)
	{
		var runtimeDepth = _children.TryGetValue(sessionId, out var record) ? record.Depth : 0;
		var persistedDepth = sessions.GetAsync(sessionId).GetAwaiter().GetResult()?.Session.DelegationDepth ?? 0;
		return Math.Max(runtimeDepth, persistedDepth);
	}

	public Task<SubagentRun> StartAsync(
		string label,
		string prompt,
		Guid parentSessionId,
		Guid parentWorkspaceSessionId,
		bool runInBackground = false,
		CancellationToken cancellationToken = default,
		SubagentRunOptions? options = null) =>
		ForkAsync(label, prompt, parentSessionId, parentWorkspaceSessionId, [], runInBackground, cancellationToken, options);

	public async Task<SubagentRun> ForkAsync(
		string label,
		string prompt,
		Guid parentSessionId,
		Guid parentWorkspaceSessionId,
		IReadOnlyList<SessionEvent> parentHistory,
		bool runInBackground = false,
		CancellationToken cancellationToken = default,
		SubagentRunOptions? options = null)
	{
		// Depth is derived from the caller, so a child cannot widen its own recursion budget.
		var parent = await sessions.GetAsync(parentSessionId, cancellationToken);
		var runtimeDepth = _children.TryGetValue(parentSessionId, out var parentRecord) ? parentRecord.Depth : 0;
		var depth = Math.Max(runtimeDepth, parent?.Session.DelegationDepth ?? 0) + 1;
		if (depth > maxDepth)
			throw new InvalidOperationException(
				$"Delegation depth limit reached ({maxDepth}): an agent at depth {depth - 1} cannot start further subagents.");

		var childSession = await CreateChildSessionAsync(parentWorkspaceSessionId, parentSessionId, depth, label, cancellationToken);
		var history = new List<ChatMessage>(BuildHistory(parentHistory));
		var record = new ChildRecord(childSession, label, parentSessionId, depth, history);
		_children[childSession.Id] = record;

		// The completion source is created and queued before the worker starts, so a turn that
		// settles immediately still has somewhere to deliver its result.
		var completion = new TaskCompletionSource<SubagentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		await record.Queue.Writer.WriteAsync(
			new ChildTurn(
				new AgentInboxMessage(Guid.NewGuid().ToString("N"), InboxMessageKind.User, prompt, label, DateTimeOffset.UtcNow),
				completion,
				NotifyParent: runInBackground,
				options ?? new SubagentRunOptions()),
			cancellationToken);

		StartWorker(record);

		var id = childSession.Id.ToString("N");
		if (runInBackground)
		{
			// A background run outlives the turn that started it, so its result is never tied to
			// the starter's token: the parent hears about it through its inbox instead.
			return new SubagentRun(id, label, parentSessionId, depth, completion.Task, DisposeChildAsync(childSession.Id));
		}

		try
		{
			var result = await WaitAsync(completion.Task, cancellationToken);
			return new SubagentRun(id, label, parentSessionId, depth, Task.FromResult(result), DisposeChildAsync(childSession.Id));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await DisposeChildAsync(childSession.Id)();
			throw;
		}
	}

	private void StartWorker(ChildRecord record)
	{
		if (Interlocked.CompareExchange(ref record.WorkerStarted, 1, 0) == 0)
			_ = RunChildAsync(record.Session, record, record.Lifetime.Token);
	}

	/// <summary>Await a turn, letting the caller walk away without stranding the child.</summary>
	private static async Task<SubagentResult> WaitAsync(Task<SubagentResult> completion, CancellationToken cancellationToken)
	{
		var cancelled = new TaskCompletionSource<SubagentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
		return await await Task.WhenAny(completion, cancelled.Task);
	}

	private async Task RunChildAsync(HarnessSession childSession, ChildRecord record, CancellationToken lifetime)
	{
		try
		{
			await foreach (var turn in record.Queue.Reader.ReadAllAsync(lifetime))
			{
				using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
				if (turn.Options.TimeoutSeconds is > 0)
					turnCts.CancelAfter(TimeSpan.FromSeconds(turn.Options.TimeoutSeconds.Value));
				record.Turn = turnCts;
				SetStatus(record, SubagentStatus.Running);
				try
				{
					await RunTurnAsync(childSession, record, turn, turnCts.Token);
				}
				catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
				{
					Settle(record, turn, new SubagentResult(SubagentStopReason.Aborted, record.FinalText ?? string.Empty, "Subagent disposed."));
					break;
				}
				catch (OperationCanceledException)
				{
					// Interrupted: only this turn ends. The queue and the child stay alive.
					Settle(record, turn, new SubagentResult(SubagentStopReason.Aborted, record.FinalText ?? string.Empty, "Turn interrupted."));
				}
				catch (Exception exception)
				{
					Settle(record, turn, new SubagentResult(SubagentStopReason.Error, record.FinalText ?? string.Empty, ModelFailureClassifier.Chain(exception)));
				}
				finally
				{
					record.Turn = null;
				}
				SetStatus(record, SubagentStatus.Idle);
			}
		}
		catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
		{
			// Worker drained after disposal.
		}

		// Any turn still parked in the queue will never run; fail it rather than leave it hanging.
		while (record.Queue.Reader.TryRead(out var abandoned))
			abandoned.Completion?.TrySetResult(new SubagentResult(SubagentStopReason.Aborted, string.Empty, "Subagent disposed before this turn ran."));

		SetStatus(record, SubagentStatus.Terminal);
	}

	private async Task RunTurnAsync(HarnessSession childSession, ChildRecord record, ChildTurn turn, CancellationToken cancellationToken)
	{
		var agent = await agentFactory().CreateAsync(
			new AgentContext(
				childSession.Id,
				childSession.Workspace,
				childSession.AccessMode,
				childSession.Preset,
				Interactive: false,
				Intent: turn.Message.Content,
				ToolProfile: turn.Options.ToolProfile,
				ModelOverride: turn.Options.ModelOverride,
				MaxOutputTokensOverride: turn.Options.MaxOutputTokens),
			cancellationToken);

		if (agent is null)
		{
			Settle(record, turn, new SubagentResult(SubagentStopReason.Error, string.Empty, "No model configured for this session."));
			return;
		}

		record.History.Add(new ChatMessage(ChatRole.User, turn.Message.Content));
		await sessions.AppendAsync(
			new SessionEvent(Guid.NewGuid(), childSession.Id, "message", "user", turn.Message.Content, DateTimeOffset.UtcNow),
			cancellationToken);

		var workspaceBefore = turn.Options.ToolProfile is AgentToolProfile.WorkflowCode or AgentToolProfile.WorkflowRepair or AgentToolProfile.WorkflowVerify
			? CaptureWorkspaceState(childSession.Workspace)
			: null;
		try
		{
			var retryPolicy = retryPolicies is null
				? RetryPolicy.Default
				: await retryPolicies().GetAsync(childSession.Id, cancellationToken);
			var response = await RunAgentWithRetryAsync(agent, record.History, childSession.Id, retryPolicy, cancellationToken);

			await PersistChildResponseAsync(childSession.Id, response, cancellationToken);
			var text = ExtractText(response);
			var evidence = CollectExecutionEvidence(childSession.Workspace, response);
			record.History.AddRange(response.Messages);
			if (!LooksLikeJsonObject(text) && !string.IsNullOrWhiteSpace(turn.Options.FinalizationPrompt))
			{
				var finalPrompt = turn.Options.FinalizationPrompt!;
				record.History.Add(new ChatMessage(ChatRole.User, finalPrompt));
				await sessions.AppendAsync(
					new SessionEvent(Guid.NewGuid(), childSession.Id, "message", "user", finalPrompt, DateTimeOffset.UtcNow),
					cancellationToken);
				var finalizer = await agentFactory().CreateAsync(
					new AgentContext(
						childSession.Id,
						childSession.Workspace,
						childSession.AccessMode,
						childSession.Preset,
						Interactive: false,
						Intent: finalPrompt,
						ToolProfile: AgentToolProfile.None,
						ModelOverride: turn.Options.ModelOverride,
						MaxOutputTokensOverride: turn.Options.MaxOutputTokens,
						JsonOutput: true,
						JsonOutputType: turn.Options.JsonOutputType),
					cancellationToken);
				if (finalizer is null) throw new InvalidOperationException("No model configured for structured handoff finalization.");
				var finalResponse = await RunAgentWithRetryAsync(finalizer, record.History, childSession.Id, retryPolicy, cancellationToken);
				await PersistChildResponseAsync(childSession.Id, finalResponse, cancellationToken);
				record.History.AddRange(finalResponse.Messages);
				text = ExtractText(finalResponse);
				evidence = MergeEvidence(evidence, CollectExecutionEvidence(childSession.Workspace, finalResponse));
			}
			// A child's requests are counted against the child's own session, including the optional
			// tool-free finalizer, and persist when the complete role turn ends.
			if (usage is not null) await usage.FlushAsync(childSession.Id, CancellationToken.None);
			record.FinalText = text;
			evidence = MergeEvidence(evidence, CollectWorkspaceEvidence(childSession.Workspace, workspaceBefore));
			Settle(record, turn, new SubagentResult(SubagentStopReason.Completed, text, Evidence: evidence));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (usage is not null) await usage.FlushAsync(childSession.Id, CancellationToken.None);
			var evidence = CollectWorkspaceEvidence(childSession.Workspace, workspaceBefore);
			var detail = turn.Options.TimeoutSeconds is > 0
				? $"Subagent exceeded its {turn.Options.TimeoutSeconds}-second deadline."
				: "Subagent turn was cancelled.";
			Settle(record, turn, new SubagentResult(SubagentStopReason.Error, string.Empty, detail, evidence));
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			if (usage is not null) await usage.FlushAsync(childSession.Id, CancellationToken.None);
			var evidence = CollectWorkspaceEvidence(childSession.Workspace, workspaceBefore);
			Settle(record, turn, new SubagentResult(
				SubagentStopReason.Error,
				string.Empty,
				ModelFailureClassifier.Chain(exception),
				evidence));
		}
	}

	private async Task<AgentResponse> RunAgentWithRetryAsync(
		AIAgent agent,
		IReadOnlyList<ChatMessage> history,
		Guid sessionId,
		RetryPolicy policy,
		CancellationToken cancellationToken)
	{
		var retriesUsed = 0;
		while (true)
		{
			try
			{
				var session = await agent.CreateSessionAsync(cancellationToken);
				var updates = new List<AgentResponseUpdate>();
				await foreach (var update in agent.RunStreamingAsync(history, session, cancellationToken: cancellationToken))
					updates.Add(update);
				return updates.ToAgentResponse();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (ModelConfigurationException)
			{
				throw;
			}
			catch (Exception exception)
			{
				var failure = ModelFailureClassifier.Classify(exception);
				if (!policy.ShouldRetry(failure.Code, retriesUsed)) throw;

				var delay = policy.Delay(retriesUsed, failure.ProviderRetryAfter);
				retriesUsed++;
				await sessions.AppendAsync(
					new SessionEvent(
						Guid.NewGuid(),
						sessionId,
						"notice",
						"system",
						$"The subagent model request failed ({failure.Code}) and is being retried in {delay.TotalMilliseconds:F0} ms — attempt {retriesUsed + 1} of {policy.MaxRetries + 1}.",
						DateTimeOffset.UtcNow),
					cancellationToken);
				await Task.Delay(delay, cancellationToken);
			}
		}
	}

	/// <summary>Deliver a turn's outcome to its starter and, for background work, to the parent's inbox.</summary>
	private void Settle(ChildRecord record, ChildTurn turn, SubagentResult result)
	{
		// The parent's report is queued before the run is observable as settled, so anything that
		// reacts to completion — the job wrapper, the parent's next turn — already sees the notice.
		if (turn.NotifyParent)
		{
			var outcome = result.StopReason == SubagentStopReason.Completed
				? Truncate(result.Output, 2000)
				: $"{result.StopReason.ToString().ToLowerInvariant()}: {result.ErrorDetail ?? "(no detail)"}";
			inbox.Enqueue(record.ParentSessionId, new AgentInboxMessage(
				Guid.NewGuid().ToString("N"),
				InboxMessageKind.SubagentReport,
				$"Subagent {record.Id:N} ({record.Label}) finished — {outcome}",
				record.Label,
				DateTimeOffset.UtcNow));
		}

		turn.Completion?.TrySetResult(result);
	}

	private void SetStatus(ChildRecord record, SubagentStatus status)
	{
		record.Status = status;
		StatusChanged?.Invoke(record.Entry());
	}

	public IReadOnlyList<SubagentEntry> ListChildren(Guid parentSessionId, bool descendants = false)
	{
		RehydrateChildren();
		var direct = _children.Values
			.Where(child => child.ParentSessionId == parentSessionId)
			.OrderBy(child => child.Depth)
			.Select(child => child.Entry())
			.ToList();
		if (!descendants) return direct;

		// Stable pre-order: each child is followed by its own subtree.
		var all = new List<SubagentEntry>();
		foreach (var child in direct)
		{
			all.Add(child);
			all.AddRange(ListChildren(Guid.ParseExact(child.Id, "N"), descendants: true));
		}
		return all;
	}

	public async Task<string> SendMessageAsync(Guid childSessionId, string message, CancellationToken cancellationToken = default)
	{
		var record = await RehydrateChildAsync(childSessionId, cancellationToken);
		if (record is null)
			throw new InvalidOperationException($"No subagent with id '{childSessionId:N}'.");
		if (record.Status == SubagentStatus.Terminal)
			throw new InvalidOperationException($"Subagent '{childSessionId:N}' has been disposed and cannot accept messages.");

		var id = Guid.NewGuid().ToString("N");
		// A follow-up turn reports to the parent's inbox: the sender gets delivery confirmation only.
		await record.Queue.Writer.WriteAsync(
			new ChildTurn(
				new AgentInboxMessage(id, InboxMessageKind.User, message, record.Label, DateTimeOffset.UtcNow),
				Completion: null,
				NotifyParent: true,
				new SubagentRunOptions()),
			cancellationToken);
		StartWorker(record);
		return id;
	}

	public bool Interrupt(Guid sessionId)
	{
		if (!_children.TryGetValue(sessionId, out var record)) return false;
		// Only the turn in flight stops. Queued messages stay parked, descendants keep running,
		// and the child remains available for follow-ups.
		var turn = record.Turn;
		if (turn is null) return false;
		try { turn.Cancel(); }
		catch (ObjectDisposedException) { return false; }
		return true;
	}

	public SubagentEntry? Get(Guid sessionId) =>
		RehydrateChildAsync(sessionId, CancellationToken.None).GetAwaiter().GetResult()?.Entry();

	private Func<ValueTask> DisposeChildAsync(Guid childSessionId) =>
		() =>
		{
			if (_children.TryRemove(childSessionId, out var record))
			{
				record.Queue.Writer.TryComplete();
				record.Lifetime.Cancel();
			}
			return ValueTask.CompletedTask;
		};

	private async Task<HarnessSession> CreateChildSessionAsync(
		Guid parentWorkspaceSessionId,
		Guid parentSessionId,
		int depth,
		string label,
		CancellationToken cancellationToken)
	{
		var parent = await sessions.GetAsync(parentWorkspaceSessionId, cancellationToken);
		var workspace = parent?.Session.Workspace ?? Directory.GetCurrentDirectory();
		var access = parent?.Session.AccessMode ?? HarnessAccessMode.WorkspaceWrite;
		var preset = parent?.Session.Preset ?? HarnessAgentPreset.Standard;

		var now = DateTimeOffset.UtcNow;
		var child = new HarnessSession(
			Guid.NewGuid(), workspace, label, now, now,
			Preset: preset,
			ParentSessionId: parentSessionId,
			DelegationDepth: depth) with
		{
			AccessMode = access,
		};
		await sessions.SaveSessionAsync(child, cancellationToken);
		return child;
	}

	private static List<ChatMessage> BuildHistory(IReadOnlyList<SessionEvent> events)
	{
		var history = new List<ChatMessage>();
		for (var index = 0; index < events.Count; index++)
		{
			var item = events[index];
			if (item.Type == "message" && item.ModelMessageId is null && item.Content.Length > 0)
			{
				if (item.Role == "user") history.Add(new ChatMessage(ChatRole.User, item.Content));
				else if (item.Role == "assistant") history.Add(new ChatMessage(ChatRole.Assistant, item.Content));
				continue;
			}

			if (item.ModelMessageId is null || item.Type is not ("message" or "tool_call" or "tool_result")) continue;
			var contents = new List<AIContent>();
			var messageId = item.ModelMessageId;
			while (index < events.Count && events[index].ModelMessageId == messageId)
			{
				var modelEvent = events[index];
				if (modelEvent.Type == "message" && modelEvent.Content.Length > 0)
					contents.Add(new TextContent(modelEvent.Content));
				else if (modelEvent.Type == "tool_call" && modelEvent.ToolCallId is not null && modelEvent.ToolName is not null)
				{
					var arguments = string.IsNullOrEmpty(modelEvent.ToolArguments)
						? null
						: JsonSerializer.Deserialize<Dictionary<string, object?>>(modelEvent.ToolArguments);
					contents.Add(new FunctionCallContent(modelEvent.ToolCallId, modelEvent.ToolName, arguments));
				}
				else if (modelEvent.Type == "tool_result" && modelEvent.ToolCallId is not null)
					contents.Add(new FunctionResultContent(modelEvent.ToolCallId, modelEvent.Content));
				index++;
			}
			index--;
			if (contents.Count > 0)
				history.Add(new ChatMessage(item.Role == "tool" ? ChatRole.Tool : ChatRole.Assistant, contents));
		}
		return history;
	}

	private static string ExtractText(Microsoft.Agents.AI.AgentResponse response) =>
		string.Join('\n', response.Messages
			.SelectMany(message => message.Contents)
			.OfType<TextContent>()
			.Select(content => content.Text)
			.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

	private static bool LooksLikeJsonObject(string text)
	{
		var start = text.IndexOf('{');
		var end = text.LastIndexOf('}');
		if (start < 0 || end < start) return false;
		try
		{
			using var document = JsonDocument.Parse(text[start..(end + 1)]);
			return document.RootElement.ValueKind == JsonValueKind.Object;
		}
		catch (JsonException) { return false; }
	}

	private static SubagentExecutionEvidence CollectExecutionEvidence(
		string workspace,
		Microsoft.Agents.AI.AgentResponse response)
	{
		var calls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
		foreach (var call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
			calls[call.CallId] = call;
		var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var validations = new List<string>();
		foreach (var result in response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
		{
			if (!calls.TryGetValue(result.CallId, out var call)) continue;
			var output = result.Result?.ToString() ?? string.Empty;
			if (!SuccessfulToolResult(output)) continue;
			if (call.Name is "write" or "edit" or "str_replace_editor"
				&& TryWorkspacePath(workspace, call.Arguments, out var path))
				changed.Add(path);
			if (call.Name is "bash" or "powershell"
				&& call.Arguments?.TryGetValue("command", out var command) == true
				&& ValidationEvidence.IsSuccessfulValidation(command?.ToString(), output))
				validations.Add($"{command} => {PlainEvidence(output)}");
		}
		return new SubagentExecutionEvidence([.. changed], validations);
	}

	private static SubagentExecutionEvidence MergeEvidence(
		SubagentExecutionEvidence first,
		SubagentExecutionEvidence second) =>
		new(
			[.. (first.FilesChanged ?? []).Concat(second.FilesChanged ?? []).Distinct(StringComparer.OrdinalIgnoreCase)],
			[.. (first.SuccessfulValidations ?? []).Concat(second.SuccessfulValidations ?? [])]);

	private static Dictionary<string, WorkspaceFileState> CaptureWorkspaceState(string workspace)
	{
		var state = new Dictionary<string, WorkspaceFileState>(StringComparer.OrdinalIgnoreCase);
		var pending = new Stack<string>();
		pending.Push(workspace);
		while (pending.TryPop(out var directory))
		{
			try
			{
				foreach (var child in Directory.EnumerateDirectories(directory))
					if (!IgnoredEvidenceDirectory(Path.GetFileName(child))) pending.Push(child);
				foreach (var file in Directory.EnumerateFiles(directory))
				{
					var info = new FileInfo(file);
					state[Path.GetRelativePath(workspace, file).Replace('\\', '/')] =
						new WorkspaceFileState(info.Length, info.LastWriteTimeUtc.Ticks);
				}
			}
			catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
			{
				// An unreadable generated directory cannot contain a mutation made through workspace tools.
			}
		}
		return state;
	}

	private static SubagentExecutionEvidence CollectWorkspaceEvidence(
		string workspace,
		Dictionary<string, WorkspaceFileState>? before)
	{
		if (before is null) return new SubagentExecutionEvidence([], []);
		var after = CaptureWorkspaceState(workspace);
		var changed = after
			.Where(item => !before.TryGetValue(item.Key, out var prior) || prior != item.Value)
			.Select(item => item.Key)
			.Concat(before.Keys.Where(path => !after.ContainsKey(path)))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return new SubagentExecutionEvidence(changed, []);
	}

	private static bool IgnoredEvidenceDirectory(string name) =>
		name.Equals(".git", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("bin", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("obj", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("packages", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
		|| name.Equals(".suharness", StringComparison.OrdinalIgnoreCase);

	private static bool TryWorkspacePath(
		string workspace,
		IDictionary<string, object?>? arguments,
		out string relativePath)
	{
		relativePath = string.Empty;
		if (arguments?.TryGetValue("file_path", out var value) != true || string.IsNullOrWhiteSpace(value?.ToString()))
			return false;
		try
		{
			var fullPath = Path.GetFullPath(value!.ToString()!, workspace);
			var relative = Path.GetRelativePath(workspace, fullPath);
			if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
				return false;
			relativePath = relative.Replace('\\', '/');
			return true;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool SuccessfulToolResult(string output) =>
		!output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
		&& !output.Contains("[exit code:", StringComparison.OrdinalIgnoreCase)
		&& !output.Contains("[timed out", StringComparison.OrdinalIgnoreCase);

	private static string PlainEvidence(string output)
	{
		var line = output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "succeeded";
		return line.Length <= 240 ? line : line[..240];
	}

	private async Task PersistChildResponseAsync(Guid sessionId, Microsoft.Agents.AI.AgentResponse response, CancellationToken cancellationToken)
	{
		foreach (var message in response.Messages)
		{
			var modelMessageId = Guid.NewGuid().ToString("N");
			foreach (var content in message.Contents)
			{
				switch (content)
				{
					case TextContent text when !string.IsNullOrEmpty(text.Text):
						await sessions.AppendAsync(new SessionEvent(
							Guid.NewGuid(), sessionId, "message", "assistant", text.Text,
							DateTimeOffset.UtcNow, modelMessageId), cancellationToken);
						break;
					case FunctionCallContent call:
						await sessions.AppendAsync(new SessionEvent(
							Guid.NewGuid(), sessionId, "tool_call", "assistant", DescribeCall(call),
							DateTimeOffset.UtcNow, modelMessageId, call.CallId, call.Name,
							JsonSerializer.Serialize(call.Arguments)), cancellationToken);
						break;
					case FunctionResultContent result:
						await sessions.AppendAsync(new SessionEvent(
							Guid.NewGuid(), sessionId, "tool_result", "tool", result.Result?.ToString() ?? string.Empty,
							DateTimeOffset.UtcNow, modelMessageId, result.CallId), cancellationToken);
						break;
				}
			}
		}
	}

	private async Task<ChildRecord?> RehydrateChildAsync(Guid childSessionId, CancellationToken cancellationToken)
	{
		if (_children.TryGetValue(childSessionId, out var existing)) return existing;
		var transcript = await sessions.GetAsync(childSessionId, cancellationToken);
		if (transcript?.Session.ParentSessionId is not { } parentSessionId) return null;
		var record = new ChildRecord(
			transcript.Session,
			transcript.Session.Title,
			parentSessionId,
			transcript.Session.DelegationDepth,
			BuildHistory(transcript.Events));
		return _children.GetOrAdd(childSessionId, record);
	}

	private void RehydrateChildren()
	{
		foreach (var session in sessions.ListAsync().GetAwaiter().GetResult())
		{
			if (session.ParentSessionId is null || _children.ContainsKey(session.Id)) continue;
			_ = RehydrateChildAsync(session.Id, CancellationToken.None).GetAwaiter().GetResult();
		}
	}

	private static string DescribeCall(FunctionCallContent call)
	{
		if (call.Arguments is null || call.Arguments.Count == 0) return $"{call.Name}()";
		string arguments;
		try { arguments = JsonSerializer.Serialize(call.Arguments); }
		catch (NotSupportedException) { arguments = "{}"; }
		return $"{call.Name}({arguments})";
	}

	private static string Truncate(string text, int max) =>
		text.Length <= max ? text : text[..max] + "…";
}

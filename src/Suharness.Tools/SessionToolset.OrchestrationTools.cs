using Suharness.Abstractions;
using System.ComponentModel;

namespace Suharness.Tools;

/// <summary>
/// The tools that coordinate work beyond a single turn: the session goal, background jobs,
/// subagents and the messages between them, and the workflow and Ralph loops built on top.
/// These are registered only when the matching service was supplied to the constructor.
/// </summary>
public sealed partial class SessionToolset
{
	// ── Goal tools ─────────────────────────────────────────────────────────

	private string GetGoal() =>
		Guard(() =>
		{
			var goal = _goals!.Get(_sessionId);
			return goal is null
				? "{\"goal\":null}"
				: System.Text.Json.JsonSerializer.Serialize(new
				{
					goal = new
					{
						id = goal.Id,
						revision = goal.Revision,
						objective = goal.Objective,
						phase = goal.Phase.ToString().ToLowerInvariant(),
						roundsStarted = goal.RoundsStarted,
						maxGoalRounds = goal.MaxGoalRounds,
						blockedReason = goal.BlockedReason is null ? null : new { code = goal.BlockedReason.Code, message = goal.BlockedReason.Message },
					},
					activation = goal.Activation == GoalActivation.Armed ? "armed" : "disarmed",
				});
		});

	private string CreateGoal(
		[Description("The concrete completion objective inferred from the direct human request.")] string objective,
		[Description("Optional positive safe-integer limit on automatic continuation rounds.")] int? max_goal_rounds = null) =>
		Guard(() =>
		{
			var goal = _goals!.Create(_sessionId, objective, max_goal_rounds);
			return System.Text.Json.JsonSerializer.Serialize(new
			{
				goal = new { id = goal.Id, revision = goal.Revision, objective = goal.Objective, phase = goal.Phase.ToString().ToLowerInvariant() },
				activation = "armed",
			});
		});

	private string UpdateGoal(
		[Description("Exact id returned by get_goal.")] string goal_id,
		[Description("Exact positive revision returned by get_goal.")] int revision,
		[Description("edit | pause | resume | complete | blocked")] string action,
		[Description("Replacement objective; valid only with action edit.")] string? objective = null,
		[Description("Replacement cap; valid only with action edit.")] int? max_goal_rounds = null,
		[Description("Concrete blocking condition; required only with action blocked.")] string? blocked_reason = null) =>
		Guard(() =>
		{
			var service = _goals!;
			var goal = action switch
			{
				"edit" => service.Edit(_sessionId, revision, objective, max_goal_rounds),
				"pause" => service.Pause(_sessionId, revision),
				"resume" => service.Resume(_sessionId, revision),
				"complete" => service.Complete(_sessionId, revision),
				"blocked" => service.Block(_sessionId, revision, new GoalBlockReason("model-reported", blocked_reason ?? "Blocked.")),
				_ => throw new ToolInputException($"unknown goal action '{action}': expected edit, pause, resume, complete, or blocked"),
			};
			return System.Text.Json.JsonSerializer.Serialize(new
			{
				goal = new { id = goal.Id, revision = goal.Revision, objective = goal.Objective, phase = goal.Phase.ToString().ToLowerInvariant() },
				activation = goal.Activation == GoalActivation.Armed ? "armed" : "disarmed",
			});
		});

	// ── Job tools ──────────────────────────────────────────────────────────

	private async Task<string> JobOutput(
		[Description("Job id returned by the tool that started the background work.")] string job_id,
		[Description("Block until the job reaches a terminal status or the timeout expires.")] bool? wait = null,
		[Description("Max wait in milliseconds (only meaningful with wait: true).")] int? timeout_ms = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (wait == true)
			{
				await _jobs!.WaitAsync(job_id, timeout_ms ?? 30_000, _sessionId, cancellationToken);
			}
			var read = _jobs!.Read(job_id, _sessionId);
			var body = read.Text.Length > 0 ? read.Text : "(no new output)";
			var separator = body.EndsWith('\n') ? string.Empty : "\n";
			return $"{body}{separator}{StatusLine(read.Snapshot)}";
		}
		catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
		{
			return $"Error: {exception.Message}";
		}
	}

	private string JobList()
	{
		var jobs = _jobs!.List(_sessionId);
		return jobs.Count == 0
			? "(no background jobs)"
			: string.Join('\n', jobs.Select(job => $"{job.Id} [{job.Kind.ToString().ToLowerInvariant()}] {job.Status.ToString().ToLowerInvariant()} — {job.Label}"));
	}

	private string JobKill(
		[Description("Job id returned by the tool that started the background work.")] string job_id,
		[Description("Optional short reason, recorded in the log and forwarded to the job.")] string? reason = null)
	{
		var alreadyFinished = !_jobs!.Kill(job_id, _sessionId, reason);
		var snapshot = _jobs.Get(job_id, _sessionId);
		return alreadyFinished
			? $"job {snapshot.Id} had already finished {StatusLine(snapshot)}"
			: $"requested cancellation of job {snapshot.Id}";
	}

	private static string StatusLine(JobSnapshot snapshot) =>
		snapshot.Detail is not null
			? $"[status: {snapshot.Status.ToString().ToLowerInvariant()}, {snapshot.Detail}]"
			: $"[status: {snapshot.Status.ToString().ToLowerInvariant()}]";

	// ── Subagent tools ─────────────────────────────────────────────────────

	private Task<string> Subagent(
		[Description("A short (3-5 word) description of the delegated task, for display.")] string description,
		[Description("The complete, self-contained task for the subagent. It does not share this conversation's context, so include everything it needs.")] string prompt,
		[Description("Whether to run in the background and return a durable subagent id immediately. Defaults to true. Set false to wait for the result.")] bool? run_in_background = null,
		CancellationToken cancellationToken = default) =>
		DelegateAsync(description, prompt, run_in_background ?? true, fork: false, cancellationToken);

	private Task<string> SubagentFork(
		[Description("A short (3-5 word) description of the delegated task, for display.")] string description,
		[Description("The task for the subagent. It already sees this conversation's completed turns, so build on them freely and state only what is new.")] string prompt,
		[Description("Whether to run in the background and return a durable subagent id immediately. Defaults to true. Set false to wait for the result.")] bool? run_in_background = null,
		CancellationToken cancellationToken = default) =>
		DelegateAsync(description, prompt, run_in_background ?? true, fork: true, cancellationToken);

	/// <summary>
	/// Start one delegation. A background run is registered as a background job so job_list, job_output
	/// and job_kill see it like any other background work, and so killing it actually stops the child.
	/// A foreground run is awaited here and returns the child's output as this tool's result.
	/// </summary>
	private async Task<string> DelegateAsync(string description, string prompt, bool background, bool fork, CancellationToken cancellationToken)
	{
		try
		{
			var history = fork && _sessionStore is not null
				? (await _sessionStore.GetAsync(_sessionId, cancellationToken))?.Events ?? []
				: (IReadOnlyList<SessionEvent>)[];

			if (!background)
			{
				var foreground = await _subagents!.ForkAsync(description, prompt, _sessionId, _sessionId, history, runInBackground: false, cancellationToken: cancellationToken);
				try
				{
					var settled = await foreground.Result;
					return settled.StopReason == SubagentStopReason.Completed
						? settled.Output
						: $"Error: subagent {settled.StopReason.ToString().ToLowerInvariant()}: {settled.ErrorDetail ?? "(no detail)"}";
				}
				finally { await foreground.Dispose(); }
			}

			// Start before registering the job so a rejected delegation (depth limit, missing model)
			// reports as a tool error rather than as a job that fails a moment later.
			var run = await _subagents!.ForkAsync(description, prompt, _sessionId, _sessionId, history, runInBackground: true, cancellationToken: CancellationToken.None);
			if (_jobs is null) return $"started subagent {run.Id}";

			// job_kill disposes the child, which cancels its turn and drains its queue.
			bool CancelRun(string? reason)
			{
				_ = run.Dispose();
				return true;
			}

			var jobId = _jobs.Start(_sessionId, JobKind.Subagent, description, _ => new JobHooks(SettleAsync(run), CancelRun));
			return $"started subagent {run.Id} as background job {jobId}";
		}
		catch (Exception exception) when (exception is InvalidOperationException or ToolInputException or IOException)
		{
			return $"Error: {exception.Message}";
		}
	}

	/// <summary>Translate a subagent run's outcome into a background job outcome.</summary>
	private static async Task<JobOutcome> SettleAsync(SubagentRun run)
	{
		var result = await run.Result;
		return result.StopReason switch
		{
			SubagentStopReason.Completed => new JobOutcome(JobStatus.Completed, null, result.Output),
			SubagentStopReason.Aborted => new JobOutcome(JobStatus.Killed, result.ErrorDetail, result.Output),
			_ => new JobOutcome(JobStatus.Failed, result.ErrorDetail ?? result.StopReason.ToString(), result.Output),
		};
	}

	private string ListAgents(
		[Description("children (default) lists direct children only; descendants walks the complete tree below you.")] string? scope = null)
	{
		var descendants = scope == "descendants";
		var entries = _subagents!.ListChildren(_sessionId, descendants);
		return entries.Count == 0
			? "(no subagents)"
			: string.Join('\n', entries.Select(entry =>
				$"{entry.Id} [{entry.Status.ToString().ToLowerInvariant()}] depth={entry.Depth} — {entry.Label}"));
	}

	private async Task<string> SendMessage(
		[Description("The subagent id returned when the background subagent was started.")] string subagent_id,
		[Description("The message to deliver to the subagent.")] string message,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (!Guid.TryParseExact(subagent_id, "N", out var id) && !Guid.TryParse(subagent_id, out id))
				return $"Error: invalid subagent_id '{subagent_id}'.";
			await _subagents!.SendMessageAsync(id, message, cancellationToken);
			return $"message queued as the next turn for subagent {subagent_id}";
		}
		catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
		{
			return $"Error: {exception.Message}";
		}
	}

	private string InterruptAgent(
		[Description("The agent id of the running agent to interrupt.")] string agent_id)
	{
		// A malformed id is an input error. An id that is simply not running is an accepted no-op:
		// natural completion and a repeated request must not read as failures.
		if (!Guid.TryParseExact(agent_id, "N", out var id) && !Guid.TryParse(agent_id, out id))
			return $"Error: invalid agent_id '{agent_id}'.";
		_subagents!.Interrupt(id);
		return $"interrupt requested for agent {agent_id}";
	}

	private async Task<string> Workflow(
		[Description("The plain-JS workflow script body (top-level await allowed; NO `export const meta` statement; end with `return <json-value>`).")] string script,
		[Description("The workflow identity block (plain JSON — never code).")] Dictionary<string, object?> meta,
		[Description("Optional JSON input exposed to the script as the `args` global.")] Dictionary<string, object?>? args = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (!meta.TryGetValue("name", out var name) || name is not string)
				throw new ToolInputException("workflow meta requires a 'name' string");
			var argsJson = System.Text.Json.JsonSerializer.SerializeToElement(args ?? new Dictionary<string, object?>());
			var engine = new WorkflowEngine(_subagents!, _sessionId, _sessionId);
			var result = await engine.RunAsync(script, argsJson, cancellationToken);
			var rendered = System.Text.Json.JsonSerializer.Serialize(result.Value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
			return $"workflow \"{name}\" completed ({result.AgentsStarted} agent{(result.AgentsStarted == 1 ? "" : "s")}).\nReturn value:\n{rendered}";
		}
		catch (Exception exception)
		{
			return $"Error: workflow run failed: {exception.Message}";
		}
	}

	private async Task<string> CodingWorkflowTool(
		[Description("The complete original coding request and acceptance requirements.")] string request,
		[Description("Maximum reviewer/verifier repair rounds. Defaults to 2; use 0 to disable retries.")] int? max_repair_rounds = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			if (AccessMode == HarnessAccessMode.ReadOnly)
				throw new InvalidOperationException("coding_workflow requires a writable session");
			if (!BeginCodingWorkflow())
				throw new InvalidOperationException("coding_workflow already ran in this turn; return its existing result instead of starting it again");
			void ReportProgress(string progress) =>
				_lifecycle?.FireToolProgress(_sessionId, "coding_workflow", progress);
			var workflow = new CodingWorkflow(
				_subagents!,
				_sessionId,
				_sessionId,
				max_repair_rounds ?? CodingWorkflow.DefaultMaxRepairRounds,
				_teamConfiguration,
				ReportProgress);
			var result = await workflow.RunAsync(request, cancellationToken);
			return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
			{
				PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
				WriteIndented = true,
			});
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
		{
			return $"Error: coding workflow failed: {exception.Message}";
		}
	}

	private async Task<string> Ralph(
		[Description("The immutable completion objective for every fresh Ralph round.")] string objective,
		[Description("Optional positive safe-integer round cap.")] int? max_rounds = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			return await RalphTool.RunAsync(_subagents!, _sessionId, objective, max_rounds, cancellationToken);
		}
		catch (Exception exception)
		{
			return $"Error: {exception.Message}";
		}
	}
}

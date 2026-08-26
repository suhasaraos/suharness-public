using Suharness.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// The fixed multi-role coding pipeline behind the <c>coding_workflow</c> tool: explore, plan,
/// decompose into micro-tasks, implement each one, then review and verify. Every role is a fresh
/// subagent that receives only its own instructions and hands back a structured JSON report, so no
/// role inherits another's context and each handoff is inspectable in the transcript.
/// </summary>
/// <remarks>
/// The pipeline is deliberately suspicious of what a role says about itself. A role that claims
/// success without the workspace evidence to match it is treated as blocked, which is what
/// <see cref="MergeExecutionEvidence"/> enforces: the tool calls a role actually made outrank its
/// own summary of them.
/// </remarks>
public sealed partial class CodingWorkflow(
	ISubagentService subagents,
	Guid parentSessionId,
	Guid workspaceSessionId,
	int maxRepairRounds = CodingWorkflow.DefaultMaxRepairRounds,
	TeamConfiguration? teamConfiguration = null,
	Action<string>? progress = null)
{
	public sealed record Result(string Status, int RepairRounds, IReadOnlyList<StageResult> Stages, string Summary);

	public sealed record StageResult(string Role, string Status, string Summary, JsonElement Handoff);

	public const int DefaultMaxRepairRounds = 2;

	/// <summary>How many times one role is re-run when it fails for a transport-shaped reason.</summary>
	private const int TransportAttempts = 3;

	/// <summary>Deadlines per role. Review and verification read and run more than they write.</summary>
	private const int RoleTimeoutSeconds = 300;
	private const int ReviewAndVerifyTimeoutSeconds = 600;

	/// <summary>
	/// The completion cap for the roles that only have to return a verdict. A review or verification
	/// handoff is a short JSON object; leaving the cap open invites a role to narrate instead.
	/// </summary>
	private const int ValidationMaxOutputTokens = 1024;

	private static readonly ModelSelection JuniorModel = new("openrouter", "google/gemini-3.7-flash");

	private static readonly JsonSerializerOptions JsonOptions =
		new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

	private readonly TeamConfiguration _teamConfiguration = teamConfiguration ?? TeamConfiguration.Disabled;
	private bool _anchorTransformation;

	// ── Handoff shapes ────────────────────────────────────────────────────

	private sealed record Exploration(string Summary, string[] Files, string[] Constraints, string[] Risks);

	private sealed record Plan(string Summary, string[] Steps, string[] AcceptanceCriteria);

	private sealed record SeniorPlan(string Summary, MicroTask[] MicroTasks);

	private sealed record MicroTask(string Id, string Instruction, string[] AcceptanceCriteria);

	private sealed record JuniorReport(
		string Status,
		string Summary,
		string[] FilesChanged,
		string[] Validation,
		string[]? Findings = null,
		bool ObjectiveProgress = false,
		bool ObjectiveMutation = false);

	private sealed record ReviewReport(string Status, string Summary, string[] Findings);

	private sealed record VerificationReport(string Status, string Summary, string[] Commands, string[] Findings);

	private sealed record StageOutput(string Text, SubagentExecutionEvidence? Evidence);

	private sealed record RepairOutcome(bool Success, int RoundsUsed, string Summary);

	// ── Prompts ───────────────────────────────────────────────────────────

	/// <summary>
	/// One role's prompt, loaded from Prompts/workflow. CRLF because these prompts spelled out
	/// \r\n when they were C# literals, and <see cref="ScopePrompt"/> still slices them with
	/// literal CRLF markers.
	/// </summary>
	private static string RolePrompt(string id, params (string Name, string Value)[] values) =>
		PromptCatalog.Render("workflow/" + id, PromptNewline.Crlf, values);

	private static string RoleSection(string id, string section, params (string Name, string Value)[] values) =>
		PromptCatalog.RenderSection("workflow/" + id, section, PromptNewline.Crlf, values);

	/// <summary>
	/// Micro-task text the harness synthesizes itself, rather than receiving from a role. LF because
	/// these instructions used <c>\n</c> as C# literals, unlike the role prompts above.
	/// </summary>
	private static string MicroTaskText(string section, params (string Name, string Value)[] values) =>
		PromptCatalog.RenderSection("workflow/micro-tasks", section, PromptNewline.Lf, values);

	private static string SynthesizedSeniorInstruction => MicroTaskText("synthesized-senior");

	// ── Pipeline ──────────────────────────────────────────────────────────

	public async Task<Result> RunAsync(string request, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(request))
			throw new ArgumentException("A coding workflow requires the original user request.", nameof(request));
		if (maxRepairRounds < 0) throw new ArgumentOutOfRangeException(nameof(maxRepairRounds));

		_anchorTransformation = AnchorTransformation(request);
		var stages = new List<StageResult>();

		var exploration = await RunStageAsync<Exploration>(
			"explorer", RolePrompt("explorer", ("request", request)), cancellationToken);
		AddStage(stages, Stage("explorer", "completed", exploration.Summary, exploration));

		var plan = await RunStageAsync<Plan>(
			"planner",
			RolePrompt("planner", ("request", request), ("exploration", Serialize(exploration))),
			cancellationToken);
		AddStage(stages, Stage("planner", "completed", plan.Summary, plan));

		var seniorPlan = await RunStageAsync<SeniorPlan>(
			"senior-developer",
			RolePrompt(
				"senior-developer",
				("request", request),
				("exploration", Serialize(exploration)),
				("plan", Serialize(plan))),
			cancellationToken);

		// A Senior handoff the harness had to synthesize carries a placeholder instruction rather than
		// a decomposition, so the request is decomposed here instead of being sent on as one task.
		if (seniorPlan.MicroTasks is [{ } only] && only.Instruction == SynthesizedSeniorInstruction)
			seniorPlan = seniorPlan with { MicroTasks = BuildFallbackMicroTasks(request) };

		seniorPlan = seniorPlan with
		{
			MicroTasks = EnsureTaskCoverage(request, NormalizeMicroTasks(seniorPlan.MicroTasks)),
		};
		if (seniorPlan.MicroTasks is null or [])
			throw new InvalidOperationException("The Senior Developer returned no micro-tasks.");
		AddStage(stages, Stage("senior-developer", "completed", seniorPlan.Summary, seniorPlan));

		var repairRounds = 0;
		foreach (var task in seniorPlan.MicroTasks)
		{
			var report = await RunTaskAsync(request, task, stages, repairRounds, cancellationToken);
			if ((Passed(report.Status) && report.ObjectiveMutation) || report.ObjectiveProgress) continue;

			if (repairRounds >= maxRepairRounds)
			{
				return new Result(
					"fail",
					repairRounds,
					stages,
					$"Junior Developer was blocked on micro-task {task.Id}: {report.Summary}");
			}

			repairRounds++;
			var repair = await RunRepairsAsync(
				$"junior-developer:{task.Id}", BlockedTaskFindings(task, report), repairRounds, stages, cancellationToken);
			repairRounds = repair.RoundsUsed;
			if (!repair.Success) return new Result("fail", repairRounds, stages, repair.Summary);
		}

		return await ReviewAndVerifyAsync(request, seniorPlan, stages, repairRounds, cancellationToken);
	}

	/// <summary>
	/// Run one micro-task, re-attempting while the Junior reports success it cannot evidence. A role
	/// that says "completed" without having changed a file has not done the task, and re-running it is
	/// cheaper than spending a repair round on it.
	/// </summary>
	private async Task<JuniorReport> RunTaskAsync(
		string request,
		MicroTask task,
		List<StageResult> stages,
		int repairRounds,
		CancellationToken cancellationToken)
	{
		var report = await RunJuniorAsync(request, task, attempt: 1, cancellationToken);
		AddStage(stages, Stage($"junior-developer:{task.Id}", report.Status, report.Summary, report), repairRounds);

		for (var attempt = 2; !report.ObjectiveMutation && attempt <= TransportAttempts; attempt++)
		{
			report = await RunJuniorAsync(request, task, attempt, cancellationToken);
			AddStage(
				stages,
				Stage($"junior-developer:{task.Id}-attempt-{attempt}", report.Status, report.Summary, report),
				repairRounds);
		}
		return report;
	}

	/// <summary>
	/// What to hand a repair Junior when a micro-task ended blocked. A role whose handoff itself was
	/// unusable never reported a finding, so the task is restated; otherwise its own findings are the
	/// most specific thing available.
	/// </summary>
	private static string[] BlockedTaskFindings(MicroTask task, JuniorReport report)
	{
		if (report.FilesChanged is null or [] && HandoffOnlyFailure(report))
		{
			return
			[
				"Complete this exact micro-task as a workspace mutation. " + task.Instruction
					+ " Acceptance criteria: " + Serialize(task.AcceptanceCriteria),
			];
		}
		return report.Findings is { Length: > 0 } findings
			? findings
			: [$"Complete micro-task {task.Id}: {report.Summary}"];
	}

	/// <summary>
	/// Review, then verify. A failed review sends findings to a repair Junior and starts review again,
	/// because a reviewer that has not seen the repair cannot vouch for it. A failed verification does
	/// the same and re-verifies.
	/// </summary>
	private async Task<Result> ReviewAndVerifyAsync(
		string request,
		SeniorPlan seniorPlan,
		List<StageResult> stages,
		int repairRounds,
		CancellationToken cancellationToken)
	{
		while (true)
		{
			var review = await RunReviewAsync(request, seniorPlan, stages, repairRounds, cancellationToken);
			if (!Passed(review.Status))
			{
				// A transient failure that survived every attempt is a transport problem, not a defect
				// in the work; sending it to a Junior would ask it to repair an error message.
				if (TransientRoleFailure(review.Summary, review.Findings))
					return new Result("fail", repairRounds, stages, review.Summary);

				var reviewFindings = (review.Findings is { Length: > 0 } reported
						? reported
						: ["Reviewer reported failure without a specific finding. Reinspect the diff against the original request and correct the concrete mismatch."])
					.Where(ActionableMutationFinding)
					.ToArray();

				if (reviewFindings.Length > 0)
				{
					if (repairRounds == maxRepairRounds)
						return new Result("fail", repairRounds, stages, review.Summary);

					var reviewRepair = await RunRepairsAsync(
						"reviewer", reviewFindings, repairRounds + 1, stages, cancellationToken);
					repairRounds = reviewRepair.RoundsUsed;
					if (!reviewRepair.Success) return new Result("fail", repairRounds, stages, reviewRepair.Summary);
					continue;
				}
			}

			var (verification, verificationFindings) =
				await RunVerificationAsync(request, review, stages, repairRounds, cancellationToken);
			if (verification is null) return new Result("pass", repairRounds, stages, verificationFindings[0]);

			if (repairRounds == maxRepairRounds || verificationFindings.Length == 0)
				return new Result("fail", repairRounds, stages, verification.Summary);

			var repair = await RunRepairsAsync(
				"verifier", verificationFindings, repairRounds + 1, stages, cancellationToken);
			repairRounds = repair.RoundsUsed;
			if (!repair.Success) return new Result("fail", repairRounds, stages, repair.Summary);
		}
	}

	private async Task<ReviewReport> RunReviewAsync(
		string request,
		SeniorPlan seniorPlan,
		List<StageResult> stages,
		int repairRounds,
		CancellationToken cancellationToken)
	{
		for (var attempt = 1; ; attempt++)
		{
			var role = attempt == 1 ? "reviewer" : $"reviewer-attempt-{attempt}";
			var review = await RunStageAsync<ReviewReport>(
				role,
				RolePrompt("reviewer", ("request", request), ("microTasks", Serialize(seniorPlan.MicroTasks))),
				cancellationToken,
				AgentToolProfile.WorkflowReadOnly);
			AddStage(stages, Stage(role, review.Status, review.Summary, review), repairRounds);

			if (!TransientRoleFailure(review.Summary, review.Findings) || attempt >= TransportAttempts) return review;
		}
	}

	/// <summary>
	/// Run verification until it produces a usable verdict. A null report means the run passed, and
	/// the accompanying array carries the summary; otherwise the array is the actionable findings.
	/// </summary>
	private async Task<(VerificationReport? Report, string[] Findings)> RunVerificationAsync(
		string request,
		ReviewReport review,
		List<StageResult> stages,
		int repairRounds,
		CancellationToken cancellationToken)
	{
		for (var attempt = 1; ; attempt++)
		{
			var role = attempt == 1 ? "verifier" : $"verifier-attempt-{attempt}";
			var verification = await RunStageAsync<VerificationReport>(
				role,
				RolePrompt("verifier", ("request", request), ("review", Serialize(review))),
				cancellationToken,
				AgentToolProfile.WorkflowVerify);

			var status = VerificationPassed(request, verification) ? "pass" : "fail";
			AddStage(stages, Stage(role, status, verification.Summary, verification), repairRounds);
			if (status == "pass") return (null, [verification.Summary]);

			var reported = verification.Findings is { Length: > 0 } findings
				? findings
				: ["Verification did not provide a successful build or test command. Run and repair the narrowest relevant validation."];
			var actionable = reported.Where(ActionableMutationFinding).ToArray();

			// Retry only while another attempt could plausibly do better: a transient failure, or an
			// infrastructure complaint that produced nothing a Junior could act on.
			var worthRetrying = TransientRoleFailure(verification.Summary, verification.Findings)
				|| (actionable.Length == 0 && ValidationInfrastructureFailure(verification.Summary, reported));
			if (attempt >= TransportAttempts || !worthRetrying) return (verification, actionable);
		}
	}

	/// <summary>
	/// Whether verification actually demonstrated success. A "pass" with no commands, or with a
	/// command that visibly failed, is a claim rather than evidence — and a request that asked for
	/// tests needs a positive executed count, not merely the absence of failures.
	/// </summary>
	private static bool VerificationPassed(string request, VerificationReport verification) =>
		Passed(verification.Status)
		&& verification.Commands is { Length: > 0 } commands
		&& !commands.Any(ValidationEvidence.HasReportedFailure)
		&& (!request.Contains("test", StringComparison.OrdinalIgnoreCase) || commands.Any(ValidationEvidence.HasReportedPassingTests));

	/// <summary>
	/// Drive repair rounds for the first finding. Only the first is repaired on purpose: the findings
	/// come from one role's single pass, and a Junior that fixes the first often invalidates the rest,
	/// so the next review re-derives them rather than acting on a stale list.
	/// </summary>
	private async Task<RepairOutcome> RunRepairsAsync(
		string source,
		IReadOnlyList<string> findings,
		int repairRound,
		List<StageResult> stages,
		CancellationToken cancellationToken)
	{
		if (findings.Count == 0) return new RepairOutcome(true, repairRound, "All repair findings were completed.");

		var finding = findings[0];
		var currentRound = repairRound;
		var transportAttempt = 1;
		while (true)
		{
			var suffix = transportAttempt > 1 ? $"-attempt-{transportAttempt}" : string.Empty;
			var repair = await RunStageAsync<JuniorReport>(
				$"junior-developer-repair-{currentRound}-1{suffix}",
				RolePrompt("junior-developer-repair", ("source", source), ("finding", finding)),
				cancellationToken,
				AgentToolProfile.WorkflowRepair,
				JuniorModel);
			AddStage(
				stages,
				Stage($"junior-developer:repair-{currentRound}-1{suffix}", repair.Status, repair.Summary, repair),
				currentRound);

			if (Passed(repair.Status) && repair.ObjectiveMutation)
				return new RepairOutcome(true, currentRound, "All repair findings were completed.");

			// A transport failure did not consume the repair; the same round tries again.
			if (!repair.ObjectiveMutation && TransientRoleFailure(repair) && transportAttempt < TransportAttempts)
			{
				transportAttempt++;
				continue;
			}

			if (currentRound >= maxRepairRounds)
			{
				return new RepairOutcome(
					false,
					currentRound,
					$"Junior Developer could not repair finding with a workspace mutation: {finding}. {repair.Summary}");
			}

			currentRound++;
			transportAttempt = 1;
		}
	}

	private Task<JuniorReport> RunJuniorAsync(
		string request,
		MicroTask task,
		int attempt,
		CancellationToken cancellationToken)
	{
		var suffix = attempt > 1 ? $"-attempt-{attempt}" : string.Empty;
		var prompt = AnchorTransformation(request)
			? AnchorAssignment(task)
			: RolePrompt(
				"junior-developer",
				("taskId", task.Id),
				("instruction", task.Instruction),
				("acceptanceCriteria", Serialize(task.AcceptanceCriteria)));

		return RunStageAsync<JuniorReport>(
			$"junior-developer-{task.Id}{suffix}",
			prompt,
			cancellationToken,
			AgentToolProfile.WorkflowCode,
			JuniorModel);
	}

	// ── Stage execution ───────────────────────────────────────────────────

	/// <summary>
	/// Run one role and parse its structured handoff. A role that returns something unparseable does
	/// not fail the pipeline: the handoff is synthesized from whatever it did produce, so the run
	/// continues on the evidence rather than on the formatting.
	/// </summary>
	private async Task<T> RunStageAsync<T>(
		string role,
		string prompt,
		CancellationToken cancellationToken,
		AgentToolProfile? toolProfile = null,
		ModelSelection? modelOverride = null)
	{
		prompt = ScopePrompt(role, prompt);
		var output = new StageOutput(string.Empty, null);
		try
		{
			output = await RunStageOutputAsync(
				role,
				prompt,
				toolProfile ?? ProfileForRole(role),
				ModelForRole(role) ?? modelOverride,
				typeof(T),
				cancellationToken);
			return MergeExecutionEvidence(Parse<T>(output.Text, role), output.Evidence);
		}
		catch (InvalidOperationException exception)
		{
			return Synthesize<T>(role, output.Text.Length > 0 ? output.Text : exception.Message, output.Evidence);
		}
	}

	private async Task<StageOutput> RunStageOutputAsync(
		string role,
		string prompt,
		AgentToolProfile toolProfile,
		ModelSelection? modelOverride,
		Type? jsonOutputType,
		CancellationToken cancellationToken)
	{
		var reviewing = role.StartsWith("reviewer", StringComparison.Ordinal);
		var verifying = toolProfile == AgentToolProfile.WorkflowVerify;
		var options = new SubagentRunOptions(
			toolProfile,
			PromptCatalog.Get("workflow/finalization"),
			verifying || reviewing ? ReviewAndVerifyTimeoutSeconds : RoleTimeoutSeconds,
			modelOverride,
			// Only the verdict-returning roles are capped; a coding role needs room to work.
			verifying || (reviewing && toolProfile == AgentToolProfile.WorkflowReadOnly)
				? ValidationMaxOutputTokens
				: null,
			jsonOutputType);

		var run = await subagents.StartAsync(
			role, prompt, parentSessionId, workspaceSessionId, runInBackground: false, cancellationToken, options);
		try
		{
			var result = await run.Result;
			return result.StopReason == SubagentStopReason.Completed
				? new StageOutput(result.Output, result.Evidence)
				: new StageOutput(
					$"{role} subagent ended {result.StopReason}: {result.ErrorDetail ?? result.Output}", result.Evidence);
		}
		finally
		{
			await run.Dispose();
		}
	}

	private static AgentToolProfile ProfileForRole(string role) => role switch
	{
		"planner" or "senior-developer" => AgentToolProfile.None,
		"verifier" => AgentToolProfile.WorkflowVerify,
		_ when role.StartsWith("junior-developer", StringComparison.Ordinal) => AgentToolProfile.WorkflowCode,
		_ => AgentToolProfile.WorkflowReadOnly,
	};

	private ModelSelection? ModelForRole(string role)
	{
		if (!_teamConfiguration.Enabled) return null;
		var persona = role switch
		{
			_ when role.StartsWith("junior-developer", StringComparison.Ordinal) => CodingPersona.JuniorDeveloper,
			_ when role.StartsWith("reviewer", StringComparison.Ordinal) => CodingPersona.Reviewer,
			_ when role.StartsWith("verifier", StringComparison.Ordinal) => CodingPersona.Verifier,
			"planner" => CodingPersona.Planner,
			"senior-developer" => CodingPersona.SeniorDeveloper,
			_ => CodingPersona.Explorer,
		};
		return _teamConfiguration.ModelFor(persona);
	}

	// ── Handoff parsing and repair ────────────────────────────────────────

	/// <summary>
	/// The trailing JSON object of a role's output. Roles narrate before answering, so the object is
	/// found by scanning backwards from the last closing brace — the last complete object is the
	/// handoff, and anything earlier is prose that happened to contain braces.
	/// </summary>
	private static T Parse<T>(string output, string role)
	{
		var end = output.LastIndexOf('}');
		if (end < 0)
			throw new InvalidOperationException($"{role} returned an invalid structured handoff: no JSON object was found.");

		for (var start = output.LastIndexOf('{', end); start >= 0; start = output.LastIndexOf('{', start - 1))
		{
			try
			{
				if (JsonSerializer.Deserialize<T>(output[start..(end + 1)], JsonOptions) is { } parsed) return parsed;
			}
			catch (JsonException)
			{
				// Not the object's real start; keep walking left.
			}
			if (start == 0) break;
		}

		throw new InvalidOperationException(
			$"{role} returned an invalid structured handoff: no valid trailing JSON object was found.");
	}

	/// <summary>
	/// Build the handoff a role failed to return, from what it nonetheless did. This keeps a run alive
	/// through a formatting failure, and — for the roles where it matters — keeps the synthesized
	/// verdict honest by deriving it from the tool calls rather than assuming success.
	/// </summary>
	private static T Synthesize<T>(string role, string evidence, SubagentExecutionEvidence? execution)
	{
		var summary = PlainSummary(evidence);
		var changed = (execution?.FilesChanged ?? []).ToArray();
		var validations = (execution?.SuccessfulValidations ?? []).ToArray();

		object handoff = typeof(T) switch
		{
			var type when type == typeof(Exploration) =>
				new Exploration(summary, [], ["Structured handoff was synthesized from role output."], []),

			var type when type == typeof(Plan) => new Plan(
				summary,
				[
					"Implement the original request using the supplied repository evidence.",
					"Add or update the requested focused tests.",
					"Run the narrowest relevant validation.",
				],
				["The original request is fully implemented and relevant tests pass."]),

			var type when type == typeof(SeniorPlan) => new SeniorPlan(
				summary,
				[new MicroTask("1", SynthesizedSeniorInstruction, [MicroTaskText("synthesized-senior-acceptance")])]),

			var type when type == typeof(JuniorReport) => new JuniorReport(
				changed.Length > 0 ? "completed" : "blocked",
				summary,
				changed,
				validations,
				changed.Length > 0 ? [] : [summary],
				ObjectiveProgress: changed.Length > 0 && validations.Length > 0,
				ObjectiveMutation: changed.Length > 0),

			var type when type == typeof(ReviewReport) => new ReviewReport("fail", summary, [summary]),

			var type when type == typeof(VerificationReport) => new VerificationReport(
				validations.Length > 0 && changed.Length == 0 ? "pass" : "fail",
				summary,
				validations,
				changed.Length > 0
					? [.. changed.Select(path =>
						$"Edit {path} through a Junior role because the Verifier modified it during validation.")]
					: validations.Length > 0 ? [] : [summary]),

			_ => throw new InvalidOperationException($"Cannot synthesize the {role} handoff type {typeof(T).Name}."),
		};

		return (T)handoff;
	}

	/// <summary>
	/// Reconcile a role's own report with what the harness observed it do. The observation wins: a
	/// Junior that claims completion without changing a file has not completed, and a Verifier that
	/// edited the workspace was validating its own change rather than the Junior's.
	/// </summary>
	private static T MergeExecutionEvidence<T>(T handoff, SubagentExecutionEvidence? evidence)
	{
		if (evidence is null) return handoff;

		if (handoff is VerificationReport verification)
		{
			if (evidence.FilesChanged is not { Count: > 0 }) return handoff;
			return (T)(object)(verification with
			{
				Status = "fail",
				Summary = verification.Summary + " Verifier modified workspace files during validation.",
				Findings =
				[
					.. verification.Findings ?? [],
					.. evidence.FilesChanged.Select(path => MicroTaskText("verifier-edit-finding", ("path", path))),
				],
			});
		}

		if (handoff is not JuniorReport report) return handoff;

		var mutated = evidence.FilesChanged is { Count: > 0 };
		var validated = evidence.SuccessfulValidations is { Count: > 0 };
		return (T)(object)(report with
		{
			FilesChanged =
				[.. (report.FilesChanged ?? []).Concat(evidence.FilesChanged ?? []).Distinct(StringComparer.OrdinalIgnoreCase)],
			Validation =
				[.. (report.Validation ?? []).Concat(evidence.SuccessfulValidations ?? []).Distinct()],
			ObjectiveMutation = mutated,
			ObjectiveProgress = mutated && validated,
		});
	}

	// ── Micro-task shaping ────────────────────────────────────────────────

	/// <summary>
	/// Collapse a Senior's decomposition to at most two mutation tasks: one implementation and one
	/// test-authoring. Discovery- and validation-only steps are dropped because other roles own them,
	/// and a long list of small edits costs a fresh subagent each without adding coverage.
	/// </summary>
	private static MicroTask[] NormalizeMicroTasks(MicroTask[]? tasks)
	{
		if (tasks is null or []) return [];

		var actionable = tasks
			.Where(task => !DiscoveryOnly(task.Instruction) && !ValidationOnly(task.Instruction))
			.ToArray();
		if (actionable.Length <= 2) return Renumber(actionable);

		var testTasks = actionable.Where(task => TestAuthoring(task.Instruction)).ToArray();
		var implementationTasks = actionable.Except(testTasks).ToArray();

		List<MicroTask> normalized = [];
		if (implementationTasks.Length > 0)
			normalized.Add(MergeTasks(implementationTasks, MicroTaskText("merge-implementation")));
		if (testTasks.Length > 0) normalized.Add(MergeTasks(testTasks, MicroTaskText("merge-tests")));
		return Renumber([.. normalized]);
	}

	/// <summary>
	/// Guarantee the task list covers what was asked. A Senior that returned only tests for a request
	/// that also asks for a change would otherwise leave the implementation to nobody.
	/// </summary>
	private static MicroTask[] EnsureTaskCoverage(string request, MicroTask[] tasks)
	{
		var fallback = BuildFallbackMicroTasks(request);
		var fallbackTest = fallback.FirstOrDefault(task => TestAuthoring(task.Instruction));
		var implementation = tasks.FirstOrDefault(task => !TestAuthoring(task.Instruction));
		var testTask = tasks.FirstOrDefault(task => TestAuthoring(task.Instruction));

		if (implementation is not null && testTask is not null)
			return Renumber([implementation, fallbackTest ?? testTask]);

		if (implementation is not null)
		{
			return request.Contains("NUnit", StringComparison.OrdinalIgnoreCase) && fallbackTest is not null
				? Renumber([implementation, fallbackTest])
				: Renumber([implementation]);
		}

		if (testTask is not null)
		{
			return RequestsProductionChange(request)
				? Renumber([fallback[0], fallbackTest ?? testTask])
				: Renumber([fallbackTest ?? testTask]);
		}

		return Renumber(fallback);
	}

	private static MicroTask[] BuildFallbackMicroTasks(string request)
	{
		var productionContext = string.Join(
			Environment.NewLine,
			SentenceBoundary().Split(request.ReplaceLineEndings("\n")).Where(ProductionRequestLine));

		List<MicroTask> tasks =
		[
			new MicroTask(
				"1",
				MicroTaskText("fallback-implementation", ("context", productionContext)),
				[
					MicroTaskText("fallback-implementation-acceptance-behavior"),
					MicroTaskText("fallback-implementation-acceptance-api"),
				]),
		];
		if (request.Contains("test", StringComparison.OrdinalIgnoreCase))
		{
			tasks.Add(new MicroTask(
				"2",
				MicroTaskText("fallback-tests", ("request", request)),
				[MicroTaskText("fallback-tests-acceptance")]));
		}
		return [.. tasks];
	}

	private static MicroTask MergeTasks(IReadOnlyList<MicroTask> tasks, string prefix)
	{
		var requirements = tasks.Select(task => task.Instruction.Trim().TrimEnd('.'));
		var criteria = tasks
			.SelectMany(task => task.AcceptanceCriteria ?? [])
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return new MicroTask(string.Empty, $"{prefix}: {string.Join("; ", requirements)}.", criteria);
	}

	private static MicroTask[] Renumber(MicroTask[] tasks) =>
		[.. tasks.Select((task, index) => task with { Id = (index + 1).ToString() })];

	// ── Instruction classification ────────────────────────────────────────

	/// <summary>An instruction that only looks at the workspace, which the Explorer already did.</summary>
	private static bool DiscoveryOnly(string instruction)
	{
		var lower = instruction.TrimStart().ToLowerInvariant();
		string[] discovery = ["open ", "read ", "inspect ", "locate ", "analyze ", "identify "];
		string[] mutation = ["implement", "add ", "create ", "write ", "update ", "change ", "fix ", "remove ", "replace "];
		return discovery.Any(lower.StartsWith) && !mutation.Any(lower.Contains);
	}

	/// <summary>An instruction that only runs something, which the Verifier owns.</summary>
	private static bool ValidationOnly(string instruction)
	{
		var lower = instruction.TrimStart().ToLowerInvariant();
		return lower.StartsWith("build ") || lower.StartsWith("run ") || (lower.StartsWith("verify ") && lower.Contains("build"));
	}

	/// <summary>
	/// Whether an instruction authors tests. An explicit tag from the harness's own merge step wins
	/// over the wording, so a merged instruction is not reclassified by the prose it now contains.
	/// </summary>
	private static bool TestAuthoring(string instruction)
	{
		var lower = instruction.ToLowerInvariant();
		if (lower.StartsWith("[implementation task]")) return false;
		if (lower.StartsWith("[test-authoring task]")) return true;
		string[] authoring = ["add ", "create ", "write ", "update "];
		return lower.Contains("test") && authoring.Any(lower.Contains);
	}

	private static bool RequestsProductionChange(string request) =>
		ProductionChangeVerb().IsMatch(request) && !TestOnlyRequest().IsMatch(request.TrimStart());

	private static bool ProductionRequestLine(string line) => !ValidationVocabulary().IsMatch(line);

	// ── Anchor transformation ─────────────────────────────────────────────

	private static bool AnchorTransformation(string request) =>
		request.Contains("addNoFollowTag", StringComparison.OrdinalIgnoreCase)
		|| request.Contains("HtmlHelper.FormatText", StringComparison.OrdinalIgnoreCase)
		|| (request.Contains("anchor", StringComparison.OrdinalIgnoreCase)
			&& request.Contains("nofollow", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// The anchor-transformation Junior assignment. The matcher guidance always applies but is
	/// worded for the task at hand, and only one of the test-authoring or implementation blocks is
	/// included, so the Junior never receives guidance for the half of the work it was not given.
	/// </summary>
	private static string AnchorAssignment(MicroTask task)
	{
		const string id = "junior-developer-anchor";
		var testAuthoring = TestAuthoring(task.Instruction);
		return RoleSection(
			id,
			"assignment",
			("taskId", task.Id),
			("instruction", task.Instruction),
			("matcherGuidance", RoleSection(id, testAuthoring ? "matcher-test-authoring" : "matcher-implementation")),
			("testGuidance", testAuthoring ? RoleSection(id, "test-authoring") : string.Empty),
			("implementationGuidance", testAuthoring ? string.Empty : RoleSection(id, "implementation")),
			("acceptanceCriteria", Serialize(task.AcceptanceCriteria)));
	}

	/// <summary>
	/// Remove anchor-specific guidance from a role prompt when the request is not an anchor task, so
	/// a role is never told to look for HTML rewriting in work that has none.
	/// </summary>
	private string ScopePrompt(string role, string prompt)
	{
		if (_anchorTransformation) return prompt;
		if (role.StartsWith("reviewer", StringComparison.Ordinal))
			return RemovePromptSection(prompt, " For HTML opening-tag transformations", " Every failure finding");
		if (role.StartsWith("junior-developer-repair", StringComparison.Ordinal))
			return RemovePromptSection(prompt, "\r\nWhen an anchor has no rel attribute", "\r\nIf the finding requires adding tests");
		return prompt;
	}

	private static string RemovePromptSection(string prompt, string startMarker, string endMarker)
	{
		var start = prompt.IndexOf(startMarker, StringComparison.Ordinal);
		if (start < 0) return prompt;
		var end = prompt.IndexOf(endMarker, start, StringComparison.Ordinal);
		return end < 0 ? prompt : prompt.Remove(start, end - start);
	}

	// ── Failure classification ────────────────────────────────────────────

	private static bool HandoffOnlyFailure(JuniorReport report) =>
		HandoffFailure().IsMatch(Combined(report.Summary, report.Findings));

	private static bool TransientRoleFailure(JuniorReport report) =>
		TransientRoleFailure(report.Summary, report.Findings);

	/// <summary>
	/// Whether a role failed for a reason another attempt could fix — a transport error, a truncated
	/// response, a deadline — as opposed to having found a real problem with the work.
	/// </summary>
	private static bool TransientRoleFailure(string summary, IReadOnlyList<string>? findings) =>
		TransientFailure().IsMatch(Combined(summary, findings));

	/// <summary>
	/// Whether a verification failure is about the validation apparatus rather than the source, in
	/// which case repeating verification can help and sending it to a Junior cannot.
	/// </summary>
	private static bool ValidationInfrastructureFailure(string summary, IReadOnlyList<string> findings) =>
		InfrastructureFailure().IsMatch(Combined(summary, findings));

	/// <summary>
	/// Whether a finding asks for a concrete source change. Roles routinely report observations —
	/// "the implementation appears correct", "the runner is unavailable" — and handing one to a Junior
	/// spends a repair round asking it to fix something that is not broken, or not source.
	/// </summary>
	private static bool ActionableMutationFinding(string finding)
	{
		if (string.IsNullOrWhiteSpace(finding)) return false;

		// Not source defects: missing test tooling, and the harness's own runner scaffolding.
		if (MissingTestRunner().IsMatch(finding)) return false;
		if (finding.Contains("Runner.cs", StringComparison.OrdinalIgnoreCase) && RunnerScaffolding().IsMatch(finding))
			return false;
		if (RunnerLoading().IsMatch(finding)) return false;

		// Not defects: a role confirming the work is right, or blocked on something upstream.
		if (CorrectButUnexecuted().IsMatch(finding)) return false;
		if (finding.Contains("implements", StringComparison.OrdinalIgnoreCase) && !DefectVocabulary().IsMatch(finding))
			return false;
		if (AlreadyCorrect().IsMatch(finding)) return false;
		if (Confirmation().IsMatch(finding) && !DefectVocabulary().IsMatch(finding)) return false;
		// Tests that could not run because the build is broken: the build failure is the finding, and
		// it will be reported separately.
		if (TestVocabulary().IsMatch(finding) && BlockedVocabulary().IsMatch(finding) && BuildVocabulary().IsMatch(finding))
			return false;

		// Actionable: anything naming a file and a line, a project-file edit, or a compiler diagnostic.
		if (FileAndLine().IsMatch(finding)) return true;
		if (UnsupportedLanguageFeature().IsMatch(finding)) return true;
		if (finding.Contains("<Compile Include", StringComparison.OrdinalIgnoreCase)) return true;
		if (CompilerDiagnostic().IsMatch(finding)) return true;

		// Otherwise, an imperative verb makes it a request for work — unless the work is restoring
		// packages or tooling, which is not a source change.
		return ActionVerb().IsMatch(finding) && !ToolingRestore().IsMatch(finding);
	}

	/// <summary>One role's summary and findings as a single string, for the classifiers above.</summary>
	private static string Combined(string summary, IReadOnlyList<string>? findings) =>
		string.Join(' ', new[] { summary }.Concat(findings ?? []));

	// ── Shared helpers ────────────────────────────────────────────────────

	private static bool Passed(string? status) =>
		status is not null
		&& (status.Equals("pass", StringComparison.OrdinalIgnoreCase)
			|| status.Equals("completed", StringComparison.OrdinalIgnoreCase));

	private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

	private static StageResult Stage<T>(string role, string status, string summary, T value) =>
		new(role, status, summary, JsonSerializer.SerializeToElement(value, JsonOptions));

	/// <summary>Record a stage and report the run so far, so the reader sees roles land as they finish.</summary>
	private void AddStage(List<StageResult> stages, StageResult stage, int repairRounds = 0)
	{
		stages.Add(stage);
		progress?.Invoke(JsonSerializer.Serialize(
			new Result("running", repairRounds, [.. stages], stage.Summary), JsonOptions));
	}

	/// <summary>A role's raw output reduced to a bounded, single-encoding summary line.</summary>
	private static string PlainSummary(string text)
	{
		var normalized = text.ReplaceLineEndings("\n").Trim();
		if (normalized.Length == 0) return "The role returned no structured output.";
		return normalized.Length <= 1200 ? normalized : normalized[..1200];
	}

	// ── Patterns ──────────────────────────────────────────────────────────

	[GeneratedRegex(@"(?<=[.!?])\s+|\n")]
	private static partial Regex SentenceBoundary();

	[GeneratedRegex(@"\b(?:tests?|nunit|build|runner|compiler|validat(?:e|ion))\b", RegexOptions.IgnoreCase)]
	private static partial Regex ValidationVocabulary();

	[GeneratedRegex(@"\b(?:implement|fix|repair|change|update|replace|remove|wire|handle)\b", RegexOptions.IgnoreCase)]
	private static partial Regex ProductionChangeVerb();

	[GeneratedRegex(@"^(?:add|create|write|update)\s+(?:only\s+)?(?:focused\s+)?tests?\b", RegexOptions.IgnoreCase)]
	private static partial Regex TestOnlyRequest();

	[GeneratedRegex(@"invalid structured handoff|no JSON object|returned no structured output|model_empty_response", RegexOptions.IgnoreCase)]
	private static partial Regex HandoffFailure();

	[GeneratedRegex(@"\bHTTP\s+5\d{2}\b|malformed_tool_call|invalid structured handoff|no JSON object|returned no structured output|provider (?:connection|transport)|connection (?:failed|reset)|service unavailable|subagent exceeded its \d+-second deadline|an error occurred while sending the request|unknown ChatFinishReason value", RegexOptions.IgnoreCase)]
	private static partial Regex TransientFailure();

	[GeneratedRegex(@"\b(?:reflection runner|Runner\.cs|Runner\.exe|csc(?:\.exe)?|NUnit (?:console )?runner|test execution evidence|executed-test count|command budget|tool restrictions?|IResult|ResultState)\b", RegexOptions.IgnoreCase)]
	private static partial Regex InfrastructureFailure();

	[GeneratedRegex(@"\b(?:no|missing|without|could not find|not found|not present|not available)\b.{0,80}\bNUnit (?:console )?runner\b|\bNUnit (?:console )?runner\b.{0,80}\b(?:not found|not present|unavailable|missing)\b", RegexOptions.IgnoreCase)]
	private static partial Regex MissingTestRunner();

	[GeneratedRegex(@"\b(?:csc|compiler|compile|IResult|ResultState|System\.IO|Path|reflection runner)\b", RegexOptions.IgnoreCase)]
	private static partial Regex RunnerScaffolding();

	[GeneratedRegex(@"\b(?:reflection runner|test host|working directory|Push-Location|AssemblyResolve|resolve|load)\b[^\r\n]{0,100}\b(?:nunit\.framework(?:\.dll)?|test assembly|runtime dependency)\b|\b(?:nunit\.framework(?:\.dll)?|test assembly|runtime dependency)\b[^\r\n]{0,100}\b(?:working directory|Push-Location|AssemblyResolve|resolve|load)\b", RegexOptions.IgnoreCase)]
	private static partial Regex RunnerLoading();

	[GeneratedRegex(@"\b(?:error|fail|missing|incorrect|wrong|invalid|unsupported|does not|cannot|could not)\b", RegexOptions.IgnoreCase)]
	private static partial Regex DefectVocabulary();

	/// <summary>
	/// A reviewer saying the code looks right but that reading it is no substitute for running it.
	/// That is a request for verification, which the Verifier already owns — not a source change.
	/// </summary>
	[GeneratedRegex(@"(?=.*\bappears? correct\b)(?=.*\b(?:cannot substitute|not a substitute)\b)(?=.*\bexecution\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
	private static partial Regex CorrectButUnexecuted();

	[GeneratedRegex(@"\b(?:correctly (?:written|implemented|registered)|already (?:fixed|implemented|correct|present|resolved)|no (?:source )?defect|matches the requested behavior|will run once (?:the )?(?:other|compile|build) (?:error|issue|defect) is fixed)\b", RegexOptions.IgnoreCase)]
	private static partial Regex AlreadyCorrect();

	[GeneratedRegex(@"\b(?:now calls|is registered|was registered|added to (?:the )?Compile|no (?:public )?API signatures? (?:were )?modified|no temporary artifacts?|no stray files?)\b", RegexOptions.IgnoreCase)]
	private static partial Regex Confirmation();

	[GeneratedRegex(@"\b(?:tests?|test assembly)\b", RegexOptions.IgnoreCase)]
	private static partial Regex TestVocabulary();

	[GeneratedRegex(@"\b(?:blocked|cannot|could not|unable|no executed-test count|not reached)\b", RegexOptions.IgnoreCase)]
	private static partial Regex BlockedVocabulary();

	[GeneratedRegex(@"\b(?:build|compile|compiles|compiler|Nop\.Core)\b", RegexOptions.IgnoreCase)]
	private static partial Regex BuildVocabulary();

	[GeneratedRegex(@"\.(?:cs|csproj|fs|fsproj|vb|vbproj|js|jsx|ts|tsx|py|java|xml|json|razor)(?:\(\d+|:\d+|[^\r\n]{0,40}\bline\s+\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex FileAndLine();

	[GeneratedRegex(@"\.(?:cs|csproj)\b[^\r\n]{0,120}\blines?\s+\d+|\b(?:local function|language feature|C#\s*[6-9]|C#\s*1\d)\b[^\r\n]{0,160}\b(?:rewrite|replace|unsupported|compiler|does not support|only supports)\b", RegexOptions.IgnoreCase)]
	private static partial Regex UnsupportedLanguageFeature();

	[GeneratedRegex(@"\bline\s+\d+\b.*\b(?:invalid\s+C#|syntax|compiler|build failure|CS\d{4})\b", RegexOptions.IgnoreCase)]
	private static partial Regex CompilerDiagnostic();

	[GeneratedRegex(@"\b(?:add|added|edit|fix|change|replace|remove|update|wire|correct|rewrite|handle|use|advance|missing|import)\b", RegexOptions.IgnoreCase)]
	private static partial Regex ActionVerb();

	[GeneratedRegex(@"\b(?:restore|install|download)\s+(?:nuget|package|sdk|targeting pack|tool)", RegexOptions.IgnoreCase)]
	private static partial Regex ToolingRestore();
}

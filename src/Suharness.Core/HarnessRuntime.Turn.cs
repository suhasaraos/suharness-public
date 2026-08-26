using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

/// <summary>
/// The turn loop: accepting a prompt, driving the model and its tool calls to a stopping point,
/// retrying what is retryable, and persisting whatever was produced — including when the turn is
/// interrupted or fails part-way. This is the core of the harness.
/// </summary>
public sealed partial class HarnessRuntime
{
	public async Task SubmitAsync(
		Guid sessionId,
		string prompt,
		Func<HarnessRunProgress, ValueTask>? progress = null,
		CancellationToken cancellationToken = default,
		IReadOnlyList<ImageAttachmentRef>? attachments = null)
	{
		var runGate = _sessionRunGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
		await runGate.WaitAsync(cancellationToken);
		using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
		_running[sessionId] = turnCts;
		try
		{
			// A goal persisted by an earlier process is folded back in before the first turn, and
			// lands disarmed so it cannot drive rounds until a human resumes it.
			if (goals is not null) await goals.RehydrateAsync(sessionId, turnCts.Token);

			var error = await SubmitCoreAsync(sessionId, prompt, goalRound: null, progress, turnCts.Token, attachments);
			await DriveGoalRoundsAsync(sessionId, error, progress, turnCts.Token);
		}
		finally
		{
			if (turnCts.IsCancellationRequested) goals?.Disarm(sessionId);
			_running.TryRemove(sessionId, out _);
			runGate.Release();
		}
	}

	/// <summary>
	/// Same-session goal continuation. While the session's goal is active, armed, and under its round
	/// cap, the runtime keeps re-entering the agent loop with a synthesized round prompt — this is what
	/// makes a goal a long-running objective rather than a note in the transcript. The loop stops on a
	/// failed turn, on interruption, and when the model marks the goal complete or blocked.
	/// </summary>
	private async Task DriveGoalRoundsAsync(
		Guid sessionId,
		string? previousError,
		Func<HarnessRunProgress, ValueTask>? progress,
		CancellationToken cancellationToken)
	{
		if (goals is null || previousError is not null) return;

		try
		{
			while (goals.ShouldContinue(sessionId))
			{
				if (cancellationToken.IsCancellationRequested)
				{
					// An interrupted goal is left active but disarmed: a human decides whether it resumes.
					goals.Disarm(sessionId);
					return;
				}

				goals.RecordRound(sessionId);
				var goal = goals.Get(sessionId);
				if (goal is null) return;

				var error = await SubmitCoreAsync(sessionId, GoalRoundPrompt(goal), goalRound: goal.RoundsStarted, progress, cancellationToken);
				if (error is not null)
				{
					// A failing round would otherwise spin against the same error every iteration.
					goals.Disarm(sessionId);
					return;
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			goals.Disarm(sessionId);
			throw;
		}
	}

	/// <summary>The model-visible instruction that opens one automatic goal round.</summary>
	private static string GoalRoundPrompt(GoalView goal) => PromptCatalog.Render(
		"continuation/goal-round",
		PromptNewline.Lf,
		("objective", JsonSerializer.Serialize(goal.Objective)),
		("round", goal.RoundsStarted.ToString(CultureInfo.InvariantCulture)),
		("maxRounds", goal.MaxGoalRounds.ToString(CultureInfo.InvariantCulture)));

	/// <summary>
	/// Run one turn. Returns null on success, or the error text recorded in the transcript, which the
	/// goal driver uses to decide whether another round is worth attempting.
	/// </summary>
	private async Task<string?> SubmitCoreAsync(
		Guid sessionId,
		string prompt,
		int? goalRound,
		Func<HarnessRunProgress, ValueTask>? progress,
		CancellationToken cancellationToken,
		IReadOnlyList<ImageAttachmentRef>? attachments = null)
	{
		var transcript = await store.GetAsync(sessionId, cancellationToken)
			?? throw new InvalidOperationException($"Session {sessionId} does not exist.");
		var now = DateTimeOffset.UtcNow;
		var trimmedPrompt = prompt.Trim();
		// A goal round is machine-generated, so it never renames the session after the human's prompt.
		var title = transcript.Session.Title == "New session" && goalRound is null
			? trimmedPrompt[..Math.Min(trimmedPrompt.Length, 56)]
			: transcript.Session.Title;
		var turnNumber = _turnCounters.AddOrUpdate(sessionId, 1, (_, current) => current + 1);

		// Token accounting is kept per turn as well as per session, and this is the only place that
		// knows where one turn ends and the next begins. A tool loop makes many requests inside one
		// turn, so nothing about the request stream itself distinguishes a turn's last request from
		// the next turn's first. Opening the turn here resets what the turn has spent and leaves the
		// session's running total alone.
		usage?.BeginTurn(sessionId, turnNumber);

		// Claim queued inbox work — background subagent reports, job completions, injected context.
		// These persist as "inbox" events, which BuildHistory replays as model-visible input, so a
		// report the model was told about on one turn is still there on the next.
		foreach (var message in inbox?.Dequeue(sessionId) ?? [])
		{
			await store.AppendAsync(
				NewEvent(sessionId, "inbox", "system", $"[{InboxLabel(message.Kind)}] {message.Content}"),
				cancellationToken);
		}

		await store.SaveSessionAsync(transcript.Session with { Title = title, UpdatedAt = now }, cancellationToken);
		var modelPrompt = prompt;
		var mirroredAttachments = new List<string>();
		if (workspaceAgentState is not null && imageAttachments is not null && attachments is { Count: > 0 })
		{
			foreach (var attachment in attachments)
			{
				try
				{
					if (await workspaceAgentState.MirrorAttachmentAsync(
						transcript.Session.Workspace,
						sessionId,
						imageAttachments.Read(attachment),
						cancellationToken) is { Length: > 0 } mirrored)
						mirroredAttachments.Add(mirrored);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
				{
					// The durable attachment still exists; a failed workspace mirror must not reject the turn.
				}
			}
		}
		if (mirroredAttachments.Count > 0)
			modelPrompt += $"\n\nAttached image{(mirroredAttachments.Count == 1 ? " is" : "s are")} also available as workspace file{(mirroredAttachments.Count == 1 ? string.Empty : "s")}: {string.Join(", ", mirroredAttachments.Select(path => $"`{path}`"))}.";
		var promptEvent = NewEvent(
			sessionId,
			goalRound is null ? "message" : "goal_round",
			goalRound is null ? "user" : "system",
			modelPrompt,
			attachments: goalRound is null ? attachments : null);
		await store.AppendAsync(promptEvent, cancellationToken);

		// The state the workspace is in before this turn touches anything, which is what an undo of
		// this message returns to. It is recorded against the prompt event so the boundary a reader
		// picks in the transcript and the boundary the restore uses are the same one.
		await CaptureAsync(sessionId, transcript.Session.Workspace, SessionRevert.TurnStart, promptEvent.Id, cancellationToken);

		// Re-read once the inbox and the prompt are on the log, so replay has a single source. An
		// attempt that has to be rebuilt — after a retry or a checkpoint — then reconstructs exactly
		// the same request from the transcript instead of re-appending the prompt by hand.
		transcript = await store.GetAsync(sessionId, cancellationToken) ?? transcript;
		var persistedToolCallIds = transcript.Events
			.Where(item => item.Type == "tool_call" && item.ToolCallId is not null)
			.Select(item => item.ToolCallId!)
			.ToHashSet(StringComparer.Ordinal);
		var persistedToolResultIds = transcript.Events
			.Where(item => item.Type == "tool_result" && item.ToolCallId is not null)
			.Select(item => item.ToolCallId!)
			.ToHashSet(StringComparer.Ordinal);

		var turnContext = new TurnContext(sessionId, transcript.Session.Workspace, turnNumber, 0, cancellationToken);
		if (lifecycle is not null && !await lifecycle.FireBeforeTurnAsync(turnContext))
		{
			const string vetoed = "Turn cancelled before it started.";
			await store.AppendAsync(NewEvent(sessionId, "notice", "system", vetoed), cancellationToken);
			return vetoed;
		}

		// Held outside the attempt so an interrupt can still persist whatever the in-flight attempt
		// had streamed by the time it was cancelled.
		var updates = new List<AgentResponseUpdate>();
		string? turnError = null;
		var retriesUsed = 0;
		var overflowRepairs = 0;
		var toolSegments = 0;
		var unfinishedContinuationMutationBaseline = 0;
		// One counter per remedy, so adding a remedy does not mean adding a local here and another
		// field to the turn summary at the bottom.
		var continuations = new Dictionary<ContinuationKind, int>();
		int Spent(ContinuationKind kind) => continuations.GetValueOrDefault(kind);
		var stopReason = "completed";
		// Step boundaries come from the agent's own function-calling loop, which reports the iteration
		// each tool call belongs to through IAgentLifecycle.ObserveStep. The runtime only opens the
		// first step of an attempt and closes whichever step it ended on.
		void OpenStep(int stepNumber) => lifecycle?.FireBeforeStep(turnContext with { StepNumber = stepNumber });
		void CloseStep(string? stepError) =>
			lifecycle?.FireAfterStep(turnContext with { StepNumber = Math.Max(1, lifecycle.MaxStep(sessionId)) }, stepError);

		// Spend one reserved pass: tell the reader why, put the instruction on the log, and re-read so
		// the next attempt replays a transcript that contains it. Every remedy below does exactly this
		// and differs only in its reason, its instruction, and what the view is told — which is what
		// made eight copies of it, differing in four values, easy to write and easy to let drift.
		async Task ContinueAsync(
			ContinuationKind kind,
			string notice,
			string activity,
			TurnProgressSnapshot state)
		{
			continuations[kind] = Spent(kind) + 1;
			await store.AppendAsync(NewEvent(sessionId, "notice", "system", notice), cancellationToken);
			await store.AppendAsync(
				NewEvent(
					sessionId,
					"continuation",
					"system",
					StatefulContinuation(ContinuationPrompt(kind), transcript.Session.Workspace, prompt, state)),
				cancellationToken);
			transcript = await store.GetAsync(sessionId, cancellationToken) ?? transcript;
			CloseStep(null);
			if (progress is not null) await progress(new HarnessRunProgress(string.Empty, activity));
		}

		try
		{
			var agent = await agentFactory.CreateAsync(
				new AgentContext(
					sessionId,
					transcript.Session.Workspace,
					transcript.Session.AccessMode,
					transcript.Session.Preset,
					Intent: prompt),
				cancellationToken);
			if (agent is null)
			{
				turnError = "No model is selected for this session.";
				stopReason = "no_model";
				await store.AppendAsync(NewEvent(
					sessionId,
					"notice",
					"system",
					turnError + " Open the model picker, choose Manage models, then select a provider and model."), cancellationToken);
				return turnError;
			}
			var maxToolIterations = toolIterationPolicies is null
				? ModelProviderProfile.DefaultMaxToolIterationsPerTurn
				: await toolIterationPolicies.GetMaxIterationsAsync(sessionId, cancellationToken);
			var maxSegmentIterations = toolIterationPolicies is null
				? ModelProviderProfile.DefaultMaxToolIterationsPerSegment
				: await toolIterationPolicies.GetMaxSegmentIterationsAsync(sessionId, cancellationToken);

			// Condense the transcript before replaying it when the session is near the model's context
			// window. A session that simply replays everything eventually crowds the model out of its
			// own context, which surfaces as a model that stops calling tools rather than as an error.
			if (compaction is not null)
			{
				var checkpoint = await compaction.MaybeCompactAsync(sessionId, transcript.Events, agent, cancellationToken);
				if (checkpoint is not null)
				{
					await store.AppendAsync(checkpoint, cancellationToken);
					await store.AppendAsync(
						NewEvent(sessionId, "notice", "system", "Earlier conversation was condensed into a checkpoint to free up context."),
						cancellationToken);
					transcript = await store.GetAsync(sessionId, cancellationToken) ?? transcript;
				}
			}

			var policy = retryPolicies is null
				? RetryPolicy.Default
				: await retryPolicies.GetAsync(sessionId, cancellationToken);
			var autoContinueUnfinished = behaviorPolicies is not null
				&& await behaviorPolicies.AutoContinueUnfinishedTurnsAsync(sessionId, cancellationToken);
			var requestedTestChanges = RequestsTestChanges(prompt);
			// The model-request recovery loop repairs a context overflow by compacting and re-asking;
			// a transient
			// failure is retried under the provider's policy. Anything else ends the turn.
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				updates.Clear();
				var openingStep = toolSegments == 0 ? 1 : (lifecycle?.MaxStep(sessionId) ?? 0) + 1;
				var segmentStartProgress = lifecycle?.Progress(sessionId) ?? new TurnProgressSnapshot(0, 0, 0);
				OpenStep(openingStep);

				// Rebuilt every attempt: a repair between attempts changes what the transcript replays.
				var history = BuildHistory(transcript.Events, imageAttachments);
				var runOptions = Spent(ContinuationKind.Finalization) > 0
					? new ChatClientAgentRunOptions(new ChatOptions { ToolMode = ChatToolMode.None })
					: null;

				AgentResponse? response = null;
				ModelFailure? failure = null;
				try
				{
					var session = await agent.CreateSessionAsync(cancellationToken);
					if (progress is null)
					{
						response = await agent.RunAsync(history, session, runOptions, cancellationToken);
					}
					else
					{
						await foreach (var update in agent.RunStreamingAsync(history, session, runOptions, cancellationToken))
						{
							updates.Add(update);
							await progress(new HarnessRunProgress(update.Text, Activity(update), ProgressEvents(update)));
						}
						response = updates.ToAgentResponse();
					}
					// A completion carrying nothing at all is not an answer. Local servers emit one often
					// enough that treating it as success ends turns silently, so it is classified as a
					// failure the policy may repeat rather than being written to the transcript as blank.
					if (IsDegenerate(response))
					{
						failure = new ModelFailure(
							ModelFailureCode.EmptyResponse,
							"The model returned a completion with no content.");
						response = null;
					}
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
					failure = ModelFailureClassifier.Classify(exception);
				}

				if (response is not null)
				{
					var unfinished = ShouldContinueUnfinished(response);
					await PersistResponseAsync(
						sessionId,
						response,
						persistedToolCallIds,
						persistedToolResultIds,
						cancellationToken);
					var segmentExhausted = ExhaustedSegment(
						lifecycle,
						sessionId,
						openingStep,
						maxSegmentIterations);
					if (segmentExhausted)
					{
						toolSegments++;
						var currentStep = lifecycle?.MaxStep(sessionId) ?? maxToolIterations;
						var progressNow = lifecycle?.Progress(sessionId) ?? segmentStartProgress;
						var anotherFullSegmentFits = currentStep + maxSegmentIterations <= maxToolIterations;
						if (anotherFullSegmentFits && progressNow.SuccessfulMutations == 0 && Spent(ContinuationKind.Implementation) == 0)
						{
							await ContinueAsync(
								ContinuationKind.Implementation,
								"A full discovery segment ended without a workspace change. SuHarness is forcing one implementation-or-blocker pass.",
								"Forcing implementation after discovery",
								progressNow);
							continue;
						}
						if (requestedTestChanges && progressNow.SuccessfulMutations > 0 && !progressNow.TestsChanged
							&& Spent(ContinuationKind.RequestedTests) == 0)
						{
							await ContinueAsync(
								ContinuationKind.RequestedTests,
								"The implementation changed, but the explicitly requested tests are still missing. SuHarness is reserving one test-authoring pass.",
								"Requesting explicitly required tests",
								progressNow);
							continue;
						}
						if (progressNow.RequiresValidation && !progressNow.ValidationSatisfied && Spent(ContinuationKind.Validation) == 0)
						{
							await ContinueAsync(
								ContinuationKind.Validation,
								"The tool segment ended with unvalidated workspace changes. SuHarness is reserving one focused validation pass.",
								"Running reserved validation",
								progressNow);
							continue;
						}
						if (progressNow.RequiresValidation && progressNow.ValidationSatisfied && Spent(ContinuationKind.Finalization) == 0)
						{
							await ContinueAsync(
								ContinuationKind.Finalization,
								"The ordinary tool budget ended after successful validation. SuHarness is reserving one tool-free final response.",
								"Requesting final validated response",
								progressNow);
							continue;
						}
						if (anotherFullSegmentFits && progressNow.AdvancedFrom(segmentStartProgress))
						{
							await ContinueAsync(
								ContinuationKind.ToolSegment,
								$"Tool segment {toolSegments} reached its {maxSegmentIterations}-round-trip checkpoint after making progress. SuHarness is continuing with tools.",
								"Continuing productive tool work",
								progressNow);

							// A segment that ran to its checkpoint is the largest block of transcript the
							// turn will produce, so it is the natural place to condense before going on.
							if (compaction is not null
								&& await compaction.MaybeCompactAsync(sessionId, transcript.Events, agent, cancellationToken) is { } checkpoint)
							{
								await store.AppendAsync(checkpoint, cancellationToken);
								await store.AppendAsync(
									NewEvent(sessionId, "notice", "system", "The completed tool segment was condensed before continuing."),
									cancellationToken);
								transcript = await store.GetAsync(sessionId, cancellationToken) ?? transcript;
							}
							continue;
						}

						var notice = anotherFullSegmentFits
							? $"The last {maxSegmentIterations}-round-trip tool segment produced no new evidence or workspace changes, so SuHarness stopped it as stalled."
							: ExhaustedToolIterations(lifecycle, sessionId, maxToolIterations)
								?? $"This turn used the largest whole tool segment that fits within its {maxToolIterations}-round-trip budget.";
						await store.AppendAsync(NewEvent(sessionId, "notice", "system", notice), cancellationToken);
						stopReason = anotherFullSegmentFits ? "stalled" : "turn_budget_exhausted";
						break;
					}
					if (EndsWithoutAssistantOutputAfterTools(response))
					{
						turnError = "The model returned no final response after using a tool.";
						await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
						stopReason = "model_empty_response";
						break;
					}
					var completionProgress = lifecycle?.Progress(sessionId) ?? new TurnProgressSnapshot(0, 0, 0);
					var suspectPaths = ExistingSuspectPaths(transcript.Session.Workspace, completionProgress.SuspectPaths);
					if (suspectPaths.Count > 0 && Spent(ContinuationKind.ScratchHygiene) == 0)
					{
						await ContinueAsync(
							ContinuationKind.ScratchHygiene,
							$"Potential temporary workspace artifacts were detected: {string.Join(", ", suspectPaths)}. SuHarness is requesting one cleanup pass.",
							"Requesting temporary-file cleanup",
							completionProgress);
						continue;
					}
					if (suspectPaths.Count > 0)
					{
						await store.AppendAsync(
							NewEvent(sessionId, "notice", "system", $"Potential temporary artifacts remain in the workspace: {string.Join(", ", suspectPaths)}."),
							cancellationToken);
					}
					if (Spent(ContinuationKind.Implementation) > 0 && completionProgress.SuccessfulMutations == 0)
					{
						turnError = "The discovery phase was followed by a forced implementation pass, but the workspace still did not change and no concrete blocker was established.";
						await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
						stopReason = "stalled";
						break;
					}
					if (requestedTestChanges && completionProgress.SuccessfulMutations > 0 && !completionProgress.TestsChanged)
					{
						if (Spent(ContinuationKind.RequestedTests) == 0)
						{
							await ContinueAsync(
								ContinuationKind.RequestedTests,
								"The user requested tests, but no test file changed. SuHarness is requesting one focused test-authoring pass.",
								"Requesting explicitly required tests",
								completionProgress);
							continue;
						}

						turnError = "The run changed the implementation but did not add the tests explicitly requested by the user.";
						await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
						stopReason = "requirements_missing";
						break;
					}
					if (completionProgress.RequiresValidation && !completionProgress.ValidationSatisfied)
					{
						if (Spent(ContinuationKind.Validation) == 0)
						{
							await ContinueAsync(
								ContinuationKind.Validation,
								"The workspace changed without successful post-mutation validation. SuHarness is requesting one focused validation pass.",
								"Requesting validation evidence",
								completionProgress);
							continue;
						}

						turnError = completionProgress.TestsChanged
							? "The run changed tests but ended without a successful test execution after the latest mutation."
							: "The run changed the workspace but ended without a successful build or test after the latest mutation.";
						await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
						stopReason = "validation_missing";
						break;
					}
					// Each automatic continuation has to earn the next one: without the baseline, a model
					// that stops mid-sentence twice in a row would spend both passes going nowhere.
					var unfinishedMadeProgress = Spent(ContinuationKind.Automatic) == 0
						|| completionProgress.SuccessfulMutations > unfinishedContinuationMutationBaseline;
					if (autoContinueUnfinished
						&& unfinished
						&& Spent(ContinuationKind.Automatic) < MaxUnfinishedContinuations
						&& unfinishedMadeProgress)
					{
						unfinishedContinuationMutationBaseline = completionProgress.SuccessfulMutations;
						await ContinueAsync(
							ContinuationKind.Automatic,
							"The model ended before completing its response. SuHarness is continuing it automatically "
								+ $"({Spent(ContinuationKind.Automatic) + 1} of {MaxUnfinishedContinuations}).",
							"Continuing unfinished work",
							completionProgress);
						continue;
					}
					if (ExhaustedToolIterations(lifecycle, sessionId, maxToolIterations) is { } exhausted)
						await store.AppendAsync(NewEvent(sessionId, "notice", "system", exhausted), cancellationToken);
					else if (StopNotice(response) is { } notice)
						await store.AppendAsync(NewEvent(sessionId, "notice", "system", notice), cancellationToken);
					break;
				}

				// The provider has now measured the request the local estimate got wrong, so the repair
				// compacts without consulting the threshold and re-asks within the same turn.
				if (failure!.Code == ModelFailureCode.ContextWindowExceeded
					&& compaction is not null
					&& overflowRepairs < MaxOverflowRepairs)
				{
					var repair = await compaction.CompactForOverflowAsync(sessionId, transcript.Events, agent, cancellationToken);
					if (repair is not null)
					{
						await store.AppendAsync(repair, cancellationToken);
						await store.AppendAsync(
							NewEvent(
								sessionId,
								"notice",
								"system",
								"The provider rejected the request for exceeding its context window. Earlier conversation "
									+ "was condensed into a checkpoint and the request was sent again."),
							cancellationToken);
						transcript = await store.GetAsync(sessionId, cancellationToken) ?? transcript;
						overflowRepairs++;
						CloseStep(failure.Message);
						await DiscardAttemptAsync(progress, "Recovering from a context overflow");
						continue;
					}
				}

				if (policy.ShouldRetry(failure.Code, retriesUsed))
				{
					var delay = policy.Delay(retriesUsed, failure.ProviderRetryAfter);
					retriesUsed++;
					var attemptLabel = policy.Mode == RetryMode.Always
						? $"attempt {retriesUsed + 1}"
						: $"attempt {retriesUsed + 1} of {policy.MaxRetries + 1}";
					await store.AppendAsync(
						NewEvent(
							sessionId,
							"notice",
							"system",
							$"The model request failed ({FailureLabel(failure.Code)}) and is being retried in "
								+ $"{delay.TotalMilliseconds:F0} ms — {attemptLabel}."),
						cancellationToken);
					CloseStep(failure.Message);
					await DiscardAttemptAsync(progress, "Retrying the model request");
					await Task.Delay(delay, cancellationToken);
					continue;
				}

				turnError = FriendlyModelError(failure);
				stopReason = $"model_{failure.Code.ToString().ToLowerInvariant()}";
				await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
				break;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			turnError = "Generation stopped.";
			stopReason = "interrupted";
			await PersistInterruptedAsync(sessionId, updates, persistedToolCallIds, persistedToolResultIds);
			await store.AppendAsync(NewEvent(sessionId, "notice", "system", "Generation stopped."), CancellationToken.None);
			throw;
		}
		catch (ModelConfigurationException exception)
		{
			turnError = exception.Message;
			stopReason = "model_configuration";
			await store.AppendAsync(NewEvent(sessionId, "error", "system", exception.Message), cancellationToken);
		}
		catch (Exception exception)
		{
			// Everything the model request itself can throw is classified inside the loop; reaching
			// here means the failure came from the harness around it.
			turnError = FriendlyModelError(ModelFailureClassifier.Classify(exception));
			stopReason = "harness_error";
			await store.AppendAsync(NewEvent(sessionId, "error", "system", turnError), cancellationToken);
		}
		finally
		{
			CloseStep(turnError);
			var finalProgress = lifecycle?.Progress(sessionId) ?? new TurnProgressSnapshot(0, 0, 0);
			var outcome = stopReason switch
			{
				"completed" when turnError is null => "completed",
				"interrupted" => "interrupted",
				"stalled" or "turn_budget_exhausted" when finalProgress.SuccessfulMutations > 0 => "partial",
				"stalled" or "turn_budget_exhausted" => "failed",
				_ when turnError is null => "partial",
				_ => "failed",
			};
			var summary = JsonSerializer.Serialize(new
			{
				turnNumber,
				outcome,
				stopReason,
				modelRoundTrips = lifecycle?.MaxStep(sessionId) ?? 0,
				retries = retriesUsed,
				overflowRepairs,
				toolSegments,
				implementationContinuations = Spent(ContinuationKind.Implementation),
				requestedTestsContinuations = Spent(ContinuationKind.RequestedTests),
				unfinishedContinuations = Spent(ContinuationKind.Automatic),
				validationContinuations = Spent(ContinuationKind.Validation),
				finalizationContinuations = Spent(ContinuationKind.Finalization),
				hygieneContinuations = Spent(ContinuationKind.ScratchHygiene),
				toolCalls = finalProgress.ToolCalls,
				uniqueToolResults = finalProgress.UniqueResults,
				mutations = finalProgress.SuccessfulMutations,
				testsChanged = finalProgress.TestsChanged,
				successfulBuilds = finalProgress.SuccessfulBuildsAfterLastMutation,
				successfulTests = finalProgress.SuccessfulTestsAfterLastMutation,
				validationSatisfied = finalProgress.ValidationSatisfied,
				validationStatus = !finalProgress.RequiresValidation
					? "not_required"
					: finalProgress.ValidationSatisfied ? "satisfied" : "missing",
				suspectPaths = finalProgress.SuspectPaths ?? [],
			});
			await store.AppendAsync(NewEvent(sessionId, "turn_summary", "system", summary), CancellationToken.None);
			lifecycle?.FireAfterTurn(turnContext, turnError);
			// The turn is the boundary usage is persisted at: every request it made has finished, and
			// writing once here is what keeps a streamed answer from turning into a write per token.
			// An interrupted turn still spent what it spent, so it flushes on the way out like any other.
			if (usage is not null) await usage.FlushAsync(sessionId, CancellationToken.None);
			// An interrupted turn still changed files, so it is captured on the way out like any other.
			// The turn's own token is spent by then, so the capture runs on its own deadline instead.
			await CaptureAsync(
				sessionId,
				transcript.Session.Workspace,
				SessionRevert.TurnEnd,
				promptEvent.Id,
				CancellationToken.None);
			if (workspaceAgentState is not null)
			{
				try
				{
					var completed = await store.GetAsync(sessionId, CancellationToken.None);
					if (completed is not null) await workspaceAgentState.ProjectSessionAsync(completed, CancellationToken.None);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
				{
					// Projection failures never alter the durable transcript or the turn outcome.
				}
			}
		}
		return turnError;
	}

	/// <summary>
	/// Record one workspace snapshot on the transcript. Capture is best-effort: a workspace too large
	/// to capture inside the deadline leaves the turn uncovered and says so, rather than delaying or
	/// failing work the user asked for to protect it.
	/// </summary>
	private async Task CaptureAsync(
		Guid sessionId,
		string workspace,
		string kind,
		Guid boundaryEventId,
		CancellationToken cancellationToken)
	{
		if (snapshots is not { Enabled: true } || string.IsNullOrWhiteSpace(workspace)) return;
		try
		{
			var capture = await snapshots.CaptureAsync(workspace, cancellationToken);
			if (capture is null)
			{
				// Only worth saying once per turn: a reader needs to know undo will not reach this turn,
				// and needs to be able to tell "not yet" from "not working".
				if (kind == SessionRevert.TurnStart)
				{
					await store.AppendAsync(
						NewEvent(sessionId, "notice", "system", snapshots.IsReady(workspace)
							? "The workspace could not be snapshotted, so this turn cannot be undone."
							: "The workspace is still being indexed for undo, so this turn cannot be rolled back yet."),
						CancellationToken.None);
				}
				return;
			}

			var record = new SnapshotRecord(
				capture.TreeHash,
				kind,
				boundaryEventId,
				capture.ElapsedMs,
				capture.Skipped.Count == 0 ? null : capture.Skipped);
			await store.AppendAsync(
				NewEvent(sessionId, SessionRevert.SnapshotType, "system", SessionRevert.Serialize(record)),
				CancellationToken.None);
		}
		catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
		{
			// A snapshot is a safety net around the turn, never a precondition for it.
			await store.AppendAsync(
				NewEvent(sessionId, "notice", "system", $"Workspace snapshot failed: {exception.Message}"),
				CancellationToken.None);
		}
	}

	/// <summary>
	/// Whether a completed response carries nothing the reader or the loop can act on: no tool call,
	/// no tool result, and no non-blank text.
	/// </summary>
	private static bool IsDegenerate(AgentResponse response) =>
		!response.Messages.Any(message => message.Contents.Any(content => content switch
		{
			FunctionCallContent or FunctionResultContent => true,
			TextContent text => text.Text.Trim().Length > 0,
			_ => false,
		}));

	/// <summary>
	/// Agent Framework returns the complete inner tool exchange. A provider can stop after receiving
	/// a tool result without contributing a terminal assistant message, so the earlier call must not
	/// make that aggregate response look complete.
	/// </summary>
	private static bool EndsWithoutAssistantOutputAfterTools(AgentResponse response)
	{
		var usedTools = false;
		var hasAssistantOutputAfterTools = false;
		foreach (var content in response.Messages.SelectMany(message => message.Contents))
		{
			switch (content)
			{
				case FunctionCallContent or FunctionResultContent:
					usedTools = true;
					hasAssistantOutputAfterTools = false;
					break;
				case TextContent text when usedTools && !string.IsNullOrWhiteSpace(text.Text):
					hasAssistantOutputAfterTools = true;
					break;
			}
		}
		return usedTools && !hasAssistantOutputAfterTools;
	}

	/// <summary>
	/// Tell the reader an attempt was thrown away, so a partial answer already streamed into the view
	/// does not sit above the answer that replaces it. Nothing from a discarded attempt is persisted,
	/// which is what keeps a retry a genuine repeat of the same request rather than a continuation.
	/// </summary>
	private static async ValueTask DiscardAttemptAsync(Func<HarnessRunProgress, ValueTask>? progress, string activity)
	{
		if (progress is null) return;
		await progress(new HarnessRunProgress(
			string.Empty,
			activity,
			[new HarnessRunEvent(Guid.NewGuid().ToString("N"), "retry", activity)]));
	}

	/// <summary>
	/// Persist what an interrupted turn actually did. The tool calls it already made had real effects
	/// — files written, commands run — so dropping them leaves a transcript that disagrees with the
	/// workspace, and a model that repeats the work on the next turn because it cannot see it.
	/// </summary>
	/// <remarks>
	/// A call whose result never arrived is closed with an explicit aborted result: providers reject a
	/// conversation containing a call with no matching result, so leaving one open would break every
	/// later turn in the session. The tool loop runs inside the Agent Framework and the response
	/// stream does not reveal which calls were dispatched, so the wording claims only what is known.
	/// </remarks>
	private async Task PersistInterruptedAsync(
		Guid sessionId,
		IReadOnlyList<AgentResponseUpdate> updates,
		HashSet<string> persistedToolCallIds,
		HashSet<string> persistedToolResultIds)
	{
		if (updates.Count == 0) return;
		var response = updates.ToAgentResponse();
		await PersistResponseAsync(
			sessionId,
			response,
			persistedToolCallIds,
			persistedToolResultIds,
			CancellationToken.None);

		var answered = response.Messages
			.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
			.Select(result => result.CallId)
			.ToHashSet(StringComparer.Ordinal);
		foreach (var call in response.Messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>()))
		{
			if (!answered.Add(call.CallId)) continue;
			await store.AppendAsync(
				NewEvent(
					sessionId,
					"tool_result",
					"tool",
					"Error: tool call aborted because the turn was interrupted.",
					Guid.NewGuid().ToString("N"),
					call.CallId),
				CancellationToken.None);
		}
	}
}

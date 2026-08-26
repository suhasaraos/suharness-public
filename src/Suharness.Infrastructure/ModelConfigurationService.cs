using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Suharness.Abstractions;
using Suharness.Tools;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Infrastructure;

public sealed partial class ModelConfigurationService(
	string root,
	IDataProtectionProvider protectionProvider,
	HttpClient httpClient,
	PipelineTransport? modelTransport = null,
	IUserQuestionBroker? userQuestions = null,
	IPlanModeStore? planModeStore = null,
	IGoalService? goalService = null,
	IJobService? jobService = null,
	IAgentLifecycle? lifecycle = null,
	ISubagentService? subagentService = null,
	ISessionStore? sessionStoreRef = null,
	ISpillStore? spillStore = null,
	RepeatToolReminder? repeatReminder = null,
	IMcpToolProvider? mcp = null,
	ICapabilityEscalation? capabilities = null,
	ILanguageServerService? languageServers = null,
	ISessionUsageTracker? usageTracker = null,
	IWorkspaceIndex? workspaceIndex = null,
	ISymbolIndex? symbols = null,
	ISessionScratchStore? scratch = null) : IModelConfigurationService, IAgentFactory, IRetryPolicyProvider, IToolIterationPolicyProvider, IModelBehaviorPolicyProvider
{
	private static readonly object NullToolResult = new();

	// Enums are written by name so a provider's retry policy stays hand-editable in models.json.
	// The converter still reads the numeric form, so a file written before this change loads unchanged.
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
	};
	private readonly string _settingsPath = Path.Combine(Path.GetFullPath(root), "models.json");
	private readonly string _credentialsPath = Path.Combine(Path.GetFullPath(root), "credentials.bin");
	private readonly IDataProtector _protector = protectionProvider.CreateProtector("Suharness.ModelCredentials.v1");
	private readonly SemaphoreSlim _gate = new(1, 1);

	// One toolset per session: the read-before-mutate observation log is session state, so it must
	// outlive the individual agent instances rebuilt on every turn.
	private readonly ConcurrentDictionary<Guid, SessionToolset> _toolsets = new();

	public async Task<ModelConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			var credentials = await ReadCredentialsAsync(cancellationToken);
			return new ModelConfigurationSnapshot(
				state.Providers.Select(profile => new ConfiguredModelProvider(profile, credentials.ContainsKey(profile.Id))).ToArray(),
				state.DefaultSelection);
		}
		finally { _gate.Release(); }
	}

	public async Task SaveProviderAsync(ModelProviderProfile profile, string? apiKey, CancellationToken cancellationToken = default)
	{
		Validate(profile);
		if (!string.IsNullOrWhiteSpace(apiKey)) ValidateCredential(profile, apiKey.Trim());
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			var providers = state.Providers.Where(item => item.Id != profile.Id).Append(profile).OrderBy(item => item.DisplayName).ToArray();
			var selection = state.DefaultSelection;
			if (selection is null || selection.Provider == profile.Id && profile.Models.All(model => model.Id != selection.Model))
			{
				selection = new ModelSelection(profile.Id, profile.Models[0].Id);
			}
			var sessionSelections = state.SessionSelections
				.Where(pair => pair.Value.Provider != profile.Id || profile.Models.Any(model => model.Id == pair.Value.Model))
				.ToDictionary();
			var teamConfigurations = FilterTeamConfigurations(state.TeamConfigurations, providers);
			await WriteStateAsync(state with
			{
				Providers = providers,
				DefaultSelection = selection,
				SessionSelections = sessionSelections,
				TeamConfigurations = teamConfigurations,
			}, cancellationToken);

			if (apiKey is not null)
			{
				var credentials = await ReadCredentialsAsync(cancellationToken);
				var trimmed = apiKey.Trim();
				var changed = true;
				if (trimmed.Length == 0) changed = credentials.Remove(profile.Id);
				else credentials[profile.Id] = trimmed;
				if (changed) await WriteCredentialsAsync(credentials, cancellationToken);
			}
		}
		finally { _gate.Release(); }
	}

	public async Task DeleteProviderAsync(string providerId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			var providers = state.Providers.Where(item => item.Id != providerId).ToArray();
			var nextDefault = state.DefaultSelection?.Provider == providerId
				? providers.FirstOrDefault() is { } first ? new ModelSelection(first.Id, first.Models[0].Id) : null
				: state.DefaultSelection;
			var sessionSelections = state.SessionSelections
				.Where(pair => pair.Value.Provider != providerId)
				.ToDictionary();
			var teamConfigurations = FilterTeamConfigurations(state.TeamConfigurations, providers);
			await WriteStateAsync(state with { Providers = providers, DefaultSelection = nextDefault, SessionSelections = sessionSelections, TeamConfigurations = teamConfigurations }, cancellationToken);
			var credentials = await ReadCredentialsAsync(cancellationToken);
			if (credentials.Remove(providerId)) await WriteCredentialsAsync(credentials, cancellationToken);
		}
		finally { _gate.Release(); }
	}

	public async Task<IReadOnlyList<ModelDescriptor>> DiscoverModelsAsync(
		ModelProviderProfile draft,
		string? apiKey,
		CancellationToken cancellationToken = default)
	{
		ValidateEndpoint(draft.BaseUrl);
		var key = string.IsNullOrWhiteSpace(apiKey) ? await CredentialAsync(draft.Id, cancellationToken) : apiKey.Trim();
		if (!string.IsNullOrWhiteSpace(key)) ValidateCredential(draft, key);
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{draft.BaseUrl.TrimEnd('/')}/models");
		if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		using var response = await httpClient.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		// Context window drives compaction and pricing drives cost estimates, so both are taken from
		// discovery when the endpoint reports them. OpenRouter publishes context_length and a pricing
		// block for every model; local servers usually publish neither, and there the fields stay
		// unset for the operator to fill in with usable — not advertised — values. A listing that is
		// not the expected shape yields no models rather than failing the fetch.
		if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
		var source = ModelPricing.SourceLabel(draft.Id, draft.BaseUrl);
		var fetchedAt = DateTimeOffset.UtcNow;
		return data.EnumerateArray()
			.Select(item => ModelPricing.Read(item, source, fetchedAt))
			.OfType<ModelDescriptor>()
			.OrderBy(model => model.Id, StringComparer.Ordinal)
			.ToArray();
	}

	public async Task<ModelSelection?> GetSelectionAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			var sessionSelection = state.SessionSelections.GetValueOrDefault(sessionId);
			return IsConfigured(state, sessionSelection) ? sessionSelection
				: IsConfigured(state, state.DefaultSelection) ? state.DefaultSelection
				: null;
		}
		finally { _gate.Release(); }
	}

	private static bool IsConfigured(ModelState state, ModelSelection? selection) =>
		selection is not null
		&& state.Providers.Any(provider =>
			provider.Id == selection.Provider
			&& provider.Models.Any(model => model.Id == selection.Model));

	public async Task SelectAsync(Guid sessionId, ModelSelection selection, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			var provider = state.Providers.SingleOrDefault(item => item.Id == selection.Provider);
			if (provider is null || provider.Models.All(model => model.Id != selection.Model))
				throw new InvalidOperationException("The selected provider/model is not configured.");
			var sessions = new Dictionary<Guid, ModelSelection>(state.SessionSelections) { [sessionId] = selection };
			await WriteStateAsync(state with { DefaultSelection = selection, SessionSelections = sessions }, cancellationToken);
		}
		finally { _gate.Release(); }
	}

	public async Task<TeamConfiguration> GetTeamConfigurationAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			return state.TeamConfigurations?.GetValueOrDefault(sessionId) ?? TeamConfiguration.Disabled;
		}
		finally { _gate.Release(); }
	}

	public async Task SetTeamConfigurationAsync(Guid sessionId, TeamConfiguration configuration, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var state = await ReadStateAsync(cancellationToken);
			if (configuration.Enabled)
			{
				var missing = Enum.GetValues<CodingPersona>().Where(persona => !configuration.Assignments.ContainsKey(persona)).ToArray();
				if (missing.Length > 0)
					throw new InvalidOperationException($"Team mode requires a model for every persona. Missing: {string.Join(", ", missing)}.");
			}
			foreach (var selection in configuration.Assignments.Values)
			{
				if (!IsConfigured(state, selection))
					throw new InvalidOperationException($"Team model '{selection.Provider}/{selection.Model}' is not configured.");
			}
			if (configuration.Enabled && planModeStore is not null)
				await planModeStore.SetAsync(sessionId, false, cancellationToken);
			var configurations = new Dictionary<Guid, TeamConfiguration>(state.TeamConfigurations ?? [])
			{
				[sessionId] = configuration with { Assignments = new(configuration.Assignments) },
			};
			await WriteStateAsync(state with { TeamConfigurations = configurations }, cancellationToken);
			_toolsets.TryRemove(sessionId, out _);
		}
		finally { _gate.Release(); }
	}

	public async ValueTask<AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default)
	{
		var selection = context.ModelOverride ?? await GetSelectionAsync(context.SessionId, cancellationToken);
		if (selection is null) return null;
		var snapshot = await GetSnapshotAsync(cancellationToken);
		var provider = snapshot.Providers.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile;
		if (provider is null) return null;
		// A selection naming a model the profile no longer lists is stale rather than fatal, and is
		// reported the same way as a deleted provider on the line above.
		var model = provider.Models.FirstOrDefault(item => item.Id == selection.Model);
		if (model is null) return null;
		var endpoint = new Uri(provider.BaseUrl);
		var key = await CredentialAsync(provider.Id, cancellationToken);
		if (string.IsNullOrWhiteSpace(key))
		{
			// Local servers such as KoboldCpp are deliberately unauthenticated, but a remote gateway
			// without a key returns an opaque HTTP 401. Say what is actually wrong instead.
			if (provider.RequiresApiKey)
			{
				throw new ModelConfigurationException(
					$"No API key is configured for provider '{provider.Id}' ({provider.BaseUrl}). "
					+ "Open the model picker, choose Manage models, and add the provider's API key.");
			}
			key = "not-required";
		}
		ValidateCredential(provider, key);
		// NetworkTimeout bounds silence between reads, not the length of a response, so raising it
		// cannot make a turn run longer than the model actually takes. The library default of 100
		// seconds is shorter than a large local model's prompt-processing pause before its first
		// token, which fails the request and then retries it — reprocessing the prompt each time.
		var options = new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = provider.IdleTimeout };
		// The client library retries 5xx, 429, and transport failures on its own, silently and before
		// the harness ever sees them. That hides the failure from the transcript, multiplies with the
		// harness's own policy, and cannot repair a context overflow. Turning it off leaves one retry
		// authority — the provider's configured RetryPolicy, applied by the runtime's recovery loop.
		options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
		if (modelTransport is not null) options.Transport = modelTransport;
		var chatClient = new ChatClient(
			selection.Model,
			new ApiKeyCredential(key),
			options);

		// The toolset is keyed by session so a file read on an earlier turn still satisfies the
		// observation policy on a later one, and is rebuilt if the session's workspace changes.
		var interactiveQuestions = context.Interactive ? userQuestions : null;
		var inlineCap = provider.InlineToolByteCap;
		var scratchRoot = scratch?.GetOrCreate(context.SessionId);
		var planMode = planModeStore is not null && await planModeStore.IsActiveAsync(context.SessionId, cancellationToken);
		var teamConfiguration = context.ToolProfile == AgentToolProfile.Standard
			? await GetTeamConfigurationAsync(context.SessionId, cancellationToken)
			: TeamConfiguration.Disabled;
		if (planMode && teamConfiguration.Enabled)
		{
			teamConfiguration = teamConfiguration with { Enabled = false };
			await SetTeamConfigurationAsync(context.SessionId, teamConfiguration, cancellationToken);
		}
		SessionToolset Build() => new(
			context.Workspace, context.AccessMode, context.SessionId, interactiveQuestions, planModeStore, context.Preset,
			goals: goalService, jobs: jobService, subagents: subagentService, sessionStore: sessionStoreRef, lifecycle: lifecycle,
			spillStore: spillStore, maxInlineBytes: inlineCap, mcp: mcp,
			capabilities: capabilities, languageServers: languageServers,
			workspaceIndex: workspaceIndex, symbols: symbols, scratchRoot: scratchRoot, toolProfile: context.ToolProfile,
			teamConfiguration: teamConfiguration);
		var toolset = _toolsets.AddOrUpdate(
			context.SessionId,
			_ => Build(),
			(_, existing) => PathsEqual(existing.WorkspaceRoot, context.Workspace)
				&& existing.AccessMode == context.AccessMode
				&& existing.Preset == context.Preset
				&& existing.ToolProfile == context.ToolProfile
				&& existing.TeamEnabled == teamConfiguration.Enabled
				&& existing.MaxInlineBytes == inlineCap
				? existing
				: Build());

		var instructions = HarnessSystemPrompt.Build(
			toolset.WorkspaceRoot,
			toolset.AccessMode,
			selection.Model,
			planMode,
			toolset.Preset,
			toolset.Orchestration,
			toolset.SemanticNavigationAvailable,
			toolset.ScratchRoot,
			toolset.ToolProfile,
			toolset.TeamEnabled);

		// The function-calling loop is composed here rather than left to the agent's defaults, because
		// its settings are harness policy: how many model round-trips one turn may spend, and who gets
		// told which round-trip a tool call belongs to. UseProvidedChatClientAsIs stops the agent from
		// layering a second, unconfigured function-invocation client over this one.
		var sessionId = context.SessionId;
		// Which model round-trips have dispatched at least one tool call. Several calls in one response
		// share a round, so the count of distinct rounds — not the count of calls — is what advances a
		// step. The loop's own iteration numbering is used only for identity here: it is documented as
		// one-based and observed to be zero-based, so deriving the step from how many distinct rounds
		// have dispatched keeps step numbers correct whichever base a future version reports.
		var dispatchedRoundGate = new object();
		var lastDispatchedIteration = -1;
		var observedStep = 1;
		// The session's running totals are seeded before the first request of the turn, so a second
		// turn adds to what earlier turns spent instead of starting the count again.
		if (usageTracker is not null) await usageTracker.EnsureLoadedAsync(context.SessionId, cancellationToken);
		var completedToolResults = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
		if (sessionStoreRef is not null && await sessionStoreRef.GetAsync(context.SessionId, cancellationToken) is { } persisted)
		{
			foreach (var result in persisted.Events.Where(item => item.Type == "tool_result" && item.ToolCallId is not null))
				completedToolResults[result.ToolCallId!] = result.Content;
		}
		var toolCallGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
		var toolFingerprintGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
		var pipeline = chatClient.AsIChatClient()
			.AsBuilder()
			.Use(inner =>
			{
				return new McpMultimodalFunctionInvokingChatClient(inner)
				{
					MaximumIterationsPerRequest = provider.ToolSegmentCap,
					FunctionInvoker = async (invocation, token) =>
					{
						// A dispatched tool call guarantees another model request, so it opens the step that
						// request belongs to. Reporting the round the call came from instead would never open
						// a step for the final, answer-producing request, because no tool runs during it.
						int step;
						lock (dispatchedRoundGate)
						{
							if (invocation.Iteration != lastDispatchedIteration)
							{
								lastDispatchedIteration = invocation.Iteration;
								observedStep = Math.Max(observedStep + 1, (lifecycle?.Current(sessionId)?.StepNumber ?? 1) + 1);
							}
							step = observedStep;
						}
						lifecycle?.ObserveStep(sessionId, step);

						var callId = invocation.CallContent.CallId;
						if (string.IsNullOrWhiteSpace(callId))
							return await invocation.Function.InvokeAsync(invocation.Arguments, token);

						var gate = toolCallGates.GetOrAdd(callId, _ => new SemaphoreSlim(1, 1));
						await gate.WaitAsync(token);
						try
						{
							if (completedToolResults.TryGetValue(callId, out var completed))
							{
								AgentLoopDiagnostics.Tool(
									lifecycle?.Current(sessionId), callId, invocation.Function.Name, "CALL_ID_REPLAY", null, null);
								return ReferenceEquals(completed, NullToolResult) ? null : completed;
							}

							var turn = lifecycle?.Current(sessionId)
								?? new TurnContext(sessionId, context.Workspace, 0, step, token);
							var canonical = RepeatToolReminder.Canonicalize(invocation.Arguments);
							var fingerprint = RepeatToolReminder.Fingerprint(invocation.Function.Name, canonical);
							var fingerprintGate = toolFingerprintGates.GetOrAdd(fingerprint, _ => new SemaphoreSlim(1, 1));
							await fingerprintGate.WaitAsync(token);
							object? result;
							try
							{
								var decision = repeatReminder?.Before(
									sessionId, turn.TurnNumber, invocation.Function.Name, invocation.Arguments);
								if (decision is { ShouldExecute: false })
								{
									result = decision.SyntheticResult ?? "Duplicate tool call blocked.";
									AgentLoopDiagnostics.Tool(turn, callId, invocation.Function.Name, decision.Action.ToString(), decision, null);
								}
								else
								{
									result = await invocation.Function.InvokeAsync(invocation.Arguments, token);
									var completion = decision is null
										? null
										: repeatReminder!.Complete(
											sessionId, turn.TurnNumber, decision, RenderToolResult(result));
									if (completion?.Reminder is { } reminder)
										result = RepeatToolReminder.Attach(RenderToolResult(result), reminder);
									AgentLoopDiagnostics.Tool(turn, callId, invocation.Function.Name, "EXECUTE", decision, completion);
								}
							}
							finally { fingerprintGate.Release(); }
							completedToolResults[callId] = result ?? NullToolResult;
							return result;
						}
						finally { gate.Release(); }
					},
				};
			})
			.Use(inner => new AgentLoopDiagnosticsChatClient(inner, context.SessionId, lifecycle))
			// Registered after the function-calling loop, so it wraps the model client rather than the
			// loop: each round-trip the loop makes is one request the provider billed, and each is
			// counted once. Wrapping the loop instead would count a whole turn as a single request.
			// The window and the completion cap are handed over here because this is where the selected
			// model is resolved. Recording them against each request is what lets a context reading be
			// taken later without re-resolving a selection that may since have changed.
			.Use(inner => usageTracker is null
				? inner
				: new UsageTrackingChatClient(
					inner,
					usageTracker,
					context.SessionId,
					provider.Id,
					selection.Model,
					lifecycle,
					model.ContextWindow,
					model.MaxOutputTokens))
			.Build();

		var chatOptions = new ChatOptions
		{
			Instructions = instructions,
			Tools = toolset.BuildTools(context.Intent),
			Temperature = provider.Temperature,
			TopP = provider.TopP,
			TopK = provider.TopK,
			MaxOutputTokens = context.MaxOutputTokensOverride ?? model.MaxOutputTokens,
			ResponseFormat = context.JsonOutputType is not null
				? Microsoft.Extensions.AI.ChatResponseFormat.ForJsonSchema(
					context.JsonOutputType,
					new JsonSerializerOptions(JsonSerializerDefaults.Web),
					"handoff")
				: context.JsonOutput ? Microsoft.Extensions.AI.ChatResponseFormat.Json : null,
		};
		// A session's own choice wins over the model's catalog default; null at both levels sends no
		// reasoning field at all, which is the only thing an endpoint that has never heard of the
		// parameter will accept.
		var reasoningEffort = selection.ReasoningEffort ?? model.ReasoningEffort;
		// Neither top-k nor reasoning effort exists on ChatOptions, so both are written straight onto
		// the OpenAI request. They have to share one factory: assigning a second would replace the
		// first rather than add to it, and the knob that lost would go missing without an error.
		if (provider.TopK is not null || reasoningEffort is not null)
		{
			chatOptions.RawRepresentationFactory = _ =>
			{
				var openAiOptions = new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is experimental.
				if (provider.TopK is { } topK) openAiOptions.Patch.Set("$.top_k"u8, topK);
				if (reasoningEffort is { } effort) openAiOptions.Patch.Set("$.reasoning_effort"u8, WireName(effort));
#pragma warning restore SCME0001
				return openAiOptions;
			};
		}

		return pipeline.AsAIAgent(new ChatClientAgentOptions
		{
			Name = "SuHarness",
			ChatOptions = chatOptions,
			UseProvidedChatClientAsIs = true,
		});
	}

	/// <summary>
	/// The spelling OpenAI-compatible endpoints expect for a reasoning level. It is written out rather
	/// than lower-casing the enum name so that renaming a member cannot quietly change what goes on
	/// the wire.
	/// </summary>
	private static string WireName(ReasoningEffortLevel effort) => effort switch
	{
		ReasoningEffortLevel.Minimal => "minimal",
		ReasoningEffortLevel.Low => "low",
		ReasoningEffortLevel.Medium => "medium",
		ReasoningEffortLevel.High => "high",
		_ => throw new ArgumentOutOfRangeException(nameof(effort)),
	};

	private static string RenderToolResult(object? result) => result switch
	{
		null => string.Empty,
		string text => text,
		JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
		JsonElement json => json.ToString(),
		_ => result.ToString() ?? string.Empty,
	};

	/// <summary>
	/// The retry policy of the provider this session is about to call. A session with no selection,
	/// or one naming a provider that has since been deleted, falls back to the default policy rather
	/// than failing: the caller is on a recovery path already.
	/// </summary>
	public async ValueTask<RetryPolicy> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var selection = await GetSelectionAsync(sessionId, cancellationToken);
		if (selection is null) return RetryPolicy.Default;
		var snapshot = await GetSnapshotAsync(cancellationToken);
		var profile = snapshot.Providers.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile;
		if (profile is null) return RetryPolicy.Default;
		try { return profile.EffectiveRetryPolicy; }
		catch (ArgumentException)
		{
			// A hand-edited policy that cannot behave as written must not also break the turn it was
			// meant to protect; the default is the safe reading of "retry sensibly".
			return RetryPolicy.Default;
		}
	}

	public async ValueTask<int> GetMaxIterationsAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var selection = await GetSelectionAsync(sessionId, cancellationToken);
		if (selection is null) return ModelProviderProfile.DefaultMaxToolIterationsPerTurn;
		var snapshot = await GetSnapshotAsync(cancellationToken);
		return snapshot.Providers.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile.ToolIterationCap
			?? ModelProviderProfile.DefaultMaxToolIterationsPerTurn;
	}

	public async ValueTask<int> GetMaxSegmentIterationsAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var selection = await GetSelectionAsync(sessionId, cancellationToken);
		if (selection is null) return ModelProviderProfile.DefaultMaxToolIterationsPerSegment;
		var snapshot = await GetSnapshotAsync(cancellationToken);
		return snapshot.Providers.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile.ToolSegmentCap
			?? ModelProviderProfile.DefaultMaxToolIterationsPerSegment;
	}

	public async ValueTask<bool> AutoContinueUnfinishedTurnsAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var selection = await GetSelectionAsync(sessionId, cancellationToken);
		if (selection is null) return false;
		var snapshot = await GetSnapshotAsync(cancellationToken);
		return snapshot.Providers.SingleOrDefault(item => item.Profile.Id == selection.Provider)?.Profile.AutoContinueUnfinishedTurns
			?? false;
	}

	/// <summary>Drop a session's cached toolset, so an archived session releases its observations.</summary>
	public void ForgetSession(Guid sessionId)
	{
		_toolsets.TryRemove(sessionId, out _);
		repeatReminder?.Forget(sessionId);
	}

	private static bool PathsEqual(string left, string right) =>
		string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)
			? string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)
			: string.Equals(
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private async Task<string?> CredentialAsync(string providerId, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken);
		try { return (await ReadCredentialsAsync(cancellationToken)).GetValueOrDefault(providerId); }
		finally { _gate.Release(); }
	}

	private async Task<ModelState> ReadStateAsync(CancellationToken cancellationToken)
	{
		var bytes = await ReadDocumentAsync(_settingsPath, cancellationToken);
		return bytes is null ? new([], null, []) : JsonSerializer.Deserialize<ModelState>(bytes, JsonOptions) ?? new([], null, []);
	}

	private Task WriteStateAsync(ModelState state, CancellationToken cancellationToken) =>
		WriteDocumentAsync(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), cancellationToken);

	private async Task<Dictionary<string, string>> ReadCredentialsAsync(CancellationToken cancellationToken)
	{
		var protectedBytes = await ReadDocumentAsync(_credentialsPath, cancellationToken);
		if (protectedBytes is null) return [];
		byte[] json;
		try
		{
			json = _protector.Unprotect(protectedBytes);
		}
		catch (System.Security.Cryptography.CryptographicException)
		{
			// The Data Protection key ring cannot read this payload — it was written by a different
			// application discriminator, or the keys were replaced. Report no stored credentials so
			// the UI still loads and the key can be re-entered, instead of failing every request.
			return [];
		}
		return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];
	}

	private async Task WriteCredentialsAsync(Dictionary<string, string> credentials, CancellationToken cancellationToken)
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials, JsonOptions);
		await WriteDocumentAsync(_credentialsPath, _protector.Protect(bytes), cancellationToken);
	}

	/// <summary>Read one of the two documents whole, or null when it has not been written yet.</summary>
	/// <remarks>
	/// <c>_gate</c> serializes these documents within one process, but nothing serializes two SuHarness
	/// processes pointed at the same <c>SUHARNESS_HOME</c> — a CLI run beside the Web UI, or a second
	/// window. Sharing delete is what lets one of them replace a document while the other is mid-read,
	/// rather than failing one side with a sharing violation.
	/// </remarks>
	private static async Task<byte[]?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			await using var stream = new FileStream(
				path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
			using var buffer = new MemoryStream();
			await stream.CopyToAsync(buffer, cancellationToken);
			return buffer.ToArray();
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
	}

	/// <summary>Replace one of the two documents in a single step.</summary>
	/// <remarks>
	/// Written beside the target and renamed over it, so a reader sees either the whole previous
	/// document or the whole new one and never a half-written file — which for the credential store
	/// would mean one interrupted write costing every saved key. The temporary name carries a random
	/// suffix because a fixed one is itself a collision between two writers.
	/// </remarks>
	private static async Task WriteDocumentAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporary = $"{path}.{Path.GetRandomFileName()}.tmp";
		try
		{
			await using (var stream = File.Create(temporary)) await stream.WriteAsync(bytes, cancellationToken);
			await ReplaceAsync(temporary, path, cancellationToken);
		}
		catch
		{
			try { File.Delete(temporary); } catch (IOException) { }
			throw;
		}
	}

	/// <summary>Rename the temporary file over the target, waiting out a contended moment.</summary>
	/// <remarks>
	/// Windows refuses a replacing rename while another handle on the destination is still closing,
	/// and a second SuHarness process against the same home is exactly that. The contended window is
	/// measured in milliseconds, so a short bounded retry clears it; failing the first attempt instead
	/// would surface to the operator as an API key that did not save.
	/// </remarks>
	private static async Task ReplaceAsync(string temporary, string path, CancellationToken cancellationToken)
	{
		const int Attempts = 10;
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				File.Move(temporary, path, true);
				return;
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < Attempts)
			{
				await Task.Delay(10 * attempt, cancellationToken);
			}
		}
	}

	private static void Validate(ModelProviderProfile profile)
	{
		if (!ProviderId().IsMatch(profile.Id)) throw new InvalidOperationException("Provider ID must start with a lowercase letter and contain lowercase letters, digits, or dashes.");
		if (string.IsNullOrWhiteSpace(profile.DisplayName)) throw new InvalidOperationException("Display name is required.");
		ValidateEndpoint(profile.BaseUrl);
		if (profile.Models.Count == 0) throw new InvalidOperationException("At least one model is required.");
		if (profile.Models.Any(model => string.IsNullOrWhiteSpace(model.Id)) || profile.Models.Select(model => model.Id).Distinct().Count() != profile.Models.Count)
			throw new InvalidOperationException("Model IDs must be non-empty and unique.");
		if (profile.Temperature is < 0 or > 2) throw new InvalidOperationException("Temperature must be between 0 and 2.");
		if (profile.TopP is <= 0 or > 1) throw new InvalidOperationException("Top P must be greater than 0 and at most 1.");
		if (profile.TopK is <= 0) throw new InvalidOperationException("Top K must be greater than 0.");
		if (profile.MaxToolIterationsPerSegment is <= 0) throw new InvalidOperationException("Model round-trips per segment must be greater than 0.");
	}

	private static void ValidateEndpoint(string baseUrl)
	{
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
			throw new InvalidOperationException("Base URL must be an absolute HTTP or HTTPS URL.");
	}

	private static void ValidateCredential(ModelProviderProfile profile, string apiKey)
	{
		if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var endpoint)) return;
		var isOpenRouter = profile.Id.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
			|| endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);
		if (!isOpenRouter) return;
		if (apiKey.StartsWith("sk-or-", StringComparison.Ordinal)) return;
		throw new ModelConfigurationException(
			"The configured OpenRouter API key is invalid. OpenRouter keys start with 'sk-or-'. "
			+ "Open model settings and replace the stored credential with a valid OpenRouter key.");
	}

	[GeneratedRegex("^[a-z][a-z0-9-]*$")]
	private static partial Regex ProviderId();

	private sealed record ModelState(
		IReadOnlyList<ModelProviderProfile> Providers,
		ModelSelection? DefaultSelection,
		Dictionary<Guid, ModelSelection> SessionSelections,
		Dictionary<Guid, TeamConfiguration>? TeamConfigurations = null);

	private static Dictionary<Guid, TeamConfiguration> FilterTeamConfigurations(
		Dictionary<Guid, TeamConfiguration>? configurations,
		IReadOnlyList<ModelProviderProfile> providers)
	{
		var result = new Dictionary<Guid, TeamConfiguration>();
		foreach (var pair in configurations ?? [])
		{
			var assignments = pair.Value.Assignments
				.Where(assignment => providers.Any(provider =>
					provider.Id == assignment.Value.Provider
					&& provider.Models.Any(model => model.Id == assignment.Value.Model)))
				.ToDictionary();
			var complete = Enum.GetValues<CodingPersona>().All(assignments.ContainsKey);
			result[pair.Key] = new TeamConfiguration(pair.Value.Enabled && complete, assignments);
		}
		return result;
	}
}

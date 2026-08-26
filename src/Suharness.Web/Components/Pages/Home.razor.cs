using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// The chat page. Markup lives in Home.razor; behaviour is split across this file and its
/// Home.*.cs siblings by concern, so that changing the composer does not mean scrolling past
/// the diff viewer to reach it.
///
/// This part holds the page's state, its lifecycle, and the session it is looking at.
/// </summary>
public partial class Home
{
	/// <summary>
	/// One-line label for a tool card. A call shows its name and arguments; a result shows its first
	/// line, so a large file dump stays collapsed instead of flooding the transcript.
	/// </summary>
	private string ToolSummary(SessionEvent item)
	{
		if (item.Type == "tool_call") return ToolCallSummary(item.ToolName, item.ToolArguments, item.Content);
		var text = item.Content.ReplaceLineEndings("\n");
		var firstLine = text.Split('\n')[0];
		if (firstLine.Length > 120) firstLine = firstLine[..120] + "...";
		if (item.Type == "tool_call") return firstLine;
		var lines = text.Split('\n').Length;
		return lines > 1 ? $"{firstLine}   ({lines} lines)" : firstLine;
	}

	/// <summary>
	/// The events a reader should see. Goal and snapshot bookkeeping is persisted so it survives a
	/// restart, but its JSON payload is machine state, not conversation, so it stays out of the
	/// transcript — and an undone span is hidden here for the same reason replay hides it.
	/// </summary>
	private static IReadOnlyList<SessionEvent> VisibleEvents(IReadOnlyList<SessionEvent> events)
	{
		if (events.Count == 0) return events;
		var hidden = SessionRevert.Hidden(events);
		return [.. events.Where(item =>
			item.Type != "goal/change"
			&& item.Type != "continuation"
			&& item.Type != "turn_summary"
			&& item.Type != SessionUsageEvent.Type
			&& item.Type != SessionRevert.SnapshotType
			&& item.Type != SessionRevert.RevertType
			&& item.Type != SessionRevert.RedoType
			&& !hidden.Contains(item.Id))];
	}

	/// <summary>The messages this session can be rolled back to, mapped to the snapshot behind each.</summary>
	private IReadOnlyDictionary<Guid, string> RevertPoints =>
		ActiveSession is null || !Runtime.SnapshotsEnabled
			? System.Collections.Immutable.ImmutableDictionary<Guid, string>.Empty
			: SessionRevert.RevertPoints(ActiveSession.Events);

	/// <summary>Whether an undo is standing that a redo could reinstate.</summary>
	private bool CanRedo =>
		ActiveSession is not null && Runtime.SnapshotsEnabled && SessionRevert.Active(ActiveSession.Events).Count > 0;

	/// <summary>What a restore does to a path, phrased as the action rather than the git status letter.</summary>
	private static string RevertVerb(SnapshotChangeKind kind) => kind switch
	{
		SnapshotChangeKind.Added => "delete",
		SnapshotChangeKind.Deleted => "restore",
		_ => "revert",
	};

	private Guid? RevertTarget { get; set; }
	private IReadOnlyList<SnapshotChange> RevertPreview { get; set; } = [];
	private bool RevertBusy { get; set; }
	private string? RevertError { get; set; }

	/// <summary>
	/// Open the confirmation, having first worked out what the undo would actually do. A file count
	/// is the difference between an informed click and a hopeful one.
	/// </summary>
	private async Task AskRevertAsync(Guid boundaryEventId)
	{
		if (ActiveSession is null || IsSending) return;
		RevertTarget = boundaryEventId;
		RevertError = null;
		RevertPreview = [];
		RevertBusy = true;
		StateHasChanged();
		try
		{
			RevertPreview = await Runtime.PreviewRevertAsync(ActiveSession.Session.Id, boundaryEventId);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException)
		{
			RevertError = exception.Message;
		}
		finally
		{
			RevertBusy = false;
		}
	}

	private void CancelRevert()
	{
		RevertTarget = null;
		RevertPreview = [];
		RevertError = null;
	}

	private async Task ConfirmRevertAsync()
	{
		if (ActiveSession is null || RevertTarget is not { } boundary) return;
		RevertBusy = true;
		RevertError = null;
		try
		{
			await Runtime.RevertAsync(ActiveSession.Session.Id, boundary);
			await RefreshAsync(ActiveSession.Session.Id);
			CancelRevert();
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
		{
			// A failed rollback leaves the workspace as it was, so the dialog stays open with the reason.
			RevertError = exception.Message;
		}
		finally
		{
			RevertBusy = false;
		}
	}

	private async Task RedoAsync()
	{
		if (ActiveSession is null || IsSending) return;
		RevertBusy = true;
		RevertError = null;
		try
		{
			await Runtime.RedoAsync(ActiveSession.Session.Id);
			await RefreshAsync(ActiveSession.Session.Id);
		}
		catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
		{
			RevertError = exception.Message;
		}
		finally
		{
			RevertBusy = false;
		}
	}

	private SessionEvent? ToolResultFor(SessionEvent call) =>
		call.ToolCallId is null ? null : ActiveSession?.Events.FirstOrDefault(item => item.Type == "tool_result" && item.ToolCallId == call.ToolCallId);

	private bool HasToolCall(SessionEvent result) =>
		result.ToolCallId is not null && ActiveSession?.Events.Any(item => item.Type == "tool_call" && item.ToolCallId == result.ToolCallId) == true;

	private static readonly string[] Suggestions = [
		"Summarize this repository",
		"Find and explain the main entry points",
		"Review recent changes",
		"Create a plan for a new feature"
	];
	private static readonly string[] ChatSuggestions = [
		"Explain a complex topic",
		"Research the latest information",
		"Compare two approaches",
		"Help me think through a decision"
	];
	private static readonly HarnessAccessMode[] AccessModes =
	[
		HarnessAccessMode.ReadOnly,
		HarnessAccessMode.WorkspaceWrite,
		HarnessAccessMode.FullAccess,
	];
	private IReadOnlyList<HarnessSession> Sessions { get; set; } = [];
	private IReadOnlyList<HarnessWorkspace> Workspaces { get; set; } = [];
	private HarnessWorkspace? CurrentWorkspace { get; set; }
	private SessionTranscript? ActiveSession { get; set; }
	private string Prompt { get; set; } = string.Empty;
	private string PromptHistoryDraft { get; set; } = string.Empty;
	private int PromptHistoryIndex { get; set; } = -1;
	private string PendingPrompt { get; set; } = string.Empty;
	private List<ImageDraft> AttachmentDrafts { get; } = [];
	private IReadOnlyList<ImageAttachmentRef> PendingAttachmentRefs { get; set; } = [];
	private string? AttachmentError { get; set; }
	private const int MaxAttachmentBytes = 10 * 1024 * 1024;
	private List<LiveRunItem> LiveItems { get; } = [];
	private int LiveSequence { get; set; }
	private string? LiveActivity { get; set; }
	private bool IsSending { get; set; }
	private CancellationTokenSource? SendCancellation { get; set; }
	private bool ShowModels { get; set; }
	private bool ShowPromptQueue { get; set; }
	private string SettingsSection { get; set; } = "models";
	private bool ShowAccessPicker { get; set; }
	private bool ShowModelPicker { get; set; }
	private bool ShowTeamPicker { get; set; }
	private bool ShowWorkspacePicker { get; set; }
	private bool SidebarExpanded { get; set; } = true;
	private bool ModifiedFilesExpanded { get; set; } = true;
	private IReadOnlyList<SnapshotChange> ModifiedFiles { get; set; } = [];
	private string? ChangesError { get; set; }
	private CancellationTokenSource? ChangesRefreshCancellation { get; set; }
	private WorkspaceFileDiff? DiffFile { get; set; }
	private bool DiffBusy { get; set; }
	private string? DiffError { get; set; }
	private SnapshotChange? FileRevertTarget { get; set; }
	private bool FileActionBusy { get; set; }
	private string? FileActionError { get; set; }
	private bool ShowSessionActions { get; set; }
	private HarnessSession? SessionActionsTarget { get; set; }
	private HarnessSession? SessionOperationTarget { get; set; }
	private bool RenamingSession { get; set; }
	private bool ArchivingSession { get; set; }
	private HarnessSession? DeletingSession { get; set; }
	private string SessionTitleDraft { get; set; } = string.Empty;
	private ModelConfigurationSnapshot ModelSnapshot { get; set; } = new([], null);
	private ModelSelection? CurrentSelection { get; set; }
	private TeamConfiguration TeamConfiguration { get; set; } = TeamConfiguration.Disabled;
	private static readonly CodingPersona[] CodingPersonas = Enum.GetValues<CodingPersona>();
	private ElementReference ConversationElement { get; set; }
	private IJSObjectReference? UiModule { get; set; }
	private bool ScrollPending { get; set; }
	private string WorkspaceKey => CurrentWorkspace?.Path ?? string.Empty;
	private string WorkspacePath => CurrentWorkspace?.Path ?? "Chat";
	private string WorkspaceName => CurrentWorkspace?.Title ?? "No workspace";
	private IReadOnlyList<string> CurrentSuggestions => CurrentWorkspace is null ? ChatSuggestions : Suggestions;
	private string ModelLabel => CurrentSelection is null ? "No model configured" : $"{ProviderName(CurrentSelection.Provider)} / {ModelName(CurrentSelection)}";
	// The effective effort is appended only when one is actually in force, so a setup that never
	// configures reasoning sees the plain model name it always saw.
	private string ModelButtonLabel => CurrentSelection is null
		? "Select model"
		: (CurrentSelection.ReasoningEffort ?? ModelDefaultEffort) is { } effort
			? $"{ModelName(CurrentSelection)} · {effort.ToString().ToLowerInvariant()}"
			: ModelName(CurrentSelection);
	private ConfiguredModelProvider? CurrentProvider =>
		CurrentSelection is null ? null : ModelSnapshot.Providers.FirstOrDefault(item => item.Profile.Id == CurrentSelection.Provider);
	private bool ModelReady => CurrentProvider is { } provider && (!provider.Profile.RequiresApiKey || provider.CredentialConfigured);
	private string ModelStatusTitle => CurrentSelection switch
	{
		null => "Configure a model",
		_ when !ModelReady => $"API key missing for {ProviderName(CurrentSelection.Provider)}. Open model settings to configure it.",
		_ => ModelLabel,
	};
	private string ComposerNote => CurrentSelection switch
	{
		null => "Select a provider and model in Settings to enable the agent.",
		_ when !ModelReady => $"API key missing for {ProviderName(CurrentSelection.Provider)}. Open model settings to configure it; attached images will remain here.",
		_ => $"Using {ModelLabel}. Model configuration is applied on the next request.",
	};
	private HarnessAccessMode CurrentAccessMode => ActiveSession?.Session.AccessMode ?? HarnessAccessMode.WorkspaceWrite;
	private bool ChangesAvailable => CurrentWorkspace is not null && ActiveSession is not null && CurrentAccessMode != HarnessAccessMode.ReadOnly;
	private bool CanSend => !string.IsNullOrWhiteSpace(Prompt) || AttachmentDrafts.Count > 0;

	private PendingUserQuestions? PendingQuestions { get; set; }
	private Dictionary<string, HashSet<string>> QuestionChoices { get; } = [];
	private Dictionary<string, string> QuestionCustom { get; } = [];

	private bool IsPlanMode { get; set; }

	private async Task TogglePlanMode()
	{
		if (ActiveSession is null) return;
		var enable = !IsPlanMode;
		if (enable && TeamConfiguration.Enabled)
		{
			TeamConfiguration = TeamConfiguration with { Enabled = false };
			await Models.SetTeamConfigurationAsync(ActiveSession.Session.Id, TeamConfiguration);
			ShowTeamPicker = false;
		}
		await PlanMode.SetAsync(ActiveSession.Session.Id, enable);
		IsPlanMode = enable;
	}

	private async Task RefreshPlanModeAsync()
	{
		IsPlanMode = ActiveSession is not null && await PlanMode.IsActiveAsync(ActiveSession.Session.Id);
		if (IsPlanMode && TeamConfiguration.Enabled && ActiveSession is not null)
		{
			TeamConfiguration = TeamConfiguration with { Enabled = false };
			await Models.SetTeamConfigurationAsync(ActiveSession.Session.Id, TeamConfiguration);
			ShowTeamPicker = false;
		}
	}

	/// <summary>Every question needs a choice or typed text before the agent can be resumed.</summary>
	private bool CanSubmitAnswers => PendingQuestions is not null && PendingQuestions.Questions.All(question =>
		QuestionChoices.TryGetValue(question.Id, out var chosen) && chosen.Count > 0
		|| !string.IsNullOrWhiteSpace(CustomAnswer(question.Id)));
	private bool CanApproveAll => PendingQuestions is { Questions.Count: > 0 } && PendingQuestions.Questions.All(question =>
		question.ApproveAllOption is { Length: > 0 } option
		&& question.Options?.Any(item => item.Label == option) == true);

	private bool IsChosen(UserQuestionItem question, string label) =>
		QuestionChoices.TryGetValue(question.Id, out var chosen) && chosen.Contains(label);

	private string CustomAnswer(string id) => QuestionCustom.GetValueOrDefault(id, string.Empty);

	private void SetCustomAnswer(string id, string? value) => QuestionCustom[id] = value ?? string.Empty;

	private void ToggleChoice(UserQuestionItem question, string label)
	{
		if (!QuestionChoices.TryGetValue(question.Id, out var chosen))
			QuestionChoices[question.Id] = chosen = [];
		if (!chosen.Remove(label))
		{
			if (!question.MultiSelect) chosen.Clear();
			chosen.Add(label);
		}
	}

	private void SubmitAnswers()
	{
		if (PendingQuestions is null || !CanSubmitAnswers) return;
		var answers = PendingQuestions.Questions
			.Select(question => new UserQuestionAnswerItem(
				question.Id,
				[.. QuestionChoices.GetValueOrDefault(question.Id) ?? []],
				string.IsNullOrWhiteSpace(CustomAnswer(question.Id)) ? null : CustomAnswer(question.Id).Trim()))
			.ToArray();
		Questions.TryAnswer(PendingQuestions.SessionId, answers);
		ClearQuestionDrafts();
	}

	private void DismissQuestions()
	{
		if (PendingQuestions is null) return;
		Questions.TryCancel(PendingQuestions.SessionId);
		ClearQuestionDrafts();
	}

	private void ApproveAllQuestions()
	{
		if (PendingQuestions is null || !CanApproveAll) return;
		var answers = PendingQuestions.Questions
			.Select(question => new UserQuestionAnswerItem(question.Id, [question.ApproveAllOption!]))
			.ToArray();
		Questions.TryAnswer(PendingQuestions.SessionId, answers);
		ClearQuestionDrafts();
	}

	private void ClearQuestionDrafts()
	{
		PendingQuestions = null;
		QuestionChoices.Clear();
		QuestionCustom.Clear();
	}

	/// <summary>The broker signals from the agent's task, so re-render on the renderer's thread.</summary>
	private void OnQuestionsChanged(Guid sessionId) => _ = InvokeAsync(() =>
	{
		if (ActiveSession?.Session.Id != sessionId) return;
		var pending = Questions.Pending(sessionId);
		if (pending is null) ClearQuestionDrafts();
		else PendingQuestions = pending;
		ScrollPending = true;
		StateHasChanged();
	});

	/// <summary>exit_plan_mode flips this from the agent's task, so the toggle follows it.</summary>
	private void OnPlanModeChanged(Guid sessionId) => _ = InvokeAsync(async () =>
	{
		if (ActiveSession?.Session.Id != sessionId) return;
		await RefreshPlanModeAsync();
		StateHasChanged();
	});

	/// <summary>
	/// An explicit todo_write remains authoritative. A coding workflow owns its own task graph, so
	/// when no ordinary list exists its structured stages become the parent session's visible tasks.
	/// </summary>
	private IReadOnlyList<TodoView> CurrentTodos
	{
		get
		{
			var explicitTodos = ParseTodos(LatestTodoArguments());
			return explicitTodos.Count > 0 ? explicitTodos : WorkflowTodos(CurrentWorkflow());
		}
	}

	private string? LatestTodoArguments()
	{
		for (var index = LiveItems.Count - 1; index >= 0; index--)
			if (LiveItems[index].ToolName == "todo_write") return LiveItems[index].ToolArguments;
		var events = ActiveSession?.Events;
		for (var index = (events?.Count ?? 0) - 1; index >= 0; index--)
			if (events![index].ToolName == "todo_write") return events[index].ToolArguments;
		return null;
	}

	private static IReadOnlyList<TodoView> ParseTodos(string? arguments)
	{
		if (string.IsNullOrWhiteSpace(arguments)) return [];
		try
		{
			using var document = JsonDocument.Parse(arguments);
			if (!document.RootElement.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return [];
			return [.. todos.EnumerateArray()
				.Where(todo => todo.ValueKind == JsonValueKind.Object)
				.Select(todo => new TodoView(
					StringProperty(todo, "content") ?? string.Empty,
					StringProperty(todo, "status") ?? "pending"))
				.Where(todo => todo.Content.Length > 0)];
		}
		catch (JsonException) { return []; }
	}

	private CodingWorkflowView? CurrentWorkflow()
	{
		for (var index = LiveItems.Count - 1; index >= 0; index--)
		{
			var item = LiveItems[index];
			if (item.Type != "tool_call" || item.ToolName != "coding_workflow") continue;
			return item.Result is null ? CodingWorkflowView.Running : ParseWorkflow(item.Result);
		}

		var events = ActiveSession?.Events;
		for (var index = (events?.Count ?? 0) - 1; index >= 0; index--)
		{
			var item = events![index];
			if (item.Type != "tool_call" || item.ToolName != "coding_workflow") continue;
			return ParseWorkflow(ToolResultFor(item)?.Content);
		}
		return null;
	}

	private CodingWorkflowView? CodingWorkflowFor(SessionEvent call) =>
		call.ToolName == "coding_workflow" ? ParseWorkflow(ToolResultFor(call)?.Content) : null;

	private static CodingWorkflowView? ParseWorkflow(string? content)
	{
		if (string.IsNullOrWhiteSpace(content) || content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)) return null;
		try
		{
			using var document = JsonDocument.Parse(content);
			var root = document.RootElement;
			if (!root.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array) return null;
			return new CodingWorkflowView(
				StringProperty(root, "status") ?? "running",
				root.TryGetProperty("repairRounds", out var rounds) && rounds.TryGetInt32(out var count) ? count : 0,
				StringProperty(root, "summary") ?? string.Empty,
				[.. stages.EnumerateArray()
					.Where(stage => stage.ValueKind == JsonValueKind.Object)
					.Select(stage => new CodingWorkflowStageView(
						StringProperty(stage, "role") ?? "stage",
						StringProperty(stage, "status") ?? "pending",
						StringProperty(stage, "summary") ?? string.Empty,
						stage.TryGetProperty("handoff", out var handoff)
							? JsonSerializer.Serialize(handoff, new JsonSerializerOptions { WriteIndented = true })
							: string.Empty))]);
		}
		catch (JsonException) { return null; }
	}

	private static IReadOnlyList<TodoView> WorkflowTodos(CodingWorkflowView? workflow) =>
		workflow is null ? [] : [.. workflow.Stages.Select(stage => new TodoView(
			WorkflowRoleLabel(stage.Role),
			stage.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
				|| stage.Status.Equals("pass", StringComparison.OrdinalIgnoreCase)
					? "completed"
					: stage.Status.Equals("running", StringComparison.OrdinalIgnoreCase) ? "in_progress" : "pending"))];

	public static IReadOnlyList<(string Content, string Status)> WorkflowTodoProjection(string content)
	{
		var workflow = ParseWorkflow(content);
		return [.. WorkflowTodos(workflow).Select(todo => (todo.Content, todo.Status))];
	}

	private static string WorkflowRoleLabel(string role) =>
		string.Join(" · ", role.Split(':').Select(part => string.Join(' ', part.Split('-').Select(word =>
			word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]))));

	private static string TodoMark(string status) => status switch
	{
		"completed" => "✓",
		"in_progress" => "▸",
		_ => "○",
	};

	protected override async Task OnInitializedAsync()
	{
		Questions.Changed += OnQuestionsChanged;
		PlanMode.Changed += OnPlanModeChanged;
		Lifecycle.ToolProgress += OnToolProgress;
		Workspaces = await WorkspaceService.ListAsync();
		if (Workspaces.Count == 0)
		{
			await WorkspaceService.AddAsync(Directory.GetCurrentDirectory());
			Workspaces = await WorkspaceService.ListAsync();
		}
		var selection = await WorkspaceService.GetSelectionAsync();
		CurrentWorkspace = selection switch
		{
			null => Workspaces.FirstOrDefault(),
			{ WorkspaceId: { } workspaceId } => Workspaces.FirstOrDefault(item => item.Id == workspaceId),
			_ => null,
		};
		ModelSnapshot = await Models.GetSnapshotAsync();
		await ConnectWorkspaceAsync();
	}

	private async Task CreateSessionAsync()
	{
		var session = await Runtime.CreateSessionAsync(WorkspaceKey);
		Sessions = await ListVisibleSessionsAsync();
		await OpenSessionAsync(session.Id);
	}

	/// <summary>
	/// The sidebar is a list of human sessions. Fresh subagent sessions remain durable for replay and
	/// audit, but appear inside their parent workflow rather than as peer conversations.
	/// </summary>
	private async Task<IReadOnlyList<HarnessSession>> ListVisibleSessionsAsync() =>
		SidebarSessions(await Runtime.ListSessionsAsync(WorkspaceKey));

	public static IReadOnlyList<HarnessSession> SidebarSessions(IEnumerable<HarnessSession> sessions) =>
		[.. sessions.Where(session => session.ParentSessionId is null)];

	private async Task OpenSessionAsync(Guid id)
	{
		ClearComposerDraft();
		ActiveSession = await LoadStandardSessionAsync(id);
		CurrentSelection = await Models.GetSelectionAsync(id);
		TeamConfiguration = await Models.GetTeamConfigurationAsync(id);
		ClearQuestionDrafts();
		PendingQuestions = Questions.Pending(id);
		await RefreshPlanModeAsync();
		await RefreshModifiedFilesAsync();
		ResetPromptHistory();
		ScrollPending = true;
	}

	private void ToggleModifiedFiles() => ModifiedFilesExpanded = !ModifiedFilesExpanded;
	private void ToggleSidebar() => SidebarExpanded = !SidebarExpanded;

	private async Task<SessionTranscript?> LoadStandardSessionAsync(Guid id)
	{
		var transcript = await Runtime.GetSessionAsync(id);
		if (transcript is null || transcript.Session.Preset == HarnessAgentPreset.Standard) return transcript;
		await Runtime.SetPresetAsync(id, HarnessAgentPreset.Standard);
		transcript = await Runtime.GetSessionAsync(id);
		if (transcript is not null)
			Sessions = Sessions.Select(item => item.Id == id ? transcript.Session : item).ToArray();
		return transcript;
	}

	protected override async Task OnAfterRenderAsync(bool firstRender)
	{
		if (firstRender)
		{
			UiModule = await JS.InvokeAsync<IJSObjectReference>("import", "./ui.js");
		}
		if (ScrollPending && UiModule is not null)
		{
			ScrollPending = false;
			await UiModule.InvokeVoidAsync("scrollToEnd", ConversationElement);
		}
	}

	public async ValueTask DisposeAsync()
	{
		Questions.Changed -= OnQuestionsChanged;
		PlanMode.Changed -= OnPlanModeChanged;
		Lifecycle.ToolProgress -= OnToolProgress;
		SendCancellation?.Cancel();
		SendCancellation?.Dispose();
		ChangesRefreshCancellation?.Cancel();
		ChangesRefreshCancellation?.Dispose();
		if (UiModule is not null)
		{
			try
			{
				await UiModule.DisposeAsync();
			}
			catch (JSDisconnectedException) { }
		}
	}

	/// <summary>Render key for live items: a model may reuse one message id across a run.</summary>
	private sealed record LiveRunItem(
		int Seq,
		string Key,
		string Type,
		string Content,
		string? ToolName = null,
		string? ToolArguments = null,
		string? Result = null);

	private sealed record TodoView(string Content, string Status);
	private sealed record CodingWorkflowStageView(string Role, string Status, string Summary, string Handoff);
	private sealed record CodingWorkflowView(string Status, int RepairRounds, string Summary, IReadOnlyList<CodingWorkflowStageView> Stages)
	{
		public static CodingWorkflowView Running { get; } = new(
			"running",
			0,
			string.Empty,
			[
				new("explorer", "running", string.Empty, string.Empty),
				new("planner", "pending", string.Empty, string.Empty),
				new("senior-developer", "pending", string.Empty, string.Empty),
				new("junior-developer", "pending", string.Empty, string.Empty),
				new("reviewer", "pending", string.Empty, string.Empty),
				new("verifier", "pending", string.Empty, string.Empty),
			]);
	}
	private sealed record ImageDraft(string Name, string MediaType, byte[] Bytes, string Preview);
}

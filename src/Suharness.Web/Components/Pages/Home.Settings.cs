using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// The pickers and dialogs around a session rather than inside it: model and access-mode
/// selection, workspace selection, and renaming, archiving, or deleting a session.
/// </summary>
public partial class Home
{
	private void ToggleAccessPicker() { ShowModelPicker = false; ShowTeamPicker = false; ShowAccessPicker = !ShowAccessPicker; }
	private void ToggleModelPicker() { ShowAccessPicker = false; ShowTeamPicker = false; ShowModelPicker = !ShowModelPicker; }
	private async Task ToggleTeamPicker()
	{
		ShowAccessPicker = false;
		ShowModelPicker = false;
		if (ActiveSession is null || CurrentSelection is null) return;
		if (!TeamConfiguration.Enabled)
		{
			if (IsPlanMode)
			{
				await PlanMode.SetAsync(ActiveSession.Session.Id, false);
				IsPlanMode = false;
			}
			var assignments = CodingPersonas.ToDictionary(persona => persona, _ => CurrentSelection);
			TeamConfiguration = new TeamConfiguration(true, assignments);
			await Models.SetTeamConfigurationAsync(ActiveSession.Session.Id, TeamConfiguration);
		}
		ShowTeamPicker = !ShowTeamPicker;
	}
	private void ClosePickerMenus() { ShowAccessPicker = false; ShowModelPicker = false; ShowTeamPicker = false; ShowSessionActions = false; CloseCost(); }
	private void OpenModels() { ShowAccessPicker = false; ShowModelPicker = false; ShowTeamPicker = false; SettingsSection = "models"; ShowModels = true; }
	private void OpenMcpSettings() { ShowAccessPicker = false; ShowModelPicker = false; SettingsSection = "mcp"; ShowModels = true; }
	private void OpenWorkspaceAgentSettings() { ShowAccessPicker = false; ShowModelPicker = false; ShowTeamPicker = false; SettingsSection = "workspace"; ShowModels = true; }
	private void OpenPromptQueue() { ShowAccessPicker = false; ShowModelPicker = false; ShowPromptQueue = true; }
	private Task ClosePromptQueue() { ShowPromptQueue = false; return Task.CompletedTask; }
	private async Task OpenQueuedSessionAsync(Guid sessionId)
	{
		ShowPromptQueue = false;
		Sessions = await ListVisibleSessionsAsync();
		await OpenSessionAsync(sessionId);
	}
	private Task CloseModels() { ShowModels = false; return Task.CompletedTask; }
	private async Task RefreshModelsAsync()
	{
		ModelSnapshot = await Models.GetSnapshotAsync();
		CurrentSelection = ActiveSession is null ? ModelSnapshot.DefaultSelection : await Models.GetSelectionAsync(ActiveSession.Session.Id);
		TeamConfiguration = ActiveSession is null ? TeamConfiguration.Disabled : await Models.GetTeamConfigurationAsync(ActiveSession.Session.Id);
	}
	private async Task DisableTeamAsync()
	{
		if (ActiveSession is null) return;
		TeamConfiguration = TeamConfiguration with { Enabled = false };
		await Models.SetTeamConfigurationAsync(ActiveSession.Session.Id, TeamConfiguration);
		ShowTeamPicker = false;
	}
	private async Task SelectTeamModelAsync(CodingPersona persona, ChangeEventArgs eventArgs)
	{
		if (ActiveSession is null || eventArgs.Value?.ToString() is not { } key) return;
		var separator = key.IndexOf('\u001f');
		if (separator <= 0 || separator == key.Length - 1) return;
		var selection = new ModelSelection(key[..separator], key[(separator + 1)..]);
		var assignments = new Dictionary<CodingPersona, ModelSelection>(TeamConfiguration.Assignments)
		{
			[persona] = selection,
		};
		TeamConfiguration = new TeamConfiguration(true, assignments);
		await Models.SetTeamConfigurationAsync(ActiveSession.Session.Id, TeamConfiguration);
	}
	private static string TeamModelKey(ModelSelection? selection) =>
		selection is null ? string.Empty : $"{selection.Provider}\u001f{selection.Model}";
	private string TeamModelLabel(ModelSelection? selection)
	{
		if (selection is null) return "No model assigned";
		var provider = ModelSnapshot.Providers.FirstOrDefault(item => item.Profile.Id == selection.Provider)?.Profile;
		var model = provider?.Models.FirstOrDefault(item => item.Id == selection.Model);
		return provider is null
			? $"{selection.Provider} / {selection.Model}"
			: TeamModelOptionLabel(provider.DisplayName, model?.Name ?? selection.Model, selection.Model);
	}
	private static string TeamModelOptionLabel(string provider, string name, string id) =>
		name.Equals(id, StringComparison.OrdinalIgnoreCase)
			? $"{provider} / {name}"
			: $"{provider} / {name} [{id}]";
	private static string PersonaLabel(CodingPersona persona) => persona switch
	{
		CodingPersona.SeniorDeveloper => "Senior developer",
		CodingPersona.JuniorDeveloper => "Junior developer",
		_ => string.Concat(persona.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? $" {char.ToLowerInvariant(character)}" : character.ToString())),
	};
	private async Task SelectModelAsync(ModelSelection selection)
	{
		if (ActiveSession is null)
		{
			var session = await Runtime.CreateSessionAsync(WorkspaceKey);
			await RefreshAsync(session.Id);
		}
		await Models.SelectAsync(ActiveSession!.Session.Id, selection);
		CurrentSelection = selection; ModelSnapshot = await Models.GetSnapshotAsync(); ShowModelPicker = false;
	}
	private async Task SelectAccessModeAsync(HarnessAccessMode accessMode)
	{
		if (ActiveSession is null) return;
		await Runtime.SetAccessModeAsync(ActiveSession.Session.Id, accessMode);
		var session = ActiveSession.Session with { AccessMode = accessMode, UpdatedAt = DateTimeOffset.UtcNow };
		ActiveSession = ActiveSession with { Session = session };
		Sessions = Sessions.Select(item => item.Id == session.Id ? session : item).ToArray();
		ShowAccessPicker = false;
		await RefreshModifiedFilesAsync();
	}
	/// <summary>
	/// Whether the picker entry for this route and model is the one the session is running. The
	/// selection record also carries a reasoning-effort override, which must not affect the tick.
	/// </summary>
	private bool IsCurrentModel(string providerId, string modelId) =>
		CurrentSelection is { } current && current.Provider == providerId && current.Model == modelId;

	/// <summary>
	/// Re-select the running model with a different reasoning budget. Effort lives on the selection,
	/// so changing it is the same operation as changing model — it is persisted for this session and
	/// becomes the default for new ones, exactly as a model choice does.
	/// </summary>
	private async Task SelectReasoningEffortAsync(ReasoningEffortLevel? effort)
	{
		if (CurrentSelection is not { } current) return;
		await SelectModelAsync(current with { ReasoningEffort = effort });
	}

	/// <summary>The catalog default the "Default" entry defers to, named so the choice is not blind.</summary>
	private string ModelDefaultEffortLabel =>
		ModelDefaultEffort is { } level ? $"Default ({level})" : "Default (none sent)";

	/// <summary>The reasoning effort configured on the running model itself, before any override.</summary>
	private ReasoningEffortLevel? ModelDefaultEffort =>
		CurrentSelection is not { } current
			? null
			: ModelSnapshot.Providers
				.FirstOrDefault(item => item.Profile.Id == current.Provider)?.Profile.Models
				.FirstOrDefault(model => model.Id == current.Model)?.ReasoningEffort;

	private static string AccessModeLabel(HarnessAccessMode accessMode) => accessMode switch
	{
		HarnessAccessMode.ReadOnly => "Read",
		HarnessAccessMode.FullAccess => "Execute",
		_ => "Write",
	};
	private static string AccessModeIcon(HarnessAccessMode accessMode) => accessMode switch
	{
		HarnessAccessMode.ReadOnly => "◇",
		HarnessAccessMode.FullAccess => "!",
		_ => "✎",
	};
	private string ProviderName(string id) => ModelSnapshot.Providers.FirstOrDefault(item => item.Profile.Id == id)?.Profile.DisplayName ?? id;
	private string ModelName(ModelSelection selection) => ModelSnapshot.Providers.FirstOrDefault(item => item.Profile.Id == selection.Provider)?.Profile.Models.FirstOrDefault(model => model.Id == selection.Model)?.Name ?? selection.Model;
	private void OpenWorkspacePicker() => ShowWorkspacePicker = true;
	private Task CloseWorkspacePicker() { ShowWorkspacePicker = false; return Task.CompletedTask; }
	private async Task SelectWorkspaceAsync(HarnessWorkspace workspace)
	{
		await WorkspaceService.SelectAsync(workspace.Id);
		CurrentWorkspace = workspace; ShowWorkspacePicker = false; ShowSessionActions = false;
		Workspaces = await WorkspaceService.ListAsync();
		await ConnectWorkspaceAsync();
	}
	private async Task SelectNoWorkspaceAsync()
	{
		await WorkspaceService.SelectAsync(null);
		CurrentWorkspace = null; ShowWorkspacePicker = false; ShowSessionActions = false;
		await ConnectWorkspaceAsync();
	}
	private async Task WorkspaceRemovedAsync(Guid workspaceId)
	{
		Workspaces = await WorkspaceService.ListAsync();
		if (CurrentWorkspace?.Id != workspaceId) return;
		CurrentWorkspace = null;
		await ConnectWorkspaceAsync();
	}
	private async Task ConnectWorkspaceAsync()
	{
		Sessions = await ListVisibleSessionsAsync();
		var session = Sessions.FirstOrDefault() ?? await Runtime.CreateSessionAsync(WorkspaceKey);
		Sessions = await ListVisibleSessionsAsync();
		await OpenSessionAsync(session.Id);
	}
	private void ToggleSessionActions(HarnessSession session)
	{
		ShowSessionActions = SessionActionsTarget?.Id != session.Id || !ShowSessionActions;
		SessionActionsTarget = session;
	}
	private async Task ExportSessionAsync(HarnessSession session)
	{
		ShowSessionActions = false;
		var transcript = await Runtime.GetSessionAsync(session.Id);
		if (transcript is null) return;
		var content = JsonSerializer.Serialize(new
		{
			exportedAt = DateTimeOffset.UtcNow,
			transcript.Session,
			transcript.Events,
		}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
		UiModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./ui.js");
		await UiModule.InvokeVoidAsync("downloadText", $"suharness-session-{session.Id:N}.json", content, "application/json");
	}
	private void BeginRename(HarnessSession session) { ShowSessionActions = false; SessionOperationTarget = session; SessionTitleDraft = session.Title; RenamingSession = true; }
	private void CancelRename() { RenamingSession = false; SessionOperationTarget = null; }
	private async Task RenameAsync()
	{
		if (SessionOperationTarget is null) return;
		await Runtime.RenameSessionAsync(SessionOperationTarget.Id, SessionTitleDraft);
		RenamingSession = false;
		SessionOperationTarget = null;
		if (ActiveSession is not null) await RefreshAsync(ActiveSession.Session.Id);
		else Sessions = await ListVisibleSessionsAsync();
	}
	private void ConfirmArchive(HarnessSession session) { ShowSessionActions = false; SessionOperationTarget = session; ArchivingSession = true; }
	private void CancelArchive() { ArchivingSession = false; SessionOperationTarget = null; }
	private async Task ArchiveAsync()
	{
		if (SessionOperationTarget is null) return;
		var sessionId = SessionOperationTarget.Id;
		var archivingActiveSession = ActiveSession?.Session.Id == sessionId;
		await Runtime.ArchiveSessionAsync(sessionId);
		ArchivingSession = false;
		SessionOperationTarget = null;
		if (archivingActiveSession) { ActiveSession = null; await ConnectWorkspaceAsync(); }
		else Sessions = await ListVisibleSessionsAsync();
	}
	private void ConfirmDelete(HarnessSession session) => DeletingSession = session;
	private void CancelDelete() => DeletingSession = null;
	private async Task DeleteAsync()
	{
		if (DeletingSession is null) return;
		var sessionId = DeletingSession.Id;
		var deletingActiveSession = ActiveSession?.Session.Id == sessionId;
		await Runtime.DeleteSessionAsync(sessionId);
		DeletingSession = null;
		if (deletingActiveSession)
		{
			ActiveSession = null;
			await ConnectWorkspaceAsync();
		}
		else Sessions = await ListVisibleSessionsAsync();
	}
	private static string SuggestionIcon(string suggestion) => suggestion.StartsWith("Summarize") ? "#" : suggestion.StartsWith("Find") ? ">" : suggestion.StartsWith("Review") ? "~" : "+";
}

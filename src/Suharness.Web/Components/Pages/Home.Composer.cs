using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// The composer: the prompt draft, recall through this session's earlier prompts, image
/// attachments, and sending a turn.
/// </summary>
public partial class Home
{
	private void ClearComposerDraft()
	{
		Prompt = string.Empty;
		AttachmentDrafts.Clear();
		AttachmentError = null;
	}

	private void UpdatePrompt(ChangeEventArgs eventArgs)
	{
		Prompt = eventArgs.Value?.ToString() ?? string.Empty;
		PromptHistoryIndex = -1;
		PromptHistoryDraft = string.Empty;
	}

	private async Task HandleComposerKeyUp(KeyboardEventArgs eventArgs)
	{
		if (eventArgs.Key == "Enter" && !eventArgs.ShiftKey)
		{
			Prompt = Prompt.Trim();
			await SendAsync();
			return;
		}
		if (eventArgs.Key == "ArrowUp") RecallPreviousPrompt();
		else if (eventArgs.Key == "ArrowDown") RecallNextPrompt();
	}

	private void RecallPreviousPrompt()
	{
		var history = UserPromptHistory();
		if (history.Count == 0 || PromptHistoryIndex + 1 >= history.Count) return;
		if (PromptHistoryIndex == -1) PromptHistoryDraft = Prompt;
		PromptHistoryIndex++;
		Prompt = history[^(PromptHistoryIndex + 1)];
	}

	private void RecallNextPrompt()
	{
		if (PromptHistoryIndex < 0) return;
		PromptHistoryIndex--;
		Prompt = PromptHistoryIndex < 0
			? PromptHistoryDraft
			: UserPromptHistory()[^(PromptHistoryIndex + 1)];
	}

	private IReadOnlyList<string> UserPromptHistory() =>
		ActiveSession?.Events
			.Where(item => item.Type == "message" && item.Role == "user" && !string.IsNullOrWhiteSpace(item.Content))
			.Select(item => item.Content)
			.ToArray()
		?? [];

	private void ResetPromptHistory()
	{
		PromptHistoryIndex = -1;
		PromptHistoryDraft = string.Empty;
	}

	private async Task SendAsync()
	{
		if (!CanSend || !ModelReady) return;
		if (IsSending)
		{
			if (ActiveSession is not null && Runtime.QueueMessage(ActiveSession.Session.Id, Prompt))
			{
				Prompt = string.Empty;
				ResetPromptHistory();
			}
			return;
		}
		IsSending = true;
		SendCancellation = new CancellationTokenSource();
		try
		{
			if (ActiveSession is null)
			{
				var session = await Runtime.CreateSessionAsync(WorkspaceKey);
				Sessions = await ListVisibleSessionsAsync();
				ActiveSession = await Runtime.GetSessionAsync(session.Id);
			}
			var prompt = string.IsNullOrWhiteSpace(Prompt) ? "Describe the attached image." : Prompt.Trim();
			var references = new List<ImageAttachmentRef>(AttachmentDrafts.Count);
			foreach (var image in AttachmentDrafts)
			{
				references.Add(await ImageAttachments.SaveAsync(image.Name, image.MediaType, image.Bytes, SendCancellation.Token));
			}
			AttachmentDrafts.Clear();
			AttachmentError = null;
			Prompt = string.Empty;
			ResetPromptHistory();
			PendingPrompt = prompt;
			PendingAttachmentRefs = references;
			UpdatePendingTitle(prompt);
			LiveItems.Clear();
			LiveActivity = "Thinking";
			await Runtime.SubmitAsync(
				ActiveSession!.Session.Id,
				prompt,
				OnProgressAsync,
				SendCancellation.Token,
				attachments: references);
			await RefreshAsync(ActiveSession.Session.Id);
			ScrollPending = true;
		}
		catch (OperationCanceledException) when (SendCancellation.IsCancellationRequested)
		{
			if (ActiveSession is not null) await RefreshAsync(ActiveSession.Session.Id);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			AttachmentError = exception.Message;
		}
		finally
		{
			SendCancellation.Dispose();
			SendCancellation = null;
			PendingPrompt = string.Empty;
			PendingAttachmentRefs = [];
			LiveItems.Clear();
			LiveActivity = null;
			IsSending = false;
		}
	}

	private async Task SelectImagesAsync(InputFileChangeEventArgs eventArgs)
	{
		AttachmentError = null;
		try
		{
			var files = eventArgs.GetMultipleFiles(4);
			if (AttachmentDrafts.Count + files.Count > 4) throw new InvalidDataException("Attach at most four images per message.");
			foreach (var file in files)
			{
				if (file.Size > MaxAttachmentBytes) throw new InvalidDataException("Each image must be 10 MB or smaller.");
				if (file.ContentType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
					throw new InvalidDataException($"{file.Name} is not a supported image type.");
				await using var stream = file.OpenReadStream(MaxAttachmentBytes);
				using var buffer = new MemoryStream((int)file.Size);
				await stream.CopyToAsync(buffer);
				var bytes = buffer.ToArray();
				AttachmentDrafts.Add(new ImageDraft(file.Name, file.ContentType, bytes, $"data:{file.ContentType};base64,{Convert.ToBase64String(bytes)}"));
			}
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or InvalidOperationException)
		{
			AttachmentError = exception.Message;
		}
	}

	private void RemoveAttachment(ImageDraft image)
	{
		AttachmentDrafts.Remove(image);
		AttachmentError = null;
	}

	private static string AttachmentUrl(string id) => $"/attachments/{Uri.EscapeDataString(id)}";
}

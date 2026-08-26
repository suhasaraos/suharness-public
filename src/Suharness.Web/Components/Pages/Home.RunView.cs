using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Suharness.Abstractions;
using Suharness.Core;
using System.Text.Json;

namespace Suharness.Web.Components.Pages;

/// <summary>
/// Rendering a turn while it runs. Streamed deltas and tool events arrive here and are
/// summarised into the live rows shown in the transcript.
/// </summary>
public partial class Home
{
	private async ValueTask OnProgressAsync(HarnessRunProgress progress)
	{
		foreach (var item in progress.Events ?? []) AddLiveItem(item);
		if (progress.Events?.Any(item => item.Type == "tool_result") == true) ScheduleModifiedFilesRefresh();
		if (progress.Activity is not null) LiveActivity = progress.Activity;
		else if (progress.TextDelta.Length > 0) LiveActivity = "Responding";
		ScrollPending = true;
		await InvokeAsync(StateHasChanged);
	}

	private void OnToolProgress(Guid sessionId, string toolName, string result)
	{
		if (ActiveSession?.Session.Id != sessionId) return;
		_ = InvokeAsync(() =>
		{
			var callIndex = LiveItems.FindLastIndex(item => item.Type == "tool_call" && item.ToolName == toolName);
			if (callIndex >= 0) LiveItems[callIndex] = LiveItems[callIndex] with { Result = result };
			ScheduleModifiedFilesRefresh();
			ScrollPending = true;
			StateHasChanged();
		});
	}

	private void AddLiveItem(HarnessRunEvent item)
	{
		// The runtime threw the attempt away and is sending the same request again. Its partial output
		// is never persisted, so leaving it on screen would show text that no longer exists anywhere.
		if (item.Type == "retry")
		{
			LiveItems.Clear();
			return;
		}

		if (item.Type == "message")
		{
			if (LiveItems.LastOrDefault() is { Type: "message" } previous)
			{
				LiveItems[^1] = previous with { Content = previous.Content + item.Content };
				return;
			}
			LiveItems.Add(new LiveRunItem(LiveSequence++, item.Key, item.Type, item.Content));
			return;
		}

		if (item.Type == "tool_result")
		{
			var callIndex = LiveItems.FindIndex(existing => existing.Key == item.Key && existing.Type == "tool_call");
			if (callIndex >= 0)
			{
				LiveItems[callIndex] = LiveItems[callIndex] with { Result = item.Content };
				return;
			}
		}

		var index = LiveItems.FindIndex(existing => existing.Key == item.Key && existing.Type == item.Type);
		if (index < 0)
		{
			LiveItems.Add(new LiveRunItem(LiveSequence++, item.Key, item.Type, item.Content, item.ToolName, item.ToolArguments));
			return;
		}
		LiveItems[index] = LiveItems[index] with
		{
			Content = item.Content,
			ToolName = item.ToolName,
			ToolArguments = item.ToolArguments,
		};
	}

	private string LiveToolSummary(LiveRunItem item)
	{
		if (item.Type == "tool_call") return ToolCallSummary(item.ToolName, item.ToolArguments, item.Content);
		var text = item.Content.ReplaceLineEndings("\n");
		var firstLine = text.Split('\n')[0];
		if (firstLine.Length > 120) firstLine = firstLine[..120] + "...";
		if (item.Type == "tool_call") return firstLine;
		var lines = text.Split('\n').Length;
		return lines > 1 ? $"{firstLine}   ({lines} lines)" : firstLine;
	}

	private string ToolCallSummary(string? toolName, string? arguments, string fallback)
	{
		if (string.IsNullOrWhiteSpace(toolName)) return fallback.Split('(', 2)[0];
		var label = char.ToUpperInvariant(toolName[0]) + toolName[1..];
		if (string.IsNullOrWhiteSpace(arguments)) return label;
		try
		{
			using var document = JsonDocument.Parse(arguments);
			var root = document.RootElement;
			var detail = toolName switch
			{
				"bash" => StringProperty(root, "description"),
				"read" or "write" or "edit" => DisplayToolPath(StringProperty(root, "file_path")),
				"glob" or "grep" => StringProperty(root, "pattern"),
				"ask_user_question" => FirstQuestion(root),
				"todo_write" => TodoSummary(root),
				_ => null,
			};
			return string.IsNullOrWhiteSpace(detail) ? label : $"{label} · {detail}";
		}
		catch (JsonException) { return label; }
	}

	private static string? FirstQuestion(JsonElement root) =>
		root.TryGetProperty("questions", out var questions)
			&& questions.ValueKind == JsonValueKind.Array
			&& questions.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first
			? StringProperty(first, "question")
			: null;

	private static string? TodoSummary(JsonElement root) =>
		root.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array
			? $"{todos.GetArrayLength()} tasks"
			: null;

	private string? DisplayToolPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return path;
		var normalized = path.Replace('\\', '/');
		if (CurrentWorkspace is null) return normalized;
		var workspace = WorkspacePath.Replace('\\', '/').TrimEnd('/');
		if (normalized.Equals(workspace, StringComparison.OrdinalIgnoreCase)) return ".";
		return normalized.StartsWith(workspace + "/", StringComparison.OrdinalIgnoreCase)
			? normalized[(workspace.Length + 1)..]
			: normalized;
	}

	private static string? StringProperty(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static string ToolStatus(string? result, string pending) =>
		result is null ? pending
		: result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? "Failed"
		: "Done";

	private void UpdatePendingTitle(string prompt)
	{
		if (ActiveSession is null || ActiveSession.Session.Title != "New session") return;
		var title = prompt[..Math.Min(prompt.Length, 56)];
		var session = ActiveSession.Session with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
		ActiveSession = ActiveSession with { Session = session };
		// A session created inside this turn is not in the sidebar list yet, and mapping over that
		// list cannot add it, so it leads the list until the end-of-turn refresh reorders from disk.
		Sessions = Sessions.Any(item => item.Id == session.Id)
			? Sessions.Select(item => item.Id == session.Id ? session : item).ToArray()
			: Sessions.Prepend(session).ToArray();
	}

	private void Stop() => SendCancellation?.Cancel();

	private async Task RefreshAsync(Guid id)
	{
		Sessions = await ListVisibleSessionsAsync();
		ActiveSession = await LoadStandardSessionAsync(id);
		await RefreshModifiedFilesAsync();
	}
}

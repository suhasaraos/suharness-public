using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

/// <summary>
/// Turning an append-only event log back into the messages a model sees, and turning a model's
/// response back into events. Replay is what makes a session resumable, so the checkpoint and
/// shadowing rules here decide what the model remembers of its own past.
/// </summary>
public sealed partial class HarnessRuntime
{
	private static string InboxLabel(InboxMessageKind kind) => kind switch
	{
		InboxMessageKind.SubagentReport => "subagent",
		InboxMessageKind.JobCompletion => "background job",
		InboxMessageKind.GoalUpdate => "goal",
		InboxMessageKind.Notice => "notice",
		_ => "message",
	};

	private static string? Activity(AgentResponseUpdate update)
	{
		if (update.Contents.OfType<FunctionCallContent>().FirstOrDefault() is { } call)
			return $"Running {call.Name}";
		return update.Contents.OfType<FunctionResultContent>().Any() ? "Tool completed" : null;
	}

	private static IReadOnlyList<HarnessRunEvent> ProgressEvents(AgentResponseUpdate update)
	{
		var events = new List<HarnessRunEvent>();
		foreach (var content in update.Contents)
		{
			switch (content)
			{
				case TextContent text when text.Text.Length > 0:
					events.Add(new HarnessRunEvent(update.MessageId ?? string.Empty, "message", text.Text));
					break;
				case FunctionCallContent call:
					events.Add(new HarnessRunEvent(
						call.CallId,
						"tool_call",
						DescribeCall(call),
						call.Name,
						JsonSerializer.Serialize(call.Arguments)));
					break;
				case FunctionResultContent result:
					events.Add(new HarnessRunEvent(result.CallId, "tool_result", result.Result?.ToString() ?? string.Empty));
					break;
			}
		}
		return events;
	}

	/// <summary>
	/// Rebuild the chat history from the persisted transcript. Notices and errors are presentation
	/// events; messages, tool calls, and tool results are replayed exactly as model-visible turns.
	/// </summary>
	internal static List<ChatMessage> BuildHistory(IReadOnlyList<SessionEvent> events, IImageAttachmentStore? imageAttachments = null)
	{
		var history = new List<ChatMessage>();
		// A reverted span is dropped before anything else looks at the transcript, so an undone turn
		// is invisible to replay while still being on the log.
		events = SessionRevert.Visible(events);
		// A checkpoint stands in for the prefix it shadows, so replay starts from the checkpoint text
		// and continues with the events the compaction deliberately retained.
		var (checkpoint, from) = Checkpoint(events);
		if (checkpoint is not null) history.Add(new ChatMessage(ChatRole.User, checkpoint.Content));

		for (var index = from; index < events.Count; index++)
		{
			var item = events[index];
			// Checkpoints replay from the shadowing pass above, never as ordinary conversation.
			if (item.Type == "compaction") continue;
			// Inbox injections and goal rounds were real model input on the turn that produced them,
			// so they replay as user messages rather than vanishing from the conversation afterwards.
			if (item.Type is "inbox" or "goal_round" or "continuation" && item.Content.Length > 0)
			{
				history.Add(new ChatMessage(ChatRole.User, item.Content));
				continue;
			}

			if (item.Type == "message" && item.ModelMessageId is null && (item.Content.Length > 0 || item.Attachments is { Count: > 0 }))
			{
				if (item.Role == "user" && item.Attachments is { Count: > 0 })
				{
					if (imageAttachments is null) throw new InvalidOperationException("Image attachment storage is not configured.");
					var userContents = new List<AIContent>();
					if (item.Content.Length > 0) userContents.Add(new TextContent(item.Content));
					foreach (var reference in item.Attachments)
					{
						var image = imageAttachments.Read(reference);
						userContents.Add(new DataContent(image.Bytes, reference.MediaType) { Name = reference.Name });
					}
					history.Add(new ChatMessage(ChatRole.User, userContents));
				}
				else if (item.Role == "user") history.Add(new ChatMessage(ChatRole.User, item.Content));
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
				{
					contents.Add(new TextContent(modelEvent.Content));
				}
				else if (modelEvent.Type == "tool_call" && modelEvent.ToolCallId is not null && modelEvent.ToolName is not null)
				{
					var arguments = string.IsNullOrEmpty(modelEvent.ToolArguments)
						? null
						: JsonSerializer.Deserialize<Dictionary<string, object?>>(modelEvent.ToolArguments);
					contents.Add(new FunctionCallContent(modelEvent.ToolCallId, modelEvent.ToolName, arguments));
				}
				else if (modelEvent.Type == "tool_result" && modelEvent.ToolCallId is not null)
				{
					contents.Add(new FunctionResultContent(modelEvent.ToolCallId, modelEvent.Content));
				}
				index++;
			}
			index--;
			if (contents.Count > 0)
				history.Add(new ChatMessage(item.Role == "tool" ? ChatRole.Tool : ChatRole.Assistant, contents));
		}
		return history;
	}

	/// <summary>
	/// The checkpoint in force and the index replay resumes from. The last compaction event wins,
	/// since each one shadows a prefix that includes every earlier checkpoint.
	/// </summary>
	private static (SessionEvent? Checkpoint, int ResumeFrom) Checkpoint(IReadOnlyList<SessionEvent> events)
	{
		for (var index = events.Count - 1; index >= 0; index--)
		{
			if (events[index].Type != "compaction") continue;
			var boundary = events[index].CompactedThroughId;
			if (boundary is null) continue;
			for (var scan = 0; scan < events.Count; scan++)
			{
				if (events[scan].Id == boundary) return (events[index], scan + 1);
			}
			// The boundary is missing, so which prefix it covers is unknown. Replaying the whole
			// transcript is wasteful but correct; dropping an unknown span would not be.
			return (null, 0);
		}
		return (null, 0);
	}

	/// <summary>
	/// Append one completed run to the transcript: every tool call and result as its own event, so
	/// the UI can show what the agent did, then the assistant's text.
	/// </summary>
	private async Task PersistResponseAsync(
		Guid sessionId,
		AgentResponse response,
		HashSet<string> persistedToolCallIds,
		HashSet<string> persistedToolResultIds,
		CancellationToken cancellationToken)
	{
		foreach (var message in response.Messages)
		{
			var modelMessageId = Guid.NewGuid().ToString("N");
			foreach (var content in message.Contents)
			{
				switch (content)
				{
					case TextContent text when !string.IsNullOrEmpty(text.Text):
						await store.AppendAsync(
							NewEvent(sessionId, "message", "assistant", text.Text, modelMessageId),
							cancellationToken);
						break;
					case FunctionCallContent call when persistedToolCallIds.Add(call.CallId):
						await store.AppendAsync(
							NewEvent(
								sessionId,
								"tool_call",
								"assistant",
								DescribeCall(call),
								modelMessageId,
								call.CallId,
								call.Name,
								JsonSerializer.Serialize(call.Arguments)),
							cancellationToken);
						break;
					case FunctionResultContent result when persistedToolResultIds.Add(result.CallId):
						await store.AppendAsync(
							NewEvent(
								sessionId,
								"tool_result",
								"tool",
								result.Result?.ToString() ?? string.Empty,
								modelMessageId,
								result.CallId),
							cancellationToken);
						break;
				}
			}
		}
	}

	/// <summary>Render a tool call as "name(json arguments)" for the transcript.</summary>
	private static string DescribeCall(FunctionCallContent call)
	{
		if (call.Arguments is null || call.Arguments.Count == 0) return $"{call.Name}()";
		string arguments;
		try { arguments = JsonSerializer.Serialize(call.Arguments); }
		catch (NotSupportedException) { arguments = "{}"; }
		return $"{call.Name}({arguments})";
	}

	private static SessionEvent NewEvent(
		Guid sessionId,
		string type,
		string role,
		string content,
		string? modelMessageId = null,
		string? toolCallId = null,
		string? toolName = null,
		string? toolArguments = null,
		IReadOnlyList<ImageAttachmentRef>? attachments = null) =>
		new(
			Guid.NewGuid(),
			sessionId,
			type,
			role,
			content,
			DateTimeOffset.UtcNow,
			modelMessageId,
			toolCallId,
			toolName,
			toolArguments,
			Attachments: attachments);

	/// <summary>
	/// What the reader is told when a failure has exhausted every recovery the harness has. Written
	/// from the classified code rather than the provider's own text, so the advice names the setting
	/// that fixes it instead of quoting an opaque status line.
	/// </summary>
	private static string FriendlyModelError(ModelFailure failure)
	{
		var detail = failure.Message.ReplaceLineEndings(" ").Trim();
		if (detail.Length > 500) detail = detail[..500];
		return failure.Code switch
		{
			ModelFailureCode.Auth =>
				"Model provider rejected the configured API key (HTTP 401). Open model settings and replace it with a valid key.",
			ModelFailureCode.Timeout =>
				"The model endpoint went silent for longer than this provider's idle timeout and the "
				+ "request was abandoned. A local model that needs longer to process a prompt before its "
				+ "first token needs a larger idle timeout for its provider in model settings.",
			ModelFailureCode.ContextWindowExceeded =>
				"The model provider rejected the request for exceeding its context window, and condensing "
				+ "the conversation did not bring it under the limit. Start a new session, or select a "
				+ "model with a larger context window.",
			ModelFailureCode.Quota =>
				$"The model provider reports this account's quota or balance is exhausted: {detail}",
			ModelFailureCode.RateLimit =>
				$"The model provider is still rate-limiting this key after every retry: {detail}",
			ModelFailureCode.EmptyResponse =>
				"The model returned an empty completion on every attempt. A local server usually reports "
				+ "this when its context is full or its sampler settings suppress all output.",
			ModelFailureCode.Transport =>
				$"Could not reach the model endpoint: {detail}",
			_ => $"Model request failed: {detail}",
		};
	}

	private static bool PathsEqual(string left, string right) =>
		string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)
			? string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)
			: string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

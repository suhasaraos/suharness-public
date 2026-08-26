using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Tools;
using System.Runtime.CompilerServices;

namespace Suharness.Infrastructure;

internal static class AgentLoopDiagnostics
{
	public static void Tool(
		TurnContext? turn,
		string callId,
		string toolName,
		string action,
		RepeatToolDecision? decision,
		RepeatToolCompletion? completion)
	{
		var stateChanged = completion?.StateChanged ?? false;
		Console.Error.WriteLine(
			$"[AgentLoop] Session={turn?.SessionId:N} Turn={turn?.TurnNumber ?? 0} Step={turn?.StepNumber ?? 0} "
			+ $"ToolCall={callId} Tool={toolName} Fingerprint={decision?.Fingerprint ?? "unknown"} "
			+ $"RepeatCount={decision?.RepeatCount ?? 1} StateVersion={completion?.StateVersion ?? decision?.StateVersion ?? 0} "
			+ $"PreviousStateVersion={decision?.PreviousStateVersion?.ToString() ?? "none"} "
			+ $"PreviousResultHash={decision?.PreviousResultHash ?? "none"} StateChanged={stateChanged} Action={action}");
	}
}

internal sealed class AgentLoopDiagnosticsChatClient(
	IChatClient inner,
	Guid sessionId,
	IAgentLifecycle? lifecycle) : DelegatingChatClient(inner)
{
	private int _modelCall;
	private readonly HashSet<string> _seenResultIds = new(StringComparer.Ordinal);
	private readonly HashSet<string> _seenProviderCallIds = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _providerCallNames = new(StringComparer.Ordinal);
	private readonly Dictionary<string, int> _providerCallIdCollisions = new(StringComparer.Ordinal);
	private readonly object _providerCallIdGate = new();

	public override Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		var snapshot = messages.ToList();
		Log(snapshot);
		return GetNormalizedResponseAsync(snapshot, options, cancellationToken);
	}

	private async Task<ChatResponse> GetNormalizedResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options,
		CancellationToken cancellationToken)
	{
		var response = await base.GetResponseAsync(messages, options, cancellationToken);
		NormalizeProviderCallIds(response.Messages, new Dictionary<string, string>(StringComparer.Ordinal));
		return response;
	}

	public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		var snapshot = messages.ToList();
		Log(snapshot);
		var responseCallIds = new Dictionary<string, string>(StringComparer.Ordinal);
		await foreach (var update in base.GetStreamingResponseAsync(snapshot, options, cancellationToken))
		{
			NormalizeProviderCallIds([new ChatMessage(update.Role ?? ChatRole.Assistant, update.Contents)], responseCallIds);
			yield return update;
		}
	}

	private void NormalizeProviderCallIds(
		IEnumerable<ChatMessage> messages,
		Dictionary<string, string> responseCallIds)
	{
		foreach (var message in messages)
		{
			for (var index = 0; index < message.Contents.Count; index++)
			{
				if (message.Contents[index] is not FunctionCallContent call || string.IsNullOrWhiteSpace(call.CallId)) continue;
				if (!responseCallIds.TryGetValue(call.CallId, out var normalized))
				{
					normalized = UniqueProviderCallId(call);
					responseCallIds[call.CallId] = normalized;
				}
				if (normalized == call.CallId) continue;
				message.Contents[index] = new FunctionCallContent(normalized, call.Name, call.Arguments);
			}
		}
	}

	private string UniqueProviderCallId(FunctionCallContent call)
	{
		var callId = call.CallId;
		lock (_providerCallIdGate)
		{
			if (_seenProviderCallIds.Add(callId))
			{
				_providerCallNames[callId] = call.Name;
				return callId;
			}
			if (_providerCallNames.GetValueOrDefault(callId) == call.Name) return callId;
			var collision = _providerCallIdCollisions.GetValueOrDefault(callId);
			string candidate;
			do candidate = $"{callId}__suharness_{++collision}";
			while (!_seenProviderCallIds.Add(candidate));
			_providerCallIdCollisions[callId] = collision;
			_providerCallNames[candidate] = call.Name;
			return candidate;
		}
	}

	private void Log(IReadOnlyList<ChatMessage> messages)
	{
		var modelCall = Interlocked.Increment(ref _modelCall);
		var turn = lifecycle?.Current(sessionId);
		var calls = messages
			.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
			.GroupBy(call => call.CallId, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
		var results = messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
		var duplicateCallIds = messages.SelectMany(message => message.Contents.OfType<FunctionCallContent>())
			.GroupBy(call => call.CallId, StringComparer.Ordinal).Count(group => group.Count() > 1);
		Console.Error.WriteLine(
			$"[AgentLoop] Session={sessionId:N} Turn={turn?.TurnNumber ?? 0} ModelCall={modelCall} Step={turn?.StepNumber ?? modelCall} "
			+ $"AssistantToolCalls={calls.Count} ToolResults={results.Length} DuplicateAssistantCallIds={duplicateCallIds} "
			+ $"Balanced={results.All(result => calls.ContainsKey(result.CallId))}");
		foreach (var result in results.Where(result => _seenResultIds.Add(result.CallId)))
		{
			// The first request contains replayed history, not a result produced by this invocation loop.
			// Establish it as the baseline without emitting one line per historical result.
			if (modelCall == 1) continue;
			var text = Render(result.Result);
			Console.Error.WriteLine(
				$"[AgentLoop] Session={sessionId:N} Turn={turn?.TurnNumber ?? 0} ModelCall={modelCall} "
				+ $"ToolCall={result.CallId} MatchedAssistantCall={calls.ContainsKey(result.CallId)} "
				+ $"ResultHash={RepeatToolReminder.Hash(text)} ResultChars={text.Length} FullResultIncluded=true");
		}
	}

	private static string Render(object? result) => result switch
	{
		null => string.Empty,
		string text => text,
		_ => result.ToString() ?? string.Empty,
	};
}

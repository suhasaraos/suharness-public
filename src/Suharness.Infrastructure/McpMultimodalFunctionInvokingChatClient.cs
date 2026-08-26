using Microsoft.Extensions.AI;

namespace Suharness.Infrastructure;

internal sealed record McpMultimodalResult(
	string ToolName,
	string Text,
	IReadOnlyList<DataContent> Images)
{
	public override string ToString() => Text;
}

/// <summary>Promotes image blocks returned by MCP tools into model-facing multimodal messages.</summary>
internal sealed class McpMultimodalFunctionInvokingChatClient(IChatClient inner) : FunctionInvokingChatClient(inner)
{
	protected override IList<ChatMessage> CreateResponseMessages(
		ReadOnlySpan<FunctionInvocationResult> results)
	{
		var messages = base.CreateResponseMessages(results);
		PromoteToolImages(messages);
		return messages;
	}

	internal static void PromoteToolImages(IList<ChatMessage> messages)
	{
		var visualContent = new List<AIContent>();
		foreach (var result in messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>())
		{
			if (result.Result is not McpMultimodalResult multimodal) continue;
			result.Result = multimodal.Text;
			visualContent.Add(new TextContent($"Image output returned by MCP tool {multimodal.ToolName}."));
			visualContent.AddRange(multimodal.Images);
		}

		if (visualContent.Count > 0) messages.Add(new ChatMessage(ChatRole.User, visualContent));
	}
}

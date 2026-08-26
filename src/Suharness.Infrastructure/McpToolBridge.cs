using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Security.Cryptography;
using System.Text;

namespace Suharness.Infrastructure;

/// <summary>
/// How an MCP tool's raw name becomes the name the model sees. Ported from DeepSeek Harness's
/// <c>dsh-mcp-client</c>, including the <c>mcp__server__tool</c> shape Claude Code and Codex also
/// use, so a prompt written against any of them names the same tool.
/// </summary>
public static class McpToolNaming
{
	/// <summary>The function-name budget the providers enforce.</summary>
	public const int MaxNameLength = 64;

	/// <summary>Hex characters of the disambiguating hash appended to an altered name.</summary>
	private const int HashLength = 12;

	/// <summary>Whether a server name may be used as a tool namespace.</summary>
	public static bool IsValidServerName(string name) =>
		name.Length is > 0 and <= 32 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

	/// <summary>
	/// The model-facing name for one server's tool. It is a pure function of the pair, so connection
	/// order, a re-sync, and other servers can never rename a tool the model has already been told
	/// about. When sanitizing or truncating changes the name, a hash of the exact pair is appended,
	/// so two raw names that would otherwise collapse into one stay distinct.
	/// </summary>
	public static string PublicName(string serverName, string rawName)
	{
		var candidate = $"mcp__{serverName}__{rawName}";
		var sanitized = new string([.. candidate.Select(character =>
			char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_')]);
		var altered = !string.Equals(sanitized, candidate, StringComparison.Ordinal) || sanitized.Length > MaxNameLength;
		if (!altered) return sanitized;

		var suffix = "_" + Hash(serverName, rawName);
		return sanitized[..Math.Min(sanitized.Length, MaxNameLength - suffix.Length)] + suffix;
	}

	private static string Hash(string serverName, string rawName)
	{
		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{serverName}\0{rawName}"));
		return Convert.ToHexStringLower(bytes)[..HashLength];
	}
}

/// <summary>
/// One MCP tool presented to the model as an ordinary harness tool. The wrapper exists for three
/// reasons the raw SDK function does not cover: the call carries its own deadline, a server failure
/// comes back as a readable tool result rather than an exception that would end the turn, and the
/// result is rendered to the same plain text every other tool returns.
/// </summary>
internal sealed class McpBridgedTool(
	McpClientTool inner,
	string serverName,
	string rawName,
	int toolCallTimeoutMs) : DelegatingAIFunction(inner)
{
	/// <summary>The raw name sent on the wire. The public name is never shown to the server.</summary>
	public string RawName => rawName;

	protected override async ValueTask<object?> InvokeCoreAsync(
		AIFunctionArguments arguments,
		CancellationToken cancellationToken)
	{
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		if (toolCallTimeoutMs > 0) deadline.CancelAfter(toolCallTimeoutMs);

		try
		{
			var result = await Task.Run(
				async () => await inner.CallAsync(arguments, cancellationToken: deadline.Token),
				CancellationToken.None).WaitAsync(deadline.Token);
			return Render(result);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return $"Error: the {serverName} MCP server did not answer {rawName} within {toolCallTimeoutMs} ms.";
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// A failed remote call is this tool's failure, not the turn's: the model needs a result it
			// can read so it can choose another approach.
			return $"Error: the {serverName} MCP server failed to run {rawName}: {exception.Message}";
		}
	}

	/// <summary>
	/// The plain text the model sees. Text blocks join with newlines; every other block kind becomes a
	/// placeholder naming what was returned, because a harness that silently drops an image block
	/// leaves the model believing the tool returned nothing.
	/// </summary>
	private object Render(CallToolResult result)
	{
		const int maxImages = 4;
		const int maxImageBytes = 10 * 1024 * 1024;
		var images = new List<DataContent>();
		var rendered = string.Join('\n', result.Content.Select((block, index) => block switch
		{
			TextContentBlock text => text.Text,
			ImageContentBlock image when images.Count >= maxImages =>
				"[image content omitted: an MCP tool result may contain at most four model-facing images]",
			ImageContentBlock image when image.DecodedData.Length > maxImageBytes =>
				$"[image content omitted: {image.DecodedData.Length} bytes exceeds the 10 MB limit]",
			ImageContentBlock image => AddImage(image, index),
			_ => $"[{block.Type} content omitted: this harness presents MCP results as text]",
		}));
		if (rendered.Length == 0 && result.StructuredContent is { } structured) rendered = structured.GetRawText();
		if (result.IsError == true) return rendered.Length == 0 ? "Error: the MCP tool reported a failure." : $"Error: {rendered}";
		return images.Count == 0 ? rendered : new McpMultimodalResult(rawName, rendered, images);

		string AddImage(ImageContentBlock image, int index)
		{
			var extension = image.MimeType switch
			{
				"image/jpeg" => "jpg",
				"image/gif" => "gif",
				"image/webp" => "webp",
				_ => "png",
			};
			images.Add(new DataContent(image.DecodedData, image.MimeType)
			{
				Name = $"{rawName}-{index + 1}.{extension}",
			});
			return $"[image content attached: {image.MimeType}, {image.DecodedData.Length} bytes]";
		}
	}
}

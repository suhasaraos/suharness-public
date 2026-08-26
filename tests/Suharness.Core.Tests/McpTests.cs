using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ComponentModel;
using System.IO.Pipelines;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the MCP client bridge. The naming tests
/// matter most: a tool's model-facing name is a promise, and a rename between runs would silently
/// break every prompt and transcript that referred to it.
/// </summary>
public sealed class McpTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-mcp-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	// ─── Tool naming ─────────────────────────────────────────────────────

	/// <summary>
	/// The stable server-qualified shape lets a prompt name a tool unambiguously.
	/// </summary>
	[Fact]
	public void ToolNamesAreServerQualified()
	{
		Assert.Equal("mcp__github__create_issue", McpToolNaming.PublicName("github", "create_issue"));
		Assert.Equal("mcp__web__search", McpToolNaming.PublicName("web", "search"));
	}

	/// <summary>Two servers publishing the same raw name coexist under their own namespaces.</summary>
	[Fact]
	public void TheSameRawNameOnTwoServersDoesNotCollide()
	{
		Assert.NotEqual(McpToolNaming.PublicName("a", "search"), McpToolNaming.PublicName("b", "search"));
	}

	/// <summary>
	/// A name the provider's function-name rules cannot carry is sanitized, and the hash of the exact
	/// pair is appended so two different raw names cannot sanitize into one.
	/// </summary>
	[Fact]
	public void AnUnrepresentableNameIsSanitizedAndDisambiguated()
	{
		var dotted = McpToolNaming.PublicName("web", "search.docs");
		var slashed = McpToolNaming.PublicName("web", "search/docs");
		Assert.StartsWith("mcp__web__search_docs_", dotted, StringComparison.Ordinal);
		Assert.All(dotted, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
		// Both sanitize to the same stem; only the appended hash keeps them apart.
		Assert.NotEqual(dotted, slashed);
	}

	/// <summary>An over-long name is truncated to the provider budget and still stays unique.</summary>
	[Fact]
	public void AnOverlongNameIsTruncatedWithinTheProviderBudget()
	{
		var first = McpToolNaming.PublicName("server", new string('a', 200));
		var second = McpToolNaming.PublicName("server", new string('a', 201));
		Assert.Equal(McpToolNaming.MaxNameLength, first.Length);
		Assert.NotEqual(first, second);
	}

	/// <summary>
	/// The name is a pure function of the pair. Nothing about connection order, a re-sync, or another
	/// server may change it — the model has already been told what the tool is called.
	/// </summary>
	[Fact]
	public void NamesAreStableAcrossCalls()
	{
		Assert.Equal(
			McpToolNaming.PublicName("github", "create_issue"),
			McpToolNaming.PublicName("github", "create_issue"));
	}

	[Theory]
	[InlineData("github", true)]
	[InlineData("my-server_2", true)]
	[InlineData("", false)]
	[InlineData("has space", false)]
	[InlineData("has.dot", false)]
	public void ServerNamesAreValidated(string name, bool valid) =>
		Assert.Equal(valid, McpToolNaming.IsValidServerName(name));

	// ─── Configuration ───────────────────────────────────────────────────

	/// <summary>
	/// Two servers under one name would publish colliding tool names, leaving the model unable to
	/// tell which server it was calling. That is refused at load rather than at the first call.
	/// </summary>
	[Fact]
	public async Task ADuplicateServerNameIsRefused()
	{
		await using var service = new McpClientService();
		var configuration = new McpConfiguration([
			new McpServerConfig("demo", McpTransport.Stdio, Command: "does-not-run", Enabled: false),
			new McpServerConfig("demo", McpTransport.Stdio, Command: "does-not-run", Enabled: false),
		]);

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(configuration));
		Assert.Contains("configured more than once", failure.Message);
	}

	[Fact]
	public async Task AnInvalidServerNameIsRefused()
	{
		await using var service = new McpClientService();
		var configuration = new McpConfiguration([
			new McpServerConfig("has space", McpTransport.Stdio, Command: "does-not-run", Enabled: false),
		]);

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(configuration));
		Assert.Contains("is invalid", failure.Message);
	}

	/// <summary>A disabled server is reported, connects to nothing, and publishes nothing.</summary>
	[Fact]
	public async Task ADisabledServerIsInertButStillReported()
	{
		await using var service = new McpClientService();
		await service.StartAsync(new McpConfiguration([
			new McpServerConfig("demo", McpTransport.Stdio, Command: "does-not-run", Enabled: false),
		]));

		Assert.Empty(service.Tools);
		var snapshot = Assert.Single(service.Servers);
		Assert.Equal(McpServerState.Disabled, snapshot.State);
	}

	/// <summary>
	/// An unreachable server leaves the harness running with no tools from it. Failing startup is
	/// opt-in per server, because one broken server must not stop the others.
	/// </summary>
	[Fact]
	public async Task AnUnreachableServerDoesNotStopStartupByDefault()
	{
		await using var service = new McpClientService(
			transportFactory: _ => throw new IOException("connection refused"));

		await service.StartAsync(new McpConfiguration([
			new McpServerConfig(
				"demo",
				McpTransport.Stdio,
				Command: "does-not-run",
				Reconnect: new McpReconnectOptions(Enabled: false)),
		]));

		Assert.Empty(service.Tools);
		var snapshot = Assert.Single(service.Servers);
		Assert.Equal(McpServerState.Failed, snapshot.State);
		Assert.Contains("connection refused", snapshot.Detail);
	}

	/// <summary>With failOnStartupError set, the same server stops the harness instead.</summary>
	[Fact]
	public async Task AnUnreachableServerCanBeConfiguredToFailStartup()
	{
		await using var service = new McpClientService(
			transportFactory: _ => throw new IOException("connection refused"));

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(new McpConfiguration([
			new McpServerConfig(
				"demo",
				McpTransport.Stdio,
				Command: "does-not-run",
				FailOnStartupError: true,
				Reconnect: new McpReconnectOptions(Enabled: false)),
		])));
		Assert.Contains("could not be reached", failure.Message);
	}

	// ─── Round trip against a real server ────────────────────────────────

	/// <summary>
	/// A real MCP session, in process: the server's tool is discovered, published under its
	/// server-qualified name, and callable with its result rendered as the plain text every other
	/// harness tool returns.
	/// </summary>
	[Fact]
	public async Task AConnectedServerPublishesAndRunsItsTools()
	{
		await using var host = new InProcessServer();
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);

		await service.StartAsync(new McpConfiguration([new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process")]));

		var snapshot = Assert.Single(service.Servers);
		Assert.Equal(McpServerState.Connected, snapshot.State);

		var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(service.Tools));
		Assert.Equal("mcp__demo__shout", tool.Name);

		var result = await tool.InvokeAsync(new AIFunctionArguments { ["text"] = "hello" });
		Assert.Contains("HELLO", result?.ToString());
	}

	/// <summary>
	/// A tool that fails on the server comes back as a readable error result. Letting it throw would
	/// end the whole turn over one remote failure the model could have worked around.
	/// </summary>
	[Fact]
	public async Task AFailingRemoteToolBecomesAnErrorResultRatherThanAThrow()
	{
		await using var host = new InProcessServer();
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);
		await service.StartAsync(new McpConfiguration([new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process")]));

		var tool = (AIFunction)service.Tools.Single();
		var result = await tool.InvokeAsync(new AIFunctionArguments { ["text"] = "boom" });
		Assert.Contains("Error", result?.ToString());
	}

	[Fact]
	public async Task AnImageToolResultIsPromotedToAModelFacingImageMessage()
	{
		await using var host = new InProcessServer(image: true);
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);
		await service.StartAsync(new McpConfiguration([new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process")]));

		var tool = (AIFunction)service.Tools.Single();
		var result = Assert.IsType<McpMultimodalResult>(await tool.InvokeAsync(new AIFunctionArguments()));
		var image = Assert.Single(result.Images);
		Assert.Equal("image/png", image.MediaType);
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Data.ToArray());
		Assert.Contains("image content attached", result.Text);

		var functionResult = new FunctionResultContent("call-1", result);
		IList<ChatMessage> messages = [new ChatMessage(ChatRole.Tool, [functionResult])];
		McpMultimodalFunctionInvokingChatClient.PromoteToolImages(messages);

		Assert.Equal(result.Text, functionResult.Result);
		var visual = Assert.Single(messages, message => message.Role == ChatRole.User);
		Assert.Contains(visual.Contents, content => content is TextContent text && text.Text.Contains("picture"));
		var promoted = Assert.Single(visual.Contents.OfType<DataContent>());
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, promoted.Data.ToArray());
	}

	[Fact]
	public async Task ARemoteToolIgnoringCancellationStillHonorsItsDeadline()
	{
		await using var host = new InProcessServer(hanging: true);
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);
		await service.StartAsync(new McpConfiguration([
			new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process", ToolCallTimeoutMs: 50),
		]));

		var tool = (AIFunction)service.Tools.Single();
		var result = await tool.InvokeAsync(new AIFunctionArguments());

		Assert.Contains("did not answer hang within 50 ms", result?.ToString());
	}

	/// <summary>
	/// Read Only promises a fixed read-only catalog, and the harness cannot tell whether a remote tool
	/// has side effects — so MCP tools are withheld in that mode and offered in the others.
	/// </summary>
	[Fact]
	public async Task McpToolsFollowTheSessionAccessMode()
	{
		await using var host = new InProcessServer();
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);
		await service.StartAsync(new McpConfiguration([new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process")]));

		var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
		var readOnly = new Suharness.Tools.SessionToolset(workspace, HarnessAccessMode.ReadOnly, Guid.NewGuid(), mcp: service);
		var writable = new Suharness.Tools.SessionToolset(workspace, HarnessAccessMode.WorkspaceWrite, Guid.NewGuid(), mcp: service);

		Assert.DoesNotContain(readOnly.BuildTools(), tool => tool is AIFunction function && function.Name.StartsWith("mcp__", StringComparison.Ordinal));
		Assert.Contains(writable.BuildTools(), tool => tool is AIFunction function && function.Name == "mcp__demo__shout");
	}

	[Fact]
	public async Task NoWorkspaceOffersRemoteToolsWithoutLocalTools()
	{
		await using var host = new InProcessServer();
		await using var service = new McpClientService(transportFactory: _ => host.ClientTransport);
		await service.StartAsync(new McpConfiguration([new McpServerConfig("demo", McpTransport.Stdio, Command: "in-process")]));

		var toolset = new Suharness.Tools.SessionToolset(string.Empty, HarnessAccessMode.WorkspaceWrite, Guid.NewGuid(), mcp: service);
		var names = toolset.BuildTools().Select(tool => tool.Name).ToArray();

		Assert.False(toolset.HasWorkspace);
		Assert.Contains("mcp__demo__shout", names);
		Assert.DoesNotContain(names, name => name is "read" or "write" or "edit" or "glob" or "grep" or "bash");
	}

	// ─── Fixtures ────────────────────────────────────────────────────────

	/// <summary>
	/// A real MCP server speaking the protocol over a pair of in-memory pipes, so the bridge is
	/// exercised end to end without launching a process.
	/// </summary>
	private sealed class InProcessServer : IAsyncDisposable
	{
		private readonly McpServer _server;
		private readonly Task _running;
		private readonly TaskCompletionSource<string> _hang = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public InProcessServer(bool hanging = false, bool image = false)
		{
			var toClient = new Pipe();
			var toServer = new Pipe();
			ClientTransport = new StreamClientTransport(
				serverInput: toServer.Writer.AsStream(),
				serverOutput: toClient.Reader.AsStream());

			_server = McpServer.Create(
				new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "demo"),
				new McpServerOptions
				{
					ServerInfo = new Implementation { Name = "demo", Version = "1.0.0" },
					ToolCollection = [image ? McpServerTool.Create(Picture) : hanging ? McpServerTool.Create(Hang) : McpServerTool.Create(Shout)],
				});
			_running = _server.RunAsync();
		}

		public IClientTransport ClientTransport { get; }

		[McpServerTool(Name = "shout")]
		[Description("Upper-cases the supplied text.")]
		private static string Shout(string text) =>
			text == "boom" ? throw new InvalidOperationException("the server refused") : text.ToUpperInvariant();

		[McpServerTool(Name = "hang")]
		[Description("Never completes and ignores cancellation.")]
		private Task<string> Hang() => _hang.Task;

		[McpServerTool(Name = "picture")]
		[Description("Returns a tiny image.")]
		private static CallToolResult Picture() => new()
		{
			Content = [
				new TextContentBlock { Text = "frame" },
				ImageContentBlock.FromBytes(new byte[] { 1, 2, 3, 4 }, "image/png"),
			],
		};

		public async ValueTask DisposeAsync()
		{
			_hang.TrySetResult("disposed");
			await _server.DisposeAsync();
			try { await _running; } catch (Exception) { /* shutdown */ }
		}
	}
}

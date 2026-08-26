using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// End-to-end checks against the real Exa and Unity MCP servers. Skipped by default: they need
/// network access and a Unity editor already open on the named project, so they belong to a person
/// changing this code rather than to CI. To run them, set <c>SUHARNESS_TEST_UNITY_PROJECT</c> to
/// your own project path and remove the <c>Skip</c> from the facts below.
/// </summary>
public sealed class McpLiveTests : IDisposable
{
	private const string Skip = "Live: needs network access and a running Unity editor.";

	/// <summary>
	/// A Unity project with the MCP bridge installed, open in the editor. There is no sensible
	/// default for this, so it is read from the environment: set
	/// <c>SUHARNESS_TEST_UNITY_PROJECT</c> before removing the <see cref="Skip"/> above.
	/// </summary>
	private static readonly string UnityProject =
		Environment.GetEnvironmentVariable("SUHARNESS_TEST_UNITY_PROJECT")
		?? Path.Combine(Path.GetTempPath(), "unity-project");

	private readonly string _root = Directory.CreateTempSubdirectory("suharness-live-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private McpConfigurationService Configuration() =>
		new(_root, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))));

	/// <summary>Exa answers without a key, so a search works as soon as the server is added.</summary>
	[Fact(Skip = Skip)]
	public async Task ExaSearchesTheWeb()
	{
		var configuration = Configuration();
		await configuration.SaveServerAsync(
			new McpServerConfig("exa", McpTransport.StreamableHttp, Url: "https://mcp.exa.ai/mcp"),
			null);

		await using var client = new McpClientService(configuration);
		await client.StartAsync();

		var search = client.Tools.OfType<AIFunction>().Single(tool => tool.Name == "mcp__exa__web_search_exa");
		var result = await search.InvokeAsync(new AIFunctionArguments { ["query"] = "Unity 6 navmesh baking", ["numResults"] = 2 });

		Assert.Equal(McpServerState.Connected, client.Servers.Single().State);
		Assert.False(string.IsNullOrWhiteSpace(result?.ToString()));
	}

	/// <summary>
	/// The Unity CLI attaches to the editor already holding the project open, so the harness reads the
	/// live scene rather than a file on disk.
	/// </summary>
	[Fact(Skip = Skip)]
	public async Task UnityReportsTheOpenEditor()
	{
		var configuration = Configuration();
		await configuration.SaveServerAsync(
			new McpServerConfig("unity", McpTransport.Stdio, Command: "unity", Args: ["mcp", "--project-path", UnityProject]),
			null);

		await using var client = new McpClientService(configuration);
		await client.StartAsync();

		var status = client.Tools.OfType<AIFunction>().Single(tool => tool.Name == "mcp__unity__editor_status");
		var result = await status.InvokeAsync(new AIFunctionArguments());

		Assert.Equal(McpServerState.Connected, client.Servers.Single().State);
		Assert.False(string.IsNullOrWhiteSpace(result?.ToString()));
	}

	/// <summary>
	/// The whole point of the capability filter: a task's tools stay a workable fraction of Unity's
	/// catalog, and the ones the task actually needs are among them.
	/// </summary>
	[Fact(Skip = Skip)]
	public async Task TheUnityCatalogIsNarrowedToTheTask()
	{
		var configuration = Configuration();
		await configuration.SaveServerAsync(
			new McpServerConfig("unity", McpTransport.Stdio, Command: "unity", Args: ["mcp", "--project-path", UnityProject]),
			null);

		await using var client = new McpClientService(configuration);
		await client.StartAsync();

		var workspace = Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName;
		var toolset = new SessionToolset(
			workspace,
			HarnessAccessMode.WorkspaceWrite,
			Guid.NewGuid(),
			mcp: client,
			capabilities: new CapabilityEscalationStore());

		var published = client.Servers.Single().ToolCount;
		var names = toolset.BuildTools("add some trees to the terrain and bake the navmesh")
			.OfType<AIFunction>()
			.Select(tool => tool.Name)
			.ToList();
		var unity = names.Count(name => name.StartsWith("mcp__unity__", StringComparison.Ordinal));

		Assert.True(published > 100, $"expected a large catalog, saw {published}");
		Assert.True(unity < published * 2 / 3, $"expected narrowing, kept {unity} of {published}");
		// The task needs to shape the terrain and to put something on it.
		Assert.Contains("mcp__unity__bake_navmesh", names);
		Assert.Contains("mcp__unity__create_gameobject", names);
		// And it must still be able to check its own work.
		Assert.Contains("mcp__unity__editor_status", names);
	}
}

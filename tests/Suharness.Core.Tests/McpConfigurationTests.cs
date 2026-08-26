using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// The MCP server list and the secrets that go with it. The point of separating them is that the
/// configuration file stays safe to copy, commit, or paste into a bug report, so most of these tests
/// are about a key never reaching it.
/// </summary>
public sealed class McpConfigurationTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-mcpcfg-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private McpConfigurationService Create() =>
		new(_root, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))));

	private string ConfigText => File.ReadAllText(Path.Combine(_root, "mcp.json"));

	[Fact]
	public async Task NoConfigurationMeansNoServers()
	{
		var snapshot = await Create().GetSnapshotAsync();
		Assert.Empty(snapshot.Servers);
	}

	/// <summary>
	/// The file uses the portable <c>mcpServers</c> shape, so existing MCP configurations work here
	/// unchanged.
	/// </summary>
	[Fact]
	public async Task TheEcosystemConfigurationShapeIsRead()
	{
		File.WriteAllText(Path.Combine(_root, "mcp.json"), """
			{
			  "mcpServers": {
			    "exa": { "url": "https://mcp.exa.ai/mcp" },
			    "unity": {
			      "transport": "stdio",
			      "command": "unity",
			      "arguments": ["mcp", "--project-path", "C:\\Projects\\Example Unity Project"]
			    },
			    "off": { "command": "nope", "disabled": true }
			  }
			}
			""");

		var snapshot = await Create().GetSnapshotAsync();

		var exa = snapshot.Servers.Single(server => server.Config.Name == "exa");
		// The transport is inferred from the presence of a URL, as every other harness reads these.
		Assert.Equal(McpTransport.StreamableHttp, exa.Config.Transport);

		var unity = snapshot.Servers.Single(server => server.Config.Name == "unity");
		Assert.Equal(McpTransport.Stdio, unity.Config.Transport);
		// "arguments" is the alias half of the shape; dropping it would reject working files.
		Assert.Equal(["mcp", "--project-path", @"C:\Projects\Example Unity Project"], unity.Config.Args);

		Assert.False(snapshot.Servers.Single(server => server.Config.Name == "off").Config.Enabled);
	}

	/// <summary>A malformed file is reported as no servers rather than taking the harness down.</summary>
	[Fact]
	public async Task AMalformedConfigurationReadsAsEmpty()
	{
		File.WriteAllText(Path.Combine(_root, "mcp.json"), "{ this is not json");
		Assert.Empty((await Create().GetSnapshotAsync()).Servers);
	}

	// ─── Secrets ─────────────────────────────────────────────────────────

	/// <summary>The key is stored, reported as present, and never written to the configuration file.</summary>
	[Fact]
	public async Task AnApiKeyIsStoredOutsideTheConfigurationFile()
	{
		var service = Create();
		await service.SaveServerAsync(
			new McpServerConfig("exa", McpTransport.StreamableHttp, Url: $"https://mcp.exa.ai/mcp?exaApiKey={McpServerConfig.ApiKeyPlaceholder}"),
			"super-secret-key");

		Assert.DoesNotContain("super-secret-key", ConfigText);
		Assert.Contains(McpServerConfig.ApiKeyPlaceholder, ConfigText);
		Assert.True((await service.GetSnapshotAsync()).Servers.Single().CredentialConfigured);
	}

	/// <summary>The stored key is substituted where the configuration says it goes.</summary>
	[Fact]
	public async Task TheKeyIsSubstitutedWhereTheConfigurationPlacesIt()
	{
		var service = Create();
		await service.SaveServerAsync(
			new McpServerConfig("exa", McpTransport.StreamableHttp, Url: $"https://mcp.exa.ai/mcp?exaApiKey={McpServerConfig.ApiKeyPlaceholder}"),
			"abc123");

		var resolved = (await service.ResolveAsync()).Single();
		Assert.Equal("https://mcp.exa.ai/mcp?exaApiKey=abc123", resolved.Url);
		// The placement was explicit, so nothing is guessed on top of it.
		Assert.True(resolved.Headers is null || !resolved.Headers.ContainsKey("Authorization"));
	}

	[Fact]
	public async Task ExaUsesOptionalApiKeyHeaderWithoutSendingAnEmptyKey()
	{
		var service = Create();
		var config = new McpServerConfig(
			"exa",
			McpTransport.StreamableHttp,
			Url: "https://mcp.exa.ai/mcp",
			Headers: new Dictionary<string, string> { ["x-api-key"] = McpServerConfig.ApiKeyPlaceholder });

		await service.SaveServerAsync(config, null);
		var keyless = (await service.ResolveAsync()).Single();
		Assert.Equal("https://mcp.exa.ai/mcp", keyless.Url);
		Assert.True(keyless.Headers is null || !keyless.Headers.ContainsKey("x-api-key"));

		await service.SaveServerAsync(config, "exa-test-key");
		var keyed = (await service.ResolveAsync()).Single();
		Assert.Equal("exa-test-key", keyed.Headers!["x-api-key"]);
		Assert.False(keyed.Headers.ContainsKey("Authorization"));
	}

	/// <summary>
	/// A key with nowhere stated goes out as a bearer token, which is what most HTTP MCP servers
	/// expect. Doing nothing with it would leave the server rejecting every call for no visible reason.
	/// </summary>
	[Fact]
	public async Task AKeyWithNoStatedPlacementBecomesABearerToken()
	{
		var service = Create();
		await service.SaveServerAsync(
			new McpServerConfig("api", McpTransport.StreamableHttp, Url: "https://mcp.example.com/mcp"),
			"abc123");

		var resolved = (await service.ResolveAsync()).Single();
		Assert.Equal("Bearer abc123", resolved.Headers!["Authorization"]);
	}

	/// <summary>A stdio server's key is never turned into an HTTP header it has no way to read.</summary>
	[Fact]
	public async Task AStdioServerGetsNoInventedHeader()
	{
		var service = Create();
		await service.SaveServerAsync(new McpServerConfig("local", McpTransport.Stdio, Command: "thing"), "abc123");

		var resolved = (await service.ResolveAsync()).Single();
		Assert.True(resolved.Headers is null || resolved.Headers.Count == 0);
	}

	/// <summary>An environment reference is expanded, so a key can also come from the shell.</summary>
	[Fact]
	public async Task EnvironmentReferencesAreExpanded()
	{
		Environment.SetEnvironmentVariable("SUHARNESS_TEST_MCP_KEY", "from-env");
		try
		{
			var service = Create();
			await service.SaveServerAsync(
				new McpServerConfig("api", McpTransport.StreamableHttp, Url: "https://example.com/mcp?k=${env:SUHARNESS_TEST_MCP_KEY}"),
				null);

			Assert.Equal("https://example.com/mcp?k=from-env", (await service.ResolveAsync()).Single().Url);
		}
		finally { Environment.SetEnvironmentVariable("SUHARNESS_TEST_MCP_KEY", null); }
	}

	/// <summary>
	/// A null key on a later save keeps what is stored, so an endpoint can be corrected without the
	/// user retyping a secret the settings view never shows back. An empty one clears it.
	/// </summary>
	[Fact]
	public async Task ANullKeyKeepsTheStoredOneAndAnEmptyKeyClearsIt()
	{
		var service = Create();
		var config = new McpServerConfig("api", McpTransport.StreamableHttp, Url: "https://example.com/mcp");
		await service.SaveServerAsync(config, "abc123");

		await service.SaveServerAsync(config with { Url = "https://example.com/v2" }, null);
		Assert.True((await service.GetSnapshotAsync()).Servers.Single().CredentialConfigured);
		Assert.Equal("Bearer abc123", (await service.ResolveAsync()).Single().Headers!["Authorization"]);

		await service.SaveServerAsync(config, string.Empty);
		Assert.False((await service.GetSnapshotAsync()).Servers.Single().CredentialConfigured);
	}

	/// <summary>Deleting a server takes its key with it.</summary>
	[Fact]
	public async Task DeletingAServerRemovesItsKey()
	{
		var service = Create();
		await service.SaveServerAsync(new McpServerConfig("api", McpTransport.StreamableHttp, Url: "https://example.com/mcp"), "abc123");
		await service.DeleteServerAsync("api");

		Assert.Empty((await service.GetSnapshotAsync()).Servers);
		await service.SaveServerAsync(new McpServerConfig("api", McpTransport.StreamableHttp, Url: "https://example.com/mcp"), null);
		Assert.False((await service.GetSnapshotAsync()).Servers.Single().CredentialConfigured);
	}

	// ─── Validation and round-tripping ───────────────────────────────────

	[Fact]
	public async Task AServerThatCannotBeConnectedIsRefusedAtTheSave()
	{
		var service = Create();
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.SaveServerAsync(new McpServerConfig("has space", McpTransport.Stdio, Command: "x"), null));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.SaveServerAsync(new McpServerConfig("nocmd", McpTransport.Stdio), null));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.SaveServerAsync(new McpServerConfig("nourl", McpTransport.StreamableHttp), null));
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			service.SaveServerAsync(new McpServerConfig("relative", McpTransport.StreamableHttp, Url: "/mcp"), null));
	}

	/// <summary>What the settings view writes is what it reads back.</summary>
	[Fact]
	public async Task AServerRoundTripsThroughTheFile()
	{
		var service = Create();
		var config = new McpServerConfig(
			"unity",
			McpTransport.Stdio,
			Command: "unity",
			Args: ["mcp", "--project-path", @"C:\Projects\Example Unity Project"],
			Cwd: @"C:\Projects",
			RequiresApproval: true,
			Enabled: false);
		await service.SaveServerAsync(config, null);

		var read = (await Create().GetSnapshotAsync()).Servers.Single().Config;
		Assert.Equal(config.Name, read.Name);
		Assert.Equal(config.Transport, read.Transport);
		Assert.Equal(config.Command, read.Command);
		Assert.Equal(config.Args, read.Args);
		Assert.Equal(config.Cwd, read.Cwd);
		Assert.True(read.RequiresApproval);
		Assert.False(read.Enabled);
	}

	/// <summary>A disabled server is configured but never connected, so it is not offered for connect.</summary>
	[Fact]
	public async Task ResolveSkipsDisabledServers()
	{
		var service = Create();
		await service.SaveServerAsync(new McpServerConfig("on", McpTransport.Stdio, Command: "a"), null);
		await service.SaveServerAsync(new McpServerConfig("off", McpTransport.Stdio, Command: "b", Enabled: false), null);

		Assert.Equal("on", (await service.ResolveAsync()).Single().Name);
	}

	/// <summary>The written file is the ecosystem shape, not an internal one.</summary>
	[Fact]
	public async Task TheWrittenFileUsesTheEcosystemShape()
	{
		await Create().SaveServerAsync(new McpServerConfig("unity", McpTransport.Stdio, Command: "unity", Args: ["mcp"]), null);

		using var document = JsonDocument.Parse(ConfigText);
		var servers = document.RootElement.GetProperty("mcpServers");
		Assert.Equal("unity", servers.GetProperty("unity").GetProperty("command").GetString());
	}
}

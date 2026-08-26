using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// The way out of a narrowed tool catalog. Without it a filter that guessed wrong is
/// indistinguishable to the model from a server that cannot do the job, and the turn ends in a
/// blocker the harness invented.
/// </summary>
public sealed class CapabilityEscalationTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-escalate-").FullName;
	private readonly Guid _session = Guid.NewGuid();

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	[Fact]
	public void RequestsAreRecordedPerSessionAndDeduplicated()
	{
		var store = new CapabilityEscalationStore();
		var other = Guid.NewGuid();

		Assert.True(store.Request(_session, "Terrain", "cannot shape the ground"));
		Assert.True(store.Request(_session, "terrain", "again"));
		Assert.True(store.Request(_session, "runtime", "cannot enter play mode"));
		Assert.False(store.Request(_session, "   ", "no group named"));

		Assert.Equal(["terrain", "runtime"], store.Requested(_session));
		Assert.Empty(store.Requested(other));

		store.Clear(_session);
		Assert.Empty(store.Requested(_session));
	}

	/// <summary>
	/// The escalation tool appears only when a catalog was actually narrowed. Offering a way to widen
	/// a catalog that was never filtered would be an invitation to spend a turn on nothing.
	/// </summary>
	[Fact]
	public void TheEscalationToolAppearsOnlyWhenSomethingWasNarrowed()
	{
		var store = new CapabilityEscalationStore();
		var small = Toolset(new StubProvider([("exa", 2)]), store);
		var large = Toolset(new StubProvider([("unity", 60)]), store);

		Assert.DoesNotContain(small.BuildTools("anything"), Named("request_capability"));
		Assert.Contains(large.BuildTools("bake the navmesh"), Named("request_capability"));
	}

	/// <summary>
	/// A requested group is honoured on the next turn. It cannot apply to the turn that asked: the
	/// tool catalog handed to a model is fixed for the duration of its request, so the tool says so
	/// rather than implying the tools just appeared.
	/// </summary>
	[Fact]
	public async Task ARequestedGroupIsHonouredOnTheNextTurn()
	{
		var store = new CapabilityEscalationStore();
		var toolset = Toolset(new StubProvider([("unity", 60)]), store);

		var before = toolset.BuildTools("change the material colour");
		Assert.DoesNotContain(before, Named("mcp__unity__terrain_1"));

		var request = before.OfType<AIFunction>().Single(tool => tool.Name == "request_capability");
		var answer = await request.InvokeAsync(new AIFunctionArguments
		{
			["group"] = "terrain",
			["reason"] = "no tool can shape the ground",
		});
		Assert.Contains("next turn", answer?.ToString());
		Assert.Equal(["terrain"], store.Requested(_session));

		var after = toolset.BuildTools("change the material colour");
		Assert.Contains(after, Named("mcp__unity__terrain_1"));
	}

	/// <summary>A request naming no group is refused, and says which groups exist.</summary>
	[Fact]
	public async Task AnEmptyRequestIsRefusedWithTheAvailableGroups()
	{
		var toolset = Toolset(new StubProvider([("unity", 60)]), new CapabilityEscalationStore());
		var request = toolset.BuildTools("change the material colour").OfType<AIFunction>()
			.Single(tool => tool.Name == "request_capability");

		var answer = await request.InvokeAsync(new AIFunctionArguments { ["group"] = " ", ["reason"] = "x" });

		Assert.Contains("name a group", answer?.ToString(), StringComparison.OrdinalIgnoreCase);
		Assert.Contains("terrain", answer?.ToString());
	}

	private SessionToolset Toolset(IMcpToolProvider mcp, ICapabilityEscalation store) =>
		new(Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName,
			HarnessAccessMode.WorkspaceWrite,
			_session,
			mcp: mcp,
			capabilities: store);

	private static Predicate<AITool> Named(string name) => tool => tool is AIFunction function && function.Name == name;

	/// <summary>
	/// A provider publishing synthetic catalogs of a chosen size, named so they classify into known
	/// groups. Enough to exercise the filter without a real server behind it.
	/// </summary>
	private sealed class StubProvider(IReadOnlyList<(string Server, int Count)> servers) : IMcpToolProvider
	{
		private static readonly string[] Kinds =
			["terrain", "material", "gameobject", "scene", "editor_status", "script", "test", "animator", "asset", "physics"];

		public IReadOnlyList<McpServerTools> Published { get; } =
			[.. servers.Select(entry => new McpServerTools(entry.Server, Build(entry.Server, entry.Count)))];

		public IReadOnlyList<AITool> Tools => [.. Published.SelectMany(entry => entry.Tools)];

		public IReadOnlyList<McpServerSnapshot> Servers =>
			[.. Published.Select(entry => new McpServerSnapshot(entry.Server, McpServerState.Connected, entry.Tools.Count))];

		public event Action? Changed { add { } remove { } }

		public string? ServerOf(string toolName) =>
			Published.FirstOrDefault(entry => entry.Tools.Any(tool => tool is AIFunction function && function.Name == toolName))?.Server;

		public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

		public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

		private static IReadOnlyList<AITool> Build(string server, int count) =>
		[
			.. Enumerable.Range(0, count).Select(index =>
			{
				var kind = Kinds[index % Kinds.Length];
				return AIFunctionFactory.Create(
					() => string.Empty,
					new AIFunctionFactoryOptions
					{
						Name = $"mcp__{server}__{kind}_{index / Kinds.Length}",
						Description = kind,
					});
			}),
		];
	}
}

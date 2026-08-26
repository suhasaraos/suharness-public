using Microsoft.Extensions.AI;
using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// The capability filter that keeps a very large MCP catalog affordable. Unity's server publishes 142
/// tools costing roughly 20,000 tokens of schema, paid on every request of every turn; without this
/// a small model has spent most of its context before reading the task.
/// </summary>
/// <remarks>
/// The tests that matter most are the ones about failing towards keeping tools. Every rule here is a
/// keyword heuristic, so the cost of a wrong guess has to fall on tokens rather than on capability.
/// </remarks>
public sealed class CapabilityCatalogTests
{
	[Theory]
	[InlineData("mcp__unity__bake_navmesh", McpCapabilityCatalog.Terrain)]
	[InlineData("mcp__unity__create_gameobject", McpCapabilityCatalog.Hierarchy)]
	[InlineData("mcp__unity__set_material_properties", McpCapabilityCatalog.Rendering)]
	[InlineData("mcp__unity__run_tests", McpCapabilityCatalog.Testing)]
	[InlineData("mcp__unity__capture_game_view", McpCapabilityCatalog.Runtime)]
	[InlineData("mcp__unity__create_script", McpCapabilityCatalog.Scripting)]
	[InlineData("mcp__unity__open_scene", McpCapabilityCatalog.Scene)]
	[InlineData("mcp__unity__editor_status", McpCapabilityCatalog.Core)]
	public void ToolsAreGroupedByWhatTheyDo(string name, string expected) =>
		Assert.Equal(expected, McpCapabilityCatalog.GroupFor(name, serverName: "unity"));

	/// <summary>
	/// A tool the vocabulary does not recognize is kept rather than hidden. It is more likely to be
	/// the one dispatcher a task needs than something safe to drop.
	/// </summary>
	[Fact]
	public void AnUnrecognizedToolLandsInTheAlwaysKeptGroup()
	{
		Assert.Equal(McpCapabilityCatalog.Other, McpCapabilityCatalog.GroupFor("mcp__x__zorble_frobnicate", serverName: "x"));
		var selection = McpCapabilityCatalog.Select("change the material colour", McpCapabilityCatalog.AllGroups);
		Assert.True(selection.Contains(McpCapabilityCatalog.Other));
	}

	/// <summary>
	/// A raw name is read without its namespace, and the exact prefix is stripped rather than
	/// everything up to the last separator, because a server may publish a name containing one.
	/// </summary>
	[Fact]
	public void TheServerNamespaceIsNotMistakenForPartOfTheToolName()
	{
		Assert.Equal(
			McpCapabilityCatalog.Terrain,
			McpCapabilityCatalog.GroupFor("mcp__editor__bake__navmesh", serverName: "editor"));
	}

	/// <summary>The task's own wording chooses the groups.</summary>
	[Fact]
	public void TheTaskWordingSelectsItsGroups()
	{
		var selection = McpCapabilityCatalog.Select(
			"add some trees to the terrain and bake the navmesh",
			McpCapabilityCatalog.AllGroups);

		Assert.True(selection.Contains(McpCapabilityCatalog.Terrain));
		Assert.False(selection.FailedOpen);
	}

	/// <summary>
	/// Status, hierarchy, and scene tools survive every selection. Checking your own work and reaching
	/// the objects you are changing are not optional, whatever the task says.
	/// </summary>
	[Fact]
	public void TheAlwaysKeptGroupsSurviveAnySelection()
	{
		var selection = McpCapabilityCatalog.Select("run the edit mode tests", McpCapabilityCatalog.AllGroups);

		Assert.True(selection.Contains(McpCapabilityCatalog.Core));
		Assert.True(selection.Contains(McpCapabilityCatalog.Hierarchy));
		Assert.True(selection.Contains(McpCapabilityCatalog.Scene));
		Assert.True(selection.Contains(McpCapabilityCatalog.Testing));
	}

	/// <summary>Wording that matches nothing widens rather than narrows, and says that it did.</summary>
	[Fact]
	public void AnUnrecognizableTaskWidens()
	{
		var selection = McpCapabilityCatalog.Select("fix it", McpCapabilityCatalog.AllGroups);

		Assert.True(selection.FailedOpen);
		Assert.Contains("No capability keyword matched", selection.Reason);
		Assert.True(selection.Contains(McpCapabilityCatalog.Rendering));
		Assert.True(selection.Contains(McpCapabilityCatalog.Scripting));
	}

	/// <summary>
	/// A group the agent asked for is honoured whatever the wording says, which is what makes
	/// narrowing recoverable rather than a dead end.
	/// </summary>
	[Fact]
	public void AnEscalatedGroupIsAlwaysHonoured()
	{
		var selection = McpCapabilityCatalog.Select(
			"change the material colour",
			McpCapabilityCatalog.AllGroups,
			escalated: ["terrain"]);

		Assert.True(selection.Contains(McpCapabilityCatalog.Terrain));
	}

	/// <summary>A selection never names a group the connected servers do not actually publish.</summary>
	[Fact]
	public void SelectionIsLimitedToGroupsThatExist()
	{
		var selection = McpCapabilityCatalog.Select(
			"bake the navmesh",
			[McpCapabilityCatalog.Core, McpCapabilityCatalog.Other]);

		Assert.All(selection.Groups, group => Assert.Contains(group, new[] { McpCapabilityCatalog.Core, McpCapabilityCatalog.Other }));
	}

	/// <summary>
	/// A task that says what to preserve as well as what to change names most of the catalog. Ranking
	/// and keeping only the strongest few is what stops the filter from selecting everything.
	/// </summary>
	[Fact]
	public void ATaskNamingManyAreasKeepsOnlyItsStrongestMatches()
	{
		var selection = McpCapabilityCatalog.Select(
			"rework the material and shader colour, but keep the physics, collision, animation, timeline and test behaviour exactly as it is",
			McpCapabilityCatalog.AllGroups,
			maxMatchedGroups: 2);

		// Rendering is what the task changes and scores highest; the areas it merely promises not to
		// break must not drag the whole catalog back in.
		Assert.True(selection.Contains(McpCapabilityCatalog.Rendering));
		Assert.DoesNotContain(McpCapabilityCatalog.Testing, selection.Groups);
	}

	// ─── Filtering ───────────────────────────────────────────────────────

	/// <summary>A small catalog is never filtered: there is nothing to save and something to lose.</summary>
	[Fact]
	public void ASmallCatalogIsLeftAlone()
	{
		var tools = Catalog(("web_search", null), ("web_fetch", null));
		var selection = McpCapabilityCatalog.Select("bake the navmesh", McpCapabilityCatalog.AllGroups);

		Assert.Equal(tools.Count, McpCapabilityCatalog.Filter(tools, selection, "exa").Count);
	}

	/// <summary>A large catalog is narrowed to the selected groups.</summary>
	[Fact]
	public void ALargeCatalogIsNarrowed()
	{
		var tools = LargeUnityLikeCatalog();
		var selection = McpCapabilityCatalog.Select("bake the navmesh", McpCapabilityCatalog.GroupsIn(tools, "unity"));

		var kept = McpCapabilityCatalog.Filter(tools, selection, "unity");

		Assert.True(kept.Count < tools.Count);
		Assert.Contains(kept, tool => tool.Name.Contains("navmesh", StringComparison.Ordinal));
		// The task said nothing about animation, and nothing always-kept covers it.
		Assert.DoesNotContain(kept, tool => tool.Name.Contains("animator", StringComparison.Ordinal));
	}

	/// <summary>
	/// A filter that would leave nothing is discarded. An empty result means the classifier is wrong
	/// about this catalog, not that the task needs no tools at all.
	/// </summary>
	[Fact]
	public void AFilterThatWouldEmptyTheCatalogIsDiscarded()
	{
		var tools = Catalog([.. Enumerable.Range(0, 50).Select(index => ($"zorble_{index}", (string?)null))]);
		var selection = new CapabilitySelection([McpCapabilityCatalog.Terrain], false, "test");

		Assert.Equal(tools.Count, McpCapabilityCatalog.Filter(tools, selection, "x").Count);
	}

	private static IReadOnlyList<AITool> Catalog(params (string Name, string? Description)[] tools) =>
		[.. tools.Select(tool => AIFunctionFactory.Create(
			() => string.Empty,
			new AIFunctionFactoryOptions { Name = $"mcp__unity__{tool.Name}", Description = tool.Description ?? tool.Name }))];

	/// <summary>Enough tools, across enough groups, to be past the filtering threshold.</summary>
	private static IReadOnlyList<AITool> LargeUnityLikeCatalog()
	{
		string[] names =
		[
			"bake_navmesh", "clear_navmesh", "get_navmesh_settings", "set_navmesh_settings", "navmesh_bake_status",
			"create_gameobject", "delete_gameobject", "set_parent", "add_component", "remove_component",
			"set_material_properties", "get_material_properties", "list_shaders", "bake_lighting", "set_graphics_settings",
			"create_animation_clip", "get_animator_controller", "add_animator_layer", "add_animator_state", "add_timeline_track",
			"run_tests", "list_tests", "cancel_tests",
			"editor_status", "console", "clear_console", "get_console_logs", "menu", "save_all",
			"open_scene", "create_scene", "list_open_scenes", "set_active_scene",
			"create_script", "recompile", "eval", "get_serialized_fields",
			"create_asset", "delete_asset", "import_asset", "find_assets", "create_folder",
			"editor_play", "editor_stop", "capture_game_view", "screenshot",
		];
		return Catalog([.. names.Select(name => (name, (string?)null))]);
	}
}

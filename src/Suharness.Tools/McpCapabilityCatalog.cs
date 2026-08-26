using Microsoft.Extensions.AI;

namespace Suharness.Tools;

/// <summary>
/// The capability groups chosen for one turn, and whether the choice was confident or a deliberate
/// widening. <see cref="FailedOpen"/> exists so an uncertain classification is visible rather than
/// quietly removing the one tool the task needed.
/// </summary>
public sealed record CapabilitySelection(IReadOnlyList<string> Groups, bool FailedOpen, string Reason)
{
	/// <summary>A selection that filters nothing, for a catalog small enough not to need it.</summary>
	public static readonly CapabilitySelection Unfiltered = new([], false, "The catalog is small enough to send whole.");

	public bool Contains(string group) => Groups.Contains(group, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Sorts a large MCP tool catalog into capability groups and picks the groups a task plausibly
/// needs. This keeps large catalogs practical: Unity's MCP server publishes 142 tools whose schemas
/// cost roughly 20,000 tokens, which a local model pays on every request of every turn before the
/// task itself is described.
/// </summary>
/// <remarks>
/// Classification is by name and description token, never by a hard-coded tool list, so a different
/// server version regroups itself instead of falling off a whitelist. Every rule here is a
/// heuristic, so each one is built to fail towards keeping tools rather than removing them: an
/// unrecognized tool lands in <see cref="Other"/> which is always kept, a task whose wording matches
/// nothing widens instead of narrowing, and a filter that would empty the catalog is discarded.
/// </remarks>
public static class McpCapabilityCatalog
{
	public const string Core = "core";
	public const string Scene = "scene";
	public const string Runtime = "runtime";
	public const string Testing = "testing";
	public const string Rendering = "rendering";
	public const string Assets = "assets";
	public const string Hierarchy = "hierarchy";
	public const string Animation = "animation";
	public const string Terrain = "terrain";
	public const string Scripting = "scripting";
	public const string Physics = "physics";
	public const string Interface = "ui";
	public const string Build = "build";
	public const string Search = "search";

	/// <summary>
	/// Tools the classifier did not recognize. Always kept: an unclassified tool is more likely to be
	/// the dispatcher a task needs than something safe to hide.
	/// </summary>
	public const string Other = "other";

	/// <summary>
	/// The number of tools a server may publish before its catalog is narrowed. Below it, filtering
	/// saves little and can only remove something the task needed.
	/// </summary>
	public const int DefaultFilterThreshold = 40;

	/// <summary>
	/// Groups kept for every task, whatever its wording. Status and console tools are how an agent
	/// checks its own work, and in an editor-shaped catalog almost nothing can be done without
	/// reaching an object or the scene holding it — "add trees to the terrain" needs the terrain
	/// tools to shape it and the hierarchy tools to put anything in it. A group with no members in a
	/// given catalog costs nothing, so naming them here is safe for servers of any shape.
	/// </summary>
	private static readonly string[] Always = [Core, Hierarchy, Scene, Other];

	/// <summary>Groups added when the task's wording matched nothing, so nothing useful is dropped.</summary>
	private static readonly string[] SafeWidening =
		[Core, Scene, Hierarchy, Assets, Scripting, Rendering, Runtime];

	/// <summary>
	/// Tool-name and description tokens that place a tool in a group. Order matters: the first group
	/// whose vocabulary appears in the name wins, so the more specific groups are listed first.
	/// </summary>
	private static readonly (string Group, string[] Tokens)[] ToolTokens =
	[
		(Testing, ["test", "testrunner", "editmode", "playmode_test"]),
		(Build, ["build", "player_settings", "build_target", "build_profile", "switch_build"]),
		(Runtime, ["play_mode", "playmode", "enter_play", "exit_play", "editor_play", "editor_pause", "editor_stop", "pause", "game_view", "screenshot", "screen_capture", "capture", "runtime", "performance", "autotick"]),
		(Rendering, ["material", "shader", "render", "hdrp", "urp", "light", "lighting", "postprocess", "post_process", "texture", "sprite", "color", "volume", "skybox", "occlusion", "graphics", "quality"]),
		(Animation, ["animation", "animator", "avatar", "timeline", "rig", "keyframe", "clip", "curve"]),
		(Terrain, ["terrain", "tree", "grass", "vegetation", "foliage", "navmesh", "nav_mesh", "environment", "probuilder", "spline"]),
		(Physics, ["physics", "collider", "rigidbody", "collision", "raycast", "joint"]),
		(Interface, ["canvas", "uielements", "uitoolkit", "uxml", "gui", "layout"]),
		(Scripting, ["script", "code", "csharp", "class", "symbol", "namespace", "text_edit", "compile", "diagnostic", "usage", "refactor", "eval", "serialized"]),
		(Hierarchy, ["gameobject", "game_object", "component", "transform", "prefab", "hierarchy", "instantiate", "parent", "select", "tag", "layer"]),
		(Scene, ["scene", "level", "camera", "view"]),
		(Search, ["search", "find", "query"]),
		(Assets, ["asset", "import", "folder", "package", "addressable", "resource", "database", "file", "directory", "path", "audio"]),
		(Core, ["editor_status", "status", "console", "log", "menu", "refresh", "help", "capabilities", "ping", "version", "settings", "undo", "save", "audit", "input", "time"]),
	];

	/// <summary>Task wording that makes a group relevant.</summary>
	private static readonly (string Group, string[] Tokens)[] IntentTokens =
	[
		(Rendering, ["material", "shader", "render", "hdrp", "urp", "texture", "colour", "color", "visual", "look", "appearance", "lighting", "light", "shading", "albedo", "normal map", "skybox", "post-processing"]),
		(Terrain, ["tree", "trees", "forest", "terrain", "grass", "vegetation", "foliage", "environment", "landscape", "outdoor", "navmesh", "navigation mesh"]),
		(Hierarchy, ["gameobject", "game object", "prefab", "component", "hierarchy", "spawn", "place", "instantiate", "transform", "parent", "position", "rotate", "scale"]),
		(Scene, ["scene", "level", "camera", "open the", "load the", "view"]),
		(Runtime, ["play mode", "playmode", "screenshot", "capture", "runtime", "game view", "in-game", "enter play", "performance", "profile"]),
		(Testing, ["test", "tests", "editmode", "unit test", "verify", "validate", "regression"]),
		(Build, ["build", "player settings", "build target", "platform", "il2cpp", "standalone"]),
		(Assets, ["asset", "import", "package", "addressable", "folder", "file", "audio", "sound"]),
		(Scripting, ["script", "code", "class", "method", "c#", "csharp", "compile", "recompile", "implement", "refactor", "serialized field"]),
		(Animation, ["animation", "animator", "timeline", "animate", "rig", "keyframe"]),
		(Physics, ["physics", "collision", "collider", "rigidbody", "gravity", "movement"]),
		(Interface, ["canvas", "hud", "menu", "ui ", "user interface"]),
		(Search, ["find", "search", "locate", "where is", "look for"]),
	];

	public static IReadOnlyList<string> AllGroups { get; } =
		[Core, Scene, Runtime, Testing, Rendering, Assets, Hierarchy, Animation, Terrain, Scripting, Physics, Interface, Build, Search, Other];

	/// <summary>The group a tool belongs to, from its name first and its description second.</summary>
	public static string GroupFor(string toolName, string? description = null, string? serverName = null)
	{
		var name = Bare(toolName, serverName);
		foreach (var (group, tokens) in ToolTokens)
		{
			if (tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase))) return group;
		}

		if (!string.IsNullOrWhiteSpace(description))
		{
			foreach (var (group, tokens) in ToolTokens)
			{
				if (tokens.Any(token => description.Contains(token, StringComparison.OrdinalIgnoreCase))) return group;
			}
		}

		return Other;
	}

	/// <summary>The groups a catalog actually contains, so a selection can never name one that is absent.</summary>
	public static IReadOnlyList<string> GroupsIn(IEnumerable<AITool> tools, string? serverName = null) =>
		[.. tools.Select(tool => GroupFor(tool.Name, tool.Description, serverName)).Distinct(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// Picks the groups a task needs from its own wording. Core is always included, groups the agent
	/// escalated for are always honoured, and a weak match widens rather than narrows.
	/// </summary>
	/// <param name="maxMatchedGroups">
	/// How many keyword-matched groups survive. A real task states what to preserve as well as what to
	/// change — "keep the collision and physics behaviour" names areas the work must not touch — so an
	/// unranked match selects nearly the whole catalog and saves nothing. Ranking by how much of a
	/// group's vocabulary the task actually uses, then keeping the strongest few, is what makes the
	/// filter worth having.
	/// </param>
	public static CapabilitySelection Select(
		string? intent,
		IReadOnlyCollection<string> availableGroups,
		IReadOnlyCollection<string>? escalated = null,
		int maxMatchedGroups = 4)
	{
		var text = (intent ?? string.Empty).ToLowerInvariant();
		var scores = new List<(string Group, int Score)>();
		foreach (var (group, tokens) in IntentTokens)
		{
			var score = tokens.Count(token => text.Contains(token, StringComparison.Ordinal));
			if (score > 0) scores.Add((group, score));
		}

		var ranked = scores
			.OrderByDescending(entry => entry.Score)
			.ThenBy(entry => entry.Group, StringComparer.Ordinal)
			.Take(Math.Max(1, maxMatchedGroups))
			.ToList();

		var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var group in Always) chosen.Add(group);
		foreach (var (group, _) in ranked) chosen.Add(group);
		foreach (var group in escalated ?? [])
		{
			if (!string.IsNullOrWhiteSpace(group)) chosen.Add(group.Trim().ToLowerInvariant());
		}

		var failedOpen = scores.Count == 0;
		if (failedOpen)
		{
			foreach (var group in SafeWidening) chosen.Add(group);
		}

		List<string> groups = availableGroups.Count == 0
			? [.. chosen]
			: [.. chosen.Where(group => availableGroups.Contains(group, StringComparer.OrdinalIgnoreCase))];

		return new CapabilitySelection(
			groups,
			failedOpen,
			failedOpen
				? "No capability keyword matched the task, so a broad safe subset was kept."
				: $"Kept the {ranked.Count} strongest of {scores.Count} matched group(s): "
					+ string.Join(", ", ranked.Select(entry => $"{entry.Group}×{entry.Score}")) + ".");
	}

	/// <summary>
	/// Keeps only the tools in the selected groups. A catalog at or below <paramref name="threshold"/>
	/// is returned untouched, and a filter that would leave nothing is discarded: an empty result means
	/// the classifier is wrong about this catalog, not that the task needs no tools.
	/// </summary>
	public static IReadOnlyList<AITool> Filter(
		IReadOnlyList<AITool> tools,
		CapabilitySelection selection,
		string? serverName = null,
		int threshold = DefaultFilterThreshold)
	{
		if (tools.Count <= threshold || selection.Groups.Count == 0) return tools;

		var kept = tools.Where(tool => selection.Contains(GroupFor(tool.Name, tool.Description, serverName))).ToList();
		return kept.Count == 0 ? tools : kept;
	}

	/// <summary>
	/// The raw tool name, with the harness's <c>mcp__server__</c> namespace removed. The exact prefix
	/// is stripped rather than everything up to the last separator, because a server is free to
	/// publish a raw name that itself contains one.
	/// </summary>
	private static string Bare(string name, string? serverName)
	{
		if (!string.IsNullOrEmpty(serverName))
		{
			var prefix = $"mcp__{serverName}__";
			if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
		}
		var index = name.IndexOf("__", StringComparison.Ordinal);
		return index >= 0 ? name[(index + 2)..] : name;
	}
}

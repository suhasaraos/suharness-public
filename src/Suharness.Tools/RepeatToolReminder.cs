using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suharness.Tools;

/// <summary>
/// How aggressively the repeat detector nudges. The default thresholds escalate reminders after
/// three, five, and eight consecutive identical calls.
/// </summary>
/// <param name="Thresholds">
/// Consecutive-repeat counts that trigger a reminder. The first delivers a short generic nudge and
/// every later one the detailed form. Escalating counts exist because one repeat is often
/// legitimate and five never are.
/// </param>
/// <param name="Include">Tool-name patterns to track, with <c>*</c> wildcards. Empty tracks every tool.</param>
/// <param name="Exclude">
/// Tool-name patterns transparent to the chain. Bookkeeping tools belong here: a model that
/// interleaves <c>todo_write</c> into a loop would otherwise launder it into a chain of length one.
/// </param>
/// <param name="ArgumentsPreviewChars">
/// Cap on the arguments quoted in the detailed reminder. Detection always compares the full
/// canonical string; this bounds only what is quoted back, so a looping <c>write</c> payload cannot
/// ride into the next request unbounded — which is precisely the situation a loop creates.
/// </param>
public sealed record RepeatReminderOptions(
	IReadOnlyList<int>? Thresholds = null,
	IReadOnlyList<string>? Include = null,
	IReadOnlyList<string>? Exclude = null,
	int ArgumentsPreviewChars = 500,
	int MaxIdenticalExecutions = 2,
	int CycleRepetitions = 3,
	int CycleMaxPeriod = 4,
	IReadOnlyList<string>? StateChanging = null,
	int NoProgressWarningThreshold = 6,
	int NoProgressBlockThreshold = 10,
	int DiscoveryWarningThreshold = 12,
	int DiscoveryBlockThreshold = 16)
{
	public static readonly IReadOnlyList<int> DefaultThresholds = [3, 5, 8];

	public static readonly IReadOnlyList<string> DefaultExclude = ["todo_write"];

	public static readonly IReadOnlyList<string> DefaultStateChanging =
		["write", "edit", "str_replace_editor", "mcp_*_write*", "mcp_*_edit*", "mcp_*_create*", "mcp_*_delete*", "mcp_*_update*"];

	public IReadOnlyList<int> EffectiveThresholds => Thresholds ?? DefaultThresholds;

	public IReadOnlyList<string> EffectiveExclude => Exclude ?? DefaultExclude;

	public IReadOnlyList<string> EffectiveStateChanging => StateChanging ?? DefaultStateChanging;
}

public enum RepeatToolAction
{
	Execute,
	BlockDuplicate,
	BlockCycle,
	BlockNoProgress,
}

public sealed record RepeatToolDecision(
	bool Tracked,
	RepeatToolAction Action,
	string ToolName,
	string CanonicalArguments,
	string Fingerprint,
	int RepeatCount,
	long StateVersion,
	long? PreviousStateVersion,
	string? PreviousResult,
	string? PreviousResultHash,
	string? Cycle,
	string? SyntheticResult)
{
	public bool ShouldExecute => Action == RepeatToolAction.Execute;
}

public sealed record RepeatToolCompletion(
	long StateVersion,
	string ResultHash,
	bool StateChanged,
	int SameResultCount,
	string? Reminder = null);

/// <summary>
/// A turn-scoped loop guard. It canonicalizes tool arguments before execution, permits one
/// legitimate repeat, and then substitutes a protocol-valid result containing the previous output
/// when the operation and relevant workspace state are unchanged. It also detects short repeated
/// cycles and advances a state generation after successful mutation tools.
/// </summary>
/// <remarks>
/// The guard is called by the central function invoker, where the provider call id is available.
/// A blocked call is not executed, but its synthetic result is returned normally so the Agent
/// Framework associates it with that exact call id and can continue the same user turn.
/// </remarks>
public sealed partial class RepeatToolReminder
{
	/// <summary>The first-threshold nudge: short, and it names the problem without prescribing a fix.</summary>
	public static string GentleReminder => PromptCatalog.Section(PromptId, "gentle");

	/// <summary>Every message this guard shows the model lives in one editable prompt file.</summary>
	private const string PromptId = "reminder/repeat-tool";

	/// <summary>Stands in for a blocked call's previous output when that output was empty.</summary>
	private static string EmptyResult => PromptCatalog.Section(PromptId, "empty-result");

	/// <summary>The delimiter that separates harness advice from the tool's own output.</summary>
	private const string OpenTag = "<harness-reminder>";

	private const string CloseTag = "</harness-reminder>";

	/// <summary>One session's chain: the last tracked call's identity and how long the run is.</summary>
	private sealed class Chain
	{
		public string Key = string.Empty;
		public int Count;
		public int TurnNumber = -1;
	}

	private sealed class Observation
	{
		public long StateVersion;
		public int RequestCount;
		public int ExecutionCount;
		public int SameResultCount;
		public string? Result;
		public string? ResultHash;
	}

	private sealed class TurnState
	{
		public int TurnNumber = -1;
		public long StateVersion;
		public int DiscoveryCallsSinceStateChange;
		public readonly Dictionary<string, Observation> Observations = new(StringComparer.Ordinal);
		public readonly Dictionary<string, Observation> DiscoveryFamilies = new(StringComparer.Ordinal);
		public readonly List<string> Recent = [];
	}

	private readonly ConcurrentDictionary<Guid, Chain> _chains = new();
	private readonly ConcurrentDictionary<Guid, TurnState> _turns = new();
	private readonly IReadOnlyList<int> _thresholds;
	private readonly HashSet<int> _thresholdSet;
	private readonly Regex[] _include;
	private readonly Regex[] _exclude;
	private readonly Regex[] _stateChanging;
	private readonly int _previewChars;
	private readonly int _maxIdenticalExecutions;
	private readonly int _cycleRepetitions;
	private readonly int _cycleMaxPeriod;
	private readonly int _noProgressWarningThreshold;
	private readonly int _noProgressBlockThreshold;
	private readonly int _discoveryWarningThreshold;
	private readonly int _discoveryBlockThreshold;

	public RepeatToolReminder(RepeatReminderOptions? options = null)
	{
		var settings = options ?? new RepeatReminderOptions();
		// Misconfiguration fails at construction rather than silently falling back: a threshold list
		// that cannot escalate would leave a loop running with no sign that the guard was misread.
		var thresholds = settings.EffectiveThresholds;
		if (thresholds.Count == 0) throw new ArgumentException("thresholds must not be empty.", nameof(options));
		foreach (var value in thresholds)
		{
			if (value < 2)
				throw new ArgumentOutOfRangeException(nameof(options), $"invalid threshold {value} — every threshold must be at least 2.");
		}
		if (thresholds.Distinct().Count() != thresholds.Count)
			throw new ArgumentException("thresholds must not contain duplicates.", nameof(options));
		if (settings.ArgumentsPreviewChars < 1)
			throw new ArgumentOutOfRangeException(nameof(options), "argumentsPreviewChars must be at least 1.");
		if (settings.MaxIdenticalExecutions < 1)
			throw new ArgumentOutOfRangeException(nameof(options), "maxIdenticalExecutions must be at least 1.");
		if (settings.CycleRepetitions < 2)
			throw new ArgumentOutOfRangeException(nameof(options), "cycleRepetitions must be at least 2.");
		if (settings.CycleMaxPeriod < 2)
			throw new ArgumentOutOfRangeException(nameof(options), "cycleMaxPeriod must be at least 2.");
		if (settings.NoProgressWarningThreshold < 2)
			throw new ArgumentOutOfRangeException(nameof(options), "noProgressWarningThreshold must be at least 2.");
		if (settings.NoProgressBlockThreshold <= settings.NoProgressWarningThreshold)
			throw new ArgumentOutOfRangeException(nameof(options), "noProgressBlockThreshold must exceed noProgressWarningThreshold.");
		if (settings.DiscoveryWarningThreshold < 2)
			throw new ArgumentOutOfRangeException(nameof(options), "discoveryWarningThreshold must be at least 2.");
		if (settings.DiscoveryBlockThreshold <= settings.DiscoveryWarningThreshold)
			throw new ArgumentOutOfRangeException(nameof(options), "discoveryBlockThreshold must exceed discoveryWarningThreshold.");

		_thresholds = [.. thresholds.Order()];
		_thresholdSet = [.. _thresholds];
		_include = [.. (settings.Include ?? []).Select(Compile)];
		_exclude = [.. settings.EffectiveExclude.Select(Compile)];
		_stateChanging = [.. settings.EffectiveStateChanging.Select(Compile)];
		_previewChars = settings.ArgumentsPreviewChars;
		_maxIdenticalExecutions = settings.MaxIdenticalExecutions;
		_cycleRepetitions = settings.CycleRepetitions;
		_cycleMaxPeriod = settings.CycleMaxPeriod;
		_noProgressWarningThreshold = settings.NoProgressWarningThreshold;
		_noProgressBlockThreshold = settings.NoProgressBlockThreshold;
		_discoveryWarningThreshold = settings.DiscoveryWarningThreshold;
		_discoveryBlockThreshold = settings.DiscoveryBlockThreshold;
	}

	public RepeatToolDecision Before(
		Guid sessionId,
		int turnNumber,
		string toolName,
		IEnumerable<KeyValuePair<string, object?>>? arguments)
	{
		var canonical = Canonicalize(arguments);
		var fingerprint = Fingerprint(toolName, canonical);
		if (!Tracked(toolName))
			return new(false, RepeatToolAction.Execute, toolName, canonical, fingerprint, 1, 0, null, null, null, null, null);

		var state = _turns.GetOrAdd(sessionId, _ => new TurnState());
		lock (state)
		{
			ResetTurn(state, turnNumber);
			var family = DiscoveryFamily(toolName);
			if (family is not null && state.DiscoveryCallsSinceStateChange >= _discoveryBlockThreshold)
			{
				return BlockedDiscoveryBudget(
					toolName,
					canonical,
					fingerprint,
					state.DiscoveryCallsSinceStateChange,
					state.StateVersion);
			}
			if (family is not null
				&& state.DiscoveryFamilies.TryGetValue(family, out var familyObservation)
				&& familyObservation.SameResultCount >= _noProgressBlockThreshold)
			{
				return BlockedNoProgress(
					toolName,
					canonical,
					fingerprint,
					family,
					familyObservation,
					state.StateVersion);
			}
			if (!state.Observations.TryGetValue(fingerprint, out var observation)
				|| observation.StateVersion != state.StateVersion)
			{
				observation = new Observation { StateVersion = state.StateVersion };
				state.Observations[fingerprint] = observation;
			}

			observation.RequestCount++;
			long? previousState = observation.ExecutionCount > 0 ? observation.StateVersion : null;
			if (observation.ExecutionCount >= _maxIdenticalExecutions
				&& observation.SameResultCount >= _maxIdenticalExecutions)
			{
				return Blocked(
					RepeatToolAction.BlockDuplicate,
					toolName,
					canonical,
					fingerprint,
					observation,
					state.StateVersion,
					previousState,
					null);
			}

			if (FindCycle(state.Recent, fingerprint) is { } cycle)
			{
				return Blocked(
					RepeatToolAction.BlockCycle,
					toolName,
					canonical,
					fingerprint,
					observation,
					state.StateVersion,
					previousState,
					cycle);
			}

			return new(
				true,
				RepeatToolAction.Execute,
				toolName,
				canonical,
				fingerprint,
				observation.RequestCount,
				state.StateVersion,
				previousState,
				observation.Result,
				observation.ResultHash,
				null,
				null);
		}
	}

	public RepeatToolCompletion Complete(Guid sessionId, int turnNumber, RepeatToolDecision decision, string result)
	{
		var resultHash = Hash(result);
		if (!decision.Tracked) return new(decision.StateVersion, resultHash, false, 1);

		var state = _turns.GetOrAdd(sessionId, _ => new TurnState());
		lock (state)
		{
			ResetTurn(state, turnNumber);
			if (!state.Observations.TryGetValue(decision.Fingerprint, out var observation)
				|| observation.StateVersion != state.StateVersion)
			{
				observation = new Observation { StateVersion = state.StateVersion, RequestCount = 1 };
				state.Observations[decision.Fingerprint] = observation;
			}

			var changedInformation = observation.ExecutionCount > 0
				&& !string.Equals(observation.ResultHash, resultHash, StringComparison.Ordinal);
			if (changedInformation) state.Recent.Clear();
			observation.SameResultCount = changedInformation ? 1 : observation.SameResultCount + 1;
			observation.ExecutionCount++;
			observation.Result = result;
			observation.ResultHash = resultHash;
			state.Recent.Add(decision.Fingerprint);
			var windowLimit = _cycleRepetitions * _cycleMaxPeriod;
			if (state.Recent.Count > windowLimit) state.Recent.RemoveRange(0, state.Recent.Count - windowLimit);

			var stateChanged = IsStateChanging(decision.ToolName) && Successful(result);
			if (stateChanged)
			{
				state.StateVersion++;
				state.DiscoveryCallsSinceStateChange = 0;
				state.Recent.Clear();
				state.DiscoveryFamilies.Clear();
			}

			string? reminder = null;
			if (!stateChanged && DiscoveryFamily(decision.ToolName) is { } family)
			{
				state.DiscoveryCallsSinceStateChange++;
				if (!state.DiscoveryFamilies.TryGetValue(family, out var familyObservation))
				{
					familyObservation = new Observation { StateVersion = state.StateVersion };
					state.DiscoveryFamilies[family] = familyObservation;
				}
				var repeatedResult = string.Equals(familyObservation.ResultHash, resultHash, StringComparison.Ordinal);
				familyObservation.SameResultCount = repeatedResult ? familyObservation.SameResultCount + 1 : 1;
				familyObservation.ExecutionCount++;
				familyObservation.Result = result;
				familyObservation.ResultHash = resultHash;
				if (familyObservation.SameResultCount == _noProgressWarningThreshold)
				{
					reminder = PromptCatalog.RenderSection(
						PromptId,
						"no-progress-warning",
						PromptNewline.Lf,
						("count", familyObservation.SameResultCount.ToString(CultureInfo.InvariantCulture)),
						("family", family));
				}
				if (state.DiscoveryCallsSinceStateChange == _discoveryWarningThreshold)
				{
					reminder = PromptCatalog.RenderSection(
						PromptId,
						"discovery-budget-warning",
						PromptNewline.Lf,
						("count", state.DiscoveryCallsSinceStateChange.ToString(CultureInfo.InvariantCulture)));
				}
			}
			return new(state.StateVersion, resultHash, stateChanged, observation.SameResultCount, reminder);
		}
	}

	/// <summary>
	/// Advance a session's chain for one completed call and return the reminder to deliver, or null.
	/// Counting happens after the call rather than before it because a refused call counts too: a
	/// model hammering a call the harness keeps refusing is exactly the loop worth breaking.
	/// </summary>
	public string? Observe(Guid sessionId, int turnNumber, string toolName, IEnumerable<KeyValuePair<string, object?>>? arguments)
	{
		// An untracked call is transparent to the chain: it neither counts nor resets, which is what
		// makes exclusion useful at all.
		if (!Tracked(toolName)) return null;

		var canonical = Canonicalize(arguments);
		var key = $"{toolName}\0{canonical}";
		var chain = _chains.GetOrAdd(sessionId, _ => new Chain());
		int count;
		lock (chain)
		{
			// A new turn is a new instruction — a human prompt or a fresh goal round — so repetition
			// across it is a new attempt at a changed problem, not a loop.
			if (chain.TurnNumber != turnNumber)
			{
				chain.TurnNumber = turnNumber;
				chain.Key = string.Empty;
				chain.Count = 0;
			}
			count = chain.Key == key ? chain.Count + 1 : 1;
			chain.Key = key;
			chain.Count = count;
		}

		if (!_thresholdSet.Contains(count)) return null;
		return count == _thresholds[0] ? GentleReminder : DetailedReminder(toolName, count, Preview(canonical));
	}

	/// <summary>Forget a session's chain, so a deleted session leaves nothing behind.</summary>
	public void Forget(Guid sessionId)
	{
		_chains.TryRemove(sessionId, out _);
		_turns.TryRemove(sessionId, out _);
	}

	/// <summary>Append a reminder to a tool result in a form the model reads as harness advice.</summary>
	public static string Attach(string result, string reminder) =>
		result.Length == 0
			? $"{OpenTag}\n{reminder}\n{CloseTag}"
			: $"{result}\n\n{OpenTag}\n{reminder}\n{CloseTag}";

	/// <summary>The later-threshold nudge: states the evidence, then rules out the call outright.</summary>
	private static string DetailedReminder(string toolName, int count, string canonicalArguments) =>
		PromptCatalog.RenderSection(
			PromptId,
			"detailed",
			PromptNewline.Lf,
			("toolName", toolName),
			("count", count.ToString(CultureInfo.InvariantCulture)),
			("arguments", canonicalArguments));

	private bool Tracked(string toolName)
	{
		if (_include.Length > 0 && !_include.Any(pattern => pattern.IsMatch(toolName))) return false;
		return !_exclude.Any(pattern => pattern.IsMatch(toolName));
	}

	public static string Fingerprint(string toolName, string canonicalArguments) =>
		Hash($"{toolName}\n{canonicalArguments}");

	public static string Hash(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

	private RepeatToolDecision Blocked(
		RepeatToolAction action,
		string toolName,
		string canonical,
		string fingerprint,
		Observation observation,
		long stateVersion,
		long? previousStateVersion,
		string? cycle)
	{
		var previous = observation.Result ?? string.Empty;
		var label = PromptCatalog.Section(
			PromptId,
			action == RepeatToolAction.BlockCycle ? "blocked-repeat-label-cycle" : "blocked-repeat-label-duplicate");
		var cycleText = cycle is null ? string.Empty : $"\nCycle: {cycle}";
		var shownResult = previous.Length == 0 ? EmptyResult : previous;
		var synthetic = PromptCatalog.RenderSection(
			PromptId,
			"blocked-repeat",
			PromptNewline.Lf,
			("label", label),
			("toolName", toolName),
			("arguments", Preview(canonical)),
			("executions", observation.ExecutionCount.ToString(CultureInfo.InvariantCulture)),
			("resultHash", observation.ResultHash ?? "none"),
			("cycle", cycleText),
			("previousResult", shownResult));
		return new(
			true,
			action,
			toolName,
			canonical,
			fingerprint,
			observation.RequestCount,
			stateVersion,
			previousStateVersion,
			observation.Result,
			observation.ResultHash,
			cycle,
			synthetic);
	}

	private RepeatToolDecision BlockedNoProgress(
		string toolName,
		string canonical,
		string fingerprint,
		string family,
		Observation observation,
		long stateVersion)
	{
		var shownResult = string.IsNullOrEmpty(observation.Result) ? EmptyResult : observation.Result;
		var synthetic = PromptCatalog.RenderSection(
			PromptId,
			"blocked-no-progress",
			PromptNewline.Lf,
			("family", family),
			("toolName", toolName),
			("arguments", Preview(canonical)),
			("count", observation.SameResultCount.ToString(CultureInfo.InvariantCulture)),
			("previousResult", shownResult));
		return new(
			true,
			RepeatToolAction.BlockNoProgress,
			toolName,
			canonical,
			fingerprint,
			observation.SameResultCount,
			stateVersion,
			stateVersion,
			observation.Result,
			observation.ResultHash,
			null,
			synthetic);
	}

	private static RepeatToolDecision BlockedDiscoveryBudget(
		string toolName,
		string canonical,
		string fingerprint,
		int discoveryCalls,
		long stateVersion)
	{
		var synthetic = PromptCatalog.RenderSection(
			PromptId,
			"blocked-discovery-budget",
			PromptNewline.Lf,
			("toolName", toolName),
			("arguments", canonical),
			("count", discoveryCalls.ToString(CultureInfo.InvariantCulture)));
		return new(
			true,
			RepeatToolAction.BlockNoProgress,
			toolName,
			canonical,
			fingerprint,
			discoveryCalls,
			stateVersion,
			stateVersion,
			null,
			null,
			null,
			synthetic);
	}

	private string? FindCycle(IReadOnlyList<string> recent, string next)
	{
		var sequence = recent.Append(next).ToArray();
		for (var period = 2; period <= _cycleMaxPeriod; period++)
		{
			var required = period * _cycleRepetitions;
			if (sequence.Length < required) continue;
			var start = sequence.Length - required;
			var repeated = true;
			for (var index = start + period; index < sequence.Length; index++)
			{
				if (sequence[index] == sequence[start + (index - start) % period]) continue;
				repeated = false;
				break;
			}
			if (repeated) return string.Join(" -> ", sequence.Skip(sequence.Length - period).Select(value => value[..8]));
		}
		return null;
	}

	private bool IsStateChanging(string toolName) => _stateChanging.Any(pattern => pattern.IsMatch(toolName));

	private static string? DiscoveryFamily(string toolName) => toolName.ToLowerInvariant() switch
	{
		"grep" => "text search",
		"glob" => "path search",
		"symbol" => "symbol search",
		_ => null,
	};

	private static bool Successful(string result) =>
		!result.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("[exit code:", StringComparison.OrdinalIgnoreCase)
		&& !result.Contains("[timed out", StringComparison.OrdinalIgnoreCase);

	private static void ResetTurn(TurnState state, int turnNumber)
	{
		if (state.TurnNumber == turnNumber) return;
		state.TurnNumber = turnNumber;
		state.StateVersion = 0;
		state.DiscoveryCallsSinceStateChange = 0;
		state.Observations.Clear();
		state.DiscoveryFamilies.Clear();
		state.Recent.Clear();
	}

	/// <summary>
	/// Compile one <c>*</c>-wildcard pattern to an anchored expression. Every other metacharacter is
	/// matched literally, so a pattern is a name predicate rather than a regular expression the
	/// configuration did not ask for.
	/// </summary>
	private static Regex Compile(string pattern) =>
		new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>
	/// The canonical string form of a call's arguments: a deep key sort, then a stable serialization.
	/// Two calls whose argument objects differ only in property order are the same call.
	/// </summary>
	public static string Canonicalize(IEnumerable<KeyValuePair<string, object?>>? arguments)
	{
		if (arguments is null) return "{}";
		var builder = new StringBuilder();
		WriteObject(builder, arguments);
		return builder.ToString();
	}

	private static void WriteObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> pairs)
	{
		builder.Append('{');
		var first = true;
		foreach (var pair in pairs.OrderBy(item => item.Key, StringComparer.Ordinal))
		{
			if (!first) builder.Append(',');
			first = false;
			builder.Append(JsonSerializer.Serialize(pair.Key)).Append(':');
			WriteValue(builder, pair.Value);
		}
		builder.Append('}');
	}

	private static void WriteValue(StringBuilder builder, object? value)
	{
		switch (value)
		{
			case null:
				builder.Append("null");
				return;
			case JsonElement element:
				WriteElement(builder, element);
				return;
			case string text:
				builder.Append(JsonSerializer.Serialize(text));
				return;
			case IEnumerable<KeyValuePair<string, object?>> nested:
				WriteObject(builder, nested);
				return;
			case System.Collections.IEnumerable sequence and not string:
				builder.Append('[');
				var first = true;
				foreach (var item in sequence)
				{
					if (!first) builder.Append(',');
					first = false;
					WriteValue(builder, item);
				}
				builder.Append(']');
				return;
			default:
				// A value the argument binder produced from JSON; round-tripping it through the
				// serializer is what keeps numbers and booleans in one canonical shape.
				try { WriteElement(builder, JsonSerializer.SerializeToElement(value)); }
				catch (NotSupportedException) { builder.Append(JsonSerializer.Serialize(value.ToString())); }
				return;
		}
	}

	private static void WriteElement(StringBuilder builder, JsonElement element)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				builder.Append('{');
				var firstProperty = true;
				foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
				{
					if (!firstProperty) builder.Append(',');
					firstProperty = false;
					builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
					WriteElement(builder, property.Value);
				}
				builder.Append('}');
				return;
			case JsonValueKind.Array:
				builder.Append('[');
				var firstItem = true;
				foreach (var item in element.EnumerateArray())
				{
					if (!firstItem) builder.Append(',');
					firstItem = false;
					WriteElement(builder, item);
				}
				builder.Append(']');
				return;
			default:
				builder.Append(element.GetRawText());
				return;
		}
	}

	/// <summary>
	/// Head-truncate the canonical arguments for quoting, marking how much was left out. This bounds
	/// only the model-visible text; the chain key always compares the full canonical string.
	/// </summary>
	private string Preview(string canonical) =>
		canonical.Length <= _previewChars
			? canonical
			: $"{canonical[.._previewChars]}… (+{canonical.Length - _previewChars} more chars)";
}

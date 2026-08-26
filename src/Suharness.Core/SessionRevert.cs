using Suharness.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suharness.Core;

/// <summary>
/// The append-only bookkeeping behind undo. A revert never deletes anything: it appends an event
/// naming the span of transcript it hides, and a redo appends an event cancelling that span again.
/// </summary>
/// <remarks>
/// Deleting the reverted messages, which is the obvious implementation, would cost the log the one
/// property the rest of the harness is built on — that the transcript is a complete record of what
/// happened. Hiding a named span instead keeps undo and redo while leaving the forensic record whole,
/// and it is what lets a new message sent after a revert land outside every hidden span rather than
/// disappearing into the one that is still open.
/// </remarks>
public static class SessionRevert
{
	public const string SnapshotType = "snapshot";
	public const string RevertType = "revert";
	public const string RedoType = "redo";

	/// <summary>A snapshot taken before a turn's first model call, the state an undo returns to.</summary>
	public const string TurnStart = "turn-start";

	/// <summary>A snapshot taken once the turn has finished or been interrupted.</summary>
	public const string TurnEnd = "turn-end";

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

	/// <summary>Read a payload, or null when the event is not one of ours or its JSON is unreadable.</summary>
	public static T? Read<T>(SessionEvent item, string type) where T : class
	{
		if (item.Type != type || item.Content.Length == 0) return null;
		try
		{
			return JsonSerializer.Deserialize<T>(item.Content, Json);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Every user message that can be rolled back to, mapped to the snapshot taken just before it.
	/// A turn whose capture failed or was deadlined out simply does not appear.
	/// </summary>
	public static IReadOnlyDictionary<Guid, string> RevertPoints(IReadOnlyList<SessionEvent> events)
	{
		var points = new Dictionary<Guid, string>();
		foreach (var item in events)
		{
			if (Read<SnapshotRecord>(item, SnapshotType) is not { Kind: TurnStart } record) continue;
			if (record.BoundaryEventId is not { } boundary || record.Hash.Length == 0) continue;
			points[boundary] = record.Hash;
		}
		return points;
	}

	/// <summary>The reverts still in force, oldest first. A redo cancels exactly one of them.</summary>
	public static IReadOnlyList<(SessionEvent Event, RevertRecord Record)> Active(IReadOnlyList<SessionEvent> events)
	{
		var cancelled = new HashSet<Guid>();
		foreach (var item in events)
			if (Read<RedoRecord>(item, RedoType) is { } redo) cancelled.Add(redo.RevertEventId);

		var active = new List<(SessionEvent, RevertRecord)>();
		foreach (var item in events)
		{
			if (Read<RevertRecord>(item, RevertType) is not { } record) continue;
			if (cancelled.Contains(item.Id)) continue;
			active.Add((item, record));
		}
		return active;
	}

	/// <summary>
	/// The events an active revert hides from replay. Resolved by id rather than by position, so
	/// overlapping spans simply union and a later append is never swept into an earlier one.
	/// </summary>
	public static IReadOnlySet<Guid> Hidden(IReadOnlyList<SessionEvent> events)
	{
		var active = Active(events);
		if (active.Count == 0) return System.Collections.Immutable.ImmutableHashSet<Guid>.Empty;

		var order = new Dictionary<Guid, int>(events.Count);
		for (var index = 0; index < events.Count; index++) order[events[index].Id] = index;

		var hidden = new HashSet<Guid>();
		foreach (var (_, record) in active)
		{
			// A span whose ends cannot both be found is left showing. Replaying a little more than
			// necessary is wasteful; hiding a span of unknown extent would be wrong.
			if (!order.TryGetValue(record.FromEventId, out var from)) continue;
			if (!order.TryGetValue(record.ToEventId, out var to)) continue;
			for (var index = from; index <= to && index < events.Count; index++) hidden.Add(events[index].Id);
		}
		return hidden;
	}

	/// <summary>The transcript as the model and the reader should see it, with hidden spans removed.</summary>
	public static IReadOnlyList<SessionEvent> Visible(IReadOnlyList<SessionEvent> events)
	{
		var hidden = Hidden(events);
		return hidden.Count == 0 ? events : [.. events.Where(item => !hidden.Contains(item.Id))];
	}

	/// <summary>A one-line summary of what a restore did, for the transcript.</summary>
	public static string Describe(IReadOnlyList<SnapshotChange> changes)
	{
		if (changes.Count == 0) return "no files differed";
		var restored = changes.Count(item => item.Kind == SnapshotChangeKind.Modified);
		var removed = changes.Count(item => item.Kind == SnapshotChangeKind.Added);
		var recreated = changes.Count(item => item.Kind == SnapshotChangeKind.Deleted);
		List<string> parts = [];
		if (restored > 0) parts.Add($"{restored} restored");
		if (removed > 0) parts.Add($"{removed} removed");
		if (recreated > 0) parts.Add($"{recreated} recreated");
		return string.Join(", ", parts);
	}
}

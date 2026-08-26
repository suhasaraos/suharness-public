namespace Suharness.Tools;

/// <summary>One task in the agent's working list.</summary>
public sealed record TodoItem(string Content, string Status);

/// <summary>
/// Whole-list replacement for todo_write. There are no partial updates:
/// each call replaces the list, so the latest call in the transcript is the current state.
/// </summary>
public static class TodoTool
{
	public const string Description =
		"Record and update a structured task list for the current work. Send the ENTIRE list every call — "
		+ "it REPLACES the previous list (there are no partial updates, no per-item edits). "
		+ "Use it to plan multi-step work and show progress: add one todo per concrete step before you start. "
		+ "Keep AT MOST ONE todo in_progress at a time; while work remains, exactly one task should be in_progress. "
		+ "Mark a todo completed the moment it is done (do not batch completions), and leave no in_progress item "
		+ "only once all work is complete. Skip the list for trivial single-step tasks. "
		+ "Statuses: pending (not started), in_progress (being worked on now), completed (finished).";

	private static readonly string[] Statuses = ["pending", "in_progress", "completed"];

	/// <summary>Validate a model-supplied list and render the confirmation the model reads back.</summary>
	public static string Write(IReadOnlyList<TodoItem>? todos)
	{
		if (todos is null || todos.Count == 0)
			throw new ToolInputException("todo_write requires at least one todo.");

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var active = 0;
		foreach (var todo in todos)
		{
			var content = todo.Content?.Trim() ?? string.Empty;
			if (content.Length == 0) throw new ToolInputException("invalid todo: content must be a non-empty string");
			if (!seen.Add(content)) throw new ToolInputException($"invalid todos: duplicate content \"{content}\"");
			if (!Statuses.Contains(todo.Status))
				throw new ToolInputException($"invalid todo status \"{todo.Status}\": expected pending, in_progress, or completed");
			if (todo.Status == "in_progress") active++;
		}
		if (active > 1) throw new ToolInputException($"invalid todos: at most one task may be in_progress (got {active})");

		var pending = todos.Count(todo => todo.Status == "pending");
		var completed = todos.Count(todo => todo.Status == "completed");
		return $"Updated todo list: {pending} pending, {active} in progress, {completed} completed.";
	}
}

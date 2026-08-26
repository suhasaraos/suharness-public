using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace Suharness.Tools;

/// <summary>A saved spill artifact: where the full text went, how big it was, and how to get it back.</summary>
public sealed record SpillRef(string Locator, int Bytes, string RetrievalHint);

/// <summary>
/// Storage for oversized tool output. The locator is an opaque model-facing handle that the model
/// retrieves through the ordinary file
/// tools, so a spilled result stays reachable without staying in the context window.
/// </summary>
public interface ISpillStore
{
	/// <summary>The directory artifacts are written under, which the read and grep tools may reach.</summary>
	string Root { get; }

	/// <summary>Persist <paramref name="content"/>, or return null when it could not be saved.</summary>
	SpillRef? SaveText(Guid sessionId, string toolName, string label, string content);
}

/// <summary>Writes spill artifacts to a harness-owned directory, one folder per session.</summary>
public sealed class FileSpillStore(string root) : ISpillStore
{
	public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

	public SpillRef? SaveText(Guid sessionId, string toolName, string label, string content)
	{
		try
		{
			var directory = Path.Combine(Root, sessionId.ToString("N"));
			Directory.CreateDirectory(directory);
			var path = Path.Combine(directory, $"{Sanitize(toolName)}-{Sanitize(label)}-{Guid.NewGuid().ToString("N")[..8]}.txt");
			File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return new SpillRef(path, Encoding.UTF8.GetByteCount(content), SpillPolicy.RetrievalHint);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			// Best effort: a storage failure must never turn a successful tool call into a failure.
			return null;
		}
	}

	/// <summary>Reduce a caller-supplied name to one safe path segment. It is a hint, never a path.</summary>
	private static string Sanitize(string name)
	{
		var cleaned = new string([.. name.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')]);
		return cleaned.Length == 0 ? "result" : cleaned[..Math.Min(cleaned.Length, 40)];
	}
}

/// <summary>
/// Keeps oversized plain-text tool results out of the model's context. A result whose UTF-8 size
/// exceeds the cap is saved whole to a
/// spill artifact and replaced, for the model, with a bounded head/tail preview plus the locator
/// and its retrieval guidance. The transcript still records what the model was given, and the full
/// text remains reachable through the ordinary read and grep tools.
/// </summary>
public static class SpillPolicy
{
	/// <summary>
	/// The default model-facing cap on one plain-text tool result, in UTF-8 bytes. A provider whose
	/// model has a smaller usable context can lower it.
	/// </summary>
	public const int DefaultMaxInlineBytes = 50_000;

	/// <summary>Stable retrieval guidance included with each spilled result.</summary>
	public const string RetrievalHint = "Use read with offset/limit, or grep this path to search within it.";

	/// <summary>
	/// Tools the model-facing arm never spills. <c>read</c> is excluded because
	/// spilling a read would answer a read with a path to read, which is a loop. Its own byte and
	/// line caps bound it instead.
	/// </summary>
	private static readonly string[] Exempt = ["read", "todo_write", "ask_user_question", "exit_plan_mode"];

	public static bool Applies(string toolName) => !Exempt.Contains(toolName, StringComparer.Ordinal);

	/// <summary>
	/// The text the model should see for one result. Returns <paramref name="text"/> unchanged when
	/// it fits, when the tool is exempt, or when the artifact could not be saved.
	/// </summary>
	public static string Apply(string text, string toolName, Guid sessionId, ISpillStore? store, int maxInlineBytes)
	{
		if (store is null || maxInlineBytes <= 0 || !Applies(toolName)) return text;
		var total = Encoding.UTF8.GetByteCount(text);
		if (total <= maxInlineBytes) return text;

		var reference = store.SaveText(sessionId, toolName, "result", text);
		if (reference is null) return text;

		// The notice has to fit inside the same budget it advertises, so its own size is reserved
		// before the preview is measured. A cap too small to hold even the notice cannot be honoured
		// by replacing anything, so the original is kept rather than returning a useless stub.
		var sample = Notice(total, reference);
		var budget = maxInlineBytes - Encoding.UTF8.GetByteCount(sample) - 2;
		if (budget <= 0) return text;

		var preview = HeadAndTail(text, budget);
		var omitted = total - Encoding.UTF8.GetByteCount(preview);
		return $"{preview}\n\n{Notice(omitted, reference)}";
	}

	private static string Notice(int omittedBytes, SpillRef reference) =>
		$"(Omitted {omittedBytes} bytes. Full formatted result stored at: {reference.Locator}. {reference.RetrievalHint})";

	/// <summary>
	/// Keep the opening and the ending of the text within a byte budget. Both ends matter: the head
	/// carries a result's header or first matches, the tail carries the summary or exit status.
	/// </summary>
	private static string HeadAndTail(string text, int budget)
	{
		const string Separator = "\n[...]\n";
		var separatorBytes = Encoding.UTF8.GetByteCount(Separator);
		if (budget <= separatorBytes) return TakeBytes(text, budget, fromEnd: false);

		var half = (budget - separatorBytes) / 2;
		var head = TakeBytes(text, half, fromEnd: false);
		var tail = TakeBytes(text, budget - separatorBytes - Encoding.UTF8.GetByteCount(head), fromEnd: true);
		return head + Separator + tail;
	}

	/// <summary>Take whole characters from one end of the text without exceeding a byte budget.</summary>
	private static string TakeBytes(string text, int budget, bool fromEnd)
	{
		if (budget <= 0) return string.Empty;
		var bytes = 0;
		var taken = 0;
		while (taken < text.Length)
		{
			var index = fromEnd ? text.Length - 1 - taken : taken;
			// A surrogate pair must be taken whole, so the low half is counted with its high half.
			var size = char.IsSurrogate(text[index]) ? 2 : Encoding.UTF8.GetByteCount(text.AsSpan(index, 1));
			if (bytes + size > budget) break;
			bytes += size;
			taken++;
		}
		return fromEnd ? text[^taken..] : text[..taken];
	}
}

/// <summary>
/// Wraps one tool so its result passes through <see cref="SpillPolicy"/> after the tool has produced
/// its final text and
/// before the model sees it — so no tool needs to know that spilling exists.
/// </summary>
internal sealed class SpillingFunction(AIFunction inner, Guid sessionId, ISpillStore store, int maxInlineBytes)
	: DelegatingAIFunction(inner)
{
	protected override async ValueTask<object?> InvokeCoreAsync(
		AIFunctionArguments arguments,
		CancellationToken cancellationToken)
	{
		var result = await base.InvokeCoreAsync(arguments, cancellationToken);
		// A factory-created function hands back its return value already serialized, so a tool that
		// returns string arrives here as a JSON string rather than as one. Anything else is a
		// structured result this text policy has no business rewriting.
		var text = result switch
		{
			string plain => plain,
			JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
			_ => null,
		};
		return text is null ? result : SpillPolicy.Apply(text, Name, sessionId, store, maxInlineBytes);
	}
}

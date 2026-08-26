using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using Suharness.Tools;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for the deterministic repeated-tool loop guard. It allows one legitimate repeat, then
/// substitutes a protocol-valid result carrying the previous output instead of executing an
/// unchanged operation forever.
/// </summary>
public sealed class RepeatToolReminderTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-repeat-").FullName;
	private readonly Guid _session = Guid.NewGuid();

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private static KeyValuePair<string, object?>[] Args(params (string Key, object? Value)[] pairs) =>
		[.. pairs.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value))];

	/// <summary>
	/// The first threshold nudges, later ones name the tool and quote the arguments. Escalating this
	/// way is what keeps one legitimate repeat from reading like an accusation.
	/// </summary>
	[Fact]
	public void RemindersEscalateAtTheConfiguredThresholds()
	{
		var reminder = new RepeatToolReminder();
		var arguments = Args(("pattern", "TODO"));

		Assert.Null(reminder.Observe(_session, 1, "grep", arguments));
		Assert.Null(reminder.Observe(_session, 1, "grep", arguments));
		Assert.Equal(RepeatToolReminder.GentleReminder, reminder.Observe(_session, 1, "grep", arguments));
		Assert.Null(reminder.Observe(_session, 1, "grep", arguments));

		var detailed = reminder.Observe(_session, 1, "grep", arguments);
		Assert.NotNull(detailed);
		Assert.Contains("- tool: grep", detailed);
		Assert.Contains("- consecutive_calls: 5", detailed);
		Assert.Contains("TODO", detailed);
	}

	/// <summary>A different call is a different approach, so the run starts over.</summary>
	[Fact]
	public void ADifferentCallResetsTheRun()
	{
		var reminder = new RepeatToolReminder();
		reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO")));
		reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO")));
		reminder.Observe(_session, 1, "grep", Args(("pattern", "FIXME")));
		Assert.Null(reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO"))));
	}

	/// <summary>
	/// An excluded tool neither counts nor resets. Without this, a model that writes a todo between
	/// every repeat launders an obvious loop into a chain of length one.
	/// </summary>
	[Fact]
	public void AnExcludedToolIsTransparentToTheChain()
	{
		var reminder = new RepeatToolReminder();
		var arguments = Args(("pattern", "TODO"));
		reminder.Observe(_session, 1, "grep", arguments);
		Assert.Null(reminder.Observe(_session, 1, "todo_write", Args(("todos", "[]"))));
		reminder.Observe(_session, 1, "grep", arguments);
		Assert.Null(reminder.Observe(_session, 1, "todo_write", Args(("todos", "[]"))));
		Assert.Equal(RepeatToolReminder.GentleReminder, reminder.Observe(_session, 1, "grep", arguments));
	}

	/// <summary>An include list narrows tracking to the named tools; a wildcard covers a family.</summary>
	[Fact]
	public void IncludeAndExcludePatternsSupportWildcards()
	{
		var reminder = new RepeatToolReminder(new RepeatReminderOptions(
			Thresholds: [2],
			Include: ["mcp_*"],
			Exclude: ["mcp_ping"]));

		Assert.Null(reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO"))));
		Assert.Null(reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO"))));

		reminder.Observe(_session, 1, "mcp_search", Args(("q", "x")));
		Assert.NotNull(reminder.Observe(_session, 1, "mcp_search", Args(("q", "x"))));

		reminder.Observe(_session, 1, "mcp_ping", Args(("q", "x")));
		Assert.Null(reminder.Observe(_session, 1, "mcp_ping", Args(("q", "x"))));
	}

	/// <summary>
	/// Two calls whose argument objects differ only in property order are the same call, so
	/// canonicalization is a deep key sort rather than a string comparison of what the model emitted.
	/// </summary>
	[Fact]
	public void ArgumentOrderDoesNotChangeTheCallIdentity()
	{
		var reminder = new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [2]));
		reminder.Observe(_session, 1, "grep", Args(("pattern", "TODO"), ("path", "src")));
		Assert.NotNull(reminder.Observe(_session, 1, "grep", Args(("path", "src"), ("pattern", "TODO"))));

		Assert.Equal(
			RepeatToolReminder.Canonicalize(Args(("b", 1), ("a", 2))),
			RepeatToolReminder.Canonicalize(Args(("a", 2), ("b", 1))));
	}

	/// <summary>
	/// A new turn carries a new instruction, so repetition across it is a fresh attempt at a changed
	/// problem rather than a loop.
	/// </summary>
	[Fact]
	public void ANewTurnResetsTheChain()
	{
		var reminder = new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [2]));
		var arguments = Args(("pattern", "TODO"));
		reminder.Observe(_session, 1, "grep", arguments);
		Assert.Null(reminder.Observe(_session, 2, "grep", arguments));
		Assert.NotNull(reminder.Observe(_session, 2, "grep", arguments));
	}

	/// <summary>One session's repetition never trips another's reminder.</summary>
	[Fact]
	public void ChainsAreKeptPerSession()
	{
		var reminder = new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [2]));
		var arguments = Args(("pattern", "TODO"));
		reminder.Observe(_session, 1, "grep", arguments);
		Assert.Null(reminder.Observe(Guid.NewGuid(), 1, "grep", arguments));
	}

	/// <summary>
	/// The quoted arguments are capped, because a looping write is precisely the case where an
	/// unbounded quote would ride its own payload into the next request.
	/// </summary>
	[Fact]
	public void QuotedArgumentsAreCappedWithoutAffectingDetection()
	{
		// Only a later threshold quotes the arguments — the first is always the short generic nudge.
		var reminder = new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [2, 3], ArgumentsPreviewChars: 20));
		var arguments = Args(("content", new string('x', 400)));
		reminder.Observe(_session, 1, "write", arguments);
		reminder.Observe(_session, 1, "write", arguments);
		var detailed = reminder.Observe(_session, 1, "write", arguments);
		Assert.NotNull(detailed);
		Assert.Contains("more chars)", detailed);
		Assert.DoesNotContain(new string('x', 100), detailed);

		// Detection still compares the full string: a payload differing only past the cap is a
		// different call.
		Assert.Null(reminder.Observe(_session, 1, "write", Args(("content", new string('x', 399) + "y"))));
	}

	/// <summary>A configuration that cannot escalate is refused where it is written.</summary>
	[Fact]
	public void AnImpossibleConfigurationIsRejected()
	{
		Assert.Throws<ArgumentException>(() => new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [])));
		Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [1])));
		Assert.Throws<ArgumentException>(() => new RepeatToolReminder(new RepeatReminderOptions(Thresholds: [3, 3])));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new RepeatToolReminder(new RepeatReminderOptions(ArgumentsPreviewChars: 0)));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new RepeatToolReminder(new RepeatReminderOptions(MaxIdenticalExecutions: 0)));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => new RepeatToolReminder(new RepeatReminderOptions(CycleRepetitions: 1)));
	}

	/// <summary>
	/// End to end: the third identical call is not run. Its synthetic result is associated with the
	/// new provider call id and carries the complete previous result into the next request.
	/// </summary>
	[Fact]
	public async Task TheThirdIdenticalCallIsBlockedWithItsPreviousResultAndCallId()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "hello");
		var call = ToolCall("grep", "{\"pattern\":\"hello\",\"path\":\"note.txt\"}");
		var model = new SequenceModel(call, call, call, Text("done"));
		var (runtime, session) = await StartAsync(model);

		await runtime.SubmitAsync(session.Id, "read the note");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var results = transcript!.Events.Where(item => item.Type == "tool_result").ToList();
		Assert.Equal(3, results.Count);
		Assert.DoesNotContain("Duplicate tool call blocked", results[0].Content);
		Assert.DoesNotContain("Duplicate tool call blocked", results[1].Content);
		Assert.Contains("Duplicate tool call blocked", results[2].Content);
		Assert.Contains("hello", results[2].Content);
		Assert.Equal("call_2", results[2].ToolCallId);

		using var request = JsonDocument.Parse(model.Requests[3]);
		var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
		var assistant = Assert.Single(messages, message => message.TryGetProperty("tool_calls", out var calls)
			&& calls.EnumerateArray().Any(callItem => callItem.GetProperty("id").GetString() == "call_2"));
		Assert.Equal("assistant", assistant.GetProperty("role").GetString());
		var toolResult = Assert.Single(messages, message => message.TryGetProperty("tool_call_id", out var id)
			&& id.GetString() == "call_2");
		Assert.Equal("tool", toolResult.GetProperty("role").GetString());
		Assert.Contains("hello", toolResult.GetProperty("content").GetString());
	}

	[Fact]
	public async Task DifferentGrepArgumentsAreAllowedAndEachResultReachesTheNextRequest()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "alpha\nbeta\ngamma\n");
		var model = new SequenceModel(
			ToolCall("grep", "{\"pattern\":\"alpha\",\"path\":\"note.txt\"}"),
			ToolCall("grep", "{\"path\":\"note.txt\",\"pattern\":\"beta\"}"),
			ToolCall("grep", "{\"pattern\":\"gamma\",\"path\":\"note.txt\"}"),
			Text("done"));
		var (runtime, session) = await StartAsync(model);

		await runtime.SubmitAsync(session.Id, "search the note");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.Equal(3, transcript!.Events.Count(item => item.Type == "tool_result"));
		Assert.DoesNotContain(transcript.Events, item => item.Content.Contains("tool call blocked", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("alpha", model.Requests[1]);
		Assert.Contains("beta", model.Requests[2]);
		Assert.Contains("gamma", model.Requests[3]);
	}

	[Fact]
	public async Task AFileMutationInvalidatesEarlierReadResults()
	{
		File.WriteAllText(Path.Combine(Workspace, "note.txt"), "before\n");
		var read = ToolCall("read", "{\"file_path\":\"note.txt\"}");
		var model = new SequenceModel(
			read,
			read,
			ToolCall("write", "{\"file_path\":\"note.txt\",\"content\":\"after\\n\"}"),
			read,
			Text("done"));
		var (runtime, session) = await StartAsync(model);

		await runtime.SubmitAsync(session.Id, "update and reread the note");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var results = transcript!.Events.Where(item => item.Type == "tool_result").ToList();
		Assert.Equal(4, results.Count);
		Assert.Contains("after", results[^1].Content);
		Assert.DoesNotContain("Duplicate tool call blocked", results[^1].Content);
	}

	[Fact]
	public void ARepeatedTwoCallCycleIsBlocked()
	{
		var guard = new RepeatToolReminder(new RepeatReminderOptions(
			MaxIdenticalExecutions: 10,
			CycleRepetitions: 3));
		var a = Args(("pattern", "alpha"));
		var b = Args(("pattern", "beta"));

		foreach (var arguments in new[] { a, b, a, b, a })
		{
			var decision = guard.Before(_session, 1, "grep", arguments);
			Assert.True(decision.ShouldExecute);
			guard.Complete(_session, 1, decision, arguments == a ? "alpha result" : "beta result");
		}

		var blocked = guard.Before(_session, 1, "grep", b);
		Assert.Equal(RepeatToolAction.BlockCycle, blocked.Action);
		Assert.Contains("Runaway tool cycle blocked", blocked.SyntheticResult);
		Assert.Contains("beta result", blocked.SyntheticResult);
	}

	[Fact]
	public void RepeatedBuildAfterAnEditIsAllowed()
	{
		var guard = new RepeatToolReminder();
		var build = Args(("command", "dotnet test"));
		var edit = Args(("file_path", "A.cs"), ("old_string", "1"), ("new_string", "2"));

		Execute(guard, "bash", build, "Build succeeded");
		Execute(guard, "bash", build, "Build succeeded");
		Execute(guard, "edit", edit, "Updated A.cs");
		var afterEdit = guard.Before(_session, 1, "bash", build);

		Assert.True(afterEdit.ShouldExecute);
		Assert.Equal(1, afterEdit.RepeatCount);
		Assert.True(afterEdit.StateVersion > 0);
	}

	[Fact]
	public void EmptyToolOutputStillProducesActionableDuplicateFeedback()
	{
		var guard = new RepeatToolReminder();
		var arguments = Args(("pattern", "missing"));
		Execute(guard, "grep", arguments, string.Empty);
		Execute(guard, "grep", arguments, string.Empty);

		var blocked = guard.Before(_session, 1, "grep", arguments);

		Assert.Equal(RepeatToolAction.BlockDuplicate, blocked.Action);
		Assert.Contains("(empty tool result)", blocked.SyntheticResult);
	}

	[Fact]
	public void AChangedResultBreaksAnIneffectiveRepeatRun()
	{
		var guard = new RepeatToolReminder();
		var arguments = Args(("pattern", "status"));
		Execute(guard, "grep", arguments, "old");
		Execute(guard, "grep", arguments, "new");

		Assert.True(guard.Before(_session, 1, "grep", arguments).ShouldExecute);
	}

	[Fact]
	public void VariedSearchesWithTheSameResultAreWarnedAndThenBlocked()
	{
		var guard = new RepeatToolReminder(new RepeatReminderOptions(
			MaxIdenticalExecutions: 20,
			CycleRepetitions: 20,
			NoProgressWarningThreshold: 3,
			NoProgressBlockThreshold: 5));

		string? warning = null;
		for (var index = 0; index < 5; index++)
		{
			var decision = guard.Before(_session, 1, "grep", Args(("pattern", $"missing-{index}")));
			Assert.True(decision.ShouldExecute);
			warning = guard.Complete(_session, 1, decision, "No matches found").Reminder ?? warning;
		}

		Assert.Contains("No-progress search warning", warning);
		var blocked = guard.Before(_session, 1, "grep", Args(("pattern", "missing-again")));
		Assert.Equal(RepeatToolAction.BlockNoProgress, blocked.Action);
		Assert.Contains("No-progress search blocked", blocked.SyntheticResult);
	}

	[Fact]
	public void NewSearchInformationAndMutationsResetNoProgress()
	{
		var guard = new RepeatToolReminder(new RepeatReminderOptions(
			MaxIdenticalExecutions: 20,
			CycleRepetitions: 20,
			NoProgressWarningThreshold: 2,
			NoProgressBlockThreshold: 3));

		Execute(guard, "grep", Args(("pattern", "one")), "No matches found");
		Execute(guard, "grep", Args(("pattern", "two")), "No matches found");
		Execute(guard, "grep", Args(("pattern", "three")), "found A.cs:10");
		Assert.True(guard.Before(_session, 1, "grep", Args(("pattern", "four"))).ShouldExecute);

		Execute(guard, "grep", Args(("pattern", "five")), "No matches found");
		Execute(guard, "grep", Args(("pattern", "six")), "No matches found");
		Execute(guard, "edit", Args(("file_path", "A.cs"), ("old_string", "1"), ("new_string", "2")), "Updated A.cs");
		Assert.True(guard.Before(_session, 1, "grep", Args(("pattern", "seven"))).ShouldExecute);
	}

	[Fact]
	public void VariedDiscoveryIsBlockedUntilAWorkspaceMutation()
	{
		var guard = new RepeatToolReminder(new RepeatReminderOptions(
			MaxIdenticalExecutions: 20,
			CycleRepetitions: 20,
			NoProgressWarningThreshold: 20,
			NoProgressBlockThreshold: 21,
			DiscoveryWarningThreshold: 3,
			DiscoveryBlockThreshold: 5));

		string? warning = null;
		for (var index = 0; index < 5; index++)
		{
			var tool = index % 2 == 0 ? "grep" : "glob";
			var decision = guard.Before(_session, 1, tool, Args(("pattern", $"query-{index}")));
			Assert.True(decision.ShouldExecute);
			warning = guard.Complete(_session, 1, decision, $"different-result-{index}").Reminder ?? warning;
		}

		Assert.Contains("Discovery budget warning", warning);
		var blocked = guard.Before(_session, 1, "symbol", Args(("query", "another-result")));
		Assert.Equal(RepeatToolAction.BlockNoProgress, blocked.Action);
		Assert.Contains("Discovery budget exhausted", blocked.SyntheticResult);

		Execute(guard, "edit", Args(("file_path", "A.cs"), ("old_string", "1"), ("new_string", "2")), "Updated A.cs");
		Assert.True(guard.Before(_session, 1, "grep", Args(("pattern", "after-edit"))).ShouldExecute);
	}

	private void Execute(RepeatToolReminder guard, string tool, IEnumerable<KeyValuePair<string, object?>> arguments, string result)
	{
		var decision = guard.Before(_session, 1, tool, arguments);
		Assert.True(decision.ShouldExecute);
		guard.Complete(_session, 1, decision, result);
	}

	// ─── Fixtures ────────────────────────────────────────────────────────

	private string Workspace
	{
		get
		{
			var path = Path.Combine(_root, "workspace");
			Directory.CreateDirectory(path);
			return path;
		}
	}

	private static string Text(string content) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
		+ JsonSerializer.Serialize(content)
		+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private static string ToolCall(string name, string argumentsJson) =>
		"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
		+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":["
		+ "{\"id\":\"__CALL_ID__\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
		+ JsonSerializer.Serialize(argumentsJson)
		+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

	private async Task<(HarnessRuntime Runtime, HarnessSession Session)> StartAsync(HttpMessageHandler model)
	{
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(model)),
			repeatReminder: new RepeatToolReminder());
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted",
				"Scripted",
				"https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")]),
			"test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service);
		var session = await runtime.CreateSessionAsync(Workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return (runtime, session);
	}

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>Replays scripted completions, minting a fresh call id for each so replay stays valid.</summary>
	private sealed class SequenceModel(params string[] responses) : HttpMessageHandler
	{
		private int _index;
		public List<string> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
			var body = responses[Math.Min(_index, responses.Length - 1)]
				.Replace("__CALL_ID__", $"call_{_index}", StringComparison.Ordinal);
			_index++;
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};
		}
	}
}

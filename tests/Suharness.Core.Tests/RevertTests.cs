using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for undo: the append-only bookkeeping that decides which transcript a turn replays, and the
/// end-to-end path from a model that wrote a file to a workspace that no longer contains it.
/// </summary>
/// <remarks>
/// The property these are really guarding is that a revert hides a span rather than deleting one.
/// A harness that deletes reverted messages passes the obvious undo test and then loses the next
/// message the user sends, because the deletion and the new append race over the same tail.
/// </remarks>
public sealed class RevertTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-revert-").FullName;
	private readonly Guid _session = Guid.NewGuid();

	public void Dispose()
	{
		try { ForceDelete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static void ForceDelete(string path)
	{
		if (!Directory.Exists(path)) return;
		foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
		{
			try { File.SetAttributes(file, FileAttributes.Normal); } catch (SystemException) { }
		}
		Directory.Delete(path, recursive: true);
	}

	private SessionEvent Event(string type, string role, string content) =>
		new(Guid.NewGuid(), _session, type, role, content, DateTimeOffset.UtcNow);

	private SessionEvent Snapshot(string hash, Guid boundary) =>
		Event(SessionRevert.SnapshotType, "system", SessionRevert.Serialize(
			new SnapshotRecord(hash, SessionRevert.TurnStart, boundary, 12)));

	private SessionEvent Revert(Guid from, Guid to, string target = "aaaa", string redo = "bbbb") =>
		Event(SessionRevert.RevertType, "system", SessionRevert.Serialize(
			new RevertRecord(from, to, target, redo, [])));

	private SessionEvent Redo(Guid revertEventId) =>
		Event(SessionRevert.RedoType, "system", SessionRevert.Serialize(
			new RedoRecord(revertEventId, "bbbb", [])));

	// ----- span bookkeeping ----------------------------------------------------------------------

	[Fact]
	public void ATranscriptWithNoRevertHidesNothing()
	{
		List<SessionEvent> events = [Event("message", "user", "hi"), Event("message", "assistant", "hello")];

		Assert.Empty(SessionRevert.Hidden(events));
		Assert.Same(events, SessionRevert.Visible(events));
	}

	[Fact]
	public void ARevertHidesItsOwnSpanAndNothingBefore()
	{
		var first = Event("message", "user", "first");
		var firstReply = Event("message", "assistant", "first reply");
		var second = Event("message", "user", "second");
		var secondReply = Event("message", "assistant", "second reply");
		List<SessionEvent> events = [first, firstReply, second, secondReply, Revert(second.Id, secondReply.Id)];

		var visible = SessionRevert.Visible(events).Select(item => item.Content).ToArray();

		Assert.Contains("first", visible);
		Assert.Contains("first reply", visible);
		Assert.DoesNotContain("second", visible);
		Assert.DoesNotContain("second reply", visible);
	}

	[Fact]
	public void ARedoUnhidesTheSpanItsRevertHid()
	{
		var message = Event("message", "user", "second");
		var reply = Event("message", "assistant", "second reply");
		var revert = Revert(message.Id, reply.Id);
		List<SessionEvent> events = [message, reply, revert, Redo(revert.Id)];

		Assert.Empty(SessionRevert.Hidden(events));
		Assert.Empty(SessionRevert.Active(events));
	}

	/// <summary>
	/// The case that makes span-hiding worth the trouble. The next message the user sends lands after
	/// the revert's recorded end, so it is outside the hidden span rather than swallowed by it.
	/// </summary>
	[Fact]
	public void AMessageSentAfterARevertStaysVisible()
	{
		var undone = Event("message", "user", "undone");
		var undoneReply = Event("message", "assistant", "undone reply");
		var revert = Revert(undone.Id, undoneReply.Id);
		var fresh = Event("message", "user", "sent after the undo");
		List<SessionEvent> events = [undone, undoneReply, revert, fresh];

		var visible = SessionRevert.Visible(events).Select(item => item.Content).ToArray();

		Assert.Contains("sent after the undo", visible);
		Assert.DoesNotContain("undone", visible);
	}

	[Fact]
	public void OverlappingRevertsUnionRatherThanFight()
	{
		var one = Event("message", "user", "one");
		var two = Event("message", "user", "two");
		var three = Event("message", "user", "three");
		List<SessionEvent> events = [one, two, three, Revert(two.Id, three.Id), Revert(one.Id, two.Id)];

		Assert.Equal(3, SessionRevert.Hidden(events).Count);
		Assert.DoesNotContain(SessionRevert.Visible(events), item => item.Type == "message");
	}

	/// <summary>
	/// A span whose ends cannot be resolved is left showing. Replaying a little more than necessary
	/// wastes context; hiding a span of unknown extent would silently drop real conversation.
	/// </summary>
	[Fact]
	public void ASpanWithAnUnresolvableEndHidesNothing()
	{
		var message = Event("message", "user", "kept");
		List<SessionEvent> events = [message, Revert(Guid.NewGuid(), Guid.NewGuid())];

		Assert.Empty(SessionRevert.Hidden(events));
	}

	[Fact]
	public void MalformedBookkeepingIsIgnoredRatherThanThrowing()
	{
		var message = Event("message", "user", "kept");
		List<SessionEvent> events = [message, Event(SessionRevert.RevertType, "system", "{ not json")];

		Assert.Empty(SessionRevert.Hidden(events));
		Assert.Empty(SessionRevert.Active(events));
	}

	[Fact]
	public void RevertPointsMapEachTurnToTheSnapshotTakenBeforeIt()
	{
		var first = Event("message", "user", "first");
		var second = Event("message", "user", "second");
		List<SessionEvent> events =
		[
			first,
			Snapshot("a".PadRight(40, 'a'), first.Id),
			// A turn-end snapshot is not a revert point; only the state before a turn is.
			Event(SessionRevert.SnapshotType, "system", SessionRevert.Serialize(
				new SnapshotRecord("c".PadRight(40, 'c'), SessionRevert.TurnEnd, first.Id, 3))),
			second,
			Snapshot("b".PadRight(40, 'b'), second.Id),
		];

		var points = SessionRevert.RevertPoints(events);

		Assert.Equal(2, points.Count);
		Assert.Equal("a".PadRight(40, 'a'), points[first.Id]);
		Assert.Equal("b".PadRight(40, 'b'), points[second.Id]);
	}

	// ----- replay --------------------------------------------------------------------------------

	[Fact]
	public void ReplaySkipsARevertedSpan()
	{
		var kept = Event("message", "user", "keep this");
		var keptReply = Event("message", "assistant", "kept reply");
		var undone = Event("message", "user", "undo this");
		var undoneReply = Event("message", "assistant", "undone reply");
		List<SessionEvent> events = [kept, keptReply, undone, undoneReply, Revert(undone.Id, undoneReply.Id)];

		var history = HarnessRuntime.BuildHistory(events);

		Assert.Equal(2, history.Count);
		Assert.Equal("keep this", history[0].Text);
		Assert.Equal("kept reply", history[1].Text);
	}

	[Fact]
	public void ReplayShowsEverythingAgainAfterARedo()
	{
		var undone = Event("message", "user", "undo this");
		var undoneReply = Event("message", "assistant", "undone reply");
		var revert = Revert(undone.Id, undoneReply.Id);
		List<SessionEvent> events = [undone, undoneReply, revert, Redo(revert.Id)];

		Assert.Equal(2, HarnessRuntime.BuildHistory(events).Count);
	}

	/// <summary>Snapshot bookkeeping is machine state, so it must never reach the model as conversation.</summary>
	[Fact]
	public void SnapshotBookkeepingIsNeverReplayedToTheModel()
	{
		var message = Event("message", "user", "hello");
		List<SessionEvent> events = [message, Snapshot("a".PadRight(40, 'a'), message.Id)];

		var history = HarnessRuntime.BuildHistory(events);

		Assert.Equal("hello", Assert.Single(history).Text);
	}

	// ----- end to end ----------------------------------------------------------------------------

	private sealed record Harness(HarnessRuntime Runtime, HarnessSession Session, string Workspace, ISessionStore Store);

	private async Task<Harness> StartAsync(string? workspace, params string[] responses)
	{
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(new ScriptedModel(responses))));
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted",
				"Scripted",
				"https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")]),
			"test-key");

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var snapshots = new GitSnapshotService(Path.Combine(_root, "snapshots"));
		var runtime = new HarnessRuntime(store, service, snapshots: snapshots);
		var session = await runtime.CreateSessionAsync(workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));
		return new Harness(runtime, session, workspace ?? string.Empty, store);
	}

	private string NewWorkspace([System.Runtime.CompilerServices.CallerMemberName] string name = "w")
	{
		var path = Path.Combine(_root, "ws-" + name);
		Directory.CreateDirectory(path);
		return path;
	}

	private static string WriteCall(string path, string content) =>
		ScriptedModel.ToolCall("write", System.Text.Json.JsonSerializer.Serialize(
			new Dictionary<string, string> { ["file_path"] = path, ["content"] = content }));

	[Fact]
	public async Task ATurnIsSnapshottedBeforeItStartsAndAfterItEnds()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, WriteCall("created.txt", "agent output"), ScriptedModel.Text("done"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "make a file");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var snapshots = events
			.Select(item => SessionRevert.Read<SnapshotRecord>(item, SessionRevert.SnapshotType))
			.OfType<SnapshotRecord>()
			.ToList();

		Assert.Contains(snapshots, record => record.Kind == SessionRevert.TurnStart);
		Assert.Contains(snapshots, record => record.Kind == SessionRevert.TurnEnd);
		// The file the agent wrote means the two snapshots cannot be the same state.
		Assert.NotEqual(
			snapshots.First(record => record.Kind == SessionRevert.TurnStart).Hash,
			snapshots.First(record => record.Kind == SessionRevert.TurnEnd).Hash);
	}

	[Fact]
	public async Task UndoingATurnRemovesTheFileTheAgentWroteAndTheMessageThatAskedForIt()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, WriteCall("created.txt", "agent output"), ScriptedModel.Text("done"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "make a file");
		Assert.True(File.Exists(Path.Combine(workspace, "created.txt")));

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var boundary = events.First(item => item.Type == "message" && item.Role == "user").Id;

		var record = await harness.Runtime.RevertAsync(harness.Session.Id, boundary);

		Assert.False(File.Exists(Path.Combine(workspace, "created.txt")));
		Assert.Equal(SnapshotChangeKind.Added, Assert.Single(record.Changes).Kind);

		var after = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		Assert.Empty(HarnessRuntime.BuildHistory(after));
	}

	/// <summary>The log is the record of what happened, so an undo must not erase any of it.</summary>
	[Fact]
	public async Task TheRevertedTurnIsStillOnTheAppendOnlyLog()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, WriteCall("created.txt", "agent output"), ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "make a file");

		var before = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		await harness.Runtime.RevertAsync(
			harness.Session.Id,
			before.First(item => item.Type == "message" && item.Role == "user").Id);
		var after = (await harness.Store.GetAsync(harness.Session.Id))!.Events;

		// Nothing was removed; the log only grew.
		Assert.True(after.Count > before.Count);
		foreach (var item in before) Assert.Contains(after, candidate => candidate.Id == item.Id);
		Assert.Contains(after, item => item.Type == "tool_call" && item.ToolName == "write");
	}

	[Fact]
	public async Task RedoingRestoresTheFileAndTheConversation()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, WriteCall("created.txt", "agent output"), ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "make a file");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var boundary = events.First(item => item.Type == "message" && item.Role == "user").Id;
		await harness.Runtime.RevertAsync(harness.Session.Id, boundary);

		await harness.Runtime.RedoAsync(harness.Session.Id);

		Assert.Equal("agent output", File.ReadAllText(Path.Combine(workspace, "created.txt")));
		var after = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		Assert.NotEmpty(HarnessRuntime.BuildHistory(after));
	}

	/// <summary>
	/// The end-to-end form of the property the span bookkeeping exists for: after an undo, the next
	/// prompt is a real turn whose transcript the model can see.
	/// </summary>
	[Fact]
	public async Task APromptSentAfterAnUndoIsReplayedAndTheUndoneOneIsNot()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(
			workspace,
			WriteCall("created.txt", "agent output"),
			ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "the undone request");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		await harness.Runtime.RevertAsync(
			harness.Session.Id,
			events.First(item => item.Type == "message" && item.Role == "user").Id);

		await harness.Runtime.SubmitAsync(harness.Session.Id, "the request that replaced it");

		var after = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var replayed = HarnessRuntime.BuildHistory(after).Select(message => message.Text).ToArray();

		Assert.Contains(replayed, text => text.Contains("the request that replaced it"));
		Assert.DoesNotContain(replayed, text => text.Contains("the undone request"));
	}

	[Fact]
	public async Task UndoingTheSameTurnTwiceIsRefused()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "hello");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var boundary = events.First(item => item.Type == "message" && item.Role == "user").Id;
		await harness.Runtime.RevertAsync(harness.Session.Id, boundary);

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => harness.Runtime.RevertAsync(harness.Session.Id, boundary));
		Assert.Contains("already been undone", failure.Message);
	}

	[Fact]
	public async Task AMessageWithNoSnapshotCannotBeUndone()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "hello");

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => harness.Runtime.RevertAsync(harness.Session.Id, Guid.NewGuid()));
		Assert.Contains("no snapshot", failure.Message);
	}

	[Fact]
	public async Task RedoingWithNothingUndoneIsRefused()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "hello");

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => harness.Runtime.RedoAsync(harness.Session.Id));
		Assert.Contains("nothing to redo", failure.Message);
	}

	/// <summary>A chat session has no workspace, so there is nothing to snapshot and nothing to undo.</summary>
	[Fact]
	public async Task ANoWorkspaceSessionIsNeverSnapshotted()
	{
		var harness = await StartAsync(null, ScriptedModel.Text("done"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "hello");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		Assert.DoesNotContain(events, item => item.Type == SessionRevert.SnapshotType);

		var failure = await Assert.ThrowsAsync<InvalidOperationException>(
			() => harness.Runtime.RevertAsync(harness.Session.Id, events[0].Id));
		Assert.Contains("no workspace", failure.Message);
	}

	[Fact]
	public async Task UndoLeavesAnEarlierTurnAlone()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(
			workspace,
			WriteCall("first.txt", "from turn one"),
			ScriptedModel.Text("turn one done"),
			WriteCall("second.txt", "from turn two"),
			ScriptedModel.Text("turn two done"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "first request");
		await harness.Runtime.SubmitAsync(harness.Session.Id, "second request");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var second = events.Where(item => item.Type == "message" && item.Role == "user").ElementAt(1);
		await harness.Runtime.RevertAsync(harness.Session.Id, second.Id);

		Assert.Equal("from turn one", File.ReadAllText(Path.Combine(workspace, "first.txt")));
		Assert.False(File.Exists(Path.Combine(workspace, "second.txt")));

		var replayed = HarnessRuntime
			.BuildHistory((await harness.Store.GetAsync(harness.Session.Id))!.Events)
			.Select(message => message.Text)
			.ToArray();
		Assert.Contains(replayed, text => text.Contains("first request"));
		Assert.DoesNotContain(replayed, text => text.Contains("second request"));
	}

	[Fact]
	public async Task PreviewReportsWhatAnUndoWouldChangeWithoutChangingIt()
	{
		var workspace = NewWorkspace();
		var harness = await StartAsync(workspace, WriteCall("created.txt", "agent output"), ScriptedModel.Text("done"));
		await harness.Runtime.SubmitAsync(harness.Session.Id, "make a file");

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		var boundary = events.First(item => item.Type == "message" && item.Role == "user").Id;

		var preview = await harness.Runtime.PreviewRevertAsync(harness.Session.Id, boundary);

		Assert.Equal("created.txt", Assert.Single(preview).Path);
		Assert.True(File.Exists(Path.Combine(workspace, "created.txt")));
	}

	/// <summary>A harness built without a snapshot service must still run turns, just without undo.</summary>
	[Fact]
	public async Task AHarnessWithoutSnapshotsStillRunsTurns()
	{
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration-plain"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys-plain"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(new ScriptedModel([ScriptedModel.Text("done")]))));
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted", "Scripted", "https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")]),
			"test-key");
		var store = new JsonlSessionStore(Path.Combine(_root, "sessions-plain"));
		var runtime = new HarnessRuntime(store, service);
		var session = await runtime.CreateSessionAsync(NewWorkspace());
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));

		await runtime.SubmitAsync(session.Id, "hello");

		Assert.False(runtime.SnapshotsEnabled);
		var events = (await store.GetAsync(session.Id))!.Events;
		Assert.Contains(events, item => item.Type == "message" && item.Role == "assistant");
		Assert.DoesNotContain(events, item => item.Type == SessionRevert.SnapshotType);
		await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RevertAsync(session.Id, events[0].Id));
	}

	/// <summary>
	/// A file the agent changed through the shell is invisible to the harness's own file bookkeeping,
	/// so it is the case an observation log cannot cover and a snapshot of the workspace can.
	/// </summary>
	[Fact]
	public async Task AChangeMadeThroughTheShellIsStillUndone()
	{
		var workspace = NewWorkspace();
		File.WriteAllText(Path.Combine(workspace, "tracked.txt"), "original");
		var harness = await StartAsync(
			workspace,
			ScriptedModel.ToolCall("bash", System.Text.Json.JsonSerializer.Serialize(
				new Dictionary<string, string>
				{
					["command"] = "printf 'rewritten by the shell' > tracked.txt && printf 'side effect' > extra.txt",
					["description"] = "rewrite a file",
				})),
			ScriptedModel.Text("done"));

		await harness.Runtime.SubmitAsync(harness.Session.Id, "use the shell");
		Assert.Equal("rewritten by the shell", File.ReadAllText(Path.Combine(workspace, "tracked.txt")));

		var events = (await harness.Store.GetAsync(harness.Session.Id))!.Events;
		await harness.Runtime.RevertAsync(
			harness.Session.Id,
			events.First(item => item.Type == "message" && item.Role == "user").Id);

		Assert.Equal("original", File.ReadAllText(Path.Combine(workspace, "tracked.txt")));
		Assert.False(File.Exists(Path.Combine(workspace, "extra.txt")));
	}

	/// <summary>
	/// An interrupted turn is exactly when undo matters most: the workspace has been changed and the
	/// conversation has no closing answer explaining what. Its files must still be recoverable.
	/// </summary>
	[Fact]
	public async Task AnInterruptedTurnCanStillBeUndone()
	{
		var workspace = NewWorkspace();
		var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = new ModelConfigurationService(
			Path.Combine(_root, "configuration-interrupt"),
			DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys-interrupt"))),
			new HttpClient(new OkHandler()),
			new HttpClientPipelineTransport(new HttpClient(
				new BlockingModel([WriteCall("created.txt", "written before the stop")], blocked))));
		await service.SaveProviderAsync(
			new ModelProviderProfile(
				"scripted", "Scripted", "https://scripted.test/v1",
				ModelProtocol.OpenAIChatCompletions,
				[new ModelDescriptor("scripted-model", "Scripted Model")]),
			"test-key");
		var store = new JsonlSessionStore(Path.Combine(_root, "sessions-interrupt"));
		var runtime = new HarnessRuntime(
			store, service, snapshots: new GitSnapshotService(Path.Combine(_root, "snapshots-interrupt")));
		var session = await runtime.CreateSessionAsync(workspace);
		await service.SelectAsync(session.Id, new ModelSelection("scripted", "scripted-model"));

		var turn = runtime.SubmitAsync(session.Id, "write a file then hang");
		await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(runtime.Interrupt(session.Id));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

		// The tool call landed, so the workspace changed even though the turn never finished.
		Assert.True(File.Exists(Path.Combine(workspace, "created.txt")));

		var events = (await store.GetAsync(session.Id))!.Events;
		await runtime.RevertAsync(session.Id, events.First(item => item.Type == "message" && item.Role == "user").Id);

		Assert.False(File.Exists(Path.Combine(workspace, "created.txt")));
	}

	// ----- fakes ---------------------------------------------------------------------------------

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	/// <summary>
	/// Replays a script and then hangs, signalling <paramref name="blocked"/> first so the test can
	/// interrupt at a point where the workspace has already been changed.
	/// </summary>
	private sealed class BlockingModel(string[] responses, TaskCompletionSource blocked) : HttpMessageHandler
	{
		private int _index;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var index = _index++;
			if (index >= responses.Length)
			{
				blocked.TrySetResult();
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(responses[index], Encoding.UTF8, "application/json"),
			};
		}
	}

	private sealed class ScriptedModel(string[] responses) : HttpMessageHandler
	{
		private int _index;

		public static string Text(string content) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
			+ System.Text.Json.JsonSerializer.Serialize(content)
			+ "},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		public static string ToolCall(string name, string argumentsJson) =>
			"{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"scripted-model\","
			+ "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":["
			+ "{\"id\":\"call_" + name + "\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
			+ System.Text.Json.JsonSerializer.Serialize(argumentsJson)
			+ "}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(
					responses[Math.Min(_index++, responses.Length - 1)],
					Encoding.UTF8,
					"application/json"),
			});
	}
}

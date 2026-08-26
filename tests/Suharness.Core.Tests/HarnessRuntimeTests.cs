using Microsoft.Agents.AI;
using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Core;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Suharness.Core.Tests;

public sealed class HarnessRuntimeTests
{
	[Fact]
	public async Task SubmitPersistsUserPromptAndModelNeutralNotice()
	{
		var store = new MemoryStore();
		var runtime = new HarnessRuntime(store, new NullAgentFactory());
		var session = await runtime.CreateSessionAsync(".");

		await runtime.SubmitAsync(session.Id, "Inspect the repository");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.NotNull(transcript);
		Assert.Collection(transcript.Events,
			item => Assert.Equal(("user", "Inspect the repository"), (item.Role, item.Content)),
			item => { Assert.Equal("system", item.Role); Assert.Contains("No model is selected", item.Content); },
			item =>
			{
				Assert.Equal("turn_summary", item.Type);
				using var summary = JsonDocument.Parse(item.Content);
				Assert.Equal("no_model", summary.RootElement.GetProperty("stopReason").GetString());
				Assert.True(summary.RootElement.GetProperty("validationSatisfied").GetBoolean());
			});
	}

	[Fact]
	public async Task AccessModeIsPersistedPerSessionAndPassedToTheAgentFactory()
	{
		var store = new MemoryStore();
		var factory = new CapturingAgentFactory();
		var runtime = new HarnessRuntime(store, factory);
		var session = await runtime.CreateSessionAsync(".");

		await runtime.SetAccessModeAsync(session.Id, HarnessAccessMode.ReadOnly);
		await runtime.SubmitAsync(session.Id, "Inspect only");

		Assert.Equal(HarnessAccessMode.ReadOnly, (await runtime.GetSessionAsync(session.Id))!.Session.AccessMode);
		Assert.Equal(HarnessAccessMode.ReadOnly, factory.Context?.AccessMode);
	}

	[Fact]
	public async Task ProviderFailureIsPersistedWithoutEscaping()
	{
		var store = new MemoryStore();
		var runtime = new HarnessRuntime(store, new ThrowingAgentFactory());
		var session = await runtime.CreateSessionAsync(".");

		await runtime.SubmitAsync(session.Id, "Run this");

		var transcript = await runtime.GetSessionAsync(session.Id);
		Assert.NotNull(transcript);
		var failure = Assert.Single(transcript.Events, item => item.Type == "error");
		Assert.Contains("Model request failed", failure.Content);
		Assert.Contains("simulated provider failure", failure.Content);
	}

	[Fact]
	public async Task AuthenticationFailuresArePersistedWithActionableGuidance()
	{
		var store = new MemoryStore();
		var runtime = new HarnessRuntime(store, new AuthenticationFailureAgentFactory());
		var session = await runtime.CreateSessionAsync(".");

		await runtime.SubmitAsync(session.Id, "Run this");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var failure = Assert.Single(transcript!.Events, item => item.Type == "error");
		Assert.Contains("rejected the configured API key", failure.Content);
		Assert.Contains("model settings", failure.Content);
	}

	private sealed class MemoryStore : ISessionStore
	{
		private readonly Dictionary<Guid, HarnessSession> _sessions = [];
		private readonly List<SessionEvent> _events = [];
		public Task<IReadOnlyList<HarnessSession>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HarnessSession>>(_sessions.Values.ToArray());
		public Task<SessionTranscript?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_sessions.TryGetValue(id, out var session) ? new SessionTranscript(session, _events.Where(item => item.SessionId == id).ToArray()) : null);
		public Task SaveSessionAsync(HarnessSession session, CancellationToken cancellationToken = default) { _sessions[session.Id] = session; return Task.CompletedTask; }
		public Task AppendAsync(SessionEvent item, CancellationToken cancellationToken = default) { _events.Add(item); return Task.CompletedTask; }
		public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { _sessions.Remove(id); _events.RemoveAll(item => item.SessionId == id); return Task.CompletedTask; }
	}

	private sealed class ThrowingAgentFactory : IAgentFactory
	{
		public ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<Microsoft.Agents.AI.AIAgent?>(new InvalidOperationException("simulated provider failure"));
	}

	private sealed class CapturingAgentFactory : IAgentFactory
	{
		public AgentContext? Context { get; private set; }

		public ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default)
		{
			Context = context;
			return ValueTask.FromResult<Microsoft.Agents.AI.AIAgent?>(null);
		}
	}

	private sealed class AuthenticationFailureAgentFactory : IAgentFactory
	{
		public ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(AgentContext context, CancellationToken cancellationToken = default) =>
			ValueTask.FromException<Microsoft.Agents.AI.AIAgent?>(new InvalidOperationException("HTTP 401: Missing Authentication header"));
	}
}

public sealed class ModelConfigurationServiceTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"suharness-models-{Guid.NewGuid():N}");

	[Fact]
	public async Task SavesRedactedProviderAndEncryptedCredential()
	{
		var service = CreateService();
		var profile = Profile();

		await service.SaveProviderAsync(profile, "secret-api-key");
		var snapshot = await service.GetSnapshotAsync();

		var configured = Assert.Single(snapshot.Providers);
		Assert.Equal((profile.Id, profile.DisplayName, profile.BaseUrl, profile.Protocol),
			(configured.Profile.Id, configured.Profile.DisplayName, configured.Profile.BaseUrl, configured.Profile.Protocol));
		Assert.Equal(profile.Models, configured.Profile.Models);
		Assert.True(configured.CredentialConfigured);
		Assert.Equal(new ModelSelection("acme", "acme-large"), snapshot.DefaultSelection);
		var credentialBytes = await File.ReadAllBytesAsync(Path.Combine(_root, "credentials.bin"));
		Assert.DoesNotContain("secret-api-key", Encoding.UTF8.GetString(credentialBytes));
		Assert.DoesNotContain("secret-api-key", await File.ReadAllTextAsync(Path.Combine(_root, "models.json")));
	}

	[Fact]
	public async Task EmptyApiKeyClearsStoredModelCredential()
	{
		var service = CreateService();
		var profile = Profile();
		await service.SaveProviderAsync(profile, "temporary-key");

		await service.SaveProviderAsync(profile, string.Empty);

		Assert.False((await service.GetSnapshotAsync()).Providers.Single().CredentialConfigured);
	}

	[Fact]
	public async Task DiscoversModelsFromDraftAndPersistsSessionSelection()
	{
		var handler = new StubHandler("""{"data":[{"id":"small","context_length":32768},{"id":"large","context_window":1000000}]}""");
		var service = CreateService(handler);
		// A remote provider needs a saved key before an agent can be created for it.
		await service.SaveProviderAsync(Profile(), "saved-key");

		var discovered = await service.DiscoverModelsAsync(Profile(), "draft-key");
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("acme", "acme-large"));

		Assert.Equal(["large", "small"], discovered.Select(model => model.Id));
		Assert.Equal([1000000, 32768], discovered.Select(model => model.ContextWindow));
		Assert.Equal("Bearer", handler.Authorization?.Scheme);
		Assert.Equal("draft-key", handler.Authorization?.Parameter);
		Assert.Equal(new ModelSelection("acme", "acme-large"), await service.GetSelectionAsync(sessionId));
		Assert.NotNull(await service.CreateAsync(new AgentContext(sessionId, _root)));
	}

	[Fact]
	public async Task RemovingAModelReconcilesStaleSessionSelections()
	{
		var service = CreateService();
		var profile = Profile() with
		{
			Models =
			[
				new ModelDescriptor("acme-large", "Acme Large"),
				new ModelDescriptor("acme-small", "Acme Small"),
			],
		};
		await service.SaveProviderAsync(profile, "saved-key");
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("acme", "acme-small"));

		await service.SaveProviderAsync(Profile(), null);

		Assert.Equal(new ModelSelection("acme", "acme-large"), await service.GetSelectionAsync(sessionId));
	}

	[Fact]
	public async Task PersistsTeamPersonaAssignmentsPerSession()
	{
		var service = CreateService();
		await service.SaveProviderAsync(Profile(), "saved-key");
		var sessionId = Guid.NewGuid();
		var selection = new ModelSelection("acme", "acme-large");
		var configuration = new TeamConfiguration(true, Enum.GetValues<CodingPersona>().ToDictionary(persona => persona, _ => selection));

		await service.SetTeamConfigurationAsync(sessionId, configuration);

		var persisted = await service.GetTeamConfigurationAsync(sessionId);
		Assert.True(persisted.Enabled);
		Assert.Equal(selection, persisted.ModelFor(CodingPersona.Explorer));
		Assert.Equal(selection, persisted.ModelFor(CodingPersona.JuniorDeveloper));
		Assert.Equal(selection, persisted.ModelFor(CodingPersona.Reviewer));
		Assert.Null((await service.GetTeamConfigurationAsync(Guid.NewGuid())).ModelFor(CodingPersona.Explorer));

		await service.SetTeamConfigurationAsync(sessionId, persisted with { Enabled = false });
		Assert.False((await service.GetTeamConfigurationAsync(sessionId)).Enabled);
	}

	[Fact]
	public async Task EnablingTeamModeDisablesPlanMode()
	{
		var sessions = new JsonlSessionStore(Path.Combine(_root, "mode-sessions"));
		var planMode = new PlanModeStore(sessions);
		var service = CreateService(planModeStore: planMode);
		await service.SaveProviderAsync(Profile(), "saved-key");
		var sessionId = Guid.NewGuid();
		await planMode.SetAsync(sessionId, true);
		var selection = new ModelSelection("acme", "acme-large");
		var configuration = new TeamConfiguration(true, Enum.GetValues<CodingPersona>().ToDictionary(persona => persona, _ => selection));

		await service.SetTeamConfigurationAsync(sessionId, configuration);

		Assert.True((await service.GetTeamConfigurationAsync(sessionId)).Enabled);
		Assert.False(await planMode.IsActiveAsync(sessionId));
	}

	[Fact]
	public async Task AgentFrameworkRequestCarriesBearerCredentialAndSelectedModel()
	{
		var handler = new ChatCompletionHandler();
		var transport = new HttpClientPipelineTransport(new HttpClient(handler));
		var service = CreateService(new StubHandler("{\"data\":[]}"), transport);
		await service.SaveProviderAsync(Profile(), "secret-api-key");
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("acme", "acme-large"));

		var agent = await service.CreateAsync(new AgentContext(sessionId, _root));
		var response = await agent!.RunAsync("hello");

		Assert.Equal("Bearer", handler.Authorization?.Scheme);
		Assert.Equal("secret-api-key", handler.Authorization?.Parameter);
		Assert.EndsWith("/chat/completions", handler.RequestUri?.AbsolutePath);
		Assert.Contains("\"model\":\"acme-large\"", handler.RequestBody);
		Assert.Contains("transport ok", response.ToString());
	}

	[Fact]
	public async Task AgentContextModelOverrideWinsWithoutChangingSessionSelection()
	{
		var handler = new ChatCompletionHandler();
		var transport = new HttpClientPipelineTransport(new HttpClient(handler));
		var service = CreateService(new StubHandler("{\"data\":[]}"), transport);
		await service.SaveProviderAsync(Profile(), "acme-key");
		await service.SaveProviderAsync(new ModelProviderProfile(
			"override",
			"Override",
			"https://override.example/v1",
			ModelProtocol.OpenAIChatCompletions,
			[new ModelDescriptor("override-model", "Override Model")]), "override-key");
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("acme", "acme-large"));

		var agent = await service.CreateAsync(new AgentContext(
			sessionId,
			_root,
			ModelOverride: new ModelSelection("override", "override-model")));
		await agent!.RunAsync("hello");

		Assert.Contains("\"model\":\"override-model\"", handler.RequestBody);
		Assert.Equal("override-key", handler.Authorization?.Parameter);
		Assert.Equal(new ModelSelection("acme", "acme-large"), await service.GetSelectionAsync(sessionId));
	}

	private ModelConfigurationService CreateService(HttpMessageHandler? handler = null, PipelineTransport? modelTransport = null, IPlanModeStore? planModeStore = null) => new(
		_root,
		DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
		new HttpClient(handler ?? new StubHandler("{\"data\":[]}")),
		modelTransport,
		planModeStore: planModeStore);

	private static ModelProviderProfile Profile() => new(
		"acme",
		"Acme",
		"https://models.acme.test/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor("acme-large", "Acme Large")]);

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
		GC.SuppressFinalize(this);
	}

	private sealed class StubHandler(string json) : HttpMessageHandler
	{
		public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Authorization = request.Headers.Authorization;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
		}
	}

	private sealed class ChatCompletionHandler : HttpMessageHandler
	{
		public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
		public Uri? RequestUri { get; private set; }
		public string RequestBody { get; private set; } = string.Empty;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Authorization = request.Headers.Authorization;
			RequestUri = request.RequestUri;
			RequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
			const string json = """
                {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"acme-large","choices":[{"index":0,"message":{"role":"assistant","content":"transport ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
                """;
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		}
	}
}

public sealed class WorkspaceRecoveryTests : IDisposable
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"suharness-workspaces-{Guid.NewGuid():N}");

	[Fact]
	public async Task CanonicalRegistrationIsIdempotentAndNewestFirst()
	{
		var firstPath = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
		var secondPath = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
		var service = new WorkspaceService(Path.Combine(_root, "state"));

		var first = await service.AddAsync(firstPath + Path.DirectorySeparatorChar);
		var duplicate = await service.AddAsync(Path.Combine(firstPath, "."));
		var second = await service.AddAsync(secondPath);

		Assert.Equal(first.Id, duplicate.Id);
		Assert.Equal([second.Id, first.Id], (await service.ListAsync()).Select(item => item.Id));
	}

	[Fact]
	public async Task LastSelectionSurvivesRestartAndRepresentsNoWorkspace()
	{
		var state = Path.Combine(_root, "state");
		var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var service = new WorkspaceService(state);
		var workspace = await service.AddAsync(project);

		Assert.Null(await service.GetSelectionAsync());
		await service.SelectAsync(workspace.Id);
		Assert.Equal(workspace.Id, (await new WorkspaceService(state).GetSelectionAsync())?.WorkspaceId);

		await service.SelectAsync(null);
		var noWorkspace = await new WorkspaceService(state).GetSelectionAsync();
		Assert.NotNull(noWorkspace);
		Assert.Null(noWorkspace.WorkspaceId);
	}

	[Fact]
	public async Task RemovingSelectedWorkspaceFallsBackToNoWorkspace()
	{
		var service = new WorkspaceService(Path.Combine(_root, "state"));
		var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var workspace = await service.AddAsync(project);
		await service.SelectAsync(workspace.Id);

		await service.RemoveAsync(workspace.Id);

		var selection = await service.GetSelectionAsync();
		Assert.NotNull(selection);
		Assert.Null(selection.WorkspaceId);
	}

	[Fact]
	public async Task FilesystemRootRemainsAbsoluteAndBrowsable()
	{
		var service = new WorkspaceService(Path.Combine(_root, "state"));
		var root = Path.GetPathRoot(_root)!;

		var listing = await service.BrowseAsync(root);

		Assert.Equal(root, listing.CurrentPath);
		Assert.All(listing.Entries, entry => Assert.True(Path.IsPathFullyQualified(entry.Path)));
	}

	[Fact]
	public async Task IncompleteCreateRollsBackAndCommittedCreateClearsStaleMarker()
	{
		var state = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
		var workspacePath = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var workspace = new HarnessWorkspace(Guid.NewGuid(), workspacePath, "project", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
		await WriteAsync(Path.Combine(state, "workspaces.records.json"), new Dictionary<Guid, HarnessWorkspace> { [workspace.Id] = workspace });
		await WriteAsync(Path.Combine(state, "workspaces.order.json"), Array.Empty<Guid>());
		await WriteAsync(Path.Combine(state, "workspace.mutation.json"), new { operation = 0, workspace });

		Assert.Empty(await new WorkspaceService(state).ListAsync());
		Assert.False(File.Exists(Path.Combine(state, "workspace.mutation.json")));

		await WriteAsync(Path.Combine(state, "workspaces.records.json"), new Dictionary<Guid, HarnessWorkspace> { [workspace.Id] = workspace });
		await WriteAsync(Path.Combine(state, "workspaces.order.json"), new[] { workspace.Id });
		await WriteAsync(Path.Combine(state, "workspace.mutation.json"), new { operation = 0, workspace });
		Assert.Equal(workspace.Id, Assert.Single(await new WorkspaceService(state).ListAsync()).Id);
		Assert.False(File.Exists(Path.Combine(state, "workspace.mutation.json")));
	}

	[Fact]
	public async Task PendingDeleteAlwaysCompletes()
	{
		var state = Path.Combine(_root, "state");
		var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var service = new WorkspaceService(state);
		var workspace = await service.AddAsync(project);
		await WriteAsync(Path.Combine(state, "workspace.mutation.json"), new { operation = 1, workspace });

		Assert.Empty(await new WorkspaceService(state).ListAsync());
		Assert.True(Directory.Exists(project));
		Assert.False(File.Exists(Path.Combine(state, "workspace.mutation.json")));
	}

	private static Task WriteAsync<T>(string path, T value)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		return File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
		GC.SuppressFinalize(this);
	}
}

public sealed class WorkspaceSessionLifecycleTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"suharness-sessions-{Guid.NewGuid():N}");

	[Fact]
	public async Task ReusesOneBlankPerWorkspaceThenScopesRenamesAndArchives()
	{
		var firstWorkspace = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
		var secondWorkspace = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "sessions")), new NullAgentFactory());

		var blank = await runtime.CreateSessionAsync(firstWorkspace);
		Assert.Equal(blank.Id, (await runtime.CreateSessionAsync(firstWorkspace)).Id);
		await runtime.SubmitAsync(blank.Id, "First task");
		var nextBlank = await runtime.CreateSessionAsync(firstWorkspace);
		Assert.NotEqual(blank.Id, nextBlank.Id);
		var other = await runtime.CreateSessionAsync(secondWorkspace);

		Assert.Equal([nextBlank.Id, blank.Id], (await runtime.ListSessionsAsync(firstWorkspace)).Select(item => item.Id));
		Assert.Equal(other.Id, Assert.Single(await runtime.ListSessionsAsync(secondWorkspace)).Id);
		await runtime.RenameSessionAsync(nextBlank.Id, "  Planned   refactor  ");
		Assert.Equal("Planned refactor", (await runtime.GetSessionAsync(nextBlank.Id))!.Session.Title);
		await runtime.ArchiveSessionAsync(nextBlank.Id);
		Assert.DoesNotContain(await runtime.ListSessionsAsync(firstWorkspace), item => item.Id == nextBlank.Id);
		Assert.True((await runtime.GetSessionAsync(nextBlank.Id))!.Session.Archived);
	}

	[Fact]
	public async Task ConcurrentCreatesShareOneBlankSession()
	{
		var workspace = Directory.CreateDirectory(Path.Combine(_root, "concurrent")).FullName;
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "concurrent-sessions")), new NullAgentFactory());

		var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => runtime.CreateSessionAsync(workspace)));

		Assert.Single(sessions.Select(session => session.Id).Distinct());
		Assert.Single(await runtime.ListSessionsAsync(workspace));
	}

	[Fact]
	public async Task DeleteRemovesOnlyTheSelectedSessionAndTranscript()
	{
		var workspace = Directory.CreateDirectory(Path.Combine(_root, "delete")).FullName;
		var runtime = new HarnessRuntime(new JsonlSessionStore(Path.Combine(_root, "delete-sessions")), new NullAgentFactory());
		var first = await runtime.CreateSessionAsync(workspace);
		await runtime.SubmitAsync(first.Id, "First task");
		var second = await runtime.CreateSessionAsync(workspace);
		await runtime.SubmitAsync(second.Id, "Second task");

		await runtime.DeleteSessionAsync(first.Id);

		Assert.Null(await runtime.GetSessionAsync(first.Id));
		Assert.Equal(second.Id, Assert.Single(await runtime.ListSessionsAsync(workspace)).Id);
		Assert.NotNull(await runtime.GetSessionAsync(second.Id));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, true);
		GC.SuppressFinalize(this);
	}
}

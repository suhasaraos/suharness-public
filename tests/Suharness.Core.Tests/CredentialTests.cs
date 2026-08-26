using Microsoft.AspNetCore.DataProtection;
using Suharness.Abstractions;
using Suharness.Infrastructure;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace Suharness.Core.Tests;

/// <summary>
/// Tests for how a saved-but-blank API key behaves. A provider row created with an empty key looked
/// configured in the snapshot while sending no <c>Authorization</c> header, which an OpenAI-compatible
/// gateway rejects with a bare HTTP 401 — the failure seen against OpenRouter.
/// </summary>
public sealed class CredentialTests : IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("suharness-credentials-").FullName;

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
	}

	private ModelConfigurationService CreateService(PipelineTransport? modelTransport = null) => new(
		_root,
		DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))),
		new HttpClient(new OkHandler()),
		modelTransport);

	private static ModelProviderProfile Profile() => new(
		"openrouter",
		"OpenRouter",
		"https://openrouter.test/api/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor("example/test-model", "Example Test Model")]);

	[Fact]
	public void OnlyRemoteProvidersRequireApiKeys()
	{
		Assert.True(Profile().RequiresApiKey);
		Assert.False(LocalProfile().RequiresApiKey);
	}

	[Fact]
	public async Task ABlankApiKeyIsNotReportedAsAConfiguredCredential()
	{
		var service = CreateService();

		await service.SaveProviderAsync(Profile(), "   ");

		var snapshot = await service.GetSnapshotAsync();
		var provider = Assert.Single(snapshot.Providers);
		Assert.False(provider.CredentialConfigured);
	}

	[Fact]
	public async Task ANonBlankApiKeyIsReportedAsConfiguredAndIsSent()
	{
		var handler = new ChatHandler();
		var service = CreateService(new HttpClientPipelineTransport(new HttpClient(handler)));
		await service.SaveProviderAsync(Profile(), "sk-or-test-key");
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("openrouter", "example/test-model"));

		var snapshot = await service.GetSnapshotAsync();
		Assert.True(Assert.Single(snapshot.Providers).CredentialConfigured);

		var agent = await service.CreateAsync(new AgentContext(sessionId, _root));
		await agent!.RunAsync("hello");

		Assert.Equal("Bearer", handler.Authorization?.Scheme);
		Assert.Equal("sk-or-test-key", handler.Authorization?.Parameter);
	}

	[Fact]
	public async Task AnInvalidOpenRouterKeyIsRejectedBeforeItCanBeSavedOrSent()
	{
		var service = CreateService();

		var exception = await Assert.ThrowsAsync<ModelConfigurationException>(() =>
			service.SaveProviderAsync(Profile(), "not-an-openrouter-key"));

		Assert.Contains("start with 'sk-or-'", exception.Message);
		Assert.Empty((await service.GetSnapshotAsync()).Providers);
	}

	[Fact]
	public async Task ARemoteProviderWithNoKeyFailsWithAnActionableMessageRatherThanABare401()
	{
		var service = CreateService();
		await service.SaveProviderAsync(Profile(), null);
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("openrouter", "example/test-model"));

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service);
		var session = await runtime.CreateSessionAsync(_root);
		await service.SelectAsync(session.Id, new ModelSelection("openrouter", "example/test-model"));

		await runtime.SubmitAsync(session.Id, "hello");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var failure = Assert.Single(transcript!.Events, item => item.Type is "error" or "notice");
		Assert.Contains("API key", failure.Content, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("openrouter.test", failure.Content);
	}

	[Fact]
	public async Task ALoopbackProviderWithNoKeyStillRunsUnauthenticated()
	{
		// KoboldCpp and other local servers are intentionally unauthenticated, so a missing key
		// must not block them the way it blocks a remote gateway.
		var handler = new ChatHandler();
		var service = CreateService(new HttpClientPipelineTransport(new HttpClient(handler)));
		var local = new ModelProviderProfile(
			"koboldcpp",
			"KoboldCpp Local",
			"http://127.0.0.1:5001/v1",
			ModelProtocol.OpenAIChatCompletions,
			[new ModelDescriptor("local-model", "Local Model")]);
		await service.SaveProviderAsync(local, null);
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("koboldcpp", "local-model"));

		var agent = await service.CreateAsync(new AgentContext(sessionId, _root));

		Assert.NotNull(agent);
		await agent.RunAsync("hello");
	}

	[Fact]
	public void AProviderWithoutAnExplicitIdleTimeoutUsesTheDefault()
	{
		Assert.Null(Profile().IdleTimeoutSeconds);
		Assert.Equal(ModelProviderProfile.DefaultIdleTimeout, Profile().IdleTimeout);

		// The library default is 100 seconds, which a slow local model exceeds on prompt processing.
		Assert.True(ModelProviderProfile.DefaultIdleTimeout > TimeSpan.FromSeconds(100));
	}

	[Theory]
	[InlineData(45, 45)]
	[InlineData(null, 600)]
	[InlineData(0, 600)]
	public void AProviderIdleTimeoutOverridesTheDefaultOnlyWhenItIsAPositiveNumber(int? configured, int expectedSeconds)
	{
		var profile = Profile() with { IdleTimeoutSeconds = configured };

		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), profile.IdleTimeout);
	}

	[Theory]
	[InlineData(25, 25)]
	[InlineData(null, ModelProviderProfile.DefaultMaxToolIterationsPerTurn)]
	[InlineData(0, ModelProviderProfile.DefaultMaxToolIterationsPerTurn)]
	public void AProviderToolIterationCapOverridesTheDefaultOnlyWhenItIsAPositiveNumber(int? configured, int expected)
	{
		var profile = Profile() with { MaxToolIterationsPerTurn = configured };

		Assert.Equal(expected, profile.ToolIterationCap);
	}

	/// <summary>
	/// A provider's idle timeout must actually reach the client pipeline. The timeout bounds silence
	/// between reads rather than the length of a response, so an endpoint that pauses before its first
	/// token is exactly what trips it — which is what a large local model does during prompt processing.
	/// </summary>
	[Fact]
	public async Task AnEndpointThatPausesLongerThanTheProviderIdleTimeoutFails()
	{
		var service = CreateService(new HttpClientPipelineTransport(new HttpClient(new SlowChatHandler(TimeSpan.FromSeconds(2)))));
		var profile = LocalProfile() with { IdleTimeoutSeconds = 1 };
		await service.SaveProviderAsync(profile, null);
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("koboldcpp", "local-model"));

		var agent = await service.CreateAsync(new AgentContext(sessionId, _root));

		await Assert.ThrowsAnyAsync<Exception>(() => agent!.RunAsync("hello"));
	}

	[Fact]
	public async Task TheSamePauseSucceedsUnderTheDefaultIdleTimeout()
	{
		var service = CreateService(new HttpClientPipelineTransport(new HttpClient(new SlowChatHandler(TimeSpan.FromSeconds(2)))));
		await service.SaveProviderAsync(LocalProfile(), null);
		var sessionId = Guid.NewGuid();
		await service.SelectAsync(sessionId, new ModelSelection("koboldcpp", "local-model"));

		var agent = await service.CreateAsync(new AgentContext(sessionId, _root));
		var response = await agent!.RunAsync("hello");

		Assert.Contains("ok", response.Text);
	}

	/// <summary>
	/// The client library's timeout message names a class the reader cannot reach and says nothing
	/// about which setting to change, so the transcript carries an actionable message instead. A
	/// timeout is a transient class, so it is retried first: the actionable message is what the reader
	/// gets once the provider's whole retry budget is spent.
	/// </summary>
	[Fact]
	public async Task AnIdleTimeoutIsRetriedThenReportedWithAnActionableMessage()
	{
		var service = CreateService(new HttpClientPipelineTransport(new HttpClient(new SlowChatHandler(TimeSpan.FromSeconds(2)))));
		// One retry keeps the test to two attempts; the default policy's two would only repeat it.
		var policy = new RetryPolicy(MaxRetries: 1, InitialDelayMs: 1, MaxDelayMs: 2, JitterRatio: 0);
		await service.SaveProviderAsync(LocalProfile() with { IdleTimeoutSeconds = 1, RetryPolicy = policy }, null);

		var store = new JsonlSessionStore(Path.Combine(_root, "sessions"));
		var runtime = new HarnessRuntime(store, service, retryPolicies: service);
		var session = await runtime.CreateSessionAsync(_root);
		await service.SelectAsync(session.Id, new ModelSelection("koboldcpp", "local-model"));

		await runtime.SubmitAsync(session.Id, "hello");

		var transcript = await runtime.GetSessionAsync(session.Id);
		var retry = Assert.Single(transcript!.Events, item => item.Type == "notice" && item.Content.Contains("retried"));
		Assert.Contains("attempt 2 of 2", retry.Content);

		var failure = Assert.Single(transcript.Events, item => item.Type == "error");
		Assert.Contains("idle timeout", failure.Content, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("ClientPipelineOptions", failure.Content);
	}

	private static ModelProviderProfile LocalProfile() => new(
		"koboldcpp",
		"KoboldCpp Local",
		"http://127.0.0.1:5001/v1",
		ModelProtocol.OpenAIChatCompletions,
		[new ModelDescriptor("local-model", "Local Model")]);

	private sealed class OkHandler : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json"),
			});
	}

	private sealed class ChatHandler : HttpMessageHandler
	{
		public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Authorization = request.Headers.Authorization;
			const string json = """
				{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
				""";
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(json, Encoding.UTF8, "application/json"),
			});
		}
	}

	/// <summary>Stands in for a model that pauses before producing any output.</summary>
	private sealed class SlowChatHandler(TimeSpan pause) : HttpMessageHandler
	{
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			await Task.Delay(pause, cancellationToken);
			const string json = """
				{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
				""";
			return new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(json, Encoding.UTF8, "application/json"),
			};
		}
	}
}

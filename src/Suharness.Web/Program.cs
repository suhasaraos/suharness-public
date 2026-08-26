using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Suharness.Abstractions;
using Suharness.Core;
using Suharness.Infrastructure;
using Suharness.Tools;
using Suharness.Web.Components;

namespace Suharness.Web;

public static class Program
{
	public static async Task Main(string[] args)
	{
		var builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			Args = args,
			ApplicationName = typeof(Program).Assembly.GetName().Name,
		});
		StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
		var home = Environment.GetEnvironmentVariable("SUHARNESS_HOME")
			?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".suharness");

		builder.Services.AddRazorComponents().AddInteractiveServerComponents();
		var dataProtection = builder.Services.AddDataProtection()
			.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(home, "data-protection")));
		if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

		// Persistence
		var sessionStore = new JsonlSessionStore(Path.Combine(home, "sessions"));
		builder.Services.AddSingleton<ISessionStore>(sessionStore);

		// Token accounting. The tracker holds each session's running totals in memory and writes them
		// through to the session record at a turn boundary, so the session list can show what a
		// session has spent without reading a transcript and streaming never writes per token.
		builder.Services.AddSingleton<ISessionUsageTracker>(new SessionUsageTracker(sessionStore));
		builder.Services.AddSingleton<IWorkspaceService>(new WorkspaceService(Path.Combine(home, "configuration")));
		var imageAttachmentStore = new ImageAttachmentStore(Path.Combine(home, "attachments"));
		builder.Services.AddSingleton(imageAttachmentStore);
		builder.Services.AddSingleton<IImageAttachmentStore>(imageAttachmentStore);

		// Orchestration services
		builder.Services.AddSingleton<IUserQuestionBroker, UserQuestionBroker>();
		var planModeStore = new PlanModeStore(sessionStore);
		builder.Services.AddSingleton<IPlanModeStore>(planModeStore);
		builder.Services.AddSingleton<IGoalService>(new GoalService(sessionStore));
		builder.Services.AddSingleton<IJobService, JobService>();
		builder.Services.AddSingleton<IAgentInbox, AgentInbox>();
		builder.Services.AddSingleton<IAgentLifecycle, AgentLifecycle>();

		// Oversized tool results are moved here and replaced with a preview plus this path, so the
		// model can read them back without carrying them in the context window for the whole session.
		builder.Services.AddSingleton<ISpillStore>(new FileSpillStore(Path.Combine(home, "spill")));

		// The turn-scoped loop guard. It allows one legitimate repeat, then returns the prior result
		// under the new call id instead of executing an unchanged operation indefinitely.
		builder.Services.AddSingleton(new RepeatToolReminder());

		// MCP servers declared in configuration/mcp.json contribute their tools under stable,
		// server-qualified mcp__<server>__<tool> names. Server secrets live beside the model provider
		// keys, protected and write-only, never in the config file.
		builder.Services.AddSingleton(provider => new McpConfigurationService(
			Path.Combine(home, "configuration"),
			provider.GetRequiredService<IDataProtectionProvider>()));
		builder.Services.AddSingleton<IMcpConfigurationService>(provider => provider.GetRequiredService<McpConfigurationService>());
		builder.Services.AddSingleton(provider => new McpClientService(
			provider.GetRequiredService<IMcpConfigurationService>(),
			Console.Error.WriteLine));
		builder.Services.AddSingleton<IMcpToolProvider>(provider => provider.GetRequiredService<McpClientService>());

		// A narrowed tool catalog needs a way out, so the agent can ask for the capability group its
		// task actually needed rather than reporting a blocker the filter invented.
		builder.Services.AddSingleton<ICapabilityEscalation, CapabilityEscalationStore>();
		builder.Services.AddSingleton<ILanguageServerService>(new LanguageServerService(Path.Combine(home, "configuration")));
		var workspaceIndex = new WorkspaceIndex(Path.Combine(home, "index"));
		var symbolIndex = new SymbolIndex(workspaceIndex, Path.Combine(home, "index"));
		builder.Services.AddSingleton<IWorkspaceIndex>(workspaceIndex);
		builder.Services.AddSingleton<ISymbolIndex>(symbolIndex);
		builder.Services.AddSingleton<ISessionScratchStore>(new SessionScratchStore(Path.Combine(home, "scratch")));
		builder.Services.AddSingleton<IWorkspaceAgentStateStore>(new WorkspaceAgentStateStore(workspaceIndex, symbolIndex));

		// Subagent service first (depends on lazy IAgentFactory to avoid cycle).
		builder.Services.AddSingleton<ISubagentService>(provider => new SubagentService(
			() => provider.GetRequiredService<IAgentFactory>(),
			provider.GetRequiredService<ISessionStore>(),
			provider.GetRequiredService<IAgentInbox>(),
			usage: provider.GetRequiredService<ISessionUsageTracker>(),
			retryPolicies: () => provider.GetRequiredService<IRetryPolicyProvider>()));

		// Model / agent
		builder.Services.AddSingleton(provider => new ModelConfigurationService(
			Path.Combine(home, "configuration"),
			provider.GetRequiredService<IDataProtectionProvider>(),
			new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
			modelTransport: null,
			provider.GetRequiredService<IUserQuestionBroker>(),
			provider.GetRequiredService<IPlanModeStore>(),
			provider.GetRequiredService<IGoalService>(),
			provider.GetRequiredService<IJobService>(),
			provider.GetRequiredService<IAgentLifecycle>(),
			provider.GetRequiredService<ISubagentService>(),
			provider.GetRequiredService<ISessionStore>(),
			provider.GetRequiredService<ISpillStore>(),
			provider.GetRequiredService<RepeatToolReminder>(),
			provider.GetRequiredService<IMcpToolProvider>(),
			provider.GetRequiredService<ICapabilityEscalation>(),
			provider.GetRequiredService<ILanguageServerService>(),
			provider.GetRequiredService<ISessionUsageTracker>(),
			provider.GetRequiredService<IWorkspaceIndex>(),
			provider.GetRequiredService<ISymbolIndex>(),
			provider.GetRequiredService<ISessionScratchStore>()));
		builder.Services.AddSingleton<IModelConfigurationService>(provider => provider.GetRequiredService<ModelConfigurationService>());
		builder.Services.AddSingleton<IAgentFactory>(provider => provider.GetRequiredService<ModelConfigurationService>());
		// Retry policy travels with the provider route: a local server
		// that stalls while loading a model wants different behaviour from a hosted API that rate-limits.
		builder.Services.AddSingleton<IRetryPolicyProvider>(provider => provider.GetRequiredService<ModelConfigurationService>());
		builder.Services.AddSingleton<IToolIterationPolicyProvider>(provider => provider.GetRequiredService<ModelConfigurationService>());
		builder.Services.AddSingleton<IModelBehaviorPolicyProvider>(provider => provider.GetRequiredService<ModelConfigurationService>());

		builder.Services.AddSingleton<ICompactionService>(provider => new CompactionService(
			provider.GetRequiredService<IModelConfigurationService>()));

		// A turn's file changes are snapshotted into a Git object database the harness owns, held
		// here rather than inside the workspace so the user's own repository is never written to.
		// Without it a tool call is permanent the moment it runs, whatever the transcript says.
		builder.Services.AddSingleton<ISnapshotService>(new GitSnapshotService(
			Path.Combine(home, "snapshots"),
			GitSnapshotService.LoadOptions(Path.Combine(home, "configuration"), Console.Error.WriteLine),
			Console.Error.WriteLine));

		builder.Services.AddSingleton(provider => new HarnessRuntime(
			provider.GetRequiredService<ISessionStore>(),
			provider.GetRequiredService<IAgentFactory>(),
			provider.GetRequiredService<IGoalService>(),
			provider.GetRequiredService<IAgentInbox>(),
			provider.GetRequiredService<IAgentLifecycle>(),
			provider.GetRequiredService<ICompactionService>(),
			provider.GetRequiredService<IRetryPolicyProvider>(),
			provider.GetRequiredService<IToolIterationPolicyProvider>(),
			provider.GetRequiredService<IImageAttachmentStore>(),
			provider.GetRequiredService<ISnapshotService>(),
			provider.GetRequiredService<ISessionUsageTracker>(),
			provider.GetRequiredService<IModelBehaviorPolicyProvider>(),
			provider.GetRequiredService<ISessionScratchStore>(),
			provider.GetRequiredService<IWorkspaceAgentStateStore>()));

		// Prompt Queue store and service
		var promptQueueStore = new JsonPromptQueueStore(Path.Combine(home, "queue"));
		builder.Services.AddSingleton<IPromptQueueStore>(promptQueueStore);
		builder.Services.AddSingleton<IPromptQueueService>(provider => new PromptQueueService(
			provider.GetRequiredService<IPromptQueueStore>(),
			provider.GetRequiredService<HarnessRuntime>(),
			provider.GetRequiredService<IModelConfigurationService>(),
			provider.GetRequiredService<IPlanModeStore>(),
			provider.GetRequiredService<ISessionStore>()));
		builder.Services.AddHostedService<PromptQueueWorker>();

		var app = builder.Build();

		// SIGTERM/SIGINT: stop in-flight turns so a terminating process leaves each transcript with
		// its partial output persisted rather than being killed mid-stream.
		var runtime = app.Services.GetRequiredService<HarnessRuntime>();
		app.Lifetime.ApplicationStopping.Register(runtime.Shutdown);

		// A server marked as requiring approval escalates each of its tool calls to the human through
		// the ordinary pre-tool gate. Unity's editor tools change a live project, so this is how a
		// remote server whose effects the harness cannot inspect stays under the reader's control.
		var mcp = app.Services.GetRequiredService<McpClientService>();
		var mcpConfiguration = app.Services.GetRequiredService<McpConfigurationService>();
		var lifecycle = app.Services.GetRequiredService<IAgentLifecycle>();
		var workspaceAgentState = app.Services.GetRequiredService<IWorkspaceAgentStateStore>();
		lifecycle.ToolProgress += (sessionId, toolName, result) =>
		{
			if (!toolName.Equals("coding_workflow", StringComparison.Ordinal)) return;
			_ = Task.Run(async () =>
			{
				try
				{
					var transcript = await sessionStore.GetAsync(sessionId);
					if (transcript is not null) await workspaceAgentState.ProjectWorkflowProgressAsync(transcript, result);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { }
			});
		};
		lifecycle.BeforeToolCall += async (_, toolName, _) =>
		{
			if (mcp.ServerOf(toolName) is not { } server) return PreToolDecision.Allow;
			var snapshot = await mcpConfiguration.GetSnapshotAsync();
			var configured = snapshot.Servers.FirstOrDefault(item =>
				item.Config.Name.Equals(server, StringComparison.OrdinalIgnoreCase));
			return configured?.Config.RequiresApproval == true
				? PreToolDecision.Ask($"It runs on the \"{server}\" MCP server.")
				: PreToolDecision.Allow;
		};

		// Connect MCP servers before the first turn, so the model is never shown a tool catalog that
		// is missing tools it was told about. Startup is bounded; a slow server lands later.
		await mcp.StartAsync();

		// Editing a server from settings reconnects it, so a newly supplied key or endpoint applies
		// without a restart.
		mcpConfiguration.Changed += () => _ = mcp.ReloadAsync();

		app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
		app.UseAntiforgery();
		app.MapStaticAssets();
		app.MapGet("/attachments/{id}", (string id, IImageAttachmentStore attachments) =>
		{
			var image = attachments.Find(id);
			return image is null
				? Results.NotFound()
				: Results.File(image.Bytes.ToArray(), image.Reference.MediaType, enableRangeProcessing: true);
		});
		app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
		try { await app.RunAsync(); }
		finally { await mcp.DisposeAsync(); }
	}
}

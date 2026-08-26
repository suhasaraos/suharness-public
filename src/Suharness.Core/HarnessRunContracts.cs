using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Suharness.Core;

public sealed record HarnessRunEvent(
	string Key,
	string Type,
	string Content,
	string? ToolName = null,
	string? ToolArguments = null);

public sealed record HarnessRunProgress(
	string TextDelta,
	string? Activity,
	IReadOnlyList<HarnessRunEvent>? Events = null);

public sealed class NullAgentFactory : IAgentFactory
{
	public ValueTask<Microsoft.Agents.AI.AIAgent?> CreateAsync(
		AgentContext context,
		CancellationToken cancellationToken = default) => ValueTask.FromResult<Microsoft.Agents.AI.AIAgent?>(null);
}

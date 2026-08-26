using Suharness.Abstractions;

namespace Suharness.Web;

/// <summary>
/// Wakes scheduled prompt jobs and resumes persisted queue work when the application starts.
/// Enqueue operations also signal the dispatcher directly; this worker is the clock for RunAt jobs
/// and the recovery path for work that was persisted before the current process started.
/// </summary>
internal sealed class PromptQueueWorker(
	IPromptQueueService queue,
	ILogger<PromptQueueWorker> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
		try
		{
			do
			{
				try
				{
					await queue.ProcessQueueAsync(stoppingToken);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					logger.LogError(ex, "Prompt queue processing failed; it will retry on the next tick.");
				}
			}
			while (await timer.WaitForNextTickAsync(stoppingToken));
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
			// Normal application shutdown.
		}
	}
}

using Suharness.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suharness.Infrastructure;

/// <summary>
/// Persists prompt jobs, dependencies, and sequence counter to JSON files.
/// </summary>
public sealed class JsonPromptQueueStore : IPromptQueueStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates = new(
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	private readonly string _root;
	private readonly string _jobsFilePath;
	private readonly string _sequenceFilePath;
	private readonly SemaphoreSlim _gate;

	public JsonPromptQueueStore(string root)
	{
		_root = Path.GetFullPath(root);
		_jobsFilePath = Path.Combine(_root, "prompt_jobs.json");
		_sequenceFilePath = Path.Combine(_root, "prompt_sequence.json");
		// Multiple service/store instances can briefly overlap during a restart or in tests. They
		// must coordinate the shared read-modify-write files rather than racing on the same .tmp file.
		_gate = FileGates.GetOrAdd(_root, static _ => new SemaphoreSlim(1, 1));
	}

	private sealed class SequenceState
	{
		public int LastId { get; set; }
	}

	public async Task<int> NextIdSequenceAsync(CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_root);
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var current = 0;
			if (File.Exists(_sequenceFilePath))
			{
				await using var stream = File.OpenRead(_sequenceFilePath);
				var state = await JsonSerializer.DeserializeAsync<SequenceState>(stream, JsonOptions, cancellationToken);
				if (state is not null) current = state.LastId;
			}

			current++;
			var tempPath = _sequenceFilePath + ".tmp";
			await using (var outStream = File.Create(tempPath))
			{
				await JsonSerializer.SerializeAsync(outStream, new SequenceState { LastId = current }, JsonOptions, cancellationToken);
			}
			File.Move(tempPath, _sequenceFilePath, overwrite: true);
			return current;
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task<IReadOnlyList<PromptJob>> LoadJobsAsync(CancellationToken cancellationToken = default)
	{
		if (!File.Exists(_jobsFilePath)) return [];

		await _gate.WaitAsync(cancellationToken);
		try
		{
			if (!File.Exists(_jobsFilePath)) return [];
			await using var stream = File.OpenRead(_jobsFilePath);
			var list = await JsonSerializer.DeserializeAsync<List<PromptJob>>(stream, JsonOptions, cancellationToken);
			return list ?? [];
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task SaveJobAsync(PromptJob job, CancellationToken cancellationToken = default)
	{
		await SaveJobsCoreAsync([job], isFullReplacement: false, cancellationToken);
	}

	public async Task SaveJobsAsync(IEnumerable<PromptJob> jobs, CancellationToken cancellationToken = default)
	{
		await SaveJobsCoreAsync(jobs, isFullReplacement: false, cancellationToken);
	}

	private async Task SaveJobsCoreAsync(IEnumerable<PromptJob> jobs, bool isFullReplacement, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_root);
		await _gate.WaitAsync(cancellationToken);
		try
		{
			var existing = new Dictionary<string, PromptJob>(StringComparer.OrdinalIgnoreCase);
			if (!isFullReplacement && File.Exists(_jobsFilePath))
			{
				await using var readStream = File.OpenRead(_jobsFilePath);
				var loaded = await JsonSerializer.DeserializeAsync<List<PromptJob>>(readStream, JsonOptions, cancellationToken);
				if (loaded is not null)
				{
					foreach (var item in loaded)
					{
						existing[item.PromptId] = item;
					}
				}
			}

			foreach (var job in jobs)
			{
				existing[job.PromptId] = job;
			}

			var sorted = existing.Values.OrderBy(j => j.PromptId, StringComparer.OrdinalIgnoreCase).ToList();
			var tempPath = _jobsFilePath + ".tmp";
			await using (var outStream = File.Create(tempPath))
			{
				await JsonSerializer.SerializeAsync(outStream, sorted, JsonOptions, cancellationToken);
			}
			File.Move(tempPath, _jobsFilePath, overwrite: true);
		}
		finally
		{
			_gate.Release();
		}
	}
}

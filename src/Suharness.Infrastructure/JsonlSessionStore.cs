using Suharness.Abstractions;
using System.Text.Json;

namespace Suharness.Infrastructure;

public sealed class JsonlSessionStore(string root) : ISessionStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly string _root = Path.GetFullPath(root);
	private readonly SemaphoreSlim _gate = new(1, 1);

	public async Task<IReadOnlyList<HarnessSession>> ListAsync(CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(_root)) return [];
		var sessions = new List<HarnessSession>();
		foreach (var path in Directory.EnumerateFiles(_root, "*.session.json"))
		{
			await using var stream = File.OpenRead(path);
			var session = await JsonSerializer.DeserializeAsync<HarnessSession>(stream, JsonOptions, cancellationToken);
			if (session is not null) sessions.Add(session);
		}

		return sessions.OrderByDescending(session => session.UpdatedAt).ToArray();
	}

	/// <summary>
	/// The session record without its transcript. Usage totals are flushed through this on every
	/// turn, and reading a long event log to update a counter would charge the longest sessions the
	/// most for it.
	/// </summary>
	public async Task<HarnessSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var sessionPath = SessionPath(sessionId);
		if (!File.Exists(sessionPath)) return null;
		await using var stream = File.OpenRead(sessionPath);
		return await JsonSerializer.DeserializeAsync<HarnessSession>(stream, JsonOptions, cancellationToken);
	}

	public async Task<SessionTranscript?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		var sessionPath = SessionPath(sessionId);
		if (!File.Exists(sessionPath)) return null;

		await using var sessionStream = File.OpenRead(sessionPath);
		var session = await JsonSerializer.DeserializeAsync<HarnessSession>(sessionStream, JsonOptions, cancellationToken);
		if (session is null) return null;

		var events = new List<SessionEvent>();
		var eventsPath = EventsPath(sessionId);
		if (File.Exists(eventsPath))
		{
			using var reader = File.OpenText(eventsPath);
			while (await reader.ReadLineAsync(cancellationToken) is { } line)
			{
				var sessionEvent = JsonSerializer.Deserialize<SessionEvent>(line, JsonOptions);
				if (sessionEvent is not null) events.Add(sessionEvent);
			}
		}

		return new SessionTranscript(session, events);
	}

	public async Task SaveSessionAsync(HarnessSession session, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_root);
		var temporaryPath = SessionPath(session.Id) + ".tmp";
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await using (var stream = File.Create(temporaryPath))
			{
				await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken);
			}
			File.Move(temporaryPath, SessionPath(session.Id), true);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task AppendAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_root);
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await File.AppendAllTextAsync(
				EventsPath(sessionEvent.SessionId),
				JsonSerializer.Serialize(sessionEvent, JsonOptions) + Environment.NewLine,
				cancellationToken);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			File.Delete(EventsPath(sessionId));
			File.Delete(SessionPath(sessionId));
		}
		finally
		{
			_gate.Release();
		}
	}

	private string SessionPath(Guid id) => Path.Combine(_root, $"{id:N}.session.json");
	private string EventsPath(Guid id) => Path.Combine(_root, $"{id:N}.events.jsonl");
}

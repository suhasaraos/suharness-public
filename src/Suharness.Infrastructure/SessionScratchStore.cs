using Suharness.Abstractions;

namespace Suharness.Infrastructure;

/// <summary>Filesystem-backed scratch directories kept outside user workspaces.</summary>
public sealed class SessionScratchStore(string root) : ISessionScratchStore
{
	private readonly string _root = Path.GetFullPath(root);

	public string GetOrCreate(Guid sessionId)
	{
		var path = Path.Combine(_root, sessionId.ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	public void Delete(Guid sessionId)
	{
		var path = Path.Combine(_root, sessionId.ToString("N"));
		try
		{
			if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Session deletion must not fail because an external process still has a scratch file open.
		}
	}
}

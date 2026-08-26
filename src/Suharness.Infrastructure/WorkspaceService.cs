using Suharness.Abstractions;
using System.Text.Json;

namespace Suharness.Infrastructure;

public sealed class WorkspaceService : IWorkspaceService
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private readonly string _recordsPath;
	private readonly string _orderPath;
	private readonly string _selectionPath;
	private readonly string _mutationPath;
	private readonly SemaphoreSlim _gate = new(1, 1);

	public WorkspaceService(string root)
	{
		var canonicalRoot = Path.GetFullPath(root);
		_recordsPath = Path.Combine(canonicalRoot, "workspaces.records.json");
		_orderPath = Path.Combine(canonicalRoot, "workspaces.order.json");
		_selectionPath = Path.Combine(canonicalRoot, "workspace.selection.json");
		_mutationPath = Path.Combine(canonicalRoot, "workspace.mutation.json");
	}

	public async Task<WorkspaceSelection?> GetSelectionAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await RecoverAsync(cancellationToken);
			var selection = await ReadSelectionAsync(cancellationToken);
			if (selection?.WorkspaceId is not { } workspaceId) return selection;
			return (await ReadRecordsAsync(cancellationToken)).ContainsKey(workspaceId)
				? selection
				: new WorkspaceSelection(null);
		}
		finally { _gate.Release(); }
	}

	public async Task SelectAsync(Guid? workspaceId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await RecoverAsync(cancellationToken);
			if (workspaceId is not null && !(await ReadRecordsAsync(cancellationToken)).ContainsKey(workspaceId.Value))
				throw new InvalidOperationException("The selected workspace is not registered.");
			await WriteJsonAtomicAsync(_selectionPath, new WorkspaceSelection(workspaceId), cancellationToken);
		}
		finally { _gate.Release(); }
	}

	public async Task<IReadOnlyList<HarnessWorkspace>> ListAsync(CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await RecoverAsync(cancellationToken);
			var records = await ReadRecordsAsync(cancellationToken);
			return (await ReadOrderAsync(cancellationToken)).Where(records.ContainsKey).Select(id => records[id]).ToArray();
		}
		finally { _gate.Release(); }
	}

	public async Task<HarnessWorkspace> AddAsync(string path, CancellationToken cancellationToken = default)
	{
		var canonical = Canonical(path);
		if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException($"Workspace directory does not exist: {canonical}");
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await RecoverAsync(cancellationToken);
			var records = await ReadRecordsAsync(cancellationToken);
			var existing = records.Values.FirstOrDefault(item => PathsEqual(item.Path, canonical));
			if (existing is not null) return existing;
			var now = DateTimeOffset.UtcNow;
			var workspace = new HarnessWorkspace(Guid.NewGuid(), canonical, DirectoryName(canonical), now, now);
			await WriteJsonAtomicAsync(_mutationPath, new WorkspaceMutation(WorkspaceMutationKind.Create, workspace), cancellationToken);
			records[workspace.Id] = workspace;
			await WriteJsonAtomicAsync(_recordsPath, records, cancellationToken);
			var order = (await ReadOrderAsync(cancellationToken)).Where(id => id != workspace.Id).Prepend(workspace.Id).ToArray();
			await WriteJsonAtomicAsync(_orderPath, order, cancellationToken);
			CompleteMutation();
			return workspace;
		}
		finally { _gate.Release(); }
	}

	public async Task RemoveAsync(Guid workspaceId, CancellationToken cancellationToken = default)
	{
		await _gate.WaitAsync(cancellationToken);
		try
		{
			await RecoverAsync(cancellationToken);
			var records = await ReadRecordsAsync(cancellationToken);
			if (!records.TryGetValue(workspaceId, out var workspace)) return;
			await WriteJsonAtomicAsync(_mutationPath, new WorkspaceMutation(WorkspaceMutationKind.Delete, workspace), cancellationToken);
			await WriteJsonAtomicAsync(_orderPath, (await ReadOrderAsync(cancellationToken)).Where(id => id != workspaceId).ToArray(), cancellationToken);
			records.Remove(workspaceId);
			await WriteJsonAtomicAsync(_recordsPath, records, cancellationToken);
			if ((await ReadSelectionAsync(cancellationToken))?.WorkspaceId == workspaceId)
				await WriteJsonAtomicAsync(_selectionPath, new WorkspaceSelection(null), cancellationToken);
			CompleteMutation();
		}
		finally { _gate.Release(); }
	}

	public Task<DirectoryListing> BrowseAsync(string? path, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(path))
		{
			var roots = DriveInfo.GetDrives().Where(drive => drive.IsReady)
				.Select(drive => new DirectoryEntry(drive.Name, drive.RootDirectory.FullName, true)).ToArray();
			return Task.FromResult(new DirectoryListing(null, null, roots));
		}
		var canonical = Canonical(path);
		if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException($"Directory does not exist: {canonical}");
		var parent = Directory.GetParent(canonical)?.FullName;
		IReadOnlyList<DirectoryEntry> entries;
		try
		{
			entries = Directory.EnumerateDirectories(canonical)
				.Select(item => new DirectoryEntry(Path.GetFileName(item), item, true))
				.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		}
		catch (UnauthorizedAccessException) { entries = []; }
		return Task.FromResult(new DirectoryListing(canonical, parent, entries));
	}

	private async Task RecoverAsync(CancellationToken cancellationToken)
	{
		DeleteStaleTemporary(_recordsPath);
		DeleteStaleTemporary(_orderPath);
		DeleteStaleTemporary(_selectionPath);
		DeleteStaleTemporary(_mutationPath);
		if (!File.Exists(_mutationPath)) return;
		WorkspaceMutation? mutation;
		await using (var stream = File.OpenRead(_mutationPath))
			mutation = await JsonSerializer.DeserializeAsync<WorkspaceMutation>(stream, JsonOptions, cancellationToken);
		if (mutation is null) throw new InvalidDataException("Workspace mutation journal is unreadable.");

		var records = await ReadRecordsAsync(cancellationToken);
		var order = await ReadOrderAsync(cancellationToken);
		if (mutation.Operation == WorkspaceMutationKind.Create)
		{
			var recordCommitted = records.TryGetValue(mutation.Workspace.Id, out var record) && record == mutation.Workspace;
			var orderCommitted = order.Contains(mutation.Workspace.Id);
			if (!recordCommitted || !orderCommitted)
			{
				records.Remove(mutation.Workspace.Id);
				order = order.Where(id => id != mutation.Workspace.Id).ToArray();
				await WriteJsonAtomicAsync(_recordsPath, records, cancellationToken);
				await WriteJsonAtomicAsync(_orderPath, order, cancellationToken);
			}
		}
		else
		{
			records.Remove(mutation.Workspace.Id);
			order = order.Where(id => id != mutation.Workspace.Id).ToArray();
			await WriteJsonAtomicAsync(_orderPath, order, cancellationToken);
			await WriteJsonAtomicAsync(_recordsPath, records, cancellationToken);
		}
		CompleteMutation();
	}

	private async Task<Dictionary<Guid, HarnessWorkspace>> ReadRecordsAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_recordsPath)) return [];
		await using var stream = File.OpenRead(_recordsPath);
		return await JsonSerializer.DeserializeAsync<Dictionary<Guid, HarnessWorkspace>>(stream, JsonOptions, cancellationToken) ?? [];
	}

	private async Task<Guid[]> ReadOrderAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_orderPath)) return [];
		await using var stream = File.OpenRead(_orderPath);
		return await JsonSerializer.DeserializeAsync<Guid[]>(stream, JsonOptions, cancellationToken) ?? [];
	}

	private async Task<WorkspaceSelection?> ReadSelectionAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_selectionPath)) return null;
		await using var stream = File.OpenRead(_selectionPath);
		return await JsonSerializer.DeserializeAsync<WorkspaceSelection>(stream, JsonOptions, cancellationToken);
	}

	private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var temporary = path + ".tmp";
		await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
		{
			await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
			await stream.FlushAsync(cancellationToken);
		}
		File.Move(temporary, path, true);
	}

	private void CompleteMutation()
	{
		if (File.Exists(_mutationPath)) File.Delete(_mutationPath);
	}

	private static void DeleteStaleTemporary(string path)
	{
		var temporary = path + ".tmp";
		if (File.Exists(temporary)) File.Delete(temporary);
	}

	private static string Canonical(string path)
	{
		var absolute = Path.GetFullPath(path);
		var absoluteRoot = Path.GetPathRoot(absolute);
		if (absoluteRoot is not null && string.Equals(absolute, absoluteRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			return absoluteRoot;
		var directory = new DirectoryInfo(absolute);
		var resolved = directory.Exists ? directory.ResolveLinkTarget(true)?.FullName : null;
		var fullPath = resolved ?? directory.FullName;
		var root = Path.GetPathRoot(fullPath);
		return root is not null && string.Equals(fullPath, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
			? root
			: fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
	private static string DirectoryName(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;
	private static bool PathsEqual(string left, string right) => string.Equals(Canonical(left), Canonical(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private enum WorkspaceMutationKind { Create, Delete }
	private sealed record WorkspaceMutation(WorkspaceMutationKind Operation, HarnessWorkspace Workspace);
}

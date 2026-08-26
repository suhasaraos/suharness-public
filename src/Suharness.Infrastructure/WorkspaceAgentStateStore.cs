using Suharness.Abstractions;
using Suharness.Tools;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Suharness.Infrastructure;

/// <summary>
/// Workspace-visible projections of SuHarness state. The transcript, indexes, and attachment store
/// remain authoritative; everything under .suharness can be regenerated after deletion or damage.
/// </summary>
public sealed class WorkspaceAgentStateStore(IWorkspaceIndex workspaceIndex, ISymbolIndex? symbolIndex = null) : IWorkspaceAgentStateStore
{
	public const int SchemaVersion = 1;
	public const int MaxKnowledgeNotes = 50;
	public const int MaxKnowledgeBytes = 256 * 1024;
	private const string DirectoryName = ".suharness";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
	private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(PathComparer);

	public async Task EnsureAsync(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		var paths = ResolvePaths(workspaceRoot);
		var gate = Gate(paths.Root);
		await gate.WaitAsync(cancellationToken);
		try
		{
			Directory.CreateDirectory(paths.Root);
			Directory.CreateDirectory(paths.Sessions);
			Directory.CreateDirectory(paths.Cache);
			Directory.CreateDirectory(paths.Temp);
			Directory.CreateDirectory(paths.Skills);
			if (!File.Exists(paths.Ignore))
			{
				await WriteTextAtomicAsync(paths, paths.Ignore,
					"# Generated SuHarness state; instructions and skills remain trackable.\n/.gitignore\n/index.json\n/architecture.md\n/knowledge.json\n/sessions/\n/cache/\n/temp/\n",
					cancellationToken);
			}
		}
		finally { gate.Release(); }
	}

	public async Task ProjectRepositoryAsync(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		await EnsureAsync(workspaceRoot, cancellationToken);
		var paths = ResolvePaths(workspaceRoot);
		var snapshot = workspaceIndex.Snapshot(paths.Workspace, cancellationToken);
		var files = snapshot.Files
			.Where(file => !file.Hidden && !file.Ignored && !UnderAgentDirectory(file.RelativePath))
			.OrderBy(file => file.RelativePath, StringComparer.Ordinal)
			.ToArray();
		var relativePaths = files.Select(file => file.RelativePath).ToArray();
		var projects = relativePaths.Where(IsProjectFile).ToArray();
		var configurations = relativePaths.Where(IsConfigurationFile).ToArray();
		var dependencies = relativePaths.Where(IsDependencyFile).ToArray();
		var entryPoints = relativePaths.Where(IsEntryPoint).Take(50).ToArray();
		var areas = relativePaths.Select(TopArea).Where(area => area is not null).GroupBy(area => area!, StringComparer.OrdinalIgnoreCase)
			.OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Take(30).Select(group => group.Key).ToArray();
		var kinds = ProjectKinds(relativePaths, projects);
		var generatedAt = DateTimeOffset.UtcNow;
		var manifestFiles = new List<RepositoryManifestFile>(files.Length);
		var symbolBudget = 10_000;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var symbols = symbolBudget > 0 && symbolIndex is not null && Kind(file.RelativePath) == "source"
				? symbolIndex.InFile(paths.Workspace, file.RelativePath, cancellationToken).Take(Math.Min(100, symbolBudget)).Select(symbol => new RepositoryManifestSymbol(symbol.Name, symbol.Kind, symbol.Line, symbol.Container)).ToArray()
				: [];
			symbolBudget -= symbols.Length;
			manifestFiles.Add(new RepositoryManifestFile(file.RelativePath, file.Size, new DateTimeOffset(file.ModifiedTicks, TimeSpan.Zero), Kind(file.RelativePath), symbols));
		}
		var manifest = new RepositoryManifest(
			SchemaVersion,
			paths.Workspace,
			generatedAt,
			snapshot.Partial,
			manifestFiles,
			projects,
			configurations,
			dependencies,
			entryPoints,
			areas,
			kinds,
			SkillCatalog.Discover(paths.Workspace).Select(skill => skill.Name).ToArray());
		var architecture = RenderArchitecture(manifest);
		var gate = Gate(paths.Root);
		await gate.WaitAsync(cancellationToken);
		try
		{
			await WriteJsonAtomicAsync(paths, paths.Index, manifest, cancellationToken);
			await WriteTextAtomicAsync(paths, paths.Architecture, architecture, cancellationToken);
			await EnsureKnowledgeStructureAsync(paths, manifest, cancellationToken);
		}
		finally { gate.Release(); }
	}

	public async Task ProjectSessionAsync(SessionTranscript transcript, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(transcript.Session.Workspace)) return;
		await ProjectRepositoryAsync(transcript.Session.Workspace, cancellationToken);
		await EnsureAsync(transcript.Session.Workspace, cancellationToken);
		var paths = ResolvePaths(transcript.Session.Workspace);
		var sessionRoot = SessionRoot(paths, transcript.Session.Id);
		Directory.CreateDirectory(sessionRoot);
		Directory.CreateDirectory(Path.Combine(sessionRoot, "summaries"));

		var latestUser = transcript.Events.LastOrDefault(item => item.Type == "message" && item.Role == "user")?.Content ?? transcript.Session.Title;
		var checkpoint = transcript.Events.LastOrDefault(item => item.Type == "compaction");
		var todo = transcript.Events.LastOrDefault(item => item.Type == "tool_call" && item.ToolName == "todo_write")?.ToolArguments;
		var workflow = LatestWorkflowResult(transcript.Events);
		var updatedAt = DateTimeOffset.UtcNow;
		var memory = new SessionMemoryProjection(
			SchemaVersion,
			transcript.Session.Id,
			latestUser,
			transcript.Session.PlanMode ? "planning" : workflow is null ? "standard" : WorkflowStatus(workflow.Value),
			checkpoint?.Content,
			transcript.Events.Count,
			updatedAt);
		var gate = Gate(paths.Root);
		await gate.WaitAsync(cancellationToken);
		try
		{
			await WriteJsonAtomicAsync(paths, Path.Combine(sessionRoot, "working-memory.json"), memory, cancellationToken);
			if (todo is not null && TryJson(todo, out var todoJson))
				await WriteJsonAtomicAsync(paths, Path.Combine(sessionRoot, "tasks.json"), new SessionTasksProjection(SchemaVersion, transcript.Session.Id, "todo_write", updatedAt, todoJson), cancellationToken);
			if (workflow is not null)
				await ProjectWorkflowCoreAsync(paths, transcript.Session.Id, workflow.Value, updatedAt, cancellationToken);
		}
		finally { gate.Release(); }
	}

	public async Task ProjectWorkflowProgressAsync(SessionTranscript transcript, string workflowJson, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(transcript.Session.Workspace) || !TryJson(workflowJson, out var workflow)) return;
		await EnsureAsync(transcript.Session.Workspace, cancellationToken);
		var paths = ResolvePaths(transcript.Session.Workspace);
		var gate = Gate(paths.Root);
		await gate.WaitAsync(cancellationToken);
		try { await ProjectWorkflowCoreAsync(paths, transcript.Session.Id, workflow, DateTimeOffset.UtcNow, cancellationToken); }
		finally { gate.Release(); }
	}

	public async Task DeleteSessionAsync(string workspaceRoot, Guid sessionId, CancellationToken cancellationToken = default)
	{
		var paths = ResolvePaths(workspaceRoot);
		var gate = Gate(paths.Root);
		await gate.WaitAsync(cancellationToken);
		try
		{
			var root = SessionRoot(paths, sessionId);
			if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		finally { gate.Release(); }
	}

	public async Task<string?> MirrorAttachmentAsync(
		string workspaceRoot,
		Guid sessionId,
		ImageAttachmentData attachment,
		CancellationToken cancellationToken = default)
	{
		await EnsureAsync(workspaceRoot, cancellationToken);
		var paths = ResolvePaths(workspaceRoot);
		var directory = Path.Combine(SessionRoot(paths, sessionId), "attachments");
		Directory.CreateDirectory(directory);
		var extension = attachment.Reference.MediaType switch
		{
			"image/png" => ".png",
			"image/jpeg" => ".jpg",
			"image/gif" => ".gif",
			_ => ".webp",
		};
		var stem = Sanitize(Path.GetFileNameWithoutExtension(attachment.Reference.Name));
		var name = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{attachment.Reference.Id[..12]}-{stem}{extension}";
		var destination = Contained(paths.Workspace, Path.Combine(directory, name));
		if (!File.Exists(destination)) await WriteBytesAtomicAsync(paths, destination, attachment.Bytes, cancellationToken);
		return Path.GetRelativePath(paths.Workspace, destination).Replace('\\', '/');
	}

	public async Task<WorkspaceAgentStatus> GetStatusAsync(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		var paths = ResolvePaths(workspaceRoot);
		DateTimeOffset? generatedAt = null;
		var notes = 0;
		var warnings = new List<string>();
		if (File.Exists(paths.Index))
		{
			var index = await ReadJsonAsync<RepositoryManifest>(paths.Index, cancellationToken);
			if (index is null) warnings.Add("index.json is unreadable and will be regenerated.");
			else generatedAt = index.GeneratedAt;
		}
		if (File.Exists(paths.Knowledge))
		{
			var knowledge = await ReadJsonAsync<KnowledgeDocument>(paths.Knowledge, cancellationToken);
			if (knowledge is null) warnings.Add("knowledge.json is unreadable and will be regenerated.");
			else notes = knowledge.Notes.Count;
		}
		return new WorkspaceAgentStatus(
			paths.Root,
			Directory.Exists(paths.Root),
			File.Exists(paths.Instructions) ? paths.Instructions : null,
			SkillCatalog.Discover(paths.Workspace).Count,
			generatedAt,
			notes,
			Directory.Exists(paths.Sessions) ? Directory.EnumerateDirectories(paths.Sessions).Count() : 0,
			warnings);
	}

	public async Task<WorkspaceAgentImportResult> ImportSuAgentAsync(string workspaceRoot, CancellationToken cancellationToken = default)
	{
		await EnsureAsync(workspaceRoot, cancellationToken);
		var paths = ResolvePaths(workspaceRoot);
		var source = Path.Combine(paths.Workspace, ".suagent");
		if (!Directory.Exists(source)) return new(false, 0, 0, 0, ["No .suagent directory exists in this workspace."]);
		var warnings = new List<string>();
		var instructions = false;
		var skills = 0;
		var notes = 0;
		var sessions = 0;
		var sourceInstructions = Path.Combine(source, "instructions.md");
		if (File.Exists(sourceInstructions) && !File.Exists(paths.Instructions))
		{
			await WriteTextAtomicAsync(paths, paths.Instructions, await File.ReadAllTextAsync(sourceInstructions, cancellationToken), cancellationToken);
			instructions = true;
		}
		var sourceSkills = Path.Combine(source, "skills");
		if (Directory.Exists(sourceSkills))
		{
			foreach (var file in Directory.EnumerateFiles(sourceSkills, "*.md", SearchOption.AllDirectories))
			{
				var relative = Path.GetRelativePath(sourceSkills, file);
				var destination = Contained(paths.Workspace, Path.Combine(paths.Skills, relative));
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				if (File.Exists(destination)) continue;
				await WriteTextAtomicAsync(paths, destination, await File.ReadAllTextAsync(file, cancellationToken), cancellationToken);
				skills++;
			}
		}
		var sourceKnowledge = Path.Combine(source, "knowledge.json");
		if (File.Exists(sourceKnowledge))
		{
			try
			{
				using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sourceKnowledge, cancellationToken));
				if (document.RootElement.TryGetProperty("notes", out var sourceNotes) && sourceNotes.ValueKind == JsonValueKind.Array)
				{
					foreach (var note in sourceNotes.EnumerateArray())
					{
						var text = String(note, "text");
						if (string.IsNullOrWhiteSpace(text)) continue;
						await AppendKnowledgeAsync(paths, new KnowledgeNote(String(note, "topic") ?? "Imported", text, String(note, "source") ?? ".suagent", DateTimeOffset.UtcNow), cancellationToken);
						notes++;
					}
				}
			}
			catch (JsonException) { warnings.Add(".suagent/knowledge.json is unreadable and was skipped."); }
		}
		var sourceSessions = Path.Combine(source, "sessions");
		if (Directory.Exists(sourceSessions))
		{
			foreach (var sourceSession in Directory.EnumerateDirectories(sourceSessions))
			{
				var destination = Path.Combine(paths.Sessions, Path.GetFileName(sourceSession));
				if (Directory.Exists(destination)) continue;
				CopyDirectory(sourceSession, destination);
				sessions++;
			}
		}
		return new(instructions, skills, notes, sessions, warnings);
	}

	private async Task ProjectWorkflowCoreAsync(Paths paths, Guid sessionId, JsonElement workflow, DateTimeOffset updatedAt, CancellationToken cancellationToken)
	{
		if (workflow.ValueKind != JsonValueKind.Object || !workflow.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array) return;
		var sessionRoot = SessionRoot(paths, sessionId);
		var summaries = Path.Combine(sessionRoot, "summaries");
		Directory.CreateDirectory(summaries);
		var taskProjection = new SessionTasksProjection(SchemaVersion, sessionId, "coding_workflow", updatedAt, workflow.Clone());
		await WriteJsonAtomicAsync(paths, Path.Combine(sessionRoot, "tasks.json"), taskProjection, cancellationToken);
		var findings = new List<SessionFinding>();
		var index = 0;
		foreach (var stage in stages.EnumerateArray())
		{
			index++;
			var role = String(stage, "role") ?? $"stage-{index}";
			var status = String(stage, "status") ?? "unknown";
			var summary = String(stage, "summary") ?? string.Empty;
			var handoff = stage.TryGetProperty("handoff", out var value) ? value : default;
			CollectFindings(role, status, summary, handoff, findings);
			var markdown = $"# {RoleLabel(role)}\n\nStatus: {status}\n\n## Outcome\n{summary}\n";
			if (handoff.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
				markdown += $"\n## Structured handoff\n```json\n{JsonSerializer.Serialize(handoff, JsonOptions)}\n```\n";
			await WriteTextAtomicAsync(paths, Path.Combine(summaries, $"{index:00}-{Sanitize(role)}.md"), markdown, cancellationToken);
		}
		await WriteJsonAtomicAsync(paths, Path.Combine(sessionRoot, "findings.json"), new SessionFindingsProjection(SchemaVersion, sessionId, updatedAt, findings), cancellationToken);
		if (String(workflow, "status")?.Equals("pass", StringComparison.OrdinalIgnoreCase) == true)
		{
			foreach (var finding in findings.Where(item => item.Role.StartsWith("explorer", StringComparison.OrdinalIgnoreCase) || item.Role.StartsWith("reviewer", StringComparison.OrdinalIgnoreCase)))
				await AppendKnowledgeAsync(paths, new KnowledgeNote(RoleLabel(finding.Role), finding.Text, sessionId.ToString("N"), updatedAt), cancellationToken);
		}
	}

	private async Task EnsureKnowledgeStructureAsync(Paths paths, RepositoryManifest manifest, CancellationToken cancellationToken)
	{
		var current = await ReadJsonAsync<KnowledgeDocument>(paths.Knowledge, cancellationToken);
		var document = new KnowledgeDocument(SchemaVersion, paths.Workspace, DateTimeOffset.UtcNow, manifest.ProjectKinds, manifest.MajorAreas, current?.Notes ?? []);
		await WriteJsonAtomicAsync(paths, paths.Knowledge, document, cancellationToken);
	}

	private async Task AppendKnowledgeAsync(Paths paths, KnowledgeNote note, CancellationToken cancellationToken)
	{
		var current = await ReadJsonAsync<KnowledgeDocument>(paths.Knowledge, cancellationToken)
			?? new KnowledgeDocument(SchemaVersion, paths.Workspace, DateTimeOffset.UtcNow, [], [], []);
		var notes = current.Notes.Where(existing =>
			!existing.Text.Equals(note.Text, StringComparison.OrdinalIgnoreCase)
			&& !(existing.Topic.Equals(note.Topic, StringComparison.OrdinalIgnoreCase) && existing.Source.Equals(note.Source, StringComparison.OrdinalIgnoreCase)))
			.Append(note).TakeLast(MaxKnowledgeNotes).ToArray();
		var updated = current with { UpdatedAt = DateTimeOffset.UtcNow, Notes = notes };
		var json = JsonSerializer.Serialize(updated, JsonOptions);
		while (Encoding.UTF8.GetByteCount(json) > MaxKnowledgeBytes && updated.Notes.Count > 1)
		{
			updated = updated with { Notes = updated.Notes.Skip(1).ToArray() };
			json = JsonSerializer.Serialize(updated, JsonOptions);
		}
		await WriteTextAtomicAsync(paths, paths.Knowledge, json, cancellationToken);
	}

	private static void CollectFindings(string role, string status, string summary, JsonElement handoff, List<SessionFinding> findings)
	{
		if (handoff.ValueKind == JsonValueKind.Object)
		{
			foreach (var property in new[] { "findings", "constraints", "risks" })
			{
				if (!handoff.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) continue;
				foreach (var value in values.EnumerateArray())
					if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
						findings.Add(new(role, status, property, value.GetString()!, Files(handoff), DateTimeOffset.UtcNow));
			}
		}
		if (findings.All(item => item.Role != role) && status is "fail" or "blocked" && summary.Length > 0)
			findings.Add(new(role, status, "summary", summary, [], DateTimeOffset.UtcNow));
	}

	private static IReadOnlyList<string> Files(JsonElement handoff)
	{
		foreach (var name in new[] { "files", "filesChanged" })
			if (handoff.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
				return values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
		return [];
	}

	private static JsonElement? LatestWorkflowResult(IReadOnlyList<SessionEvent> events)
	{
		for (var index = events.Count - 1; index >= 0; index--)
		{
			var result = events[index];
			if (result.Type != "tool_result" || result.ToolCallId is null) continue;
			if (!events.Any(call => call.Type == "tool_call" && call.ToolCallId == result.ToolCallId && call.ToolName == "coding_workflow")) continue;
			if (TryJson(result.Content, out var workflow)) return workflow;
		}
		return null;
	}

	private static string WorkflowStatus(JsonElement workflow) => String(workflow, "status") ?? "workflow";

	private static bool TryJson(string value, out JsonElement element)
	{
		try
		{
			using var document = JsonDocument.Parse(value);
			element = document.RootElement.Clone();
			return true;
		}
		catch (JsonException)
		{
			element = default;
			return false;
		}
	}

	private static string? String(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
	{
		if (!File.Exists(path)) return null;
		try
		{
			await using var stream = File.OpenRead(path);
			return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
		}
		catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException or UnauthorizedAccessException) { return null; }
	}

	private static async Task WriteJsonAtomicAsync<T>(Paths paths, string destination, T value, CancellationToken cancellationToken) =>
		await WriteTextAtomicAsync(paths, destination, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

	private static async Task WriteTextAtomicAsync(Paths paths, string destination, string content, CancellationToken cancellationToken)
	{
		Contained(paths.Workspace, destination);
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		Directory.CreateDirectory(paths.Temp);
		var temporary = Path.Combine(paths.Temp, $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
			File.Move(temporary, destination, overwrite: true);
		}
		finally { try { File.Delete(temporary); } catch (IOException) { } }
	}

	private static async Task WriteBytesAtomicAsync(Paths paths, string destination, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
	{
		Contained(paths.Workspace, destination);
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		Directory.CreateDirectory(paths.Temp);
		var temporary = Path.Combine(paths.Temp, $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken);
			File.Move(temporary, destination, overwrite: true);
		}
		finally { try { File.Delete(temporary); } catch (IOException) { } }
	}

	private SemaphoreSlim Gate(string root) => _gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));

	private static Paths ResolvePaths(string workspaceRoot)
	{
		var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException($"Workspace directory does not exist: {workspace}");
		var root = Contained(workspace, Path.Combine(workspace, DirectoryName));
		return new(workspace, root);
	}

	private static string Contained(string workspace, string path)
	{
		var full = Path.GetFullPath(path);
		var relative = Path.GetRelativePath(workspace, full);
		if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			throw new InvalidOperationException("The .suharness path escapes the workspace.");
		return full;
	}

	private static string SessionRoot(Paths paths, Guid sessionId) => Path.Combine(paths.Sessions, sessionId.ToString("N"));
	private static bool UnderAgentDirectory(string path) => path.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase) || path.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase);
	private static string? TopArea(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
	private static bool IsProjectFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".vbproj" or ".xcodeproj" || Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase);
	private static bool IsConfigurationFile(string path) => Path.GetFileName(path) is "appsettings.json" or "global.json" or "tsconfig.json" or "vite.config.ts" or "vite.config.js" || Path.GetExtension(path).ToLowerInvariant() is ".yml" or ".yaml" or ".toml";
	private static bool IsDependencyFile(string path) => Path.GetFileName(path).ToLowerInvariant() is "package.json" or "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or "requirements.txt" or "pyproject.toml" or "cargo.toml" or "go.mod" or "packages.config";
	private static bool IsEntryPoint(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant() is "program" or "main" or "app" or "index" || path.EndsWith("/Program.cs", StringComparison.OrdinalIgnoreCase);
	private static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".fs" or ".vb" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".rs" or ".go" or ".java" or ".cpp" or ".c" or ".h" ? "source" : IsConfigurationFile(path) ? "configuration" : "file";

	private static IReadOnlyList<string> ProjectKinds(IReadOnlyList<string> files, IReadOnlyList<string> projects)
	{
		var kinds = new List<string>();
		if (projects.Any(path => Path.GetExtension(path) is ".sln" or ".slnx")) kinds.Add(".NET solution");
		else if (projects.Any(path => Path.GetExtension(path).EndsWith("proj", StringComparison.OrdinalIgnoreCase))) kinds.Add(".NET projects");
		if (files.Any(path => path.Equals("package.json", StringComparison.OrdinalIgnoreCase))) kinds.Add("JavaScript/TypeScript project");
		if (files.Any(path => Path.GetFileName(path).Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("requirements.txt", StringComparison.OrdinalIgnoreCase))) kinds.Add("Python project");
		if (files.Any(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) && files.Any(path => path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))) kinds.Add("Unity project");
		if (kinds.Count == 0) kinds.Add("Software repository");
		return kinds;
	}

	private static string RenderArchitecture(RepositoryManifest manifest)
	{
		var builder = new StringBuilder();
		builder.AppendLine("# Repository architecture");
		builder.AppendLine();
		builder.AppendLine($"Generated: {manifest.GeneratedAt:O}");
		builder.AppendLine($"Workspace: `{manifest.RepositoryRoot}`");
		builder.AppendLine();
		builder.AppendLine("## Project kinds");
		foreach (var kind in manifest.ProjectKinds) builder.AppendLine($"- {kind}");
		builder.AppendLine();
		builder.AppendLine("## Major areas");
		foreach (var area in manifest.MajorAreas) builder.AppendLine($"- `{area}/`");
		builder.AppendLine();
		builder.AppendLine("## Entry points");
		foreach (var entry in manifest.EntryPoints) builder.AppendLine($"- `{entry}`");
		builder.AppendLine();
		builder.AppendLine("## Dependency manifests");
		foreach (var dependency in manifest.DependencyFiles) builder.AppendLine($"- `{dependency}`");
		if (manifest.Skills.Count > 0)
		{
			builder.AppendLine();
			builder.AppendLine("## Workspace skills");
			foreach (var skill in manifest.Skills) builder.AppendLine($"- `{skill}`");
		}
		builder.AppendLine();
		builder.AppendLine("This generated map is navigation metadata, not source truth. Inspect current source before changing it.");
		return builder.ToString();
	}

	private static string RoleLabel(string role) => string.Join(" · ", role.Split(':').Select(part => string.Join(' ', part.Split('-').Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]))));
	private static string Sanitize(string value)
	{
		var invalid = Path.GetInvalidFileNameChars().ToHashSet();
		var result = new string(value.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '-' : character).ToArray()).Trim(' ', '.');
		return result.Length == 0 ? "item" : result[..Math.Min(result.Length, 100)];
	}

	private static void CopyDirectory(string source, string destination)
	{
		Directory.CreateDirectory(destination);
		foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
		foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
	}

	private sealed record Paths(string Workspace, string Root)
	{
		public string Sessions => Path.Combine(Root, "sessions");
		public string Cache => Path.Combine(Root, "cache");
		public string Temp => Path.Combine(Root, "temp");
		public string Skills => Path.Combine(Root, "skills");
		public string Ignore => Path.Combine(Root, ".gitignore");
		public string Instructions => Path.Combine(Root, "instructions.md");
		public string Index => Path.Combine(Root, "index.json");
		public string Architecture => Path.Combine(Root, "architecture.md");
		public string Knowledge => Path.Combine(Root, "knowledge.json");
	}

	private sealed record RepositoryManifest(
		int SchemaVersion,
		string RepositoryRoot,
		DateTimeOffset GeneratedAt,
		bool Partial,
		IReadOnlyList<RepositoryManifestFile> Files,
		IReadOnlyList<string> ProjectFiles,
		IReadOnlyList<string> ConfigurationFiles,
		IReadOnlyList<string> DependencyFiles,
		IReadOnlyList<string> EntryPoints,
		IReadOnlyList<string> MajorAreas,
		IReadOnlyList<string> ProjectKinds,
		IReadOnlyList<string> Skills);
	private sealed record RepositoryManifestFile(string Path, long SizeBytes, DateTimeOffset ModifiedAt, string Kind, IReadOnlyList<RepositoryManifestSymbol> Symbols);
	private sealed record RepositoryManifestSymbol(string Name, string Kind, int Line, string? Container);
	private sealed record KnowledgeDocument(int SchemaVersion, string RepositoryRoot, DateTimeOffset UpdatedAt, IReadOnlyList<string> ProjectKinds, IReadOnlyList<string> Areas, IReadOnlyList<KnowledgeNote> Notes);
	private sealed record KnowledgeNote(string Topic, string Text, string Source, DateTimeOffset RecordedAt);
	private sealed record SessionTasksProjection(int SchemaVersion, Guid SessionId, string Source, DateTimeOffset UpdatedAt, JsonElement Tasks);
	private sealed record SessionMemoryProjection(int SchemaVersion, Guid SessionId, string Goal, string Phase, string? Checkpoint, int EventCount, DateTimeOffset UpdatedAt);
	private sealed record SessionFindingsProjection(int SchemaVersion, Guid SessionId, DateTimeOffset UpdatedAt, IReadOnlyList<SessionFinding> Findings);
	private sealed record SessionFinding(string Role, string Status, string Category, string Text, IReadOnlyList<string> Files, DateTimeOffset RecordedAt);
}

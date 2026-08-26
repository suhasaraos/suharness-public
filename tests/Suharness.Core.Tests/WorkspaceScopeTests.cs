using Suharness.Tools;

namespace Suharness.Core.Tests;

/// <summary>
/// The workspace sandbox. <see cref="WorkspaceScope"/> is what stands between a model and the rest
/// of the disk for every path-taking tool, and in two configurations it is the only thing standing
/// there: a ReadOnly session and the WorkflowCode/WorkflowRepair subagent profiles have no shell to
/// fall back on, so a path that escapes this check escapes entirely.
/// </summary>
public sealed class WorkspaceScopeTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "suharness-scope-" + Guid.NewGuid().ToString("N"));
	private readonly string _outside = Path.Combine(Path.GetTempPath(), "suharness-outside-" + Guid.NewGuid().ToString("N"));

	public WorkspaceScopeTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(_outside);
	}

	public void Dispose()
	{
		foreach (var directory in (string[])[_root, _outside])
		{
			try { Directory.Delete(directory, recursive: true); }
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
		}
	}

	private WorkspaceScope Scope() => new(_root);

	[Fact]
	public void RelativePathsResolveInsideTheWorkspace() =>
		Assert.Equal(Path.Combine(_root, "src", "App.cs"), Scope().Resolve("src/App.cs", "file_path"));

	[Theory]
	[InlineData("../escape.txt")]
	[InlineData("src/../../escape.txt")]
	public void TraversalIsRefused(string path) =>
		Assert.Throws<ToolInputException>(() => Scope().Resolve(path, "file_path"));

	[Fact]
	public void AnAbsolutePathOutsideTheWorkspaceIsRefused() =>
		Assert.Throws<ToolInputException>(() => Scope().Resolve(Path.Combine(_outside, "secret.txt"), "file_path"));

	/// <summary>
	/// A file symlink pointing out of the workspace. Path.GetFullPath normalises ".." but does not
	/// follow links, so without an explicit resolve this path passes containment and the tool then
	/// reads or writes the real target.
	/// </summary>
	[Fact]
	public void AFileLinkOutOfTheWorkspaceIsRefused()
	{
		var target = Path.Combine(_outside, "secret.txt");
		File.WriteAllText(target, "secret");
		var link = Path.Combine(_root, "innocent.txt");
		if (!TryCreateFileLink(link, target)) return;

		Assert.Throws<ToolInputException>(() => Scope().Resolve("innocent.txt", "file_path"));
	}

	/// <summary>
	/// A directory link, and a path *inside* one. The second case is what makes walking the whole
	/// ancestor chain necessary: the file itself is not a link, only the directory above it is.
	/// </summary>
	/// <remarks>
	/// An NTFS junction is used on Windows rather than a symlink. Both are reparse points and both
	/// escape the workspace identically, but creating a symlink needs Developer Mode or elevation
	/// while a junction needs neither — so this asserts on a real link on an ordinary machine instead
	/// of skipping and reporting success.
	/// </remarks>
	[Fact]
	public void ADirectoryLinkOutOfTheWorkspaceIsRefused()
	{
		Directory.CreateDirectory(Path.Combine(_outside, "nested"));
		File.WriteAllText(Path.Combine(_outside, "nested", "secret.txt"), "secret");
		var link = Path.Combine(_root, "vendor");
		Assert.True(TryCreateDirectoryLink(link, _outside), "the test could not create a directory link");

		Assert.Throws<ToolInputException>(() => Scope().Resolve("vendor", "file_path"));
		Assert.Throws<ToolInputException>(() => Scope().Resolve("vendor/nested/secret.txt", "file_path"));
	}

	/// <summary>A link that stays inside the workspace is ordinary and must keep working.</summary>
	[Fact]
	public void ALinkWithinTheWorkspaceIsAllowed()
	{
		var target = Path.Combine(_root, "real.txt");
		File.WriteAllText(target, "content");
		var link = Path.Combine(_root, "alias.txt");
		if (!TryCreateFileLink(link, target)) return;

		Assert.Equal(link, Scope().Resolve("alias.txt", "file_path"));
	}

	/// <summary>Full access is the mode that deliberately has no workspace boundary.</summary>
	[Fact]
	public void FullAccessFollowsLinksOutOfTheWorkspace()
	{
		var target = Path.Combine(_outside, "secret.txt");
		File.WriteAllText(target, "secret");
		var link = Path.Combine(_root, "innocent.txt");
		if (!TryCreateFileLink(link, target)) return;

		var scope = new WorkspaceScope(_root, allowOutsideRoot: true);
		Assert.Equal(link, scope.Resolve("innocent.txt", "file_path"));
	}

	// Creating a *symlink* needs Developer Mode or elevation on Windows. Where it is unavailable the
	// test reports success rather than failing for a reason that has nothing to do with the code —
	// which is why the directory case above uses a junction, and asserts instead of skipping.
	private static bool TryCreateFileLink(string link, string target) =>
		TryCreateLink(() => File.CreateSymbolicLink(link, target));

	private static bool TryCreateDirectoryLink(string link, string target) =>
		OperatingSystem.IsWindows()
			? TryCreateLink(() => CreateJunction(link, target)) || TryCreateLink(() => Directory.CreateSymbolicLink(link, target))
			: TryCreateLink(() => Directory.CreateSymbolicLink(link, target));

	/// <summary>Create an NTFS junction, which unlike a symlink needs no elevation.</summary>
	private static void CreateJunction(string link, string target)
	{
		using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
		{
			ArgumentList = { "/c", "mklink", "/J", link, target },
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		}) ?? throw new IOException("cmd.exe did not start");
		process.WaitForExit();
		if (process.ExitCode != 0 || !Directory.Exists(link)) throw new IOException("mklink /J failed");
	}

	private static bool TryCreateLink(Action create)
	{
		try
		{
			create();
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}
}

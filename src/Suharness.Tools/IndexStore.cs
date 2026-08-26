using System.Security.Cryptography;
using System.Text;

namespace Suharness.Tools;

/// <summary>
/// Where a workspace's retained tables live on disk. The file table and the symbol table share one
/// directory per workspace so they are discarded together, and the directory is derived from the
/// canonical workspace path rather than chosen by the caller.
/// </summary>
/// <remarks>
/// The store is always harness-owned. Writing an index into the workspace it describes would put
/// harness bookkeeping inside the user's own repository, which the snapshot store already refuses
/// to do for the same reason.
/// </remarks>
public static class IndexStore
{
	/// <summary>Characters of the path hash kept in the directory name.</summary>
	private const int HashLength = 16;

	/// <summary>
	/// The directory holding <paramref name="workspaceRoot"/>'s tables. The name carries a readable
	/// leaf so the store can be inspected by hand, and a hash so two workspaces with the same leaf
	/// name never collide. Callers must still confirm the recorded root before trusting a table.
	/// </summary>
	public static string DirectoryFor(string storeRoot, string workspaceRoot)
	{
		var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		var normalized = OperatingSystem.IsWindows() ? canonical.ToLowerInvariant() : canonical;
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..HashLength].ToLowerInvariant();
		var leaf = new string([.. Path.GetFileName(canonical).Where(char.IsAsciiLetterOrDigit)]);
		if (leaf.Length > 24) leaf = leaf[..24];
		return Path.Combine(Path.GetFullPath(storeRoot), leaf.Length == 0 ? hash : $"{leaf}-{hash}");
	}

	/// <summary>A path carrying a field or record separator cannot round-trip the tab-separated tables.</summary>
	public static bool Unwritable(string value) => value.AsSpan().ContainsAny('\t', '\n', '\r');

	/// <summary>Replace a table file in one move, so an interrupted write cannot leave a torn table.</summary>
	public static void WriteAtomic(string path, IEnumerable<string> lines)
	{
		var temporary = path + ".tmp";
		File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		File.Move(temporary, path, overwrite: true);
	}
}

namespace Suharness.Abstractions;

/// <summary>Where a symbol hit came from, which is also how much it can be trusted.</summary>
public enum SymbolSource
{
	/// <summary>
	/// A tolerant line scan of the source text. Language-agnostic, needs no build, and works on code
	/// that does not compile — at the cost of not resolving overloads, generics, or receivers.
	/// </summary>
	Declarations,

	/// <summary>A configured language server answered, so the position is semantically resolved.</summary>
	LanguageServer,
}

/// <summary>One declaration the harness can navigate to by name.</summary>
/// <param name="Line">One-based line of the declaration.</param>
/// <param name="Container">Enclosing namespace, module, or type when the scanner could infer one.</param>
public sealed record SymbolHit(
	string Name,
	string Kind,
	string RelativePath,
	int Line,
	string? Container,
	SymbolSource Source);

/// <summary>
/// A name-to-declaration table built from the same walk the workspace index performs. It is the
/// entry point the semantic tools lacked: <c>definition</c> and <c>references</c> can only answer
/// questions about a position, and this is what turns a name into one.
/// </summary>
public interface ISymbolIndex
{
	/// <summary>True when at least one file in the workspace has a language the scanner understands.</summary>
	bool IsAvailable(string workspaceRoot, CancellationToken cancellationToken = default);

	/// <summary>
	/// Rank declarations against <paramref name="query"/>: exact match first, then case-insensitive
	/// exact, prefix, contains, and finally subsequence.
	/// </summary>
	IReadOnlyList<SymbolHit> Find(
		string workspaceRoot,
		string query,
		string? kind = null,
		int limit = 50,
		CancellationToken cancellationToken = default);

	/// <summary>Every declaration in one file, in source order. Used to resolve a name to a position.</summary>
	IReadOnlyList<SymbolHit> InFile(
		string workspaceRoot,
		string relativePath,
		CancellationToken cancellationToken = default);
}

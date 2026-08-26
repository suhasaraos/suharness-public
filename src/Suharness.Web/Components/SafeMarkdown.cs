using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Suharness.Web.Components;

/// <summary>
/// Renders markdown that SuHarness did not write — assistant text, tool results, and MCP server
/// output — into HTML that is safe to place in the page.
/// </summary>
/// <remarks>
/// <para>
/// Two hazards have to be closed, and Markdig closes only the first on its own:
/// </para>
/// <list type="number">
/// <item>
/// Raw HTML in the source. <c>DisableHtml</c> escapes it, so <c>&lt;img src=x onerror=...&gt;</c>
/// renders as visible text rather than as an element.
/// </item>
/// <item>
/// Dangerous URL schemes on links and images. Markdig passes a destination through verbatim, so
/// <c>[click](javascript:...)</c> becomes a live <c>javascript:</c> anchor. Nothing in the default
/// pipeline filters it, and a page with no authentication in front of a shell tool is the wrong
/// place to render an attacker-chosen URL.
/// </item>
/// </list>
/// <para>
/// The second is handled here by walking the parsed document and rewriting every link and image
/// destination whose scheme is not on the allow list. Rewriting rather than deleting keeps the
/// surrounding text intact, so a blocked link still reads as written and simply goes nowhere.
/// </para>
/// </remarks>
public static class SafeMarkdown
{
	/// <summary>Destination substituted for a link or image whose scheme is not allowed.</summary>
	/// <remarks>
	/// <c>about:blank</c> is inert in every browser, and the fragment makes a blocked destination
	/// obvious to anyone inspecting the rendered page.
	/// </remarks>
	public const string BlockedUrl = "about:blank#blocked-by-suharness";

	/// <summary>Schemes permitted on a hyperlink.</summary>
	private static readonly string[] LinkSchemes = ["http", "https", "mailto"];

	/// <summary>
	/// Schemes permitted on an image. Narrower than <see cref="LinkSchemes"/>: <c>mailto</c> is
	/// meaningless for an image, and <c>data:</c> stays out so a document cannot smuggle an
	/// arbitrary payload past the loader.
	/// </summary>
	private static readonly string[] ImageSchemes = ["http", "https"];

	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.UseAdvancedExtensions()
		.DisableHtml()
		.Build();

	/// <summary>Render <paramref name="markdown"/> to HTML with unsafe destinations neutralised.</summary>
	public static string ToHtml(string? markdown)
	{
		if (string.IsNullOrEmpty(markdown)) return string.Empty;

		var document = Markdown.Parse(markdown, Pipeline);
		foreach (var link in document.Descendants<LinkInline>())
		{
			if (!IsAllowed(link.Url, link.IsImage)) link.Url = BlockedUrl;
		}

		// An autolink carries its own destination and never becomes a LinkInline.
		foreach (var autolink in document.Descendants<AutolinkInline>())
		{
			var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
			if (!IsAllowed(url, isImage: false)) autolink.Url = BlockedUrl;
		}

		return Markdown.ToHtml(document, Pipeline);
	}

	/// <summary>Whether <paramref name="url"/> may be used as a link destination.</summary>
	public static bool IsAllowedLink(string? url) => IsAllowed(url, isImage: false);

	/// <summary>Whether <paramref name="url"/> may be used as an image source.</summary>
	public static bool IsAllowedImage(string? url) => IsAllowed(url, isImage: true);

	private static bool IsAllowed(string? url, bool isImage)
	{
		var scheme = SchemeOf(url);
		// No scheme means a relative, root-relative, or fragment destination. It can only resolve
		// against this origin, so there is nothing to filter.
		if (scheme is null) return true;
		return (isImage ? ImageSchemes : LinkSchemes).Contains(scheme, StringComparer.Ordinal);
	}

	/// <summary>
	/// Extract the lower-cased URL scheme, or <see langword="null"/> when the destination carries
	/// none and is therefore relative.
	/// </summary>
	/// <remarks>
	/// Browsers strip leading and trailing C0 control characters and spaces from a URL, and strip
	/// tab, newline, and carriage return from anywhere inside it, before parsing the scheme.
	/// <c>"java&#9;script:alert(1)"</c> and <c>"javascript:alert(1)"</c> both navigate, so the same
	/// normalisation has to happen here or the allow list is trivially bypassed.
	/// </remarks>
	private static string? SchemeOf(string? url)
	{
		if (string.IsNullOrWhiteSpace(url)) return null;

		Span<char> buffer = url.Length <= 256 ? stackalloc char[url.Length] : new char[url.Length];
		var length = 0;
		foreach (var character in url)
		{
			if (character is '\t' or '\n' or '\r') continue;
			buffer[length++] = character;
		}

		var normalised = buffer[..length];
		while (!normalised.IsEmpty && IsStrippedLeading(normalised[0])) normalised = normalised[1..];
		while (!normalised.IsEmpty && normalised[^1] <= ' ') normalised = normalised[..^1];
		if (normalised.IsEmpty) return null;

		// A scheme runs to the first ':'. A '/', '?', or '#' before that colon means the colon
		// belongs to a path, query, or fragment, so there is no scheme.
		var colon = -1;
		for (var index = 0; index < normalised.Length; index++)
		{
			var character = normalised[index];
			if (character is '/' or '?' or '#') return null;
			if (character == ':') { colon = index; break; }
		}
		if (colon <= 0) return null;

		// RFC 3986: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ). Anything else is not a
		// scheme, which makes the destination relative and safe.
		var candidate = normalised[..colon];
		if (!char.IsAsciiLetter(candidate[0])) return null;
		foreach (var character in candidate[1..])
		{
			if (!char.IsAsciiLetterOrDigit(character) && character is not ('+' or '-' or '.')) return null;
		}

		return candidate.ToString().ToLowerInvariant();
	}

	/// <summary>
	/// Whether a leading character is one the browser discards before it parses the scheme.
	/// </summary>
	/// <remarks>
	/// C0 controls and space are stripped by the URL parser. U+FFFD is included because CommonMark
	/// requires a parser to replace U+0000 with U+FFFD, so by the time a destination reaches this
	/// method a NUL written by the author already looks like a replacement character. The browser
	/// still sees the original NUL and strips it, so treating U+FFFD the same way here keeps the
	/// two in agreement instead of leaving a gap between what was written and what is checked.
	/// </remarks>
	private static bool IsStrippedLeading(char character) => character <= ' ' || character == '\uFFFD';
}

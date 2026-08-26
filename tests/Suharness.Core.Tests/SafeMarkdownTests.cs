using Suharness.Web.Components;

namespace Suharness.Core.Tests;

/// <summary>
/// Markdown reaching the UI is written by the model, by tool results, and by MCP servers, none of
/// which SuHarness controls. These cover the two ways that text could otherwise become script in a
/// page that sits in front of a shell tool with no authentication.
/// </summary>
public sealed class SafeMarkdownTests
{
	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("JavaScript:alert(1)")]
	[InlineData("JAVASCRIPT:alert(1)")]
	[InlineData("vbscript:msgbox(1)")]
	[InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
	[InlineData("file:///etc/passwd")]
	public void BlocksDangerousLinkSchemes(string url)
	{
		var html = SafeMarkdown.ToHtml($"[click]({url})");

		Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("vbscript:", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(SafeMarkdown.BlockedUrl, html, StringComparison.Ordinal);
		// The visible text survives; only the destination is replaced.
		Assert.Contains(">click<", html, StringComparison.Ordinal);
	}

	[Theory]
	// A browser strips tab, newline, and carriage return from anywhere inside a URL before it
	// parses the scheme, so each of these navigates exactly like a plain "javascript:" URL.
	[InlineData("java\tscript:alert(1)")]
	[InlineData("java\nscript:alert(1)")]
	[InlineData("java\rscript:alert(1)")]
	[InlineData("  javascript:alert(1)")]
	[InlineData("\0javascript:alert(1)")]
	[InlineData("\u000Bjavascript:alert(1)")]
	public void BlocksObfuscatedJavascriptScheme(string url)
	{
		Assert.False(SafeMarkdown.IsAllowedLink(url));

		// Markdig refuses to linkify some of these at all, which is equally safe. What must never
		// appear is a live anchor carrying a scripting scheme, so that is what is asserted rather
		// than the absence of the payload text, which may legitimately survive as escaped prose.
		var html = SafeMarkdown.ToHtml($"[click]({url})");
		Assert.DoesNotContain("href=\"javascript", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("href=\"vbscript", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("href=\"data:", html, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("https://example.com/page")]
	[InlineData("http://example.com/page")]
	[InlineData("mailto:someone@example.com")]
	[InlineData("/relative/path")]
	[InlineData("relative/path")]
	[InlineData("#fragment")]
	[InlineData("./sibling.md")]
	[InlineData("?query=1")]
	public void AllowsOrdinaryDestinations(string url)
	{
		Assert.True(SafeMarkdown.IsAllowedLink(url));
		Assert.DoesNotContain(SafeMarkdown.BlockedUrl, SafeMarkdown.ToHtml($"[link]({url})"), StringComparison.Ordinal);
	}

	[Fact]
	public void ColonInPathIsNotMistakenForAScheme()
	{
		// "notes/a:b" has a colon but no scheme: the slash comes first, so it is a relative path.
		Assert.True(SafeMarkdown.IsAllowedLink("notes/a:b"));
		Assert.True(SafeMarkdown.IsAllowedLink("#a:b"));
	}

	[Fact]
	public void ImagesRejectSchemesThatLinksAllow()
	{
		// mailto: and data: are meaningless or dangerous as an image source even though a link may
		// legitimately use mailto:.
		Assert.False(SafeMarkdown.IsAllowedImage("mailto:someone@example.com"));
		Assert.False(SafeMarkdown.IsAllowedImage("data:image/png;base64,iVBORw0KGgo="));
		Assert.True(SafeMarkdown.IsAllowedImage("https://example.com/a.png"));
		Assert.True(SafeMarkdown.IsAllowedImage("/attachments/abc"));

		var html = SafeMarkdown.ToHtml("![i](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)");
		Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void RawHtmlIsEscapedRatherThanRendered()
	{
		var html = SafeMarkdown.ToHtml("<img src=x onerror=alert(1)>\n\n<script>alert(2)</script>");

		Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("&lt;img", html, StringComparison.Ordinal);
	}

	[Fact]
	public void ReferenceStyleLinksAreFilteredToo()
	{
		var html = SafeMarkdown.ToHtml("[click][ref]\n\n[ref]: javascript:alert(1)");

		Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
		Assert.Contains(SafeMarkdown.BlockedUrl, html, StringComparison.Ordinal);
	}

	[Fact]
	public void OrdinaryMarkdownStillRenders()
	{
		var html = SafeMarkdown.ToHtml("# Heading\n\nSome **bold** text and `code`.\n\n- one\n- two");

		Assert.Contains("<h1", html, StringComparison.Ordinal);
		Assert.Contains("<strong>bold</strong>", html, StringComparison.Ordinal);
		Assert.Contains("<code>code</code>", html, StringComparison.Ordinal);
		Assert.Contains("<li>one</li>", html, StringComparison.Ordinal);
	}

	[Fact]
	public void FencedCodeSurvivesUntouched()
	{
		var html = SafeMarkdown.ToHtml("```csharp\nvar x = \"javascript:alert(1)\";\n```");

		// Inside a code fence the text is content, not a destination: it must render, escaped,
		// rather than being rewritten.
		Assert.Contains("javascript:alert(1)", html, StringComparison.Ordinal);
		Assert.DoesNotContain("<a href", html, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void EmptyInputRendersNothing(string? markdown) =>
		Assert.Equal(string.Empty, SafeMarkdown.ToHtml(markdown));
}

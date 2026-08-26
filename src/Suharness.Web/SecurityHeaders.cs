using System.Security.Cryptography;

namespace Suharness.Web;

/// <summary>
/// Response-header hardening for the browser UI.
/// </summary>
/// <remarks>
/// <para>
/// SuHarness runs a shell tool with the signed-in user's privileges and puts no authentication in
/// front of it, on the assumption that the only reachable client is the person who started it (see
/// SECURITY.md). These headers are the second line behind that assumption: they keep a page from
/// another origin from framing the UI, keep the browser from acting on a smuggled script, and keep
/// the content types the UI loads to the short list it actually uses.
/// </para>
/// <para>
/// The policy is deliberately strict about script. A per-response nonce is minted here and read
/// back by <c>App.razor</c> for the one inline script the Blazor host page emits — the import map —
/// so <c>script-src</c> never needs <c>'unsafe-inline'</c>. That matters directly: without it, a
/// <c>javascript:</c> URL that slipped past <see cref="Components.SafeMarkdown"/> would still run.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
	/// <summary>Key under which the per-response CSP nonce is published on <c>HttpContext.Items</c>.</summary>
	public const string NonceItemKey = "suharness.csp-nonce";

	/// <summary>Read the nonce minted for the current response, if the middleware has run.</summary>
	public static string? NonceFor(HttpContext? context) =>
		context?.Items.TryGetValue(NonceItemKey, out var value) == true ? value as string : null;

	/// <summary>Mint a nonce, publish it, and stamp the security headers on every response.</summary>
	public static IApplicationBuilder UseSuharnessSecurityHeaders(this IApplicationBuilder app) =>
		app.Use(async (context, next) =>
		{
			var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
			context.Items[NonceItemKey] = nonce;

			// Applied as the response starts rather than on the way in. The Razor Components
			// endpoint adds a frame-ancestors policy of its own; two Content-Security-Policy
			// headers are merged by the browser as an intersection, which is safe but leaves the
			// effective policy split across two lines and awkward to reason about. Assigning last
			// replaces it, so what ships is exactly the policy written here.
			context.Response.OnStarting(state =>
			{
				var response = (HttpResponse)state;
				var headers = response.Headers;
				headers["Content-Security-Policy"] = BuildPolicy(nonce, response.HttpContext.Request.Host);
				headers["X-Content-Type-Options"] = "nosniff";
				headers["X-Frame-Options"] = "DENY";
				headers["Referrer-Policy"] = "no-referrer";
				headers["Cross-Origin-Opener-Policy"] = "same-origin";
				headers["Cross-Origin-Resource-Policy"] = "same-origin";
				headers["Permissions-Policy"] =
					"accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
				return Task.CompletedTask;
			}, context.Response);

			await next();
		});

	/// <summary>
	/// Hosts the socket clause may name. SuHarness binds to loopback and the CLI refuses any other
	/// <c>--host</c>, so anything else in the Host header did not come from how this server was
	/// started.
	/// </summary>
	private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

	private static string BuildPolicy(string nonce, HostString host)
	{
		// Blazor Server drives the UI over a SignalR WebSocket back to this same origin. Browsers
		// have not been consistent about whether 'self' covers a ws:// upgrade of an http:// page,
		// so the socket origin is named explicitly rather than relied upon.
		//
		// The Host header is written by the client, so it is not interpolated as received. It is
		// accepted only when it names the loopback address this server is reachable on, and encoded
		// on the way out; a header from anywhere else contributes no clause at all and 'self' stands.
		// Kestrel already rejects CR/LF, so this is not about header splitting — it is that a request
		// must not be able to widen the policy of its own response.
		var socket = host.HasValue && LoopbackHosts.Contains(host.Host, StringComparer.OrdinalIgnoreCase)
			? $" ws://{host.ToUriComponent()} wss://{host.ToUriComponent()}"
			: string.Empty;

		return string.Join("; ",
		[
			"default-src 'self'",
			"base-uri 'self'",
			"object-src 'none'",
			"frame-ancestors 'none'",
			"frame-src 'none'",
			"form-action 'self'",
			$"script-src 'self' 'nonce-{nonce}'",
			// Google Fonts serves the face declarations from googleapis and the files from gstatic.
			"style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
			"font-src 'self' https://fonts.gstatic.com",
			// data: covers the SVG background encoded inline in app.css; attachments are same-origin.
			"img-src 'self' data:",
			$"connect-src 'self'{socket}",
			"worker-src 'self'",
			"manifest-src 'self'",
		]);
	}
}

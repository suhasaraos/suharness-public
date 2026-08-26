using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Suharness.Abstractions;

/// <summary>
/// Stable, provider-neutral failure classes for a model request. Recovery routes on this code and
/// never on the message text, so a
/// provider that rewords its errors cannot silently change which failures are retried.
/// </summary>
public enum ModelFailureCode
{
	/// <summary>The provider could not be reached at all — DNS, TLS, socket, or a dropped connection.</summary>
	Transport,

	/// <summary>The request went silent for longer than the provider's idle timeout and was abandoned.</summary>
	Timeout,

	/// <summary>The provider returned 5xx.</summary>
	Server,

	/// <summary>The provider is throttling this key. Transient by definition, so always worth retrying.</summary>
	RateLimit,

	/// <summary>
	/// The call finished normally but produced no content at all. Local servers emit this degenerate
	/// completion often enough to matter; treating it as success ends the turn with nothing for the
	/// reader or the loop to act on, so it is classified as a failure that is safe to repeat.
	/// </summary>
	EmptyResponse,

	/// <summary>The request was rejected because its context window was exceeded.</summary>
	ContextWindowExceeded,

	/// <summary>No credential was supplied, or the provider rejected the one that was.</summary>
	Auth,

	/// <summary>The account's quota, balance, or credit is exhausted. Retrying cannot fix it.</summary>
	Quota,

	/// <summary>The provider rejected the request as malformed or unsupported.</summary>
	InvalidRequest,

	/// <summary>Nothing in the failure identified a known class.</summary>
	Unknown,
}

/// <summary>One classified model-request failure, with the provider's own retry hint when it sent one.</summary>
/// <param name="ProviderRetryAfter">
/// A <c>Retry-After</c> the provider asked for. It replaces local backoff when it fits under the
/// policy ceiling; an over-ceiling instruction is not obeyed, because a provider asking for an hour
/// would otherwise stall a turn indefinitely.
/// </param>
public sealed record ModelFailure(
	ModelFailureCode Code,
	string Message,
	TimeSpan? ProviderRetryAfter = null);

/// <summary>
/// Turns a thrown provider error into a <see cref="ModelFailure"/>. The wording patterns account for
/// context-window and quota errors across providers, which
/// exist because OpenAI-compatible servers disagree on both status code and error code for these two
/// cases and only agree on roughly how they word them.
/// </summary>
public static partial class ModelFailureClassifier
{
	/// <summary>Structured codes and plain phrases that explicitly name a context bound being exceeded.</summary>
	[GeneratedRegex(
		@"(?:^|[^a-z0-9])context[\s_-](?:length|window)[\s_-](?:exceed(?:ed|s)?|overflow(?:ed)?|limit[\s_-]exceeded)(?:$|[^a-z0-9])",
		RegexOptions.IgnoreCase)]
	private static partial Regex StructuredContextOverflow();

	[GeneratedRegex(@"\b(?:maximum|max)(?:\s+(?:allowed|supported))?\s+context\s+(?:length|window)\b", RegexOptions.IgnoreCase)]
	private static partial Regex MaximumContext();

	/// <summary>Request-size wording that ties "too large" directly to model context capacity.</summary>
	[GeneratedRegex(
		@"\b(?:request|prompt|input|messages?)\s+(?:is\s+|are\s+)?too\s+(?:large|long)\s+for\s+(?:(?:this|the)\s+)?(?:model(?:'s)?\s+)?context(?:\s+window)?\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex TooLargeForContext();

	[GeneratedRegex(@"\b(?:input|prompt|request)\s+(?:is\s+)?too\s+(?:long|large)\s+for\s+(?:this|the)\s+model\b", RegexOptions.IgnoreCase)]
	private static partial Regex TooLongForModel();

	/// <summary>"Exceeds" wording is safe only when its object is explicitly the model context.</summary>
	[GeneratedRegex(
		@"\b(?:input|prompt|request|messages?)\b.{0,40}\b(?:exceed(?:s|ed)?|overflows?|is\s+larger\s+than)\b.{0,40}\b(?:the\s+)?(?:model(?:'s)?\s+)?context(?:\s+(?:length|window))?\b",
		RegexOptions.IgnoreCase)]
	private static partial Regex ExceedsModelContext();

	[GeneratedRegex(@"\binsufficient[\s_-]+(?:quota|balance|credits?)\b", RegexOptions.IgnoreCase)]
	private static partial Regex InsufficientQuota();

	[GeneratedRegex(@"\b(?:quota|usage[\s_-]+limit)[\s_-]+(?:exceeded|exhausted|reached)\b", RegexOptions.IgnoreCase)]
	private static partial Regex QuotaExceeded();

	[GeneratedRegex(@"\bexceed(?:ed|s)?[\s_-]+(?:(?:your|the)[\s_-]+)?(?:current[\s_-]+)?quota\b", RegexOptions.IgnoreCase)]
	private static partial Regex ExceedsQuota();

	[GeneratedRegex(@"\b(?:balance|credits?)[\s_-]+(?:exhausted|depleted)\b", RegexOptions.IgnoreCase)]
	private static partial Regex BalanceExhausted();

	[GeneratedRegex(@"\bout[\s_-]+of[\s_-]+(?:credits?|budget)\b", RegexOptions.IgnoreCase)]
	private static partial Regex OutOfCredits();

	/// <summary>An HTTP status quoted in an error message, which is how most client libraries report one.</summary>
	[GeneratedRegex(@"\bHTTP\s+(\d{3})\b", RegexOptions.IgnoreCase)]
	private static partial Regex QuotedStatus();

	/// <summary>Whether the detail identifies a request that exceeded the model's context window.</summary>
	public static bool IsContextWindowExceeded(string detail) =>
		StructuredContextOverflow().IsMatch(detail)
		|| MaximumContext().IsMatch(detail)
		|| TooLargeForContext().IsMatch(detail)
		|| TooLongForModel().IsMatch(detail)
		|| ExceedsModelContext().IsMatch(detail);

	/// <summary>Whether the detail identifies an exhausted account quota rather than a rate limit.</summary>
	public static bool IsQuotaExceeded(string detail) =>
		InsufficientQuota().IsMatch(detail)
		|| QuotaExceeded().IsMatch(detail)
		|| ExceedsQuota().IsMatch(detail)
		|| BalanceExhausted().IsMatch(detail)
		|| OutOfCredits().IsMatch(detail);

	/// <summary>
	/// Classify a thrown provider error. The whole exception chain is rendered first, because the
	/// transport libraries in play routinely wrap the informative failure inside a bland outer one
	/// ("An error occurred while sending the request").
	/// </summary>
	public static ModelFailure Classify(Exception exception)
	{
		var detail = Chain(exception);
		var status = Status(exception) ?? QuotedStatusOf(detail);

		// Wording is checked before status: a context overflow and a malformed request are both 400,
		// and only the overflow is repairable, so the distinction cannot come from the code alone.
		if (IsContextWindowExceeded(detail)) return new(ModelFailureCode.ContextWindowExceeded, detail);
		if (IsQuotaExceeded(detail)) return new(ModelFailureCode.Quota, detail);
		// OpenRouter can return HTTP 200 with finish_reason "error" when generation failed upstream.
		// The OpenAI adapter rejects that extension while converting its closed finish-reason enum,
		// before the provider's failure can reach us in structured form. Retrying is safe: the response
		// contains no completed assistant message or tool call, and the provider explicitly said the
		// generation failed rather than stopped normally.
		if (detail.Contains("Unknown ChatFinishReason value", StringComparison.OrdinalIgnoreCase)
			&& detail.Contains("Actual value was error", StringComparison.OrdinalIgnoreCase))
		{
			return new(ModelFailureCode.Server, detail);
		}

		if (status is not null)
		{
			var retryAfter = RetryAfter(exception);
			if (status == 429) return new(ModelFailureCode.RateLimit, detail, retryAfter);
			if (status is 401 or 403) return new(ModelFailureCode.Auth, detail);
			if (status is 408 or 504) return new(ModelFailureCode.Timeout, detail, retryAfter);
			if (status >= 500) return new(ModelFailureCode.Server, detail, retryAfter);
			if (status >= 400) return new(ModelFailureCode.InvalidRequest, detail);
		}

		if (detail.Contains("Missing Authentication header", StringComparison.OrdinalIgnoreCase))
			return new(ModelFailureCode.Auth, detail);

		// The idle-timeout guard in ModelProviderProfile surfaces as this wording, and a plain
		// TaskCanceledException with no cancellation requested is how HttpClient reports its timeout.
		if (detail.Contains("exceeded the configured timeout", StringComparison.OrdinalIgnoreCase)
			|| Unwrap(exception).Any(item => item is TimeoutException)
			|| Unwrap(exception).Any(item => item is TaskCanceledException))
		{
			return new(ModelFailureCode.Timeout, detail);
		}

		if (Unwrap(exception).Any(item => item is HttpRequestException or SocketException or IOException))
			return new(ModelFailureCode.Transport, detail);

		return new(ModelFailureCode.Unknown, detail);
	}

	/// <summary>The exception and every exception it wraps, outermost first.</summary>
	private static IEnumerable<Exception> Unwrap(Exception exception)
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			yield return current;
			if (current is not AggregateException aggregate) continue;
			foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap)) yield return inner;
		}
	}

	/// <summary>
	/// Render the exception with its whole cause chain, outermost first, skipping a cause that only
	/// repeats its wrapper. This is diagnostic text: classify on the returned code, never by
	/// re-parsing this string outside the classifier.
	/// </summary>
	public static string Chain(Exception exception)
	{
		var builder = new StringBuilder();
		// Exception does not override Equals, so the default comparer is reference identity, which is
		// exactly the cycle check a cause chain needs.
		var seen = new HashSet<Exception>();
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (!seen.Add(current)) break;
			var message = current.Message.ReplaceLineEndings(" ").Trim();
			if (message.Length == 0) message = current.GetType().Name;
			if (builder.Length > 0)
			{
				if (builder.ToString().EndsWith(message, StringComparison.Ordinal)) continue;
				builder.Append(": ");
			}
			builder.Append(message);
		}
		return builder.ToString();
	}

	/// <summary>The HTTP status carried by the exception itself, when the client library attached one.</summary>
	private static int? Status(Exception exception)
	{
		foreach (var item in Unwrap(exception))
		{
			if (item is HttpRequestException { StatusCode: { } code }) return (int)code;
			// The OpenAI client throws its own request-failed type carrying a Status property.
			var property = item.GetType().GetProperty("Status");
			if (property?.GetValue(item) is int status and >= 100 and < 600) return status;
		}
		return null;
	}

	private static int? QuotedStatusOf(string detail)
	{
		var match = QuotedStatus().Match(detail);
		return match.Success && int.TryParse(match.Groups[1].Value, out var status) ? status : null;
	}

	/// <summary>A provider's own Retry-After hint, when one rode along on the failure.</summary>
	private static TimeSpan? RetryAfter(Exception exception)
	{
		foreach (var item in Unwrap(exception))
		{
			if (item.Data["Retry-After"] is TimeSpan span) return span;
			if (item.Data["Retry-After"] is int seconds and > 0) return TimeSpan.FromSeconds(seconds);
		}
		return null;
	}
}

/// <summary>How aggressively one provider route retries a failed model request.</summary>
public enum RetryMode
{
	/// <summary>Retry only the configured transient codes, up to a finite budget.</summary>
	Normal,

	/// <summary>Retry every model-request failure without an attempt limit, until success or cancellation.</summary>
	Always,
}

/// <summary>
/// A provider route's model-request retry policy. The defaults absorb common transient failures
/// twice with bounded exponential backoff and jitter.
/// </summary>
/// <remarks>
/// <see cref="RetryMode.Always"/> exists for local servers: a model being loaded into VRAM refuses
/// requests with codes that are indistinguishable from permanent ones, and the useful behaviour
/// there is to keep waiting rather than to fail the turn.
/// </remarks>
public sealed record RetryPolicy(
	RetryMode Mode = RetryMode.Normal,
	int MaxRetries = RetryPolicy.DefaultMaxRetries,
	IReadOnlyList<ModelFailureCode>? RetryableCodes = null,
	int InitialDelayMs = RetryPolicy.DefaultInitialDelayMs,
	int MaxDelayMs = RetryPolicy.DefaultMaxDelayMs,
	double JitterRatio = RetryPolicy.DefaultJitterRatio)
{
	public const int DefaultMaxRetries = 2;
	public const int DefaultInitialDelayMs = 500;
	public const int DefaultMaxDelayMs = 10_000;
	public const double DefaultJitterRatio = 0.1;

	/// <summary>
	/// The codes normal mode retries by default. Each one describes an attempt that produced nothing
	/// durable, which is what makes repeating it safe.
	/// </summary>
	public static readonly IReadOnlyList<ModelFailureCode> DefaultRetryableCodes =
	[
		ModelFailureCode.EmptyResponse,
		ModelFailureCode.RateLimit,
		ModelFailureCode.Server,
		ModelFailureCode.Timeout,
		ModelFailureCode.Transport,
	];

	/// <summary>The policy in force when a provider configures none.</summary>
	public static readonly RetryPolicy Default = new();

	/// <summary>The codes this policy retries, defaults applied.</summary>
	public IReadOnlyList<ModelFailureCode> Codes => RetryableCodes ?? DefaultRetryableCodes;

	/// <summary>
	/// Reject a policy that cannot behave as written, at the point it is read rather than at the
	/// failure it was supposed to handle.
	/// </summary>
	public RetryPolicy Validated()
	{
		if (MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(MaxRetries), "maxRetries must not be negative.");
		if (InitialDelayMs <= 0) throw new ArgumentOutOfRangeException(nameof(InitialDelayMs), "initialDelayMs must be positive.");
		if (MaxDelayMs <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDelayMs), "maxDelayMs must be positive.");
		if (InitialDelayMs > MaxDelayMs)
			throw new ArgumentOutOfRangeException(nameof(InitialDelayMs), "initialDelayMs must not exceed maxDelayMs.");
		if (JitterRatio is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(JitterRatio), "jitterRatio must be between 0 and 1.");
		if (Mode == RetryMode.Normal && Codes.Count == 0)
			throw new ArgumentException("retryableCodes must not be empty in normal mode.", nameof(RetryableCodes));
		return this;
	}

	/// <summary>
	/// Whether a failure is eligible for another attempt. <paramref name="retriesUsed"/> counts
	/// retries already spent on this turn, so the first failure arrives with zero.
	/// </summary>
	public bool ShouldRetry(ModelFailureCode code, int retriesUsed) => Mode switch
	{
		RetryMode.Always => true,
		_ => retriesUsed < MaxRetries && Codes.Contains(code),
	};

	/// <summary>
	/// How long to wait before the next attempt: bounded exponential backoff with symmetric jitter.
	/// A provider's own hint replaces local backoff when it fits under the ceiling, without jitter —
	/// jittering an instruction the provider gave is how a rate limit gets hit a second time.
	/// </summary>
	public TimeSpan Delay(int retriesUsed, TimeSpan? providerRetryAfter, Random? random = null)
	{
		if (providerRetryAfter is { } hint && hint > TimeSpan.Zero && hint.TotalMilliseconds <= MaxDelayMs) return hint;

		var exponential = Math.Min((double)MaxDelayMs, InitialDelayMs * Math.Pow(2, Math.Max(0, retriesUsed)));
		if (JitterRatio <= 0) return TimeSpan.FromMilliseconds(exponential);

		var spread = exponential * JitterRatio;
		var offset = ((random ?? Random.Shared).NextDouble() * 2 - 1) * spread;
		return TimeSpan.FromMilliseconds(Math.Clamp(exponential + offset, 0, MaxDelayMs));
	}
}

/// <summary>
/// Resolves the retry policy in force for a session, which in practice means the policy configured
/// on the provider route it is about to call. Kept separate from the model configuration service so
/// the runtime can be given a policy in a test without a provider directory behind it.
/// </summary>
public interface IRetryPolicyProvider
{
	ValueTask<RetryPolicy> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

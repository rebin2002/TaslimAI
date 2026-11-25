namespace Taslim.Api.AcademicSources;

/// <summary>
/// Coarse, safe error detail. Carries only an HTTP status and a failure category — never a
/// provider payload, credential, or model identifier — so it is safe to persist in job
/// diagnostics. The concrete provider is identified internally by exception site only.
/// </summary>
public sealed record AcademicProviderErrorDetails(int? HttpStatusCode, string FailureCategory);

/// <summary>Base class for academic discovery failures. Messages are deliberately generic:
/// nothing user-visible names the underlying data provider.</summary>
public abstract class AcademicSourceException(string message, AcademicProviderErrorDetails? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AcademicProviderErrorDetails? Details { get; } = details;
}

public sealed class AcademicSourceUnavailableException(AcademicProviderErrorDetails? details = null, Exception? inner = null)
    : AcademicSourceException("The academic source service is temporarily unavailable.", details, inner);

public sealed class AcademicSourceRateLimitedException(AcademicProviderErrorDetails? details = null)
    : AcademicSourceException("The academic source service is busy. Please retry shortly.", details);

public sealed class AcademicSourceTimeoutException(AcademicProviderErrorDetails? details = null)
    : AcademicSourceException("The academic source service took too long to respond.", details
        ?? new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Timeout));

/// <summary>The request was refused by the endpoint (4xx) or blocked by the safety boundary.</summary>
public sealed class AcademicSourceRejectedException(AcademicProviderErrorDetails details, Exception? inner = null)
    : AcademicSourceException("The academic source request could not be completed.", details, inner);

/// <summary>The endpoint answered but the payload was unusable (bad JSON, wrong type, oversized).</summary>
public sealed class AcademicSourceMalformedException(AcademicProviderErrorDetails? details = null, Exception? inner = null)
    : AcademicSourceException("The academic source service returned an unexpected response.", details, inner);

public static class AcademicSourceFailureCategories
{
    public const string RateLimited = "rate_limited";
    public const string Timeout = "timeout";
    public const string Transient = "transient";
    public const string Rejected = "rejected";
    public const string BlockedEndpoint = "blocked_endpoint";
    /// <summary>The host is allow-listed but the transport is not: the endpoint, or a
    /// provider-issued redirect hop, would be reached over plaintext HTTP. Refused before any
    /// request is made to that address, because a scholarly read carries the reader's search term
    /// in the query string and the Google Books read additionally carries the deployment's
    /// configured key there — a plaintext hop publishes precisely what the allow list exists to
    /// keep private. Kept distinct from <see cref="BlockedEndpoint"/> so an operator can tell a
    /// host they never authorized apart from a host they did, over a transport they did not.</summary>
    public const string InsecureTransport = "insecure_transport";
    public const string Malformed = "malformed";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnexpectedContentType = "unexpected_content_type";
    public const string NotFound = "not_found";
    /// <summary>A provider-issued redirect carried a <c>Location</c> that names no address the
    /// gateway can dial: <c>//</c> and <c>//:80/x</c> parse as relative URIs, and resolving either
    /// one against the request URL throws. The header is the provider's defect rather than the
    /// reader's mistake or an outage, so it surfaces as a categorized refusal instead of an
    /// unlabelled <see cref="UriFormatException"/> escaping into an HTTP 500.</summary>
    public const string MalformedRedirect = "malformed_redirect";
    public const string InvalidIdentifier = "invalid_identifier";
    public const string Configuration = "configuration";
    public const string Unknown = "unknown";
}

using System.Net;
using System.Text.Json;

namespace Taslim.Api.AcademicSources;

/// <summary>A bounded JSON payload fetched from an allow-listed provider endpoint.</summary>
public sealed record AcademicJsonResponse(int StatusCode, JsonElement Root, bool IsSuccess)
{
    /// <summary>True for pass-through statuses (e.g. 404) where no usable body is expected.</summary>
    public bool IsPassThroughStatus => !IsSuccess && Root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
}

/// <summary>
/// Shared transport for scholarly provider reads. Enforces, in this order: endpoint allow
/// listing (no automatic redirects — each hop is revalidated), per-request deadline separate
/// from the caller's cancellation, bounded response size and JSON content type, retry with
/// provider-supplied <c>Retry-After</c> clamped to a configured ceiling, and failure
/// categorization that keeps provider payloads out of exceptions and logs. The backing
/// <see cref="HttpClient"/> must be configured with automatic redirects disabled and an infinite
/// <see cref="HttpClient.Timeout"/>, and its transport must be the pinned handler from
/// <see cref="AcademicSourceConnectionPolicy.CreatePinnedHandler"/> so each new connection —
/// including the one for every hop — resolves and dials a public address. All budgets are applied
/// here.
/// </summary>
public sealed class AcademicHttpGateway(HttpClient httpClient, AcademicSourceDiscoveryOptions options)
{
    private readonly IReadOnlyCollection<string> allowedHosts = options.AllowedEndpointHosts();
    private readonly IReadOnlyCollection<string> hostExceptions = options.EndpointHostExceptions;
    /// <summary>Whether plaintext reads are refused. True for every runtime that was configured by
    /// a deployment; false only where a test injected its own transport, because that switch has no
    /// public setter and the options binder cannot reach it.</summary>
    private readonly bool requireSecureTransport = !options.AllowInsecureProviderEndpoints;

    /// <summary>Query keys that carry provider credentials and must never cross an authority.</summary>
    private static readonly string[] CredentialQueryKeys =
        ["key", "apikey", "api_key", "access_token", "token", "secret", "client_secret", "subscription", "code", "sig"];

    /// <summary>Headers that must never be re-sent to a different authority.</summary>
    private static readonly string[] CredentialHeaders = ["Authorization", "Cookie", "X-Goog-Api-Key", "X-Api-Key"];

    public async Task<AcademicJsonResponse> GetJsonAsync(
        Uri endpoint,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? passThroughStatusCodes = null)
    {
        if (!options.Enabled)
            throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Configuration));
        ValidateEndpoint(endpoint);

        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(options.OperationTimeoutSeconds));

        var attempt = 0;
        var current = endpoint;
        var redirects = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(operationTimeout.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);

                if ((int)response.StatusCode is >= 301 and <= 308)
                {
                    var location = response.Headers.Location;
                    var redirectStatus = (int)response.StatusCode;
                    // A redirect's body is never read, and every decision below can throw or restart
                    // the loop: take the two values the decision needs, then release the response and
                    // its connection before any of them runs. Each individual throw path used to
                    // dispose, which is how `RequireSecureTransport` below came to be the one call
                    // that could leave a ResponseHeadersRead response — body stream and pooled
                    // connection alike — alive until the finalizer.
                    response.Dispose();
                    response = null;
                    if (++redirects > options.MaxRedirectHops)
                        throw Reject(current, "too_many_redirects", redirectStatus);
                    // Resolving the hop now happens after the release, for the reason above plus one
                    // the release-after-decision ordering missed: `Location: //` and `Location:
                    // //:80/x` parse as *relative* URIs, and turning either into an absolute address
                    // against the request URL throws. That throw used to sit a line above the
                    // dispose, so an unparseable header both kept the payload stream alive and
                    // escaped the gateway as a bare UriFormatException — which the reader experienced
                    // as an unlabelled HTTP 500 instead of a refusal the orchestrator can categorize.
                    var target = ResolveRedirectTarget(current, location, redirectStatus);
                    if (!AcademicSourceUrlGuard.IsAllowedEndpoint(target, allowedHosts, hostExceptions))
                        throw Reject(current, AcademicSourceFailureCategories.BlockedEndpoint, redirectStatus);
                    // Same host is not the same place. `Authority` is host and port only and says
                    // nothing about TLS, so an https → http hop on an allow-listed host read as
                    // "same authority" and was followed with the query string intact — the reader's
                    // search term, and for Google Books the configured key, in clear text. The hop's
                    // transport is therefore judged before anything is sent to it: the general rule
                    // first (plaintext is for an injected test transport only), then the one case
                    // that no transport may perform, which is stepping DOWN from TLS on a host the
                    // request was already talking to over TLS.
                    RequireSecureTransport(target!);
                    var sameAuthority = string.Equals(target!.Authority, current.Authority, StringComparison.OrdinalIgnoreCase);
                    var sameScheme = string.Equals(target.Scheme, current.Scheme, StringComparison.OrdinalIgnoreCase);
                    if (!sameScheme && sameAuthority && IsSecureScheme(current.Scheme))
                        throw Reject(current, AcademicSourceFailureCategories.InsecureTransport, redirectStatus);
                    // A hop that leaves the original authority could carry the request's
                    // credentials — including a Google Books key that rides the query string — to a
                    // host the operator never associated with them. Same-authority hops are the
                    // normal provider behaviour and stay untouched; a cross-authority hop is
                    // refused unless explicitly configured, and when allowed it is rebuilt without
                    // any credential parameter. A scheme change on the SAME host is not a new
                    // authority: the only one left after the rule above is an upgrade into TLS,
                    // which keeps its query, because moving a credential onto a better transport is
                    // not the leak this branch is guarding.
                    if (!sameAuthority)
                    {
                        // HttpClient merges the client's default request headers into every send,
                        // so a credential configured on the client cannot be stripped for one hop.
                        // Refusing that hop is the only honest guarantee, and it is why the default
                        // for cross-authority redirects stays off.
                        if (!options.AllowCrossHostRedirects || CarriesClientCredentials())
                            throw Reject(current, AcademicSourceFailureCategories.BlockedEndpoint, null);
                        current = ScrubCredentials(target);
                        continue;
                    }
                    current = target!;
                    continue;
                }

                if (passThroughStatusCodes is not null && passThroughStatusCodes.Contains((int)response.StatusCode))
                {
                    // The adapter asked to see this status itself — a DOI 404 is an answer, not an
                    // outage — but the body was never going to be parsed here. With
                    // ResponseHeadersRead the payload stream and the pooled connection stay live
                    // until something disposes the response, so releasing them is part of returning
                    // the status: a ten-identifier DOI batch that all miss used to hand back up to
                    // ten undisposed responses per request. Status semantics are unchanged; only the
                    // lifetime is.
                    var passThroughStatus = (int)response.StatusCode;
                    response.Dispose();
                    response = null;
                    return new AcademicJsonResponse(passThroughStatus, default, false);
                }

                if (IsRetryableStatus(response.StatusCode) && attempt < options.MaxAttempts)
                {
                    // Read the wait, release the response, then sleep — in that order. This delay is
                    // the one await in the loop that the *caller* is allowed to cut short (a tab
                    // closed, a newer search, the student navigating away), and a cancellation that
                    // arrives inside it leaves this method through the caller's own
                    // OperationCanceledException, which no catch below turns into a disposal: the
                    // catch that does is filtered to "the caller did not cancel". Sleeping on a
                    // response that is still held therefore used to keep a provider's 429 payload
                    // stream and pooled connection alive for the rest of a `Retry-After` window on a
                    // page nobody is waiting for any more.
                    var retryDelaySeconds = RetryDelaySeconds(attempt, response);
                    response.Dispose();
                    response = null;
                    await DelayBeforeRetryAsync(retryDelaySeconds, operationTimeout.Token, cancellationToken);
                    continue;
                }

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        throw new AcademicSourceRateLimitedException(Details((int)response.StatusCode, AcademicSourceFailureCategories.RateLimited));
                    if (!response.IsSuccessStatusCode)
                    {
                        // A retryable status that survived the attempt budget is an outage, not a
                        // request rejection.
                        if (IsRetryableStatus(response.StatusCode))
                            throw new AcademicSourceUnavailableException(Details((int)response.StatusCode, AcademicSourceFailureCategories.Transient));
                        throw Reject(current, StatusCategory(response.StatusCode), (int)response.StatusCode);
                    }

                    EnsureJsonContent(response);
                    var json = await ReadBoundedAsStringAsync(response.Content, operationTimeout.Token);
                    try
                    {
                        using var document = JsonDocument.Parse(json);
                        return new AcademicJsonResponse((int)response.StatusCode, document.RootElement.Clone(), true);
                    }
                    catch (JsonException exception)
                    {
                        throw new AcademicSourceMalformedException(Details((int)response.StatusCode, AcademicSourceFailureCategories.Malformed), exception);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw new AcademicSourceTimeoutException();
            }
            catch (HttpRequestException) when (attempt < options.MaxAttempts)
            {
                response?.Dispose();
                response = null;
                await DelayBeforeRetryAsync(RetryDelaySeconds(attempt, null), operationTimeout.Token, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                response?.Dispose();
                throw new AcademicSourceUnavailableException(new AcademicProviderErrorDetails(null, AcademicSourceFailureCategories.Transient), exception);
            }
            finally
            {
                // The releases above are placed where they are because their timing matters; this one
                // is here so the next branch added to this loop cannot silently reintroduce the leak
                // class they were fixing. Every path out of the try — return, throw, continue — runs
                // it, and a response that was already released is a no-op the second time.
                response?.Dispose();
            }
        }
    }

    /// <summary>
    /// Turns a provider's <c>Location</c> header into the address a hop would use, or refuses the
    /// header. Absent header stays null and is refused by the allow list as it always was; a header
    /// that parses as a relative URI with no resolvable form — the network-path reference <c>//</c>,
    /// or <c>//:80/x</c> — is refused here with a category of its own instead of throwing a bare
    /// <see cref="UriFormatException"/> out of the transport. Only the status the redirect arrived
    /// with is carried, never the header's text: a provider-issued address is not something to echo.
    /// </summary>
    private static Uri? ResolveRedirectTarget(Uri current, Uri? location, int redirectStatus)
    {
        if (location is null) return null;
        if (location.IsAbsoluteUri) return location;
        try
        {
            return new Uri(current, location);
        }
        catch (UriFormatException exception)
        {
            throw new AcademicSourceRejectedException(
                Details(redirectStatus, AcademicSourceFailureCategories.MalformedRedirect), exception);
        }
    }

    private void ValidateEndpoint(Uri endpoint)
    {
        if (!AcademicSourceUrlGuard.IsAllowedEndpoint(endpoint, allowedHosts, hostExceptions))
            throw new AcademicSourceRejectedException(Details(endpoint, AcademicSourceFailureCategories.BlockedEndpoint, null));
        RequireSecureTransport(endpoint);
    }

    /// <summary>
    /// The transport rule, applied to the first request and to every hop: a scholarly read goes out
    /// over TLS. The host allow list is not enough on its own — <c>www.googleapis.com</c> is
    /// allow-listed whether the scheme is <c>https</c> or <c>http</c>, and the query string of every
    /// one of these requests carries the reader's search term, with the Google Books read carrying
    /// the deployment's configured key as well. So the scheme is decided here, before anything is
    /// sent, rather than left to whatever the provider's <c>Location</c> header suggested.
    /// <para>
    /// A test transport that switched plaintext on may still not carry a credential in the clear:
    /// that combination has no legitimate use and is refused the same way, so the escape hatch
    /// cannot become the shape that leaks a key.
    /// </para>
    /// </summary>
    private void RequireSecureTransport(Uri endpoint)
    {
        if (IsSecureScheme(endpoint.Scheme)) return;
        if (requireSecureTransport || CarriesCredentialQuery(endpoint))
            throw new AcademicSourceRejectedException(Details(endpoint, AcademicSourceFailureCategories.InsecureTransport, null));
    }

    private static bool IsSecureScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the query string carries a parameter named like a provider credential.
    /// Uses the same key list as the cross-authority scrub so the two cannot drift.</summary>
    private static bool CarriesCredentialQuery(Uri endpoint)
    {
        var pairs = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        return pairs.Any(pair => IsCredentialParameter(pair));
    }

    /// <summary>
    /// Rebuilds a redirect target without credential-bearing query parameters. Nothing is dropped
    /// for a same-authority hop; this runs only when the target host or port changed.
    /// </summary>
    internal static Uri ScrubCredentials(Uri target)
    {
        var pairs = target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length == 0) return target;
        var kept = pairs.Where(pair => !IsCredentialParameter(pair)).ToArray();
        if (kept.Length == pairs.Length) return target;
        var rebuilt = target.GetLeftPart(UriPartial.Path) + (kept.Length > 0 ? "?" + string.Join('&', kept) : string.Empty);
        return new Uri(rebuilt, UriKind.Absolute);
    }

    private static bool IsCredentialParameter(string pair)
    {
        var index = pair.IndexOf('=');
        var key = index < 0 ? pair : pair[..index];
        return CredentialQueryKeys.Contains(Uri.UnescapeDataString(key), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True when the composed client would attach a credential to any host it is told to reach.</summary>
    private bool CarriesClientCredentials()
    {
        if (httpClient.DefaultRequestHeaders.Authorization is not null) return true;
        foreach (var header in CredentialHeaders)
        {
            if (header == "Authorization") continue;
            if (httpClient.DefaultRequestHeaders.Contains(header)) return true;
        }
        return false;
    }

    private AcademicSourceRejectedException Reject(Uri endpoint, string category, int? status) =>
        new(Details(endpoint, category, status));

    private static AcademicProviderErrorDetails Details(string category) => new(null, category);
    private static AcademicProviderErrorDetails Details(Uri? endpoint, string category, int? status) => new(status, category);
    private static AcademicProviderErrorDetails Details(int status, string category) => new(status, category);

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string StatusCategory(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType => AcademicSourceFailureCategories.Rejected,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AcademicSourceFailureCategories.Configuration,
        HttpStatusCode.NotFound => AcademicSourceFailureCategories.NotFound,
        HttpStatusCode.RequestedRangeNotSatisfiable => AcademicSourceFailureCategories.Rejected,
        >= HttpStatusCode.InternalServerError => AcademicSourceFailureCategories.Transient,
        _ => AcademicSourceFailureCategories.Unknown,
    };

    private int RetryDelaySeconds(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter is { } retryAfter)
        {
            var seconds = retryAfter.Delta is { } delta
                ? (int)Math.Ceiling(delta.TotalSeconds)
                : retryAfter.Date is { } date ? (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds) : (int?)null;
            if (seconds is not null) return Math.Clamp(seconds.Value, 0, options.MaxRetryDelaySeconds);
        }
        var backoff = options.BackoffBaseMilliseconds * Math.Pow(2, attempt - 1);
        return (int)Math.Ceiling(Math.Min(backoff / 1000d, options.MaxRetryDelaySeconds));
    }

    private static async Task DelayBeforeRetryAsync(int seconds, CancellationToken operationToken, CancellationToken callerToken)
    {
        if (seconds <= 0) return;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, callerToken);
        await Task.Delay(TimeSpan.FromSeconds(seconds), delayCancellation.Token);
    }

    private void EnsureJsonContent(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return;
        if (mediaType is "application/json" or "text/json") return;
        throw new AcademicSourceMalformedException(Details(AcademicSourceFailureCategories.UnexpectedContentType));
    }

    private async Task<string> ReadBoundedAsStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var total = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > options.MaxResponseBytes)
                throw new AcademicSourceMalformedException(Details(AcademicSourceFailureCategories.PayloadTooLarge));
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>Builds absolute provider endpoints from configured bases; query input never controls the host.</summary>
public static class AcademicEndpoint
{
    public static Uri Create(string baseUrl, string path, IEnumerable<KeyValuePair<string, string?>>? query = null)
    {
        var builder = new UriBuilder(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute));
        builder.Path = new Uri(builder.Uri, path.TrimStart('/')).AbsolutePath;
        var pairs = (query ?? []).Where(pair => pair.Value is not null).ToArray();
        if (pairs.Length > 0)
            builder.Query = string.Join('&', pairs.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
        return builder.Uri;
    }
}

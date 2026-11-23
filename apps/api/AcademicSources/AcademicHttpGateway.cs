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
                    var target = location is null ? null : location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (++redirects > options.MaxRedirectHops)
                    {
                        response.Dispose();
                        throw Reject(current, "too_many_redirects", (int)response.StatusCode);
                    }
                    if (!AcademicSourceUrlGuard.IsAllowedEndpoint(target, allowedHosts, hostExceptions))
                    {
                        response.Dispose();
                        throw Reject(current, AcademicSourceFailureCategories.BlockedEndpoint, (int)response.StatusCode);
                    }
                    // A hop that leaves the original authority could carry the request's
                    // credentials — including a Google Books key that rides the query string — to a
                    // host the operator never associated with them. Same-authority hops are the
                    // normal provider behaviour and stay untouched; a cross-authority hop is
                    // refused unless explicitly configured, and when allowed it is rebuilt without
                    // any credential parameter.
                    if (!string.Equals(target!.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
                    {
                        response.Dispose();
                        response = null;
                        // HttpClient merges the client's default request headers into every send,
                        // so a credential configured on the client cannot be stripped for one hop.
                        // Refusing that hop is the only honest guarantee, and it is why the default
                        // for cross-authority redirects stays off.
                        if (!options.AllowCrossHostRedirects || CarriesClientCredentials())
                            throw Reject(current, AcademicSourceFailureCategories.BlockedEndpoint, null);
                        current = ScrubCredentials(target);
                        continue;
                    }
                    response.Dispose();
                    response = null;
                    current = target!;
                    continue;
                }

                if (passThroughStatusCodes is not null && passThroughStatusCodes.Contains((int)response.StatusCode))
                    return new AcademicJsonResponse((int)response.StatusCode, default, false);

                if (IsRetryableStatus(response.StatusCode) && attempt < options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(RetryDelaySeconds(attempt, response), operationTimeout.Token, cancellationToken);
                    response.Dispose();
                    response = null;
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
        }
    }

    private void ValidateEndpoint(Uri endpoint)
    {
        if (!AcademicSourceUrlGuard.IsAllowedEndpoint(endpoint, allowedHosts, hostExceptions))
            throw new AcademicSourceRejectedException(Details(endpoint, AcademicSourceFailureCategories.BlockedEndpoint, null));
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

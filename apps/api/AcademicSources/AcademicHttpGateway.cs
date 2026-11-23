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
/// <see cref="HttpClient"/> must be configured with <see cref="HttpClientHandler.AllowAutoRedirect"/>
/// disabled and an infinite <see cref="HttpClient.Timeout"/>; all budgets are applied here.
/// </summary>
public sealed class AcademicHttpGateway(HttpClient httpClient, AcademicSourceDiscoveryOptions options)
{
    private readonly IReadOnlyCollection<string> allowedHosts = options.AllowedEndpointHosts();

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
                    if (!AcademicSourceUrlGuard.IsAllowedEndpoint(target, allowedHosts))
                    {
                        response.Dispose();
                        throw Reject(current, AcademicSourceFailureCategories.BlockedEndpoint, (int)response.StatusCode);
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
        if (!AcademicSourceUrlGuard.IsAllowedEndpoint(endpoint, allowedHosts))
            throw new AcademicSourceRejectedException(Details(endpoint, AcademicSourceFailureCategories.BlockedEndpoint, null));
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

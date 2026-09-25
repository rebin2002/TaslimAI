using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

/// <summary>
/// Runway Dev adapter. It deliberately exposes only the provider-neutral movie contract;
/// model names, task identifiers, URLs, and provider error bodies stay server-side.
/// </summary>
public sealed class RunwayMovieVideoProvider(
    HttpClient httpClient,
    IOptions<MovieVideoOptions> options,
    ILogger<RunwayMovieVideoProvider> logger) : IMovieVideoProvider
{
    private const string RunwayVersion = "2024-11-06";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly IReadOnlyCollection<string> Operations =
        [MovieStudioOperations.QuickMovie, MovieStudioOperations.SceneClip];

    private readonly MovieVideoOptions settings = options.Value;

    public string Key => "runway";
    public bool IsAvailable => settings.Enabled
        && string.Equals(settings.ProviderKey, Key, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(settings.ApiKey)
        && !string.IsNullOrWhiteSpace(settings.Model)
        && TryGetBaseUri(out _);
    public IReadOnlyCollection<string> SupportedOperations => Operations;

    public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        var payload = BuildPayload(request, out var endpoint);
        var response = await SendJsonWithRetryAsync(HttpMethod.Post, endpoint, payload, cancellationToken);
        var task = Deserialize<RunwayTaskResponse>(response) ?? throw Failure();
        if (!Guid.TryParse(task.Id, out _)) throw Failure();

        logger.LogInformation("Runway movie task submitted. Operation={Operation}; DurationSeconds={DurationSeconds}", request.Operation, request.DurationSeconds);
        return new MovieVideoSubmission(task.Id);
    }

    public async Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (!Guid.TryParse(providerJobId, out _)) throw Failure();
        var response = await SendJsonWithRetryAsync(
            HttpMethod.Get,
            new Uri(GetBaseUri(), $"tasks/{Uri.EscapeDataString(providerJobId)}"),
            null,
            cancellationToken);
        var task = Deserialize<RunwayTaskResponse>(response) ?? throw Failure();
        if (!string.Equals(task.Id, providerJobId, StringComparison.OrdinalIgnoreCase)) throw Failure();

        var status = MapStatus(task);
        return status with
        {
            SafeMetadataJson = BuildSafeMetadata(task, status.Status),
        };
    }

    public async Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (status.Status != MovieVideoProviderJobStatus.Succeeded) throw Failure();
        var metadata = Deserialize<RunwayOutputMetadata>(status.MetadataJson ?? string.Empty) ?? throw new MovieVideoProviderOutputException();
        if (!Guid.TryParse(providerJobId, out _) || !Guid.TryParse(metadata.TaskId, out _)
            || !string.Equals(providerJobId, metadata.TaskId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(metadata.OutputUrl)
            || !Uri.TryCreate(metadata.OutputUrl, UriKind.Absolute, out var outputUri)
            || outputUri.Scheme != Uri.UriSchemeHttps)
            throw new MovieVideoProviderOutputException();

        var contentType = NormalizeVideoContentType(metadata.ContentType, outputUri);
        var fileName = NormalizeFileName(metadata.FileName, outputUri);
        var sizeBytes = metadata.SizeBytes.GetValueOrDefault();
        if (sizeBytes <= 0 || !IsVideoContentType(metadata.ContentType))
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, outputUri);
            HttpResponseMessage headResponse;
            try { headResponse = await SendAsync(head, cancellationToken); }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Runway output headers could not be retrieved without exposing provider response details.");
                throw new MovieVideoProviderOutputException();
            }
            using (headResponse)
            {
                if (!headResponse.IsSuccessStatusCode) throw new MovieVideoProviderOutputException();
                sizeBytes = headResponse.Content.Headers.ContentLength.GetValueOrDefault();
                contentType = NormalizeVideoContentType(headResponse.Content.Headers.ContentType?.MediaType, outputUri);
            }
        }
        if (sizeBytes <= 0 || sizeBytes > MaxOutputBytes()) throw new MovieVideoProviderOutputException();

        async Task<Stream> OpenReadAsync(CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, outputUri);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, token);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Runway output retrieval failed without exposing provider response details.");
                throw Failure();
            }

            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw Failure();
            }

            var responseType = response.Content.Headers.ContentType?.MediaType;
            var responseLength = response.Content.Headers.ContentLength;
            if (!IsVideoContentType(responseType) || responseLength is <= 0 || responseLength > MaxOutputBytes() || responseLength != sizeBytes)
            {
                response.Dispose();
                throw new MovieVideoProviderOutputException();
            }

            var stream = await response.Content.ReadAsStreamAsync(token);
            return new ResponseOwnedStream(stream, response);
        }

        return new MovieVideoProviderOutput(
            contentType,
            fileName,
            sizeBytes,
            OpenReadAsync,
            status.DurationSeconds,
            JsonSerializer.Serialize(new
            {
                assetType = AssetTypes.Video,
                contentType,
                durationSeconds = status.DurationSeconds,
            }, JsonOptions),
            status.EstimatedCostUsd,
            status.ActualCostUsd,
            status.Currency ?? "USD",
            status.CostBasis ?? UsageCostBasis.Estimated,
            status.SafeMetadataJson,
            settings.Model);
    }

    public async Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
    {
        if (!IsAvailable || !Guid.TryParse(providerJobId, out _)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(GetBaseUri(), $"tasks/{Uri.EscapeDataString(providerJobId)}"));
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                logger.LogWarning("Runway cancellation returned a non-success status. StatusCode={StatusCode}", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Runway cancellation failed without exposing provider response details.");
        }
    }

    private object BuildPayload(MovieVideoGenerationRequest request, out Uri endpoint)
    {
        if (request.Operation is not (MovieStudioOperations.QuickMovie or MovieStudioOperations.SceneClip))
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false);
        if (!string.IsNullOrWhiteSpace(request.ContinuationProviderJobId))
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        if (request.DurationSeconds is < 2 or > 10)
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);

        var ratio = request.AspectRatio.Trim() switch
        {
            "16:9" => "1280:720",
            "9:16" => "720:1280",
            _ => throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false),
        };
        var prompt = BuildPrompt(request);
        var common = new Dictionary<string, object?>
        {
            ["model"] = settings.Model.Trim(),
            ["promptText"] = prompt,
            ["ratio"] = ratio,
            ["duration"] = request.DurationSeconds,
            ["outputFormat"] = "mp4",
        };

        if (!string.IsNullOrWhiteSpace(request.SourceImageUri))
        {
            if (!IsSafeMediaUri(request.SourceImageUri, "image/"))
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
            common["promptImage"] = request.SourceImageUri.Trim();
            endpoint = new Uri(GetBaseUri(), "image_to_video");
        }
        else
        {
            if (!string.Equals(settings.Model, "gen4.5", StringComparison.OrdinalIgnoreCase))
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
            endpoint = new Uri(GetBaseUri(), "text_to_video");
        }

        return common;
    }

    private string BuildPrompt(MovieVideoGenerationRequest request)
    {
        var visualDirection = new List<string>
        {
            request.Description,
            string.IsNullOrWhiteSpace(request.Style) ? null! : $"Visual style: {request.Style}",
            string.IsNullOrWhiteSpace(request.AdditionalInstructions) ? null! : request.AdditionalInstructions,
            string.IsNullOrWhiteSpace(request.ContinuityGuideJson) ? null! : $"Continuity direction: {request.ContinuityGuideJson}",
            string.IsNullOrWhiteSpace(request.SceneJson) ? null! : $"Scene direction: {request.SceneJson}",
            string.IsNullOrWhiteSpace(request.ShotJson) ? null! : $"Shot direction: {request.ShotJson}",
        };
        var prompt = string.Join("\n", visualDirection.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        if (prompt.Length < 1) throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, false);
        return prompt.Length <= 1000 ? prompt : prompt[..997].TrimEnd() + "...";
    }

    private MovieVideoProviderStatus MapStatus(RunwayTaskResponse task)
    {
        var normalized = task.Status?.Trim().ToUpperInvariant();
        var providerStatus = normalized switch
        {
            "PENDING" or "THROTTLED" => MovieVideoProviderJobStatus.Queued,
            "RUNNING" => MovieVideoProviderJobStatus.Running,
            "SUCCEEDED" => MovieVideoProviderJobStatus.Succeeded,
            "FAILED" => MovieVideoProviderJobStatus.Failed,
            "CANCELED" or "CANCELLED" or "ABORTED" => MovieVideoProviderJobStatus.Cancelled,
            _ => throw Failure(),
        };
        var output = task.Output?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var sizeBytes = task.OutputMetadata?.SizeBytes;
        var contentType = task.OutputMetadata?.ContentType;
        var fileName = task.OutputMetadata?.FileName;
        var durationSeconds = task.OutputMetadata?.DurationSeconds;
        var estimatedCostUsd = task.EstimatedCost?.Credits is { } credits
            ? (decimal?)Math.Round(Math.Max(0, credits) * Math.Max(0, settings.CreditUsd), 8, MidpointRounding.AwayFromZero)
            : null;
        var metadata = JsonSerializer.Serialize(new RunwayOutputMetadata(task.Id!, output, contentType, fileName, sizeBytes), JsonOptions);
        return new MovieVideoProviderStatus(
            providerStatus,
            providerStatus == MovieVideoProviderJobStatus.Succeeded ? 100 : providerStatus == MovieVideoProviderJobStatus.Running ? 50 : 0,
            contentType,
            fileName,
            sizeBytes,
            durationSeconds,
            metadata,
            ActualCostUsd: null,
            Currency: "USD",
            CostBasis: UsageCostBasis.Estimated,
            SafeMetadataJson: null,
            EstimatedCostUsd: estimatedCostUsd);
    }

    private string BuildSafeMetadata(RunwayTaskResponse task, MovieVideoProviderJobStatus status)
    {
        return JsonSerializer.Serialize(new
        {
            providerStatus = status.ToString().ToLowerInvariant(),
            estimatedCredits = task.EstimatedCost?.Credits,
            estimatedCostUsd = task.EstimatedCost?.Credits is { } credits ? Math.Round(Math.Max(0, credits) * Math.Max(0, settings.CreditUsd), 8, MidpointRounding.AwayFromZero) : (decimal?)null,
            outputAvailable = task.Output?.Any(value => !string.IsNullOrWhiteSpace(value)) == true,
            unsupportedFeatures = new[] { "continuation" },
        }, JsonOptions);
    }

    private async Task<string> SendJsonWithRetryAsync(HttpMethod method, Uri uri, object? payload, CancellationToken cancellationToken)
    {
        // Generation submission is a billable POST without provider idempotency;
        // only safe polling/retrieval methods may be retried in this adapter.
        var maxRetries = method == HttpMethod.Get || method == HttpMethod.Head
            ? Math.Clamp(settings.MaxTransientRetries, 0, 8)
            : 0;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, uri);
            AddHeaders(request);
            if (payload is not null) request.Content = JsonContent.Create(payload, options: JsonOptions);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < maxRetries)
            {
                logger.LogWarning(exception, "Runway request failed transiently; retrying. Attempt={Attempt}", attempt + 1);
                await RetryDelayAsync(attempt, cancellationToken);
                continue;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Runway request failed after bounded retries.");
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, true);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return body;
                if (IsTransient(response.StatusCode) && attempt < maxRetries)
                {
                    logger.LogWarning("Runway request returned a transient status; retrying. StatusCode={StatusCode}; Attempt={Attempt}", (int)response.StatusCode, attempt + 1);
                    await RetryDelayAsync(attempt, cancellationToken);
                    continue;
                }

                logger.LogWarning("Runway request returned a non-success status. StatusCode={StatusCode}", (int)response.StatusCode);
                throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, IsTransient(response.StatusCode))
                    : new MovieVideoProviderException(GenerationJobCodes(), false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 1, 120)));
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MovieVideoProviderTimeoutException();
        }
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("X-Runway-Version", RunwayVersion);
    }

    private async Task RetryDelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(Math.Max(1, settings.RetryMaxDelaySeconds), Math.Max(1, settings.RetryBaseDelaySeconds) * Math.Pow(2, attempt));
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable) throw new MovieProviderUnavailableException();
    }

    private Uri GetBaseUri()
    {
        if (!TryGetBaseUri(out var uri)) throw new MovieProviderUnavailableException();
        return uri;
    }

    private bool TryGetBaseUri(out Uri uri)
    {
        var configured = string.IsNullOrWhiteSpace(settings.ApiBaseUrl) ? "https://api.dev.runwayml.com/v1/" : settings.ApiBaseUrl.TrimEnd('/') + "/";
        if (Uri.TryCreate(configured, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps) return true;
        uri = null!;
        return false;
    }

    private long MaxOutputBytes() => Math.Min(2L * 1024 * 1024 * 1024, Math.Max(1, settings.MaxOutputBytes));

    private static T? Deserialize<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { throw Failure(); }
    }

    private static MovieVideoProviderException Failure() => new(GenerationJobErrorCodes.MovieGenerationFailed, false);
    private static string GenerationJobCodes() => GenerationJobErrorCodes.MovieGenerationFailed;
    private static bool IsTransient(HttpStatusCode statusCode) => statusCode == HttpStatusCode.RequestTimeout || statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    private static bool IsVideoContentType(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVideoContentType(string? contentType, Uri outputUri) =>
        IsVideoContentType(contentType) ? contentType!.Trim().ToLowerInvariant() : GuessContentType(outputUri);

    private static string GuessContentType(Uri uri) =>
        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            _ => throw new MovieVideoProviderOutputException(),
        };

    private static string NormalizeFileName(string? fileName, Uri outputUri)
    {
        var value = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(outputUri.AbsolutePath) : fileName;
        value = FileValidationName(value);
        return string.IsNullOrWhiteSpace(value) ? "movie-clip.mp4" : value;
    }

    private static string FileValidationName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var name = Path.GetFileName(value.Trim());
        return name.Length > 255 ? name[..255] : name;
    }

    private static bool IsSafeMediaUri(string value, string dataPrefix)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("data:" + dataPrefix, StringComparison.OrdinalIgnoreCase)) return trimmed.Length <= 5 * 1024 * 1024;
        if (trimmed.StartsWith("runway://", StringComparison.OrdinalIgnoreCase)) return trimmed.Length <= 5_000;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && trimmed.Length <= 2_048;
    }

    private sealed record RunwayTaskResponse(
        string? Id,
        string? Status,
        string[]? Output,
        RunwayCost? EstimatedCost,
        RunwayOutputMetadata? OutputMetadata,
        string? FailureCode,
        string? FailureReason);
    private sealed record RunwayCost(decimal Credits);
    private sealed record RunwayOutputMetadata(string? TaskId = null, string? OutputUrl = null, string? ContentType = null, string? FileName = null, long? SizeBytes = null, int? DurationSeconds = null);

    private sealed class ResponseOwnedStream(Stream inner, IDisposable response) : Stream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

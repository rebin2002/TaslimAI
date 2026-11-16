using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Taslim.Api.Contracts;

namespace Taslim.Api.Movies;

/// <summary>
/// Internal capability names used by direct video adapters. They are not browser-facing values.
/// </summary>
public static class DirectVideoOperations
{
    public const string TextToVideo = "text-to-video";
    public const string ImageToVideo = "image-to-video";
    public const string VideoToVideo = "video-to-video";
    public const string Upscale = "upscale";
}

public static class DirectVideoResolutions
{
    public const string Auto = "auto";
    public const string Hd720 = "720p";
    public const string FullHd = "1080p";
    public const string Uhd4k = "2160p";
}

public enum DirectVideoJobStatus
{
    Submitted,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum DirectVideoErrorCategory
{
    Unavailable,
    Authentication,
    RateLimited,
    TransientFailure,
    InvalidRequest,
    UnsupportedCapability,
    Rejected,
    InvalidOutput,
    TimedOut,
    Cancelled,
    PermanentFailure,
}

public static class DirectVideoErrorCodes
{
    public const string ProviderUnavailable = "DIRECT_VIDEO_PROVIDER_UNAVAILABLE";
    public const string ProviderAuthentication = "DIRECT_VIDEO_PROVIDER_AUTHENTICATION";
    public const string ProviderRateLimited = "DIRECT_VIDEO_PROVIDER_RATE_LIMITED";
    public const string ProviderTransientFailure = "DIRECT_VIDEO_PROVIDER_TRANSIENT_FAILURE";
    public const string RequestInvalid = "DIRECT_VIDEO_REQUEST_INVALID";
    public const string CapabilityUnsupported = "DIRECT_VIDEO_CAPABILITY_UNSUPPORTED";
    public const string RequestRejected = "DIRECT_VIDEO_REQUEST_REJECTED";
    public const string OutputInvalid = "DIRECT_VIDEO_OUTPUT_INVALID";
    public const string ProviderTimeout = "DIRECT_VIDEO_PROVIDER_TIMEOUT";
    public const string Cancelled = "DIRECT_VIDEO_CANCELLED";
    public const string ProviderFailure = "DIRECT_VIDEO_PROVIDER_FAILURE";
}

public sealed class DirectVideoProviderOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string ModelKey { get; set; } = "unconfigured";
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string SubmitPath { get; set; } = "video/generations";
    public string StatusPathTemplate { get; set; } = "video/generations/{id}";
    public string CancelPathTemplate { get; set; } = "video/generations/{id}/cancel";
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int HealthProbeTimeoutSeconds { get; set; } = 5;
    public int MaxTransientRetries { get; set; } = 2;
    public int MaxPromptCharacters { get; set; } = 8_000;
    public long MaxOutputBytes { get; set; } = 250 * 1024 * 1024;

    public DirectVideoProviderConfiguration ToConfiguration()
    {
        var baseUri = Uri.TryCreate(ApiBaseUrl?.Trim(), UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed
            : null;
        return new DirectVideoProviderConfiguration(
            ProviderKey?.Trim() ?? string.Empty,
            ModelKey?.Trim() ?? string.Empty,
            baseUri,
            Enabled,
            !string.IsNullOrWhiteSpace(ApiKey),
            Math.Clamp(RequestTimeoutSeconds, 1, 600),
            Math.Clamp(HealthProbeTimeoutSeconds, 1, 60),
            Math.Clamp(MaxTransientRetries, 0, 8),
            Math.Clamp(MaxPromptCharacters, 256, 100_000),
            Math.Clamp(MaxOutputBytes, 1, 2L * 1024 * 1024 * 1024));
    }
}

public sealed record DirectVideoProviderConfiguration(
    string ProviderKey,
    string ModelKey,
    Uri? ApiBaseUri,
    bool Enabled,
    bool HasCredentials,
    int RequestTimeoutSeconds,
    int HealthProbeTimeoutSeconds,
    int MaxTransientRetries,
    int MaxPromptCharacters,
    long MaxOutputBytes)
{
    public bool IsConfigured => Enabled
        && !string.IsNullOrWhiteSpace(ProviderKey)
        && !string.Equals(ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ModelKey)
        && !string.Equals(ModelKey, "unconfigured", StringComparison.OrdinalIgnoreCase)
        && ApiBaseUri is not null
        && HasCredentials;

    public string SanitizedDescription() =>
        $"enabled={Enabled}; providerConfigured={!string.IsNullOrWhiteSpace(ProviderKey) && !string.Equals(ProviderKey, "unconfigured", StringComparison.OrdinalIgnoreCase)}; modelConfigured={!string.IsNullOrWhiteSpace(ModelKey) && !string.Equals(ModelKey, "unconfigured", StringComparison.OrdinalIgnoreCase)}; baseUrlConfigured={ApiBaseUri is not null}; credentialsConfigured={HasCredentials}";
}

/// <summary>
/// A server-side declaration of what one adapter can do. It is deliberately independent of a vendor API schema.
/// </summary>
public sealed record DirectVideoCapabilityDeclaration(
    IReadOnlySet<string> Operations,
    IReadOnlySet<string> Resolutions,
    IReadOnlySet<string> AspectRatios,
    int MinDurationSeconds,
    int MaxDurationSeconds,
    bool SupportsReferenceImage,
    bool SupportsContinuation,
    bool SupportsUpscaling,
    bool SupportsNativeAudio)
{
    public bool SupportsOperation(string operation) => !string.IsNullOrWhiteSpace(operation)
        && Operations.Contains(operation.Trim());

    public bool SupportsResolution(string resolution) => string.Equals(resolution, DirectVideoResolutions.Auto, StringComparison.OrdinalIgnoreCase)
        || Resolutions.Contains(resolution);

    public bool SupportsAspectRatio(string aspectRatio) => AspectRatios.Contains(aspectRatio);

    public void Validate()
    {
        if (Operations.Count == 0 || Resolutions.Count == 0 || AspectRatios.Count == 0)
            throw new InvalidOperationException("A direct video capability declaration must contain supported values.");
        if (MinDurationSeconds < 1 || MaxDurationSeconds < MinDurationSeconds)
            throw new InvalidOperationException("A direct video capability declaration has an invalid duration range.");
    }
}

/// <summary>
/// Provider-neutral request passed from the Taslim movie boundary into a direct adapter.
/// </summary>
public sealed record DirectVideoRequest(
    Guid GenerationJobId,
    Guid MovieProjectId,
    Guid MovieClipId,
    string Operation,
    string PromptText,
    int DurationSeconds,
    string AspectRatio,
    string Resolution,
    bool UpscaleRequested,
    string? SourceImageUri,
    string? ContinuationProviderJobId,
    string? ContinuityContextJson,
    string? FirstFrameImageUri = null,
    string? LastFrameImageUri = null,
    IReadOnlyList<DirectVideoReferenceImage>? ReferenceImages = null);
public sealed record DirectVideoReferenceImage(string Uri, string? Role = null);

public sealed record DirectVideoSubmission(string ProviderJobId);

public sealed record DirectVideoProviderStatusResponse(
    string? Status,
    int? ProgressPercent = null,
    string? ContentType = null,
    string? FileName = null,
    long? SizeBytes = null,
    int? DurationSeconds = null,
    decimal? EstimatedCostUsd = null,
    decimal? ActualCostUsd = null,
    string? Currency = null,
    string? CostBasis = null,
    string? SafeMetadataJson = null);

public sealed record DirectVideoStatus(
    DirectVideoJobStatus Status,
    int ProgressPercent,
    string? ContentType = null,
    string? FileName = null,
    long? SizeBytes = null,
    int? DurationSeconds = null,
    decimal? EstimatedCostUsd = null,
    decimal? ActualCostUsd = null,
    string? Currency = null,
    string? CostBasis = null,
    string? SafeMetadataJson = null);

public sealed record DirectVideoProviderOutput(
    string ContentType,
    string FileName,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenReadAsync,
    int? DurationSeconds,
    decimal? EstimatedCostUsd,
    decimal? ActualCostUsd,
    string? Currency,
    string? CostBasis,
    string? SafeMetadataJson,
    string? ModelKey = null);

public sealed class DirectVideoRequestNormalizationException(string code)
    : Exception("The direct video request is not supported by the configured capability.")
{
    public string Code { get; } = code;
}

public sealed class DirectVideoProviderException(DirectVideoErrorCategory category, string code)
    : Exception("The direct video provider request failed safely.")
{
    public DirectVideoErrorCategory Category { get; } = category;
    public string Code { get; } = code;
}

public sealed class DirectVideoOutputNormalizationException()
    : Exception("The direct video provider returned an invalid output.");

public static class DirectVideoRequestNormalizer
{
    public static DirectVideoRequest Normalize(
        MovieVideoGenerationRequest request,
        DirectVideoCapabilityDeclaration capabilities,
        string? requestedResolution = null,
        bool upscaleRequested = false,
        int maxPromptCharacters = 8_000)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);
        capabilities.Validate();

        var operation = request.Operation?.Trim() ?? string.Empty;
        if (!capabilities.SupportsOperation(operation))
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);

        if (request.DurationSeconds < capabilities.MinDurationSeconds || request.DurationSeconds > capabilities.MaxDurationSeconds)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);

        var aspectRatio = request.AspectRatio?.Trim() ?? string.Empty;
        if (!capabilities.SupportsAspectRatio(aspectRatio))
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);

        var resolution = NormalizeResolution(requestedResolution);
        if (!capabilities.SupportsResolution(resolution))
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);

        var hasReferenceInput = !string.IsNullOrWhiteSpace(request.SourceImageUri)
            || !string.IsNullOrWhiteSpace(request.FirstFrameImageUri)
            || !string.IsNullOrWhiteSpace(request.LastFrameImageUri)
            || request.ReferenceImages is { Count: > 0 };
        if (hasReferenceInput && !capabilities.SupportsReferenceImage)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);
        if (!string.IsNullOrWhiteSpace(request.ContinuationProviderJobId) && !capabilities.SupportsContinuation)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);
        if (upscaleRequested && !capabilities.SupportsUpscaling)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.CapabilityUnsupported);

        var prompt = BuildPrompt(request);
        var promptLimit = Math.Clamp(maxPromptCharacters, 256, 100_000);
        if (prompt.Length > promptLimit)
            prompt = prompt[..Math.Max(1, promptLimit - 3)].TrimEnd() + "...";
        if (prompt.Length == 0)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid);

        var sourceImageUri = NormalizeMediaUri(request.SourceImageUri);
        var firstFrameImageUri = NormalizeMediaUri(request.FirstFrameImageUri);
        var lastFrameImageUri = NormalizeMediaUri(request.LastFrameImageUri);
        var referenceImages = NormalizeReferences(request.ReferenceImages, 16);
        var continuationId = NormalizeBounded(request.ContinuationProviderJobId, 240);
        var contextJson = BuildContinuityContext(request, 24_000);
        return new DirectVideoRequest(
            request.GenerationJobId,
            request.MovieProjectId,
            request.MovieClipId,
            operation,
            prompt,
            request.DurationSeconds,
            aspectRatio,
            resolution,
            upscaleRequested,
            sourceImageUri,
            continuationId,
            contextJson,
            firstFrameImageUri,
            lastFrameImageUri,
            referenceImages);
    }

    private static string NormalizeResolution(string? value) => string.IsNullOrWhiteSpace(value)
        ? DirectVideoResolutions.Auto
        : value.Trim().ToLowerInvariant() switch
        {
            "auto" => DirectVideoResolutions.Auto,
            "720p" or "1280x720" => DirectVideoResolutions.Hd720,
            "1080p" or "1920x1080" => DirectVideoResolutions.FullHd,
            "2160p" or "3840x2160" or "4k" => DirectVideoResolutions.Uhd4k,
            _ => value.Trim().ToLowerInvariant(),
        };

    private static string BuildPrompt(MovieVideoGenerationRequest request)
    {
        var parts = new[]
        {
            request.Description,
            string.IsNullOrWhiteSpace(request.Style) ? null : $"Visual style: {request.Style}",
            request.AdditionalInstructions,
            string.IsNullOrWhiteSpace(request.ContinuityGuideJson) ? null : $"Continuity direction: {request.ContinuityGuideJson}",
            string.IsNullOrWhiteSpace(request.SceneJson) ? null : $"Scene direction: {request.SceneJson}",
            string.IsNullOrWhiteSpace(request.ShotJson) ? null : $"Shot direction: {request.ShotJson}",
            string.IsNullOrWhiteSpace(request.WorldContextJson) ? null : $"World direction: {request.WorldContextJson}",
        };
        return string.Join("\n", parts.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()));
    }

    private static string? BuildContinuityContext(MovieVideoGenerationRequest request, int maxCharacters)
    {
        var perValueLimit = Math.Max(128, maxCharacters / 4);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["guide"] = BoundContext(request.ContinuityGuideJson, perValueLimit),
            ["scene"] = BoundContext(request.SceneJson, perValueLimit),
            ["shot"] = BoundContext(request.ShotJson, perValueLimit),
            ["world"] = BoundContext(request.WorldContextJson, perValueLimit),
        };
        if (values.Values.All(string.IsNullOrWhiteSpace)) return null;
        return JsonSerializer.Serialize(values);
    }

    private static string? BoundContext(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxCharacters ? trimmed : trimmed[..Math.Max(1, maxCharacters - 3)] + "...";
    }

    private static string? NormalizeMediaUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return trimmed.Length <= 5 * 1024 * 1024 ? trimmed : throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid);
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || trimmed.Length > 2_048)
            throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid);
        return trimmed;
    }

    private static IReadOnlyList<DirectVideoReferenceImage>? NormalizeReferences(IReadOnlyList<MovieVideoReferenceImage>? values, int maxCount)
    {
        if (values is null || values.Count == 0) return null;
        if (values.Count > maxCount) throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid);
        var normalized = values.Select(item => new DirectVideoReferenceImage(
            NormalizeMediaUri(item.Uri) ?? throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid),
            string.IsNullOrWhiteSpace(item.Role) ? null : item.Role.Trim()[..Math.Min(item.Role.Trim().Length, 80)])).ToArray();
        return normalized.Length == 0 ? null : normalized;
    }
    private static string? NormalizeBounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DirectVideoRequestNormalizationException(DirectVideoErrorCodes.RequestInvalid);
    }
}

public static class DirectVideoResultNormalizer
{
    public static DirectVideoStatus NormalizeStatus(DirectVideoProviderStatusResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = response.Status?.Trim().ToUpperInvariant() switch
        {
            "SUBMITTED" => DirectVideoJobStatus.Submitted,
            "PENDING" or "QUEUED" or "THROTTLED" => DirectVideoJobStatus.Queued,
            "RUNNING" or "PROCESSING" => DirectVideoJobStatus.Running,
            "SUCCEEDED" or "SUCCESS" or "COMPLETED" => DirectVideoJobStatus.Succeeded,
            "FAILED" or "ERROR" => DirectVideoJobStatus.Failed,
            "CANCELLED" or "CANCELED" or "ABORTED" => DirectVideoJobStatus.Cancelled,
            _ => throw new DirectVideoProviderException(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure),
        };
        return new DirectVideoStatus(
            status,
            Math.Clamp(response.ProgressPercent ?? (status == DirectVideoJobStatus.Succeeded ? 100 : 0), 0, 100),
            NormalizeContentType(response.ContentType),
            NormalizeFileName(response.FileName),
            response.SizeBytes,
            response.DurationSeconds,
            NormalizeMoney(response.EstimatedCostUsd),
            NormalizeMoney(response.ActualCostUsd),
            NormalizeCurrency(response.Currency),
            NormalizeCostBasis(response.CostBasis),
            NormalizeSafeMetadata(response.SafeMetadataJson));
    }

    public static DirectVideoProviderOutput NormalizeOutput(
        string? contentType,
        string? fileName,
        long sizeBytes,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        int? durationSeconds,
        decimal? estimatedCostUsd,
        decimal? actualCostUsd,
        string? currency,
        string? costBasis,
        string? safeMetadataJson,
        string? modelKey,
        long maxOutputBytes)
    {
        ArgumentNullException.ThrowIfNull(openReadAsync);
        var normalizedContentType = NormalizeContentType(contentType);
        var normalizedFileName = NormalizeFileName(fileName);
        var maximum = Math.Clamp(maxOutputBytes, 1, 2L * 1024 * 1024 * 1024);
        if (normalizedContentType is null || !normalizedContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || normalizedFileName is null || sizeBytes <= 0 || sizeBytes > maximum)
            throw new DirectVideoOutputNormalizationException();
        if (durationSeconds is <= 0) throw new DirectVideoOutputNormalizationException();
        return new DirectVideoProviderOutput(
            normalizedContentType,
            normalizedFileName,
            sizeBytes,
            openReadAsync,
            durationSeconds,
            NormalizeMoney(estimatedCostUsd),
            NormalizeMoney(actualCostUsd),
            NormalizeCurrency(currency),
            NormalizeCostBasis(costBasis),
            NormalizeSafeMetadata(safeMetadataJson),
            string.IsNullOrWhiteSpace(modelKey) ? null : modelKey.Trim());
    }

    private static string? NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? normalized : null;
    }

    private static string? NormalizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var fileName = Path.GetFileName(value.Trim());
        return fileName.Length is 0 or > 255 ? null : fileName;
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length <= 8 && normalized.All(char.IsLetter) ? normalized : null;
    }

    private static string? NormalizeCostBasis(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= 32 && normalized.All(character => char.IsLetterOrDigit(character) || character == '_') ? normalized : null;
    }

    private static decimal? NormalizeMoney(decimal? value) => value is >= 0 and <= 1_000_000_000m ? value : null;

    private static string? NormalizeSafeMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16_000) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record DirectVideoProviderError(
    DirectVideoErrorCategory Category,
    string Code,
    bool Retryable);

public static class DirectVideoErrorNormalizer
{
    public static DirectVideoProviderError FromStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(DirectVideoErrorCategory.Authentication, DirectVideoErrorCodes.ProviderAuthentication, false),
        HttpStatusCode.NotFound => new(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable, false),
        HttpStatusCode.TooManyRequests => new(DirectVideoErrorCategory.RateLimited, DirectVideoErrorCodes.ProviderRateLimited, true),
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new(DirectVideoErrorCategory.TimedOut, DirectVideoErrorCodes.ProviderTimeout, true),
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => new(DirectVideoErrorCategory.InvalidRequest, DirectVideoErrorCodes.RequestInvalid, false),
        HttpStatusCode.Conflict => new(DirectVideoErrorCategory.TransientFailure, DirectVideoErrorCodes.ProviderTransientFailure, true),
        _ when (int)statusCode == 425 => new(DirectVideoErrorCategory.TransientFailure, DirectVideoErrorCodes.ProviderTransientFailure, true),
        _ when (int)statusCode >= 500 => new(DirectVideoErrorCategory.TransientFailure, DirectVideoErrorCodes.ProviderTransientFailure, true),
        _ => new(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure, false),
    };

    public static DirectVideoProviderError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is DirectVideoProviderException direct) return new(direct.Category, direct.Code, IsRetryable(direct.Category));
        if (exception is OperationCanceledException) return new(DirectVideoErrorCategory.Cancelled, DirectVideoErrorCodes.Cancelled, false);
        if (exception is TimeoutException) return new(DirectVideoErrorCategory.TimedOut, DirectVideoErrorCodes.ProviderTimeout, true);
        if (exception is HttpRequestException) return new(DirectVideoErrorCategory.Unavailable, DirectVideoErrorCodes.ProviderUnavailable, true);
        if (exception is JsonException) return new(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure, false);
        return new(DirectVideoErrorCategory.PermanentFailure, DirectVideoErrorCodes.ProviderFailure, false);
    }

    public static DirectVideoProviderException ToException(HttpStatusCode statusCode)
    {
        var error = FromStatusCode(statusCode);
        return new DirectVideoProviderException(error.Category, error.Code);
    }

    private static bool IsRetryable(DirectVideoErrorCategory category) => category is DirectVideoErrorCategory.RateLimited or DirectVideoErrorCategory.TransientFailure or DirectVideoErrorCategory.TimedOut;
}

public enum DirectVideoHealthStatus
{
    Disabled,
    Unconfigured,
    Unknown,
    Healthy,
    Unhealthy,
}

public sealed record DirectVideoHealthSnapshot(
    DirectVideoHealthStatus Status,
    DateTimeOffset CheckedAt,
    int? LatencyMilliseconds,
    string? ErrorCode)
{
    public bool Ready => Status == DirectVideoHealthStatus.Healthy;
}

public interface IDirectVideoProviderHealthHook
{
    Task<DirectVideoHealthSnapshot> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Health is an explicit hook so adapters can add a cheap authenticated probe later without changing orchestration.
/// No network call is made when no probe delegate is supplied.
/// </summary>
public sealed class DirectVideoProviderHealthHook(
    DirectVideoProviderConfiguration configuration,
    Func<CancellationToken, Task>? probe = null,
    TimeProvider? timeProvider = null) : IDirectVideoProviderHealthHook
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DirectVideoHealthSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = clock.GetUtcNow();
        if (!configuration.Enabled) return new(DirectVideoHealthStatus.Disabled, checkedAt, null, null);
        if (!configuration.IsConfigured) return new(DirectVideoHealthStatus.Unconfigured, checkedAt, null, DirectVideoErrorCodes.ProviderUnavailable);
        if (probe is null) return new(DirectVideoHealthStatus.Unknown, checkedAt, null, "DIRECT_VIDEO_HEALTH_PROBE_NOT_IMPLEMENTED");

        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.HealthProbeTimeoutSeconds));
        try
        {
            await probe(timeout.Token);
            stopwatch.Stop();
            return new(DirectVideoHealthStatus.Healthy, checkedAt, (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(DirectVideoHealthStatus.Unhealthy, checkedAt, (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue), DirectVideoErrorCodes.ProviderTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(DirectVideoHealthStatus.Unhealthy, checkedAt, (int)Math.Clamp(stopwatch.ElapsedMilliseconds, 0, int.MaxValue), DirectVideoErrorNormalizer.FromException(exception).Code);
        }
    }
}

public interface IDirectVideoProviderAdapter
{
    string Key { get; }
    bool IsAvailable { get; }
    DirectVideoCapabilityDeclaration Capabilities { get; }
    IDirectVideoProviderHealthHook Health { get; }
    Task<DirectVideoSubmission> SubmitAsync(DirectVideoRequest request, CancellationToken cancellationToken = default);
    Task<DirectVideoStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken = default);
    Task<DirectVideoProviderOutput> RetrieveAsync(string providerJobId, DirectVideoStatus status, CancellationToken cancellationToken = default);
    Task CancelAsync(string providerJobId, CancellationToken cancellationToken = default);
}

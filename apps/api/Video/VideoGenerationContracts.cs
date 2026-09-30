using System.Collections.ObjectModel;

namespace Taslim.Api.Video;

/// <summary>
/// The stable, provider-neutral boundary for asynchronous video generation.
/// Adapter keys and opaque handles are operational data; they must not be copied
/// into ordinary user-facing DTOs.
/// </summary>
public interface IVideoGenerationAdapter
{
    /// <summary>Internal adapter route key used for selection and telemetry.</summary>
    string AdapterKey { get; }

    bool IsAvailable { get; }

    VideoGenerationCapabilities Capabilities { get; }

    Task<VideoGenerationSubmission> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken cancellationToken = default);

    Task<VideoGenerationStatus> GetStatusAsync(
        VideoGenerationHandle handle,
        CancellationToken cancellationToken = default);

    Task<VideoGenerationArtifact> RetrieveArtifactAsync(
        VideoGenerationHandle handle,
        VideoGenerationStatus completedStatus,
        CancellationToken cancellationToken = default);

    Task<VideoCancellationResult> CancelAsync(
        VideoGenerationHandle handle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts an adapter-specific callback payload into the same normalized
    /// update used by polling. The callback request contains transport data only;
    /// provider SDK/request types must not cross this boundary.
    /// </summary>
    Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(
        VideoGenerationCallbackRequest callback,
        CancellationToken cancellationToken = default);
}

public sealed record VideoGenerationRequest(
    string Operation,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    VideoResolution? RequestedResolution = null,
    string? Style = null,
    string? Language = null,
    string? AdditionalInstructions = null,
    IReadOnlyList<VideoReferenceImage>? ReferenceImages = null,
    VideoGenerationHandle? Continuation = null,
    string? IdempotencyKey = null,
    IReadOnlyDictionary<string, string>? Context = null);

public sealed record VideoReferenceImage(string Uri, string? Role = null);

/// <summary>An opaque operation identifier returned by an adapter.</summary>
public sealed record VideoGenerationHandle(string Value)
{
    public override string ToString() => Value;
}

public sealed record VideoGenerationSubmission(
    VideoGenerationHandle Handle,
    VideoGenerationStatus InitialStatus,
    VideoUsageEvidence? Usage = null);

public enum VideoGenerationState
{
    Submitted,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record VideoGenerationStatus(
    VideoGenerationState State,
    int ProgressPercent,
    DateTimeOffset ObservedAt,
    VideoGenerationArtifactDescriptor? Artifact = null,
    VideoUsageEvidence? Usage = null,
    VideoGenerationError? Error = null,
    IReadOnlyDictionary<string, string>? SafeMetadata = null)
{
    public bool IsTerminal => State is VideoGenerationState.Succeeded
        or VideoGenerationState.Failed
        or VideoGenerationState.Cancelled;
}

/// <summary>
/// Metadata known before the artifact is copied into private Taslim storage.
/// </summary>
public sealed record VideoGenerationArtifactDescriptor(
    string ContentType,
    string FileName,
    long SizeBytes,
    int? DurationSeconds = null,
    VideoResolution? Resolution = null);

/// <summary>
/// A stream-backed generated video. The opener owns the returned stream and must
/// not expose a provider URL or response object to the core domain.
/// </summary>
public sealed record VideoGenerationArtifact(
    string ContentType,
    string FileName,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenReadAsync,
    int? DurationSeconds = null,
    VideoResolution? Resolution = null,
    string? Sha256 = null,
    IReadOnlyDictionary<string, string>? SafeMetadata = null);

public readonly record struct VideoResolution(int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public override string ToString() => $"{Width}x{Height}";
}

public sealed record VideoGenerationCapabilities(
    IReadOnlySet<string> Operations,
    IReadOnlySet<VideoResolution> SupportedResolutions,
    IReadOnlySet<string> SupportedAspectRatios,
    IReadOnlySet<string> OutputContentTypes,
    int MinDurationSeconds,
    int MaxDurationSeconds,
    bool SupportsPolling,
    bool SupportsCallbacks,
    bool SupportsCancellation,
    bool SupportsContinuation,
    bool SupportsReferenceImages,
    int MaxOutputs = 1)
{
    public bool SupportsOperation(string operation) => Operations.Any(
        value => string.Equals(value, operation, StringComparison.OrdinalIgnoreCase));

    public bool SupportsAspectRatio(string aspectRatio) => SupportedAspectRatios.Any(
        value => string.Equals(value, aspectRatio, StringComparison.OrdinalIgnoreCase));

    public bool Supports(VideoGenerationRequest request)
    {
        if (!SupportsOperation(request.Operation)
            || request.DurationSeconds < MinDurationSeconds
            || request.DurationSeconds > MaxDurationSeconds
            || !SupportsAspectRatio(request.AspectRatio)
            || request.ReferenceImages is { Count: > 0 } && !SupportsReferenceImages)
            return false;

        return request.RequestedResolution is null
            || SupportedResolutions.Count == 0
            || SupportedResolutions.Contains(request.RequestedResolution.Value);
    }

}

public enum VideoGenerationErrorCode
{
    Unavailable,
    AuthenticationFailed,
    RateLimited,
    InvalidRequest,
    UnsupportedCapability,
    ContentRejected,
    TimedOut,
    Cancelled,
    ArtifactUnavailable,
    MalformedResponse,
    StaleEvent,
    InternalFailure,
}

public sealed record VideoGenerationError(
    VideoGenerationErrorCode Code,
    bool IsTransient,
    string SafeMessage,
    TimeSpan? RetryAfter = null)
{
    public static VideoGenerationError Unavailable(string message = "Video generation is unavailable.") =>
        new(VideoGenerationErrorCode.Unavailable, true, message);

    public static VideoGenerationError InvalidRequest(string message = "The video request is invalid.") =>
        new(VideoGenerationErrorCode.InvalidRequest, false, message);

    public static VideoGenerationError ArtifactUnavailable(string message = "The video artifact is not available.") =>
        new(VideoGenerationErrorCode.ArtifactUnavailable, false, message);
}

public sealed class VideoGenerationAdapterException(VideoGenerationError error)
    : Exception(error.SafeMessage)
{
    public VideoGenerationError Error { get; } = error;
}

public enum VideoCancellationOutcome
{
    Accepted,
    AlreadyTerminal,
    NotSupported,
}

public sealed record VideoCancellationResult(
    VideoCancellationOutcome Outcome,
    VideoGenerationStatus? CurrentStatus = null,
    VideoGenerationError? Error = null);

/// <summary>
/// Transport-only callback input. Adapter implementations verify signatures and
/// parse their payloads, then return only a normalized callback result.
/// </summary>
public sealed record VideoGenerationCallbackRequest(
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers,
    string ContentType,
    DateTimeOffset ReceivedAt);

public enum VideoCallbackDisposition
{
    Accepted,
    Duplicate,
    Ignored,
    Rejected,
}

public sealed record VideoGenerationCallback(
    string EventId,
    VideoGenerationHandle Handle,
    VideoGenerationStatus Status);

public sealed record VideoGenerationCallbackResult(
    VideoCallbackDisposition Disposition,
    VideoGenerationCallback? Callback = null,
    VideoGenerationError? Error = null);

public enum VideoCostBasis
{
    Unknown,
    Estimated,
    ProviderReported,
}

public enum VideoUsageMetric
{
    InputDurationSeconds,
    GeneratedDurationSeconds,
    InputReferenceImageCount,
    OutputBytes,
    ProcessingMilliseconds,
    OutputPixels,
}

public sealed record VideoUsageMeasure(VideoUsageMetric Metric, decimal Value);

/// <summary>
/// Usage evidence is intentionally provider-neutral. Provider/model identifiers
/// and raw billing payloads belong in restricted operational storage, not here.
/// </summary>
public sealed record VideoUsageEvidence(
    decimal? EstimatedCost,
    decimal? ActualCost,
    string Currency,
    VideoCostBasis CostBasis,
    IReadOnlyList<VideoUsageMeasure>? Measures = null,
    string? PricingVersion = null)
{
    public IReadOnlyList<VideoUsageMeasure> NormalizedMeasures =>
        new ReadOnlyCollection<VideoUsageMeasure>((Measures ?? []).ToList());
}

public static class VideoGenerationContract
{
    public static void ValidateRequest(VideoGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireText(request.Operation, 1, 80, nameof(request.Operation));
        RequireText(request.Description, 1, 20_000, nameof(request.Description));
        if (request.DurationSeconds is < 1 or > 3_600)
            throw new ArgumentOutOfRangeException(nameof(request.DurationSeconds));
        RequireText(request.AspectRatio, 1, 20, nameof(request.AspectRatio));
        if (request.RequestedResolution is { } resolution && !resolution.IsValid)
            throw new ArgumentException("The requested resolution must be positive.", nameof(request));
        if (request.ReferenceImages is not null)
        {
            if (request.ReferenceImages.Count > 16)
                throw new ArgumentException("Too many reference images.", nameof(request));
            foreach (var reference in request.ReferenceImages)
                RequireText(reference.Uri, 1, 4_096, nameof(request.ReferenceImages));
        }
        if (request.Context is not null && request.Context.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 80 || pair.Value.Length > 4_000))
            throw new ArgumentException("Context metadata is invalid.", nameof(request.Context));
    }

    public static void ValidateCapabilities(VideoGenerationCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.MinDurationSeconds < 1 || capabilities.MaxDurationSeconds < capabilities.MinDurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        if (capabilities.MaxOutputs < 1)
            throw new ArgumentOutOfRangeException(nameof(capabilities.MaxOutputs));
        if (capabilities.Operations.Any(string.IsNullOrWhiteSpace)
            || capabilities.SupportedAspectRatios.Any(string.IsNullOrWhiteSpace)
            || capabilities.OutputContentTypes.Any(value => string.IsNullOrWhiteSpace(value) || !value.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Capabilities contain an invalid value.", nameof(capabilities));
        if (capabilities.SupportedResolutions.Any(resolution => !resolution.IsValid))
            throw new ArgumentException("Capabilities contain an invalid resolution.", nameof(capabilities));
        if (!capabilities.SupportsPolling && !capabilities.SupportsCallbacks)
            throw new ArgumentException("An adapter must support polling or callbacks.", nameof(capabilities));
    }

    public static void ValidateStatus(VideoGenerationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.ProgressPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(status.ProgressPercent));
        if (status.State == VideoGenerationState.Failed && status.Error is null)
            throw new ArgumentException("Failed status requires a normalized error.", nameof(status));
        if (status.State == VideoGenerationState.Succeeded && status.Error is not null)
            throw new ArgumentException("Succeeded status cannot contain an error.", nameof(status));
        if (status.Artifact is not null)
            ValidateArtifactDescriptor(status.Artifact);
        if (status.Usage is not null)
            ValidateUsage(status.Usage);
    }

    public static void ValidateUsage(VideoUsageEvidence usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        RequireText(usage.Currency, 3, 3, nameof(usage.Currency));
        if (usage.EstimatedCost is < 0 || usage.ActualCost is < 0)
            throw new ArgumentOutOfRangeException(nameof(usage));
        if (usage.Measures is null) return;
        if (usage.Measures.Any(measure => measure.Value < 0 || !Enum.IsDefined(measure.Metric)))
            throw new ArgumentException("Usage measures are invalid.", nameof(usage));
    }

    public static void ValidateArtifactDescriptor(VideoGenerationArtifactDescriptor artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        RequireVideoContentType(artifact.ContentType, nameof(artifact.ContentType));
        RequireFileName(artifact.FileName);
        if (artifact.SizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(artifact.SizeBytes));
        if (artifact.DurationSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(artifact.DurationSeconds));
        if (artifact.Resolution is { } resolution && !resolution.IsValid)
            throw new ArgumentException("The artifact resolution must be positive.", nameof(artifact));
    }

    public static void ValidateArtifact(VideoGenerationArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateArtifactDescriptor(new VideoGenerationArtifactDescriptor(artifact.ContentType, artifact.FileName, artifact.SizeBytes, artifact.DurationSeconds, artifact.Resolution));
        ArgumentNullException.ThrowIfNull(artifact.OpenReadAsync);
    }

    private static void RequireText(string value, int minLength, int maxLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < minLength || value.Trim().Length > maxLength)
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
    }

    private static void RequireVideoContentType(string value, string parameterName)
    {
        RequireText(value, 7, 100, parameterName);
        if (!value.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The artifact must be a video content type.", parameterName);
    }

    private static void RequireFileName(string value)
    {
        RequireText(value, 1, 255, nameof(VideoGenerationArtifact.FileName));
        if (Path.GetFileName(value) != value || value.Contains('\0'))
            throw new ArgumentException("The artifact file name is invalid.", nameof(VideoGenerationArtifact.FileName));
    }
}

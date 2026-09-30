using Taslim.Api.Domain;

namespace Taslim.Api.Upscaling;

public enum UpscalingTargetResolution
{
    P1080,
    K2,
    K4,
}

public static class UpscalingTargetResolutions
{
    public const string P1080 = "1080p";
    public const string K2 = "2k";
    public const string K4 = "4k";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        P1080, K2, K4,
    };

    public static bool TryParse(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is "1080" or "fullhd" or "full-hd") normalized = P1080;
        if (normalized is "2k" or "2048p") normalized = K2;
        if (normalized is "4k" or "2160p" or "uhd") normalized = K4;
        return Supported.Contains(normalized);
    }
}

public enum UpscalingJobStatus
{
    Pending,
    Queued,
    Running,
    QualityControlPending,
    QualityControlRejected,
    Completed,
    Failed,
    Cancelled,
}

public enum UpscalingAttemptStatus
{
    Started,
    Succeeded,
    Failed,
    Cancelled,
    QualityControlRejected,
}

public enum UpscalingQualityStatus
{
    Pending,
    InReview,
    Approved,
    Rejected,
}

public static class UpscalingJobErrorCodes
{
    public const string SourceAssetNotFound = "UPSCALING_SOURCE_ASSET_NOT_FOUND";
    public const string SourceAssetUnsupported = "UPSCALING_SOURCE_ASSET_UNSUPPORTED";
    public const string SourceAssetUnavailable = "UPSCALING_SOURCE_ASSET_UNAVAILABLE";
    public const string TargetResolutionUnsupported = "UPSCALING_TARGET_RESOLUTION_UNSUPPORTED";
    public const string IdempotencyKeyInvalid = "UPSCALING_IDEMPOTENCY_KEY_INVALID";
    public const string IdempotencyKeyReused = "UPSCALING_IDEMPOTENCY_KEY_REUSED";
    public const string ExecutionNotAvailable = "UPSCALING_EXECUTION_NOT_AVAILABLE";
    public const string InvalidLifecycleTransition = "UPSCALING_INVALID_LIFECYCLE_TRANSITION";
    public const string RetryLimitReached = "UPSCALING_RETRY_LIMIT_REACHED";
    public const string OutputAssetInvalid = "UPSCALING_OUTPUT_ASSET_INVALID";
    public const string QualityControlPending = "UPSCALING_QUALITY_CONTROL_PENDING";
    public const string QualityControlRejected = "UPSCALING_QUALITY_CONTROL_REJECTED";
    public const string Cancelled = "UPSCALING_CANCELLED";
}

public sealed class UpscalingJob
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid SourceAssetId { get; set; }
    public Guid? OutputAssetId { get; set; }
    public string TargetResolution { get; set; } = UpscalingTargetResolutions.P1080;
    public UpscalingJobStatus Status { get; set; } = UpscalingJobStatus.Pending;
    public string? Title { get; set; }
    public string? IdempotencyKey { get; set; }
    public string RequestFingerprint { get; set; } = string.Empty;
    public string SourceProvenanceJson { get; set; } = "{}";
    public string? OutputProvenanceJson { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public int MaxRetryCount { get; set; } = 2;
    public int ProgressPercent { get; set; }
    public Guid? CurrentAttemptId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? QualityControlAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public Asset SourceAsset { get; set; } = null!;
    public Asset? OutputAsset { get; set; }
    public UpscalingAttempt? CurrentAttempt { get; set; }
    public ICollection<UpscalingAttempt> Attempts { get; set; } = [];
    public ICollection<UpscalingQualityHandoff> QualityHandoffs { get; set; } = [];
}

public sealed class UpscalingAttempt
{
    public Guid Id { get; set; }
    public Guid UpscalingJobId { get; set; }
    public int AttemptNumber { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public UpscalingAttemptStatus Status { get; set; } = UpscalingAttemptStatus.Started;
    public string? ExecutionReference { get; set; }
    public string? FailureCode { get; set; }
    public bool Retryable { get; set; }
    public string? SafeMetadataJson { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public UpscalingJob UpscalingJob { get; set; } = null!;
}

public sealed class UpscalingQualityHandoff
{
    public Guid Id { get; set; }
    public Guid UpscalingJobId { get; set; }
    public Guid SourceAssetId { get; set; }
    public Guid OutputAssetId { get; set; }
    public string TargetResolution { get; set; } = UpscalingTargetResolutions.P1080;
    public UpscalingQualityStatus Status { get; set; } = UpscalingQualityStatus.Pending;
    public string? ReviewNote { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }

    public UpscalingJob UpscalingJob { get; set; } = null!;
    public Asset SourceAsset { get; set; } = null!;
    public Asset OutputAsset { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
}

public sealed record UpscalingProviderRequest(
    Guid JobId,
    Guid SourceAssetId,
    string TargetResolution,
    string SourceContentType,
    long SourceSizeBytes,
    string IdempotencyKey,
    string SourceProvenanceJson);

public sealed record UpscalingSubmission(string ExecutionReference);

public enum UpscalingProviderJobStatus
{
    Submitted,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record UpscalingProviderStatus(
    UpscalingProviderJobStatus Status,
    int ProgressPercent,
    string? FailureCode = null);

public sealed record UpscalingProviderOutput(
    string FileName,
    string ContentType,
    long SizeBytes,
    string? OutputProvenanceJson = null,
    string? ProviderModelKey = null);

public interface IUpscalingProvider
{
    string Key { get; }
    bool IsAvailable { get; }
    IReadOnlySet<string> SupportedResolutions { get; }
    Task<UpscalingSubmission> SubmitAsync(UpscalingProviderRequest request, CancellationToken cancellationToken = default);
    Task<UpscalingProviderStatus> GetStatusAsync(string executionReference, CancellationToken cancellationToken = default);
    Task<UpscalingProviderOutput> RetrieveAsync(string executionReference, UpscalingProviderStatus status, CancellationToken cancellationToken = default);
    Task CancelAsync(string executionReference, CancellationToken cancellationToken = default);
}

public sealed class UpscalingProviderUnavailableException : Exception
{
    public UpscalingProviderUnavailableException()
        : base("Upscaling execution is not available.") { }
}

public sealed class UnavailableUpscalingProvider : IUpscalingProvider
{
    public string Key => "unconfigured";
    public bool IsAvailable => false;
    public IReadOnlySet<string> SupportedResolutions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public Task<UpscalingSubmission> SubmitAsync(UpscalingProviderRequest request, CancellationToken cancellationToken = default) =>
        throw new UpscalingProviderUnavailableException();

    public Task<UpscalingProviderStatus> GetStatusAsync(string executionReference, CancellationToken cancellationToken = default) =>
        throw new UpscalingProviderUnavailableException();

    public Task<UpscalingProviderOutput> RetrieveAsync(string executionReference, UpscalingProviderStatus status, CancellationToken cancellationToken = default) =>
        throw new UpscalingProviderUnavailableException();

    public Task CancelAsync(string executionReference, CancellationToken cancellationToken = default) =>
        throw new UpscalingProviderUnavailableException();
}

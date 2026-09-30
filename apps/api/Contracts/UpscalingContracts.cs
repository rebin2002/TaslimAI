using System.ComponentModel.DataAnnotations;
using Taslim.Api.Upscaling;

namespace Taslim.Api.Contracts;

public sealed class CreateUpscalingJobRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    public Guid? ProjectId { get; set; }

    [Required]
    public Guid SourceAssetId { get; set; }

    [Required, StringLength(20)]
    public string TargetResolution { get; set; } = string.Empty;

    [StringLength(160)]
    public string? Title { get; set; }
}

public sealed class ReviewUpscalingQualityRequest
{
    public bool Approved { get; set; }

    [StringLength(2_000)]
    public string? Note { get; set; }
}

public sealed record UpscalingQualityHandoffDto(
    Guid Id,
    Guid OutputAssetId,
    string Status,
    string? ReviewNote,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record UpscalingJobDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid SourceAssetId,
    Guid? OutputAssetId,
    string TargetResolution,
    string Status,
    string? Title,
    int ProgressPercent,
    int RetryCount,
    int MaxRetryCount,
    bool CanRetry,
    string? ErrorCode,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    DateTime? QualityControlAt,
    DateTime? CompletedAt,
    DateTime? FailedAt,
    DateTime? CancelledAt,
    UpscalingQualityHandoffDto? QualityControl);

public sealed record UpscalingJobListDto(
    IReadOnlyList<UpscalingJobDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record UpscalingJobFilter(
    Guid WorkspaceId,
    int Page = 1,
    int PageSize = 20,
    UpscalingJobStatus? Status = null,
    string? TargetResolution = null);

public static class UpscalingContractMapper
{
    public static UpscalingJobDto ToDto(UpscalingJob job) {
        var handoff = job.QualityHandoffs
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new UpscalingQualityHandoffDto(
                item.Id,
                item.OutputAssetId,
                item.Status.ToString(),
                item.ReviewNote,
                item.CreatedAt,
                item.ReviewedAt))
            .FirstOrDefault();
        return new UpscalingJobDto(
            job.Id,
            job.WorkspaceId,
            job.ProjectId,
            job.SourceAssetId,
            job.OutputAssetId,
            job.TargetResolution,
            job.Status.ToString(),
            job.Title,
            job.ProgressPercent,
            job.RetryCount,
            job.MaxRetryCount,
            (job.Status is UpscalingJobStatus.Failed or UpscalingJobStatus.QualityControlRejected or UpscalingJobStatus.Cancelled)
                && job.RetryCount < job.MaxRetryCount
                && (job.Status != UpscalingJobStatus.Failed || job.Attempts.OrderByDescending(item => item.AttemptNumber).FirstOrDefault()?.Retryable != false),
            job.LastErrorCode,
            job.LastErrorMessage,
            job.CreatedAt,
            job.QueuedAt,
            job.StartedAt,
            job.QualityControlAt,
            job.CompletedAt,
            job.FailedAt,
            job.CancelledAt,
            handoff);
    }
}

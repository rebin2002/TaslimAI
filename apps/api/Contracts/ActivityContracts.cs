using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public sealed record ActivityItemDto(
    Guid JobId,
    Guid WorkspaceId,
    Guid? ProjectId,
    string JobType,
    string Title,
    string Status,
    int ProgressPercent,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    bool IsRead,
    string? SafeFailureMessage,
    Guid? AssetId);

public sealed record ActivityListDto(
    IReadOnlyList<ActivityItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    int UnreadCount);

public sealed record ActivityFilter(
    Guid WorkspaceId,
    int Page = 1,
    int PageSize = 20,
    string? Status = null,
    string? JobType = null);

public static class ActivityContractMapper
{
    public static ActivityItemDto ToDto(GenerationJob job, bool isRead, Guid? assetId) => new(
        job.Id,
        job.WorkspaceId,
        job.ProjectId,
        ActivityJobType(job.JobType),
        string.IsNullOrWhiteSpace(job.Title) ? DefaultTitle(job.JobType) : job.Title.Trim(),
        ActivityStatus(job.Status),
        Math.Clamp(job.ProgressPercent, 0, 100),
        job.CreatedAt,
        job.CompletedAt ?? job.FailedAt ?? job.CancelledAt,
        isRead,
        job.Status == GenerationJobStatus.Failed || job.Status == GenerationJobStatus.Cancelled
            ? SafeFailureMessage(job)
            : null,
        assetId);

    public static string ActivityJobType(string jobType) => jobType.ToLowerInvariant() switch
    {
        "image.generate" => "image",
        "document.generate" => "document",
        "presentation.generate" => "presentation",
        "research.generate" => "research",
        "social.generate" => "social",
        "voice.generate" => "voice",
        "music.generate" => "music",
        "movie.generate" or "movie.quick.generate" or "movie.clip.generate" or "movie.assembly" => "movie",
        _ => "other",
    };

    public static string ActivityStatus(GenerationJobStatus status) => status switch
    {
        GenerationJobStatus.Pending or GenerationJobStatus.Queued => "Queued",
        GenerationJobStatus.Running => "Running",
        GenerationJobStatus.Succeeded => "Completed",
        GenerationJobStatus.Failed => "Failed",
        GenerationJobStatus.Cancelled => "Cancelled",
        _ => "Queued",
    };

    public static string DefaultTitle(string jobType) => ActivityJobType(jobType) switch
    {
        "image" => "Image generation",
        "document" => "Document generation",
        "presentation" => "Presentation generation",
        "research" => "Research",
        "social" => "Social content",
        "voice" => "Voice generation",
        "music" => "Music generation",
        "movie" => "Movie generation",
        _ => "Generation activity",
    };

    private static string SafeFailureMessage(GenerationJob job) => job.Status == GenerationJobStatus.Cancelled
        ? "This activity was cancelled."
        : "This activity could not be completed. Please try again.";
}

public sealed record ActivityReadRequest(Guid WorkspaceId);
public sealed record ActivityReadAllRequest(Guid WorkspaceId);

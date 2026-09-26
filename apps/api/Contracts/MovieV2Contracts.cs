using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed record MovieV2TakeApprovalDto(
    Guid Id,
    Guid UserId,
    string Decision,
    string? Comment,
    DateTime CreatedAt);

public sealed record MovieV2TakeDto(
    Guid Id,
    Guid MovieShotId,
    int VersionNumber,
    string Label,
    string Status,
    string QualityLevel,
    bool AutoDirectorEnabled,
    Guid? MovieClipId,
    Guid? GenerationJobId,
    Guid? AssetId,
    string? Notes,
    DateTime? SelectedAt,
    DateTime? FinalizedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2TakeApprovalDto> Approvals);

public sealed record MovieV2ShotDto(
    Guid Id,
    int Sequence,
    string Description,
    string Status,
    Guid? SelectedTakeId,
    Guid? FinalTakeId,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2TakeDto> Takes);

public sealed record MovieV2SceneDto(
    Guid Id,
    int Sequence,
    string Title,
    string Summary,
    string Status,
    Guid? MovieSequenceId,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2ShotDto> Shots);

public sealed record MovieV2SequenceDto(
    Guid Id,
    int Sequence,
    string Title,
    string? Summary,
    string Status,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2SceneDto> Scenes);

public sealed record MovieV2ActDto(
    Guid Id,
    int Sequence,
    string Title,
    string? Summary,
    string Status,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2SequenceDto> Sequences);

public sealed record MovieV2HierarchyDto(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string ProductionStatus,
    string QualityLevel,
    bool AutoDirectorEnabled,
    DateTime? StatusChangedAt,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieV2ActDto> Acts);

public sealed class MovieV2ActRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
}

public sealed class MovieV2SequenceRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
}

public sealed class MovieV2SceneRequest
{
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

public sealed class MovieV2TakeRequest
{
    public string? Label { get; set; }
    public string QualityLevel { get; set; } = MovieQualityLevels.Standard;
    public bool AutoDirectorEnabled { get; set; }
    public Guid? MovieClipId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public string? Notes { get; set; }
}

public sealed class MovieV2SettingsRequest
{
    public string? ProductionStatus { get; set; }
    public string? QualityLevel { get; set; }
    public bool? AutoDirectorEnabled { get; set; }
}

public sealed class MovieV2StatusRequest
{
    public string Status { get; set; } = string.Empty;
}

public sealed class MovieV2ApprovalRequest
{
    public string Decision { get; set; } = MovieApprovalDecisions.Approved;
    public string? Comment { get; set; }
}

public sealed record MovieV2OrderRequest(int Sequence);

public sealed record MovieV2OverviewProjectDto(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string Description,
    string Status,
    string ProductionStatus,
    string QualityLevel,
    bool AutoDirectorEnabled,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MovieV2OverviewStageDto(
    string Status,
    int Total,
    int Completed,
    int? Percent,
    bool Started);

public sealed record MovieV2OverviewStoryDto(
    string Status,
    int RevisionNumber,
    int ScreenplaySceneCount,
    bool HasContent);

public sealed record MovieV2OverviewResourceDto(
    int Total,
    int? Ready,
    string Status);

public sealed record MovieV2OverviewCountsDto(
    int Total,
    int Planned,
    int InProgress,
    int Approved,
    int Archived);

public sealed record MovieV2OverviewProgressDto(
    MovieV2OverviewStageDto Storyboard,
    MovieV2OverviewStageDto Keyframe,
    MovieV2OverviewStageDto Production,
    MovieV2OverviewStageDto SelectedFinalTakes);

public sealed record MovieV2OverviewTakeSummaryDto(
    int Total,
    int Selected,
    int Finalized,
    int PendingApproval,
    int Rejected);

public sealed record MovieV2OverviewApprovalBucketDto(
    int Pending,
    string Status);

public sealed record MovieV2OverviewApprovalSummaryDto(
    MovieV2OverviewApprovalBucketDto Production,
    MovieV2OverviewApprovalBucketDto Collaborative,
    MovieV2OverviewApprovalBucketDto Screenplay,
    MovieV2OverviewApprovalBucketDto DirectorProposals);

public sealed record MovieV2OverviewCostDto(
    bool IsKnown,
    decimal? RecordedProviderCostUsd,
    decimal? EstimatedRemainingProviderCostUsd,
    string Currency,
    string? Note);

public sealed record MovieV2OverviewActionDto(
    string Key,
    string Label,
    string Reason,
    string Module,
    string Priority);

public sealed record MovieV2OverviewWarningDto(
    string Key,
    string Severity,
    string Label,
    string Detail,
    string? Module,
    Guid? EntityId,
    string? EntityType);

public sealed record MovieV2OverviewActivityDto(
    string Key,
    string Label,
    string Detail,
    DateTime OccurredAt,
    string? Module);

public sealed record MovieV2OverviewBlockedItemDto(
    Guid EntityId,
    string EntityType,
    string Label,
    string Detail,
    string Module);

public sealed record MovieV2OverviewDto(
    MovieV2OverviewProjectDto Project,
    MovieV2OverviewStoryDto Story,
    MovieV2OverviewResourceDto Cast,
    MovieV2OverviewResourceDto World,
    MovieV2OverviewCountsDto Scenes,
    MovieV2OverviewCountsDto Shots,
    MovieV2OverviewProgressDto Progress,
    MovieV2OverviewTakeSummaryDto Takes,
    MovieV2OverviewApprovalSummaryDto Approvals,
    MovieV2OverviewCostDto Cost,
    Guid? LatestOutputAssetId,
    IReadOnlyList<MovieV2OverviewActionDto> NextActions,
    IReadOnlyList<MovieV2OverviewWarningDto> Warnings,
    IReadOnlyList<MovieV2OverviewBlockedItemDto> BlockedItems,
    IReadOnlyList<MovieV2OverviewActivityDto> RecentActivity);

namespace Taslim.Api.Contracts;

public sealed record MovieMissingInsertGroundingDto(
    Guid? ApprovedStoryRevisionId,
    int? ApprovedStoryRevisionNumber,
    Guid? LockedGuideRevisionId,
    int? LockedGuideRevisionNumber,
    IReadOnlyList<MovieMissingInsertProductionKitReferenceDto> ProductionKits,
    bool IsComplete);

public sealed record MovieMissingInsertProductionKitReferenceDto(
    Guid ShotId,
    string PackageHash,
    int SchemaVersion);

public sealed record MovieMissingInsertGapDto(
    Guid Id,
    string Kind,
    Guid? TrackId,
    int TimelineInMilliseconds,
    int TimelineOutMilliseconds,
    int DurationMilliseconds,
    Guid? BeforeTimelineItemId,
    Guid? AfterTimelineItemId,
    Guid? BeforeShotId,
    Guid? AfterShotId,
    Guid? SceneId,
    string Label);

public sealed record MovieMissingInsertContinuityFindingDto(
    string Code,
    string Severity,
    Guid? BeforeShotId,
    Guid? AfterShotId,
    Guid? SceneId,
    string Message,
    string Action);

public sealed record MovieMissingInsertProposalGroundingDto(
    Guid? StorySceneId,
    Guid? ApprovedStoryRevisionId,
    Guid? LockedGuideRevisionId,
    Guid? AnchorShotId,
    string? ProductionKitHash,
    IReadOnlyList<string> SourceFields);

public sealed record MovieMissingInsertProposalDto(
    Guid Id,
    Guid GapId,
    string ApprovalStatus,
    bool RequiresApproval,
    bool ChangesStoryCanon,
    string InsertType,
    Guid? SceneId,
    Guid? StorySceneId,
    Guid? AnchorShotId,
    int DurationMilliseconds,
    string Description,
    string Purpose,
    string? Subjects,
    string? LocationSet,
    string? ProductionRequirements,
    string? ContinuityReferences,
    string? CameraAndFraming,
    string? CameraMotion,
    string? VisualContinuityNotes,
    MovieMissingInsertProposalGroundingDto Grounding,
    Guid? AnchorTakeId = null,
    Guid? AnchorSelectId = null,
    string? ContinuityAnchorJson = null,
    string? ScreenDirectionAnchorJson = null);

public sealed record MovieMissingInsertWarningDto(
    string Code,
    string Severity,
    string Message,
    Guid? GapId = null);

public sealed record MovieMissingInsertPlanDto(
    string ContractVersion,
    Guid MovieProjectId,
    Guid? TimelineRevisionId,
    int? TimelineRevisionNumber,
    string TimelineStatus,
    bool RequiresApproval,
    bool CanonicalTimelineChanged,
    bool GenerationQueued,
    MovieMissingInsertGroundingDto Grounding,
    IReadOnlyList<MovieMissingInsertGapDto> Gaps,
    IReadOnlyList<MovieMissingInsertContinuityFindingDto> ContinuityFindings,
    IReadOnlyList<MovieMissingInsertProposalDto> Proposals,
    IReadOnlyList<MovieMissingInsertWarningDto> Warnings,
    DateTime AssembledAtUtc);

public sealed class MovieMissingInsertMaterializeRequest
{
    public Guid? TimelineRevisionId { get; set; }
}

public sealed class MovieMissingInsertReviewRequest
{
    public string Decision { get; set; } = "Approved";
    public Guid? TakeId { get; set; }
    public Guid? SelectId { get; set; }
    public string? Comment { get; set; }
}

public sealed record MovieMissingInsertDecisionDto(
    Guid Id,
    Guid MovieProjectId,
    Guid ProposalId,
    Guid GapId,
    Guid TimelineRevisionId,
    int TimelineRevisionNumber,
    Guid TrackId,
    Guid? BeforeTimelineItemId,
    Guid? AfterTimelineItemId,
    int TimelineInMilliseconds,
    int TimelineOutMilliseconds,
    Guid? BeforeShotId,
    Guid? AfterShotId,
    Guid? SceneId,
    Guid? StorySceneId,
    Guid? AnchorShotId,
    Guid? ApprovedStoryRevisionId,
    Guid? LockedGuideRevisionId,
    string? ProductionKitHash,
    int? ProductionKitSchemaVersion,
    Guid? AnchorTakeId,
    Guid? AnchorSelectId,
    Guid? SelectedTakeId,
    Guid? SelectedSelectId,
    string ContractVersion,
    string InsertType,
    int DurationMilliseconds,
    string Description,
    string Purpose,
    string ProposalGroundingJson,
    string ContinuityAnchorJson,
    string ScreenDirectionAnchorJson,
    string Status,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    Guid? ReviewedByUserId,
    DateTime? ReviewedAt,
    string? ReviewNote,
    Guid? AppliedByUserId,
    DateTime? AppliedAt,
    Guid? AppliedTimelineRevisionId);

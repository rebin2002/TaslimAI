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
    MovieMissingInsertProposalGroundingDto Grounding);

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

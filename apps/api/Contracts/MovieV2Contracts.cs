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

public sealed record MovieScenesWorkspaceDto(
    Guid MovieProjectId,
    string ProjectTitle,
    string ProductionStatus,
    string ScreenplayApprovalState,
    Guid? ApprovedRevisionId,
    int? ApprovedRevisionNumber,
    IReadOnlyList<MovieScenesActDto> Acts,
    int SceneCount,
    int LinkedSceneCount,
    int ShotCount);

public sealed record MovieScenesActDto(
    Guid Id,
    int Sequence,
    string Title,
    string? Summary,
    string Status,
    IReadOnlyList<MovieScenesSequenceDto> Sequences);

public sealed record MovieScenesSequenceDto(
    Guid Id,
    int Sequence,
    string Title,
    string? Summary,
    string Status,
    IReadOnlyList<MovieSceneWorkspaceDto> Scenes);

public sealed record MovieSceneWorkspaceDto(
    Guid Id,
    int Sequence,
    string Title,
    string Slug,
    string Description,
    string? Purpose,
    int? DurationSeconds,
    string ProductionStatus,
    string ApprovalState,
    string StoryPosition,
    int ActSequence,
    int SequencePosition,
    Guid? MovieSequenceId,
    Guid? ScreenplaySceneId,
    string? ScreenplaySceneIdentifier,
    string? ScreenplaySource,
    string? ScreenplaySynopsis,
    int? ScreenplayRevisionNumber,
    IReadOnlyList<string> Characters,
    IReadOnlyList<MovieSceneWorldReferenceDto> Locations,
    IReadOnlyList<MovieSceneWorldReferenceDto> Sets,
    IReadOnlyList<string> ContinuityWarnings,
    int ShotCount,
    DateTime? ArchivedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MovieSceneWorldReferenceDto(Guid Id, string Name, string? Role);

public sealed record MovieScenesBreakdownDto(
    Guid MovieProjectId,
    Guid ApprovedRevisionId,
    int ApprovedRevisionNumber,
    int CreatedSceneCount,
    int ExistingLinkedSceneCount,
    MovieScenesWorkspaceDto Workspace);

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

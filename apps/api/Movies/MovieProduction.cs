using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieProductionStages
{
    public const string ShotPlan = "ShotPlan";
    public const string StoryboardCandidate = "StoryboardCandidate";
    public const string ApprovedStoryboard = "ApprovedStoryboard";
    public const string ProductionKeyframe = "ProductionKeyframe";
    public const string ApprovedKeyframe = "ApprovedKeyframe";
    public const string MotionPreview = "MotionPreview";
    public const string ProductionRender = "ProductionRender";
    public const string SelectedFinalTake = "SelectedFinalTake";

    public static readonly IReadOnlySet<string> Persisted = new HashSet<string>(StringComparer.Ordinal)
    {
        ShotPlan, StoryboardCandidate, ApprovedStoryboard, ProductionKeyframe,
        ApprovedKeyframe, MotionPreview, ProductionRender, SelectedFinalTake,
    };
}

public static class MovieProductionVersionStatuses
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Selected = "Selected";
}

public static class MovieProductionAssetRoles
{
    public const string Composition = "composition";
    public const string FirstFrame = "first_frame";
    public const string LastFrame = "last_frame";
    public const string Reference = "reference";
    public const string Output = "output";
}

public sealed class MovieProductionVersion
{
    public Guid Id { get; set; }
    public Guid MovieShotId { get; set; }
    public int VersionNumber { get; set; }
    public string Stage { get; set; } = MovieProductionStages.StoryboardCandidate;
    public string Status { get; set; } = MovieProductionVersionStatuses.PendingApproval;
    public string? Label { get; set; }
    public string CompositionJson { get; set; } = "{}";
    public string? RegenerationMetadataJson { get; set; }
    public string? StageProvenanceJson { get; set; }
    public Guid? SourceVersionId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? FirstFrameAssetId { get; set; }
    public Guid? LastFrameAssetId { get; set; }
    public string? FirstFrameNotes { get; set; }
    public string? LastFrameNotes { get; set; }
    public string? RejectionReason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public MovieShot MovieShot { get; set; } = null!;
    public MovieProductionVersion? SourceVersion { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public Asset? Asset { get; set; }
    public Asset? FirstFrameAsset { get; set; }
    public Asset? LastFrameAsset { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
    public ICollection<MovieProductionVersionAsset> AssetReferences { get; set; } = [];
    public ICollection<MovieProductionStageTransition> Transitions { get; set; } = [];
}

public sealed class MovieProductionVersionAsset
{
    public Guid MovieProductionVersionId { get; set; }
    public Guid AssetId { get; set; }
    public string Role { get; set; } = MovieProductionAssetRoles.Reference;
    public DateTime CreatedAt { get; set; }
    public MovieProductionVersion MovieProductionVersion { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}

public sealed class MovieProductionStageTransition
{
    public Guid Id { get; set; }
    public Guid MovieShotId { get; set; }
    public Guid MovieProductionVersionId { get; set; }
    public string FromStage { get; set; } = MovieProductionStages.ShotPlan;
    public string ToStage { get; set; } = MovieProductionStages.StoryboardCandidate;
    public string EventType { get; set; } = "created";
    public string? Reason { get; set; }
    public string? MetadataJson { get; set; }
    public Guid? SourceVersionId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieShot MovieShot { get; set; } = null!;
    public MovieProductionVersion MovieProductionVersion { get; set; } = null!;
    public MovieProductionVersion? SourceVersion { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public ApplicationUser ActorUser { get; set; } = null!;
}

public static class MovieProductionWorkflow
{
    public static string? ValidateVersionCreation(string stage, MovieProductionVersion? source)
    {
        if (!MovieProductionStages.Persisted.Contains(stage)) return "Choose a supported production stage.";
        if (stage is MovieProductionStages.ShotPlan or MovieProductionStages.ApprovedStoryboard or MovieProductionStages.ApprovedKeyframe or MovieProductionStages.SelectedFinalTake)
            return "This stage is reached through the workflow and cannot be created directly.";
        if (stage == MovieProductionStages.StoryboardCandidate) return null;
        if (source is null) return "This production stage requires an approved source version.";
        if (source.Status != MovieProductionVersionStatuses.Approved) return "The source version must be approved before advancing the shot.";
        return stage switch
        {
            MovieProductionStages.ProductionKeyframe when source.Stage == MovieProductionStages.ApprovedStoryboard => null,
            MovieProductionStages.MotionPreview when source.Stage == MovieProductionStages.ApprovedKeyframe => null,
            MovieProductionStages.ProductionRender when source.Stage == MovieProductionStages.MotionPreview => null,
            _ => "The source version is not approved for this production stage.",
        };
    }

    public static (string NextStage, string Status)? ApprovalResult(string stage) => stage switch
    {
        MovieProductionStages.StoryboardCandidate => (MovieProductionStages.ApprovedStoryboard, MovieProductionVersionStatuses.Approved),
        MovieProductionStages.ProductionKeyframe => (MovieProductionStages.ApprovedKeyframe, MovieProductionVersionStatuses.Approved),
        MovieProductionStages.MotionPreview => (MovieProductionStages.MotionPreview, MovieProductionVersionStatuses.Approved),
        MovieProductionStages.ProductionRender => (MovieProductionStages.SelectedFinalTake, MovieProductionVersionStatuses.Selected),
        _ => null,
    };

    public static string? ValidateReview(string stage, string status)
    {
        if (status is not (MovieProductionVersionStatuses.PendingApproval or MovieProductionVersionStatuses.Rejected))
            return "Only pending or rejected versions can be reviewed.";
        return ApprovalResult(stage) is null ? "This version cannot be approved at its current stage." : null;
    }

    public static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class MovieProductionValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MovieProductionAssetReferenceDto(Guid AssetId, string Role);
public sealed record MovieProductionVersionDto(
    Guid Id,
    Guid MovieShotId,
    int VersionNumber,
    string Stage,
    string Status,
    string? Label,
    string CompositionJson,
    string? RegenerationMetadataJson,
    string? StageProvenanceJson,
    Guid? SourceVersionId,
    Guid? GenerationJobId,
    Guid? AssetId,
    Guid? FirstFrameAssetId,
    Guid? LastFrameAssetId,
    string? FirstFrameNotes,
    string? LastFrameNotes,
    string? RejectionReason,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ReviewedAt,
    IReadOnlyList<MovieProductionAssetReferenceDto> AssetReferences);
public sealed record MovieProductionStageTransitionDto(
    Guid Id,
    Guid MovieShotId,
    Guid MovieProductionVersionId,
    string FromStage,
    string ToStage,
    string EventType,
    string? Reason,
    string? MetadataJson,
    Guid? SourceVersionId,
    Guid? GenerationJobId,
    Guid ActorUserId,
    DateTime CreatedAt);
public sealed record MovieShotProductionDto(
    Guid MovieShotId,
    string CurrentStage,
    IReadOnlyList<MovieProductionVersionDto> Versions,
    IReadOnlyList<MovieProductionStageTransitionDto> Transitions);

public sealed record MovieStoryboardCandidateDto(
    Guid Id,
    Guid MovieShotId,
    int VersionNumber,
    string Stage,
    string Status,
    string? Label,
    string CompositionJson,
    string? RegenerationMetadataJson,
    string? StageProvenanceJson,
    Guid? SourceVersionId,
    Guid? AssetId,
    Guid? FirstFrameAssetId,
    Guid? LastFrameAssetId,
    string? FirstFrameNotes,
    string? LastFrameNotes,
    string? RejectionReason,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ReviewedAt,
    IReadOnlyList<MovieProductionAssetReferenceDto> AssetReferences);

public sealed record MovieCinematographySummaryDto(
    string? CameraAndFraming,
    string? CameraMotion,
    string? Intent,
    string? ShotSize,
    string? FocalLength,
    string? CameraAngle,
    string? Lighting,
    string? PaletteLook,
    string? CompositionNotes);

public sealed record MovieStoryboardShotDto(
    Guid Id,
    int Sequence,
    string Description,
    string ShotPlanStatus,
    string CurrentStage,
    int? DurationSeconds,
    IReadOnlyList<string> ContinuityWarnings,
    MovieCinematographySummaryDto Cinematography,
    IReadOnlyList<MovieStoryboardCandidateDto> Candidates,
    Guid? ApprovedCandidateId,
    string ApprovalStatus);

public sealed record MovieStoryboardSceneDto(
    Guid Id,
    int Sequence,
    string Title,
    string Summary,
    int? DurationSeconds,
    string? ContinuityNotes,
    IReadOnlyList<MovieStoryboardShotDto> Shots);

public sealed record MovieStoryboardProjectDto(
    Guid Id,
    Guid WorkspaceId,
    string Status,
    string Title,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    MovieGuideDto Guide,
    bool ProviderReady,
    IReadOnlyList<MovieStoryboardSceneDto> Scenes);

public sealed class MovieProductionVersionRequest
{
    public string Stage { get; set; } = MovieProductionStages.StoryboardCandidate;
    public string? Label { get; set; }
    public string CompositionJson { get; set; } = "{}";
    public string? RegenerationMetadataJson { get; set; }
    public string? StageProvenanceJson { get; set; }
    public Guid? SourceVersionId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? FirstFrameAssetId { get; set; }
    public Guid? LastFrameAssetId { get; set; }
    public string? FirstFrameNotes { get; set; }
    public string? LastFrameNotes { get; set; }
    public IReadOnlyList<MovieProductionAssetReferenceDto>? AssetReferences { get; set; }
}

public sealed class MovieProductionReviewRequest
{
    public bool Approve { get; set; }
    public string? Reason { get; set; }
    public string? MetadataJson { get; set; }
}

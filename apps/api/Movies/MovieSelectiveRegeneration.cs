using System.Text.Json;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public static class MovieRegenerationActionTypes
{
    public const string StoryboardCandidate = "storyboard_candidate";
    public const string ProductionKeyframeCandidate = "production_keyframe_candidate";
    public const string MotionPreview = "motion_preview";
    public const string ProductionRender = "production_render";
    public const string CharacterContinuity = "character_continuity";
    public const string WorldContinuity = "world_continuity";
    public const string Cinematography = "cinematography";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        StoryboardCandidate,
        ProductionKeyframeCandidate,
        MotionPreview,
        ProductionRender,
        CharacterContinuity,
        WorldContinuity,
        Cinematography,
    };

    public static bool IsContinuityOrDirection(string actionType) => actionType is CharacterContinuity or WorldContinuity or Cinematography;
}

public static class MovieRegenerationStatuses
{
    public const string PendingConfirmation = "PendingConfirmation";
    public const string Confirmed = "Confirmed";
}

public sealed class MovieRegenerationRequest
{
    public Guid Id { get; set; }
    public Guid MovieShotId { get; set; }
    public string TargetType { get; set; } = "shot";
    public Guid TargetId { get; set; }
    public string ActionType { get; set; } = MovieRegenerationActionTypes.StoryboardCandidate;
    public string RequestedStage { get; set; } = MovieProductionStages.StoryboardCandidate;
    public string Reason { get; set; } = string.Empty;
    public Guid? SourceVersionId { get; set; }
    public string ChangedInputsJson { get; set; } = "{}";
    public string CompositionJson { get; set; } = "{}";
    public decimal? EstimatedProviderCostUsd { get; set; }
    public bool EstimatedProviderCostKnown { get; set; }
    public string? CostEstimateJson { get; set; }
    public string Status { get; set; } = MovieRegenerationStatuses.PendingConfirmation;
    public Guid CreatedByUserId { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? ResultingProductionVersionId { get; set; }
    public Guid? ResultingTakeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public MovieShot MovieShot { get; set; } = null!;
    public MovieProductionVersion? SourceVersion { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ConfirmedByUser { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public MovieProductionVersion? ResultingProductionVersion { get; set; }
    public MovieTake? ResultingTake { get; set; }
}

public sealed record MovieRegenerationCostPreviewDto(
    decimal? EstimatedProviderCostUsd,
    bool EstimatedProviderCostKnown,
    string Currency,
    string? CostEstimateJson,
    bool ConfirmationRequired);

public sealed record MovieRegenerationRequestDto(
    Guid Id,
    Guid MovieShotId,
    string TargetType,
    Guid TargetId,
    string ActionType,
    string RequestedStage,
    string Reason,
    Guid? SourceVersionId,
    string ChangedInputsJson,
    string CompositionJson,
    string Status,
    Guid CreatedByUserId,
    Guid? ConfirmedByUserId,
    Guid? GenerationJobId,
    Guid? ResultingProductionVersionId,
    Guid? ResultingTakeId,
    DateTime CreatedAt,
    DateTime? ConfirmedAt,
    MovieRegenerationCostPreviewDto CostPreview);

public sealed record MovieSelectiveRegenerationResponse(
    MovieRegenerationRequestDto Request,
    GenerationJobDto? Job,
    MovieProductionVersionDto? ProductionVersion,
    MovieV2TakeDto? Take);

public sealed class MovieRegenerationRequestInput
{
    public string ActionType { get; set; } = MovieRegenerationActionTypes.StoryboardCandidate;
    public string RequestedStage { get; set; } = MovieProductionStages.StoryboardCandidate;
    public string Reason { get; set; } = string.Empty;
    public Guid? SourceVersionId { get; set; }
    public string ChangedInputsJson { get; set; } = "{}";
    public string CompositionJson { get; set; } = "{}";
    public decimal? EstimatedProviderCostUsd { get; set; }
    public GenerationCostEstimate? InternalCostEstimate { get; set; }
}

public sealed class MovieRegenerationConfirmationRequest
{
    public bool Confirm { get; set; }
}

public static class MovieRegenerationWorkflow
{
    public static string? ValidateAction(string actionType, string stage)
    {
        if (!MovieRegenerationActionTypes.Supported.Contains(actionType)) return "Choose a supported selective regeneration action.";
        if (!MovieProductionStages.Persisted.Contains(stage) || stage is MovieProductionStages.ShotPlan or MovieProductionStages.ApprovedStoryboard or MovieProductionStages.ApprovedKeyframe or MovieProductionStages.SelectedFinalTake)
            return "Choose a candidate production stage for the selective action.";
        if (actionType.Equals(MovieRegenerationActionTypes.StoryboardCandidate, StringComparison.OrdinalIgnoreCase) && stage != MovieProductionStages.StoryboardCandidate)
            return "A storyboard candidate action must create a storyboard candidate.";
        if (actionType.Equals(MovieRegenerationActionTypes.ProductionKeyframeCandidate, StringComparison.OrdinalIgnoreCase) && stage != MovieProductionStages.ProductionKeyframe)
            return "A production keyframe action must create a production keyframe candidate.";
        if (actionType.Equals(MovieRegenerationActionTypes.MotionPreview, StringComparison.OrdinalIgnoreCase) && stage != MovieProductionStages.MotionPreview)
            return "A motion preview action must create a motion preview candidate.";
        if (actionType.Equals(MovieRegenerationActionTypes.ProductionRender, StringComparison.OrdinalIgnoreCase) && stage != MovieProductionStages.ProductionRender)
            return "A production render action must create a production render candidate.";
        return null;
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

    public static bool HasChangedInputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Any();
    }
}

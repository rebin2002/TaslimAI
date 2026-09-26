using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public static class DirectorQualityLevels
{
    public const string Fast = "Fast";
    public const string Standard = "Standard";
    public const string Cinematic = "Cinematic";
    public const string Studio = "Studio";
    public const string Auto = "Auto";

    public static readonly IReadOnlySet<string> Selectable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Fast, Standard, Cinematic, Studio, Auto,
    };
}

public static class DirectorProposalStatuses
{
    public const string PendingApproval = "PendingApproval";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
}

public static class DirectorActionStatuses
{
    public const string PendingApproval = "PendingApproval";
    public const string Ready = "Ready";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public static class DirectorActionTypes
{
    public const string GenerateShot = "generate_shot";
}

public static class DirectorHistoryEventTypes
{
    public const string ContextAssembled = "context_assembled";
    public const string ProposalCreated = "proposal_created";
    public const string ProposalApproved = "proposal_approved";
    public const string ProposalRejected = "proposal_rejected";
    public const string ActionReady = "action_ready";
    public const string ActionStarted = "action_started";
    public const string ActionSucceeded = "action_succeeded";
    public const string ActionFailed = "action_failed";
}

public sealed class DirectorProjectContext
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid MovieProjectId { get; set; }
    public int ContextVersion { get; set; } = 1;
    public string SnapshotJson { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public MovieProject MovieProject { get; set; } = null!;
    public ICollection<DirectorProposal> Proposals { get; set; } = [];
    public ICollection<DirectorDecision> Decisions { get; set; } = [];
}

public sealed class DirectorProposal
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid DirectorProjectContextId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Status { get; set; } = DirectorProposalStatuses.PendingApproval;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string RationaleJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public MovieProject MovieProject { get; set; } = null!;
    public DirectorProjectContext Context { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ICollection<DirectorAction> Actions { get; set; } = [];
}

public sealed class DirectorAction
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid DirectorProposalId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string Status { get; set; } = DirectorActionStatuses.PendingApproval;
    public bool ApprovalRequired { get; set; } = true;
    public string PayloadJson { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public MovieProject MovieProject { get; set; } = null!;
    public DirectorProposal Proposal { get; set; } = null!;
    public ICollection<DirectorActionResult> Results { get; set; } = [];
    public ICollection<DirectorHistoryEvent> History { get; set; } = [];
}

public sealed class DirectorActionResult
{
    public Guid Id { get; set; }
    public Guid DirectorActionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ResultJson { get; set; }
    public string SafeMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public DirectorAction Action { get; set; } = null!;
}

public sealed class DirectorDecision
{
    public Guid Id { get; set; }
    public Guid DirectorProjectContextId { get; set; }
    public Guid? MovieShotId { get; set; }
    public string DecisionType { get; set; } = string.Empty;
    public string QualityLevel { get; set; } = DirectorQualityLevels.Fast;
    public string RationaleJson { get; set; } = "[]";
    public decimal? EstimatedCostUsd { get; set; }
    public DateTime CreatedAt { get; set; }

    public DirectorProjectContext Context { get; set; } = null!;
    public MovieShot? MovieShot { get; set; }
}

public sealed class DirectorHistoryEvent
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? DirectorProposalId { get; set; }
    public Guid? DirectorActionId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? SafeDetailsJson { get; set; }
    public DateTime CreatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public DirectorProposal? Proposal { get; set; }
    public DirectorAction? Action { get; set; }
}

public sealed record DirectorContextDto(
    Guid MovieProjectId,
    Guid WorkspaceId,
    string Title,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    DirectorGuideContext Guide,
    IReadOnlyList<DirectorSceneContext> Scenes,
    IReadOnlyList<DirectorCharacterContext> Characters,
    IReadOnlyList<DirectorLocationContext> Locations,
    DateTime AssembledAt,
    int ContextVersion = 1,
    DirectorStoryContext? ApprovedStory = null,
    MovieWorldContinuitySnapshotDto? WorldContinuity = null);

public sealed record DirectorGuideContext(
    string VisualLanguage,
    string CameraLanguage,
    string ColorAndLighting,
    string SoundAndNarration,
    string ContinuityRules,
    int? RevisionNumber = null,
    bool IsAuthoritative = false,
    string? CinematographyBibleJson = null);
public sealed record DirectorStoryContext(Guid RevisionId, int RevisionNumber, string Premise, string Logline, string Synopsis, string Treatment, string Authorship);
public sealed record DirectorSceneContext(Guid Id, int Sequence, string Title, string Summary, int? DurationSeconds, string? ContinuityNotes, IReadOnlyList<DirectorShotContext> Shots);
public sealed record DirectorShotContext(Guid Id, int Sequence, string Description, string? CameraAndFraming, string? CameraMotion, int? DurationSeconds, string? Narration, string? Dialogue, string? VisualContinuityNotes);
public sealed record DirectorCharacterContext(string Name, string Description, string? Appearance, string? ContinuityNotes);
public sealed record DirectorLocationContext(string Name, string Description, string? VisualContinuityNotes);

public sealed record DirectorContextAssemblyResult(DirectorContextDto Context, string SnapshotJson, string SnapshotHash);

public sealed record DirectorCostEstimate(bool IsKnown, decimal? AmountUsd, string Currency, string? UnknownReason);
public sealed record DirectorCostRequest(int DurationSeconds, string QualityLevel);

public interface IDirectorCostEstimator
{
    DirectorCostEstimate Estimate(DirectorCostRequest request);
}

public sealed record DirectorShotPlanningRequest(
    Guid? ShotId,
    int Importance,
    int Complexity,
    int BudgetSensitivity,
    int DurationSeconds,
    bool RequiresContinuity,
    string RequestedQuality = DirectorQualityLevels.Auto,
    decimal? BudgetLimitUsd = null);

public sealed record DirectorQualityRecommendation(
    string QualityLevel,
    string SelectionMode,
    decimal ImportanceScore,
    decimal ComplexityScore,
    decimal BudgetSensitivityScore,
    decimal? EstimatedCostUsd,
    IReadOnlyList<string> Reasons,
    bool BudgetConstrained);

public sealed class DirectorQualityPlanner(IDirectorCostEstimator costEstimator)
{
    public DirectorQualityRecommendation Recommend(DirectorShotPlanningRequest request)
    {
        var importance = Normalize(request.Importance);
        var complexity = Normalize(request.Complexity);
        var budgetSensitivity = Normalize(request.BudgetSensitivity);
        var score = decimal.Round((importance * 0.5m) + (complexity * 0.3m) + ((1m - budgetSensitivity) * 0.2m), 2);
        var requested = NormalizeQuality(request.RequestedQuality);
        var level = requested == DirectorQualityLevels.Auto
            ? score >= 0.88m ? DirectorQualityLevels.Studio
            : score >= 0.68m ? DirectorQualityLevels.Cinematic
            : score >= 0.38m ? DirectorQualityLevels.Standard
            : DirectorQualityLevels.Fast
            : requested;
        var reasons = new List<string>
        {
            importance >= 0.7m ? "high_story_importance" : "lower_story_importance",
            complexity >= 0.7m ? "high_shot_complexity" : "manageable_shot_complexity",
            request.RequiresContinuity ? "continuity_sensitive" : "continuity_flexible",
        };
        var estimate = costEstimator.Estimate(new DirectorCostRequest(Math.Clamp(request.DurationSeconds, 1, 3600), level));
        var constrained = false;
        if (request.BudgetLimitUsd.HasValue && estimate.IsKnown && estimate.AmountUsd > request.BudgetLimitUsd.Value && level != DirectorQualityLevels.Fast)
        {
            level = DirectorQualityLevels.Fast;
            constrained = true;
            reasons.Add("budget_limit_selected_lower_quality");
            estimate = costEstimator.Estimate(new DirectorCostRequest(Math.Clamp(request.DurationSeconds, 1, 3600), level));
        }
        if (budgetSensitivity >= 0.7m) reasons.Add("budget_sensitive");
        return new DirectorQualityRecommendation(level, requested, importance, complexity, budgetSensitivity, estimate.AmountUsd, reasons, constrained);
    }

    private static decimal Normalize(int value) => Math.Clamp(value, 0, 100) / 100m;
    private static string NormalizeQuality(string value) => DirectorQualityLevels.Selectable.FirstOrDefault(item => string.Equals(item, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DirectorQualityLevels.Auto;
}

public sealed class MovieDirectorCostEstimator(IMovieVideoProvider provider, IGenerationCostEstimator estimator) : IDirectorCostEstimator
{
    public DirectorCostEstimate Estimate(DirectorCostRequest request)
    {
        if (!provider.IsAvailable) return new(false, null, UsageCurrencies.Usd, "movie_capability_unavailable");
        var duration = Math.Max(1, request.DurationSeconds) * QualityMultiplier(request.QualityLevel);
        var estimate = estimator.Estimate(new GenerationCostEstimationRequest(provider.Key, VideoDurationSeconds: duration));
        return new DirectorCostEstimate(estimate.IsKnown, estimate.AmountUsd, estimate.Currency, estimate.UnknownReason);
    }

    private static decimal QualityMultiplier(string quality) => quality switch
    {
        DirectorQualityLevels.Studio => 1.5m,
        DirectorQualityLevels.Cinematic => 1.25m,
        DirectorQualityLevels.Standard => 1m,
        _ => 0.75m,
    };
}

public sealed class DirectorValidationException(string message) : Exception(message);
public sealed class DirectorActionNotApprovedException() : Exception("The Director action requires explicit user approval.");
public sealed class DirectorActionExecutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class DirectorProposalRequest
{
    public Guid? ShotId { get; set; }
    public string? Goal { get; set; }
    public string RequestedQuality { get; set; } = DirectorQualityLevels.Auto;
    public decimal? BudgetLimitUsd { get; set; }
    public int Importance { get; set; } = 50;
    public int Complexity { get; set; } = 50;
    public int BudgetSensitivity { get; set; } = 50;
}

public sealed record DirectorPlanItemDto(Guid ShotId, int Sequence, string Description, DirectorQualityRecommendation Recommendation);
public sealed record DirectorActionDto(Guid Id, Guid ProposalId, string ActionType, string Status, bool ApprovalRequired, string? FailureCode, DateTime CreatedAt, DateTime? ApprovedAt, DateTime? StartedAt, DateTime? CompletedAt, IReadOnlyList<DirectorActionResultDto> Results);
public sealed record DirectorActionResultDto(Guid Id, string Status, string SafeMessage, string? ResultJson, DateTime CreatedAt);
public sealed record DirectorProposalDto(Guid Id, Guid MovieProjectId, string Status, string Title, string Summary, IReadOnlyList<string> Rationale, IReadOnlyList<DirectorPlanItemDto> Plan, IReadOnlyList<DirectorActionDto> Actions, DateTime CreatedAt, DateTime? ApprovedAt);
public sealed record DirectorProposalResponse(DirectorProposalDto Proposal, DirectorContextDto Context);
public sealed record DirectorHistoryDto(Guid Id, string EventType, string? SafeDetailsJson, DateTime CreatedAt);
public sealed record DirectorActionExecutionResponse(DirectorActionDto Action, DirectorActionResultDto Result);

public sealed record DirectorGenerateShotPayload(Guid ShotId, string QualityLevel, int DurationSeconds, decimal? EstimatedCostUsd);

public static class DirectorJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

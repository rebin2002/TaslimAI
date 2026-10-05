using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieMissingInsertDecisionStatuses
{
    public const string PendingReview = "PendingReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Applied = "Applied";
}

/// <summary>
/// Durable review state for one server-produced missing-insert proposal. This stores
/// bounded provenance and references only; it never copies Movie hierarchy, Story,
/// Guide, World, media, provider, prompt, or charging state.
/// </summary>
public sealed class MovieMissingInsertDecision
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid GapId { get; set; }
    public Guid TimelineRevisionId { get; set; }
    public int TimelineRevisionNumber { get; set; }
    public Guid TrackId { get; set; }
    public Guid? BeforeTimelineItemId { get; set; }
    public Guid? AfterTimelineItemId { get; set; }
    public int TimelineInMilliseconds { get; set; }
    public int TimelineOutMilliseconds { get; set; }
    public Guid? BeforeShotId { get; set; }
    public Guid? AfterShotId { get; set; }
    public Guid? SceneId { get; set; }
    public Guid? StorySceneId { get; set; }
    public Guid? AnchorShotId { get; set; }
    public Guid? ApprovedStoryRevisionId { get; set; }
    public Guid? LockedGuideRevisionId { get; set; }
    public string? ProductionKitHash { get; set; }
    public int? ProductionKitSchemaVersion { get; set; }
    public Guid? AnchorTakeId { get; set; }
    public Guid? AnchorSelectId { get; set; }
    public Guid? SelectedTakeId { get; set; }
    public Guid? SelectedSelectId { get; set; }
    public string ContractVersion { get; set; } = string.Empty;
    public string InsertType { get; set; } = string.Empty;
    public int DurationMilliseconds { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string ProposalGroundingJson { get; set; } = "{}";
    public string ContinuityAnchorJson { get; set; } = "{}";
    public string ScreenDirectionAnchorJson { get; set; } = "{}";
    public string Status { get; set; } = MovieMissingInsertDecisionStatuses.PendingReview;
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public Guid? AppliedByUserId { get; set; }
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedTimelineRevisionId { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
    public ApplicationUser? AppliedByUser { get; set; }
}

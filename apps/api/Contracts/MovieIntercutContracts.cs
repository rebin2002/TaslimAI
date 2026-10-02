using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

/// <summary>
/// Review-only input for Director intercut planning. The caller supplies the canonical shot/clip
/// candidates that are already independently planned; this contract never starts generation.
/// </summary>
public sealed class MovieIntercutPlanRequest
{
    public Guid? TimelineId { get; set; }
    public int BaseTimelineVersion { get; set; } = 1;
    public IReadOnlyList<MovieIntercutCoverageRequest> Clips { get; set; } = [];
    public IReadOnlyList<Guid>? RequestedClipOrder { get; set; }
}

public sealed class MovieIntercutCoverageRequest
{
    public Guid ClipId { get; set; }
    public Guid MovieShotId { get; set; }
    public Guid MovieSceneId { get; set; }
    public int SceneSequence { get; set; }
    public int ShotSequence { get; set; }
    public decimal DurationSeconds { get; set; }
    public string CoverageKind { get; set; } = MovieIntercutCoverageKinds.Primary;
    public Guid? AnchorShotId { get; set; }
    public string? Label { get; set; }
}

public sealed record MovieIntercutCoverageDto(
    Guid ClipId,
    Guid MovieShotId,
    Guid MovieSceneId,
    int SceneSequence,
    int ShotSequence,
    decimal DurationSeconds,
    string CoverageKind,
    Guid? AnchorShotId,
    string? Label);

public sealed record MovieIntercutPlanDto(
    Guid ProposalId,
    Guid MovieProjectId,
    Guid TimelineId,
    int BaseTimelineVersion,
    int ProposedTimelineVersion,
    MovieCanonicalTimelineContract ProposedTimeline,
    IReadOnlyList<MovieIntercutCoverageDto> Coverage,
    IReadOnlyList<Guid> RequestedClipOrder,
    bool StoryOrderPreserved,
    bool RequiresUserApproval,
    IReadOnlyList<string> Findings,
    string ProvenanceHash);

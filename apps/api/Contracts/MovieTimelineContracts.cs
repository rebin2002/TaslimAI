using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed class MovieTimelineRevisionRequest
{
    public Guid? BaseRevisionId { get; set; }
    public string? Label { get; set; }
    public string? ChangeSummary { get; set; }
    public IReadOnlyList<MovieTimelineTrackRequest>? Tracks { get; set; }
}

public sealed class MovieTimelineTrackRequest
{
    public string Kind { get; set; } = MovieTimelineTrackKinds.Video;
    public string? Name { get; set; }
    public int? TrackNumber { get; set; }
    public bool IsMuted { get; set; }
    public IReadOnlyList<MovieTimelineItemRequest> Items { get; set; } = [];
}

public sealed class MovieTimelineItemRequest
{
    public string Kind { get; set; } = MovieTimelineItemKinds.Gap;
    public Guid? SourceTakeId { get; set; }
    public Guid? SourceSelectId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public int TimelineInMilliseconds { get; set; }
    public int? TimelineOutMilliseconds { get; set; }
    public int? SourceInMilliseconds { get; set; }
    public int? SourceOutMilliseconds { get; set; }
    public string? Label { get; set; }
    public string? MetadataJson { get; set; }
}

public sealed class MovieTimelineTrackUpdateRequest
{
    public string? Name { get; set; }
    public bool? IsMuted { get; set; }
}

public sealed record MovieTimelineItemDto(
    Guid Id,
    int Sequence,
    string Kind,
    Guid? SourceTakeId,
    Guid? SourceSelectId,
    Guid? SourceAssetId,
    int TimelineInMilliseconds,
    int TimelineOutMilliseconds,
    int? SourceInMilliseconds,
    int? SourceOutMilliseconds,
    int DurationMilliseconds,
    string? Label,
    string? MetadataJson,
    bool IsGap);

public sealed record MovieTimelineTrackDto(
    Guid Id,
    int TrackNumber,
    string Kind,
    string? Name,
    bool IsMuted,
    IReadOnlyList<MovieTimelineItemDto> Items);

public sealed record MovieTimelineRevisionDto(
    Guid Id,
    Guid MovieProjectId,
    int RevisionNumber,
    Guid? BaseRevisionId,
    string Status,
    string? Label,
    string? ChangeSummary,
    int DurationMilliseconds,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LockedAt,
    Guid? LockedByUserId,
    IReadOnlyList<MovieTimelineTrackDto> Tracks);

public sealed record MovieTimelineDto(
    Guid Id,
    Guid MovieProjectId,
    int CurrentRevisionNumber,
    Guid? CurrentRevisionId,
    int? LockedRevisionNumber,
    Guid? LockedRevisionId,
    IReadOnlyList<MovieTimelineRevisionDto> Revisions,
    MovieTimelineRevisionDto? CurrentRevision);

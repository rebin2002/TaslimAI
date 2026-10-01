using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed class MovieTakeSelectRequest
{
    public string Label { get; set; } = string.Empty;
    public int StartMilliseconds { get; set; }
    public int EndMilliseconds { get; set; }
    public string? Notes { get; set; }
}

public sealed class MovieTakeSelectReviewRequest
{
    public string Decision { get; set; } = MovieTakeSelectStatuses.Approved;
    public string? Comment { get; set; }
}

public sealed record MovieTakeSelectDto(
    Guid Id,
    Guid MovieTakeId,
    int SelectNumber,
    string Label,
    string Status,
    int StartMilliseconds,
    int EndMilliseconds,
    int DurationMilliseconds,
    string? Notes,
    string ProvenanceJson,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? ReviewedByUserId,
    DateTime? ReviewedAt,
    string? ReviewNote);

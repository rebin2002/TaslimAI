using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieTakeSelectStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Archived = "Archived";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Approved, Rejected, Archived,
    };
}

public static class MovieTakeSelectErrors
{
    public const string Invalid = "MOVIE_TAKE_SELECT_INVALID";
    public const string SourceInvalid = "MOVIE_TAKE_SELECT_SOURCE_INVALID";
}

/// <summary>
/// A reviewable, reusable range inside one generated MovieTake. A select never changes
/// the take's approval or selected/final pointers; it is an additional edit decision.
/// </summary>
public sealed class MovieTakeSelect
{
    public Guid Id { get; set; }
    public Guid MovieTakeId { get; set; }
    public int SelectNumber { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = MovieTakeSelectStatuses.Draft;
    public int StartMilliseconds { get; set; }
    public int EndMilliseconds { get; set; }
    public string? Notes { get; set; }
    public string ProvenanceJson { get; set; } = "{}";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public MovieTake MovieTake { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ReviewedByUser { get; set; }
    public ICollection<MovieTimelineItem> TimelineItems { get; set; } = [];

    public int DurationMilliseconds => EndMilliseconds - StartMilliseconds;
}

internal static class MovieTakeSelectProvenance
{
    public static string Create(MovieTake take, Guid sourceAssetId, int startMilliseconds, int endMilliseconds, DateTime capturedAt) =>
        JsonSerializer.Serialize(new
        {
            sourceKind = "movie_take",
            movieTakeId = take.Id,
            movieClipId = take.MovieClipId,
            sourceAssetId,
            startMilliseconds,
            endMilliseconds,
            capturedAt,
        });
}

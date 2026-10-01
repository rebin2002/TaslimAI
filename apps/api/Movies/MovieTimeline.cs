using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieTimelineTrackKinds
{
    public const string Video = "Video";
    public const string Audio = "Audio";
    public const string Captions = "Captions";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Video, Audio, Captions,
    };
}

public static class MovieTimelineItemKinds
{
    public const string VisualTake = "VisualTake";
    public const string AudioAsset = "AudioAsset";
    public const string CaptionAsset = "CaptionAsset";
    public const string Gap = "Gap";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VisualTake, AudioAsset, CaptionAsset, Gap,
    };
}

public static class MovieTimelineRevisionStatuses
{
    public const string Draft = "Draft";
    public const string Locked = "Locked";
    public const string Superseded = "Superseded";
}

public static class MovieTimelineErrors
{
    public const string Invalid = "MOVIE_TIMELINE_INVALID";
    public const string Locked = "MOVIE_TIMELINE_LOCKED";
    public const string SourceInvalid = "MOVIE_TIMELINE_SOURCE_INVALID";
}

/// <summary>
/// The project-scoped authoritative edit model. Media is never copied or mutated by
/// the timeline; revisions retain references to selected takes and approved assets.
/// </summary>
public sealed class MovieTimeline
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public int CurrentRevisionNumber { get; set; }
    public Guid? CurrentRevisionId { get; set; }
    public int? LockedRevisionNumber { get; set; }
    public Guid? LockedRevisionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieTimelineRevision? CurrentRevision { get; set; }
    public MovieTimelineRevision? LockedRevision { get; set; }
    public ICollection<MovieTimelineRevision> Revisions { get; set; } = [];
}

public sealed class MovieTimelineRevision
{
    public Guid Id { get; set; }
    public Guid MovieTimelineId { get; set; }
    public int RevisionNumber { get; set; }
    public Guid? BaseRevisionId { get; set; }
    public string Status { get; set; } = MovieTimelineRevisionStatuses.Draft;
    public string? Label { get; set; }
    public string? ChangeSummary { get; set; }
    public int DurationMilliseconds { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public MovieTimeline Timeline { get; set; } = null!;
    public MovieTimelineRevision? BaseRevision { get; set; }
    public ICollection<MovieTimelineRevision> DerivedRevisions { get; set; } = [];
    public ICollection<MovieTimelineTrack> Tracks { get; set; } = [];
}

public sealed class MovieTimelineTrack
{
    public Guid Id { get; set; }
    public Guid MovieTimelineRevisionId { get; set; }
    public int TrackNumber { get; set; }
    public string Kind { get; set; } = MovieTimelineTrackKinds.Video;
    public string? Name { get; set; }
    public bool IsMuted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieTimelineRevision Revision { get; set; } = null!;
    public ICollection<MovieTimelineItem> Items { get; set; } = [];
}

public sealed class MovieTimelineItem
{
    public Guid Id { get; set; }
    public Guid MovieTimelineTrackId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = MovieTimelineItemKinds.Gap;
    public Guid? SourceTakeId { get; set; }
    public Guid? SourceSelectId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public int TimelineInMilliseconds { get; set; }
    public int TimelineOutMilliseconds { get; set; }
    public int? SourceInMilliseconds { get; set; }
    public int? SourceOutMilliseconds { get; set; }
    public int DurationMilliseconds { get; set; }
    public string? Label { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieTimelineTrack Track { get; set; } = null!;
    public MovieTake? SourceTake { get; set; }
    public MovieTakeSelect? SourceSelect { get; set; }
    public Asset? SourceAsset { get; set; }
}

public sealed class MovieTimelineValidationException(string code, string message, IReadOnlyList<MovieTimelineValidationError>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<MovieTimelineValidationError> Errors { get; } = errors ?? [];
}

internal static class MovieTimelineSourceRules
{
    public static bool IsApprovedAsset(Asset asset)
    {
        if (asset.Status != AssetStatus.Active || !asset.StoredFileId.HasValue) return false;
        if (string.IsNullOrWhiteSpace(asset.MetadataJson)) return true;
        try
        {
            using var document = JsonDocument.Parse(asset.MetadataJson);
            var root = document.RootElement;
            if (root.TryGetProperty("approved", out var approved) && approved.ValueKind == JsonValueKind.False) return false;
            if (root.TryGetProperty("approvalState", out var state) && state.ValueKind == JsonValueKind.String)
                return string.Equals(state.GetString(), "Approved", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsAudioAsset(Asset asset) =>
        string.Equals(asset.AssetType, AssetTypes.Audio, StringComparison.OrdinalIgnoreCase)
        || string.Equals(asset.AssetType, AssetTypes.Music, StringComparison.OrdinalIgnoreCase)
        || asset.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsCaptionAsset(Asset asset)
    {
        var mime = asset.MimeType?.Trim().ToLowerInvariant();
        return mime is "text/vtt" or "application/x-subrip" or "text/srt" or "application/ttml+xml"
            || asset.Name.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
            || asset.Name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
            || asset.Name.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryReadDurationMilliseconds(Asset? asset, out int durationMilliseconds)
    {
        durationMilliseconds = 0;
        if (asset is null || string.IsNullOrWhiteSpace(asset.MetadataJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(asset.MetadataJson);
            var root = document.RootElement;
            foreach (var property in new[] { "durationMilliseconds", "durationMs" })
            {
                if (root.TryGetProperty(property, out var value) && value.TryGetInt64(out var milliseconds)
                    && milliseconds is > 0 and <= int.MaxValue)
                {
                    durationMilliseconds = (int)milliseconds;
                    return true;
                }
            }
            if (root.TryGetProperty("durationSeconds", out var seconds) && seconds.TryGetDouble(out var duration)
                && duration > 0 && duration * 1000 <= int.MaxValue)
            {
                durationMilliseconds = checked((int)Math.Round(duration * 1000, MidpointRounding.AwayFromZero));
                return durationMilliseconds > 0;
            }
        }
        catch (JsonException) { }
        return false;
    }
}

namespace Taslim.Api.Contracts;

public sealed record MovieCaptionCueDto(
    Guid Id,
    Guid MovieCaptionTrackId,
    int Sequence,
    string StartTimecode,
    string EndTimecode,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    Guid? SpeakerCharacterId,
    string? SpeakerName,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    Guid? MovieTakeId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MovieCaptionTrackDto(
    Guid Id,
    Guid MovieProjectId,
    Guid? MovieAssemblyId,
    int Sequence,
    string Name,
    string TrackType,
    string Language,
    bool IsRtl,
    bool IsDefault,
    string Status,
    string? SourceFormat,
    string? SourceFileName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieCaptionCueDto> Cues);

public sealed record MovieCaptionTimelineCueDto(
    Guid TrackId,
    string TrackName,
    string Language,
    bool IsRtl,
    string TrackType,
    MovieCaptionCueDto Cue);

public sealed record MovieCaptionTimelineDto(
    Guid MovieProjectId,
    int TrackCount,
    IReadOnlyList<MovieCaptionTimelineCueDto> Cues);

public class MovieCaptionTrackRequest
{
    public string Name { get; set; } = string.Empty;
    public string TrackType { get; set; } = "Subtitle";
    public string Language { get; set; } = "en";
    public bool IsRtl { get; set; }
    public bool IsDefault { get; set; }
    public string Status { get; set; } = "Draft";
    public Guid? MovieAssemblyId { get; set; }
    public string? SourceFormat { get; set; }
    public string? SourceFileName { get; set; }
}

public sealed class MovieCaptionCueRequest
{
    public int Sequence { get; set; } = 1;
    public string StartTimecode { get; set; } = string.Empty;
    public string EndTimecode { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public Guid? SpeakerCharacterId { get; set; }
    public string? SpeakerName { get; set; }
    public Guid? MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public Guid? MovieTakeId { get; set; }
}

public sealed class MovieCaptionImportRequest : MovieCaptionTrackRequest
{
    public string Format { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

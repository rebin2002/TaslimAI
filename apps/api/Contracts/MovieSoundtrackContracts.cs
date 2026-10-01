using System.ComponentModel.DataAnnotations;
using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed class MovieSoundtrackCueRequest
{
    [Required]
    public Guid MovieSceneId { get; set; }
    public Guid? MovieActId { get; set; }
    [Required, StringLength(160, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;
    [StringLength(2_000)]
    public string? NarrativeIntent { get; set; }
    [Required, StringLength(80, MinimumLength = 1)]
    public string Mood { get; set; } = "neutral";
    [Range(0, 100)]
    public int Intensity { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal ActStartSeconds { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal SceneStartSeconds { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal TimelineStartSeconds { get; set; }
    [Range(typeof(decimal), "0.001", "3600")]
    public decimal DurationSeconds { get; set; } = 30;
    public IReadOnlyList<MovieSoundtrackDuckingIntentRequest>? DuckingIntents { get; set; }
}

public sealed class MovieSoundtrackCueUpdateRequest
{
    [StringLength(160, MinimumLength = 1)]
    public string? Title { get; set; }
    [StringLength(2_000)]
    public string? NarrativeIntent { get; set; }
    [StringLength(80, MinimumLength = 1)]
    public string? Mood { get; set; }
    [Range(0, 100)]
    public int? Intensity { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal? ActStartSeconds { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal? SceneStartSeconds { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal? TimelineStartSeconds { get; set; }
    [Range(typeof(decimal), "0.001", "3600")]
    public decimal? DurationSeconds { get; set; }
    public IReadOnlyList<MovieSoundtrackDuckingIntentRequest>? DuckingIntents { get; set; }
}

public sealed class MovieSoundtrackCueVersionRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Label { get; set; } = string.Empty;
    [StringLength(4_000)]
    public string? ArrangementIntent { get; set; }
    [StringLength(80, MinimumLength = 1)]
    public string? Mood { get; set; }
    [Range(0, 100)]
    public int? Intensity { get; set; }
    public Guid? AssetId { get; set; }
}

public sealed class MovieSoundtrackCueVersionReviewRequest
{
    [Required]
    public string Decision { get; set; } = MovieSoundtrackApprovalStates.Approved;
    [StringLength(4_000)]
    public string? Comment { get; set; }
}

public sealed class MovieSoundtrackDuckingIntentRequest
{
    [Required, StringLength(40)]
    public string TargetLane { get; set; } = MovieSoundtrackDuckingTargets.Dialogue;
    [Range(typeof(decimal), "0", "3600")]
    public decimal StartOffsetSeconds { get; set; }
    [Range(typeof(decimal), "0", "3600")]
    public decimal EndOffsetSeconds { get; set; }
    [Range(typeof(decimal), "0.001", "24")]
    public decimal DuckDecibels { get; set; } = 6;
    [Range(0, 10000)]
    public int AttackMilliseconds { get; set; } = 50;
    [Range(0, 10000)]
    public int ReleaseMilliseconds { get; set; } = 250;
    [StringLength(500)]
    public string? Rationale { get; set; }
}

public sealed record MovieSoundtrackDto(
    Guid MovieProjectId,
    bool MediaServiceAvailable,
    IReadOnlyList<MovieSoundtrackCueDto> Cues);

public sealed record MovieSoundtrackCueDto(
    Guid Id,
    Guid MovieProjectId,
    Guid MovieActId,
    Guid MovieSceneId,
    int Sequence,
    string Title,
    string? NarrativeIntent,
    string Mood,
    int Intensity,
    decimal ActStartSeconds,
    decimal SceneStartSeconds,
    decimal TimelineStartSeconds,
    decimal DurationSeconds,
    string ApprovalState,
    Guid? ApprovedVersionId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<MovieSoundtrackDuckingIntentDto> DuckingIntents,
    IReadOnlyList<MovieSoundtrackCueVersionDto> Versions);

public sealed record MovieSoundtrackCueVersionDto(
    Guid Id,
    int VersionNumber,
    string Label,
    string? ArrangementIntent,
    string Mood,
    int Intensity,
    Guid? AssetId,
    string ApprovalState,
    string? ReviewNote,
    Guid CreatedByUserId,
    Guid? ReviewedByUserId,
    DateTime? ReviewedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    MovieSoundtrackAudioAssetProvenanceDto? AudioAssetProvenance,
    IReadOnlyList<MovieSoundtrackCueVersionReviewDto> Reviews);

public sealed record MovieSoundtrackAudioAssetProvenanceDto(
    Guid AssetId,
    Guid StoredFileId,
    string AssetType,
    string MimeType,
    long SizeBytes,
    Guid? SourceGenerationJobId,
    DateTime CapturedAt);

public sealed record MovieSoundtrackDuckingIntentDto(
    Guid Id,
    string TargetLane,
    decimal StartOffsetSeconds,
    decimal EndOffsetSeconds,
    decimal DuckDecibels,
    int AttackMilliseconds,
    int ReleaseMilliseconds,
    string? Rationale);

public sealed record MovieSoundtrackCueVersionReviewDto(
    Guid Id,
    string Decision,
    string? Comment,
    Guid ReviewedByUserId,
    DateTime CreatedAt);

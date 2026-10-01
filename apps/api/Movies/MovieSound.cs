using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Movies;

public static class MovieSoundKinds
{
    public const string SoundEffect = "sfx";
    public const string Ambience = "ambience";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        SoundEffect, Ambience,
    };
}

public static class MovieSoundLayers
{
    public const string Foreground = "foreground";
    public const string Background = "background";
    public const string Foley = "foley";
    public const string RoomTone = "room_tone";
    public const string Environment = "environment";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Foreground, Background, Foley, RoomTone, Environment,
    };
}

public static class MovieSoundStatuses
{
    public const string Draft = "Draft";
    public const string Queued = "Queued";
    public const string ReadyForReview = "ReadyForReview";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Queued, ReadyForReview, Approved, Rejected, Failed, Cancelled, Archived,
    };
}

public static class MovieSoundSourceKinds
{
    public const string Generated = "generated";
    public const string Imported = "imported";
    public const string Library = "library";
}

public sealed class MovieSoundLibraryReference
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid AssetId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ICollection<MovieSoundTrack> Tracks { get; set; } = [];
}

public sealed class MovieSoundTrack
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? LibraryReferenceId { get; set; }
    public string Kind { get; set; } = MovieSoundKinds.SoundEffect;
    public string Layer { get; set; } = MovieSoundLayers.Foreground;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int StartMilliseconds { get; set; }
    public int EndMilliseconds { get; set; }
    public int FadeInMilliseconds { get; set; }
    public int FadeOutMilliseconds { get; set; }
    public decimal GainDb { get; set; }
    public string Status { get; set; } = MovieSoundStatuses.Draft;
    public string SourceKind { get; set; } = MovieSoundSourceKinds.Imported;
    public string? ProvenanceJson { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MovieProject MovieProject { get; set; } = null!;
    public MovieScene? MovieScene { get; set; }
    public MovieShot? MovieShot { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? ApprovedByUser { get; set; }
    public Asset? Asset { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public MovieSoundLibraryReference? LibraryReference { get; set; }
    public ICollection<MovieSoundApproval> Approvals { get; set; } = [];
}

public sealed class MovieSoundApproval
{
    public Guid Id { get; set; }
    public Guid MovieSoundTrackId { get; set; }
    public Guid ReviewerUserId { get; set; }
    public string Decision { get; set; } = MovieSoundStatuses.Approved;
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; }

    public MovieSoundTrack Track { get; set; } = null!;
    public ApplicationUser ReviewerUser { get; set; } = null!;
}

public sealed class MovieSoundTrackRequest
{
    [Required, StringLength(32)]
    public string Kind { get; set; } = MovieSoundKinds.SoundEffect;

    [Required, StringLength(80)]
    public string Layer { get; set; } = MovieSoundLayers.Foreground;

    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required, StringLength(4_000, MinimumLength = 1)]
    public string Description { get; set; } = string.Empty;

    [Range(0, 86_400_000)]
    public int StartMilliseconds { get; set; }

    [Range(1, 86_400_000)]
    public int EndMilliseconds { get; set; } = 1_000;

    [Range(0, 86_400_000)]
    public int FadeInMilliseconds { get; set; }

    [Range(0, 86_400_000)]
    public int FadeOutMilliseconds { get; set; }

    [Range(-60, 12)]
    public decimal GainDb { get; set; }

    public Guid? AssetId { get; set; }
    public Guid? LibraryReferenceId { get; set; }
    public bool Generate { get; set; }
    [StringLength(2_000)]
    public string? AdditionalInstructions { get; set; }
}

public sealed class MovieSoundLibraryReferenceRequest
{
    [Required]
    public Guid AssetId { get; set; }

    [StringLength(160)]
    public string? Label { get; set; }
}

public sealed class MovieSoundReviewRequest
{
    public bool Approve { get; set; }

    [StringLength(2_000)]
    public string? Comment { get; set; }
}

public sealed record MovieSoundApprovalDto(Guid Id, Guid ReviewerUserId, string Decision, string? Comment, DateTime CreatedAt);

public sealed record MovieSoundTrackDto(
    Guid Id,
    Guid MovieProjectId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    Guid? AssetId,
    Guid? GenerationJobId,
    Guid? LibraryReferenceId,
    string Kind,
    string Layer,
    string Name,
    string Description,
    int StartMilliseconds,
    int EndMilliseconds,
    int FadeInMilliseconds,
    int FadeOutMilliseconds,
    decimal GainDb,
    string Status,
    string SourceKind,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ApprovedAt,
    IReadOnlyList<MovieSoundApprovalDto> Approvals);

public sealed record MovieSoundTrackListDto(Guid TargetId, string TargetType, IReadOnlyList<MovieSoundTrackDto> Tracks);

public sealed record MovieSoundLibraryReferenceDto(
    Guid Id,
    Guid MovieProjectId,
    Guid AssetId,
    string Label,
    string AssetName,
    string? MimeType,
    bool CanPreview,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MovieSoundLibraryDto(Guid MovieProjectId, IReadOnlyList<MovieSoundLibraryReferenceDto> References);

public sealed record MovieSoundGenerationInput(
    Guid MovieProjectId,
    Guid MovieSoundTrackId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    string Kind,
    string Layer,
    string Description,
    int DurationMilliseconds,
    string? AdditionalInstructions);

public sealed record MovieSoundProviderUsage(
    int LatencyMs,
    decimal? EstimatedCostUsd = null,
    decimal? ActualCostUsd = null,
    string FinishReason = "completed",
    string? CostBasis = UsageCostBasis.Unknown,
    string? SafeMetadataJson = null);

public sealed record MovieSoundProviderResult(
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string Format,
    int? DurationMilliseconds,
    MovieSoundProviderUsage Usage);

public interface IMovieSoundProvider
{
    string Key { get; }
    Task<MovieSoundProviderResult> GenerateAsync(MovieSoundGenerationInput request, CancellationToken cancellationToken = default);
}

public sealed class MovieSoundProviderUnavailableException() : Exception("No movie sound provider is configured.");
public sealed class MovieSoundOutputInvalidException() : Exception("The movie sound provider returned an invalid audio file.");
public sealed class MovieSoundRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class MovieSoundTrackProjection
{
    public static MovieSoundTrackDto ToDto(MovieSoundTrack track)
    {
        var generatedAsset = track.Asset ?? track.GenerationJob?.Assets.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
        var status = track.Status;
        if (track.GenerationJob?.Status == GenerationJobStatus.Succeeded && generatedAsset is not null && status == MovieSoundStatuses.Queued)
            status = MovieSoundStatuses.ReadyForReview;
        else if (track.GenerationJob?.Status == GenerationJobStatus.Failed && status == MovieSoundStatuses.Queued)
            status = MovieSoundStatuses.Failed;
        else if (track.GenerationJob?.Status == GenerationJobStatus.Cancelled && status == MovieSoundStatuses.Queued)
            status = MovieSoundStatuses.Cancelled;

        return new MovieSoundTrackDto(
            track.Id,
            track.MovieProjectId,
            track.MovieSceneId,
            track.MovieShotId,
            generatedAsset?.Id ?? track.AssetId,
            track.GenerationJobId,
            track.LibraryReferenceId,
            track.Kind,
            track.Layer,
            track.Name,
            track.Description,
            track.StartMilliseconds,
            track.EndMilliseconds,
            track.FadeInMilliseconds,
            track.FadeOutMilliseconds,
            track.GainDb,
            status,
            track.SourceKind,
            track.CreatedAt,
            track.UpdatedAt,
            track.ApprovedAt,
            track.Approvals.OrderByDescending(item => item.CreatedAt)
                .Select(item => new MovieSoundApprovalDto(item.Id, item.ReviewerUserId, item.Decision, item.Comment, item.CreatedAt))
                .ToArray());
    }

    public static MovieSoundLibraryReferenceDto ToLibraryDto(MovieSoundLibraryReference reference)
    {
        var mimeType = reference.Asset.MimeType;
        return new MovieSoundLibraryReferenceDto(
            reference.Id,
            reference.MovieProjectId,
            reference.AssetId,
            reference.Label,
            reference.Asset.Name,
            mimeType,
            mimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true,
            reference.CreatedAt,
            reference.UpdatedAt);
    }

    public static string Provenance(string sourceKind, Guid? assetId, Guid? generationJobId, Guid userId) =>
        JsonSerializer.Serialize(new
        {
            sourceKind,
            sourceAssetId = assetId,
            generationJobId,
            importedByUserId = userId,
            recordedAtUtc = DateTime.UtcNow,
        });
}

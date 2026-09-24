using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public static class MusicGenerationValues
{
    public const string Auto = "auto";
    public const string Instrumental = "instrumental";
    public const string Vocal = "vocal";
    public const string Either = "either";

    public static readonly IReadOnlySet<string> Genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Auto, "ambient", "cinematic", "classical", "electronic", "folk", "hip_hop", "jazz", "lofi", "pop", "rock", "world", "other",
    };

    public static readonly IReadOnlySet<string> Moods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "calm", "energetic", "uplifting", "melancholic", "inspiring", "dramatic", "playful", "romantic", "focused", "other",
    };

    public static readonly IReadOnlySet<string> VocalPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Instrumental, Vocal, Either,
    };

    public static readonly IReadOnlySet<int> Durations = new HashSet<int> { 15, 30, 60, 120, 180, 300, 600 };
}

public sealed class MusicGenerationRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    [Required, StringLength(4_000, MinimumLength = 3)]
    public string Description { get; set; } = string.Empty;

    [Required, StringLength(1_000, MinimumLength = 3)]
    public string Purpose { get; set; } = string.Empty;

    [Required, StringLength(40)]
    public string Genre { get; set; } = string.Empty;

    [Required, StringLength(80)]
    public string Mood { get; set; } = string.Empty;

    [Range(15, 600)]
    public int DurationSeconds { get; set; } = 60;

    [Required, StringLength(20)]
    public string VocalPreference { get; set; } = MusicGenerationValues.Instrumental;

    [Required, StringLength(10)]
    public string Language { get; set; } = MusicGenerationValues.Auto;

    [StringLength(160)]
    public string? Title { get; set; }

    [StringLength(2_000)]
    public string? AdditionalInstructions { get; set; }

    public Guid? ProjectId { get; set; }
}

public sealed record CreateMusicGenerationResponse(GenerationJobDto Job);

public sealed record MusicGenerationInput(
    string Description,
    string Purpose,
    string Genre,
    string Mood,
    int DurationSeconds,
    string VocalPreference,
    string Language,
    string? Title,
    string? AdditionalInstructions,
    Guid? ProjectId);

public sealed record MusicProviderUsage(
    int? InputTokens,
    int? OutputTokens,
    decimal? EstimatedCostUsd,
    decimal? ActualCostUsd,
    int LatencyMs,
    string FinishReason = "completed",
    string? CostBasis = UsageCostBasis.Estimated,
    string? SafeMetadataJson = null);

public sealed record MusicProviderResult(
    ReadOnlyMemory<byte> Content,
    string ContentType,
    string Format,
    int? DurationSeconds,
    MusicProviderUsage Usage);

public static class MusicGenerationContractMapper
{
    public static MusicGenerationInput ToInput(MusicGenerationRequest request) => new(
        request.Description.Trim(),
        request.Purpose.Trim(),
        request.Genre.Trim().ToLowerInvariant(),
        request.Mood.Trim().ToLowerInvariant(),
        request.DurationSeconds,
        request.VocalPreference.Trim().ToLowerInvariant(),
        request.Language.Trim().ToLowerInvariant(),
        Normalize(request.Title, 160),
        Normalize(request.AdditionalInstructions, 2_000),
        request.ProjectId);

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

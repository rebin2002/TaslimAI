using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public static class ImageGenerationValues
{
    public const string Auto = "auto";
    public const string Photorealistic = "photorealistic";
    public const string Product = "product";
    public const string Illustration = "illustration";
    public const string ThreeD = "3d";
    public const string Minimal = "minimal";
    public const string Poster = "poster";
    public const string SocialMedia = "social_media";
    public const string Square = "square";
    public const string Portrait = "portrait";
    public const string Landscape = "landscape";
    public const string Standard = "standard";
    public const string High = "high";

    public static readonly IReadOnlySet<string> Styles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Auto, Photorealistic, Product, Illustration, ThreeD, Minimal, Poster, SocialMedia,
    };
    public static readonly IReadOnlySet<string> AspectRatios = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Square, Portrait, Landscape,
    };
    public static readonly IReadOnlySet<string> Qualities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Standard, High,
    };
}

public sealed class ImageGenerationRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    [Required, StringLength(4_000, MinimumLength = 3)]
    public string Description { get; set; } = string.Empty;

    [Required, StringLength(40)]
    public string Style { get; set; } = ImageGenerationValues.Auto;

    [Required, StringLength(20)]
    public string AspectRatio { get; set; } = ImageGenerationValues.Square;

    [Required, StringLength(20)]
    public string Quality { get; set; } = ImageGenerationValues.Standard;

    [StringLength(120)]
    public string? Title { get; set; }

    [StringLength(120)]
    public string? Mood { get; set; }

    [StringLength(240)]
    public string? Background { get; set; }

    [StringLength(500)]
    public string? TextInImage { get; set; }

    public Guid? ProjectId { get; set; }

    // Reference-image execution is deliberately deferred in the MVP. The field
    // reserves the stable contract for a future authorized StoredFile/Asset flow.
    public Guid? ReferenceFileId { get; set; }
}

public sealed record CreateImageGenerationResponse(GenerationJobDto Job);

public sealed record ImagePromptBuildResult(
    string Prompt,
    string NormalizedStyle,
    string NormalizedAspectRatio,
    string NormalizedQuality,
    string? Mood,
    string? Background,
    string? TextInImage);

public sealed record ImageGenerationInput(
    string Description,
    string Style,
    string AspectRatio,
    string Quality,
    string? Title,
    string? Mood,
    string? Background,
    string? TextInImage,
    Guid? ProjectId,
    Guid? ReferenceFileId);

public static class ImageGenerationContractMapper
{
    public static ImageGenerationInput ToInput(ImageGenerationRequest request) => new(
        request.Description.Trim(),
        request.Style.Trim().ToLowerInvariant(),
        request.AspectRatio.Trim().ToLowerInvariant(),
        request.Quality.Trim().ToLowerInvariant(),
        Normalize(request.Title, 120),
        Normalize(request.Mood, 120),
        Normalize(request.Background, 240),
        Normalize(request.TextInImage, 500),
        request.ProjectId,
        request.ReferenceFileId);

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

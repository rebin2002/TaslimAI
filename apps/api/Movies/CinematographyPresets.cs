using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Taslim.Api.Movies;

public static class CinematographyIntent
{
    public const string Intimate = "intimate";
    public const string Natural = "natural";
    public const string Epic = "epic";
    public const string Dynamic = "dynamic";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Intimate, Natural, Epic, Dynamic,
    };
}

public static class CinematographyCapabilityClassification
{
    public const string Native = "Native";
    public const string Translated = "Translated";
    public const string SimulatedPost = "Simulated/Post";
    public const string Unsupported = "Unsupported";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Native, Translated, SimulatedPost, Unsupported,
    };
}

public sealed record CinematographyCapabilityReference(
    string Field,
    string Classification,
    string? Rationale = null)
{
    public static CinematographyCapabilityReference Translated(string field, string rationale) =>
        new(field, CinematographyCapabilityClassification.Translated, rationale);
}

public sealed record CinematographyPreset(
    string Id,
    string Name,
    string Intent,
    string Summary,
    string ShotSize,
    string FocalLength,
    string LensIntent,
    string ApertureDepthOfField,
    string CameraAngle,
    string CameraMovement,
    string FrameRateIntent,
    string Lighting,
    string PaletteLook,
    string CompositionNotes,
    IReadOnlyList<CinematographyCapabilityReference> CapabilityReferences);

public static class CinematographyPresetCatalog
{
    private static readonly IReadOnlyList<CinematographyPreset> presets = new ReadOnlyCollection<CinematographyPreset>(
    [
        new(
            "intimate-naturalism",
            "Intimate",
            CinematographyIntent.Intimate,
            "Close, human, and present without prescribing a provider implementation.",
            "Close-up to medium close-up",
            "50–85mm equivalent",
            "Portrait-forward, gentle perspective",
            "Shallow focus with a soft falloff",
            "Eye level, occasionally slightly below",
            "Subtle handheld drift or restrained push-in",
            "24 fps with natural motion",
            "Soft window light, gentle negative fill",
            "Warm skin tones, quiet neutrals, restrained contrast",
            "Keep the subject close to an edge of the frame; protect breathing room toward the eyeline.",
            [
                CinematographyCapabilityReference.Translated("focalLength", "Expressed as a visual lens intent; the eventual adapter decides whether a physical focal length is available."),
                CinematographyCapabilityReference.Translated("apertureDepthOfField", "Expressed as depth-of-field intent; focus behavior may require translation or post work."),
                CinematographyCapabilityReference.Translated("cameraMovement", "Movement is a production intent and must be classified by the eventual adapter."),
            ]),
        new(
            "natural-observation",
            "Natural",
            CinematographyIntent.Natural,
            "Observational coverage that feels believable, flexible, and lightly shaped.",
            "Medium to medium wide",
            "28–50mm equivalent",
            "Documentary-normal perspective",
            "Moderate depth of field for environmental context",
            "Eye level, observational angles",
            "Stable handheld or quiet lateral movement",
            "24 fps; preserve believable motion cadence",
            "Available light with soft practical motivation",
            "True-to-life color, balanced highlights, gentle grain",
            "Favor motivated eyelines and readable spatial relationships over ornamental symmetry.",
            [
                CinematographyCapabilityReference.Translated("focalLength", "Equivalent focal range communicates perspective intent without assuming a native camera control."),
                CinematographyCapabilityReference.Translated("lighting", "Available-light language may be translated into a visual treatment or simulated in post."),
            ]),
        new(
            "epic-scale",
            "Epic",
            CinematographyIntent.Epic,
            "Scale, atmosphere, and graphic composition for a larger-than-life beat.",
            "Wide to extreme wide with selective hero close-ups",
            "18–35mm equivalent",
            "Expansive perspective with controlled distortion",
            "Deep focus where geography matters; selective isolation for hero moments",
            "Low angle, high angle, and deliberate horizon control",
            "Crane-like reveal, sweeping track, or measured orbit",
            "24 fps; optional high-frame-rate capture intent for emphasis",
            "Directional key with atmospheric separation and motivated backlight",
            "Bold tonal separation, cinematic shadows, controlled accent color",
            "Use leading lines and layered foreground/midground/background depth to sell scale.",
            [
                CinematographyCapabilityReference.Translated("focalLength", "Wide perspective is a compositional intent; an adapter must classify how it can be realized."),
                CinematographyCapabilityReference.Translated("cameraMovement", "Large-format movement may be translated, simulated, or unsupported depending on the adapter."),
                CinematographyCapabilityReference.Translated("frameRateIntent", "Frame rate is retained as intent and is not claimed as a provider-native control."),
            ]),
        new(
            "dynamic-energy",
            "Dynamic",
            CinematographyIntent.Dynamic,
            "Kinetic, directional coverage with clear energy and controlled visual rhythm.",
            "Medium, wide, and impact close-ups",
            "24–50mm equivalent",
            "Responsive perspective with readable spatial change",
            "Selective focus pulls; enough depth to keep action legible",
            "Canted or low angles used sparingly for pressure",
            "Dolly, tracking, whip-pan, or motivated handheld acceleration",
            "24 fps with high-frame-rate intent for selected beats",
            "Harder motivated sources, practical contrast, crisp edge light",
            "Higher contrast, saturated accents, purposeful color shifts",
            "Compose for directional flow; leave exit space and avoid accidental tangents during motion.",
            [
                CinematographyCapabilityReference.Translated("cameraMovement", "Kinetic movement needs adapter-specific classification and may be simulated or handled in post."),
                CinematographyCapabilityReference.Translated("apertureDepthOfField", "Focus-pull intent is not a claim that a provider exposes aperture control."),
                CinematographyCapabilityReference.Translated("frameRateIntent", "High-frame-rate language remains a request-level intent until capability resolution."),
            ]),
    ]);

    public static IReadOnlyList<CinematographyPreset> All => presets;

    public static CinematographyPreset? Find(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : presets.FirstOrDefault(item => string.Equals(item.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    public static CinematographyPreset? FindByIntent(string? intent) => string.IsNullOrWhiteSpace(intent)
        ? null
        : presets.FirstOrDefault(item => string.Equals(item.Intent, intent.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record CinematographyIntentSelection(
    string? Intent,
    string? PresetId,
    string? Notes,
    IReadOnlyList<CinematographyCapabilityReference>? CapabilityReferences = null,
    string? ShotSize = null,
    string? FocalLength = null,
    string? LensIntent = null,
    string? ApertureDepthOfField = null,
    string? CameraAngle = null,
    string? CameraMovement = null,
    string? FrameRateIntent = null,
    string? Lighting = null,
    string? PaletteLook = null,
    string? CompositionNotes = null);

public static class CinematographyIntentValidator
{
    private const int MaxNotesLength = 4_000;
    private const int MaxFieldLength = 600;

    public static string? Validate(CinematographyIntentSelection? selection)
    {
        if (selection is null) return null;
        if (!string.IsNullOrWhiteSpace(selection.Intent) && !CinematographyIntent.Supported.Contains(selection.Intent.Trim()))
            return "Choose Intimate, Natural, Epic, or Dynamic for the cinematography intent.";
        if (!string.IsNullOrWhiteSpace(selection.PresetId) && CinematographyPresetCatalog.Find(selection.PresetId) is null)
            return "Choose a supported cinematography preset.";
        if (!string.IsNullOrWhiteSpace(selection.Notes) && selection.Notes.Trim().Length > MaxNotesLength)
            return "Cinematography notes must be 4,000 characters or fewer.";
        if (selection.CapabilityReferences is { Count: > 32 })
            return "Cinematography capability references are limited to 32 items.";
        if (selection.CapabilityReferences is not null && selection.CapabilityReferences.Any(reference =>
                string.IsNullOrWhiteSpace(reference.Field) || reference.Field.Trim().Length > 80 ||
                !CinematographyCapabilityClassification.Supported.Contains(reference.Classification) ||
                reference.Rationale?.Trim().Length > MaxFieldLength))
            return "Cinematography capability references are invalid.";
        if (new[]
            {
                selection.ShotSize, selection.FocalLength, selection.LensIntent, selection.ApertureDepthOfField,
                selection.CameraAngle, selection.CameraMovement, selection.FrameRateIntent, selection.Lighting,
                selection.PaletteLook, selection.CompositionNotes,
            }.Any(value => value?.Trim().Length > MaxFieldLength))
            return "Cinematography control notes must be 600 characters or fewer.";
        return null;
    }

    public static CinematographyIntentSelection? Normalize(CinematographyIntentSelection? selection)
    {
        if (selection is null) return null;
        var preset = CinematographyPresetCatalog.Find(selection.PresetId) ?? CinematographyPresetCatalog.FindByIntent(selection.Intent);
        var intent = string.IsNullOrWhiteSpace(selection.Intent) ? preset?.Intent : selection.Intent.Trim().ToLowerInvariant();
        var presetId = string.IsNullOrWhiteSpace(selection.PresetId) ? preset?.Id : preset?.Id ?? selection.PresetId.Trim();
        var references = selection.CapabilityReferences is null
            ? preset?.CapabilityReferences
            : selection.CapabilityReferences.Select(reference => new CinematographyCapabilityReference(reference.Field.Trim(), reference.Classification, reference.Rationale?.Trim())).ToArray();
        return new CinematographyIntentSelection(
            intent,
            presetId,
            Clean(selection.Notes, MaxNotesLength),
            references,
            Clean(selection.ShotSize ?? preset?.ShotSize, MaxFieldLength),
            Clean(selection.FocalLength ?? preset?.FocalLength, MaxFieldLength),
            Clean(selection.LensIntent ?? preset?.LensIntent, MaxFieldLength),
            Clean(selection.ApertureDepthOfField ?? preset?.ApertureDepthOfField, MaxFieldLength),
            Clean(selection.CameraAngle ?? preset?.CameraAngle, MaxFieldLength),
            Clean(selection.CameraMovement ?? preset?.CameraMovement, MaxFieldLength),
            Clean(selection.FrameRateIntent ?? preset?.FrameRateIntent, MaxFieldLength),
            Clean(selection.Lighting ?? preset?.Lighting, MaxFieldLength),
            Clean(selection.PaletteLook ?? preset?.PaletteLook, MaxFieldLength),
            Clean(selection.CompositionNotes ?? preset?.CompositionNotes, MaxFieldLength));
    }

    public static string ToJson(CinematographyIntentSelection? selection) =>
        JsonSerializer.Serialize(Normalize(selection), new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

    public static CinematographyIntentSelection? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CinematographyIntentSelection>(json); }
        catch (JsonException) { return null; }
    }

    private static string? Clean(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];
}

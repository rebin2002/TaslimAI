namespace Taslim.Api.Movies;

public static class MovieWorkspaceModules
{
    public const string Overview = "overview";
    public const string Story = "story";
    public const string Cast = "cast";
    public const string World = "world";
    public const string Scenes = "scenes";
    public const string Storyboard = "storyboard";
    public const string Production = "production";
    public const string Edit = "edit";
    public const string Audio = "audio";
    public const string QualityControl = "qc";
    public const string Exports = "exports";
    public const string Team = "team";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Overview, Story, Cast, World, Scenes, Storyboard, Production, Edit, Audio, QualityControl, Exports, Team,
    };

    public static string Normalize(string? module) =>
        !string.IsNullOrWhiteSpace(module) && Supported.Contains(module.Trim())
            ? module.Trim().ToLowerInvariant()
            : Overview;
}

public sealed record MovieWorkspaceGuideDto(
    Guid Id,
    string VisualLanguage,
    string CameraLanguage,
    string ColorAndLighting,
    string SoundAndNarration,
    string ContinuityRules,
    DateTime UpdatedAt);

public sealed record MovieWorkspaceClipDto(
    Guid Id,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    Guid? AssetId,
    string Status,
    int? DurationSeconds);

public sealed record MovieWorkspaceSceneDto(
    Guid Id,
    int Sequence,
    string Title,
    string Summary,
    int? DurationSeconds,
    string? ContinuityNotes,
    string? Narration,
    string? Dialogue,
    int ShotCount,
    IReadOnlyList<MovieWorkspaceClipDto> Clips);

public sealed record MovieWorkspaceCharacterDto(
    Guid Id,
    string Name,
    string? Role,
    string Description,
    string? Appearance,
    string? VoiceAndPerformance,
    string? ContinuityNotes,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record MovieWorkspaceLocationDto(
    Guid Id,
    string Name,
    string Description,
    string? VisualContinuityNotes);

public sealed record MovieWorkspaceWorldDto(IReadOnlyList<MovieWorkspaceLocationDto> Locations);

public sealed record MovieWorkspaceAssemblyDto(
    Guid Id,
    Guid? AssetId,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record MovieWorkspaceProjectDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string Mode,
    string Status,
    string Title,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    string? AdditionalInstructions,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    MovieWorkspaceGuideDto Guide,
    IReadOnlyList<MovieWorkspaceSceneDto> Scenes,
    IReadOnlyList<MovieWorkspaceCharacterDto> Characters,
    IReadOnlyList<MovieWorkspaceLocationDto> Locations,
    IReadOnlyList<MovieWorkspaceClipDto> Clips,
    IReadOnlyList<MovieWorkspaceAssemblyDto> Assemblies,
    MovieWorkspaceWorldDto? World);

public sealed record MovieWorkspaceResponse(string Module, MovieWorkspaceProjectDto Project);

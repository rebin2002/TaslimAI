using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Ai;

namespace Taslim.Api.Movies;

public static class MovieProjectModes
{
    public const string Quick = "Quick";
    public const string Full = "Full";
}

public static class MovieProjectStatuses
{
    public const string Draft = "Draft";
    public const string Active = "Active";
    public const string Archived = "Archived";
}

public static class MovieClipStatuses
{
    public const string Planned = "Planned";
    public const string Queued = "Queued";
    public const string Generating = "Generating";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

public static class MovieAssemblyStatuses
{
    public const string Planned = "Planned";
    public const string Queued = "Queued";
    public const string Assembling = "Assembling";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

public sealed class MovieProject
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Mode { get; set; } = MovieProjectModes.Full;
    public string Status { get; set; } = MovieProjectStatuses.Draft;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public string AspectRatio { get; set; } = "16:9";
    public string Style { get; set; } = "cinematic";
    public string Language { get; set; } = LanguageCodes.English;
    public string? AdditionalInstructions { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public MovieContinuityGuide Guide { get; set; } = null!;
    public ICollection<MovieScene> Scenes { get; set; } = [];
    public ICollection<MovieCharacter> Characters { get; set; } = [];
    public ICollection<MovieLocation> Locations { get; set; } = [];
    public ICollection<MovieClip> Clips { get; set; } = [];
    public ICollection<MovieAssembly> Assemblies { get; set; } = [];
}

public sealed class MovieContinuityGuide
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string VisualLanguage { get; set; } = string.Empty;
    public string CameraLanguage { get; set; } = string.Empty;
    public string ColorAndLighting { get; set; } = string.Empty;
    public string SoundAndNarration { get; set; } = string.Empty;
    public string ContinuityRules { get; set; } = string.Empty;
    public string? ReferenceAssetIdsJson { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
}

public sealed class MovieScene
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public string? ContinuityNotes { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ICollection<MovieShot> Shots { get; set; } = [];
    public ICollection<MovieClip> Clips { get; set; } = [];
}

public sealed class MovieCharacter
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Appearance { get; set; }
    public string? VoiceAndPerformance { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed class MovieLocation
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? VisualContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed class MovieShot
{
    public Guid Id { get; set; }
    public Guid MovieSceneId { get; set; }
    public int Sequence { get; set; }
    public string ProductionStage { get; set; } = MovieProductionStages.ShotPlan;
    public string Description { get; set; } = string.Empty;
    public string? CameraAndFraming { get; set; }
    public string? CameraMotion { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public string? VisualContinuityNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieScene Scene { get; set; } = null!;
    public ICollection<MovieClip> Clips { get; set; } = [];
    public ICollection<MovieProductionVersion> ProductionVersions { get; set; } = [];
    public ICollection<MovieProductionStageTransition> ProductionTransitions { get; set; } = [];
}

public sealed class MovieClip
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? StoredFileId { get; set; }
    public string Status { get; set; } = MovieClipStatuses.Planned;
    public string? ProviderKey { get; set; }
    public string? ProviderClipId { get; set; }
    public int? DurationSeconds { get; set; }
    public string? MetadataJson { get; set; }
    public string? ContinuitySnapshotJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieScene? MovieScene { get; set; }
    public MovieShot? MovieShot { get; set; }
    public GenerationJob? GenerationJob { get; set; }
    public Asset? Asset { get; set; }
    public StoredFile? StoredFile { get; set; }
}

public sealed class MovieAssembly
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public Guid? AssetId { get; set; }
    public string Status { get; set; } = MovieAssemblyStatuses.Planned;
    public string OutputFormat { get; set; } = "mp4";
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public GenerationJob? GenerationJob { get; set; }
    public Asset? Asset { get; set; }
}

public enum MovieVideoProviderJobStatus
{
    Submitted,
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record MovieVideoGenerationRequest(
    string Operation,
    Guid GenerationJobId,
    Guid MovieProjectId,
    Guid MovieClipId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    string? AdditionalInstructions,
    string? ContinuityGuideJson,
    string? SceneJson,
    string? ShotJson,
    string? SourceImageUri = null,
    string? ContinuationProviderJobId = null);

public sealed record MovieVideoSubmission(string ProviderJobId);
public sealed record MovieVideoProviderStatus(
    MovieVideoProviderJobStatus Status,
    int ProgressPercent,
    string? ContentType = null,
    string? FileName = null,
    long? SizeBytes = null,
    int? DurationSeconds = null,
    string? MetadataJson = null,
    decimal? ActualCostUsd = null,
    string? Currency = null,
    string? CostBasis = null,
    string? SafeMetadataJson = null,
    decimal? EstimatedCostUsd = null);

public sealed record MovieVideoProviderOutput(
    string ContentType,
    string FileName,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenReadAsync,
    int? DurationSeconds,
    string? MetadataJson,
    decimal? EstimatedCostUsd,
    decimal? ActualCostUsd,
    string? Currency,
    string? CostBasis,
    string? SafeMetadataJson,
    string? ProviderModelKey = null);

public interface IMovieVideoProvider
{
    string Key { get; }
    bool IsAvailable { get; }
    IReadOnlyCollection<string> SupportedOperations { get; }
    Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken);
    Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken);
    Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken);
    Task CancelAsync(string providerJobId, CancellationToken cancellationToken);
}

public sealed class MovieProviderUnavailableException : Exception
{
    public MovieProviderUnavailableException() : base("No movie video provider is configured.") { }
}

public sealed class MovieVideoProviderTimeoutException() : Exception("The movie provider did not finish within the configured polling limit.");
public sealed class MovieVideoProviderCancelledException() : OperationCanceledException("The movie provider task was cancelled.");
public sealed class MovieVideoStaleWorkerException() : OperationCanceledException("The movie execution ownership changed.");

public sealed class UnavailableMovieVideoProvider : IMovieVideoProvider
{
    public string Key => "unconfigured";
    public bool IsAvailable => false;
    public IReadOnlyCollection<string> SupportedOperations => [];
    public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken) => Task.FromException<MovieVideoSubmission>(new MovieProviderUnavailableException());
    public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) => Task.FromException<MovieVideoProviderStatus>(new MovieProviderUnavailableException());
    public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) => Task.FromException<MovieVideoProviderOutput>(new MovieProviderUnavailableException());
    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class MovieVideoProviderException(string code, bool transient, string message = "Movie provider request failed.") : Exception(message)
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = transient;
}

public sealed class MovieVideoProviderOutputException : Exception
{
    public MovieVideoProviderOutputException() : base("The movie provider returned an invalid output.") { }
}

public sealed record MovieGenerationInput(
    string Operation,
    Guid MovieProjectId,
    Guid MovieClipId,
    Guid? MovieSceneId,
    Guid? MovieShotId,
    string Description,
    int DurationSeconds,
    string AspectRatio,
    string Style,
    string Language,
    string? AdditionalInstructions,
    string? ContinuityGuideJson,
    string? SceneJson,
    string? ShotJson,
    string? SourceImageUri = null,
    string? ContinuationProviderJobId = null);

public sealed record MovieProviderReadinessDto(bool Ready, IReadOnlyList<string> SupportedOperations);

public sealed record MovieGuideDto(Guid Id, string VisualLanguage, string CameraLanguage, string ColorAndLighting, string SoundAndNarration, string ContinuityRules, DateTime UpdatedAt);
public sealed record MovieSceneDto(Guid Id, int Sequence, string Title, string Summary, int? DurationSeconds, string? ContinuityNotes, string? Narration, string? Dialogue, IReadOnlyList<MovieShotDto> Shots, IReadOnlyList<MovieClipDto> Clips);
public sealed record MovieShotDto(Guid Id, int Sequence, string Description, string? CameraAndFraming, string? CameraMotion, int? DurationSeconds, string? Narration, string? Dialogue, string? VisualContinuityNotes, string ProductionStage, IReadOnlyList<MovieClipDto> Clips, IReadOnlyList<MovieProductionVersionDto> ProductionVersions);
public sealed record MovieCharacterDto(Guid Id, string Name, string Description, string? Appearance, string? VoiceAndPerformance, string? ContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieLocationDto(Guid Id, string Name, string Description, string? VisualContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieClipDto(Guid Id, Guid? MovieSceneId, Guid? MovieShotId, Guid? GenerationJobId, Guid? AssetId, string Status, int? DurationSeconds, string? MetadataJson, string? ContinuitySnapshotJson);
public sealed record MovieAssemblyDto(Guid Id, Guid? GenerationJobId, Guid? AssetId, string Status, string OutputFormat, string? MetadataJson, DateTime CreatedAt, DateTime? CompletedAt);
public sealed record MovieStudioProjectDto(Guid Id, Guid WorkspaceId, Guid? ProjectId, string Mode, string Status, string Title, string Description, int DurationSeconds, string AspectRatio, string Style, string Language, string? AdditionalInstructions, DateTime CreatedAt, DateTime UpdatedAt, MovieGuideDto Guide, IReadOnlyList<MovieSceneDto> Scenes, IReadOnlyList<MovieCharacterDto> Characters, IReadOnlyList<MovieLocationDto> Locations, IReadOnlyList<MovieClipDto> Clips, IReadOnlyList<MovieAssemblyDto> Assemblies);
public sealed record MovieStudioProjectResponse(MovieStudioProjectDto Project, GenerationJobDto? Job);
public sealed record MovieStudioProviderResponse(MovieProviderReadinessDto Provider);
public sealed record MovieStudioGenerationResponse(MovieStudioProjectDto Project, GenerationJobDto Job, Guid ClipId);

public sealed class MovieStudioCreateRequest
{
    public Guid WorkspaceId { get; set; }
    public Guid? ProjectId { get; set; }
    public string Mode { get; set; } = MovieProjectModes.Full;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public string AspectRatio { get; set; } = "16:9";
    public string Style { get; set; } = "cinematic";
    public string Language { get; set; } = LanguageCodes.English;
    public string? AdditionalInstructions { get; set; }
    public string? VisualLanguage { get; set; }
    public string? CameraLanguage { get; set; }
    public string? ColorAndLighting { get; set; }
    public string? SoundAndNarration { get; set; }
    public string? ContinuityRules { get; set; }
}

public sealed record MovieStudioSceneRequest(string Title, string Summary, int? DurationSeconds, string? ContinuityNotes, string? Narration, string? Dialogue);
public sealed record MovieStudioCharacterRequest(string Name, string Description, string? Appearance, string? VoiceAndPerformance, string? ContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieStudioLocationRequest(string Name, string Description, string? VisualContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieStudioShotRequest(string Description, string? CameraAndFraming, string? CameraMotion, int? DurationSeconds, string? Narration, string? Dialogue, string? VisualContinuityNotes);
public sealed record MovieStudioGuideRequest(string? VisualLanguage, string? CameraLanguage, string? ColorAndLighting, string? SoundAndNarration, string? ContinuityRules);
public sealed record MovieStudioGenerationRequest(string? Title = null);

public static class MovieStudioValidation
{
    public static string? Validate(MovieStudioCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 160) return "Add a movie title.";
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 8000) return "Describe the movie in 1–8,000 characters.";
        if (request.DurationSeconds is < 1 or > 3600) return "Movie duration must be between 1 second and 60 minutes.";
        if (request.AspectRatio is not ("16:9" or "9:16" or "1:1" or "4:5" or "4:3")) return "Choose a supported aspect ratio.";
        if (!LanguageCodes.Supported.Contains(request.Language)) return "Choose English, Arabic, or Kurdish.";
        if (request.Mode is not (MovieProjectModes.Quick or MovieProjectModes.Full)) return "Choose Quick Movie or Full Movie Project.";
        return null;
    }
}

public static class MovieStudioOperations
{
    public const string QuickMovie = "quick_movie";
    public const string SceneClip = "scene_clip";
    public const string Assembly = "assembly";
}

public static class MovieStudioHelpers
{
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static string CleanRequired(string value) => value.Trim();
}

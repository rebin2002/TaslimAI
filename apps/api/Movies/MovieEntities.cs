using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Ai;
using Taslim.Api.Usage;

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

public static class MovieProductionStatuses
{
    public const string Draft = "Draft";
    public const string InDevelopment = "InDevelopment";
    public const string PreProduction = "PreProduction";
    public const string Production = "Production";
    public const string PostProduction = "PostProduction";
    public const string Locked = "Locked";
    public const string Completed = "Completed";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft, InDevelopment, PreProduction, Production, PostProduction, Locked, Completed, Archived,
    };
}

public static class MovieQualityLevels
{
    public const string Fast = "Fast";
    public const string Standard = "Standard";
    public const string Cinematic = "Cinematic";
    public const string Studio = "Studio";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Fast, Standard, Cinematic, Studio,
    };
}

public static class MovieHierarchyStatuses
{
    public const string Planned = "Planned";
    public const string InProgress = "InProgress";
    public const string Approved = "Approved";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Planned, InProgress, Approved, Archived,
    };
}

public static class MovieShotStatuses
{
    public const string Planned = "Planned";
    public const string InProgress = "InProgress";
    public const string ReadyForReview = "ReadyForReview";
    public const string Approved = "Approved";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Planned, InProgress, ReadyForReview, Approved, Archived,
    };
}

public static class MovieTakeStatuses
{
    public const string Draft = "Draft";
    public const string Generating = "Generating";
    public const string Ready = "Ready";
    public const string Rejected = "Rejected";
    public const string Approved = "Approved";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Generating, Ready, Rejected, Approved, Archived,
    };
}

public static class MovieApprovalDecisions
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
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

public static class MovieGuideSectionTypes
{
    public const string StoryBible = "story_bible";
    public const string CharacterBibleReferences = "character_bible_references";
    public const string WorldBibleReferences = "world_bible_references";
    public const string VisualBible = "visual_bible";
    public const string CinematographyBible = "cinematography_bible";
    public const string AudioBible = "audio_bible";
    public const string ContinuityBible = "continuity_bible";
    public static readonly IReadOnlyList<string> All =
    [
        StoryBible, CharacterBibleReferences, WorldBibleReferences, VisualBible,
        CinematographyBible, AudioBible, ContinuityBible,
    ];
}

public static class MovieGuideRevisionStatuses
{
    public const string Draft = "Draft";
    public const string Locked = "Locked";
}

public static class MovieWorldEntityTypes
{
    public const string Location = "location";
    public const string Set = "set";
    public const string Prop = "prop";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Location, Set, Prop };
}

public static class MovieWorldReferenceKinds
{
    public const string Moodboard = "moodboard";
    public const string Location = "location";
    public const string Set = "set";
    public const string Prop = "prop";
    public const string Continuity = "continuity";
    public const string Other = "other";
}

public static class MovieWorldScopes
{
    public const string Project = "project";
    public const string Scene = "scene";
    public const string Shot = "shot";
    public const string Location = "location";
    public const string Set = "set";
    public const string Prop = "prop";
}

public static class MovieContinuityLockStrengths
{
    public const string Soft = "soft";
    public const string Hard = "hard";
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
    public string ProductionStatus { get; set; } = MovieProductionStatuses.Draft;
    public string QualityLevel { get; set; } = MovieQualityLevels.Standard;
    public bool AutoDirectorEnabled { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public Guid? StatusChangedByUserId { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Workspace Workspace { get; set; } = null!;
    public Project? Project { get; set; }
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public MovieContinuityGuide Guide { get; set; } = null!;
    public ICollection<MovieScene> Scenes { get; set; } = [];
    public ICollection<MovieAct> Acts { get; set; } = [];
    public ICollection<MovieCharacter> Characters { get; set; } = [];
    public ICollection<MovieLocation> Locations { get; set; } = [];
    public ICollection<MovieSet> Sets { get; set; } = [];
    public ICollection<MovieProp> Props { get; set; } = [];
    public ICollection<MovieWorldReference> WorldReferences { get; set; } = [];
    public ICollection<MovieContinuityFact> ContinuityFacts { get; set; } = [];
    public ICollection<MovieContinuityLock> ContinuityLocks { get; set; } = [];
    public ICollection<MovieWorldUsage> WorldUsages { get; set; } = [];
    public ICollection<MovieClip> Clips { get; set; } = [];
    public ICollection<MovieAssembly> Assemblies { get; set; } = [];
    public ICollection<MovieTeamMember> TeamMembers { get; set; } = [];
    public ICollection<MovieComment> Comments { get; set; } = [];
    public ICollection<MovieReview> Reviews { get; set; } = [];
    public ICollection<MovieProductionAssignment> Assignments { get; set; } = [];
    public ICollection<MovieProductionCredit> Credits { get; set; } = [];
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
    public string? CinematographyIntent { get; set; }
    public string? CinematographyBibleReferencesJson { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public int CurrentRevisionNumber { get; set; } = 1;
    public int? LockedRevisionNumber { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public ICollection<MovieGuideRevision> Revisions { get; set; } = [];
}

public sealed class MovieGuideRevision
{
    public Guid Id { get; set; }
    public Guid MovieContinuityGuideId { get; set; }
    public int RevisionNumber { get; set; }
    public string Status { get; set; } = MovieGuideRevisionStatuses.Draft;
    public string StoryBibleJson { get; set; } = "{}";
    public string CharacterBibleReferencesJson { get; set; } = "[]";
    public string WorldBibleReferencesJson { get; set; } = "[]";
    public string VisualBibleJson { get; set; } = "{}";
    public string CinematographyBibleJson { get; set; } = "{}";
    public string AudioBibleJson { get; set; } = "{}";
    public string ContinuityBibleJson { get; set; } = "{}";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedByUserId { get; set; }
    public MovieContinuityGuide Guide { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public ApplicationUser? LockedByUser { get; set; }
}

public sealed class MovieScene
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieSequenceId { get; set; }
    public int Sequence { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public string? ContinuityNotes { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public string Status { get; set; } = MovieHierarchyStatuses.Planned;
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieSequence? MovieSequence { get; set; }
    public ICollection<MovieShot> Shots { get; set; } = [];
    public ICollection<MovieClip> Clips { get; set; } = [];
}

public sealed class MovieCharacter
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Appearance { get; set; }
    public string? PhysicalDescription { get; set; }
    public string? Wardrobe { get; set; }
    public string? VoiceReference { get; set; }
    public string? PersonalityAndStoryNotes { get; set; }
    public string? VoiceAndPerformance { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
    public ICollection<MovieCharacterState> States { get; set; } = [];
    public ICollection<MovieCharacterReferenceAsset> ReferenceAssets { get; set; } = [];
    public ICollection<MovieCharacterRelationship> Relationships { get; set; } = [];
    public ICollection<MovieCharacterContinuityLock> ContinuityLocks { get; set; } = [];
}

public sealed class MovieCharacterState
{
    public Guid Id { get; set; }
    public Guid MovieCharacterId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Wardrobe { get; set; }
    public string? AgeOrTimeState { get; set; }
    public string? Appearance { get; set; }
    public string? InjuryOrCondition { get; set; }
    public string? LocationOrStoryState { get; set; }
    public string? ContinuityNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieCharacter Character { get; set; } = null!;
    public ICollection<MovieCharacterContinuityLock> ContinuityLocks { get; set; } = [];
}

public sealed class MovieCharacterReferenceAsset
{
    public Guid MovieCharacterId { get; set; }
    public Guid AssetId { get; set; }
    public string? Label { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieCharacter Character { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}

public sealed class MovieCharacterRelationship
{
    public Guid Id { get; set; }
    public Guid MovieCharacterId { get; set; }
    public Guid RelatedCharacterId { get; set; }
    public string RelationshipType { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieCharacter Character { get; set; } = null!;
    public MovieCharacter RelatedCharacter { get; set; } = null!;
}

public sealed class MovieCharacterContinuityLock
{
    public Guid Id { get; set; }
    public Guid MovieCharacterId { get; set; }
    public Guid? MovieCharacterStateId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string LockedValue { get; set; } = string.Empty;
    public Guid ApprovedByUserId { get; set; }
    public DateTime ApprovedAt { get; set; }
    public MovieCharacter Character { get; set; } = null!;
    public MovieCharacterState? CharacterState { get; set; }
    public ApplicationUser ApprovedByUser { get; set; } = null!;
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

public sealed class MovieSet
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieLocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EnvironmentType { get; set; } = "practical";
    public string? VisualDescription { get; set; }
    public string? TimeOfDay { get; set; }
    public string? Weather { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieLocation? MovieLocation { get; set; }
    public Asset? ReferenceAsset { get; set; }
    public ICollection<MovieSetVariation> Variations { get; set; } = [];
}

public sealed class MovieSetVariation
{
    public Guid Id { get; set; }
    public Guid MovieSetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? VisualDescription { get; set; }
    public string? TimeOfDay { get; set; }
    public string? Weather { get; set; }
    public string? Lighting { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieSet MovieSet { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed class MovieProp
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public Asset? ReferenceAsset { get; set; }
}

public sealed class MovieWorldReference
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = MovieWorldReferenceKinds.Other;
    public string? Description { get; set; }
    public string? TagsJson { get; set; }
    public Guid? AssetId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public Asset? Asset { get; set; }
    public ICollection<MovieWorldReferenceLink> Links { get; set; } = [];
}

public sealed class MovieWorldReferenceLink
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MovieWorldReferenceId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string? Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieWorldReference Reference { get; set; } = null!;
}

public sealed class MovieWorldUsage
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string? Role { get; set; }
    public DateTime CreatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieScene MovieScene { get; set; } = null!;
    public MovieShot? MovieShot { get; set; }
}

public sealed class MovieContinuityFact
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string ScopeType { get; set; } = MovieWorldScopes.Project;
    public Guid? ScopeId { get; set; }
    public string FactKey { get; set; } = string.Empty;
    public string FactValue { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
}

public sealed class MovieContinuityLock
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public string EntityType { get; set; } = MovieWorldScopes.Project;
    public Guid? EntityId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string LockedValue { get; set; } = string.Empty;
    public string Strength { get; set; } = MovieContinuityLockStrengths.Hard;
    public string? Reason { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
}

public sealed class MovieShot
{
    public Guid Id { get; set; }
    public Guid MovieSceneId { get; set; }
    public Guid? SelectedTakeId { get; set; }
    public Guid? FinalTakeId { get; set; }
    public int Sequence { get; set; }
    public string ProductionStage { get; set; } = MovieProductionStages.ShotPlan;
    public string Description { get; set; } = string.Empty;
    public string? CameraAndFraming { get; set; }
    public string? CameraMotion { get; set; }
    public string? CinematographyJson { get; set; }
    public int? DurationSeconds { get; set; }
    public string? Narration { get; set; }
    public string? Dialogue { get; set; }
    public string? VisualContinuityNotes { get; set; }
    public string Status { get; set; } = MovieShotStatuses.Planned;
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieScene Scene { get; set; } = null!;
    public MovieTake? SelectedTake { get; set; }
    public MovieTake? FinalTake { get; set; }
    public ICollection<MovieTake> Takes { get; set; } = [];
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
    public Guid? ContinuitySnapshotId { get; set; }
    public int? ContinuitySnapshotVersion { get; set; }
    public string? ContinuitySnapshotHash { get; set; }
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
    string? ContinuationProviderJobId = null,
    string? WorldContextJson = null);

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
    string? ContinuationProviderJobId = null,
    string? WorldContextJson = null);

public sealed record MovieProviderReadinessDto(bool Ready, IReadOnlyList<string> SupportedOperations);

public sealed record MovieCinematographyBibleDto(string? Intent, string? PresetId, string? Notes, IReadOnlyList<CinematographyCapabilityReference> CapabilityReferences);
public sealed record MovieGuideDto(Guid Id, string VisualLanguage, string CameraLanguage, string ColorAndLighting, string SoundAndNarration, string ContinuityRules, DateTime UpdatedAt, int CurrentRevisionNumber = 1, int? LockedRevisionNumber = null, DateTime? LockedAt = null, MovieCinematographyBibleDto? CinematographyBible = null);
public sealed record MovieGuideSectionDto(string Type, string ContentJson);
public sealed record MovieGuideRevisionDto(Guid Id, int RevisionNumber, string Status, IReadOnlyList<MovieGuideSectionDto> Sections, Guid CreatedByUserId, DateTime CreatedAt, DateTime? LockedAt, Guid? LockedByUserId);
public sealed record MovieDirectorContextDto(Guid MovieProjectId, Guid MovieGuideId, bool IsAuthoritative, int RevisionNumber, DateTime? LockedAt, IReadOnlyList<MovieGuideSectionDto> Sections);
public sealed record MovieSceneDto(Guid Id, int Sequence, string Title, string Summary, int? DurationSeconds, string? ContinuityNotes, string? Narration, string? Dialogue, IReadOnlyList<MovieShotDto> Shots, IReadOnlyList<MovieClipDto> Clips);
public sealed record MovieShotDto(Guid Id, int Sequence, string Description, string? CameraAndFraming, string? CameraMotion, string? CinematographyJson, int? DurationSeconds, string? Narration, string? Dialogue, string? VisualContinuityNotes, string ProductionStage, IReadOnlyList<MovieClipDto> Clips, IReadOnlyList<MovieProductionVersionDto> ProductionVersions);
public sealed record MovieCharacterStateDto(Guid Id, string Key, string? Label, string? Wardrobe, string? AgeOrTimeState, string? Appearance, string? InjuryOrCondition, string? LocationOrStoryState, string? ContinuityNotes, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record MovieCharacterRelationshipDto(Guid Id, Guid RelatedCharacterId, string RelatedCharacterName, string RelationshipType, string? Notes);
public sealed record MovieCharacterContinuityLockDto(Guid Id, string FieldKey, string LockedValue, Guid? CharacterStateId, DateTime ApprovedAt);
public sealed record MovieCharacterDto(Guid Id, string Name, string? Role, string Description, string? Appearance, string? PhysicalDescription, string? Wardrobe, string? VoiceReference, string? PersonalityAndStoryNotes, string? VoiceAndPerformance, string? ContinuityNotes, Guid? ReferenceAssetId, IReadOnlyList<Guid> ReferenceAssetIds, IReadOnlyList<MovieCharacterStateDto> States, IReadOnlyList<MovieCharacterRelationshipDto> Relationships, IReadOnlyList<MovieCharacterContinuityLockDto> ContinuityLocks);
public sealed record MovieLocationDto(Guid Id, string Name, string Description, string? VisualContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieSetVariationDto(Guid Id, Guid MovieSetId, string Name, string? VisualDescription, string? TimeOfDay, string? Weather, string? Lighting, string? ContinuityNotes, Guid? ReferenceAssetId, bool IsDefault);
public sealed record MovieSetDto(Guid Id, Guid? MovieLocationId, string Name, string Description, string EnvironmentType, string? VisualDescription, string? TimeOfDay, string? Weather, string? ContinuityNotes, Guid? ReferenceAssetId, IReadOnlyList<MovieSetVariationDto> Variations);
public sealed record MoviePropDto(Guid Id, string Name, string Description, string? Category, string? ContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieWorldReferenceDto(Guid Id, string Name, string Kind, string? Description, string? TagsJson, Guid? AssetId);
public sealed record MovieWorldUsageDto(Guid Id, Guid MovieSceneId, Guid? MovieShotId, string EntityType, Guid EntityId, string? Role);
public sealed record MovieContinuityFactDto(Guid Id, string ScopeType, Guid? ScopeId, string FactKey, string FactValue, string? Notes, DateTime UpdatedAt);
public sealed record MovieContinuityLockDto(Guid Id, string EntityType, Guid? EntityId, string FieldName, string LockedValue, string Strength, string? Reason, DateTime CreatedAt, DateTime? ReleasedAt);
public sealed record MovieWorldDto(IReadOnlyList<MovieLocationDto> Locations, IReadOnlyList<MovieSetDto> Sets, IReadOnlyList<MoviePropDto> Props, IReadOnlyList<MovieWorldReferenceDto> References, IReadOnlyList<MovieWorldUsageDto> Usages, IReadOnlyList<MovieContinuityFactDto> Facts, IReadOnlyList<MovieContinuityLockDto> Locks);
public sealed record MovieClipDto(Guid Id, Guid? MovieSceneId, Guid? MovieShotId, Guid? GenerationJobId, Guid? AssetId, string Status, int? DurationSeconds, string? MetadataJson, string? ContinuitySnapshotJson, Guid? ContinuitySnapshotId = null, int? ContinuitySnapshotVersion = null, string? ContinuitySnapshotHash = null);
public sealed record MovieAssemblyDto(Guid Id, Guid? GenerationJobId, Guid? AssetId, string Status, string OutputFormat, string? MetadataJson, DateTime CreatedAt, DateTime? CompletedAt);
public sealed record MovieStudioProjectDto(Guid Id, Guid WorkspaceId, Guid? ProjectId, string Mode, string Status, string Title, string Description, int DurationSeconds, string AspectRatio, string Style, string Language, string? AdditionalInstructions, DateTime CreatedAt, DateTime UpdatedAt, MovieGuideDto Guide, IReadOnlyList<MovieSceneDto> Scenes, IReadOnlyList<MovieCharacterDto> Characters, IReadOnlyList<MovieLocationDto> Locations, IReadOnlyList<MovieClipDto> Clips, IReadOnlyList<MovieAssemblyDto> Assemblies, MovieWorldDto World);
public sealed record MovieStudioProjectResponse(MovieStudioProjectDto Project, GenerationJobDto? Job);
public sealed record MovieStudioProviderResponse(MovieProviderReadinessDto Provider);
public sealed record MovieStudioGenerationResponse(MovieStudioProjectDto Project, GenerationJobDto Job, Guid ClipId);
public sealed record MovieGuideRevisionResponse(MovieGuideRevisionDto Revision, MovieDirectorContextDto? AuthoritativeContext);
public sealed record MovieGuideHistoryResponse(Guid MovieGuideId, int CurrentRevisionNumber, int? LockedRevisionNumber, IReadOnlyList<MovieGuideRevisionDto> Revisions);

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
    public CinematographyIntentSelection? Cinematography { get; set; }
}

public sealed record MovieStudioSceneRequest(string Title, string Summary, int? DurationSeconds, string? ContinuityNotes, string? Narration, string? Dialogue);
public sealed class MovieStudioCharacterRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Appearance { get; set; }
    public string? PhysicalDescription { get; set; }
    public string? Wardrobe { get; set; }
    public string? VoiceReference { get; set; }
    public string? PersonalityAndStoryNotes { get; set; }
    public string? VoiceAndPerformance { get; set; }
    public string? ContinuityNotes { get; set; }
    public Guid? ReferenceAssetId { get; set; }
    public List<Guid>? ReferenceAssetIds { get; set; }
}
public sealed class MovieStudioCharacterStateRequest
{
    public string Key { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Wardrobe { get; set; }
    public string? AgeOrTimeState { get; set; }
    public string? Appearance { get; set; }
    public string? InjuryOrCondition { get; set; }
    public string? LocationOrStoryState { get; set; }
    public string? ContinuityNotes { get; set; }
}
public sealed class MovieStudioCharacterRelationshipRequest
{
    public Guid RelatedCharacterId { get; set; }
    public string RelationshipType { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
public sealed class MovieCharacterContinuityLockRequest
{
    public Guid? CharacterStateId { get; set; }
    public string FieldKey { get; set; } = string.Empty;
    public string LockedValue { get; set; } = string.Empty;
}

public sealed record MovieStudioLocationRequest(string Name, string Description, string? VisualContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieStudioSetRequest(string Name, string Description, string? EnvironmentType, Guid? MovieLocationId, string? VisualDescription, string? TimeOfDay, string? Weather, string? ContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieStudioSetVariationRequest(string Name, string? VisualDescription, string? TimeOfDay, string? Weather, string? Lighting, string? ContinuityNotes, Guid? ReferenceAssetId, bool IsDefault = false);
public sealed record MovieStudioPropRequest(string Name, string Description, string? Category, string? ContinuityNotes, Guid? ReferenceAssetId);
public sealed record MovieStudioWorldReferenceRequest(string Name, string Kind, string? Description, string? TagsJson, Guid? AssetId);
public sealed record MovieStudioWorldUsageRequest(string EntityType, Guid EntityId, Guid? MovieShotId, string? Role);
public sealed record MovieStudioContinuityFactRequest(string ScopeType, Guid? ScopeId, string FactKey, string FactValue, string? Notes);
public sealed record MovieStudioContinuityLockRequest(string EntityType, Guid? EntityId, string FieldName, string LockedValue, string? Strength, string? Reason);
public sealed class MovieGuideRevisionRequest
{
    public string StoryBibleJson { get; set; } = "{}";
    public string CharacterBibleReferencesJson { get; set; } = "[]";
    public string WorldBibleReferencesJson { get; set; } = "[]";
    public string VisualBibleJson { get; set; } = "{}";
    public string CinematographyBibleJson { get; set; } = "{}";
    public string AudioBibleJson { get; set; } = "{}";
    public string ContinuityBibleJson { get; set; } = "{}";
}
public sealed class MovieGuideLockRequest
{
    public int? RevisionNumber { get; set; }
}
public sealed record MovieStudioShotRequest(string Description, string? CameraAndFraming, string? CameraMotion, int? DurationSeconds, string? Narration, string? Dialogue, string? VisualContinuityNotes, CinematographyIntentSelection? Cinematography = null);
public sealed record MovieStudioGuideRequest(string? VisualLanguage, string? CameraLanguage, string? ColorAndLighting, string? SoundAndNarration, string? ContinuityRules, CinematographyIntentSelection? Cinematography = null);
public sealed record MovieStudioGenerationRequest(string? Title = null, decimal? EstimatedProviderCostUsd = null, GenerationCostEstimate? InternalCostEstimate = null);

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
        var cinematographyValidation = CinematographyIntentValidator.Validate(request.Cinematography);
        if (cinematographyValidation is not null) return cinematographyValidation;
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

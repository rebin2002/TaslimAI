using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public static class MovieFinalAssemblyProfiles
{
    public const string Hd1080p = "hd-1080p";
    public const string Uhd4K = "uhd-4k";

    private static readonly IReadOnlyDictionary<string, MovieFinalAssemblyProfile> Profiles =
        new Dictionary<string, MovieFinalAssemblyProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [Hd1080p] = new(Hd1080p, 1_920, 1_080),
            [Uhd4K] = new(Uhd4K, 3_840, 2_160),
        };

    public static bool TryGet(string? key, out MovieFinalAssemblyProfile profile) =>
        Profiles.TryGetValue(key?.Trim() ?? string.Empty, out profile!);
}

public sealed record MovieFinalAssemblyProfile(string Key, int Width, int Height);

public static class MovieAssemblyCaptionModes
{
    public const string None = "none";
    public const string BurnIn = "burn_in";
    public const string Embedded = "embedded";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { None, BurnIn, Embedded };
}

public static class MovieFinalAssemblyQcStatuses
{
    public const string NotRun = "NotRun";
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string ReviewRequired = "ReviewRequired";
}

public sealed class MovieFinalAssemblyRequest
{
    public string ResolutionProfile { get; set; } = MovieFinalAssemblyProfiles.Uhd4K;
    public IReadOnlyList<MovieAssemblyTimelineItemRequest> Timeline { get; set; } = [];
    public IReadOnlyList<MovieAssemblyAudioMixInputRequest> AudioMixInputs { get; set; } = [];
    public bool IncludeApprovedSoundtrackCues { get; set; }
    public MovieAssemblyCaptionsRequest Captions { get; set; } = new();
}

public sealed class MovieAssemblyTimelineItemRequest
{
    public Guid TakeId { get; set; }
    public decimal InPointSeconds { get; set; }
    public decimal? OutPointSeconds { get; set; }
}

public sealed class MovieAssemblyAudioMixInputRequest
{
    public Guid AssetId { get; set; }
    public string Role { get; set; } = "music";
    public decimal GainDb { get; set; }
    public decimal? StartTimeSeconds { get; set; }
    public decimal? EndTimeSeconds { get; set; }
    public bool Required { get; set; } = true;
}

public sealed class MovieAssemblyCaptionsRequest
{
    public string Mode { get; set; } = MovieAssemblyCaptionModes.None;
    public Guid? AssetId { get; set; }
    public string? Language { get; set; }
}

public sealed record MovieAssemblyTimelineItem(
    Guid TakeId,
    Guid AssetId,
    int Sequence,
    decimal InPointSeconds,
    decimal? OutPointSeconds,
    string? ContinuitySnapshotHash);

public sealed record MovieAssemblyAudioMixInput(
    Guid AssetId,
    string Role,
    decimal GainDb,
    decimal? StartTimeSeconds,
    decimal? EndTimeSeconds,
    bool Required);

public sealed record MovieAssemblyCaptions(
    string Mode,
    Guid? AssetId,
    string? Language);

public sealed record MovieFinalAssemblyInput(
    string Operation,
    Guid AssemblyId,
    Guid MovieProjectId,
    string ResolutionProfile,
    MovieFinalAssemblyProfile Profile,
    IReadOnlyList<MovieAssemblyTimelineItem> Timeline,
    IReadOnlyList<MovieAssemblyAudioMixInput> AudioMixInputs,
    MovieAssemblyCaptions Captions,
    string RequestFingerprint,
    string IdempotencyKey,
    int ExpectedDurationSeconds,
    IReadOnlyList<MovieTimelineTransitionContract>? Transitions = null);

public sealed record MovieFinalAssemblyDto(
    Guid Id,
    Guid MovieProjectId,
    Guid? GenerationJobId,
    Guid? OutputAssetId,
    string Status,
    string ResolutionProfile,
    int OutputWidth,
    int OutputHeight,
    int TimelineItemCount,
    IReadOnlyList<Guid> SourceTakeIds,
    int AudioMixInputCount,
    string CaptionsMode,
    string QcStatus,
    string? QcResultJson,
    string? ProvenanceJson,
    int ProgressPercent,
    int AttemptCount,
    bool CanResume,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    GenerationJobDto? Job = null);

public interface IMovieFinalAssemblyService
{
    Task<MovieFinalAssemblyDto?> RequestAsync(Guid userId, Guid movieProjectId, MovieFinalAssemblyRequest request, string? idempotencyKey, CancellationToken cancellationToken);
    Task<MovieFinalAssemblyDto?> GetAsync(Guid userId, Guid assemblyId, CancellationToken cancellationToken);
}

public sealed class MovieFinalAssemblyService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IGenerationJobService jobs,
    IMovieTimelineTransitionService transitionEdits) : IMovieFinalAssemblyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<MovieFinalAssemblyDto?> RequestAsync(Guid userId, Guid movieProjectId, MovieFinalAssemblyRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.FinalApproval, cancellationToken)) return null;
        if (!MovieFinalAssemblyProfiles.TryGet(request.ResolutionProfile, out var profile))
            throw new MovieFinalAssemblyValidationException("ASSEMBLY_PROFILE_UNSUPPORTED", "Choose a supported final output profile.");

        var captions = NormalizeCaptions(request.Captions);
        var shots = await db.MovieShots.AsNoTracking()
            .Include(item => item.Scene)
            .Where(item => item.Scene.MovieProjectId == movieProjectId)
            .OrderBy(item => item.Scene.Sequence)
            .ThenBy(item => item.Sequence)
            .ToListAsync(cancellationToken);
        var selectedTakeIds = shots
            .Select(item => item.FinalTakeId ?? item.SelectedTakeId)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        var requestedTimeline = request.Timeline ?? [];
        var timelineTakeIds = requestedTimeline.Count == 0 ? selectedTakeIds : requestedTimeline.Select(item => item.TakeId).ToArray();
        if (timelineTakeIds.Length == 0 || timelineTakeIds.Length > 500 || timelineTakeIds.Distinct().Count() != timelineTakeIds.Length)
            throw new MovieFinalAssemblyValidationException("ASSEMBLY_TIMELINE_INVALID", "The final timeline must contain between one and 500 unique selected takes.");

        var takes = await db.MovieTakes.AsNoTracking()
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene)
            .Include(item => item.Asset).ThenInclude(item => item!.StoredFile)
            .Include(item => item.MovieClip).ThenInclude(item => item!.Asset).ThenInclude(item => item!.StoredFile)
            .Where(item => timelineTakeIds.Contains(item.Id) && item.MovieShot.Scene.MovieProjectId == movieProjectId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var canonicalTimeline = new List<MovieAssemblyTimelineItem>(timelineTakeIds.Length);
        for (var index = 0; index < timelineTakeIds.Length; index++)
        {
            var takeId = timelineTakeIds[index];
            if (!takes.TryGetValue(takeId, out var take))
                throw new MovieFinalAssemblyValidationException("ASSEMBLY_TIMELINE_SOURCE_NOT_FOUND", "Every timeline take must belong to this movie project.");
            var shot = take.MovieShot;
            var selected = shot.FinalTakeId == take.Id || shot.SelectedTakeId == take.Id || take.SelectedAt.HasValue;
            var approved = take.Status is MovieTakeStatuses.Approved or MovieTakeStatuses.Selected;
            if (!selected || !approved)
                throw new MovieFinalAssemblyValidationException("ASSEMBLY_TIMELINE_SOURCE_NOT_APPROVED", "Only approved selected takes can be assembled into a master.");
            var sourceAsset = take.Asset ?? take.MovieClip?.Asset;
            if (sourceAsset is null || !string.Equals(sourceAsset.AssetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase) || sourceAsset.StoredFile?.Status != StoredFileStatus.Ready)
                throw new MovieFinalAssemblyValidationException("ASSEMBLY_TIMELINE_SOURCE_UNAVAILABLE", "Every selected take must have a ready video asset.");
            var supplied = requestedTimeline.Count == 0 ? null : requestedTimeline[index];
            ValidateTrim(supplied?.InPointSeconds ?? 0m, supplied?.OutPointSeconds);
            canonicalTimeline.Add(new(
                take.Id,
                sourceAsset.Id,
                index,
                supplied?.InPointSeconds ?? 0m,
                supplied?.OutPointSeconds,
                take.MovieClip?.ContinuitySnapshotHash));
        }

        var requestedAudioInputs = request.AudioMixInputs ?? [];
        if (request.IncludeApprovedSoundtrackCues)
        {
            var explicitAssetIds = requestedAudioInputs.Select(item => item.AssetId).ToHashSet();
            var projected = await ProjectApprovedSoundtrackInputsAsync(movieProjectId, explicitAssetIds, cancellationToken);
            requestedAudioInputs = requestedAudioInputs.Concat(projected).ToArray();
        }
        var audioInputs = await NormalizeAudioInputsAsync(movie.WorkspaceId, movie.ProjectId, requestedAudioInputs, cancellationToken);
        var captionAsset = await ValidateCaptionAssetAsync(movie.WorkspaceId, movie.ProjectId, captions, cancellationToken);
        var persistedTransitions = (await transitionEdits.GetLatestAsync(userId, movieProjectId, cancellationToken))?.Timeline.Transitions ?? [];
        var expectedDuration = Math.Max(1, movie.DurationSeconds);
        var canonicalPayload = new
        {
            schemaVersion = 1,
            movieProjectId,
            profile = profile.Key,
            timeline = canonicalTimeline,
            transitions = persistedTransitions,
            audioMixInputs = audioInputs,
            includeApprovedSoundtrackCues = request.IncludeApprovedSoundtrackCues,
            captions,
            expectedDuration,
        };
        var canonicalJson = JsonSerializer.Serialize(canonicalPayload, JsonOptions);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey) ?? $"assembly:{fingerprint[..48]}";
        var existing = await db.MovieAssemblies
            .Include(item => item.GenerationJob).ThenInclude(item => item!.Outputs)
            .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId && item.IdempotencyKey == normalizedIdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new MovieFinalAssemblyValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different assembly request.");
            await ReconcileTerminalJobAsync(existing, cancellationToken);
            return ToDto(existing);
        }

        var now = DateTime.UtcNow;
        var assembly = new MovieAssembly
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movieProjectId,
            Status = MovieAssemblyStatuses.Planned,
            OutputFormat = "mp4",
            ResolutionProfile = profile.Key,
            OutputWidth = profile.Width,
            OutputHeight = profile.Height,
            TimelineJson = JsonSerializer.Serialize(canonicalTimeline, JsonOptions),
            AudioMixJson = JsonSerializer.Serialize(audioInputs, JsonOptions),
            CaptionsJson = JsonSerializer.Serialize(captions, JsonOptions),
            IdempotencyKey = normalizedIdempotencyKey,
            RequestFingerprint = fingerprint,
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                contract = "movie-final-assembly.v1",
                movieProjectId,
                sourceTakeIds = canonicalTimeline.Select(item => item.TakeId).ToArray(),
                sourceAssetIds = canonicalTimeline.Select(item => item.AssetId).ToArray(),
                audioAssetIds = audioInputs.Select(item => item.AssetId).ToArray(),
                includeApprovedSoundtrackCues = request.IncludeApprovedSoundtrackCues,
                transitions = persistedTransitions,
                captionsAssetId = captionAsset?.Id,
                resolutionProfile = profile.Key,
                outputWidth = profile.Width,
                outputHeight = profile.Height,
            }, JsonOptions),
            CheckpointJson = JsonSerializer.Serialize(new { phase = "planned", progressPercent = 0 }, JsonOptions),
            QcStatus = MovieFinalAssemblyQcStatuses.NotRun,
            ProgressPercent = 0,
            RequestedByUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieAssemblies.Add(assembly);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique (MovieProjectId, IdempotencyKey) index is the final
            // concurrency gate. If another request won the race after both
            // callers passed the read-before-write check, replay that durable
            // row instead of leaking a database exception to the client.
            db.Entry(assembly).State = EntityState.Detached;
            var concurrent = await db.MovieAssemblies
                .Include(item => item.GenerationJob).ThenInclude(item => item!.Outputs)
                .FirstOrDefaultAsync(item => item.MovieProjectId == movieProjectId && item.IdempotencyKey == normalizedIdempotencyKey, cancellationToken);
            if (concurrent is null) throw;
            if (!string.Equals(concurrent.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new MovieFinalAssemblyValidationException("IDEMPOTENCY_KEY_REUSED", "This idempotency key was already used for a different assembly request.");
            return ToDto(concurrent);
        }

        var input = new MovieFinalAssemblyInput(
            "final_assembly",
            assembly.Id,
            movieProjectId,
            profile.Key,
            profile,
            canonicalTimeline,
            audioInputs,
            captions,
            fingerprint,
            normalizedIdempotencyKey,
            expectedDuration,
            persistedTransitions);
        try
        {
            var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = movie.WorkspaceId,
                ProjectId = movie.ProjectId,
                JobType = GenerationJobTypes.MovieAssembly,
                Title = $"{movie.Title} master export",
                // Movie generation job payloads use the default System.Text.Json contract.
                // Every worker-side reader (GenerationJobExecution target validation and
                // MovieFinalAssemblyJobHandler) deserializes with default options, so the
                // producer must match. Serializing with web options here previously produced
                // camelCase payloads that read back as Guid.Empty and rejected every assembly.
                InputJson = JsonSerializer.Serialize(input),
                EstimatedProviderCostUsd = 0m,
                InternalCostEstimate = new GenerationCostEstimate(true, 0m, UsageCurrencies.Usd, null, null, null, []),
            }, cancellationToken, normalizedIdempotencyKey);
            db.Entry(job).State = EntityState.Detached;
            assembly.GenerationJobId = job.Id;
            assembly.Status = MovieAssemblyStatuses.Queued;
            assembly.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(assembly, job);
        }
        catch
        {
            assembly.Status = MovieAssemblyStatuses.Failed;
            assembly.LastErrorCode = GenerationJobErrorCodes.MovieAssemblyQueueFailed;
            assembly.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<MovieFinalAssemblyDto?> GetAsync(Guid userId, Guid assemblyId, CancellationToken cancellationToken)
    {
        var assembly = await db.MovieAssemblies
            .Include(item => item.GenerationJob).ThenInclude(item => item!.Outputs)
            .FirstOrDefaultAsync(item => item.Id == assemblyId, cancellationToken);
        if (assembly is null || !await collaboration.HasPermissionAsync(userId, assembly.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        await ReconcileTerminalJobAsync(assembly, cancellationToken);
        return ToDto(assembly);
    }

    /// <summary>
    /// Repairs the movie-specific projection when the canonical generation job
    /// is already terminal but the worker's sidecar finalization was interrupted
    /// or lost a fenced update. Successful reconciliation requires the exact
    /// published asset and ready stored file, so an incomplete publication can
    /// never become downloadable by observation.
    /// </summary>
    private async Task ReconcileTerminalJobAsync(MovieAssembly assembly, CancellationToken cancellationToken)
    {
        var job = assembly.GenerationJob;
        if (job is null || IsTerminalAssemblyStatus(assembly.Status)) return;

        if (job.Status == GenerationJobStatus.Failed)
        {
            var errorCode = job.ErrorCode ?? GenerationJobErrorCodes.MovieAssemblyExecutionFailed;
            var updated = await db.MovieAssemblies
                .Where(item => item.Id == assembly.Id
                    && item.GenerationJobId == job.Id
                    && item.Status != MovieAssemblyStatuses.Ready
                    && item.Status != MovieAssemblyStatuses.Failed
                    && item.Status != MovieAssemblyStatuses.Cancelled)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, MovieAssemblyStatuses.Failed)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.QcStatus, MovieFinalAssemblyQcStatuses.Failed)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (updated > 0)
            {
                assembly.Status = MovieAssemblyStatuses.Failed;
                assembly.LastErrorCode = errorCode;
                assembly.QcStatus = MovieFinalAssemblyQcStatuses.Failed;
                assembly.UpdatedAt = DateTime.UtcNow;
            }
            return;
        }

        if (job.Status == GenerationJobStatus.Cancelled)
        {
            var errorCode = job.ErrorCode ?? GenerationJobErrorCodes.MovieAssemblyCancelled;
            var updated = await db.MovieAssemblies
                .Where(item => item.Id == assembly.Id
                    && item.GenerationJobId == job.Id
                    && item.Status != MovieAssemblyStatuses.Ready
                    && item.Status != MovieAssemblyStatuses.Failed
                    && item.Status != MovieAssemblyStatuses.Cancelled)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, MovieAssemblyStatuses.Cancelled)
                    .SetProperty(item => item.LastErrorCode, errorCode)
                    .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
            if (updated > 0)
            {
                assembly.Status = MovieAssemblyStatuses.Cancelled;
                assembly.LastErrorCode = errorCode;
                assembly.UpdatedAt = DateTime.UtcNow;
            }
            return;
        }

        if (job.Status != GenerationJobStatus.Succeeded || assembly.AssetId is not Guid assetId) return;

        var hasPublishedAsset = await db.Assets.AsNoTracking()
            .AnyAsync(item => item.Id == assetId
                && item.SourceGenerationJobId == job.Id
                && item.Status == AssetStatus.Active
                && item.StoredFile != null
                && item.StoredFile.Status == StoredFileStatus.Ready, cancellationToken);
        if (!hasPublishedAsset) return;

        var checkpoint = JsonSerializer.Serialize(new { phase = "published", progressPercent = 100 });
        var now = DateTime.UtcNow;
        var ready = await db.MovieAssemblies
            .Where(item => item.Id == assembly.Id
                && item.GenerationJobId == job.Id
                && item.Status != MovieAssemblyStatuses.Ready
                && item.Status != MovieAssemblyStatuses.Failed
                && item.Status != MovieAssemblyStatuses.Cancelled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, MovieAssemblyStatuses.Ready)
                .SetProperty(item => item.QcStatus, MovieFinalAssemblyQcStatuses.Passed)
                .SetProperty(item => item.ProgressPercent, 100)
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.CheckpointJson, checkpoint), cancellationToken);
        if (ready > 0)
        {
            assembly.Status = MovieAssemblyStatuses.Ready;
            assembly.QcStatus = MovieFinalAssemblyQcStatuses.Passed;
            assembly.ProgressPercent = 100;
            assembly.CompletedAt = now;
            assembly.UpdatedAt = now;
            assembly.CheckpointJson = checkpoint;
        }
    }

    private static bool IsTerminalAssemblyStatus(string status) =>
        status is MovieAssemblyStatuses.Ready or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled;

    private async Task<IReadOnlyList<MovieAssemblyAudioMixInput>> NormalizeAudioInputsAsync(Guid workspaceId, Guid? projectId, IReadOnlyList<MovieAssemblyAudioMixInputRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count > 64) throw new MovieFinalAssemblyValidationException("ASSEMBLY_AUDIO_MIX_INVALID", "A final mix can contain at most 64 audio inputs.");
        var ids = requests.Select(item => item.AssetId).ToArray();
        if (ids.Any(item => item == Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new MovieFinalAssemblyValidationException("ASSEMBLY_AUDIO_MIX_INVALID", "Audio mix inputs must use unique asset identifiers.");
        var assets = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .Where(item => ids.Contains(item.Id) && item.WorkspaceId == workspaceId)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var normalized = new List<MovieAssemblyAudioMixInput>(requests.Count);
        foreach (var request in requests)
        {
            if (!assets.TryGetValue(request.AssetId, out var asset)
                || asset.Status != AssetStatus.Active
                || asset.ProjectId.HasValue && asset.ProjectId != projectId
                || (!string.Equals(asset.AssetType, AssetTypes.Audio, StringComparison.OrdinalIgnoreCase) && !string.Equals(asset.AssetType, AssetTypes.Music, StringComparison.OrdinalIgnoreCase))
                || asset.StoredFile?.Status != StoredFileStatus.Ready)
                throw new MovieFinalAssemblyValidationException("ASSEMBLY_AUDIO_SOURCE_UNAVAILABLE", "Every audio mix input must be a ready audio asset in the movie workspace.");
            if (request.GainDb is < -60m or > 12m || request.StartTimeSeconds is < 0m || request.EndTimeSeconds is <= 0m || request.EndTimeSeconds <= request.StartTimeSeconds)
                throw new MovieFinalAssemblyValidationException("ASSEMBLY_AUDIO_MIX_INVALID", "Audio mix gain and time bounds are outside the supported range.");
            var role = string.IsNullOrWhiteSpace(request.Role) ? "music" : request.Role.Trim().ToLowerInvariant();
            if (role.Length > 40) throw new MovieFinalAssemblyValidationException("ASSEMBLY_AUDIO_MIX_INVALID", "Audio mix roles are limited to 40 characters.");
            normalized.Add(new(request.AssetId, role, request.GainDb, request.StartTimeSeconds, request.EndTimeSeconds, request.Required));
        }
        return normalized;
    }

    private async Task<IReadOnlyList<MovieAssemblyAudioMixInputRequest>> ProjectApprovedSoundtrackInputsAsync(Guid movieProjectId, IReadOnlySet<Guid> explicitAssetIds, CancellationToken cancellationToken)
    {
        var cues = await db.MovieSoundtrackCues.AsNoTracking()
            .Include(item => item.Versions)
            .Where(item => item.MovieProjectId == movieProjectId && item.ApprovalState == MovieSoundtrackApprovalStates.Approved && item.ApprovedVersionId.HasValue)
            .ToListAsync(cancellationToken);
        return cues
            .OrderBy(item => item.TimelineStartSeconds)
            .ThenBy(item => item.Sequence)
            .Select(cue => (cue, version: cue.Versions.FirstOrDefault(version => version.Id == cue.ApprovedVersionId && version.AssetId.HasValue)))
            .Where(item => item.version?.AssetId is Guid assetId && !explicitAssetIds.Contains(assetId))
            .Select(item => new MovieAssemblyAudioMixInputRequest
            {
                AssetId = item.version!.AssetId!.Value,
                Role = "music",
                GainDb = 0m,
                StartTimeSeconds = item.cue.TimelineStartSeconds,
                EndTimeSeconds = item.cue.TimelineStartSeconds + item.cue.DurationSeconds,
                Required = true,
            })
            .ToArray();
    }

    private async Task<Asset?> ValidateCaptionAssetAsync(Guid workspaceId, Guid? projectId, MovieAssemblyCaptions captions, CancellationToken cancellationToken)
    {
        if (captions.Mode == MovieAssemblyCaptionModes.None) return null;
        if (!captions.AssetId.HasValue) throw new MovieFinalAssemblyValidationException("ASSEMBLY_CAPTIONS_SOURCE_REQUIRED", "A captions asset is required for the selected captions mode.");
        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .FirstOrDefaultAsync(item => item.Id == captions.AssetId && item.WorkspaceId == workspaceId, cancellationToken);
        if (asset is null || asset.Status != AssetStatus.Active || asset.ProjectId.HasValue && asset.ProjectId != projectId || asset.StoredFile?.Status != StoredFileStatus.Ready || !string.Equals(asset.AssetType, AssetTypes.File, StringComparison.OrdinalIgnoreCase))
            throw new MovieFinalAssemblyValidationException("ASSEMBLY_CAPTIONS_SOURCE_UNAVAILABLE", "The captions asset is not available in the movie workspace.");
        return asset;
    }

    private static MovieAssemblyCaptions NormalizeCaptions(MovieAssemblyCaptionsRequest? request)
    {
        request ??= new();
        var mode = request.Mode?.Trim().ToLowerInvariant() ?? MovieAssemblyCaptionModes.None;
        if (!MovieAssemblyCaptionModes.Supported.Contains(mode)) throw new MovieFinalAssemblyValidationException("ASSEMBLY_CAPTIONS_MODE_INVALID", "Choose a supported captions option.");
        var language = string.IsNullOrWhiteSpace(request.Language) ? null : request.Language.Trim().ToLowerInvariant();
        if (language is not null && language.Length > 16) throw new MovieFinalAssemblyValidationException("ASSEMBLY_CAPTIONS_LANGUAGE_INVALID", "The captions language is too long.");
        return new(mode, mode == MovieAssemblyCaptionModes.None ? null : request.AssetId, language);
    }

    private static void ValidateTrim(decimal start, decimal? end)
    {
        if (start < 0m || start > 86_400m || end is < 0m or > 86_400m || end.HasValue && end <= start)
            throw new MovieFinalAssemblyValidationException("ASSEMBLY_TIMELINE_TRIM_INVALID", "Timeline trim points must be bounded and ordered.");
    }

    private static string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim();
        if (normalized.Length > 80) throw new MovieFinalAssemblyValidationException("IDEMPOTENCY_KEY_INVALID", "The idempotency key is too long.");
        return normalized;
    }

    public static MovieFinalAssemblyDto ToDto(MovieAssembly assembly, GenerationJob? job = null)
    {
        IReadOnlyList<Guid> takeIds = [];
        try
        {
            takeIds = JsonSerializer.Deserialize<List<MovieAssemblyTimelineItem>>(assembly.TimelineJson, JsonOptions)?.Select(item => item.TakeId).ToArray() ?? [];
        }
        catch (JsonException) { }
        MovieFinalAssemblyDto dto = new(
            assembly.Id,
            assembly.MovieProjectId,
            assembly.GenerationJobId,
            assembly.AssetId,
            assembly.Status,
            assembly.ResolutionProfile,
            assembly.OutputWidth,
            assembly.OutputHeight,
            takeIds.Count,
            takeIds,
            CountJsonArray(assembly.AudioMixJson),
            ReadCaptionMode(assembly.CaptionsJson),
            assembly.QcStatus,
            assembly.QcResultJson,
            assembly.ProvenanceJson,
            assembly.ProgressPercent,
            assembly.AttemptCount,
            assembly.Status is MovieAssemblyStatuses.Planned or MovieAssemblyStatuses.Queued or MovieAssemblyStatuses.Assembling or MovieAssemblyStatuses.Failed or MovieAssemblyStatuses.Cancelled,
            assembly.CreatedAt,
            assembly.UpdatedAt,
            assembly.CompletedAt,
            job is null ? null : GenerationJobContractMapper.ToMovieDto(job));
        return dto;
    }

    private static int CountJsonArray(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0; }
        catch (JsonException) { return 0; }
    }

    private static string ReadCaptionMode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() ?? MovieAssemblyCaptionModes.None : MovieAssemblyCaptionModes.None;
        }
        catch (JsonException) { return MovieAssemblyCaptionModes.None; }
    }
}

public sealed class MovieFinalAssemblyValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MovieFinalAssemblyUnavailableException() : Exception("The local final assembly executor is disabled.");
public sealed class MovieFinalAssemblyExecutionException(string code, string message = "The final assembly could not be completed.") : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record MovieFinalAssemblyQcResult(
    string ContractVersion,
    string Status,
    IReadOnlyList<string> Findings,
    int Width,
    int Height,
    int DurationSeconds,
    string? OutputSha256)
{
    public bool Passed => Status == MovieFinalAssemblyQcStatuses.Passed;
}

public sealed class MovieFinalAssemblyQualityControl
{
    public const string ContractVersion = "movie-final-assembly-qc.v1";

    public MovieFinalAssemblyQcResult Evaluate(MovieFinalAssemblyInput input, MovieFinalAssemblyExecutionResult output)
    {
        var findings = new List<string>();
        if (output.Resolution.Width != input.Profile.Width || output.Resolution.Height != input.Profile.Height) findings.Add("MOVIE_ASSEMBLY_QC_RESOLUTION_MISMATCH");
        if (output.SizeBytes <= 0) findings.Add("MOVIE_ASSEMBLY_QC_OUTPUT_EMPTY");
        if (!output.DurationMeasured) findings.Add("MOVIE_ASSEMBLY_QC_DURATION_UNMEASURED");
        if (output.DurationSeconds <= 0) findings.Add("MOVIE_ASSEMBLY_QC_DURATION_INVALID");
        if (output.DurationSeconds > Math.Max(1, input.ExpectedDurationSeconds) + 5) findings.Add("MOVIE_ASSEMBLY_QC_DURATION_EXCEEDED");
        return new(ContractVersion, findings.Count == 0 ? MovieFinalAssemblyQcStatuses.Passed : MovieFinalAssemblyQcStatuses.Failed, findings, output.Resolution.Width, output.Resolution.Height, output.DurationSeconds, output.OutputSha256);
    }
}

public sealed record MovieFinalAssemblyExecutionRequest(
    MovieFinalAssemblyInput Contract,
    IReadOnlyDictionary<Guid, string> SourcePaths);

public sealed record MovieFinalAssemblyExecutionResult(
    string FileName,
    string ContentType,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenReadAsync,
    MovieResolution Resolution,
    int DurationSeconds,
    string? OutputSha256,
    string? MetadataJson = null,
    bool DurationMeasured = true);

public interface IMovieFinalAssemblyExecutor
{
    string Key { get; }
    bool IsAvailable { get; }
    Task<MovieFinalAssemblyExecutionResult> ExecuteAsync(MovieFinalAssemblyExecutionRequest request, IProgress<int> progress, CancellationToken cancellationToken);
}

public sealed class UnavailableMovieFinalAssemblyExecutor : IMovieFinalAssemblyExecutor
{
    public string Key => "unconfigured";
    public bool IsAvailable => false;
    public Task<MovieFinalAssemblyExecutionResult> ExecuteAsync(MovieFinalAssemblyExecutionRequest request, IProgress<int> progress, CancellationToken cancellationToken) => Task.FromException<MovieFinalAssemblyExecutionResult>(new MovieFinalAssemblyUnavailableException());
}

public sealed class MovieFinalAssemblyOptions
{
    public bool Enabled { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public long MaxOutputBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int ProcessTimeoutSeconds { get; set; } = 3_600;
}

public static class FfmpegMovieFinalAssemblyCommandBuilder
{
    public static IReadOnlyList<string> Build(MovieFinalAssemblyExecutionRequest request, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("An output path is required.", nameof(outputPath));
        if (request.Contract.Timeline.Count == 0) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_TIMELINE_EMPTY");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var item in request.Contract.Timeline)
        {
            if (!request.SourcePaths.TryGetValue(item.AssetId, out var path)) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_SOURCE_PATH_MISSING");
            args.Add("-i"); args.Add(path);
        }
        foreach (var item in request.Contract.AudioMixInputs)
        {
            if (!request.SourcePaths.TryGetValue(item.AssetId, out var path)) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_AUDIO_PATH_MISSING");
            args.Add("-i"); args.Add(path);
        }
        var filters = new List<string>();
        for (var index = 0; index < request.Contract.Timeline.Count; index++)
        {
            var item = request.Contract.Timeline[index];
            var trim = item.OutPointSeconds.HasValue
                ? $"trim=start={Format(item.InPointSeconds)}:end={Format(item.OutPointSeconds.Value)}"
                : $"trim=start={Format(item.InPointSeconds)}";
            filters.Add($"[{index}:v]{trim},setpts=PTS-STARTPTS[v{index}];[{index}:a]{trim},asetpts=PTS-STARTPTS[a{index}]");
        }
        var videoInputs = string.Concat(request.Contract.Timeline.Select((_, index) => $"[v{index}][a{index}]"));
        filters.Add($"{videoInputs}concat=n={request.Contract.Timeline.Count}:v=1:a=1[videoBase][timelineAudio]");
        var videoLabel = "videoBase";
        if (request.Contract.Captions.Mode == MovieAssemblyCaptionModes.BurnIn)
        {
            if (!request.Contract.Captions.AssetId.HasValue || !request.SourcePaths.TryGetValue(request.Contract.Captions.AssetId.Value, out var captionsPath)) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_CAPTIONS_PATH_MISSING");
            filters.Add($"[videoBase]subtitles=filename='{EscapeFilterValue(captionsPath)}'[captioned]");
            videoLabel = "captioned";
        }
        filters.Add($"[{videoLabel}]scale={request.Contract.Profile.Width}:{request.Contract.Profile.Height}:force_original_aspect_ratio=decrease,pad={request.Contract.Profile.Width}:{request.Contract.Profile.Height}:(ow-iw)/2:(oh-ih)/2:color=black[video]");
        var audioLabel = "timelineAudio";
        if (request.Contract.AudioMixInputs.Count > 0)
        {
            var audioLabels = new List<string> { "[timelineAudio]" };
            for (var index = 0; index < request.Contract.AudioMixInputs.Count; index++)
            {
                var item = request.Contract.AudioMixInputs[index];
                var inputIndex = request.Contract.Timeline.Count + index;
                filters.Add($"[{inputIndex}:a]volume={Gain(item.GainDb)}[mix{index}]");
                audioLabels.Add($"[mix{index}]");
            }
            filters.Add($"{string.Concat(audioLabels)}amix=inputs={audioLabels.Count}:duration=first:dropout_transition=0[audio]");
            audioLabel = "audio";
        }
        args.Add("-filter_complex"); args.Add(string.Join(';', filters));
        args.Add("-map"); args.Add("[video]");
        args.Add("-map"); args.Add($"[{audioLabel}]");
        if (request.Contract.Captions.Mode == MovieAssemblyCaptionModes.Embedded && request.Contract.Captions.AssetId.HasValue)
        {
            var captionIndex = request.Contract.Timeline.Count + request.Contract.AudioMixInputs.Count;
            if (!request.SourcePaths.ContainsKey(request.Contract.Captions.AssetId.Value)) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_CAPTIONS_PATH_MISSING");
            args.Add("-i"); args.Add(request.SourcePaths[request.Contract.Captions.AssetId.Value]);
            args.Add("-map"); args.Add($"{captionIndex}:0");
            args.Add("-c:s"); args.Add("mov_text");
        }
        args.Add("-c:v"); args.Add("libx264");
        args.Add("-pix_fmt"); args.Add("yuv420p");
        args.Add("-c:a"); args.Add("aac");
        args.Add("-movflags"); args.Add("+faststart");
        args.Add("-shortest");
        args.Add("-f"); args.Add("mp4");
        args.Add(outputPath);
        return args;
    }

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Gain(decimal gainDb) => Math.Pow(10d, (double)gainDb / 20d).ToString("0.######", CultureInfo.InvariantCulture);
    private static string EscapeFilterValue(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
}

public sealed class FfmpegMovieFinalAssemblyExecutor(IOptions<MovieFinalAssemblyOptions> options) : IMovieFinalAssemblyExecutor
{
    private readonly MovieFinalAssemblyOptions settings = options.Value;
    public string Key => "local";
    public bool IsAvailable => settings.Enabled && (Path.IsPathRooted(settings.FfmpegPath) ? File.Exists(settings.FfmpegPath) : !string.IsNullOrWhiteSpace(settings.FfmpegPath));

    public async Task<MovieFinalAssemblyExecutionResult> ExecuteAsync(MovieFinalAssemblyExecutionRequest request, IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (!IsAvailable) throw new MovieFinalAssemblyUnavailableException();
        var directory = Path.Combine(Path.GetTempPath(), "taslim-final-assembly", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, "master.mp4");
        try
        {
            var start = Stopwatch.GetTimestamp();
            var startInfo = new ProcessStartInfo { FileName = settings.FfmpegPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in FfmpegMovieFinalAssemblyCommandBuilder.Build(request, outputPath)) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_EXECUTION_FAILED");
            progress.Report(35);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(Math.Clamp(settings.ProcessTimeoutSeconds, 1, 86_400)), cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_EXECUTION_FAILED", string.IsNullOrWhiteSpace(error) ? "The local assembly process failed." : "The local assembly process failed safely.");
            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length <= 0 || info.Length > Math.Min(2L * 1024 * 1024 * 1024, Math.Max(1, settings.MaxOutputBytes))) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_OUTPUT_INVALID");
            progress.Report(90);
            var hash = await ComputeSha256Async(outputPath, cancellationToken);
            var duration = await MeasureDurationAsync(outputPath, cancellationToken);
            return new(
                "master.mp4",
                "video/mp4",
                info.Length,
                _ => Task.FromResult<Stream>(new DeleteOnDisposeFileStream(outputPath, directory)),
                new MovieResolution(request.Contract.Profile.Width, request.Contract.Profile.Height),
                duration,
                hash,
                JsonSerializer.Serialize(new { width = request.Contract.Profile.Width, height = request.Contract.Profile.Height, durationSeconds = duration, durationMeasured = true, outputSha256 = hash, elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds }),
                true);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private async Task<int> MeasureDurationAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(settings.FfprobePath) && !File.Exists(settings.FfprobePath))
            throw new MovieFinalAssemblyExecutionException("ASSEMBLY_DURATION_MEASUREMENT_FAILED");
        if (string.IsNullOrWhiteSpace(settings.FfprobePath))
            throw new MovieFinalAssemblyExecutionException("ASSEMBLY_DURATION_MEASUREMENT_FAILED");

        var startInfo = new ProcessStartInfo
        {
            FileName = settings.FfprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new MovieFinalAssemblyExecutionException("ASSEMBLY_DURATION_MEASUREMENT_FAILED");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(Math.Clamp(settings.ProcessTimeoutSeconds, 1, 86_400)), cancellationToken);
        var output = (await outputTask).Trim();
        _ = await errorTask;
        if (process.ExitCode != 0 || !decimal.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0m || seconds > int.MaxValue)
            throw new MovieFinalAssemblyExecutionException("ASSEMBLY_DURATION_MEASUREMENT_FAILED");
        return Math.Max(1, checked((int)Math.Round(seconds, MidpointRounding.AwayFromZero)));
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { }
    }

    private sealed class DeleteOnDisposeFileStream(string path, string directory) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, System.IO.FileOptions.Asynchronous)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) TryDelete(directory);
        }
    }
}

public sealed class MovieFinalAssemblyExecutionStore(TaslimDbContext db)
{
    public async Task MarkRunningAsync(Guid jobId, Guid token, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieAssemblies.Where(item => item.GenerationJobId == jobId && item.GenerationJob!.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieAssemblyStatuses.Assembling).SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1).SetProperty(item => item.ProgressPercent, 5).SetProperty(item => item.LastErrorCode, (string?)null).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task TouchCheckpointAsync(Guid assemblyId, Guid jobId, Guid token, string phase, int progress, CancellationToken cancellationToken)
    {
        var checkpoint = JsonSerializer.Serialize(new { phase, progressPercent = Math.Clamp(progress, 0, 99), updatedAt = DateTime.UtcNow });
        var now = DateTime.UtcNow;
        await db.MovieAssemblies.Where(item => item.Id == assemblyId && item.GenerationJobId == jobId && item.GenerationJob!.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CheckpointJson, checkpoint).SetProperty(item => item.ProgressPercent, Math.Clamp(progress, 0, 99)).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkReadyAsync(Guid jobId, Guid token, Guid assetId, string? metadataJson, string qcResultJson, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var checkpoint = JsonSerializer.Serialize(new { phase = "published", progressPercent = 100 });
        await db.MovieAssemblies.Where(item => item.GenerationJobId == jobId && item.GenerationJob!.Status == GenerationJobStatus.Succeeded && item.GenerationJob.ConcurrencyToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieAssemblyStatuses.Ready).SetProperty(item => item.AssetId, assetId).SetProperty(item => item.MetadataJson, metadataJson).SetProperty(item => item.QcStatus, MovieFinalAssemblyQcStatuses.Passed).SetProperty(item => item.QcResultJson, qcResultJson).SetProperty(item => item.ProgressPercent, 100).SetProperty(item => item.CompletedAt, now).SetProperty(item => item.UpdatedAt, now).SetProperty(item => item.CheckpointJson, checkpoint), cancellationToken);
    }

    public async Task MarkFailedAsync(Guid jobId, Guid token, string code, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieAssemblies.Where(item => item.GenerationJobId == jobId && item.GenerationJob!.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieAssemblyStatuses.Failed).SetProperty(item => item.LastErrorCode, code).SetProperty(item => item.QcStatus, MovieFinalAssemblyQcStatuses.Failed).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid jobId, Guid token, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieAssemblies.Where(item => item.GenerationJobId == jobId && item.GenerationJob!.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieAssemblyStatuses.Cancelled).SetProperty(item => item.LastErrorCode, GenerationJobErrorCodes.MovieAssemblyCancelled).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }
}

public sealed class MovieFinalAssemblyJobHandler(
    TaslimDbContext db,
    IMovieFinalAssemblyExecutor executor,
    MovieFinalAssemblyExecutionStore executions,
    MovieFinalAssemblyQualityControl qualityControl,
    IFileStorageService storage) : IGenerationJobHandler
{
    public bool CanHandle(string jobType) => string.Equals(jobType, GenerationJobTypes.MovieAssembly, StringComparison.OrdinalIgnoreCase);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var input = Deserialize(job.InputJson);
        var assembly = await db.MovieAssemblies.FirstOrDefaultAsync(item => item.Id == input.AssemblyId && item.MovieProjectId == input.MovieProjectId, cancellationToken)
            ?? throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblyTargetInvalid);
        if (assembly.GenerationJobId is null)
        {
            assembly.GenerationJobId = job.Id;
            assembly.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (assembly.GenerationJobId != job.Id)
        {
            throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblyTargetInvalid);
        }
        if (!executor.IsAvailable) throw new MovieFinalAssemblyUnavailableException();
        IReadOnlyDictionary<Guid, string> sourcePaths = new Dictionary<Guid, string>();
        try
        {
            await executions.TouchCheckpointAsync(assembly.Id, job.Id, job.ConcurrencyToken, "materializing_sources", 10, cancellationToken);
            // A Full Movie project owns a separate root Project record, and every generated
            // movie asset is scoped to that root project rather than to the MovieProject id.
            // Resolve the root project so approved sources can actually be materialized.
            var rootProjectId = await db.MovieProjects.AsNoTracking()
                .Where(item => item.Id == input.MovieProjectId)
                .Select(item => item.ProjectId)
                .FirstOrDefaultAsync(cancellationToken);
            sourcePaths = await MaterializeSourcesAsync(job.WorkspaceId, rootProjectId, input, cancellationToken);
            await executions.TouchCheckpointAsync(assembly.Id, job.Id, job.ConcurrencyToken, "rendering", 20, cancellationToken);
            var output = await executor.ExecuteAsync(new MovieFinalAssemblyExecutionRequest(input, sourcePaths), progress, cancellationToken);
            var qc = qualityControl.Evaluate(input, output);
            if (!qc.Passed)
            {
                await using var rejectedOutput = await output.OpenReadAsync(CancellationToken.None);
                throw new MovieFinalAssemblyQualityControlException(qc);
            }
            await executions.TouchCheckpointAsync(assembly.Id, job.Id, job.ConcurrencyToken, "qc_passed", 95, cancellationToken);
            var metadata = output.MetadataJson ?? JsonSerializer.Serialize(new { assetType = AssetTypes.Video, contentType = output.ContentType, width = output.Resolution.Width, height = output.Resolution.Height, durationSeconds = output.DurationSeconds, outputSha256 = output.OutputSha256 });
            var artifact = new GeneratedStreamFileArtifact(output.FileName, output.ContentType, output.SizeBytes, output.OpenReadAsync, metadata);
            var result = JsonSerializer.Serialize(new { assetType = AssetTypes.Video, assemblyId = input.AssemblyId, resolutionProfile = input.ResolutionProfile, width = output.Resolution.Width, height = output.Resolution.Height, durationSeconds = output.DurationSeconds, captionsMode = input.Captions.Mode, qcStatus = qc.Status });
            return new GenerationHandlerResult(result, [new GenerationHandlerOutput(GenerationJobOutputTypes.StoredFile, null, metadata, StreamArtifact: artifact, Asset: new GeneratedAssetDescriptor("Movie master", "Final movie master export.", AssetTypes.Video, metadata))], new AiUsageMetadata("local", "movie.final-assembly", null, null, null, 0m, 0m, 0, "completed", true, SafeMetadataJson: JsonSerializer.Serialize(new { contract = "movie-final-assembly.v1", qc = qc.Status })));
        }
        finally
        {
            foreach (var path in sourcePaths.Values.Distinct(StringComparer.Ordinal))
            {
                try { File.Delete(path); } catch { }
            }
            var directory = sourcePaths.Values.Select(Path.GetDirectoryName).FirstOrDefault(item => item is not null);
            try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
        }
    }

    private async Task<IReadOnlyDictionary<Guid, string>> MaterializeSourcesAsync(Guid workspaceId, Guid? rootProjectId, MovieFinalAssemblyInput input, CancellationToken cancellationToken)
    {
        var captionIds = input.Captions.AssetId is Guid captionAssetId ? new[] { captionAssetId } : Array.Empty<Guid>();
        var ids = input.Timeline.Select(item => item.AssetId).Concat(input.AudioMixInputs.Select(item => item.AssetId)).Concat(captionIds).Distinct().ToArray();
        var files = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .Where(item => ids.Contains(item.Id) && item.WorkspaceId == workspaceId && item.Status == AssetStatus.Active && (!item.ProjectId.HasValue || item.ProjectId == rootProjectId))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        if (files.Count != ids.Length) throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblySourceUnavailable);
        var directory = Path.Combine(Path.GetTempPath(), "taslim-final-assembly-inputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new Dictionary<Guid, string>();
        try
        {
            foreach (var asset in files.Values)
            {
                if (asset.StoredFile is null || asset.StoredFile.Status != StoredFileStatus.Ready) throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblySourceUnavailable);
                await using var source = await storage.OpenReadAsync(asset.StoredFile.StorageKey, cancellationToken) ?? throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblySourceUnavailable);
                var path = Path.Combine(directory, $"{asset.Id:N}{asset.StoredFile.Extension}");
                await using var destination = File.Create(path);
                await source.CopyToAsync(destination, cancellationToken);
                paths.Add(asset.Id, path);
            }
            return paths;
        }
        catch
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            throw;
        }
    }

    private static MovieFinalAssemblyInput Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<MovieFinalAssemblyInput>(json) ?? throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblyInputInvalid); }
        catch (JsonException) { throw new MovieFinalAssemblyExecutionException(GenerationJobErrorCodes.MovieAssemblyInputInvalid); }
    }
}

public sealed class MovieFinalAssemblyQualityControlException(MovieFinalAssemblyQcResult result) : Exception("The assembled master did not pass deterministic quality checks.")
{
    public MovieFinalAssemblyQcResult Result { get; } = result;
}

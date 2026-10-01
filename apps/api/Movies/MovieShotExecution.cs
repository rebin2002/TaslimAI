using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public static class MovieShotExecutionOperations
{
    public const string ImageToVideo = "image-to-video";
}

public sealed class MovieShotExecutionRequest
{
    public Guid KeyframeVersionId { get; set; }
    public string? Title { get; set; }
    public string? Label { get; set; }
    public string QualityLevel { get; set; } = MovieQualityLevels.Standard;
    public string? SourceResolution { get; set; }
    public string MasterResolution { get; set; } = MovieResolutionTiers.P1080;
    public string ProcessingPath { get; set; } = MovieResolutionPathKinds.Native;
    public int TakeCount { get; set; } = 1;
}

public sealed record MovieShotExecutionResolutionDto(
    string SourceResolution,
    string MasterResolution,
    string ProcessingPath,
    bool UpscalingRequested);

public sealed record MovieShotExecutionCandidateDto(
    Guid TakeId,
    Guid ClipId,
    Guid GenerationJobId,
    int TakeNumber,
    int TakeCount,
    string Status,
    GenerationJobDto Job);

public sealed record MovieShotExecutionResponse(
    Guid ShotId,
    Guid KeyframeVersionId,
    int DurationSeconds,
    string AspectRatio,
    MovieShotExecutionResolutionDto Resolution,
    IReadOnlyList<MovieShotExecutionCandidateDto> Candidates);

public interface IMovieShotExecutionService
{
    Task<MovieShotExecutionResponse?> QueueAsync(
        Guid userId,
        Guid shotId,
        MovieShotExecutionRequest request,
        CancellationToken cancellationToken,
        string? idempotencyKey = null);
}

/// <summary>
/// Queues image-to-video candidates from an approved keyframe without changing
/// the selected/final take pointers. Each candidate is an independent canonical
/// MovieTake tied to its own MovieClip and GenerationJob.
/// </summary>
public sealed class MovieShotExecutionService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IGenerationJobService jobs,
    IMovieVideoProvider provider,
    IMovieCharacterContinuityService continuity,
    MovieWorldContinuityProjector worldContinuity,
    IMovieGenerationCostEstimator costEstimator) : IMovieShotExecutionService
{
    private const int MaximumTakes = 8;
    private const int MaximumReferencePackageCharacters = 50_000;

    public async Task<MovieShotExecutionResponse?> QueueAsync(
        Guid userId,
        Guid shotId,
        MovieShotExecutionRequest request,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shot = await db.MovieShots
            .Include(item => item.Scene)
                .ThenInclude(item => item.MovieProject)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken)) return null;

        var keyframe = await db.MovieProductionVersions
            .Include(item => item.AssetReferences)
            .FirstOrDefaultAsync(item => item.Id == request.KeyframeVersionId && item.MovieShotId == shotId, cancellationToken);
        if (keyframe is null)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_KEYFRAME_NOT_FOUND", "The approved keyframe was not found for this shot.");
        if (keyframe.Stage != MovieProductionStages.ApprovedKeyframe || keyframe.Status != MovieProductionVersionStatuses.Approved)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_KEYFRAME_NOT_APPROVED", "Production can only start from an approved keyframe.");

        var durationSeconds = shot.DurationSeconds;
        if (durationSeconds is null or < 1 or > 3600)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_DURATION_INVALID", "The shot must have a duration between 1 second and 60 minutes before production can start.");
        if (string.IsNullOrWhiteSpace(shot.Scene.MovieProject.AspectRatio))
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_ASPECT_INVALID", "The movie aspect ratio is required before production can start.");
        var qualityLevel = request.QualityLevel?.Trim() ?? string.Empty;
        if (!MovieQualityLevels.Supported.Contains(qualityLevel))
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_QUALITY_INVALID", "Choose a supported quality level.");
        if (request.Title?.Trim().Length > 160 || request.Label?.Trim().Length > 160)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_LABEL_TOO_LARGE", "Execution title and label must be 160 characters or fewer.");
        var resolution = NormalizeResolutionPlan(request);
        var referencePackageJson = BuildReferencePackage(keyframe);
        if (referencePackageJson.Length > MaximumReferencePackageCharacters)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_REFERENCE_PACKAGE_TOO_LARGE", "The approved keyframe reference package is too large to execute safely.");

        var takeCount = request.TakeCount;
        if (takeCount is < 1 or > MaximumTakes)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_TAKE_COUNT_INVALID", $"Choose between 1 and {MaximumTakes} takes.");
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        var candidateKeys = Enumerable.Range(1, takeCount).Select(index => CandidateIdempotencyKey(normalizedIdempotencyKey, index)).ToArray();
        if (normalizedIdempotencyKey is not null)
        {
            var existing = await db.MovieTakes
                .Include(item => item.GenerationJob)
                .Where(item => item.MovieShotId == shotId && item.GenerationJob != null && candidateKeys.Contains(item.GenerationJob.IdempotencyKey!))
                .ToListAsync(cancellationToken);
            if (existing.Count == takeCount && existing.All(item => item.GenerationJob is not null))
                return ToResponse(shot, keyframe.Id, durationSeconds.Value, resolution, existing.OrderBy(item => item.VersionNumber).ToArray());
            if (existing.Count > 0)
                throw new MovieShotExecutionValidationException("SHOT_EXECUTION_IDEMPOTENCY_PARTIAL", "The requested take batch was only partially created; use the original idempotency key to retrieve it.");
        }

        var movie = shot.Scene.MovieProject;
        var estimate = await costEstimator.EstimateAsync(
            new MovieGenerationCostRequest(
                durationSeconds.Value,
                resolution.SourceResolution,
                resolution.MasterResolution,
                qualityLevel,
                resolution.ProcessingPath,
                RetryAttempts: 0,
                UpscalingRequested: false,
                UpscalePasses: 0),
            provider.Key,
            cancellationToken: cancellationToken);
        var continuitySnapshot = await continuity.BuildSnapshotForTargetAsync(movie.Id, shot.Scene.Id, shot.Id, true, cancellationToken);
        var worldSnapshot = await worldContinuity.ProjectAsync(movie.Id, shot.Scene.Id, shot.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var candidates = new List<MovieTake>(takeCount);

        for (var index = 1; index <= takeCount; index++)
        {
            var clip = new MovieClip
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movie.Id,
                MovieSceneId = shot.Scene.Id,
                MovieShotId = shot.Id,
                Status = MovieClipStatuses.Queued,
                DurationSeconds = durationSeconds.Value,
                ContinuitySnapshotJson = continuitySnapshot.SnapshotJson,
                ContinuitySnapshotId = continuitySnapshot.SnapshotId,
                ContinuitySnapshotVersion = continuitySnapshot.Version,
                ContinuitySnapshotHash = continuitySnapshot.SnapshotHash,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MovieClips.Add(clip);
            await db.SaveChangesAsync(cancellationToken);

            var input = new MovieGenerationInput(
                MovieStudioOperations.SceneClip,
                movie.Id,
                clip.Id,
                shot.Scene.Id,
                shot.Id,
                shot.Description,
                durationSeconds.Value,
                movie.AspectRatio,
                movie.Style,
                movie.Language,
                movie.AdditionalInstructions,
                continuitySnapshot.SnapshotJson,
                SceneSnapshot(shot.Scene),
                ShotSnapshot(shot),
                WorldContextJson: worldSnapshot?.ToJson(),
                SourceProductionVersionId: keyframe.Id,
                ReferencePackageJson: referencePackageJson,
                SourceResolution: resolution.SourceResolution,
                MasterResolution: resolution.MasterResolution,
                ProcessingPath: resolution.ProcessingPath,
                UpscalingRequested: false,
                TakeNumber: index,
                TakeCount: takeCount);
            var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = movie.WorkspaceId,
                ProjectId = movie.ProjectId,
                JobType = GenerationJobTypes.MovieClipGenerate,
                Title = string.IsNullOrWhiteSpace(request.Title) ? $"{movie.Title} — Take {index}" : request.Title.Trim(),
                EstimatedProviderCostUsd = estimate.MaximumAmountUsd,
                InternalCostEstimate = estimate.ToGenerationCostEstimate(),
                InternalCostEstimateJson = estimate.ToJson(),
                InputJson = JsonSerializer.Serialize(input),
            }, cancellationToken, candidateKeys[index - 1]);

            // The worker may claim immediately after CreateAsync. Detaching prevents
            // a stale tracked job from overwriting its concurrency state when the
            // clip and take links are persisted.
            db.Entry(job).State = EntityState.Detached;
            clip.GenerationJobId = job.Id;
            var versionNumber = (await db.MovieTakes.Where(item => item.MovieShotId == shot.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
            var take = new MovieTake
            {
                Id = Guid.NewGuid(),
                MovieShotId = shot.Id,
                VersionNumber = versionNumber,
                Label = string.IsNullOrWhiteSpace(request.Label) ? $"Take {versionNumber}" : $"{request.Label.Trim()} {versionNumber}",
                Status = MovieTakeStatuses.Queued,
                QualityLevel = qualityLevel,
                AutoDirectorEnabled = movie.AutoDirectorEnabled,
                MovieClipId = clip.Id,
                GenerationJobId = job.Id,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    execution = MovieShotExecutionOperations.ImageToVideo,
                    sourceProductionVersionId = keyframe.Id,
                    takeNumber = index,
                    takeCount,
                    resolution,
                    automaticUpscale = false,
                }),
                StatusChangedAt = now,
                StatusChangedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MovieTakes.Add(take);
            candidates.Add(take);
            await db.SaveChangesAsync(cancellationToken);
            // Attach only after persistence; otherwise EF treats the detached
            // job returned by the shared job service as a new graph node.
            take.GenerationJob = job;
        }
        return ToResponse(shot, keyframe.Id, durationSeconds.Value, resolution, candidates.ToArray());
    }

    private static MovieShotExecutionResponse ToResponse(
        MovieShot shot,
        Guid keyframeVersionId,
        int durationSeconds,
        MovieShotExecutionResolutionDto resolution,
        IReadOnlyList<MovieTake> takes)
    {
        var candidates = takes.OrderBy(item => item.VersionNumber).Select((take, position) =>
        {
            var job = take.GenerationJob ?? throw new InvalidOperationException("A shot execution take must reference its generation job.");
            var total = takes.Count;
            return new MovieShotExecutionCandidateDto(take.Id, take.MovieClipId!.Value, job.Id, position + 1, total, take.Status, GenerationJobContractMapper.ToMovieDto(job));
        }).ToArray();
        return new MovieShotExecutionResponse(shot.Id, keyframeVersionId, durationSeconds, shot.Scene.MovieProject.AspectRatio, resolution, candidates);
    }

    private static MovieShotExecutionResolutionDto NormalizeResolutionPlan(MovieShotExecutionRequest request)
    {
        var master = NormalizeResolution(request.MasterResolution);
        if (!MovieResolutionTiers.TryGet(master, out _))
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_MASTER_RESOLUTION_INVALID", "Choose a supported master resolution.");
        var path = request.ProcessingPath?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!MovieResolutionPathKinds.Supported.Contains(path))
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_PATH_INVALID", "Choose a supported source/master resolution path.");
        var source = string.IsNullOrWhiteSpace(request.SourceResolution) ? master : NormalizeResolution(request.SourceResolution);
        if (!MovieResolutionTiers.TryGet(source, out var sourceTier))
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_SOURCE_RESOLUTION_INVALID", "Choose a supported source resolution.");
        var masterTier = MovieResolutionTiers.All.Single(item => item.Code == master);
        if (path == MovieResolutionPathKinds.Native && source != master)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_NATIVE_RESOLUTION_MISMATCH", "Native execution requires source and master resolutions to match.");
        if (path == MovieResolutionPathKinds.SourceToMaster && sourceTier.Order >= masterTier.Order)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_SOURCE_MASTER_INVALID", "A source-to-master plan requires a source resolution below the master resolution.");
        return new MovieShotExecutionResolutionDto(source, master, path, UpscalingRequested: false);
    }

    private static string BuildReferencePackage(MovieProductionVersion keyframe)
    {
        var references = new List<MovieShotExecutionAssetReference>();
        AddReference(references, keyframe.AssetId, MovieProductionAssetRoles.Composition);
        AddReference(references, keyframe.FirstFrameAssetId, MovieProductionAssetRoles.FirstFrame);
        AddReference(references, keyframe.LastFrameAssetId, MovieProductionAssetRoles.LastFrame);
        foreach (var reference in keyframe.AssetReferences.OrderBy(item => item.Role).ThenBy(item => item.AssetId))
            AddReference(references, reference.AssetId, reference.Role);
        return JsonSerializer.Serialize(new MovieShotExecutionReferencePackage(
            1,
            keyframe.Id,
            keyframe.CompositionJson,
            keyframe.ContinuitySnapshotReferenceJson,
            keyframe.CinematographyReferenceJson,
            keyframe.FirstFrameNotes,
            keyframe.LastFrameNotes,
            references));
    }

    private static void AddReference(ICollection<MovieShotExecutionAssetReference> references, Guid? assetId, string role)
    {
        if (assetId.HasValue && !references.Any(item => item.AssetId == assetId.Value && item.Role == role))
            references.Add(new MovieShotExecutionAssetReference(assetId.Value, role));
    }

    private static string NormalizeResolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "2k" => MovieResolutionTiers.P1440,
        "4k" => MovieResolutionTiers.P2160,
        var normalized => normalized ?? string.Empty,
    };

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Length > 70)
            throw new MovieShotExecutionValidationException("SHOT_EXECUTION_IDEMPOTENCY_INVALID", "The idempotency key must be 70 characters or fewer when queuing a take batch.");
        return normalized;
    }

    private static string? CandidateIdempotencyKey(string? value, int index) => value is null ? null : $"{value}:{index}";

    private static string SceneSnapshot(MovieScene scene) => JsonSerializer.Serialize(new
    {
        scene.Id,
        scene.Sequence,
        scene.Title,
        scene.Summary,
        scene.DurationSeconds,
        scene.ContinuityNotes,
        scene.Narration,
        scene.Dialogue,
    });

    private static string ShotSnapshot(MovieShot shot) => JsonSerializer.Serialize(new
    {
        shot.Id,
        shot.Sequence,
        shot.Description,
        shot.Purpose,
        shot.Subjects,
        subjectCharacterIds = MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson),
        shot.LocationSet,
        shot.DurationSeconds,
        shot.ProductionRequirements,
        shot.ContinuityReferences,
        shot.CameraAndFraming,
        shot.CameraMotion,
        shot.CinematographyJson,
        shot.Narration,
        shot.Dialogue,
        shot.VisualContinuityNotes,
    });

    private sealed record MovieShotExecutionAssetReference(Guid AssetId, string Role);
    private sealed record MovieShotExecutionReferencePackage(
        int SchemaVersion,
        Guid ApprovedKeyframeVersionId,
        string CompositionJson,
        string? ContinuityReferenceJson,
        string? CinematographyReferenceJson,
        string? FirstFrameNotes,
        string? LastFrameNotes,
        IReadOnlyList<MovieShotExecutionAssetReference> References);
}

public sealed class MovieShotExecutionValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

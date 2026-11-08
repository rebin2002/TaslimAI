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

internal static class MovieShotExecutionIntentFingerprint
{
    public static string Compute(string inputJson) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inputJson)));
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
    public bool AllowReferenceReadinessOverride { get; set; }
    public string? ReferenceReadinessOverrideReason { get; set; }
    /// <summary>
    /// Paid generation is fail-closed: the server evaluates the shared cost
    /// guardrail before queueing and requires an explicit confirmation for an
    /// expensive or unestimable request.
    /// </summary>
    public bool ConfirmationAccepted { get; set; }
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
    IMovieGenerationCostEstimator costEstimator,
    IMovieReferenceReadinessService referenceReadiness,
    IGenerationCostGuardrailService costGuardrails) : IMovieShotExecutionService
{
    private const int MaximumTakes = 8;
    private static readonly TimeSpan AbandonedPlaceholderAge = TimeSpan.FromHours(1);
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
        var movie = shot.Scene.MovieProject;
        var providerModelKey = MovieStudioHelpers.Clean(provider.SceneClipPricingModelKey);
        var normalizedIdempotencyKey = NormalizeIdempotencyKey(idempotencyKey);
        var candidateKeys = Enumerable.Range(1, takeCount).Select(index => CandidateIdempotencyKey(normalizedIdempotencyKey, index)).ToArray();
        if (normalizedIdempotencyKey is not null)
        {
            var allCandidateKeys = Enumerable.Range(1, MaximumTakes)
                .Select(index => CandidateIdempotencyKey(normalizedIdempotencyKey, index)!)
                .ToArray();
            var existing = await db.MovieTakes
                .Include(item => item.GenerationJob)
                .Include(item => item.MovieClip)
                .Where(item => item.MovieShotId == shotId
                    && item.GenerationJob != null
                    && item.GenerationJob.CreatedByUserId == userId
                    && allCandidateKeys.Contains(item.GenerationJob.IdempotencyKey!))
                .ToListAsync(cancellationToken);
            int? StoredTakeCount(MovieTake take)
            {
                if (take.GenerationJob is null) return null;
                try { return JsonSerializer.Deserialize<MovieGenerationInput>(take.GenerationJob.InputJson)?.TakeCount; }
                catch (JsonException) { return null; }
            }
            bool MatchesCandidate(MovieTake take)
            {
                var job = take.GenerationJob;
                var clip = take.MovieClip;
                if (job is null || clip is null) return false;
                MovieGenerationInput? input;
                try { input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson); }
                catch (JsonException) { return false; }
                if (input is null || input.TakeNumber is not { } takeNumber
                    || takeNumber < 1 || takeNumber > takeCount)
                    return false;
                var expectedTitle = string.IsNullOrWhiteSpace(request.Title)
                    ? $"{movie.Title} — Take {takeNumber}"
                    : request.Title.Trim();
                var expectedLabel = string.IsNullOrWhiteSpace(request.Label)
                    ? $"Take {take.VersionNumber}"
                    : $"{request.Label.Trim()} {take.VersionNumber}";
                return string.Equals(job.IdempotencyKey, candidateKeys[takeNumber - 1], StringComparison.Ordinal)
                    && input.Operation == MovieStudioOperations.SceneClip
                    && input.MovieProjectId == movie.Id
                    && input.MovieClipId == take.MovieClipId
                    && input.MovieSceneId == shot.Scene.Id
                    && input.MovieShotId == shot.Id
                    && input.DurationSeconds == durationSeconds.Value
                    && input.AspectRatio == movie.AspectRatio
                    && input.Description == shot.Description
                    && input.SceneJson == SceneSnapshot(shot.Scene)
                    && input.ShotJson == ShotSnapshot(shot)
                    && input.ContinuityGuideJson == clip.ContinuitySnapshotJson
                    && input.ContinuitySnapshotHash == clip.ContinuitySnapshotHash
                    && input.Style == movie.Style
                    && input.Language == movie.Language
                    && input.AdditionalInstructions == movie.AdditionalInstructions
                    && input.SourceProductionVersionId == keyframe.Id
                    && input.ReferencePackageJson == referencePackageJson
                    && input.SourceResolution == resolution.SourceResolution
                    && input.TargetResolution == resolution.MasterResolution
                    && input.MasterResolution is null
                    && input.QualityTier == qualityLevel
                    && input.ProcessingPath == resolution.ProcessingPath
                    && !input.UpscalingRequested
                    && string.IsNullOrWhiteSpace(input.SourceImageUri)
                    && string.IsNullOrWhiteSpace(input.ContinuationProviderJobId)
                    && input.SelectiveRegenerationId is null
                    && string.IsNullOrWhiteSpace(input.ChangedInputsJson)
                    && string.IsNullOrWhiteSpace(input.SelectiveActionType)
                    && string.IsNullOrWhiteSpace(input.SelectiveReason)
                    && string.IsNullOrWhiteSpace(input.CharacterContinuityJson)
                    && string.IsNullOrWhiteSpace(input.FirstFrameImageUri)
                    && string.IsNullOrWhiteSpace(input.LastFrameImageUri)
                    && input.ReferenceImages is not { Count: > 0 }
                    && input.SelectedProviderKey == provider.Key
                    && string.Equals(input.SelectedProviderModelKey, providerModelKey, StringComparison.OrdinalIgnoreCase)
                    && input.TakeCount == takeCount
                    && job.Title == expectedTitle
                    && take.Label == expectedLabel;
            }

            var storedBatchCounts = existing.Select(StoredTakeCount).Distinct().ToArray();
            if (storedBatchCounts.Length == 1
                && storedBatchCounts[0] is { } storedBatchCount
                && storedBatchCount is >= 1 and <= MaximumTakes
                && existing.Count == storedBatchCount)
            {
                if (storedBatchCount == takeCount && existing.All(MatchesCandidate))
                    return ToResponse(shot, keyframe.Id, durationSeconds.Value, resolution, existing.OrderBy(item => item.VersionNumber).ToArray());
                throw new MovieShotExecutionValidationException(
                    "SHOT_EXECUTION_IDEMPOTENCY_REUSED",
                    "This idempotency key was already used for a different shot execution request.");
            }
            if (existing.Count > 0)
                throw new MovieShotExecutionValidationException("SHOT_EXECUTION_IDEMPOTENCY_PARTIAL", "The requested take batch was only partially created; use the original idempotency key to retrieve it.");

            // A generation job commits before its MovieTake link is saved. Also
            // inspect the user/key boundary directly so a job-first replay or a
            // cross-shot key reuse cannot create a second speculative clip.
            var existingJobs = await db.GenerationJobs.AsNoTracking()
                .Where(item => item.CreatedByUserId == userId && allCandidateKeys.Contains(item.IdempotencyKey!))
                .ToListAsync(cancellationToken);
            if (existingJobs.Count > 0)
            {
                int? StoredJobTakeCount(GenerationJob job)
                {
                    try { return JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)?.TakeCount; }
                    catch (JsonException) { return null; }
                }
                bool MatchesPendingRequest(GenerationJob job)
                {
                    MovieGenerationInput? input;
                    try { input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson); }
                    catch (JsonException) { return false; }
                    if (input is null || input.TakeNumber is not { } takeNumber
                        || takeNumber < 1 || takeNumber > takeCount)
                        return false;
                    var expectedTitle = string.IsNullOrWhiteSpace(request.Title)
                        ? $"{movie.Title} — Take {takeNumber}"
                        : request.Title.Trim();
                    return string.Equals(job.IdempotencyKey, candidateKeys[takeNumber - 1], StringComparison.Ordinal)
                        && input.Operation == MovieStudioOperations.SceneClip
                        && input.MovieProjectId == movie.Id
                        && input.MovieSceneId == shot.Scene.Id
                        && input.MovieShotId == shot.Id
                        && input.DurationSeconds == durationSeconds.Value
                        && input.AspectRatio == movie.AspectRatio
                        && input.Description == shot.Description
                        && input.SceneJson == SceneSnapshot(shot.Scene)
                        && input.ShotJson == ShotSnapshot(shot)
                        && input.Style == movie.Style
                        && input.Language == movie.Language
                        && input.AdditionalInstructions == movie.AdditionalInstructions
                        && input.SourceProductionVersionId == keyframe.Id
                        && input.ReferencePackageJson == referencePackageJson
                        && input.SourceResolution == resolution.SourceResolution
                        && input.TargetResolution == resolution.MasterResolution
                        && input.MasterResolution is null
                        && input.QualityTier == qualityLevel
                        && input.ProcessingPath == resolution.ProcessingPath
                        && !input.UpscalingRequested
                        && string.IsNullOrWhiteSpace(input.SourceImageUri)
                        && string.IsNullOrWhiteSpace(input.ContinuationProviderJobId)
                        && input.SelectiveRegenerationId is null
                        && string.IsNullOrWhiteSpace(input.ChangedInputsJson)
                        && string.IsNullOrWhiteSpace(input.SelectiveActionType)
                        && string.IsNullOrWhiteSpace(input.SelectiveReason)
                        && string.IsNullOrWhiteSpace(input.CharacterContinuityJson)
                        && string.IsNullOrWhiteSpace(input.FirstFrameImageUri)
                        && string.IsNullOrWhiteSpace(input.LastFrameImageUri)
                        && input.ReferenceImages is not { Count: > 0 }
                        && input.SelectedProviderKey == provider.Key
                        && string.Equals(input.SelectedProviderModelKey, providerModelKey, StringComparison.OrdinalIgnoreCase)
                        && input.TakeCount == takeCount
                        && job.Title == expectedTitle;
                }

                var storedJobBatchCounts = existingJobs.Select(StoredJobTakeCount).Distinct().ToArray();
                if (storedJobBatchCounts.Length == 1
                    && storedJobBatchCounts[0] is { } storedJobBatchCount
                    && storedJobBatchCount is >= 1 and <= MaximumTakes
                    && existingJobs.Count == storedJobBatchCount)
                {
                    var pendingBatchMatchesRequest = storedJobBatchCount == takeCount
                        && existingJobs.All(MatchesPendingRequest);
                    if (pendingBatchMatchesRequest)
                    {
                        foreach (var job in existingJobs)
                        {
                            var input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)!;
                            var canonicalClip = await db.MovieClips.AsNoTracking()
                                .SingleOrDefaultAsync(item => item.Id == input.MovieClipId, cancellationToken);
                            if (canonicalClip is null
                                || canonicalClip.MovieProjectId != movie.Id
                                || canonicalClip.MovieSceneId != shot.Scene.Id
                                || canonicalClip.MovieShotId != shot.Id
                                || canonicalClip.ContinuitySnapshotJson != input.ContinuityGuideJson
                                || canonicalClip.ContinuitySnapshotHash != input.ContinuitySnapshotHash
                                || (canonicalClip.GenerationJobId.HasValue && canonicalClip.GenerationJobId.Value != job.Id))
                            {
                                pendingBatchMatchesRequest = false;
                                break;
                            }
                        }
                    }
                    if (pendingBatchMatchesRequest)
                    {
                        if (existingJobs.All(job => job.Status is GenerationJobStatus.Pending or GenerationJobStatus.Queued or GenerationJobStatus.Running))
                            throw new MovieShotExecutionValidationException(
                                "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                                "A generation batch with this key is being linked. Retry the same request shortly.");
                        if (existingJobs.All(job => job.Status is GenerationJobStatus.Succeeded or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
                            throw new MovieShotExecutionValidationException(
                                "SHOT_EXECUTION_IDEMPOTENCY_TERMINAL",
                                "The generation batch has already reached a terminal state. Review it before submitting a new request.");
                        throw new MovieShotExecutionValidationException(
                            "SHOT_EXECUTION_IDEMPOTENCY_PARTIAL",
                            "The generation batch has mixed job states and requires reconciliation before retrying.");
                    }
                    throw new MovieShotExecutionValidationException(
                        "SHOT_EXECUTION_IDEMPOTENCY_REUSED",
                        "This idempotency key was already used for a different shot execution request.");
                }
                throw new MovieShotExecutionValidationException(
                    "SHOT_EXECUTION_IDEMPOTENCY_PARTIAL",
                    "The requested take batch was only partially created; use the original idempotency key to retrieve it.");
            }
        }

        if (await ReconcilePendingShotPlaceholdersAsync(shot.Id, candidateKeys, cancellationToken))
            throw new MovieShotExecutionValidationException(
                "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                "A candidate with this idempotency key is being prepared. Retry the same request shortly.");

        if (!provider.IsAvailable
            || !provider.SupportedOperations.Contains(MovieStudioOperations.SceneClip, StringComparer.OrdinalIgnoreCase))
            throw new MovieShotExecutionValidationException(
                "MOVIE_PROVIDER_UNAVAILABLE",
                "The selected video provider is unavailable for shot execution.");
        if (provider.SceneClipDurationRange is not { } clipDurationRange
            || !clipDurationRange.Supports(durationSeconds.Value))
            throw new MovieShotExecutionValidationException(
                "MOVIE_CLIP_DURATION_UNSUPPORTED",
                "The selected video provider cannot generate a single clip of this duration.");
        if (!provider.SupportsSceneClipCostIntent(
                resolution.SourceResolution,
                resolution.MasterResolution,
                qualityLevel,
                resolution.ProcessingPath,
                movie.AspectRatio))
            throw new MovieShotExecutionValidationException(
                "MOVIE_CLIP_EXECUTION_INTENT_UNSUPPORTED",
                "The selected video provider cannot guarantee the requested shot resolution and processing profile.");
        if (!provider.SupportsReferencePackageCostIntent)
            throw new MovieShotExecutionValidationException(
                "SHOT_EXECUTION_REFERENCE_PACKAGE_UNSUPPORTED",
                "The selected video provider has not verified priced execution from an approved keyframe reference package.");
        if (string.IsNullOrWhiteSpace(providerModelKey))
            throw new MovieShotExecutionValidationException(
                "MOVIE_PROVIDER_MODEL_UNVERIFIED",
                "The selected video adapter has not declared an exact model key for pricing this shot execution.");

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
            selectedModelKey: providerModelKey,
            cancellationToken: cancellationToken);
        var costPreflight = await costGuardrails.EvaluateAsync(
            userId,
            movie.WorkspaceId,
            movie.ProjectId,
            estimate.ToGenerationCostEstimate(),
            request.ConfirmationAccepted,
            cancellationToken);
        if (!costPreflight.CanProceed)
            throw new MovieShotExecutionValidationException(
                costPreflight.RejectionCode ?? "GENERATION_CONFIRMATION_REQUIRED",
                costPreflight.RejectionMessage ?? "Confirm the generation cost before queueing these takes.");
        await referenceReadiness.EnsureCanGenerateAsync(
            userId,
            shot.Id,
            request.AllowReferenceReadinessOverride,
            request.ReferenceReadinessOverrideReason,
            MovieReferenceReadinessOverrideSources.ShotExecution,
            cancellationToken);
        var worldSnapshot = await worldContinuity.ProjectAsync(movie.Id, shot.Scene.Id, shot.Id, cancellationToken);
        var worldContextJson = worldSnapshot?.ToJson();
        var sceneJson = SceneSnapshot(shot.Scene);
        var shotJson = ShotSnapshot(shot);
        var minimumPromptCharacters = (long)(shot.Description?.Trim().Length ?? 0)
            + (movie.Style?.Trim().Length ?? 0)
            + (movie.AdditionalInstructions?.Trim().Length ?? 0)
            + sceneJson.Trim().Length
            + shotJson.Trim().Length
            + (provider.SceneClipWorldContextInPrompt ? (worldContextJson?.Trim().Length ?? 0) : 0)
            + Math.Max(0, provider.SceneClipPromptFormattingOverheadCharacters);
        var maximumContinuityPromptCharacters = provider.SceneClipPromptMaxCharacters is { } promptLimit
            ? (int)Math.Max(0L, (long)promptLimit - minimumPromptCharacters)
            : (int?)null;
        MovieCharacterContinuitySnapshotDto continuitySnapshot;
        try
        {
            continuitySnapshot = await BuildContinuitySnapshotWithConcurrencyRetryAsync(
                movie.Id, shot.Scene.Id, shot.Id, maximumContinuityPromptCharacters, cancellationToken);
        }
        catch (MovieContinuityPromptLimitException)
        {
            throw new MovieShotExecutionValidationException(
                "MOVIE_CLIP_PROMPT_UNSUPPORTED",
                "The shot's character-continuity context exceeds this provider's prompt limit. Shorten the continuity details before queueing.");
        }
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
                sceneJson,
                shotJson,
                WorldContextJson: worldContextJson,
                ContinuitySnapshotHash: continuitySnapshot.SnapshotHash,
                SourceProductionVersionId: keyframe.Id,
                ReferencePackageJson: referencePackageJson,
                SourceResolution: resolution.SourceResolution,
                TargetResolution: resolution.MasterResolution,
                QualityTier: qualityLevel,
                ProcessingPath: resolution.ProcessingPath,
                MasterResolution: null,
                UpscalingRequested: false,
                TakeNumber: index,
                TakeCount: takeCount,
                SelectedProviderKey: provider.Key,
                SelectedProviderModelKey: providerModelKey);
            var inputJson = JsonSerializer.Serialize(input);
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
                GenerationJobId = null,
                MetadataJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    execution = MovieShotExecutionOperations.ImageToVideo,
                    sourceProductionVersionId = keyframe.Id,
                    takeNumber = index,
                    takeCount,
                    idempotencyKey = candidateKeys[index - 1],
                    queuedInputFingerprint = MovieShotExecutionIntentFingerprint.Compute(inputJson),
                    resolution,
                    automaticUpscale = false,
                }),
                StatusChangedAt = now,
                StatusChangedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            for (var allocationAttempt = 0; allocationAttempt < MaximumTakes * 2; allocationAttempt++)
            {
                db.MovieClips.Add(clip);
                db.MovieTakes.Add(take);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    break;
                }
                catch (DbUpdateException)
                {
                    db.Entry(take).State = EntityState.Detached;
                    db.Entry(clip).State = EntityState.Detached;
                    var conflictingTake = await db.MovieTakes.AsNoTracking()
                        .Where(item => item.MovieShotId == shot.Id && item.VersionNumber == versionNumber)
                        .Select(item => new { item.Id, item.MetadataJson })
                        .SingleOrDefaultAsync(cancellationToken);
                    if (conflictingTake is null) throw;
                    if (HasCandidateIdempotencyKey(conflictingTake.MetadataJson, candidateKeys[index - 1]))
                        throw new MovieShotExecutionValidationException(
                            "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                            "A candidate with this idempotency key is being prepared. Retry the same request shortly.");
                    if (allocationAttempt == MaximumTakes * 2 - 1) throw;
                    versionNumber = (await db.MovieTakes.Where(item => item.MovieShotId == shot.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
                    take.VersionNumber = versionNumber;
                    take.Label = string.IsNullOrWhiteSpace(request.Label) ? $"Take {versionNumber}" : $"{request.Label.Trim()} {versionNumber}";
                }
            }
            GenerationJob job;
            try
            {
                job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
                {
                    WorkspaceId = movie.WorkspaceId,
                    ProjectId = movie.ProjectId,
                    JobType = GenerationJobTypes.MovieClipGenerate,
                    Title = string.IsNullOrWhiteSpace(request.Title) ? $"{movie.Title} — Take {index}" : request.Title.Trim(),
                    ConfirmationAccepted = request.ConfirmationAccepted,
                    EstimatedProviderCostUsd = estimate.MaximumAmountUsd,
                    InternalCostEstimate = estimate.ToGenerationCostEstimate(),
                    InternalCostEstimateJson = estimate.ToJson(),
                    InputJson = inputJson,
                }, cancellationToken, candidateKeys[index - 1]);
            }
            catch (GenerationJobValidationException exception) when (exception.Code == "IDEMPOTENCY_KEY_REUSED")
            {
                bool committedJobReferencesClip;
                try { committedJobReferencesClip = await RemoveSpeculativeClipAsync(clip, take, candidateKeys[index - 1]); }
                catch (Exception cleanupException)
                {
                    throw new AggregateException("The idempotency collision and speculative MovieClip cleanup both failed.", exception, cleanupException);
                }
                if (committedJobReferencesClip)
                    throw new MovieShotExecutionValidationException(
                        "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                        "A generation job for this candidate already committed. Retry the same request shortly.");
                throw new MovieShotExecutionValidationException(
                    "SHOT_EXECUTION_IDEMPOTENCY_REUSED",
                    "This idempotency key was already used for a different shot execution request.");
            }
            catch (Exception creationException)
            {
                bool committedJobReferencesClip;
                try { committedJobReferencesClip = await RemoveSpeculativeClipAsync(clip, take, candidateKeys[index - 1]); }
                catch (Exception cleanupException)
                {
                    throw new AggregateException("Generation job creation and speculative MovieClip cleanup both failed.", creationException, cleanupException);
                }
                if (committedJobReferencesClip)
                    throw new MovieShotExecutionValidationException(
                        "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                        "A generation job for this candidate already committed. Retry the same request shortly.");
                var committedJobOwnsRequestKey = candidateKeys[index - 1] is { } candidateKey
                    && await db.GenerationJobs.AsNoTracking().AnyAsync(
                        item => item.CreatedByUserId == userId && item.IdempotencyKey == candidateKey,
                        CancellationToken.None);
                if (committedJobOwnsRequestKey)
                    throw new MovieShotExecutionValidationException(
                        "SHOT_EXECUTION_IDEMPOTENCY_IN_PROGRESS",
                        "A generation job with this request key committed during reconciliation. Retry the same request shortly.");
                throw;
            }

            // The worker may claim immediately after CreateAsync. Detaching prevents
            // a stale tracked job from overwriting its concurrency state when the
            // clip and take links are persisted.
            db.Entry(job).State = EntityState.Detached;
            clip.GenerationJobId = job.Id;
            take.GenerationJobId = job.Id;
            await db.SaveChangesAsync(cancellationToken);
            // Attach only after persistence; otherwise EF treats the detached
            // job returned by the shared job service as a new graph node.
            take.GenerationJob = job;
            candidates.Add(take);
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

    private async Task<bool> RemoveSpeculativeClipAsync(MovieClip clip, MovieTake take, string? candidateKey)
    {
        foreach (var entry in db.ChangeTracker.Entries<GenerationJob>()
            .Where(entry => entry.State == EntityState.Added
                && ((candidateKey is not null && entry.Entity.IdempotencyKey == candidateKey)
                    || (entry.Entity.JobType == GenerationJobTypes.MovieClipGenerate
                        && InputReferencesClip(entry.Entity.InputJson, clip.Id))))
            .ToArray())
            entry.State = EntityState.Detached;

        var committedJobReferencesClip = await db.GenerationJobs.AsNoTracking()
            .AnyAsync(job => job.JobType == GenerationJobTypes.MovieClipGenerate
                && job.InputJson.Contains(clip.Id.ToString()), CancellationToken.None);
        if (committedJobReferencesClip) return true;

        if (db.Entry(take).State != EntityState.Detached)
            db.MovieTakes.Remove(take);
        if (db.Entry(clip).State != EntityState.Detached)
            db.MovieClips.Remove(clip);
        await db.SaveChangesAsync(CancellationToken.None);
        return false;
    }

    private static bool InputReferencesClip(string inputJson, Guid clipId)
    {
        try { return JsonSerializer.Deserialize<MovieGenerationInput>(inputJson)?.MovieClipId == clipId; }
        catch (JsonException) { return false; }
    }

    private static bool HasCandidateIdempotencyKey(string? metadataJson, string? candidateKey)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || string.IsNullOrWhiteSpace(candidateKey)) return false;
        try
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            return metadata.RootElement.TryGetProperty("idempotencyKey", out var keyElement)
                && keyElement.ValueKind == JsonValueKind.String
                && string.Equals(keyElement.GetString(), candidateKey, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<MovieCharacterContinuitySnapshotDto> BuildContinuitySnapshotWithConcurrencyRetryAsync(
        Guid movieProjectId,
        Guid sceneId,
        Guid shotId,
        int? maximumPromptCharacters,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return await continuity.BuildSnapshotForTargetAsync(
                    movieProjectId,
                    sceneId,
                    shotId,
                    persist: true,
                    cancellationToken: cancellationToken,
                    maximumPromptCharacters: maximumPromptCharacters);
            }
            catch (DbUpdateException) when (attempt < maxAttempts - 1)
            {
                // Concurrent requests can read the same next snapshot version.
                // Detach only the failed snapshot insert, then re-read the now
                // committed version before retrying this short-form queue path.
                foreach (var entry in db.ChangeTracker.Entries<MovieCharacterContinuitySnapshot>()
                    .Where(entry => entry.State == EntityState.Added)
                    .ToArray())
                    entry.State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException("Continuity snapshot persistence retry was exhausted.");
    }

    private async Task<bool> ReconcilePendingShotPlaceholdersAsync(
        Guid shotId,
        IReadOnlyCollection<string?> candidateKeys,
        CancellationToken cancellationToken)
    {
        var pendingTakes = await db.MovieTakes
            .Where(item => item.MovieShotId == shotId
                && item.GenerationJobId == null
                && item.MovieClipId.HasValue
                && item.MetadataJson != null)
            .ToListAsync(cancellationToken);
        var staleBefore = DateTime.UtcNow - AbandonedPlaceholderAge;
        var hasMatchingPendingPlaceholder = false;
        var removedPlaceholder = false;

        foreach (var take in pendingTakes)
        {
            if (!IsShotExecutionPlaceholder(take.MetadataJson)) continue;
            var storedKey = ReadCandidateIdempotencyKey(take.MetadataJson);
            var keyMatchesRequest = storedKey is not null
                && candidateKeys.Any(candidateKey => string.Equals(candidateKey, storedKey, StringComparison.Ordinal));
            var clipId = take.MovieClipId;
            var clip = clipId.HasValue
                ? await db.MovieClips.SingleOrDefaultAsync(item => item.Id == clipId.Value, cancellationToken)
                : null;
            var referencedByJob = clipId.HasValue && await db.GenerationJobs.AsNoTracking()
                .AnyAsync(job => job.JobType == GenerationJobTypes.MovieClipGenerate
                    && job.InputJson.Contains(clipId.Value.ToString()), cancellationToken);

            // A crash before GenerationJobService.CreateAsync leaves only this
            // unlinked placeholder. Reclaim it after a generous lease only if
            // no persisted job references the clip and neither row is linked.
            if (take.CreatedAt < staleBefore
                && take.Status == MovieTakeStatuses.Queued
                && !referencedByJob
                && (clip is null || clip.GenerationJobId is null))
            {
                db.MovieTakes.Remove(take);
                if (clip is not null) db.MovieClips.Remove(clip);
                removedPlaceholder = true;
                continue;
            }

            if (keyMatchesRequest) hasMatchingPendingPlaceholder = true;
        }

        if (removedPlaceholder) await db.SaveChangesAsync(cancellationToken);
        return hasMatchingPendingPlaceholder;
    }

    private static bool IsShotExecutionPlaceholder(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        try
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            return metadata.RootElement.TryGetProperty("execution", out var execution)
                && execution.ValueKind == JsonValueKind.String
                && string.Equals(execution.GetString(), MovieShotExecutionOperations.ImageToVideo, StringComparison.Ordinal)
                && metadata.RootElement.TryGetProperty("queuedInputFingerprint", out var fingerprint)
                && fingerprint.ValueKind == JsonValueKind.String
                && fingerprint.GetString() is { Length: 64 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadCandidateIdempotencyKey(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            return metadata.RootElement.TryGetProperty("idempotencyKey", out var key)
                && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
            2,
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
        screenDirection = MovieScreenDirectionPlanCodec.FromJson(shot.ScreenDirectionJson),
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
        IReadOnlyList<MovieShotExecutionAssetReference> References,
        string? ScreenDirectionJson = null);
}

public sealed class MovieShotExecutionValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

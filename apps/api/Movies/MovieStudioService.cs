using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieStudioService
{
    Task<MovieStudioProjectResponse?> CreateAsync(Guid userId, MovieStudioCreateRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken);
    Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken);
    Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken);
    Task<MovieShotProductionDto?> GetShotProductionAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> CreateProductionVersionAsync(Guid userId, Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> ReviewProductionVersionAsync(Guid userId, Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken);
    Task<MovieStudioGenerationResponse?> GenerateSceneAsync(Guid userId, Guid movieProjectId, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieProviderReadinessDto> ProviderReadinessAsync();
}

public sealed class MovieStudioService(TaslimDbContext db, WorkspaceAccessService access, IGenerationJobService jobs, IMovieVideoProvider provider) : IMovieStudioService
{
    public async Task<MovieStudioProjectResponse?> CreateAsync(Guid userId, MovieStudioCreateRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var validation = MovieStudioValidation.Validate(request);
        if (validation is not null) throw new MovieStudioValidationException(validation);
        if (!await access.IsMemberAsync(userId, request.WorkspaceId, cancellationToken)) return null;
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == request.WorkspaceId, cancellationToken))
            throw new MovieStudioValidationException("The selected project is not in this workspace.");

        var now = DateTime.UtcNow;
        Guid? projectId = request.ProjectId;
        if (request.Mode == MovieProjectModes.Full && !projectId.HasValue)
        {
            var rootProject = new Project
            {
                Id = Guid.NewGuid(), WorkspaceId = request.WorkspaceId, Name = request.Title.Trim(),
                Description = request.Description.Trim(), Type = ProjectTypes.Movie, Status = ProjectStatuses.Active,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Projects.Add(rootProject);
            projectId = rootProject.Id;
        }

        var movie = new MovieProject
        {
            Id = Guid.NewGuid(), WorkspaceId = request.WorkspaceId, ProjectId = projectId, CreatedByUserId = userId,
            Mode = request.Mode, Status = MovieProjectStatuses.Draft, Title = request.Title.Trim(), Description = request.Description.Trim(),
            DurationSeconds = request.DurationSeconds, AspectRatio = request.AspectRatio, Style = request.Style.Trim(),
            Language = request.Language.ToLowerInvariant(), AdditionalInstructions = MovieStudioHelpers.Clean(request.AdditionalInstructions),
            CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide
            {
                Id = Guid.NewGuid(), VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty,
                CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty, ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty,
                SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty, ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty,
                UpdatedAt = now,
            },
        };
        db.MovieProjects.Add(movie);
        await db.SaveChangesAsync(cancellationToken);

        GenerationJobDto? job = null;
        if (request.Mode == MovieProjectModes.Quick)
        {
            var scene = new MovieScene
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movie.Id,
                Sequence = 1,
                Title = "Opening shot plan",
                Summary = movie.Description,
                DurationSeconds = movie.DurationSeconds,
                CreatedAt = now,
                UpdatedAt = now,
            };
            scene.Shots.Add(new MovieShot
            {
                Id = Guid.NewGuid(),
                Sequence = 1,
                Description = movie.Description,
                DurationSeconds = Math.Min(movie.DurationSeconds, 60),
                ProductionStage = MovieProductionStages.ShotPlan,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieScenes.Add(scene);
            await db.SaveChangesAsync(cancellationToken);
        }

        var saved = await GetAsync(userId, movie.Id, cancellationToken);
        return saved is null ? null : new MovieStudioProjectResponse(saved, job);
    }

    public async Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await Query().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : ToDto(movie);
    }

    public async Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.Include(item => item.Guide).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        movie.Guide.VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty;
        movie.Guide.CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty;
        movie.Guide.ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty;
        movie.Guide.SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty;
        movie.Guide.ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty;
        movie.Guide.UpdatedAt = movie.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, id, cancellationToken);
    }

    public async Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Summary)) throw new MovieStudioValidationException("Scene title and summary are required.");
        var now = DateTime.UtcNow;
        var scene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = id, Sequence = await db.MovieScenes.CountAsync(item => item.MovieProjectId == id, cancellationToken) + 1, Title = request.Title.Trim(), Summary = request.Summary.Trim(), DurationSeconds = request.DurationSeconds, ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.Add(scene);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(scene, [], []);
    }

    public async Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Character name and description are required.");
        var now = DateTime.UtcNow;
        var character = new MovieCharacter { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), Appearance = MovieStudioHelpers.Clean(request.Appearance), VoiceAndPerformance = MovieStudioHelpers.Clean(request.VoiceAndPerformance), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieCharacters.Add(character);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(character);
    }

    public async Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Location name and description are required.");
        var now = DateTime.UtcNow;
        var location = new MovieLocation { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieLocations.Add(location);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(location);
    }

    public async Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken)
    {
        var scene = await db.MovieScenes.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await access.IsMemberAsync(userId, scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Shot description is required.");
        var now = DateTime.UtcNow;
        var shot = new MovieShot { Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = await db.MovieShots.CountAsync(item => item.MovieSceneId == sceneId, cancellationToken) + 1, Description = request.Description.Trim(), CameraAndFraming = MovieStudioHelpers.Clean(request.CameraAndFraming), CameraMotion = MovieStudioHelpers.Clean(request.CameraMotion), DurationSeconds = request.DurationSeconds, Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), CreatedAt = now, UpdatedAt = now };
        db.MovieShots.Add(shot);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shot, []);
    }

    public async Task<MovieShotProductionDto?> GetShotProductionAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await ProductionQuery().FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await access.IsMemberAsync(userId, shot.Scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        return ToProductionDto(shot);
    }

    public async Task<MovieProductionVersionDto?> CreateProductionVersionAsync(Guid userId, Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.ProductionVersions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await access.IsMemberAsync(userId, shot.Scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.CompositionJson) || request.CompositionJson.Length > 20_000 || !MovieProductionWorkflow.IsJsonObject(request.CompositionJson))
            throw new MovieProductionValidationException("PRODUCTION_COMPOSITION_INVALID", "CompositionJson must be a JSON object of 20,000 characters or fewer.");
        if (request.RegenerationMetadataJson is { Length: > 8_000 } || request.StageProvenanceJson is { Length: > 20_000 })
            throw new MovieProductionValidationException("PRODUCTION_METADATA_TOO_LARGE", "Production metadata exceeds the supported limit.");
        if (request.RegenerationMetadataJson is not null && !MovieProductionWorkflow.IsJsonObject(request.RegenerationMetadataJson))
            throw new MovieProductionValidationException("PRODUCTION_REGENERATION_METADATA_INVALID", "RegenerationMetadataJson must be a JSON object.");
        if (request.StageProvenanceJson is not null && !MovieProductionWorkflow.IsJsonObject(request.StageProvenanceJson))
            throw new MovieProductionValidationException("PRODUCTION_PROVENANCE_INVALID", "StageProvenanceJson must be a JSON object.");

        MovieProductionVersion? source = null;
        if (request.SourceVersionId.HasValue)
        {
            source = await db.MovieProductionVersions.FirstOrDefaultAsync(item => item.Id == request.SourceVersionId && item.MovieShotId == shotId, cancellationToken);
            if (source is null) throw new MovieProductionValidationException("PRODUCTION_SOURCE_NOT_FOUND", "The source version does not belong to this shot.");
        }
        var stage = request.Stage.Trim();
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(stage, source);
        if (workflowError is not null) throw new MovieProductionValidationException("PRODUCTION_STAGE_INVALID", workflowError);
        var assetIds = new Dictionary<Guid, string>();
        AddAsset(assetIds, request.AssetId, MovieProductionAssetRoles.Composition);
        AddAsset(assetIds, request.FirstFrameAssetId, MovieProductionAssetRoles.FirstFrame);
        AddAsset(assetIds, request.LastFrameAssetId, MovieProductionAssetRoles.LastFrame);
        foreach (var reference in request.AssetReferences ?? [])
        {
            if (!MovieProductionAssetRoles.Reference.Equals(reference.Role, StringComparison.OrdinalIgnoreCase)
                && !MovieProductionAssetRoles.Composition.Equals(reference.Role, StringComparison.OrdinalIgnoreCase)
                && !MovieProductionAssetRoles.FirstFrame.Equals(reference.Role, StringComparison.OrdinalIgnoreCase)
                && !MovieProductionAssetRoles.LastFrame.Equals(reference.Role, StringComparison.OrdinalIgnoreCase)
                && !MovieProductionAssetRoles.Output.Equals(reference.Role, StringComparison.OrdinalIgnoreCase))
                throw new MovieProductionValidationException("PRODUCTION_ASSET_ROLE_INVALID", "The asset reference role is not supported.");
            AddAsset(assetIds, reference.AssetId, reference.Role.Trim().ToLowerInvariant());
        }
        if (assetIds.Count > 0 && await db.Assets.CountAsync(item => item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId && assetIds.Keys.Contains(item.Id), cancellationToken) != assetIds.Count)
            throw new MovieProductionValidationException("PRODUCTION_ASSET_NOT_FOUND", "Every referenced Asset must belong to the movie workspace.");
        if (request.GenerationJobId.HasValue)
        {
            var job = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.GenerationJobId && item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId, cancellationToken);
            if (job is null) throw new MovieProductionValidationException("PRODUCTION_JOB_NOT_FOUND", "The linked GenerationJob is not available in the movie workspace.");
        }

        var now = DateTime.UtcNow;
        var version = new MovieProductionVersion
        {
            Id = Guid.NewGuid(), MovieShotId = shotId, VersionNumber = shot.ProductionVersions.Count == 0 ? 1 : shot.ProductionVersions.Max(item => item.VersionNumber) + 1,
            Stage = stage, Status = MovieProductionVersionStatuses.PendingApproval, Label = CleanBounded(request.Label, 160), CompositionJson = request.CompositionJson.Trim(),
            RegenerationMetadataJson = request.RegenerationMetadataJson?.Trim(), StageProvenanceJson = request.StageProvenanceJson?.Trim(), SourceVersionId = source?.Id,
            GenerationJobId = request.GenerationJobId, AssetId = request.AssetId, FirstFrameAssetId = request.FirstFrameAssetId, LastFrameAssetId = request.LastFrameAssetId,
            FirstFrameNotes = CleanBounded(request.FirstFrameNotes, 2_000), LastFrameNotes = CleanBounded(request.LastFrameNotes, 2_000), CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        foreach (var asset in assetIds) version.AssetReferences.Add(new MovieProductionVersionAsset { MovieProductionVersionId = version.Id, AssetId = asset.Key, Role = asset.Value, CreatedAt = now });
        var provenance = new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(), MovieShotId = shotId, MovieProductionVersionId = version.Id, FromStage = shot.ProductionStage, ToStage = stage,
            EventType = "created", SourceVersionId = source?.Id, GenerationJobId = request.GenerationJobId, ActorUserId = userId, CreatedAt = now,
            MetadataJson = request.StageProvenanceJson,
        };
        db.MovieProductionVersions.Add(version);
        db.MovieProductionStageTransitions.Add(provenance);
        if (stage == MovieProductionStages.StoryboardCandidate && shot.ProductionStage == MovieProductionStages.ShotPlan)
            shot.ProductionStage = stage;
        shot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(version);
    }

    public async Task<MovieProductionVersionDto?> ReviewProductionVersionAsync(Guid userId, Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken)
    {
        var version = await db.MovieProductionVersions
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.AssetReferences)
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || !await access.IsMemberAsync(userId, version.MovieShot.Scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        var reviewError = MovieProductionWorkflow.ValidateReview(version.Stage, version.Status);
        if (reviewError is not null) throw new MovieProductionValidationException("PRODUCTION_REVIEW_INVALID", reviewError);
        if (request.Reason is { Length: > 2_000 } || request.MetadataJson is { Length: > 8_000 })
            throw new MovieProductionValidationException("PRODUCTION_REVIEW_METADATA_TOO_LARGE", "Review metadata exceeds the supported limit.");
        if (request.MetadataJson is not null && !MovieProductionWorkflow.IsJsonObject(request.MetadataJson))
            throw new MovieProductionValidationException("PRODUCTION_REVIEW_METADATA_INVALID", "Review metadata must be a JSON object.");
        var now = DateTime.UtcNow;
        var fromStage = version.Stage;
        if (!request.Approve)
        {
            version.Status = MovieProductionVersionStatuses.Rejected;
            version.RejectionReason = CleanBounded(request.Reason, 2_000);
        }
        else
        {
            var approval = MovieProductionWorkflow.ApprovalResult(version.Stage)!.Value;
            version.Stage = approval.NextStage;
            version.Status = approval.Status;
            version.RejectionReason = null;
            if (StageRank(version.Stage) > StageRank(version.MovieShot.ProductionStage)) version.MovieShot.ProductionStage = version.Stage;
        }
        version.ReviewedByUserId = userId;
        version.ReviewedAt = now;
        version.UpdatedAt = now;
        db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(), MovieShotId = version.MovieShotId, MovieProductionVersionId = version.Id, FromStage = fromStage,
            ToStage = version.Stage, EventType = request.Approve ? "approved" : "rejected", Reason = CleanBounded(request.Reason, 2_000), MetadataJson = request.MetadataJson,
            SourceVersionId = version.SourceVersionId, GenerationJobId = version.GenerationJobId, ActorUserId = userId, CreatedAt = now,
        });
        version.MovieShot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(version);
    }

    public async Task<MovieStudioGenerationResponse?> GenerateSceneAsync(Guid userId, Guid movieProjectId, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var scene = await db.MovieScenes.Include(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == sceneId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (scene is null || !await access.IsMemberAsync(userId, scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        return await QueueClipAsync(userId, scene.MovieProject, scene, null, request, cancellationToken, idempotencyKey);
    }

    public async Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await access.IsMemberAsync(userId, shot.Scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        return await QueueClipAsync(userId, shot.Scene.MovieProject, shot.Scene, shot, request, cancellationToken, idempotencyKey);
    }

    public Task<MovieProviderReadinessDto> ProviderReadinessAsync() => Task.FromResult(new MovieProviderReadinessDto(provider.IsAvailable, provider.SupportedOperations.ToArray()));

    private async Task<MovieStudioGenerationResponse> QueueClipAsync(Guid userId, MovieProject movie, MovieScene scene, MovieShot? shot, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey)
    {
        var description = shot?.Description ?? scene.Summary;
        var durationSeconds = Math.Clamp(shot?.DurationSeconds ?? scene.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), 1, 3600);
        var now = DateTime.UtcNow;
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            MovieSceneId = scene.Id,
            MovieShotId = shot?.Id,
            Status = MovieClipStatuses.Queued,
            DurationSeconds = durationSeconds,
            ContinuitySnapshotJson = ContinuitySnapshot(movie.Guide),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieClips.Add(clip);
        await db.SaveChangesAsync(cancellationToken);
        var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
        {
            WorkspaceId = movie.WorkspaceId,
            ProjectId = movie.ProjectId,
            JobType = shot is null ? GenerationJobTypes.MovieClipGenerate : GenerationJobTypes.MovieClipGenerate,
            Title = string.IsNullOrWhiteSpace(request.Title) ? movie.Title : request.Title.Trim(),
            InputJson = JsonSerializer.Serialize(new MovieGenerationInput(
                MovieStudioOperations.SceneClip,
                movie.Id,
                clip.Id,
                scene.Id,
                shot?.Id,
                description,
                durationSeconds,
                movie.AspectRatio,
                movie.Style,
                movie.Language,
                movie.AdditionalInstructions,
                ContinuitySnapshot(movie.Guide),
                SceneSnapshot(scene),
                shot is null ? null : ShotSnapshot(shot))),
        }, cancellationToken, idempotencyKey);
        clip.GenerationJobId = job.Id;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieStudioGenerationResponse(await GetAsync(userId, movie.Id, cancellationToken) ?? throw new InvalidOperationException("Movie project disappeared."), GenerationJobContractMapper.ToDto(job), clip.Id);
    }

    private IQueryable<MovieProject> Query() => db.MovieProjects.AsNoTracking().Include(item => item.Guide).Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.Clips).Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.ProductionVersions).ThenInclude(version => version.AssetReferences).Include(item => item.Scenes).ThenInclude(scene => scene.Clips).Include(item => item.Clips).Include(item => item.Characters).Include(item => item.Locations).Include(item => item.Assemblies);
    private IQueryable<MovieShot> ProductionQuery() => db.MovieShots.AsNoTracking().Include(item => item.Scene).ThenInclude(scene => scene.MovieProject).Include(item => item.ProductionVersions).ThenInclude(version => version.AssetReferences).Include(item => item.ProductionTransitions);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray(), scene.Clips.Where(clip => clip.MovieShotId is null).Select(ToDto).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Clips.Where(clip => clip.MovieSceneId is null).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray());
    private static string ContinuitySnapshot(MovieContinuityGuide guide) => JsonSerializer.Serialize(new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.UpdatedAt });
    private static string SceneSnapshot(MovieScene scene) => JsonSerializer.Serialize(new { scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue });
    private static string ShotSnapshot(MovieShot shot) => JsonSerializer.Serialize(new { shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes });
    private static MovieSceneDto ToDto(MovieScene scene, IReadOnlyList<MovieShotDto> shots, IReadOnlyList<MovieClipDto> clips) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue, shots, clips);
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips) => new(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, shot.ProductionStage, clips, shot.ProductionVersions.OrderByDescending(item => item.VersionNumber).Select(ToDto).ToArray());
    private static MovieCharacterDto ToDto(MovieCharacter character) => new(character.Id, character.Name, character.Description, character.Appearance, character.VoiceAndPerformance, character.ContinuityNotes, character.ReferenceAssetId);
    private static MovieLocationDto ToDto(MovieLocation location) => new(location.Id, location.Name, location.Description, location.VisualContinuityNotes, location.ReferenceAssetId);
    private static MovieClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieSceneId, clip.MovieShotId, clip.GenerationJobId, clip.AssetId, clip.Status, clip.DurationSeconds, clip.MetadataJson, clip.ContinuitySnapshotJson);
    private static MovieAssemblyDto ToDto(MovieAssembly assembly) => new(assembly.Id, assembly.GenerationJobId, assembly.AssetId, assembly.Status, assembly.OutputFormat, assembly.MetadataJson, assembly.CreatedAt, assembly.CompletedAt);
    private static MovieShotProductionDto ToProductionDto(MovieShot shot) => new(shot.Id, shot.ProductionStage, shot.ProductionVersions.OrderByDescending(item => item.VersionNumber).Select(ToDto).ToArray(), shot.ProductionTransitions.OrderBy(item => item.CreatedAt).Select(item => new MovieProductionStageTransitionDto(item.Id, item.MovieShotId, item.MovieProductionVersionId, item.FromStage, item.ToStage, item.EventType, item.Reason, item.MetadataJson, item.SourceVersionId, item.GenerationJobId, item.ActorUserId, item.CreatedAt)).ToArray());
    private static MovieProductionVersionDto ToDto(MovieProductionVersion version) => new(version.Id, version.MovieShotId, version.VersionNumber, version.Stage, version.Status, version.Label, version.CompositionJson, version.RegenerationMetadataJson, version.StageProvenanceJson, version.SourceVersionId, version.GenerationJobId, version.AssetId, version.FirstFrameAssetId, version.LastFrameAssetId, version.FirstFrameNotes, version.LastFrameNotes, version.RejectionReason, version.CreatedAt, version.UpdatedAt, version.ReviewedAt, version.AssetReferences.OrderBy(item => item.Role).Select(item => new MovieProductionAssetReferenceDto(item.AssetId, item.Role)).ToArray());
    private static void AddAsset(IDictionary<Guid, string> assets, Guid? assetId, string role)
    {
        if (assetId.HasValue) assets[assetId.Value] = role;
    }
    private static string? CleanBounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : throw new MovieProductionValidationException("PRODUCTION_FIELD_TOO_LONG", $"A production field exceeds {maxLength} characters.");
    }
    private static int StageRank(string stage) => stage switch
    {
        MovieProductionStages.ShotPlan => 0,
        MovieProductionStages.StoryboardCandidate => 1,
        MovieProductionStages.ApprovedStoryboard => 2,
        MovieProductionStages.ProductionKeyframe => 3,
        MovieProductionStages.ApprovedKeyframe => 4,
        MovieProductionStages.MotionPreview => 5,
        MovieProductionStages.ProductionRender => 6,
        MovieProductionStages.SelectedFinalTake => 7,
        _ => -1,
    };
}

public sealed class MovieStudioValidationException(string message) : Exception(message);

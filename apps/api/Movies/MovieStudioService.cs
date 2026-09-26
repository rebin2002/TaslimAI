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
    Task<MovieSetDto?> AddSetAsync(Guid userId, Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken);
    Task<MovieSetVariationDto?> AddSetVariationAsync(Guid userId, Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken);
    Task<MoviePropDto?> AddPropAsync(Guid userId, Guid id, MovieStudioPropRequest request, CancellationToken cancellationToken);
    Task<MovieWorldReferenceDto?> AddWorldReferenceAsync(Guid userId, Guid id, MovieStudioWorldReferenceRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityFactDto?> AddContinuityFactAsync(Guid userId, Guid id, MovieStudioContinuityFactRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid id, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieWorldUsageDto?> AddWorldUsageAsync(Guid userId, Guid sceneId, MovieStudioWorldUsageRequest request, CancellationToken cancellationToken);
    Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken);
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
            var clip = new MovieClip
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movie.Id,
                Status = MovieClipStatuses.Queued,
                ContinuitySnapshotJson = ContinuitySnapshot(movie.Guide),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MovieClips.Add(clip);
            await db.SaveChangesAsync(cancellationToken);
            var createdJob = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
            {
                WorkspaceId = request.WorkspaceId, ProjectId = projectId, JobType = GenerationJobTypes.MovieQuickGenerate,
                Title = request.Title.Trim(), InputJson = JsonSerializer.Serialize(new MovieGenerationInput(
                    MovieStudioOperations.QuickMovie, movie.Id, clip.Id, null, null, movie.Description, movie.DurationSeconds, movie.AspectRatio,
                    movie.Style, movie.Language, movie.AdditionalInstructions, ContinuitySnapshot(movie.Guide), null, null,
                    WorldContextJson: await WorldContextSnapshotAsync(movie.Id, null, null, cancellationToken))),
            }, cancellationToken, idempotencyKey);
            clip.GenerationJobId = createdJob.Id;
            await db.SaveChangesAsync(cancellationToken);
            job = GenerationJobContractMapper.ToDto(createdJob);
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
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, movie.WorkspaceId, cancellationToken);
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
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, movie.WorkspaceId, cancellationToken);
        var now = DateTime.UtcNow;
        var location = new MovieLocation { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieLocations.Add(location);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(location);
    }

    public async Task<MovieSetDto?> AddSetAsync(Guid userId, Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.Name, request.Description, "Set name and description are required.");
        if (request.MovieLocationId.HasValue && !await db.MovieLocations.AnyAsync(item => item.Id == request.MovieLocationId && item.MovieProjectId == id, cancellationToken))
            throw new MovieStudioValidationException("The set location must belong to this movie project.");
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, movie.WorkspaceId, cancellationToken);
        var now = DateTime.UtcNow;
        var item = new MovieSet { Id = Guid.NewGuid(), MovieProjectId = id, MovieLocationId = request.MovieLocationId, Name = request.Name.Trim(), Description = request.Description.Trim(), EnvironmentType = string.IsNullOrWhiteSpace(request.EnvironmentType) ? "practical" : request.EnvironmentType.Trim(), VisualDescription = MovieStudioHelpers.Clean(request.VisualDescription), TimeOfDay = MovieStudioHelpers.Clean(request.TimeOfDay), Weather = MovieStudioHelpers.Clean(request.Weather), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieSets.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieSetVariationDto?> AddSetVariationAsync(Guid userId, Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken)
    {
        var set = await db.MovieSets.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == setId, cancellationToken);
        if (set is null || !await access.IsMemberAsync(userId, set.MovieProject.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name)) throw new MovieStudioValidationException("Set variation name is required.");
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, set.MovieProject.WorkspaceId, cancellationToken);
        if (request.IsDefault) await db.MovieSetVariations.Where(item => item.MovieSetId == setId && item.IsDefault).ExecuteUpdateAsync(update => update.SetProperty(item => item.IsDefault, false), cancellationToken);
        var now = DateTime.UtcNow;
        var item = new MovieSetVariation { Id = Guid.NewGuid(), MovieSetId = setId, Name = request.Name.Trim(), VisualDescription = MovieStudioHelpers.Clean(request.VisualDescription), TimeOfDay = MovieStudioHelpers.Clean(request.TimeOfDay), Weather = MovieStudioHelpers.Clean(request.Weather), Lighting = MovieStudioHelpers.Clean(request.Lighting), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, IsDefault = request.IsDefault, CreatedAt = now, UpdatedAt = now };
        db.MovieSetVariations.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MoviePropDto?> AddPropAsync(Guid userId, Guid id, MovieStudioPropRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.Name, request.Description, "Prop name and description are required.");
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, movie.WorkspaceId, cancellationToken);
        var now = DateTime.UtcNow;
        var item = new MovieProp { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), Category = MovieStudioHelpers.Clean(request.Category), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieProps.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieWorldReferenceDto?> AddWorldReferenceAsync(Guid userId, Guid id, MovieStudioWorldReferenceRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.Name, request.Kind, "Reference name and kind are required.");
        await EnsureAssetInWorkspaceAsync(request.AssetId, movie.WorkspaceId, cancellationToken);
        var now = DateTime.UtcNow;
        var item = new MovieWorldReference { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Kind = request.Kind.Trim().ToLowerInvariant(), Description = MovieStudioHelpers.Clean(request.Description), TagsJson = MovieStudioHelpers.Clean(request.TagsJson), AssetId = request.AssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieWorldReferences.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieContinuityFactDto?> AddContinuityFactAsync(Guid userId, Guid id, MovieStudioContinuityFactRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.ScopeType, request.FactKey, request.FactValue, "Continuity fact scope, key, and value are required.");
        var scopeType = request.ScopeType.Trim().ToLowerInvariant();
        if (scopeType == MovieWorldScopes.Project && request.ScopeId.HasValue) throw new MovieStudioValidationException("Project facts cannot specify a scope id.");
        if (scopeType == MovieWorldScopes.Scene && (!request.ScopeId.HasValue || !await db.MovieScenes.AnyAsync(item => item.Id == request.ScopeId && item.MovieProjectId == id, cancellationToken))) throw new MovieStudioValidationException("The fact scene must belong to this movie project.");
        if (scopeType == MovieWorldScopes.Shot && (!request.ScopeId.HasValue || !await db.MovieShots.AnyAsync(item => item.Id == request.ScopeId && item.Scene.MovieProjectId == id, cancellationToken))) throw new MovieStudioValidationException("The fact shot must belong to this movie project.");
        if (scopeType is not (MovieWorldScopes.Project or MovieWorldScopes.Scene or MovieWorldScopes.Shot)) throw new MovieStudioValidationException("Continuity fact scope must be project, scene, or shot.");
        var now = DateTime.UtcNow;
        var item = new MovieContinuityFact { Id = Guid.NewGuid(), MovieProjectId = id, ScopeType = scopeType, ScopeId = request.ScopeId, FactKey = request.FactKey.Trim(), FactValue = request.FactValue.Trim(), Notes = MovieStudioHelpers.Clean(request.Notes), CreatedAt = now, UpdatedAt = now };
        db.MovieContinuityFacts.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid id, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.EntityType, request.FieldName, request.LockedValue, "Continuity lock entity, field, and value are required.");
        var entityType = request.EntityType.Trim().ToLowerInvariant();
        if (!await LockEntityBelongsToProjectAsync(entityType, request.EntityId, id, cancellationToken)) throw new MovieStudioValidationException("The continuity lock entity must belong to this movie project.");
        var strength = string.IsNullOrWhiteSpace(request.Strength) ? MovieContinuityLockStrengths.Hard : request.Strength.Trim().ToLowerInvariant();
        if (strength is not (MovieContinuityLockStrengths.Soft or MovieContinuityLockStrengths.Hard)) throw new MovieStudioValidationException("Continuity lock strength must be soft or hard.");
        var now = DateTime.UtcNow;
        var item = new MovieContinuityLock { Id = Guid.NewGuid(), MovieProjectId = id, EntityType = entityType, EntityId = request.EntityId, FieldName = request.FieldName.Trim(), LockedValue = request.LockedValue.Trim(), Strength = strength, Reason = MovieStudioHelpers.Clean(request.Reason), CreatedByUserId = userId, CreatedAt = now };
        db.MovieContinuityLocks.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieWorldUsageDto?> AddWorldUsageAsync(Guid userId, Guid sceneId, MovieStudioWorldUsageRequest request, CancellationToken cancellationToken)
    {
        var scene = await db.MovieScenes.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await access.IsMemberAsync(userId, scene.MovieProject.WorkspaceId, cancellationToken)) return null;
        var entityType = request.EntityType.Trim().ToLowerInvariant();
        if (!MovieWorldEntityTypes.Supported.Contains(entityType)) throw new MovieStudioValidationException("World usage entity type must be location, set, or prop.");
        if (!await WorldEntityBelongsToProjectAsync(entityType, request.EntityId, scene.MovieProjectId, cancellationToken)) throw new MovieStudioValidationException("The reusable world entity must belong to this movie project.");
        if (request.MovieShotId.HasValue && !await db.MovieShots.AnyAsync(item => item.Id == request.MovieShotId && item.MovieSceneId == sceneId, cancellationToken)) throw new MovieStudioValidationException("The world usage shot must belong to this scene.");
        var existing = await db.MovieWorldUsages.FirstOrDefaultAsync(item => item.MovieSceneId == sceneId && item.MovieShotId == request.MovieShotId && item.EntityType == entityType && item.EntityId == request.EntityId, cancellationToken);
        if (existing is not null) return ToDto(existing);
        var item = new MovieWorldUsage { Id = Guid.NewGuid(), MovieProjectId = scene.MovieProjectId, MovieSceneId = sceneId, MovieShotId = request.MovieShotId, EntityType = entityType, EntityId = request.EntityId, Role = MovieStudioHelpers.Clean(request.Role), CreatedAt = DateTime.UtcNow };
        db.MovieWorldUsages.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
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
                shot is null ? null : ShotSnapshot(shot),
                WorldContextJson: await WorldContextSnapshotAsync(movie.Id, scene.Id, shot?.Id, cancellationToken))),
        }, cancellationToken, idempotencyKey);
        clip.GenerationJobId = job.Id;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieStudioGenerationResponse(await GetAsync(userId, movie.Id, cancellationToken) ?? throw new InvalidOperationException("Movie project disappeared."), GenerationJobContractMapper.ToDto(job), clip.Id);
    }

    private IQueryable<MovieProject> Query() => db.MovieProjects.AsNoTracking()
        .Include(item => item.Guide)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.Clips)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Clips)
        .Include(item => item.Clips)
        .Include(item => item.Characters)
        .Include(item => item.Locations)
        .Include(item => item.Sets).ThenInclude(item => item.Variations)
        .Include(item => item.Props)
        .Include(item => item.WorldReferences)
        .Include(item => item.WorldUsages)
        .Include(item => item.ContinuityFacts)
        .Include(item => item.ContinuityLocks)
        .Include(item => item.Assemblies);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray(), scene.Clips.Where(clip => clip.MovieShotId is null).Select(ToDto).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Clips.Where(clip => clip.MovieSceneId is null).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray(), MovieWorld(movie));

    private static MovieWorldDto MovieWorld(MovieProject movie) => new(
        movie.Locations.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.Sets.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.Props.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.WorldReferences.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.WorldUsages.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.ContinuityFacts.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.ContinuityLocks.OrderByDescending(item => item.CreatedAt).Select(ToDto).ToArray());

    private async Task<MovieProject?> GetAuthorizedMovieAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : movie;
    }

    private async Task EnsureAssetInWorkspaceAsync(Guid? assetId, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (assetId.HasValue && !await db.Assets.AnyAsync(item => item.Id == assetId && item.WorkspaceId == workspaceId, cancellationToken))
            throw new MovieStudioValidationException("The reference asset must belong to this workspace.");
    }

    private async Task<bool> WorldEntityBelongsToProjectAsync(string entityType, Guid entityId, Guid movieProjectId, CancellationToken cancellationToken) => entityType.ToLowerInvariant() switch
    {
        MovieWorldEntityTypes.Location => await db.MovieLocations.AnyAsync(item => item.Id == entityId && item.MovieProjectId == movieProjectId, cancellationToken),
        MovieWorldEntityTypes.Set => await db.MovieSets.AnyAsync(item => item.Id == entityId && item.MovieProjectId == movieProjectId, cancellationToken),
        MovieWorldEntityTypes.Prop => await db.MovieProps.AnyAsync(item => item.Id == entityId && item.MovieProjectId == movieProjectId, cancellationToken),
        _ => false,
    };

    private async Task<bool> LockEntityBelongsToProjectAsync(string entityType, Guid? entityId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (entityType == MovieWorldScopes.Project) return !entityId.HasValue || entityId == movieProjectId;
        if (!entityId.HasValue) return false;
        if (entityType == MovieWorldScopes.Scene) return await db.MovieScenes.AnyAsync(item => item.Id == entityId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (entityType == MovieWorldScopes.Shot) return await db.MovieShots.AnyAsync(item => item.Id == entityId && item.Scene.MovieProjectId == movieProjectId, cancellationToken);
        return MovieWorldEntityTypes.Supported.Contains(entityType) && await WorldEntityBelongsToProjectAsync(entityType, entityId.Value, movieProjectId, cancellationToken);
    }

    private static void ValidateRequired(params string[] values)
    {
        if (values.Any(string.IsNullOrWhiteSpace)) throw new MovieStudioValidationException("Required Movie World fields are missing.");
    }

    private static void ValidateRequired(string first, string second, string message)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) throw new MovieStudioValidationException(message);
    }

    private static void ValidateRequired(string first, string second, string third, string message)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) || string.IsNullOrWhiteSpace(third)) throw new MovieStudioValidationException(message);
    }
    private static string ContinuitySnapshot(MovieContinuityGuide guide) => JsonSerializer.Serialize(new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.UpdatedAt });

    private async Task<string> WorldContextSnapshotAsync(Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken)
    {
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (!sceneId.HasValue || item.MovieSceneId == sceneId) && (!shotId.HasValue || item.MovieShotId == null || item.MovieShotId == shotId)).ToArrayAsync(cancellationToken);
        var locationIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Location).Select(item => item.EntityId).ToArray();
        var setIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Set).Select(item => item.EntityId).ToArray();
        var propIds = usages.Where(item => item.EntityType == MovieWorldEntityTypes.Prop).Select(item => item.EntityId).ToArray();
        var facts = await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (!item.ScopeId.HasValue || item.ScopeId == sceneId || item.ScopeId == shotId)).Select(item => new { item.Id, item.ScopeType, item.ScopeId, item.FactKey, item.FactValue, item.Notes, item.UpdatedAt }).ToArrayAsync(cancellationToken);
        var locks = await db.MovieContinuityLocks.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.ReleasedAt == null).Select(item => new { item.Id, item.EntityType, item.EntityId, item.FieldName, item.LockedValue, item.Strength, item.Reason, item.CreatedAt }).ToArrayAsync(cancellationToken);
        var locations = await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (locationIds.Length == 0 || locationIds.Contains(item.Id))).Select(item => new { item.Id, item.Name, item.Description, item.VisualContinuityNotes, item.ReferenceAssetId }).ToArrayAsync(cancellationToken);
        var sets = await db.MovieSets.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (setIds.Length == 0 || setIds.Contains(item.Id))).Select(item => new { item.Id, item.MovieLocationId, item.Name, item.Description, item.EnvironmentType, item.VisualDescription, item.TimeOfDay, item.Weather, item.ContinuityNotes, item.ReferenceAssetId, Variations = item.Variations.Select(variation => new { variation.Id, variation.Name, variation.VisualDescription, variation.TimeOfDay, variation.Weather, variation.Lighting, variation.ContinuityNotes, variation.ReferenceAssetId, variation.IsDefault }) }).ToArrayAsync(cancellationToken);
        var props = await db.MovieProps.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && (propIds.Length == 0 || propIds.Contains(item.Id))).Select(item => new { item.Id, item.Name, item.Description, item.Category, item.ContinuityNotes, item.ReferenceAssetId }).ToArrayAsync(cancellationToken);
        var references = await db.MovieWorldReferences.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).Select(item => new { item.Id, item.Name, item.Kind, item.Description, item.TagsJson, item.AssetId }).ToArrayAsync(cancellationToken);
        var usageSnapshots = usages.Select(item => new { item.Id, item.MovieSceneId, item.MovieShotId, item.EntityType, item.EntityId, item.Role }).ToArray();
        return JsonSerializer.Serialize(new { locations, sets, props, references, usages = usageSnapshots, facts, locks });
    }
    private static string SceneSnapshot(MovieScene scene) => JsonSerializer.Serialize(new { scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue });
    private static string ShotSnapshot(MovieShot shot) => JsonSerializer.Serialize(new { shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes });
    private static MovieSceneDto ToDto(MovieScene scene, IReadOnlyList<MovieShotDto> shots, IReadOnlyList<MovieClipDto> clips) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue, shots, clips);
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips) => new(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, clips);
    private static MovieCharacterDto ToDto(MovieCharacter character) => new(character.Id, character.Name, character.Description, character.Appearance, character.VoiceAndPerformance, character.ContinuityNotes, character.ReferenceAssetId);
    private static MovieLocationDto ToDto(MovieLocation location) => new(location.Id, location.Name, location.Description, location.VisualContinuityNotes, location.ReferenceAssetId);
    private static MovieSetDto ToDto(MovieSet item) => new(item.Id, item.MovieLocationId, item.Name, item.Description, item.EnvironmentType, item.VisualDescription, item.TimeOfDay, item.Weather, item.ContinuityNotes, item.ReferenceAssetId, item.Variations.OrderBy(variation => variation.CreatedAt).Select(ToDto).ToArray());
    private static MovieSetVariationDto ToDto(MovieSetVariation item) => new(item.Id, item.MovieSetId, item.Name, item.VisualDescription, item.TimeOfDay, item.Weather, item.Lighting, item.ContinuityNotes, item.ReferenceAssetId, item.IsDefault);
    private static MoviePropDto ToDto(MovieProp item) => new(item.Id, item.Name, item.Description, item.Category, item.ContinuityNotes, item.ReferenceAssetId);
    private static MovieWorldReferenceDto ToDto(MovieWorldReference item) => new(item.Id, item.Name, item.Kind, item.Description, item.TagsJson, item.AssetId);
    private static MovieWorldUsageDto ToDto(MovieWorldUsage item) => new(item.Id, item.MovieSceneId, item.MovieShotId, item.EntityType, item.EntityId, item.Role);
    private static MovieContinuityFactDto ToDto(MovieContinuityFact item) => new(item.Id, item.ScopeType, item.ScopeId, item.FactKey, item.FactValue, item.Notes, item.UpdatedAt);
    private static MovieContinuityLockDto ToDto(MovieContinuityLock item) => new(item.Id, item.EntityType, item.EntityId, item.FieldName, item.LockedValue, item.Strength, item.Reason, item.CreatedAt, item.ReleasedAt);
    private static MovieClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieSceneId, clip.MovieShotId, clip.GenerationJobId, clip.AssetId, clip.Status, clip.DurationSeconds, clip.MetadataJson, clip.ContinuitySnapshotJson);
    private static MovieAssemblyDto ToDto(MovieAssembly assembly) => new(assembly.Id, assembly.GenerationJobId, assembly.AssetId, assembly.Status, assembly.OutputFormat, assembly.MetadataJson, assembly.CreatedAt, assembly.CompletedAt);
}

public sealed class MovieStudioValidationException(string message) : Exception(message);

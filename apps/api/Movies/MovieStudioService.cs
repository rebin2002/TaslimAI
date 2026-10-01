using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;

namespace Taslim.Api.Movies;

public interface IMovieStudioService
{
    Task<MovieStudioProjectResponse?> CreateAsync(Guid userId, MovieStudioCreateRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieWorkspaceResponse?> GetWorkspaceAsync(Guid userId, Guid id, string? module, CancellationToken cancellationToken);
    Task<MovieStudioProjectShellDto?> GetShellAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieCastDto?> GetCastAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieCharacterDetailDto?> GetCharacterDetailAsync(Guid userId, Guid characterId, CancellationToken cancellationToken);
    Task<MovieWorldWorkspaceDto?> GetWorldAsync(Guid userId, Guid id, CancellationToken cancellationToken);
    Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken);
    Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterDto?> UpdateCharacterAsync(Guid userId, Guid characterId, MovieStudioCharacterRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterStateDto?> AddCharacterStateAsync(Guid userId, Guid characterId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterStateDto?> UpdateCharacterStateAsync(Guid userId, Guid stateId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterRelationshipDto?> AddCharacterRelationshipAsync(Guid userId, Guid characterId, MovieStudioCharacterRelationshipRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterContinuityLockDto?> AddCharacterContinuityLockAsync(Guid userId, Guid characterId, MovieCharacterContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> UpdateLocationAsync(Guid userId, Guid locationId, MovieStudioLocationRequest request, CancellationToken cancellationToken);
    Task<MovieSetDto?> AddSetAsync(Guid userId, Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken);
    Task<MovieSetVariationDto?> AddSetVariationAsync(Guid userId, Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken);
    Task<MoviePropDto?> AddPropAsync(Guid userId, Guid id, MovieStudioPropRequest request, CancellationToken cancellationToken);
    Task<MovieWorldReferenceDto?> AddWorldReferenceAsync(Guid userId, Guid id, MovieStudioWorldReferenceRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityFactDto?> AddContinuityFactAsync(Guid userId, Guid id, MovieStudioContinuityFactRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid id, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieWorldUsageDto?> AddWorldUsageAsync(Guid userId, Guid sceneId, MovieStudioWorldUsageRequest request, CancellationToken cancellationToken);
    Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken);
    Task<MovieStoryboardProjectDto?> GetStoryboardAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieSceneShotPlanDto?> GetSceneShotPlanAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken);
    Task<MovieShotDto?> GetShotPlanningAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieShotDto?> UpdateShotPlanningAsync(Guid userId, Guid shotId, MovieStudioShotUpdateRequest request, CancellationToken cancellationToken);
    Task<MovieSceneShotPlanDto?> ReorderShotAsync(Guid userId, Guid shotId, MovieShotReorderRequest request, CancellationToken cancellationToken);
    Task<MovieSceneShotPlanDto?> ArchiveShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieShotProductionDto?> GetShotProductionAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> CreateProductionVersionAsync(Guid userId, Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken);
    Task<MovieKeyframeGenerationResponse?> QueueKeyframeGenerationAsync(Guid userId, Guid shotId, MovieKeyframeGenerationRequest request, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> SelectKeyframeAsync(Guid userId, Guid versionId, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> ReviewProductionVersionAsync(Guid userId, Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken);
    Task<MovieSelectiveRegenerationResponse?> CreateRegenerationRequestAsync(Guid userId, Guid shotId, MovieRegenerationRequestInput request, CancellationToken cancellationToken);
    Task<MovieSelectiveRegenerationResponse?> ConfirmRegenerationAsync(Guid userId, Guid requestId, MovieRegenerationConfirmationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieSelectiveRegenerationResponse?> GetRegenerationRequestAsync(Guid userId, Guid requestId, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> CreateMotionPreviewAsync(Guid userId, Guid shotId, MovieProductionMotionPreviewRequest request, CancellationToken cancellationToken);
    Task<MovieProductionRenderResponse?> QueueProductionRenderAsync(Guid userId, Guid shotId, MovieProductionRenderRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieV2TakeDto?> CreateTakeFromProductionAsync(Guid userId, Guid versionId, MovieProductionTakeRequest request, CancellationToken cancellationToken);
    Task<MovieStudioGenerationResponse?> GenerateSceneAsync(Guid userId, Guid movieProjectId, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieProviderReadinessDto> ProviderReadinessAsync();
}

public sealed class MovieStudioService(TaslimDbContext db, WorkspaceAccessService access, MovieCollaborationAccess collaboration, IGenerationJobService jobs, IMovieVideoProvider provider, IMovieCharacterContinuityService continuity, MovieWorldContinuityProjector worldContinuity, IMovieGenerationCostEstimator movieCostEstimator, IGenerationCostGuardrailService costGuardrails, IMovieProductionGenerationOrchestrator productionOrchestrator) : IMovieStudioService
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
        };
        var guide = new MovieContinuityGuide
        {
            Id = Guid.NewGuid(), VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty,
            CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty, ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty,
            SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty, ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty,
            CinematographyIntent = CinematographyIntentValidator.Normalize(request.Cinematography)?.Intent,
            CinematographyBibleReferencesJson = CinematographyIntentValidator.ToJson(request.Cinematography),
            CurrentRevisionNumber = 1, UpdatedAt = now,
        };
        guide.Revisions.Add(MovieGuideService.CreateInitialRevision(guide, userId, now, request));
        movie.Guide = guide;
        db.MovieProjects.Add(movie);
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movie.Id, UserId = userId, Role = MovieTeamRoles.Producer,
            IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        GenerationJobDto? job = null;
        var saved = await GetAsync(userId, movie.Id, cancellationToken);
        return saved is null ? null : new MovieStudioProjectResponse(saved, job);
    }

    public async Task<MovieStudioProjectDto?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await Query().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken) ? null : ToDto(movie);
    }

    public async Task<MovieStudioProjectShellDto?> GetShellAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().Include(item => item.Guide).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken)
            ? null
            : new MovieStudioProjectShellDto(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, movie.Guide.LockedRevisionNumber);
    }

    public async Task<MovieCastDto?> GetCastAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken)) return null;
        var characters = await CastQuery().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt).ToListAsync(cancellationToken);
        return new MovieCastDto(ToCastProjectDto(movie), characters.Select(ToCastCharacterDto).ToArray());
    }

    public async Task<MovieCharacterDetailDto?> GetCharacterDetailAsync(Guid userId, Guid characterId, CancellationToken cancellationToken)
    {
        var character = await CastQuery().FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        return character is null || !await collaboration.HasPermissionAsync(userId, character.MovieProjectId, MoviePermissions.View, cancellationToken)
            ? null
            : new MovieCharacterDetailDto(ToCastProjectDto(character.MovieProject), ToDto(character));
    }

    public async Task<MovieWorldWorkspaceDto?> GetWorldAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken)) return null;

        var locations = await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var sets = await db.MovieSets.AsNoTracking().Where(item => item.MovieProjectId == id).Include(item => item.Variations).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var props = await db.MovieProps.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var references = await db.MovieWorldReferences.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var usages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == id).Include(item => item.MovieScene).Include(item => item.MovieShot).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var facts = await db.MovieContinuityFacts.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var locks = await db.MovieContinuityLocks.AsNoTracking().Where(item => item.MovieProjectId == id && item.ReleasedAt == null).OrderByDescending(item => item.CreatedAt).ToArrayAsync(cancellationToken);
        var world = new MovieWorldDto(locations.Select(ToDto).ToArray(), sets.Select(ToDto).ToArray(), props.Select(ToDto).ToArray(), references.Select(ToDto).ToArray(), usages.Select(ToDto).ToArray(), facts.Select(ToDto).ToArray(), locks.Select(ToDto).ToArray());

        var names = locations.ToDictionary(item => (Type: MovieWorldEntityTypes.Location, item.Id), item => item.Name);
        foreach (var item in sets) names[(MovieWorldEntityTypes.Set, item.Id)] = item.Name;
        foreach (var item in props) names[(MovieWorldEntityTypes.Prop, item.Id)] = item.Name;
        var usageDetails = usages.Select(item => new MovieWorldUsageDetailDto(item.Id, item.MovieSceneId, item.MovieShotId, item.MovieScene.Sequence, item.MovieScene.Title, item.MovieShot?.Sequence, item.MovieShot?.Description, item.EntityType, item.EntityId, names.GetValueOrDefault((item.EntityType, item.EntityId), "World record"), item.Role)).ToArray();

        var assetIds = locations.Select(item => item.ReferenceAssetId).Concat(sets.Select(item => item.ReferenceAssetId)).Concat(sets.SelectMany(item => item.Variations.Select(variation => variation.ReferenceAssetId))).Concat(props.Select(item => item.ReferenceAssetId)).Concat(references.Select(item => item.AssetId)).Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        var assets = assetIds.Length == 0
            ? Array.Empty<MovieWorldAssetDto>()
            : await db.Assets.AsNoTracking().Where(item => item.WorkspaceId == movie.WorkspaceId && assetIds.Contains(item.Id)).OrderBy(item => item.Name).Select(item => new MovieWorldAssetDto(item.Id, item.Name, item.AssetType, item.MimeType, item.StoredFileId.HasValue, item.StoredFileId.HasValue && (item.MimeType != null && (item.MimeType.StartsWith("image/") || item.MimeType.StartsWith("audio/") || item.MimeType.StartsWith("video/"))))).ToArrayAsync(cancellationToken);
        return new MovieWorldWorkspaceDto(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, world, usageDetails, assets);
    }

    public async Task<MovieWorkspaceResponse?> GetWorkspaceAsync(Guid userId, Guid id, string? module, CancellationToken cancellationToken)
    {
        var requestedModule = MovieWorkspaceModules.Normalize(module);
        var baseProject = await db.MovieProjects.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.Id, item.WorkspaceId, item.ProjectId, item.Mode, item.Status, item.Title, item.Description,
                item.DurationSeconds, item.AspectRatio, item.Style, item.Language, item.AdditionalInstructions,
                item.CreatedAt, item.UpdatedAt,
                Guide = new MovieWorkspaceGuideDto(
                    item.Guide.Id, item.Guide.VisualLanguage, item.Guide.CameraLanguage, item.Guide.ColorAndLighting,
                    item.Guide.SoundAndNarration, item.Guide.ContinuityRules, item.Guide.UpdatedAt),
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (baseProject is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken)) return null;

        // Scene and clip summaries are the stable shell contract. They intentionally
        // omit screenplay, asset metadata, production JSON, and character/world detail.
        var sceneRows = await db.MovieScenes.AsNoTracking()
            .Where(item => item.MovieProjectId == id)
            .OrderBy(item => item.Sequence)
            .Select(item => new
            {
                item.Id, item.Sequence, item.Title, item.Summary, item.DurationSeconds,
                item.ContinuityNotes, item.Narration, item.Dialogue,
                ShotCount = item.Shots.Count(),
            })
            .ToListAsync(cancellationToken);
        var clips = await db.MovieClips.AsNoTracking()
            .Where(item => item.MovieProjectId == id)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new MovieWorkspaceClipDto(item.Id, item.MovieSceneId, item.MovieShotId, item.AssetId, item.Status, item.DurationSeconds))
            .ToListAsync(cancellationToken);
        var sceneClips = clips.Where(item => item.MovieSceneId.HasValue).ToLookup(item => item.MovieSceneId!.Value);
        var projectedScenes = sceneRows.Select(item => new MovieWorkspaceSceneDto(
                item.Id, item.Sequence, item.Title, item.Summary, item.DurationSeconds,
                item.ContinuityNotes, item.Narration, item.Dialogue, item.ShotCount, sceneClips[item.Id].ToArray())).ToArray();

        var characters = requestedModule == MovieWorkspaceModules.Cast
            ? await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt)
                .Select(item => new MovieWorkspaceCharacterDto(item.Id, item.Name, item.Role, item.Description, item.Appearance, item.VoiceAndPerformance, item.ContinuityNotes, item.CreatedAt, item.UpdatedAt))
                .ToListAsync(cancellationToken)
            : [];
        var locations = requestedModule == MovieWorkspaceModules.World
            ? await db.MovieLocations.AsNoTracking().Where(item => item.MovieProjectId == id).OrderBy(item => item.CreatedAt)
                .Select(item => new MovieWorkspaceLocationDto(item.Id, item.Name, item.Description, item.VisualContinuityNotes))
                .ToListAsync(cancellationToken)
            : [];
        var assemblies = requestedModule is MovieWorkspaceModules.Overview or MovieWorkspaceModules.Production
            ? await db.MovieAssemblies.AsNoTracking().Where(item => item.MovieProjectId == id).OrderByDescending(item => item.CreatedAt)
                .Select(item => new MovieWorkspaceAssemblyDto(item.Id, item.AssetId, item.Status, item.CreatedAt, item.CompletedAt))
                .ToListAsync(cancellationToken)
            : [];

        var project = new MovieWorkspaceProjectDto(
            baseProject.Id, baseProject.WorkspaceId, baseProject.ProjectId, baseProject.Mode, baseProject.Status,
            baseProject.Title, baseProject.Description, baseProject.DurationSeconds, baseProject.AspectRatio,
            baseProject.Style, baseProject.Language, baseProject.AdditionalInstructions, baseProject.CreatedAt,
            baseProject.UpdatedAt, baseProject.Guide, projectedScenes, characters, locations, clips, assemblies,
            requestedModule == MovieWorkspaceModules.World ? new MovieWorkspaceWorldDto(locations) : null);
        return new MovieWorkspaceResponse(requestedModule, project);
    }

    public async Task<MovieStudioProjectDto?> UpdateGuideAsync(Guid userId, Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.Include(item => item.Guide).FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken)) return null;
        if (movie.Guide.LockedRevisionNumber is not null) throw new MovieGuideLockedException();
        var cinematographyValidation = CinematographyIntentValidator.Validate(request.Cinematography);
        if (cinematographyValidation is not null) throw new MovieStudioValidationException(cinematographyValidation);
        movie.Guide.VisualLanguage = request.VisualLanguage?.Trim() ?? string.Empty;
        movie.Guide.CameraLanguage = request.CameraLanguage?.Trim() ?? string.Empty;
        movie.Guide.ColorAndLighting = request.ColorAndLighting?.Trim() ?? string.Empty;
        movie.Guide.SoundAndNarration = request.SoundAndNarration?.Trim() ?? string.Empty;
        movie.Guide.ContinuityRules = request.ContinuityRules?.Trim() ?? string.Empty;
        var cinematography = CinematographyIntentValidator.Normalize(request.Cinematography);
        movie.Guide.CinematographyIntent = cinematography?.Intent;
        movie.Guide.CinematographyBibleReferencesJson = CinematographyIntentValidator.ToJson(cinematography);
        var now = DateTime.UtcNow;
        movie.Guide.Revisions.Add(MovieGuideService.BuildRevision(movie.Guide, userId, new MovieGuideRevisionRequest
        {
            StoryBibleJson = "{}",
            CharacterBibleReferencesJson = "[]",
            WorldBibleReferencesJson = "[]",
            VisualBibleJson = JsonSerializer.Serialize(new { visualLanguage = movie.Guide.VisualLanguage, colorAndLighting = movie.Guide.ColorAndLighting }),
            CinematographyBibleJson = JsonSerializer.Serialize(new { cameraLanguage = movie.Guide.CameraLanguage }),
            AudioBibleJson = JsonSerializer.Serialize(new { soundAndNarration = movie.Guide.SoundAndNarration }),
            ContinuityBibleJson = JsonSerializer.Serialize(new { continuityRules = movie.Guide.ContinuityRules }),
        }, movie.Guide.CurrentRevisionNumber + 1, now));
        movie.Guide.CurrentRevisionNumber++;
        movie.Guide.UpdatedAt = movie.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(userId, id, cancellationToken);
    }

    public async Task<MovieSceneDto?> AddSceneAsync(Guid userId, Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Summary)) throw new MovieStudioValidationException("Scene title and summary are required.");
        var durationValidation = MovieDurationBudgetPolicy.ValidateSceneDuration(request.DurationSeconds);
        if (durationValidation is not null) throw new MovieStudioValidationException(durationValidation);
        var now = DateTime.UtcNow;
        var scene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = id, Sequence = await db.MovieScenes.CountAsync(item => item.MovieProjectId == id, cancellationToken) + 1, Title = request.Title.Trim(), Summary = request.Summary.Trim(), DurationSeconds = request.DurationSeconds, ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.Add(scene);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(scene, [], []);
    }

    public async Task<MovieCharacterDto?> AddCharacterAsync(Guid userId, Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Character name and description are required.");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, movie.WorkspaceId, movie.ProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var referenceAssetIds = request.ReferenceAssetIds?.Distinct().ToList() ?? [];
        if (referenceAssetIds.Count > 64) throw new MovieStudioValidationException("A character can have at most 64 reference assets.");
        if (request.ReferenceAssetId.HasValue && !referenceAssetIds.Contains(request.ReferenceAssetId.Value)) referenceAssetIds.Insert(0, request.ReferenceAssetId.Value);
        await ValidateReferenceAssetsAsync(movie.WorkspaceId, movie.ProjectId, referenceAssetIds, cancellationToken);
        var character = new MovieCharacter
        {
            Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Role = MovieStudioHelpers.Clean(request.Role),
            Description = request.Description.Trim(), Appearance = MovieStudioHelpers.Clean(request.Appearance),
            PhysicalDescription = MovieStudioHelpers.Clean(request.PhysicalDescription), Wardrobe = MovieStudioHelpers.Clean(request.Wardrobe),
            VoiceReference = MovieStudioHelpers.Clean(request.VoiceReference), PersonalityAndStoryNotes = MovieStudioHelpers.Clean(request.PersonalityAndStoryNotes),
            VoiceAndPerformance = MovieStudioHelpers.Clean(request.VoiceAndPerformance), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes),
            ReferenceAssetId = request.ReferenceAssetId ?? (referenceAssetIds.Count == 0 ? null : referenceAssetIds[0]), CreatedAt = now, UpdatedAt = now,
        };
        character.ReferenceAssets = referenceAssetIds.Select((assetId, index) => new MovieCharacterReferenceAsset { MovieCharacterId = character.Id, AssetId = assetId, SortOrder = index, CreatedAt = now }).ToList();
        db.MovieCharacters.Add(character);
        await db.SaveChangesAsync(cancellationToken);
        return await GetCharacterDtoAsync(userId, character.Id, cancellationToken);
    }

    public async Task<MovieCharacterDto?> UpdateCharacterAsync(Guid userId, Guid characterId, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).Include(item => item.ContinuityLocks).Include(item => item.ReferenceAssets).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        if (character is null || !await collaboration.HasPermissionAsync(userId, character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Character name and description are required.");
        EnsureCardLocksAllow(character, request);
        var referenceAssetIds = request.ReferenceAssetIds?.Distinct().ToList() ?? character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToList();
        if (referenceAssetIds.Count > 64) throw new MovieStudioValidationException("A character can have at most 64 reference assets.");
        if (request.ReferenceAssetId.HasValue && !referenceAssetIds.Contains(request.ReferenceAssetId.Value)) referenceAssetIds.Insert(0, request.ReferenceAssetId.Value);
        await ValidateReferenceAssetsAsync(character.MovieProject.WorkspaceId, character.MovieProject.ProjectId, referenceAssetIds, cancellationToken);
        character.Name = request.Name.Trim(); character.Role = MovieStudioHelpers.Clean(request.Role); character.Description = request.Description.Trim();
        character.Appearance = MovieStudioHelpers.Clean(request.Appearance); character.PhysicalDescription = MovieStudioHelpers.Clean(request.PhysicalDescription);
        character.Wardrobe = MovieStudioHelpers.Clean(request.Wardrobe); character.VoiceReference = MovieStudioHelpers.Clean(request.VoiceReference);
        character.PersonalityAndStoryNotes = MovieStudioHelpers.Clean(request.PersonalityAndStoryNotes); character.VoiceAndPerformance = MovieStudioHelpers.Clean(request.VoiceAndPerformance);
        character.ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes); character.ReferenceAssetId = request.ReferenceAssetId ?? (referenceAssetIds.Count == 0 ? null : referenceAssetIds[0]);
        db.MovieCharacterReferenceAssets.RemoveRange(character.ReferenceAssets);
        character.ReferenceAssets = referenceAssetIds.Select((assetId, index) => new MovieCharacterReferenceAsset { MovieCharacterId = character.Id, AssetId = assetId, SortOrder = index, CreatedAt = DateTime.UtcNow }).ToList();
        character.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(cancellationToken);
        return await GetCharacterDtoAsync(userId, character.Id, cancellationToken);
    }

    public async Task<MovieCharacterStateDto?> AddCharacterStateAsync(Guid userId, Guid characterId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken)
    {
        var character = await GetCharacterForUserAsync(userId, characterId, cancellationToken);
        if (character is null) return null;
        ValidateState(request, requireKey: true);
        if (await db.MovieCharacterStates.AnyAsync(item => item.MovieCharacterId == characterId && item.Key == request.Key.Trim(), cancellationToken)) throw new MovieStudioValidationException("A character state with this key already exists.");
        var now = DateTime.UtcNow;
        var state = new MovieCharacterState { Id = Guid.NewGuid(), MovieCharacterId = characterId, Key = request.Key.Trim(), Label = MovieStudioHelpers.Clean(request.Label), Wardrobe = MovieStudioHelpers.Clean(request.Wardrobe), AgeOrTimeState = MovieStudioHelpers.Clean(request.AgeOrTimeState), Appearance = MovieStudioHelpers.Clean(request.Appearance), InjuryOrCondition = MovieStudioHelpers.Clean(request.InjuryOrCondition), LocationOrStoryState = MovieStudioHelpers.Clean(request.LocationOrStoryState), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), CreatedAt = now, UpdatedAt = now };
        db.MovieCharacterStates.Add(state); character.UpdatedAt = now; await db.SaveChangesAsync(cancellationToken); return ToDto(state);
    }

    public async Task<MovieCharacterStateDto?> UpdateCharacterStateAsync(Guid userId, Guid stateId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken)
    {
        var state = await db.MovieCharacterStates.Include(item => item.Character).ThenInclude(item => item.MovieProject).Include(item => item.ContinuityLocks).FirstOrDefaultAsync(item => item.Id == stateId, cancellationToken);
        if (state is null || !await collaboration.HasPermissionAsync(userId, state.Character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateState(request, requireKey: false); EnsureStateLocksAllow(state, request);
        state.Label = MovieStudioHelpers.Clean(request.Label); state.Wardrobe = MovieStudioHelpers.Clean(request.Wardrobe); state.AgeOrTimeState = MovieStudioHelpers.Clean(request.AgeOrTimeState); state.Appearance = MovieStudioHelpers.Clean(request.Appearance); state.InjuryOrCondition = MovieStudioHelpers.Clean(request.InjuryOrCondition); state.LocationOrStoryState = MovieStudioHelpers.Clean(request.LocationOrStoryState); state.ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes); state.UpdatedAt = state.Character.MovieProject.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken); return ToDto(state);
    }

    public async Task<MovieCharacterRelationshipDto?> AddCharacterRelationshipAsync(Guid userId, Guid characterId, MovieStudioCharacterRelationshipRequest request, CancellationToken cancellationToken)
    {
        var character = await GetCharacterForUserAsync(userId, characterId, cancellationToken);
        if (character is null) return null;
        if (request.RelatedCharacterId == characterId || string.IsNullOrWhiteSpace(request.RelationshipType)) throw new MovieStudioValidationException("Choose another character and provide a relationship type.");
        var related = await db.MovieCharacters.FirstOrDefaultAsync(item => item.Id == request.RelatedCharacterId && item.MovieProjectId == character.MovieProjectId, cancellationToken);
        if (related is null) throw new MovieStudioValidationException("The related character must belong to this movie project.");
        if (await db.MovieCharacterRelationships.AnyAsync(item => item.MovieCharacterId == characterId && item.RelatedCharacterId == related.Id && item.RelationshipType == request.RelationshipType.Trim(), cancellationToken)) throw new MovieStudioValidationException("This character relationship already exists.");
        var now = DateTime.UtcNow; var relationship = new MovieCharacterRelationship { Id = Guid.NewGuid(), MovieCharacterId = characterId, RelatedCharacterId = related.Id, RelationshipType = request.RelationshipType.Trim(), Notes = MovieStudioHelpers.Clean(request.Notes), CreatedAt = now, UpdatedAt = now };
        db.MovieCharacterRelationships.Add(relationship); character.UpdatedAt = now; await db.SaveChangesAsync(cancellationToken); return new MovieCharacterRelationshipDto(relationship.Id, related.Id, related.Name, relationship.RelationshipType, relationship.Notes);
    }

    public async Task<MovieCharacterContinuityLockDto?> AddCharacterContinuityLockAsync(Guid userId, Guid characterId, MovieCharacterContinuityLockRequest request, CancellationToken cancellationToken)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        if (character is null || !await collaboration.HasPermissionAsync(userId, character.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var fieldKey = request.FieldKey.Trim();
        var lockedValue = request.LockedValue.Trim();
        if (string.IsNullOrWhiteSpace(lockedValue)) throw new MovieStudioValidationException("The continuity lock value is required.");
        if (request.CharacterStateId is Guid stateId)
        {
            var state = await db.MovieCharacterStates.Include(item => item.Character).FirstOrDefaultAsync(item => item.Id == stateId && item.MovieCharacterId == characterId, cancellationToken);
            if (state is null) throw new MovieStudioValidationException("The character state does not belong to this character.");
            if (!CharacterFieldKeys.State.Contains(fieldKey)) throw new MovieStudioValidationException("The continuity lock field is invalid for a character state.");
            var currentValue = StateFieldValue(state, fieldKey); if (!string.Equals(currentValue, lockedValue, StringComparison.Ordinal)) throw new MovieStudioValidationException("The locked value must match the current character state fact.");
            if (await db.MovieCharacterContinuityLocks.AnyAsync(item => item.MovieCharacterId == characterId && item.MovieCharacterStateId == stateId && item.FieldKey == fieldKey, cancellationToken)) throw new MovieStudioValidationException("This character state fact is already locked.");
            var stateLock = new MovieCharacterContinuityLock { Id = Guid.NewGuid(), MovieCharacterId = characterId, MovieCharacterStateId = stateId, FieldKey = fieldKey, LockedValue = lockedValue, ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow };
            db.MovieCharacterContinuityLocks.Add(stateLock); await db.SaveChangesAsync(cancellationToken); return ToDto(stateLock);
        }
        if (!CharacterFieldKeys.Card.Contains(fieldKey)) throw new MovieStudioValidationException("The continuity lock field is invalid for a character card.");
        var cardValue = CardFieldValue(character, fieldKey); if (!string.Equals(cardValue, lockedValue, StringComparison.Ordinal)) throw new MovieStudioValidationException("The locked value must match the current character fact.");
        if (await db.MovieCharacterContinuityLocks.AnyAsync(item => item.MovieCharacterId == characterId && item.MovieCharacterStateId == null && item.FieldKey == fieldKey, cancellationToken)) throw new MovieStudioValidationException("This character fact is already locked.");
        var lockEntity = new MovieCharacterContinuityLock { Id = Guid.NewGuid(), MovieCharacterId = characterId, FieldKey = fieldKey, LockedValue = lockedValue, ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow };
        db.MovieCharacterContinuityLocks.Add(lockEntity); await db.SaveChangesAsync(cancellationToken); return ToDto(lockEntity);
    }

    public async Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Location name and description are required.");
        ValidateWorldText(request.Name, 160, "Location name"); ValidateWorldText(request.Description, 8_000, "Location description"); ValidateWorldText(request.VisualContinuityNotes, 4_000, "Location continuity notes");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, movie.WorkspaceId, movie.ProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var location = new MovieLocation { Id = Guid.NewGuid(), MovieProjectId = id, Name = request.Name.Trim(), Description = request.Description.Trim(), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieLocations.Add(location);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(location);
    }

    public async Task<MovieLocationDto?> UpdateLocationAsync(Guid userId, Guid locationId, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        var location = await db.MovieLocations.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == locationId, cancellationToken);
        if (location is null || !await collaboration.HasPermissionAsync(userId, location.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateRequired(request.Name, request.Description, "Location name and description are required.");
        ValidateWorldText(request.Name, 160, "Location name"); ValidateWorldText(request.Description, 8_000, "Location description"); ValidateWorldText(request.VisualContinuityNotes, 4_000, "Location continuity notes");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, location.MovieProject.WorkspaceId, location.MovieProject.ProjectId, cancellationToken);
        await EnsureWorldLocksAllowAsync(location.MovieProjectId, MovieWorldEntityTypes.Location, locationId, new Dictionary<string, string?>
        {
            ["name"] = request.Name.Trim(),
            ["description"] = request.Description.Trim(),
            ["visualContinuityNotes"] = MovieStudioHelpers.Clean(request.VisualContinuityNotes),
            ["referenceAssetId"] = request.ReferenceAssetId?.ToString(),
        }, cancellationToken);
        location.Name = request.Name.Trim();
        location.Description = request.Description.Trim();
        location.VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes);
        location.ReferenceAssetId = request.ReferenceAssetId;
        location.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(location);
    }

    public async Task<MovieSetDto?> AddSetAsync(Guid userId, Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken)
    {
        var movie = await GetAuthorizedMovieAsync(userId, id, cancellationToken);
        if (movie is null) return null;
        ValidateRequired(request.Name, request.Description, "Set name and description are required.");
        ValidateWorldText(request.Name, 160, "Set name"); ValidateWorldText(request.Description, 8_000, "Set description"); ValidateWorldText(request.EnvironmentType, 40, "Set environment type"); ValidateWorldText(request.VisualDescription, 4_000, "Set visual description"); ValidateWorldText(request.TimeOfDay, 80, "Set time of day"); ValidateWorldText(request.Weather, 160, "Set weather"); ValidateWorldText(request.ContinuityNotes, 4_000, "Set continuity notes");
        if (request.MovieLocationId.HasValue && !await db.MovieLocations.AnyAsync(item => item.Id == request.MovieLocationId && item.MovieProjectId == id, cancellationToken))
            throw new MovieStudioValidationException("The set location must belong to this movie project.");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, movie.WorkspaceId, movie.ProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var item = new MovieSet { Id = Guid.NewGuid(), MovieProjectId = id, MovieLocationId = request.MovieLocationId, Name = request.Name.Trim(), Description = request.Description.Trim(), EnvironmentType = string.IsNullOrWhiteSpace(request.EnvironmentType) ? "practical" : request.EnvironmentType.Trim(), VisualDescription = MovieStudioHelpers.Clean(request.VisualDescription), TimeOfDay = MovieStudioHelpers.Clean(request.TimeOfDay), Weather = MovieStudioHelpers.Clean(request.Weather), ContinuityNotes = MovieStudioHelpers.Clean(request.ContinuityNotes), ReferenceAssetId = request.ReferenceAssetId, CreatedAt = now, UpdatedAt = now };
        db.MovieSets.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<MovieSetVariationDto?> AddSetVariationAsync(Guid userId, Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken)
    {
        var set = await db.MovieSets.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == setId, cancellationToken);
        if (set is null || !await collaboration.HasPermissionAsync(userId, set.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name)) throw new MovieStudioValidationException("Set variation name is required.");
        ValidateWorldText(request.Name, 160, "Set variation name"); ValidateWorldText(request.VisualDescription, 4_000, "Set variation visual description"); ValidateWorldText(request.TimeOfDay, 80, "Set variation time of day"); ValidateWorldText(request.Weather, 160, "Set variation weather"); ValidateWorldText(request.Lighting, 2_000, "Set variation lighting"); ValidateWorldText(request.ContinuityNotes, 4_000, "Set variation continuity notes");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, set.MovieProject.WorkspaceId, set.MovieProject.ProjectId, cancellationToken);
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
        ValidateWorldText(request.Name, 160, "Prop name"); ValidateWorldText(request.Description, 8_000, "Prop description"); ValidateWorldText(request.Category, 80, "Prop category"); ValidateWorldText(request.ContinuityNotes, 4_000, "Prop continuity notes");
        await EnsureMovieAssetAsync(request.ReferenceAssetId, movie.WorkspaceId, movie.ProjectId, cancellationToken);
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
        ValidateWorldText(request.Name, 160, "Reference name"); ValidateWorldText(request.Kind, 40, "Reference kind"); ValidateWorldText(request.Description, 4_000, "Reference description"); ValidateWorldText(request.TagsJson, 4_000, "Reference tags");
        await EnsureMovieAssetAsync(request.AssetId, movie.WorkspaceId, movie.ProjectId, cancellationToken);
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
        ValidateWorldText(request.FactKey, 160, "Continuity fact key"); ValidateWorldText(request.FactValue, 4_000, "Continuity fact value"); ValidateWorldText(request.Notes, 4_000, "Continuity fact notes");
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
        ValidateWorldText(request.EntityType, 40, "Continuity lock entity type"); ValidateWorldText(request.FieldName, 160, "Continuity lock field"); ValidateWorldText(request.LockedValue, 4_000, "Continuity lock value"); ValidateWorldText(request.Reason, 4_000, "Continuity lock reason");
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
        if (scene is null || !await collaboration.HasPermissionAsync(userId, scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var entityType = request.EntityType.Trim().ToLowerInvariant();
        if (!MovieWorldEntityTypes.Supported.Contains(entityType)) throw new MovieStudioValidationException("World usage entity type must be location, set, or prop.");
        ValidateWorldText(request.Role, 120, "World usage role");
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
        var scene = await db.MovieScenes.Include(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await collaboration.HasPermissionAsync(userId, scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Shot description is required.");
        var durationValidation = MovieDurationBudgetPolicy.ValidateShotDuration(request.DurationSeconds);
        if (durationValidation is not null) throw new MovieStudioValidationException(durationValidation);
        var cinematographyValidation = CinematographyIntentValidator.Validate(request.Cinematography);
        if (cinematographyValidation is not null) throw new MovieStudioValidationException(cinematographyValidation);
        var narrativeImportance = MovieShotProductionContractSerialization.NormalizeChoice(request.NarrativeImportance) ?? MovieShotNarrativeImportance.Supporting;
        var productionComplexity = request.ProductionComplexity ?? new MovieShotComplexityProfile(MovieShotComplexityLevels.Low, [], "Defaulted for a provider-neutral planning shot.");
        var qualityRequirements = request.QualityRequirements ?? new MovieShotQualityRequirements(MovieQualityLevels.Fast, ["Shot intent is explicit and reviewable."], "Defaulted for a provider-neutral planning shot.");
        var continuitySensitivity = MovieShotProductionContractSerialization.NormalizeChoice(request.ContinuitySensitivity) ?? MovieShotContinuitySensitivities.Low;
        var upscaleSuitability = MovieShotProductionContractSerialization.NormalizeChoice(request.UpscaleSuitability) ?? MovieShotUpscaleSuitabilities.Preferred;
        var targetOutputRequirements = request.TargetOutputRequirements ?? new MovieShotTargetOutputRequirements(scene.MovieProject.AspectRatio);
        var productionContractValidation = MovieShotProductionContractValidation.Validate(request.DurationSeconds, narrativeImportance, productionComplexity, qualityRequirements, continuitySensitivity, upscaleSuitability, targetOutputRequirements);
        if (productionContractValidation is not null) throw new MovieStudioValidationException(productionContractValidation);
        await ValidateSubjectCharacterIdsAsync(request.SubjectCharacterIds, scene.MovieProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        var shot = new MovieShot
        {
            Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = await db.MovieShots.CountAsync(item => item.MovieSceneId == sceneId, cancellationToken) + 1,
            Description = request.Description.Trim(), Purpose = ShotText(request.Purpose, 2_000), Subjects = ShotText(request.Subjects, 4_000),
            SubjectCharacterIdsJson = MovieShotReadiness.SerializeSubjectCharacterIds(request.SubjectCharacterIds), LocationSet = ShotText(request.LocationSet, 2_000),
            NarrativeImportance = narrativeImportance,
            ProductionComplexityJson = MovieShotProductionContractSerialization.ToJson(productionComplexity),
            QualityRequirementsJson = MovieShotProductionContractSerialization.ToJson(qualityRequirements),
            ContinuitySensitivity = continuitySensitivity,
            UpscaleSuitability = upscaleSuitability,
            TargetOutputRequirementsJson = MovieShotProductionContractSerialization.ToJson(targetOutputRequirements),
            ProductionRequirements = ShotText(request.ProductionRequirements, 4_000), ContinuityReferences = ShotText(request.ContinuityReferences, 4_000),
            CameraAndFraming = MovieStudioHelpers.Clean(request.CameraAndFraming), CameraMotion = MovieStudioHelpers.Clean(request.CameraMotion),
            CinematographyJson = CinematographyIntentValidator.ToJson(request.Cinematography), DurationSeconds = request.DurationSeconds,
            Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes),
            CreatedAt = now, UpdatedAt = now,
        };
        shot.QualityRequirementsJson = SerializeQualityProfile(shot, QualityPlanningContext(scene.MovieProject, scene, shot), qualityRequirements);
        db.MovieShots.Add(shot);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shot, []);
    }

    public async Task<MovieSceneShotPlanDto?> GetSceneShotPlanAsync(Guid userId, Guid sceneId, CancellationToken cancellationToken)
    {
        var scene = await PlanningQuery().FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null || !await collaboration.HasPermissionAsync(userId, scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToShotPlanDto(scene);
    }

    public async Task<MovieShotDto?> GetShotPlanningAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await PlanningShotQuery().FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToDto(shot, shot.Clips.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray());
    }

    public async Task<MovieShotDto?> UpdateShotPlanningAsync(Guid userId, Guid shotId, MovieStudioShotUpdateRequest request, CancellationToken cancellationToken)
    {
        var shot = await PlanningShotQuery().FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Shot description is required.");
        var durationValidation = MovieDurationBudgetPolicy.ValidateShotDuration(request.DurationSeconds);
        if (durationValidation is not null) throw new MovieStudioValidationException(durationValidation);
        if (!string.IsNullOrWhiteSpace(request.Status) && !MovieShotStatuses.Supported.Contains(request.Status.Trim())) throw new MovieStudioValidationException("Shot status is not supported.");
        if (string.Equals(request.Status?.Trim(), MovieShotStatuses.Approved, StringComparison.OrdinalIgnoreCase) && !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Approve, cancellationToken)) return null;
        var cinematographyValidation = CinematographyIntentValidator.Validate(request.Cinematography);
        if (cinematographyValidation is not null) throw new MovieStudioValidationException(cinematographyValidation);
        var productionContractValidation = MovieShotProductionContractValidation.Validate(request.DurationSeconds, request.NarrativeImportance, request.ProductionComplexity, request.QualityRequirements, request.ContinuitySensitivity, request.UpscaleSuitability, request.TargetOutputRequirements);
        if (productionContractValidation is not null) throw new MovieStudioValidationException(productionContractValidation);
        await ValidateSubjectCharacterIdsAsync(request.SubjectCharacterIds, shot.Scene.MovieProjectId, cancellationToken);
        var now = DateTime.UtcNow;
        shot.Description = request.Description.Trim(); shot.Purpose = ShotText(request.Purpose, 2_000); shot.Subjects = ShotText(request.Subjects, 4_000);
        shot.SubjectCharacterIdsJson = MovieShotReadiness.SerializeSubjectCharacterIds(request.SubjectCharacterIds); shot.LocationSet = ShotText(request.LocationSet, 2_000);
        shot.NarrativeImportance = MovieShotProductionContractSerialization.NormalizeChoice(request.NarrativeImportance);
        shot.ProductionComplexityJson = MovieShotProductionContractSerialization.ToJson(request.ProductionComplexity);
        shot.QualityRequirementsJson = MovieShotProductionContractSerialization.ToJson(request.QualityRequirements);
        shot.ContinuitySensitivity = MovieShotProductionContractSerialization.NormalizeChoice(request.ContinuitySensitivity);
        shot.UpscaleSuitability = MovieShotProductionContractSerialization.NormalizeChoice(request.UpscaleSuitability);
        shot.TargetOutputRequirementsJson = MovieShotProductionContractSerialization.ToJson(request.TargetOutputRequirements);
        shot.DurationSeconds = request.DurationSeconds; shot.ProductionRequirements = ShotText(request.ProductionRequirements, 4_000); shot.ContinuityReferences = ShotText(request.ContinuityReferences, 4_000);
        shot.CameraAndFraming = MovieStudioHelpers.Clean(request.CameraAndFraming); shot.CameraMotion = MovieStudioHelpers.Clean(request.CameraMotion);
        shot.CinematographyJson = CinematographyIntentValidator.ToJson(request.Cinematography); shot.Narration = MovieStudioHelpers.Clean(request.Narration);
        shot.Dialogue = MovieStudioHelpers.Clean(request.Dialogue); shot.VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes);
        if (!string.IsNullOrWhiteSpace(request.Status)) { shot.Status = request.Status.Trim(); shot.ArchivedAt = shot.Status == MovieShotStatuses.Archived ? now : null; }
        shot.QualityRequirementsJson = SerializeQualityProfile(shot, QualityPlanningContext(shot.Scene.MovieProject, shot.Scene, shot), request.QualityRequirements);
        shot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(shot, shot.Clips.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray());
    }

    public async Task<MovieSceneShotPlanDto?> ReorderShotAsync(Guid userId, Guid shotId, MovieShotReorderRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (shot.Status == MovieShotStatuses.Archived) throw new MovieStudioValidationException("Archived shots cannot be reordered.");
        var shots = await db.MovieShots.Where(item => item.MovieSceneId == shot.MovieSceneId && item.Status != MovieShotStatuses.Archived).OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
        if (request.Sequence < 1 || request.Sequence > shots.Count) throw new MovieStudioValidationException($"Shot order must be between 1 and {shots.Count}.");
        shots.Remove(shot); shots.Insert(request.Sequence - 1, shot);
        for (var index = 0; index < shots.Count; index++) shots[index].Sequence = 10_000 + index;
        await db.SaveChangesAsync(cancellationToken);
        for (var index = 0; index < shots.Count; index++) shots[index].Sequence = index + 1;
        await db.SaveChangesAsync(cancellationToken);
        return await GetSceneShotPlanAsync(userId, shot.MovieSceneId, cancellationToken);
    }

    public async Task<MovieSceneShotPlanDto?> ArchiveShotAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        shot.Status = MovieShotStatuses.Archived; shot.ArchivedAt = DateTime.UtcNow; shot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await GetSceneShotPlanAsync(userId, shot.MovieSceneId, cancellationToken);
    }

    public async Task<MovieStoryboardProjectDto?> GetStoryboardAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.AsNoTracking()
            .Include(item => item.Guide)
            .Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.ProductionVersions).ThenInclude(version => version.AssetReferences)
            .FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (movie is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;

        return new MovieStoryboardProjectDto(
            movie.Id,
            movie.WorkspaceId,
            movie.Status,
            movie.Title,
            movie.Description,
            movie.DurationSeconds,
            movie.AspectRatio,
            movie.Style,
            movie.Language,
            new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt, movie.Guide.CurrentRevisionNumber, movie.Guide.LockedRevisionNumber, movie.Guide.LockedAt, ToCinematographyBible(movie.Guide)),
            provider.IsAvailable,
            movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => new MovieStoryboardSceneDto(
                scene.Id,
                scene.Sequence,
                scene.Title,
                scene.Summary,
                scene.DurationSeconds,
                scene.ContinuityNotes,
                scene.Shots.OrderBy(shot => shot.Sequence).Select(ToStoryboardShot).ToArray())).ToArray());
    }

    public async Task<MovieShotProductionDto?> GetShotProductionAsync(Guid userId, Guid shotId, CancellationToken cancellationToken)
    {
        var shot = await ProductionQuery().FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return ToProductionDto(shot, await worldContinuity.ProjectAsync(shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id, cancellationToken));
    }

    public async Task<MovieProductionVersionDto?> CreateProductionVersionAsync(Guid userId, Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.ProductionVersions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null) return null;
        var requiredPermission = string.Equals(request.Stage?.Trim(), MovieProductionStages.ProductionKeyframe, StringComparison.Ordinal)
            ? MoviePermissions.Generate
            : MoviePermissions.Edit;
        await collaboration.RequireAsync(userId, shot.Scene.MovieProjectId, requiredPermission, cancellationToken);
        if (request.VersionId.HasValue && await db.MovieProductionVersions.AnyAsync(item => item.Id == request.VersionId.Value, cancellationToken))
            throw new MovieProductionValidationException("PRODUCTION_VERSION_ID_REUSED", "The production version identifier is already in use.");
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
        var stage = request.Stage?.Trim() ?? string.Empty;
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(stage, source);
        if (workflowError is not null) throw new MovieProductionValidationException("PRODUCTION_STAGE_INVALID", workflowError);
        var assetIds = new Dictionary<Guid, string>();
        if (request.AssetReferences is { Count: > 64 })
            throw new MovieProductionValidationException("PRODUCTION_ASSET_REFERENCES_TOO_MANY", "A production version can contain at most 64 asset references.");
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
        if (assetIds.Count > 0)
        {
            var productionProjectId = shot.Scene.MovieProject.ProjectId;
            var referencedAssets = await db.Assets.AsNoTracking()
                .Where(item => item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId && assetIds.Keys.Contains(item.Id))
                .Select(item => new { item.Id, item.ProjectId, item.Status, item.StoredFileId })
                .ToListAsync(cancellationToken);
            if (referencedAssets.Count != assetIds.Count)
                throw new MovieProductionValidationException("PRODUCTION_ASSET_NOT_FOUND", "Every referenced Asset must belong to the movie workspace.");
            if (referencedAssets.Any(item => item.Status != AssetStatus.Active
                || item.ProjectId.HasValue && item.ProjectId != productionProjectId))
                throw new MovieProductionValidationException("PRODUCTION_ASSET_SCOPE_INVALID", "Referenced assets must be active and belong to this movie project or its workspace.");
            var storedFileIds = referencedAssets.Where(item => item.StoredFileId.HasValue).Select(item => item.StoredFileId!.Value).ToArray();
            if (storedFileIds.Length > 0 && await db.StoredFiles.AsNoTracking().CountAsync(item => storedFileIds.Contains(item.Id) && item.Status == StoredFileStatus.Ready, cancellationToken) != storedFileIds.Length)
                throw new MovieProductionValidationException("PRODUCTION_ASSET_FILE_NOT_READY", "Every referenced asset file must be ready before production can use it.");
        }
        GenerationJob? linkedJob = null;
        if (request.GenerationJobId.HasValue)
        {
            linkedJob = await db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == request.GenerationJobId && item.WorkspaceId == shot.Scene.MovieProject.WorkspaceId, cancellationToken);
            if (linkedJob is null) throw new MovieProductionValidationException("PRODUCTION_JOB_NOT_FOUND", "The linked GenerationJob is not available in the movie workspace.");
            if (!MovieProductionWorkflow.IsGenerationJobTypeAllowed(stage, linkedJob.JobType)) throw new MovieProductionValidationException("PRODUCTION_JOB_TYPE_INVALID", "The linked GenerationJob type is not valid for this production stage.");
            if (shot.Scene.MovieProject.ProjectId.HasValue && linkedJob.ProjectId != shot.Scene.MovieProject.ProjectId) throw new MovieProductionValidationException("PRODUCTION_JOB_PROJECT_MISMATCH", "The linked GenerationJob must belong to the movie project.");
            if (string.Equals(linkedJob.JobType, GenerationJobTypes.ImageGenerate, StringComparison.OrdinalIgnoreCase))
            {
                ImageGenerationInput? imageInput;
                try { imageInput = JsonSerializer.Deserialize<ImageGenerationInput>(linkedJob.InputJson); }
                catch (JsonException) { imageInput = null; }
                if (!request.VersionId.HasValue || imageInput?.ProductionVersionId != request.VersionId)
                    throw new MovieProductionValidationException("PRODUCTION_JOB_VERSION_MISMATCH", "The linked image generation job is not bound to this production version.");
            }
            if (GenerationJobTypes.MovieTypes.Contains(linkedJob.JobType)
                && !await db.MovieClips.AnyAsync(item => item.GenerationJobId == linkedJob.Id && item.MovieProjectId == shot.Scene.MovieProjectId && item.MovieShotId == shotId, cancellationToken))
                throw new MovieProductionValidationException("PRODUCTION_JOB_SHOT_MISMATCH", "The linked Movie generation job must belong to this shot.");
            if (request.AssetId.HasValue && !await db.Assets.AnyAsync(item => item.Id == request.AssetId && item.SourceGenerationJobId == linkedJob.Id, cancellationToken))
                throw new MovieProductionValidationException("PRODUCTION_ASSET_JOB_MISMATCH", "The primary production asset must be the output of the linked generation job.");
        }

        var continuitySnapshot = await continuity.BuildSnapshotForTargetAsync(shot.Scene.MovieProjectId, shot.Scene.Id, shot.Id, true, cancellationToken);
        var continuityReference = await ContinuitySnapshotReferenceAsync(shot.Scene.MovieProjectId, shot.MovieSceneId, shot.Id, cancellationToken);
        var cinematographyReference = CinematographyReference(shot);
        var now = DateTime.UtcNow;
        var version = new MovieProductionVersion
        {
            Id = request.VersionId ?? Guid.NewGuid(), MovieShotId = shotId, VersionNumber = shot.ProductionVersions.Count == 0 ? 1 : shot.ProductionVersions.Max(item => item.VersionNumber) + 1,
            Stage = stage, Status = MovieProductionVersionStatuses.PendingApproval, Label = CleanBounded(request.Label, 160), CompositionJson = request.CompositionJson.Trim(),
            RegenerationMetadataJson = request.RegenerationMetadataJson?.Trim(), StageProvenanceJson = request.StageProvenanceJson?.Trim(), ContinuitySnapshotReferenceJson = continuityReference, CinematographyReferenceJson = cinematographyReference, SourceVersionId = source?.Id,
            GenerationJobId = request.GenerationJobId, AssetId = request.AssetId, FirstFrameAssetId = request.FirstFrameAssetId, LastFrameAssetId = request.LastFrameAssetId,
            FirstFrameNotes = CleanBounded(request.FirstFrameNotes, 2_000), LastFrameNotes = CleanBounded(request.LastFrameNotes, 2_000), CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
            ContinuitySnapshotId = continuitySnapshot.SnapshotId, ContinuitySnapshotVersion = continuitySnapshot.Version, ContinuitySnapshotHash = continuitySnapshot.SnapshotHash,
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

    public async Task<MovieKeyframeGenerationResponse?> QueueKeyframeGenerationAsync(Guid userId, Guid shotId, MovieKeyframeGenerationRequest request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots
            .Include(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.ProductionVersions)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null) return null;
        await collaboration.RequireAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken);
        if (request.SourceStoryboardVersionId == Guid.Empty)
            throw new MovieProductionValidationException("KEYFRAME_SOURCE_REQUIRED", "Approve a storyboard before generating a keyframe.");
        var source = await db.MovieProductionVersions.FirstOrDefaultAsync(item => item.Id == request.SourceStoryboardVersionId && item.MovieShotId == shotId, cancellationToken);
        var sourceError = MovieProductionWorkflow.ValidateVersionCreation(MovieProductionStages.ProductionKeyframe, source);
        if (source is null || sourceError is not null || source.Stage != MovieProductionStages.ApprovedStoryboard || source.Status != MovieProductionVersionStatuses.Approved)
            throw new MovieProductionValidationException("KEYFRAME_SOURCE_INVALID", sourceError ?? "Approve a storyboard before generating a keyframe.");
        var composition = string.IsNullOrWhiteSpace(request.CompositionJson) ? source.CompositionJson : request.CompositionJson.Trim();
        if (composition.Length > 20_000 || !MovieProductionWorkflow.IsJsonObject(composition))
            throw new MovieProductionValidationException("PRODUCTION_COMPOSITION_INVALID", "CompositionJson must be a JSON object of 20,000 characters or fewer.");
        if (request.RegenerationMetadataJson is { Length: > 8_000 } || request.RegenerationMetadataJson is not null && !MovieProductionWorkflow.IsJsonObject(request.RegenerationMetadataJson))
            throw new MovieProductionValidationException("PRODUCTION_REGENERATION_METADATA_INVALID", "RegenerationMetadataJson must be a JSON object of 8,000 characters or fewer.");

        var versionId = Guid.NewGuid();
        var description = $"Shot {shot.Sequence}: {shot.Description}. Composition: {composition}";
        if (description.Length > 4_000) description = description[..4_000];
        var imageInput = new ImageGenerationInput(
            description,
            ImageGenerationValues.Auto,
            shot.Scene.MovieProject.AspectRatio.Contains("9:16", StringComparison.OrdinalIgnoreCase) ? ImageGenerationValues.Portrait : shot.Scene.MovieProject.AspectRatio.Contains("1:1", StringComparison.OrdinalIgnoreCase) ? ImageGenerationValues.Square : ImageGenerationValues.Landscape,
            ImageGenerationValues.Standard,
            CleanBounded(request.Label, 120), null, null, null, shot.Scene.MovieProject.ProjectId, null, null, versionId);
        var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
        {
            WorkspaceId = shot.Scene.MovieProject.WorkspaceId,
            ProjectId = shot.Scene.MovieProject.ProjectId,
            JobType = GenerationJobTypes.ImageGenerate,
            Title = CleanBounded(request.Label, 160) ?? $"Shot {shot.Sequence} keyframe",
            InputJson = JsonSerializer.Serialize(imageInput),
        }, cancellationToken);
        var version = await CreateProductionVersionAsync(userId, shotId, new MovieProductionVersionRequest
        {
            VersionId = versionId,
            Stage = MovieProductionStages.ProductionKeyframe,
            SourceVersionId = source.Id,
            GenerationJobId = job.Id,
            Label = request.Label,
            CompositionJson = composition,
            RegenerationMetadataJson = request.RegenerationMetadataJson,
            StageProvenanceJson = JsonSerializer.Serialize(new { schemaVersion = 1, workflow = "storyboard_to_keyframe", sourceVersionId = source.Id, generationJobId = job.Id }),
        }, cancellationToken);
        return version is null ? null : new MovieKeyframeGenerationResponse(version, GenerationJobContractMapper.ToMovieDto(job));
    }

    public async Task<MovieProductionVersionDto?> SelectKeyframeAsync(Guid userId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await db.MovieProductionVersions
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.AssetReferences)
            .Include(item => item.GenerationJob).ThenInclude(job => job!.ProviderAttempts)
            .Include(item => item.GenerationJob).ThenInclude(job => job!.Assets)
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null) return null;
        await collaboration.RequireAsync(userId, version.MovieShot.Scene.MovieProjectId, MoviePermissions.Approve, cancellationToken);
        if (version.Stage != MovieProductionStages.ApprovedKeyframe || version.Status != MovieProductionVersionStatuses.Approved || !version.IsLocked)
            throw new MovieProductionValidationException("KEYFRAME_NOT_SELECTABLE", "Only an approved and locked keyframe can be selected.");
        var now = DateTime.UtcNow;
        version.MovieShot.SelectedKeyframeVersionId = version.Id;
        version.MovieShot.UpdatedAt = now;
        db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
        {
            Id = Guid.NewGuid(), MovieShotId = version.MovieShotId, MovieProductionVersionId = version.Id,
            FromStage = version.MovieShot.ProductionStage, ToStage = version.MovieShot.ProductionStage,
            EventType = "selected", Reason = "keyframe_selected", SourceVersionId = version.SourceVersionId,
            GenerationJobId = version.GenerationJobId, ActorUserId = userId, CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(version, true);
    }

    public async Task<MovieProductionVersionDto?> ReviewProductionVersionAsync(Guid userId, Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken)
    {
        var version = await db.MovieProductionVersions
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.AssetReferences)
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null) return null;
        await collaboration.RequireAsync(userId, version.MovieShot.Scene.MovieProjectId, MoviePermissions.Approve, cancellationToken);
        if (request.Approve && version.Stage == MovieProductionStages.ProductionRender) throw new MovieProductionValidationException("PRODUCTION_TAKE_REQUIRED", "Create and approve a canonical MovieTake before finalizing a rendered production artifact.");
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
            version.IsLocked = true;
            version.LockedAt = now;
            version.LockedByUserId = userId;
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

    public async Task<MovieProductionVersionDto?> CreateMotionPreviewAsync(Guid userId, Guid shotId, MovieProductionMotionPreviewRequest request, CancellationToken cancellationToken)
    {
        if (request.SourceVersionId == Guid.Empty)
            throw new MovieProductionValidationException("PRODUCTION_SOURCE_REQUIRED", "Approve a keyframe before creating a motion preview.");
        return await CreateProductionVersionAsync(userId, shotId, new MovieProductionVersionRequest
        {
            Stage = MovieProductionStages.MotionPreview,
            SourceVersionId = request.SourceVersionId,
            Label = request.Label,
            CompositionJson = request.CompositionJson,
            StageProvenanceJson = request.StageProvenanceJson,
        }, cancellationToken);
    }

    public async Task<MovieProductionRenderResponse?> QueueProductionRenderAsync(Guid userId, Guid shotId, MovieProductionRenderRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var queued = await productionOrchestrator.QueueApprovedAsync(userId, new MovieProductionGenerationRequest(
            shotId,
            request.SourceVersionId,
            request.Label,
            request.Title,
            request.TargetResolution ?? MovieResolutionTiers.P1080,
            request.QualityTier ?? DirectorQualityLevels.Standard,
            request.ConfirmationAccepted,
            idempotencyKey), cancellationToken);
        if (queued is null) return null;
        var project = await GetAsync(userId, queued.Clip.MovieProjectId, cancellationToken);
        if (project is null) throw new InvalidOperationException("Movie project disappeared.");
        return new MovieProductionRenderResponse(ToDto(queued.Version), GenerationJobContractMapper.ToMovieDto(queued.Job), queued.Clip.Id, project);
    }

    public async Task<MovieV2TakeDto?> CreateTakeFromProductionAsync(Guid userId, Guid versionId, MovieProductionTakeRequest request, CancellationToken cancellationToken)
    {
        var version = await db.MovieProductionVersions
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject)
            .Include(item => item.GenerationJob).ThenInclude(job => job!.ProviderAttempts)
            .Include(item => item.GenerationJob).ThenInclude(job => job!.Assets)
            .FirstOrDefaultAsync(item => item.Id == versionId, cancellationToken);
        if (version is null || !await collaboration.HasPermissionAsync(userId, version.MovieShot.Scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (version.Stage != MovieProductionStages.ProductionRender || version.GenerationJob is null)
            throw new MovieProductionValidationException("PRODUCTION_TAKE_INVALID", "A MovieTake can only be created from a production render.");
        if (version.GenerationJob.Status != GenerationJobStatus.Succeeded)
            throw new MovieProductionValidationException("PRODUCTION_RENDER_NOT_READY", "The production render must complete before it can become a MovieTake.");
        var clip = await db.MovieClips.FirstOrDefaultAsync(item => item.GenerationJobId == version.GenerationJobId && item.MovieShotId == version.MovieShotId, cancellationToken);
        var assetId = clip?.AssetId ?? version.GenerationJob.Assets.OrderByDescending(item => item.CreatedAt).Select(item => (Guid?)item.Id).FirstOrDefault();
        if (!assetId.HasValue)
            throw new MovieProductionValidationException("PRODUCTION_RENDER_OUTPUT_MISSING", "The completed render has no published Asset yet.");
        var outputAsset = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .FirstOrDefaultAsync(item => item.Id == assetId && item.WorkspaceId == version.MovieShot.Scene.MovieProject.WorkspaceId
                && item.ProjectId == version.MovieShot.Scene.MovieProject.ProjectId && item.SourceGenerationJobId == version.GenerationJobId
                && item.Status == AssetStatus.Active, cancellationToken);
        if (outputAsset is null || outputAsset.StoredFileId.HasValue && outputAsset.StoredFile?.Status != StoredFileStatus.Ready)
            throw new MovieProductionValidationException("PRODUCTION_RENDER_OUTPUT_INVALID", "The completed render output is not an active, ready asset for this movie project.");
        if (request.Notes is { Length: > 4_000 })
            throw new MovieProductionValidationException("PRODUCTION_TAKE_NOTES_TOO_LARGE", "Take notes must be 4,000 characters or fewer.");
        if (!MovieQualityLevels.Supported.Contains(request.QualityLevel.Trim()))
            throw new MovieProductionValidationException("PRODUCTION_TAKE_QUALITY_INVALID", "Choose a supported quality level.");
        var existingTake = await db.MovieTakes.FirstOrDefaultAsync(item => item.MovieShotId == version.MovieShotId && item.GenerationJobId == version.GenerationJobId, cancellationToken);
        if (existingTake is not null)
        {
            var savedAt = DateTime.UtcNow;
            existingTake.Label = string.IsNullOrWhiteSpace(request.Label) ? existingTake.Label : request.Label.Trim();
            existingTake.Status = MovieTakeStatuses.Succeeded;
            existingTake.QualityLevel = request.QualityLevel.Trim();
            existingTake.MovieProductionVersionId = version.Id;
            existingTake.AssetId = assetId;
            existingTake.Notes = MovieStudioHelpers.Clean(request.Notes);
            existingTake.StatusChangedAt = savedAt;
            existingTake.UpdatedAt = savedAt;
            existingTake.GenerationJob = version.GenerationJob;
            version.ResultingTake = existingTake;
            await db.SaveChangesAsync(cancellationToken);
            return MovieProductionProjection.ToTakeDto(existingTake);
        }
        var versionNumber = (await db.MovieTakes.Where(item => item.MovieShotId == version.MovieShotId).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1;
        var now = DateTime.UtcNow;
        var take = new MovieTake
        {
            Id = Guid.NewGuid(),
            MovieShotId = version.MovieShotId,
            VersionNumber = versionNumber,
            Label = string.IsNullOrWhiteSpace(request.Label) ? $"Take {versionNumber}" : request.Label.Trim(),
            Status = MovieTakeStatuses.Ready,
            QualityLevel = request.QualityLevel.Trim(),
            MovieClipId = clip?.Id,
            GenerationJobId = version.GenerationJobId,
            AssetId = assetId,
            GenerationJob = version.GenerationJob,
            Notes = MovieStudioHelpers.Clean(request.Notes),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieTakes.Add(take);
        await db.SaveChangesAsync(cancellationToken);
        return MovieProductionProjection.ToTakeDto(take);
    }

    public async Task<MovieSelectiveRegenerationResponse?> CreateRegenerationRequestAsync(Guid userId, Guid shotId, MovieRegenerationRequestInput request, CancellationToken cancellationToken)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).Include(item => item.ProductionVersions).ThenInclude(item => item.ResultingTake)
            .FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        ValidateRegenerationRequest(request);

        MovieProductionVersion? source = null;
        if (request.SourceVersionId.HasValue)
        {
            source = shot.ProductionVersions.FirstOrDefault(item => item.Id == request.SourceVersionId.Value);
            if (source is null) throw new MovieProductionValidationException("PRODUCTION_SOURCE_NOT_FOUND", "The source version does not belong to this shot.");
        }
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(request.RequestedStage.Trim(), source);
        if (workflowError is not null) throw new MovieProductionValidationException("PRODUCTION_STAGE_INVALID", workflowError);

        var movie = shot.Scene.MovieProject;
        var durationSeconds = Math.Clamp(shot.DurationSeconds ?? shot.Scene.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), 1, 3600);
        // The server owns the estimate. Client-supplied cost fields are not trusted for
        // cap enforcement because they could otherwise be lowered to bypass a cap.
        var estimate = await movieCostEstimator.EstimateAsync(
            new MovieGenerationCostRequest(durationSeconds, "1080p", "1080p", movie.QualityLevel, "native"),
            provider.Key,
            cancellationToken: cancellationToken);
        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            movie.WorkspaceId,
            movie.ProjectId,
            estimate.ToGenerationCostEstimate(),
            confirmationAccepted: false,
            cancellationToken: cancellationToken);
        if (!preflight.Allowed)
            throw new MovieProductionValidationException(preflight.RejectionCode!, preflight.RejectionMessage!);
        var now = DateTime.UtcNow;
        var regeneration = new MovieRegenerationRequest
        {
            Id = Guid.NewGuid(), MovieShotId = shotId, TargetId = shotId, ActionType = request.ActionType.Trim().ToLowerInvariant(),
            RequestedStage = request.RequestedStage.Trim(), Reason = request.Reason.Trim(), SourceVersionId = source?.Id,
            ChangedInputsJson = request.ChangedInputsJson.Trim(), CompositionJson = request.CompositionJson.Trim(),
            EstimatedProviderCostUsd = estimate.MaximumAmountUsd, EstimatedProviderCostKnown = estimate.IsEstimated && estimate.MaximumAmountUsd.HasValue,
            CostEstimateJson = estimate.ToJson(), Status = MovieRegenerationStatuses.PendingConfirmation, CreatedByUserId = userId, CreatedAt = now,
        };
        db.MovieRegenerationRequests.Add(regeneration);
        await db.SaveChangesAsync(cancellationToken);
        return await GetRegenerationRequestAsync(userId, regeneration.Id, cancellationToken);
    }

    public async Task<MovieSelectiveRegenerationResponse?> ConfirmRegenerationAsync(Guid userId, Guid requestId, MovieRegenerationConfirmationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var regeneration = await RegenerationQuery().FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (regeneration is null || !await collaboration.HasPermissionAsync(userId, regeneration.MovieShot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        if (regeneration.Status == MovieRegenerationStatuses.Confirmed) return await GetRegenerationRequestAsync(userId, requestId, cancellationToken);
        if (regeneration.Status != MovieRegenerationStatuses.PendingConfirmation)
            throw new MovieProductionValidationException("REGENERATION_STALE", "This regeneration request is no longer pending confirmation.");
        if (!request.Confirm) throw new MovieProductionValidationException("REGENERATION_CONFIRMATION_REQUIRED", "Explicit confirmation is required before queuing selective regeneration.");

        var shot = regeneration.MovieShot;
        var movie = shot.Scene.MovieProject;
        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            movie.WorkspaceId,
            movie.ProjectId,
            ParseMovieCostEstimate(regeneration.CostEstimateJson)?.ToGenerationCostEstimate() ?? GenerationCostEstimate.Unknown("pricing_unavailable"),
            confirmationAccepted: true,
            cancellationToken: cancellationToken);
        if (!preflight.Allowed)
            throw new MovieProductionValidationException(preflight.RejectionCode!, preflight.RejectionMessage!);
        var source = regeneration.SourceVersionId.HasValue ? await db.MovieProductionVersions.FirstOrDefaultAsync(item => item.Id == regeneration.SourceVersionId.Value && item.MovieShotId == shot.Id, cancellationToken) : null;
        var workflowError = MovieProductionWorkflow.ValidateVersionCreation(regeneration.RequestedStage, source);
        if (workflowError is not null) throw new MovieProductionValidationException("PRODUCTION_STAGE_INVALID", workflowError);
        var now = DateTime.UtcNow;
        var durationSeconds = Math.Clamp(shot.DurationSeconds ?? shot.Scene.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), 1, 3600);
        var estimate = await movieCostEstimator.EstimateAsync(
            new MovieGenerationCostRequest(durationSeconds, "1080p", "1080p", movie.QualityLevel, "native"),
            provider.Key,
            cancellationToken: cancellationToken);
        var refreshedPreflight = await costGuardrails.EvaluateAsync(
            userId,
            movie.WorkspaceId,
            movie.ProjectId,
            estimate.ToGenerationCostEstimate(),
            confirmationAccepted: true,
            cancellationToken: cancellationToken);
        if (!refreshedPreflight.Allowed)
            throw new MovieProductionValidationException(refreshedPreflight.RejectionCode!, refreshedPreflight.RejectionMessage!);
        regeneration.EstimatedProviderCostUsd = estimate.AmountUsd;
        regeneration.EstimatedProviderCostKnown = estimate.IsEstimated && estimate.AmountUsd.HasValue;
        regeneration.CostEstimateJson = estimate.ToJson();
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(), MovieProjectId = movie.Id, MovieSceneId = shot.Scene.Id, MovieShotId = shot.Id,
            Status = MovieClipStatuses.Queued, DurationSeconds = durationSeconds,
            ContinuitySnapshotJson = (await continuity.BuildSnapshotForTargetAsync(movie.Id, shot.Scene.Id, shot.Id, true, cancellationToken)).SnapshotJson, CreatedAt = now, UpdatedAt = now,
        };
        db.MovieClips.Add(clip);
        await db.SaveChangesAsync(cancellationToken);

        var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
        {
            WorkspaceId = movie.WorkspaceId, ProjectId = movie.ProjectId, JobType = GenerationJobTypes.MovieClipGenerate,
            Title = $"Selective {regeneration.ActionType} for {movie.Title}", EstimatedProviderCostUsd = regeneration.EstimatedProviderCostUsd,
            InternalCostEstimate = ParseMovieCostEstimate(regeneration.CostEstimateJson)?.ToGenerationCostEstimate(),
            InternalCostEstimateJson = regeneration.CostEstimateJson,
            ConfirmationAccepted = true,
            InputJson = JsonSerializer.Serialize(new MovieGenerationInput(
                MovieStudioOperations.SceneClip, movie.Id, clip.Id, shot.Scene.Id, shot.Id, shot.Description, clip.DurationSeconds!.Value,
                movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, clip.ContinuitySnapshotJson,
                SceneSnapshot(shot.Scene), ShotSnapshot(shot), WorldContextJson: await WorldContextSnapshotAsync(movie.Id, shot.Scene.Id, shot.Id, cancellationToken),
                SelectiveRegenerationId: regeneration.Id, SourceProductionVersionId: source?.Id, ChangedInputsJson: regeneration.ChangedInputsJson,
                SelectiveActionType: regeneration.ActionType, SelectiveReason: regeneration.Reason)),
        }, cancellationToken, idempotencyKey ?? $"movie-regeneration:{requestId:N}");
        clip.GenerationJobId = job.Id;

        var version = new MovieProductionVersion
        {
            Id = Guid.NewGuid(), MovieShotId = shot.Id, VersionNumber = (shot.ProductionVersions.Count == 0 ? 0 : shot.ProductionVersions.Max(item => item.VersionNumber)) + 1,
            Stage = regeneration.RequestedStage, Status = MovieProductionVersionStatuses.PendingApproval,
            Label = $"Selective {regeneration.ActionType}", CompositionJson = regeneration.CompositionJson,
            RegenerationMetadataJson = JsonSerializer.Serialize(new { regeneration.Id, target = new { regeneration.TargetType, regeneration.TargetId }, regeneration.ActionType, regeneration.Reason, regeneration.ChangedInputsJson, providerBoundary = "shot_clip_replacement_only" }),
            StageProvenanceJson = JsonSerializer.Serialize(new { sourceVersionId = source?.Id, generationJobId = job.Id, operation = MovieStudioOperations.SceneClip, providerBoundary = "No pixel-level or object-level editing; the provider receives a complete shot clip request." }),
            SourceVersionId = source?.Id, GenerationJobId = job.Id, CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        };
        var take = new MovieTake
        {
            Id = Guid.NewGuid(), MovieShotId = shot.Id, VersionNumber = (await db.MovieTakes.Where(item => item.MovieShotId == shot.Id).MaxAsync(item => (int?)item.VersionNumber, cancellationToken) ?? 0) + 1,
            Label = $"Selective {regeneration.ActionType}", Status = MovieTakeStatuses.Queued, QualityLevel = movie.QualityLevel,
            AutoDirectorEnabled = movie.AutoDirectorEnabled, MovieClipId = clip.Id, GenerationJobId = job.Id, MovieProductionVersionId = version.Id,
            CreatedAt = now, UpdatedAt = now,
        };
        version.ResultingTake = take;
        InvalidateDownstream(regeneration, shot, version, userId, now);
        db.MovieProductionVersions.Add(version);
        db.MovieTakes.Add(take);
        regeneration.Status = MovieRegenerationStatuses.Confirmed;
        regeneration.ConfirmedByUserId = userId;
        regeneration.ConfirmedAt = now;
        regeneration.GenerationJobId = job.Id;
        regeneration.ResultingProductionVersionId = version.Id;
        regeneration.ResultingTakeId = take.Id;
        shot.ProductionStage = regeneration.RequestedStage;
        shot.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return await GetRegenerationRequestAsync(userId, requestId, cancellationToken);
    }

    public async Task<MovieSelectiveRegenerationResponse?> GetRegenerationRequestAsync(Guid userId, Guid requestId, CancellationToken cancellationToken)
    {
        var regeneration = await RegenerationQuery().AsNoTracking().FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);
        if (regeneration is null || !await collaboration.HasPermissionAsync(userId, regeneration.MovieShot.Scene.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var estimate = ParseMovieCostEstimate(regeneration.CostEstimateJson)?.ToGenerationCostEstimate()
            ?? (regeneration.EstimatedProviderCostKnown && regeneration.EstimatedProviderCostUsd.HasValue
                ? new GenerationCostEstimate(true, regeneration.EstimatedProviderCostUsd.Value, UsageCurrencies.Usd, null, null, null, [])
                : GenerationCostEstimate.Unknown("pricing_unavailable"));
        var preflight = await costGuardrails.EvaluateAsync(
            userId,
            regeneration.MovieShot.Scene.MovieProject.WorkspaceId,
            regeneration.MovieShot.Scene.MovieProject.ProjectId,
            estimate,
            regeneration.Status == MovieRegenerationStatuses.Confirmed,
            cancellationToken);
        return ToSelectiveResponse(regeneration, GenerationCostPreviewMapper.ToDto(preflight));
    }

    private static void ValidateRegenerationRequest(MovieRegenerationRequestInput request)
    {
        if (MovieRegenerationWorkflow.ValidateAction(request.ActionType.Trim(), request.RequestedStage.Trim()) is { } error)
            throw new MovieProductionValidationException("REGENERATION_ACTION_INVALID", error);
        if (!request.SourceVersionId.HasValue)
            throw new MovieProductionValidationException("REGENERATION_SOURCE_REQUIRED", "Every selective regeneration must identify its source production version.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 2_000)
            throw new MovieProductionValidationException("REGENERATION_REASON_INVALID", "A regeneration reason between 1 and 2,000 characters is required.");
        if (string.IsNullOrWhiteSpace(request.ChangedInputsJson) || request.ChangedInputsJson.Length > 20_000 || !MovieRegenerationWorkflow.IsJsonObject(request.ChangedInputsJson) || !MovieRegenerationWorkflow.HasChangedInputs(request.ChangedInputsJson))
            throw new MovieProductionValidationException("REGENERATION_CHANGED_INPUTS_INVALID", "ChangedInputsJson must be a non-empty JSON object.");
        if (string.IsNullOrWhiteSpace(request.CompositionJson) || request.CompositionJson.Length > 20_000 || !MovieRegenerationWorkflow.IsJsonObject(request.CompositionJson))
            throw new MovieProductionValidationException("PRODUCTION_COMPOSITION_INVALID", "CompositionJson must be a JSON object of 20,000 characters or fewer.");
    }

    private void InvalidateDownstream(MovieRegenerationRequest regeneration, MovieShot shot, MovieProductionVersion newVersion, Guid userId, DateTime now)
    {
        var threshold = StageRank(regeneration.RequestedStage);
        foreach (var version in shot.ProductionVersions.Where(item => item.Id != newVersion.Id && StageRank(item.Stage) > threshold && item.Status is MovieProductionVersionStatuses.Approved or MovieProductionVersionStatuses.Selected or MovieProductionVersionStatuses.ReviewRequired))
        {
            version.Status = MovieProductionVersionStatuses.ReviewRequired;
            version.UpdatedAt = now;
            db.MovieProductionStageTransitions.Add(new MovieProductionStageTransition
            {
                Id = Guid.NewGuid(), MovieShotId = shot.Id, MovieProductionVersionId = version.Id, FromStage = version.Stage, ToStage = version.Stage,
                EventType = "invalidated", Reason = $"Upstream selective regeneration changed {regeneration.RequestedStage}.", MetadataJson = regeneration.ChangedInputsJson,
                SourceVersionId = regeneration.SourceVersionId, GenerationJobId = regeneration.GenerationJobId, ActorUserId = userId, CreatedAt = now,
            });
            if (version.ResultingTake is { } take)
            {
                take.Status = MovieTakeStatuses.ReviewRequired;
                take.UpdatedAt = now;
                if (shot.SelectedTakeId == take.Id) shot.SelectedTakeId = null;
                if (shot.FinalTakeId == take.Id) shot.FinalTakeId = null;
            }
        }
    }

    private IQueryable<MovieRegenerationRequest> RegenerationQuery() => db.MovieRegenerationRequests
        .Include(item => item.MovieShot).ThenInclude(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide)
        .Include(item => item.MovieShot).ThenInclude(item => item.ProductionVersions).ThenInclude(item => item.ResultingTake)
        .Include(item => item.SourceVersion)
        .Include(item => item.GenerationJob)
        .Include(item => item.ResultingProductionVersion).ThenInclude(item => item!.AssetReferences)
        .Include(item => item.ResultingTake).ThenInclude(item => item!.Approvals);

    public async Task<MovieStudioGenerationResponse?> GenerateSceneAsync(Guid userId, Guid movieProjectId, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var scene = await db.MovieScenes.Include(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == sceneId && item.MovieProjectId == movieProjectId, cancellationToken);
        if (scene is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        return await QueueClipAsync(userId, scene.MovieProject, scene, null, request, cancellationToken, idempotencyKey);
    }

    public async Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        var readiness = MovieShotReadiness.Evaluate(shot);
        if (!readiness.Ready) throw new MovieStudioValidationException($"Shot is not ready for generation. {readiness.Summary}");
        return await QueueClipAsync(userId, shot.Scene.MovieProject, shot.Scene, shot, request, cancellationToken, idempotencyKey);
    }

    public Task<MovieProviderReadinessDto> ProviderReadinessAsync() => Task.FromResult(new MovieProviderReadinessDto(provider.IsAvailable, provider.SupportedOperations.ToArray()));

    private async Task<MovieStudioGenerationResponse> QueueClipAsync(Guid userId, MovieProject movie, MovieScene scene, MovieShot? shot, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var targetShotId = shot?.Id;
            var existingClip = await db.MovieClips
                .Include(item => item.GenerationJob)
                .FirstOrDefaultAsync(item => item.MovieProjectId == movie.Id
                    && item.MovieSceneId == scene.Id
                    && item.MovieShotId == targetShotId
                    && item.GenerationJob != null
                    && item.GenerationJob.CreatedByUserId == userId
                    && item.GenerationJob.IdempotencyKey == idempotencyKey.Trim(), cancellationToken);
            if (existingClip?.GenerationJob is not null)
                return new MovieStudioGenerationResponse(
                    await GetAsync(userId, movie.Id, cancellationToken) ?? throw new InvalidOperationException("Movie project disappeared."),
                    GenerationJobContractMapper.ToMovieDto(existingClip.GenerationJob),
                    existingClip.Id);
        }
        var description = shot?.Description ?? scene.Summary;
        var durationSeconds = Math.Clamp(shot?.DurationSeconds ?? scene.DurationSeconds ?? Math.Min(movie.DurationSeconds, 60), 1, 3600);
        var estimate = await movieCostEstimator.EstimateAsync(
            new MovieGenerationCostRequest(
                durationSeconds,
                request.SourceResolution ?? "1080p",
                request.TargetResolution ?? "1080p",
                request.QualityTier ?? movie.QualityLevel,
                request.ProcessingPath ?? "native",
                request.RetryAttempts,
                request.UpscalingRequested,
                request.UpscalePasses),
            provider.Key,
            cancellationToken: cancellationToken);
        var now = DateTime.UtcNow;
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            MovieSceneId = scene.Id,
            MovieShotId = shot?.Id,
            Status = MovieClipStatuses.Queued,
            DurationSeconds = durationSeconds,
            ContinuitySnapshotJson = null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var continuitySnapshot = await continuity.BuildSnapshotForTargetAsync(movie.Id, scene.Id, shot?.Id, true, cancellationToken);
        clip.ContinuitySnapshotJson = continuitySnapshot.SnapshotJson;
        clip.ContinuitySnapshotId = continuitySnapshot.SnapshotId;
        clip.ContinuitySnapshotVersion = continuitySnapshot.Version;
        clip.ContinuitySnapshotHash = continuitySnapshot.SnapshotHash;
        db.MovieClips.Add(clip);
        await db.SaveChangesAsync(cancellationToken);
        var job = await jobs.CreateAsync(userId, new CreateGenerationJobRequest
        {
            WorkspaceId = movie.WorkspaceId,
            ProjectId = movie.ProjectId,
            JobType = shot is null ? GenerationJobTypes.MovieClipGenerate : GenerationJobTypes.MovieClipGenerate,
            Title = string.IsNullOrWhiteSpace(request.Title) ? movie.Title : request.Title.Trim(),
            EstimatedProviderCostUsd = estimate.MaximumAmountUsd,
            InternalCostEstimate = estimate.ToGenerationCostEstimate(),
            InternalCostEstimateJson = estimate.ToJson(),
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
                clip.ContinuitySnapshotJson,
                SceneSnapshot(scene),
                shot is null ? null : ShotSnapshot(shot),
                WorldContextJson: await WorldContextSnapshotAsync(movie.Id, scene.Id, shot?.Id, cancellationToken))),
        }, cancellationToken, idempotencyKey);
        // CreateAsync queues immediately; the worker may claim the job before this
        // context links the clip. Do not keep a stale tracked job row in this save.
        db.Entry(job).State = EntityState.Detached;
        clip.GenerationJobId = job.Id;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieStudioGenerationResponse(await GetAsync(userId, movie.Id, cancellationToken) ?? throw new InvalidOperationException("Movie project disappeared."), GenerationJobContractMapper.ToMovieDto(job), clip.Id);
    }

    private IQueryable<MovieProject> Query() => db.MovieProjects.AsNoTracking().AsSplitQuery()
        .Include(item => item.Guide)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.Clips)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.ProductionComplexityAssessments)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.ProductionVersions).ThenInclude(version => version.AssetReferences)
        .Include(item => item.Scenes).ThenInclude(scene => scene.Clips)
        .Include(item => item.Clips)
        .Include(item => item.Characters).ThenInclude(character => character.States).ThenInclude(state => state.ContinuityLocks)
        .Include(item => item.Characters).ThenInclude(character => character.ReferenceAssets)
        .Include(item => item.Characters).ThenInclude(character => character.Relationships).ThenInclude(relationship => relationship.RelatedCharacter)
        .Include(item => item.Characters).ThenInclude(character => character.ContinuityLocks)
        .Include(item => item.Locations)
        .Include(item => item.Sets).ThenInclude(item => item.Variations)
        .Include(item => item.Props)
        .Include(item => item.WorldReferences)
        .Include(item => item.WorldUsages)
        .Include(item => item.ContinuityFacts)
        .Include(item => item.ContinuityLocks)
        .Include(item => item.Assemblies);
    private IQueryable<MovieCharacter> CastQuery() => db.MovieCharacters.AsNoTracking()
        .Include(item => item.MovieProject)
        .Include(item => item.States).ThenInclude(state => state.ContinuityLocks)
        .Include(item => item.ReferenceAssets)
        .Include(item => item.Relationships).ThenInclude(relationship => relationship.RelatedCharacter)
        .Include(item => item.ContinuityLocks);

    private IQueryable<MovieScene> PlanningQuery() => db.MovieScenes.AsNoTracking()
        .Include(item => item.Shots).ThenInclude(shot => shot.Clips)
        .Include(item => item.Shots).ThenInclude(shot => shot.ProductionComplexityAssessments)
        .Include(item => item.Shots).ThenInclude(shot => shot.ProductionVersions).ThenInclude(version => version.AssetReferences);

    private IQueryable<MovieShot> PlanningShotQuery() => db.MovieShots
        .Include(item => item.Scene).ThenInclude(scene => scene.MovieProject)
        .Include(item => item.Scene).ThenInclude(scene => scene.MovieProject).ThenInclude(project => project.Guide)
        .Include(item => item.Clips)
        .Include(item => item.ProductionComplexityAssessments)
        .Include(item => item.ProductionVersions).ThenInclude(version => version.AssetReferences);

    private IQueryable<MovieShot> ProductionQuery() => db.MovieShots.AsNoTracking()
        .Include(item => item.Scene).ThenInclude(scene => scene.MovieProject)
        .Include(item => item.ProductionVersions).ThenInclude(version => version.AssetReferences)
        .Include(item => item.ProductionVersions).ThenInclude(version => version.GenerationJob).ThenInclude(job => job!.ProviderAttempts)
        .Include(item => item.ProductionVersions).ThenInclude(version => version.GenerationJob).ThenInclude(job => job!.Assets)
        .Include(item => item.Takes).ThenInclude(take => take.Approvals)
        .Include(item => item.Takes).ThenInclude(take => take.GenerationJob).ThenInclude(job => job!.ProviderAttempts)
        .Include(item => item.Takes).ThenInclude(take => take.GenerationJob).ThenInclude(job => job!.Assets)
        .Include(item => item.ProductionTransitions)
        .Include(item => item.RegenerationRequests).ThenInclude(request => request.GenerationJob)
        .Include(item => item.RegenerationRequests).ThenInclude(request => request.ResultingProductionVersion).ThenInclude(version => version!.AssetReferences)
        .Include(item => item.RegenerationRequests).ThenInclude(request => request.ResultingTake).ThenInclude(take => take!.Approvals);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt, movie.Guide.CurrentRevisionNumber, movie.Guide.LockedRevisionNumber, movie.Guide.LockedAt, ToCinematographyBible(movie.Guide)), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray(), scene.Clips.Where(clip => clip.MovieShotId is null).Select(ToDto).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Clips.Where(clip => clip.MovieSceneId is null).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray(), MovieWorld(movie));

    private static MovieWorldDto MovieWorld(MovieProject movie) => new(
        movie.Locations.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.Sets.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.Props.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.WorldReferences.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.WorldUsages.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.ContinuityFacts.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(),
        movie.ContinuityLocks.OrderByDescending(item => item.CreatedAt).Select(ToDto).ToArray());

    private static MovieCastProjectDto ToCastProjectDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.CreatedAt, movie.UpdatedAt);
    private static MovieCastCharacterDto ToCastCharacterDto(MovieCharacter character)
    {
        var latestState = character.States.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.CreatedAt).Select(ToDto).FirstOrDefault();
        var locks = character.ContinuityLocks.Concat(character.States.SelectMany(item => item.ContinuityLocks)).ToArray();
        return new MovieCastCharacterDto(character.Id, character.Name, character.Role, character.Description, character.Appearance, character.ReferenceAssetId,
            character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToArray(), character.ReferenceAssets.Count, character.States.Count,
            latestState, character.Relationships.Count, character.Relationships.Select(item => item.RelationshipType).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToArray(),
            locks.Length, locks.Select(item => item.FieldKey).Distinct(StringComparer.Ordinal).OrderBy(item => item).ToArray(), character.UpdatedAt);
    }

    private async Task<MovieProject?> GetAuthorizedMovieAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var movie = await db.MovieProjects.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken) ? null : movie;
    }

    private async Task EnsureMovieAssetAsync(Guid? assetId, Guid workspaceId, Guid? projectId, CancellationToken cancellationToken)
    {
        if (!assetId.HasValue) return;
        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile)
            .FirstOrDefaultAsync(item => item.Id == assetId && item.WorkspaceId == workspaceId, cancellationToken);
        if (asset is null || asset.Status != AssetStatus.Active || asset.ProjectId.HasValue && asset.ProjectId != projectId)
            throw new MovieStudioValidationException("The reference asset must be active and belong to this movie project or its workspace.");
        if (asset.StoredFileId.HasValue && asset.StoredFile?.Status != StoredFileStatus.Ready)
            throw new MovieStudioValidationException("The reference asset file must be ready before it can be used.");
    }

    private async Task EnsureWorldLocksAllowAsync(Guid movieProjectId, string entityType, Guid entityId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        var locks = await db.MovieContinuityLocks.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId && item.EntityType == entityType && item.EntityId == entityId && item.ReleasedAt == null)
            .ToArrayAsync(cancellationToken);
        foreach (var locked in locks)
        {
            if (values.TryGetValue(locked.FieldName, out var next) && !string.Equals(next, locked.LockedValue, StringComparison.Ordinal))
                throw new MovieStudioContinuityLockException(locked.FieldName);
        }
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
    private static void ValidateWorldText(string? value, int maxLength, string field)
    {
        if (value is not null && value.Trim().Length > maxLength) throw new MovieStudioValidationException($"{field} must be {maxLength} characters or fewer.");
    }
    private static MovieCinematographyBibleDto? ToCinematographyBible(MovieContinuityGuide guide)
    {
        var selection = CinematographyIntentValidator.FromJson(guide.CinematographyBibleReferencesJson);
        if (selection is null && string.IsNullOrWhiteSpace(guide.CinematographyIntent)) return null;
        return new MovieCinematographyBibleDto(guide.CinematographyIntent ?? selection?.Intent, selection?.PresetId, selection?.Notes, selection?.CapabilityReferences ?? []);
    }

    private static string ContinuitySnapshot(MovieContinuityGuide guide) => JsonSerializer.Serialize(new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.CinematographyIntent, guide.CinematographyBibleReferencesJson, guide.UpdatedAt });

    private async Task<string> WorldContextSnapshotAsync(Guid movieProjectId, Guid? sceneId, Guid? shotId, CancellationToken cancellationToken)
    {
        var snapshot = await worldContinuity.ProjectAsync(movieProjectId, sceneId, shotId, cancellationToken);
        return snapshot?.ToJson() ?? "{}";
    }
    private static string SceneSnapshot(MovieScene scene) => JsonSerializer.Serialize(new { scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue });
    private static string ShotSnapshot(MovieShot shot)
    {
        var profile = MovieShotQualityRequirementsPlanner.TryParse(shot.QualityRequirementsJson, out var stored) && stored is not null
            ? stored
            : MovieShotQualityRequirementsPlanner.PlanWithAvailableContext(shot);
        return JsonSerializer.Serialize(new
        {
            shot.Id, shot.Sequence, shot.Description, shot.Purpose, shot.Subjects,
            subjectCharacterIds = MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson),
            shot.LocationSet, shot.DurationSeconds, shot.ProductionRequirements, shot.ContinuityReferences,
            shot.CameraAndFraming, shot.CameraMotion, shot.CinematographyJson, shot.Narration, shot.Dialogue,
            shot.VisualContinuityNotes, productionContract = MovieShotProductionContractProjection.FromShot(shot),
            qualityRequirements = profile.QualityRequirements,
            adaptiveResolutionDirectorInput = profile.AdaptiveResolutionDirectorInput,
        });
    }

    private static string SerializeQualityProfile(MovieShot shot, MovieShotQualityPlanningContext context, MovieShotQualityRequirements? productionRequirements)
    {
        var profileJson = MovieShotQualityRequirementsPlanner.Serialize(MovieShotQualityRequirementsPlanner.Plan(shot, context));
        if (productionRequirements is null) return profileJson;
        var root = JsonNode.Parse(profileJson)?.AsObject() ?? throw new InvalidOperationException("Quality profile serialization failed.");
        root["productionContractQualityRequirements"] = JsonSerializer.SerializeToNode(productionRequirements, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static MovieShotQualityPlanningContext QualityPlanningContext(MovieProject movie, MovieScene scene, MovieShot shot) =>
        new(movie.Id, scene.Id, movie.Title, movie.Description, movie.Style,
            movie.Guide?.VisualLanguage, movie.Guide?.CameraLanguage, movie.Guide?.ColorAndLighting,
            movie.Guide?.ContinuityRules, scene.Summary,
            MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson).Select(id => $"character:{id:N}").ToArray(),
            string.IsNullOrWhiteSpace(shot.LocationSet) ? [] : [shot.LocationSet!]);
    private async Task<MovieCharacterDto?> GetCharacterDtoAsync(Guid userId, Guid characterId, CancellationToken cancellationToken)
    {
        var movie = await Query().FirstOrDefaultAsync(item => item.Characters.Any(character => character.Id == characterId), cancellationToken);
        var character = movie?.Characters.FirstOrDefault(item => item.Id == characterId);
        return movie is null || character is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : ToDto(character);
    }

    private async Task<MovieCharacter?> GetCharacterForUserAsync(Guid userId, Guid characterId, CancellationToken cancellationToken)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        return character is null || !await collaboration.HasPermissionAsync(userId, character.MovieProjectId, MoviePermissions.Edit, cancellationToken) ? null : character;
    }

    private async Task ValidateReferenceAssetsAsync(Guid workspaceId, Guid? projectId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken)
    {
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        var valid = await db.Assets.CountAsync(item => ids.Contains(item.Id) && item.WorkspaceId == workspaceId && item.Status == AssetStatus.Active && (!item.ProjectId.HasValue || item.ProjectId == projectId), cancellationToken);
        if (valid != ids.Length) throw new MovieStudioValidationException("Every reference asset must belong to the movie workspace.");
    }

    private static void ValidateState(MovieStudioCharacterStateRequest request, bool requireKey)
    {
        if (requireKey && string.IsNullOrWhiteSpace(request.Key)) throw new MovieStudioValidationException("Character state key is required.");
        if (request.Key.Trim().Length > 120) throw new MovieStudioValidationException("Character state key is too long.");
    }

    private static void EnsureCardLocksAllow(MovieCharacter character, MovieStudioCharacterRequest request)
    {
        foreach (var locked in character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null))
        {
            var next = CardFieldValue(request, locked.FieldKey);
            if (!string.Equals(next, locked.LockedValue, StringComparison.Ordinal)) throw new MovieStudioContinuityLockException(locked.FieldKey);
        }
    }

    private static string? CardFieldValue(MovieCharacter character, string fieldKey) => fieldKey switch
    {
        "name" => character.Name, "role" => character.Role, "description" => character.Description, "appearance" => character.Appearance,
        "physicalDescription" => character.PhysicalDescription, "wardrobe" => character.Wardrobe, "voiceReference" => character.VoiceReference,
        "personalityAndStoryNotes" => character.PersonalityAndStoryNotes, "voiceAndPerformance" => character.VoiceAndPerformance,
        "continuityNotes" => character.ContinuityNotes, _ => null,
    };

    private static string? CardFieldValue(MovieStudioCharacterRequest request, string fieldKey) => fieldKey switch
    {
        "name" => request.Name.Trim(), "role" => MovieStudioHelpers.Clean(request.Role), "description" => request.Description.Trim(), "appearance" => MovieStudioHelpers.Clean(request.Appearance),
        "physicalDescription" => MovieStudioHelpers.Clean(request.PhysicalDescription), "wardrobe" => MovieStudioHelpers.Clean(request.Wardrobe), "voiceReference" => MovieStudioHelpers.Clean(request.VoiceReference),
        "personalityAndStoryNotes" => MovieStudioHelpers.Clean(request.PersonalityAndStoryNotes), "voiceAndPerformance" => MovieStudioHelpers.Clean(request.VoiceAndPerformance),
        "continuityNotes" => MovieStudioHelpers.Clean(request.ContinuityNotes), _ => null,
    };

    private static string? StateFieldValue(MovieCharacterState state, string fieldKey) => fieldKey switch
    {
        "label" => state.Label, "wardrobe" => state.Wardrobe, "ageOrTimeState" => state.AgeOrTimeState, "appearance" => state.Appearance,
        "injuryOrCondition" => state.InjuryOrCondition, "locationOrStoryState" => state.LocationOrStoryState, "continuityNotes" => state.ContinuityNotes, _ => null,
    };

    private static void EnsureStateLocksAllow(MovieCharacterState state, MovieStudioCharacterStateRequest request)
    {
        foreach (var locked in state.ContinuityLocks)
        {
            var next = locked.FieldKey switch
            {
                "label" => MovieStudioHelpers.Clean(request.Label), "wardrobe" => MovieStudioHelpers.Clean(request.Wardrobe), "ageOrTimeState" => MovieStudioHelpers.Clean(request.AgeOrTimeState),
                "appearance" => MovieStudioHelpers.Clean(request.Appearance), "injuryOrCondition" => MovieStudioHelpers.Clean(request.InjuryOrCondition), "locationOrStoryState" => MovieStudioHelpers.Clean(request.LocationOrStoryState),
                "continuityNotes" => MovieStudioHelpers.Clean(request.ContinuityNotes), _ => null,
            };
            if (!string.Equals(next, locked.LockedValue, StringComparison.Ordinal)) throw new MovieStudioContinuityLockException(locked.FieldKey);
        }
    }

    private static MovieSceneDto ToDto(MovieScene scene, IReadOnlyList<MovieShotDto> shots, IReadOnlyList<MovieClipDto> clips) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue, shots, clips);
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips)
    {
        var profile = MovieShotQualityRequirementsPlanner.TryParse(shot.QualityRequirementsJson, out var stored) && stored is not null
            ? stored
            : MovieShotQualityRequirementsPlanner.PlanWithAvailableContext(shot);
        return new MovieShotDto(shot.Id, shot.Sequence, shot.Description, shot.Purpose, shot.Subjects, MovieShotReadiness.ParseSubjectCharacterIds(shot.SubjectCharacterIdsJson), shot.LocationSet, shot.DurationSeconds, shot.ProductionRequirements, shot.ContinuityReferences, shot.CameraAndFraming, shot.CameraMotion, shot.CinematographyJson, MovieShotReadiness.CinematographySummary(shot), shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, shot.Status, MovieShotReadiness.PlanState(shot), MovieShotReadiness.Evaluate(shot), shot.ProductionStage, clips, shot.ProductionVersions.OrderByDescending(item => item.VersionNumber).Select(item => ToDto(item)).ToArray(), shot.Takes.OrderBy(item => item.VersionNumber).Select(MovieProductionProjection.ToTakeDto).ToArray(), MovieShotProductionContractProjection.FromShot(shot), shot.ProductionComplexityAssessments.OrderByDescending(item => item.Version).Select(MovieProductionComplexityProjection.ToDto).FirstOrDefault(), profile.QualityRequirements, profile.AdaptiveResolutionDirectorInput, CinematographyShotPlanValidator.FromJson(shot.CinematographyJson), shot.SelectedKeyframeVersionId);
    }
    private static MovieSceneShotPlanDto ToShotPlanDto(MovieScene scene)
    {
        var shots = scene.Shots.OrderBy(item => item.Sequence).Select(item => ToDto(item, item.Clips.OrderBy(clip => clip.CreatedAt).Select(ToDto).ToArray())).ToArray();
        var active = scene.Shots.Where(item => item.Status != MovieShotStatuses.Archived).ToArray();
        var totalDuration = active.Sum(item => item.DurationSeconds ?? 0);
        var coverage = scene.DurationSeconds is > 0 ? Math.Min(100, (int)Math.Round(totalDuration * 100d / scene.DurationSeconds.Value)) : 0;
        return new MovieSceneShotPlanDto(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.Status, shots.Length, active.Length, active.Count(item => MovieShotReadiness.Evaluate(item).Ready), totalDuration, coverage, shots);
    }
    private static MovieCharacterDto ToDto(MovieCharacter character) => new(character.Id, character.Name, character.Role, character.Description, character.Appearance, character.PhysicalDescription, character.Wardrobe, character.VoiceReference, character.PersonalityAndStoryNotes, character.VoiceAndPerformance, character.ContinuityNotes, character.ReferenceAssetId, character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToArray(), character.States.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(), character.Relationships.OrderBy(item => item.CreatedAt).Select(item => new MovieCharacterRelationshipDto(item.Id, item.RelatedCharacterId, item.RelatedCharacter?.Name ?? string.Empty, item.RelationshipType, item.Notes)).ToArray(), character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null).OrderBy(item => item.ApprovedAt).Select(ToDto).Concat(character.States.SelectMany(item => item.ContinuityLocks).OrderBy(item => item.ApprovedAt).Select(ToDto)).ToArray());
    private static MovieCharacterStateDto ToDto(MovieCharacterState state) => new(state.Id, state.Key, state.Label, state.Wardrobe, state.AgeOrTimeState, state.Appearance, state.InjuryOrCondition, state.LocationOrStoryState, state.ContinuityNotes, state.CreatedAt, state.UpdatedAt);
    private static MovieCharacterContinuityLockDto ToDto(MovieCharacterContinuityLock lockEntity) => new(lockEntity.Id, lockEntity.FieldKey, lockEntity.LockedValue, lockEntity.MovieCharacterStateId, lockEntity.ApprovedAt);
    private static MovieLocationDto ToDto(MovieLocation location) => new(location.Id, location.Name, location.Description, location.VisualContinuityNotes, location.ReferenceAssetId);
    private static MovieSetDto ToDto(MovieSet item) => new(item.Id, item.MovieLocationId, item.Name, item.Description, item.EnvironmentType, item.VisualDescription, item.TimeOfDay, item.Weather, item.ContinuityNotes, item.ReferenceAssetId, item.Variations.OrderBy(variation => variation.CreatedAt).Select(ToDto).ToArray());
    private static MovieSetVariationDto ToDto(MovieSetVariation item) => new(item.Id, item.MovieSetId, item.Name, item.VisualDescription, item.TimeOfDay, item.Weather, item.Lighting, item.ContinuityNotes, item.ReferenceAssetId, item.IsDefault);
    private static MoviePropDto ToDto(MovieProp item) => new(item.Id, item.Name, item.Description, item.Category, item.ContinuityNotes, item.ReferenceAssetId);
    private static MovieWorldReferenceDto ToDto(MovieWorldReference item) => new(item.Id, item.Name, item.Kind, item.Description, item.TagsJson, item.AssetId);
    private static MovieWorldUsageDto ToDto(MovieWorldUsage item) => new(item.Id, item.MovieSceneId, item.MovieShotId, item.EntityType, item.EntityId, item.Role);
    private static MovieContinuityFactDto ToDto(MovieContinuityFact item) => new(item.Id, item.ScopeType, item.ScopeId, item.FactKey, item.FactValue, item.Notes, item.UpdatedAt);
    private static MovieContinuityLockDto ToDto(MovieContinuityLock item) => new(item.Id, item.EntityType, item.EntityId, item.FieldName, item.LockedValue, item.Strength, item.Reason, item.CreatedAt, item.ReleasedAt);
    private static MovieClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieSceneId, clip.MovieShotId, clip.GenerationJobId, clip.AssetId, clip.Status, clip.DurationSeconds, clip.MetadataJson, clip.ContinuitySnapshotJson, clip.ContinuitySnapshotId, clip.ContinuitySnapshotVersion, clip.ContinuitySnapshotHash);
    private static MovieAssemblyDto ToDto(MovieAssembly assembly) => new(assembly.Id, assembly.GenerationJobId, assembly.AssetId, assembly.Status, assembly.OutputFormat, assembly.MetadataJson, assembly.CreatedAt, assembly.CompletedAt);
    private static MovieShotProductionDto ToProductionDto(MovieShot shot, MovieWorldContinuitySnapshotDto? worldContinuity = null) => new(shot.Id, shot.ProductionStage, shot.ProductionVersions.OrderByDescending(item => item.VersionNumber).Select(item => ToDto(item, shot.SelectedKeyframeVersionId == item.Id)).ToArray(), shot.ProductionTransitions.OrderBy(item => item.CreatedAt).Select(item => new MovieProductionStageTransitionDto(item.Id, item.MovieShotId, item.MovieProductionVersionId, item.FromStage, item.ToStage, item.EventType, item.Reason, item.MetadataJson, item.SourceVersionId, item.GenerationJobId, item.ActorUserId, item.CreatedAt)).ToArray(), worldContinuity, shot.Takes.OrderBy(item => item.VersionNumber).Select(MovieProductionProjection.ToTakeDto).ToArray(), null!, MovieShotProductionContractProjection.FromShot(shot), shot.SelectedKeyframeVersionId);
    private static MovieProductionVersionDto ToDto(MovieProductionVersion version, bool isSelected = false)
    {
        var generatedAssetId = version.AssetId ?? version.GenerationJob?.Assets.OrderByDescending(item => item.CreatedAt).Select(item => (Guid?)item.Id).FirstOrDefault();
        return new(version.Id, version.MovieShotId, version.VersionNumber, version.Stage, version.Status, version.Label, version.CompositionJson, version.RegenerationMetadataJson, version.StageProvenanceJson, version.ContinuitySnapshotReferenceJson, version.CinematographyReferenceJson, version.SourceVersionId, version.GenerationJobId, generatedAssetId, version.FirstFrameAssetId, version.LastFrameAssetId, version.FirstFrameNotes, version.LastFrameNotes, version.RejectionReason, version.ContinuitySnapshotId, version.ContinuitySnapshotVersion, version.ContinuitySnapshotHash, version.CreatedAt, version.UpdatedAt, version.ReviewedAt, version.AssetReferences.OrderBy(item => item.Role).Select(item => new MovieProductionAssetReferenceDto(item.AssetId, item.Role)).ToArray(), MovieProductionProjection.ToExecution(version.GenerationJob), version.IsLocked, version.LockedAt, version.LockedByUserId, isSelected);
    }
    private async Task ValidateSubjectCharacterIdsAsync(IEnumerable<Guid>? ids, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var distinct = (ids ?? []).Distinct().ToArray();
        if (distinct.Length == 0) return;
        var count = await db.MovieCharacters.CountAsync(item => item.MovieProjectId == movieProjectId && distinct.Contains(item.Id), cancellationToken);
        if (count != distinct.Length) throw new MovieStudioValidationException("Every shot subject character must belong to this movie project.");
    }

    private static string? ShotText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength) throw new MovieStudioValidationException($"A shot planning field cannot exceed {maxLength} characters.");
        return normalized;
    }

    private static MovieStoryboardShotDto ToStoryboardShot(MovieShot shot)
    {
        var candidates = shot.ProductionVersions
            .Where(version => version.Stage is MovieProductionStages.StoryboardCandidate or MovieProductionStages.ApprovedStoryboard)
            .OrderByDescending(version => version.VersionNumber)
            .Select(ToStoryboardCandidate)
            .ToArray();
        var approved = candidates.FirstOrDefault(item => item.Stage == MovieProductionStages.ApprovedStoryboard && item.Status == MovieProductionVersionStatuses.Approved);
        var approvalStatus = approved is not null
            ? "Approved"
            : candidates.Any(item => item.Status == MovieProductionVersionStatuses.PendingApproval)
                ? "PendingApproval"
                : candidates.Any(item => item.Status == MovieProductionVersionStatuses.Rejected)
                    ? "Rejected"
                    : "NotStarted";
        return new MovieStoryboardShotDto(shot.Id, shot.Sequence, shot.Description, shot.Status, shot.ProductionStage, shot.DurationSeconds, ContinuityWarnings(shot, candidates), CinematographySummary(shot), candidates, approved?.Id, approvalStatus);
    }
    private static MovieStoryboardCandidateDto ToStoryboardCandidate(MovieProductionVersion version) => new(version.Id, version.MovieShotId, version.VersionNumber, version.Stage, version.Status, version.Label, version.CompositionJson, version.RegenerationMetadataJson, version.StageProvenanceJson, version.SourceVersionId, version.AssetId, version.FirstFrameAssetId, version.LastFrameAssetId, version.FirstFrameNotes, version.LastFrameNotes, version.RejectionReason, version.CreatedAt, version.UpdatedAt, version.ReviewedAt, version.AssetReferences.OrderBy(item => item.Role).Select(item => new MovieProductionAssetReferenceDto(item.AssetId, item.Role)).ToArray());
    private static IReadOnlyList<string> ContinuityWarnings(MovieShot shot, IReadOnlyList<MovieStoryboardCandidateDto> candidates)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(shot.VisualContinuityNotes)) warnings.Add("No shot-specific continuity note recorded.");
        if (string.IsNullOrWhiteSpace(shot.CameraAndFraming)) warnings.Add("Camera and framing are not specified.");
        if (candidates.FirstOrDefault()?.Status == MovieProductionVersionStatuses.Rejected && !string.IsNullOrWhiteSpace(candidates[0].RejectionReason)) warnings.Add($"Latest storyboard candidate rejected: {candidates[0].RejectionReason}");
        return warnings;
    }
    private static MovieCinematographySummaryDto CinematographySummary(MovieShot shot)
    {
        var selection = CinematographyIntentValidator.FromJson(shot.CinematographyJson);
        return new MovieCinematographySummaryDto(shot.CameraAndFraming, shot.CameraMotion, selection?.Intent, selection?.ShotSize, selection?.FocalLength, selection?.CameraAngle, selection?.Lighting, selection?.PaletteLook, selection?.CompositionNotes);
    }
    private async Task<string> ContinuitySnapshotReferenceAsync(Guid movieProjectId, Guid sceneId, Guid shotId, CancellationToken cancellationToken)
    {
        var guide = await db.MovieContinuityGuides.AsNoTracking().FirstAsync(item => item.MovieProjectId == movieProjectId, cancellationToken);
        var characterIds = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).Select(item => item.Id).ToArrayAsync(cancellationToken);
        var characterReferenceAssets = await db.MovieCharacterReferenceAssets.AsNoTracking().Where(item => characterIds.Contains(item.MovieCharacterId)).Select(item => new { item.MovieCharacterId, item.AssetId, item.SortOrder }).ToArrayAsync(cancellationToken);
        var worldUsages = await db.MovieWorldUsages.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.MovieSceneId == sceneId && (item.MovieShotId == null || item.MovieShotId == shotId)).Select(item => new { item.Id, item.EntityType, item.EntityId, item.Role }).ToArrayAsync(cancellationToken);
        var continuityLocks = await db.MovieContinuityLocks.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId && item.ReleasedAt == null).Select(item => item.Id).ToArrayAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            movieProjectId,
            sceneId,
            shotId,
            guideId = guide.Id,
            guideRevision = guide.CurrentRevisionNumber,
            lockedGuideRevision = guide.LockedRevisionNumber,
            characterIds,
            characterReferenceAssets,
            worldUsageIds = worldUsages.Select(item => item.Id).ToArray(),
            worldReferences = worldUsages.Select(item => new { item.EntityType, item.EntityId, item.Role }).ToArray(),
            continuityLockIds = continuityLocks,
            capturedAtUtc = DateTime.UtcNow,
        });
    }

    private static string CinematographyReference(MovieShot shot) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        shotId = shot.Id,
        source = "movie-shot",
        shot.CameraAndFraming,
        shot.CameraMotion,
        shot.CinematographyJson,
    });

    private static MovieRegenerationRequestDto ToRegenerationDto(MovieRegenerationRequest request, GenerationCostPreviewDto? guardrails = null)
    {
        var estimate = ParseMovieCostEstimate(request.CostEstimateJson);
        return new MovieRegenerationRequestDto(
            request.Id, request.MovieShotId, request.TargetType, request.TargetId, request.ActionType, request.RequestedStage,
            request.Reason, request.SourceVersionId, request.ChangedInputsJson, request.CompositionJson, request.Status,
            request.CreatedByUserId, request.ConfirmedByUserId, request.GenerationJobId, request.ResultingProductionVersionId,
            request.ResultingTakeId, request.CreatedAt, request.ConfirmedAt,
            new MovieRegenerationCostPreviewDto(
                request.EstimatedProviderCostUsd,
                request.EstimatedProviderCostKnown,
                estimate?.Currency ?? UsageCurrencies.Usd,
                request.CostEstimateJson,
                true,
                estimate?.State ?? MovieGenerationCostEstimateStates.Unevaluated,
                estimate?.MinimumAmountUsd,
                estimate?.MaximumAmountUsd,
                guardrails));
    }

    private static MovieGenerationCostEstimate? ParseMovieCostEstimate(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<MovieGenerationCostEstimate>(json); }
        catch (JsonException) { return null; }
    }
    private static MovieSelectiveRegenerationResponse ToSelectiveResponse(MovieRegenerationRequest request, GenerationCostPreviewDto? guardrails = null) => new(ToRegenerationDto(request, guardrails), request.GenerationJob is null ? null : GenerationJobContractMapper.ToMovieDto(request.GenerationJob), request.ResultingProductionVersion is null ? null : ToDto(request.ResultingProductionVersion), request.ResultingTake is null ? null : MovieProductionProjection.ToTakeDto(request.ResultingTake));
    private static void AddAsset(IDictionary<Guid, string> assets, Guid? assetId, string role)
    {
        if (!assetId.HasValue) return;
        if (assets.TryGetValue(assetId.Value, out var existingRole) && !string.Equals(existingRole, role, StringComparison.OrdinalIgnoreCase))
            throw new MovieProductionValidationException("PRODUCTION_ASSET_ROLE_CONFLICT", "An asset cannot be assigned conflicting production roles.");
        assets[assetId.Value] = role;
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
public sealed class MovieStudioContinuityLockException(string fieldKey) : Exception($"The approved continuity lock for '{fieldKey}' cannot be changed.");
public static class CharacterFieldKeys
{
    public static readonly IReadOnlySet<string> Card = new HashSet<string>(StringComparer.Ordinal) { "name", "role", "description", "appearance", "physicalDescription", "wardrobe", "voiceReference", "personalityAndStoryNotes", "voiceAndPerformance", "continuityNotes" };
    public static readonly IReadOnlySet<string> State = new HashSet<string>(StringComparer.Ordinal) { "label", "wardrobe", "ageOrTimeState", "appearance", "injuryOrCondition", "locationOrStoryState", "continuityNotes" };
}

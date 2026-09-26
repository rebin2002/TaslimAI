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
    Task<MovieCharacterDto?> UpdateCharacterAsync(Guid userId, Guid characterId, MovieStudioCharacterRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterStateDto?> AddCharacterStateAsync(Guid userId, Guid characterId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterStateDto?> UpdateCharacterStateAsync(Guid userId, Guid stateId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterRelationshipDto?> AddCharacterRelationshipAsync(Guid userId, Guid characterId, MovieStudioCharacterRelationshipRequest request, CancellationToken cancellationToken);
    Task<MovieCharacterContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid characterId, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken);
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
                    movie.Style, movie.Language, movie.AdditionalInstructions, ContinuitySnapshot(movie.Guide), null, null)),
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
        var now = DateTime.UtcNow;
        var referenceAssetIds = request.ReferenceAssetIds?.Distinct().ToList() ?? [];
        if (request.ReferenceAssetId.HasValue && !referenceAssetIds.Contains(request.ReferenceAssetId.Value)) referenceAssetIds.Insert(0, request.ReferenceAssetId.Value);
        await ValidateReferenceAssetsAsync(movie.WorkspaceId, referenceAssetIds, cancellationToken);
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
        if (character is null || !await access.IsMemberAsync(userId, character.MovieProject.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Character name and description are required.");
        EnsureCardLocksAllow(character, request);
        var referenceAssetIds = request.ReferenceAssetIds?.Distinct().ToList() ?? character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToList();
        if (request.ReferenceAssetId.HasValue && !referenceAssetIds.Contains(request.ReferenceAssetId.Value)) referenceAssetIds.Insert(0, request.ReferenceAssetId.Value);
        await ValidateReferenceAssetsAsync(character.MovieProject.WorkspaceId, referenceAssetIds, cancellationToken);
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
        if (state is null || !await access.IsMemberAsync(userId, state.Character.MovieProject.WorkspaceId, cancellationToken)) return null;
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

    public async Task<MovieCharacterContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid characterId, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        if (character is null || !await access.IsMemberAsync(userId, character.MovieProject.WorkspaceId, cancellationToken)) return null;
        var fieldKey = request.FieldKey.Trim();
        if (string.IsNullOrWhiteSpace(request.LockedValue)) throw new MovieStudioValidationException("The continuity lock value is required.");
        if (request.CharacterStateId is Guid stateId)
        {
            var state = await db.MovieCharacterStates.Include(item => item.Character).FirstOrDefaultAsync(item => item.Id == stateId && item.MovieCharacterId == characterId, cancellationToken);
            if (state is null) throw new MovieStudioValidationException("The character state does not belong to this character.");
            if (!CharacterFieldKeys.State.Contains(fieldKey)) throw new MovieStudioValidationException("The continuity lock field is invalid for a character state.");
            var currentValue = StateFieldValue(state, fieldKey); if (!string.Equals(currentValue, request.LockedValue, StringComparison.Ordinal)) throw new MovieStudioValidationException("The locked value must match the current character state fact.");
            if (await db.MovieCharacterContinuityLocks.AnyAsync(item => item.MovieCharacterId == characterId && item.MovieCharacterStateId == stateId && item.FieldKey == fieldKey, cancellationToken)) throw new MovieStudioValidationException("This character state fact is already locked.");
            var stateLock = new MovieCharacterContinuityLock { Id = Guid.NewGuid(), MovieCharacterId = characterId, MovieCharacterStateId = stateId, FieldKey = fieldKey, LockedValue = request.LockedValue, ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow };
            db.MovieCharacterContinuityLocks.Add(stateLock); await db.SaveChangesAsync(cancellationToken); return ToDto(stateLock);
        }
        if (!CharacterFieldKeys.Card.Contains(fieldKey)) throw new MovieStudioValidationException("The continuity lock field is invalid for a character card.");
        var cardValue = CardFieldValue(character, fieldKey); if (!string.Equals(cardValue, request.LockedValue, StringComparison.Ordinal)) throw new MovieStudioValidationException("The locked value must match the current character fact.");
        if (await db.MovieCharacterContinuityLocks.AnyAsync(item => item.MovieCharacterId == characterId && item.MovieCharacterStateId == null && item.FieldKey == fieldKey, cancellationToken)) throw new MovieStudioValidationException("This character fact is already locked.");
        var lockEntity = new MovieCharacterContinuityLock { Id = Guid.NewGuid(), MovieCharacterId = characterId, FieldKey = fieldKey, LockedValue = request.LockedValue, ApprovedByUserId = userId, ApprovedAt = DateTime.UtcNow };
        db.MovieCharacterContinuityLocks.Add(lockEntity); await db.SaveChangesAsync(cancellationToken); return ToDto(lockEntity);
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
            ContinuitySnapshotJson = await ContinuitySnapshotAsync(movie.Id, movie.Guide, cancellationToken),
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
                clip.ContinuitySnapshotJson,
                SceneSnapshot(scene),
                shot is null ? null : ShotSnapshot(shot))),
        }, cancellationToken, idempotencyKey);
        clip.GenerationJobId = job.Id;
        await db.SaveChangesAsync(cancellationToken);
        return new MovieStudioGenerationResponse(await GetAsync(userId, movie.Id, cancellationToken) ?? throw new InvalidOperationException("Movie project disappeared."), GenerationJobContractMapper.ToDto(job), clip.Id);
    }

    private IQueryable<MovieProject> Query() => db.MovieProjects.AsNoTracking().Include(item => item.Guide).Include(item => item.Scenes).ThenInclude(scene => scene.Shots).ThenInclude(shot => shot.Clips).Include(item => item.Scenes).ThenInclude(scene => scene.Clips).Include(item => item.Clips).Include(item => item.Characters).ThenInclude(character => character.States).ThenInclude(state => state.ContinuityLocks).Include(item => item.Characters).ThenInclude(character => character.ReferenceAssets).Include(item => item.Characters).ThenInclude(character => character.Relationships).ThenInclude(relationship => relationship.RelatedCharacter).Include(item => item.Characters).ThenInclude(character => character.ContinuityLocks).Include(item => item.Locations).Include(item => item.Assemblies);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray(), scene.Clips.Where(clip => clip.MovieShotId is null).Select(ToDto).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Clips.Where(clip => clip.MovieSceneId is null).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray());
    private static string ContinuitySnapshot(MovieContinuityGuide guide) => JsonSerializer.Serialize(new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.UpdatedAt });
    private static string SceneSnapshot(MovieScene scene) => JsonSerializer.Serialize(new { scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue });
    private static string ShotSnapshot(MovieShot shot) => JsonSerializer.Serialize(new { shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes });
    private async Task<MovieCharacterDto?> GetCharacterDtoAsync(Guid userId, Guid characterId, CancellationToken cancellationToken)
    {
        var movie = await Query().FirstOrDefaultAsync(item => item.Characters.Any(character => character.Id == characterId), cancellationToken);
        var character = movie?.Characters.FirstOrDefault(item => item.Id == characterId);
        return movie is null || character is null || !await access.IsMemberAsync(userId, movie.WorkspaceId, cancellationToken) ? null : ToDto(character);
    }

    private async Task<MovieCharacter?> GetCharacterForUserAsync(Guid userId, Guid characterId, CancellationToken cancellationToken)
    {
        var character = await db.MovieCharacters.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == characterId, cancellationToken);
        return character is null || !await access.IsMemberAsync(userId, character.MovieProject.WorkspaceId, cancellationToken) ? null : character;
    }

    private async Task ValidateReferenceAssetsAsync(Guid workspaceId, IEnumerable<Guid> assetIds, CancellationToken cancellationToken)
    {
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        var valid = await db.Assets.CountAsync(item => ids.Contains(item.Id) && item.WorkspaceId == workspaceId, cancellationToken);
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

    private async Task<string> ContinuitySnapshotAsync(Guid movieProjectId, MovieContinuityGuide guide, CancellationToken cancellationToken)
    {
        var characters = await db.MovieCharacters.AsNoTracking().Where(item => item.MovieProjectId == movieProjectId).Include(item => item.States).ThenInclude(state => state.ContinuityLocks).Include(item => item.ContinuityLocks).Include(item => item.ReferenceAssets).Include(item => item.Relationships).ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            guide = new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.UpdatedAt },
            characters = characters.Select(character => new
            {
                character.Id, character.Name, character.Role, character.Description, character.Appearance, character.PhysicalDescription, character.Wardrobe,
                character.VoiceReference, character.PersonalityAndStoryNotes, character.VoiceAndPerformance, character.ContinuityNotes,
                referenceAssetIds = character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToArray(),
                states = character.States.OrderBy(item => item.CreatedAt).Select(state => new { state.Id, state.Key, state.Label, state.Wardrobe, state.AgeOrTimeState, state.Appearance, state.InjuryOrCondition, state.LocationOrStoryState, state.ContinuityNotes }),
                continuityLocks = character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null).Concat(character.States.SelectMany(state => state.ContinuityLocks)).OrderBy(item => item.ApprovedAt).Select(lockEntity => new { lockEntity.Id, lockEntity.MovieCharacterStateId, lockEntity.FieldKey, lockEntity.LockedValue, lockEntity.ApprovedAt })
            })
        });
    }

    private static MovieSceneDto ToDto(MovieScene scene, IReadOnlyList<MovieShotDto> shots, IReadOnlyList<MovieClipDto> clips) => new(scene.Id, scene.Sequence, scene.Title, scene.Summary, scene.DurationSeconds, scene.ContinuityNotes, scene.Narration, scene.Dialogue, shots, clips);
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips) => new(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, clips);
    private static MovieCharacterDto ToDto(MovieCharacter character) => new(character.Id, character.Name, character.Role, character.Description, character.Appearance, character.PhysicalDescription, character.Wardrobe, character.VoiceReference, character.PersonalityAndStoryNotes, character.VoiceAndPerformance, character.ContinuityNotes, character.ReferenceAssetId, character.ReferenceAssets.OrderBy(item => item.SortOrder).Select(item => item.AssetId).ToArray(), character.States.OrderBy(item => item.CreatedAt).Select(ToDto).ToArray(), character.Relationships.OrderBy(item => item.CreatedAt).Select(item => new MovieCharacterRelationshipDto(item.Id, item.RelatedCharacterId, item.RelatedCharacter?.Name ?? string.Empty, item.RelationshipType, item.Notes)).ToArray(), character.ContinuityLocks.Where(item => item.MovieCharacterStateId is null).OrderBy(item => item.ApprovedAt).Select(ToDto).Concat(character.States.SelectMany(item => item.ContinuityLocks).OrderBy(item => item.ApprovedAt).Select(ToDto)).ToArray());
    private static MovieCharacterStateDto ToDto(MovieCharacterState state) => new(state.Id, state.Key, state.Label, state.Wardrobe, state.AgeOrTimeState, state.Appearance, state.InjuryOrCondition, state.LocationOrStoryState, state.ContinuityNotes, state.CreatedAt, state.UpdatedAt);
    private static MovieCharacterContinuityLockDto ToDto(MovieCharacterContinuityLock lockEntity) => new(lockEntity.Id, lockEntity.FieldKey, lockEntity.LockedValue, lockEntity.MovieCharacterStateId, lockEntity.ApprovedAt);
    private static MovieLocationDto ToDto(MovieLocation location) => new(location.Id, location.Name, location.Description, location.VisualContinuityNotes, location.ReferenceAssetId);
    private static MovieClipDto ToDto(MovieClip clip) => new(clip.Id, clip.MovieSceneId, clip.MovieShotId, clip.GenerationJobId, clip.AssetId, clip.Status, clip.DurationSeconds, clip.MetadataJson, clip.ContinuitySnapshotJson);
    private static MovieAssemblyDto ToDto(MovieAssembly assembly) => new(assembly.Id, assembly.GenerationJobId, assembly.AssetId, assembly.Status, assembly.OutputFormat, assembly.MetadataJson, assembly.CreatedAt, assembly.CompletedAt);
}

public sealed class MovieStudioValidationException(string message) : Exception(message);
public sealed class MovieStudioContinuityLockException(string fieldKey) : Exception($"The approved continuity lock for '{fieldKey}' cannot be changed.");
public static class CharacterFieldKeys
{
    public static readonly IReadOnlySet<string> Card = new HashSet<string>(StringComparer.Ordinal) { "name", "role", "description", "appearance", "physicalDescription", "wardrobe", "voiceReference", "personalityAndStoryNotes", "voiceAndPerformance", "continuityNotes" };
    public static readonly IReadOnlySet<string> State = new HashSet<string>(StringComparer.Ordinal) { "label", "wardrobe", "ageOrTimeState", "appearance", "injuryOrCondition", "locationOrStoryState", "continuityNotes" };
}

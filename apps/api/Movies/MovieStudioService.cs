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
    Task<MovieCharacterContinuityLockDto?> AddCharacterContinuityLockAsync(Guid userId, Guid characterId, MovieCharacterContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieLocationDto?> AddLocationAsync(Guid userId, Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken);
    Task<MovieSetDto?> AddSetAsync(Guid userId, Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken);
    Task<MovieSetVariationDto?> AddSetVariationAsync(Guid userId, Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken);
    Task<MoviePropDto?> AddPropAsync(Guid userId, Guid id, MovieStudioPropRequest request, CancellationToken cancellationToken);
    Task<MovieWorldReferenceDto?> AddWorldReferenceAsync(Guid userId, Guid id, MovieStudioWorldReferenceRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityFactDto?> AddContinuityFactAsync(Guid userId, Guid id, MovieStudioContinuityFactRequest request, CancellationToken cancellationToken);
    Task<MovieContinuityLockDto?> AddContinuityLockAsync(Guid userId, Guid id, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken);
    Task<MovieWorldUsageDto?> AddWorldUsageAsync(Guid userId, Guid sceneId, MovieStudioWorldUsageRequest request, CancellationToken cancellationToken);
    Task<MovieShotDto?> AddShotAsync(Guid userId, Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken);
    Task<MovieShotProductionDto?> GetShotProductionAsync(Guid userId, Guid shotId, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> CreateProductionVersionAsync(Guid userId, Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken);
    Task<MovieProductionVersionDto?> ReviewProductionVersionAsync(Guid userId, Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken);
    Task<MovieStudioGenerationResponse?> GenerateSceneAsync(Guid userId, Guid movieProjectId, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<MovieProviderReadinessDto> ProviderReadinessAsync();
}

public sealed class MovieStudioService(TaslimDbContext db, WorkspaceAccessService access, MovieCollaborationAccess collaboration, IGenerationJobService jobs, IMovieVideoProvider provider) : IMovieStudioService
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
        return movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.View, cancellationToken) ? null : ToDto(movie);
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
        await EnsureAssetInWorkspaceAsync(request.ReferenceAssetId, movie.WorkspaceId, cancellationToken);
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

    public async Task<MovieCharacterContinuityLockDto?> AddCharacterContinuityLockAsync(Guid userId, Guid characterId, MovieCharacterContinuityLockRequest request, CancellationToken cancellationToken)
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
        if (movie is null || !await collaboration.HasPermissionAsync(userId, id, MoviePermissions.Edit, cancellationToken)) return null;
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
        if (scene is null || !await collaboration.HasPermissionAsync(userId, scene.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Description)) throw new MovieStudioValidationException("Shot description is required.");
        var cinematographyValidation = CinematographyIntentValidator.Validate(request.Cinematography);
        if (cinematographyValidation is not null) throw new MovieStudioValidationException(cinematographyValidation);
        var now = DateTime.UtcNow;
        var shot = new MovieShot { Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = await db.MovieShots.CountAsync(item => item.MovieSceneId == sceneId, cancellationToken) + 1, Description = request.Description.Trim(), CameraAndFraming = MovieStudioHelpers.Clean(request.CameraAndFraming), CameraMotion = MovieStudioHelpers.Clean(request.CameraMotion), CinematographyJson = CinematographyIntentValidator.ToJson(request.Cinematography), DurationSeconds = request.DurationSeconds, Narration = MovieStudioHelpers.Clean(request.Narration), Dialogue = MovieStudioHelpers.Clean(request.Dialogue), VisualContinuityNotes = MovieStudioHelpers.Clean(request.VisualContinuityNotes), CreatedAt = now, UpdatedAt = now };
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
        if (scene is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
        return await QueueClipAsync(userId, scene.MovieProject, scene, null, request, cancellationToken, idempotencyKey);
    }

    public async Task<MovieStudioGenerationResponse?> GenerateShotAsync(Guid userId, Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        var shot = await db.MovieShots.Include(item => item.Scene).ThenInclude(item => item.MovieProject).ThenInclude(item => item.Guide).FirstOrDefaultAsync(item => item.Id == shotId, cancellationToken);
        if (shot is null || !await collaboration.HasPermissionAsync(userId, shot.Scene.MovieProjectId, MoviePermissions.Generate, cancellationToken)) return null;
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
    private IQueryable<MovieShot> ProductionQuery() => db.MovieShots.AsNoTracking().Include(item => item.Scene).ThenInclude(scene => scene.MovieProject).Include(item => item.ProductionVersions).ThenInclude(version => version.AssetReferences).Include(item => item.ProductionTransitions);

    private static MovieStudioProjectDto ToDto(MovieProject movie) => new(movie.Id, movie.WorkspaceId, movie.ProjectId, movie.Mode, movie.Status, movie.Title, movie.Description, movie.DurationSeconds, movie.AspectRatio, movie.Style, movie.Language, movie.AdditionalInstructions, movie.CreatedAt, movie.UpdatedAt, new MovieGuideDto(movie.Guide.Id, movie.Guide.VisualLanguage, movie.Guide.CameraLanguage, movie.Guide.ColorAndLighting, movie.Guide.SoundAndNarration, movie.Guide.ContinuityRules, movie.Guide.UpdatedAt, movie.Guide.CurrentRevisionNumber, movie.Guide.LockedRevisionNumber, movie.Guide.LockedAt, ToCinematographyBible(movie.Guide)), movie.Scenes.OrderBy(scene => scene.Sequence).Select(scene => ToDto(scene, scene.Shots.OrderBy(shot => shot.Sequence).Select(shot => ToDto(shot, shot.Clips.Select(ToDto).ToArray())).ToArray(), scene.Clips.Where(clip => clip.MovieShotId is null).Select(ToDto).ToArray())).ToArray(), movie.Characters.OrderBy(character => character.CreatedAt).Select(ToDto).ToArray(), movie.Locations.OrderBy(location => location.CreatedAt).Select(ToDto).ToArray(), movie.Clips.Where(clip => clip.MovieSceneId is null).Select(ToDto).ToArray(), movie.Assemblies.OrderByDescending(assembly => assembly.CreatedAt).Select(ToDto).ToArray(), MovieWorld(movie));

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
    private static MovieCinematographyBibleDto? ToCinematographyBible(MovieContinuityGuide guide)
    {
        var selection = CinematographyIntentValidator.FromJson(guide.CinematographyBibleReferencesJson);
        if (selection is null && string.IsNullOrWhiteSpace(guide.CinematographyIntent)) return null;
        return new MovieCinematographyBibleDto(guide.CinematographyIntent ?? selection?.Intent, selection?.PresetId, selection?.Notes, selection?.CapabilityReferences ?? []);
    }

    private static string ContinuitySnapshot(MovieContinuityGuide guide) => JsonSerializer.Serialize(new { guide.VisualLanguage, guide.CameraLanguage, guide.ColorAndLighting, guide.SoundAndNarration, guide.ContinuityRules, guide.ReferenceAssetIdsJson, guide.CinematographyIntent, guide.CinematographyBibleReferencesJson, guide.UpdatedAt });

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
    private static string ShotSnapshot(MovieShot shot) => JsonSerializer.Serialize(new { shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.CinematographyJson, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes });
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
    private static MovieShotDto ToDto(MovieShot shot, IReadOnlyList<MovieClipDto> clips) => new(shot.Id, shot.Sequence, shot.Description, shot.CameraAndFraming, shot.CameraMotion, shot.CinematographyJson, shot.DurationSeconds, shot.Narration, shot.Dialogue, shot.VisualContinuityNotes, shot.ProductionStage, clips, shot.ProductionVersions.OrderByDescending(item => item.VersionNumber).Select(ToDto).ToArray());
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
public sealed class MovieStudioContinuityLockException(string fieldKey) : Exception($"The approved continuity lock for '{fieldKey}' cannot be changed.");
public static class CharacterFieldKeys
{
    public static readonly IReadOnlySet<string> Card = new HashSet<string>(StringComparer.Ordinal) { "name", "role", "description", "appearance", "physicalDescription", "wardrobe", "voiceReference", "personalityAndStoryNotes", "voiceAndPerformance", "continuityNotes" };
    public static readonly IReadOnlySet<string> State = new HashSet<string>(StringComparer.Ordinal) { "label", "wardrobe", "ageOrTimeState", "appearance", "injuryOrCondition", "locationOrStoryState", "continuityNotes" };
}

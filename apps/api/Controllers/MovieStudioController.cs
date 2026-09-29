using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Generation;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieStudioController(IMovieStudioService movies, IMovieGuideService guides, IMovieStoryService stories, IMovieStoryCastService storyCast, IMovieCharacterContinuityService continuity, IMovieWorldContinuityService worldContinuity, MovieAuthorizationService authorization, MovieShotImportanceService shotImportance) : ControllerBase
{
    [HttpGet("cinematography/presets")]
    public IActionResult CinematographyPresets() => Ok(CinematographyPresetCatalog.All);

    [HttpGet("provider")]
    public async Task<IActionResult> Provider(CancellationToken cancellationToken) => Ok(new MovieStudioProviderResponse(await movies.ProviderReadinessAsync()));

    [HttpPost("projects")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MovieStudioCreateRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return ApiResults.Validation(this);
        try
        {
            var result = await movies.CreateAsync(GetUserId(), request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? Forbid() : Accepted(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_REQUEST_INVALID", exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("projects/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await movies.GetAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/capabilities")]
    public async Task<IActionResult> Capabilities(Guid id, CancellationToken cancellationToken)
    {
        var result = await authorization.GetCapabilitiesAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }
    [HttpGet("projects/{id:guid}/shell")]
    public async Task<IActionResult> GetShell(Guid id, CancellationToken cancellationToken)
    {
        var result = await movies.GetShellAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/cast")]
    public async Task<IActionResult> GetCast(Guid id, CancellationToken cancellationToken)
    {
        var result = await movies.GetCastAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/cast/from-story")]
    public async Task<IActionResult> GetCastFromStory(Guid id, CancellationToken cancellationToken)
    {
        var result = await storyCast.GetSuggestionsAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("characters/{characterId:guid}/detail")]
    public async Task<IActionResult> GetCharacterDetail(Guid characterId, CancellationToken cancellationToken)
    {
        var result = await movies.GetCharacterDetailAsync(GetUserId(), characterId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/world")]
    public async Task<IActionResult> GetWorld(Guid id, CancellationToken cancellationToken)
    {
        var result = await movies.GetWorldAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/workspace")]
    public async Task<IActionResult> Workspace(Guid id, [FromQuery] string? module, CancellationToken cancellationToken)
    {
        var result = await movies.GetWorkspaceAsync(GetUserId(), id, module, cancellationToken);
        if (result is null) return ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.");
        Response.Headers["X-Movie-Read-Model"] = "workspace-v1";
        Response.Headers["X-Movie-Read-Module"] = result.Module;
        return Ok(result);
    }

    [HttpPatch("projects/{id:guid}/guide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGuide(Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.UpdateGuideAsync(GetUserId(), id, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieGuideLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_GUIDE_LOCKED", exception.Message); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_GUIDE_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/guide/revisions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGuideRevision(Guid id, MovieGuideRevisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await guides.CreateRevisionAsync(GetUserId(), id, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : CreatedAtAction(nameof(GetGuideHistory), new { id }, result);
        }
        catch (MovieGuideValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_GUIDE_INVALID", exception.Message); }
        catch (MovieGuideLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_GUIDE_LOCKED", exception.Message); }
    }

    [HttpGet("projects/{id:guid}/guide/history")]
    public async Task<IActionResult> GetGuideHistory(Guid id, CancellationToken cancellationToken)
    {
        var result = await guides.GetHistoryAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("projects/{id:guid}/guide/lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockGuide(Guid id, MovieGuideLockRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await guides.LockAsync(GetUserId(), id, request.RevisionNumber, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieGuideValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_GUIDE_INVALID", exception.Message); }
        catch (MovieGuideLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_GUIDE_LOCKED", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/guide/unlock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockGuide(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await guides.UnlockAsync(GetUserId(), id, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieGuideValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_GUIDE_INVALID", exception.Message); }
    }

    [HttpGet("projects/{id:guid}/director-context")]
    public async Task<IActionResult> GetDirectorContext(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await guides.GetDirectorContextAsync(GetUserId(), id, cancellationToken);
            return result is null
                ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.")
                : Ok(result);
        }
        catch (MovieGuideNotLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_GUIDE_NOT_LOCKED", exception.Message); }
    }

    [HttpGet("projects/{id:guid}/continuity/characters")]
    public async Task<IActionResult> ProjectCharacterContinuity(Guid id, [FromQuery] Guid? sceneId, [FromQuery] Guid? shotId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await continuity.ProjectAsync(GetUserId(), id, sceneId, shotId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieContinuityTargetException exception) { return ApiResults.Error(this, 400, "MOVIE_CONTINUITY_TARGET_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/continuity/snapshots")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCharacterContinuitySnapshot(Guid id, MovieCharacterContinuitySnapshotRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await continuity.BuildSnapshotAsync(GetUserId(), id, request.MovieSceneId, request.MovieShotId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : CreatedAtAction(nameof(GetCharacterContinuitySnapshot), new { snapshotId = result.SnapshotId }, result);
        }
        catch (MovieContinuityTargetException exception) { return ApiResults.Error(this, 400, "MOVIE_CONTINUITY_TARGET_INVALID", exception.Message); }
    }

    [HttpGet("continuity/snapshots/{snapshotId:guid}")]
    public async Task<IActionResult> GetCharacterContinuitySnapshot(Guid snapshotId, CancellationToken cancellationToken)
    {
        var result = await continuity.GetSnapshotAsync(GetUserId(), snapshotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_CONTINUITY_SNAPSHOT_NOT_FOUND", "Continuity snapshot not found.") : Ok(result);
    }

    [HttpPost("projects/{id:guid}/scenes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddScene(Guid id, MovieStudioSceneRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddSceneAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SCENE_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/characters")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharacter(Guid id, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddCharacterAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_INVALID", exception.Message); }
    }

    [HttpPatch("characters/{characterId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCharacter(Guid characterId, MovieStudioCharacterRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.UpdateCharacterAsync(GetUserId(), characterId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result); }
        catch (MovieStudioContinuityLockException exception) { return ApiResults.Error(this, 409, "MOVIE_CHARACTER_CONTINUITY_LOCKED", exception.Message); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_INVALID", exception.Message); }
    }

    [HttpPost("characters/{characterId:guid}/states")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharacterState(Guid characterId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddCharacterStateAsync(GetUserId(), characterId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_STATE_INVALID", exception.Message); }
    }

    [HttpPatch("character-states/{stateId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCharacterState(Guid stateId, MovieStudioCharacterStateRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.UpdateCharacterStateAsync(GetUserId(), stateId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_STATE_NOT_FOUND", "Movie character state not found.") : Ok(result); }
        catch (MovieStudioContinuityLockException exception) { return ApiResults.Error(this, 409, "MOVIE_CHARACTER_CONTINUITY_LOCKED", exception.Message); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_STATE_INVALID", exception.Message); }
    }

    [HttpPost("characters/{characterId:guid}/relationships")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCharacterRelationship(Guid characterId, MovieStudioCharacterRelationshipRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddCharacterRelationshipAsync(GetUserId(), characterId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_RELATIONSHIP_INVALID", exception.Message); }
    }

    [HttpPost("characters/{characterId:guid}/continuity-locks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContinuityLock(Guid characterId, MovieCharacterContinuityLockRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddCharacterContinuityLockAsync(GetUserId(), characterId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CHARACTER_CONTINUITY_LOCK_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/locations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLocation(Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddLocationAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_INVALID", exception.Message); }
    }

    [HttpPatch("locations/{locationId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLocation(Guid locationId, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.UpdateLocationAsync(GetUserId(), locationId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_NOT_FOUND", "Movie location not found.") : Ok(result); }
        catch (MovieStudioContinuityLockException exception) { return ApiResults.Error(this, 409, "MOVIE_WORLD_CONTINUITY_LOCKED", exception.Message); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/sets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSet(Guid id, MovieStudioSetRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddSetAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SET_INVALID", exception.Message); }
    }

    [HttpPost("sets/{setId:guid}/variations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSetVariation(Guid setId, MovieStudioSetVariationRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddSetVariationAsync(GetUserId(), setId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_SET_NOT_FOUND", "Movie set not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SET_VARIATION_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/props")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddProp(Guid id, MovieStudioPropRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddPropAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/world-references")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddWorldReference(Guid id, MovieStudioWorldReferenceRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddWorldReferenceAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_WORLD_REFERENCE_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/continuity-facts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContinuityFact(Guid id, MovieStudioContinuityFactRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddContinuityFactAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CONTINUITY_FACT_INVALID", exception.Message); }
    }

    [HttpPost("projects/{id:guid}/continuity-locks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddContinuityLock(Guid id, MovieStudioContinuityLockRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddContinuityLockAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CONTINUITY_LOCK_INVALID", exception.Message); }
    }

    [HttpPost("scenes/{sceneId:guid}/shots")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddShot(Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddShotAsync(GetUserId(), sceneId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_INVALID", exception.Message); }
    }

    [HttpGet("scenes/{sceneId:guid}/shots")]
    public async Task<IActionResult> GetSceneShotPlan(Guid sceneId, CancellationToken cancellationToken)
    {
        var result = await movies.GetSceneShotPlanAsync(GetUserId(), sceneId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result);
    }

    [HttpGet("shots/{shotId:guid}")]
    public async Task<IActionResult> GetShotPlanning(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await movies.GetShotPlanningAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPatch("shots/{shotId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateShotPlanning(Guid shotId, MovieStudioShotUpdateRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.UpdateShotPlanningAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_INVALID", exception.Message); }
    }

    [HttpGet("shots/{shotId:guid}/importance")]
    public async Task<IActionResult> GetShotImportance(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await shotImportance.GetAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPatch("shots/{shotId:guid}/importance")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetShotImportanceOverride(Guid shotId, MovieShotImportanceOverrideRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await shotImportance.SetOverrideAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_IMPORTANCE_INVALID", exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/reorder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReorderShot(Guid shotId, MovieShotReorderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.ReorderShotAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_ORDER_INVALID", exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ArchiveShot(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await movies.ArchiveShotAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPost("scenes/{sceneId:guid}/world-usage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddWorldUsage(Guid sceneId, MovieStudioWorldUsageRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddWorldUsageAsync(GetUserId(), sceneId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_WORLD_USAGE_INVALID", exception.Message); }
    }

    [HttpGet("projects/{movieProjectId:guid}/world-continuity")]
    public async Task<IActionResult> GetWorldContinuity(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await worldContinuity.GetProjectAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("scenes/{sceneId:guid}/world-continuity")]
    public async Task<IActionResult> GetSceneWorldContinuity(Guid sceneId, CancellationToken cancellationToken)
    {
        var result = await worldContinuity.GetSceneAsync(GetUserId(), sceneId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result);
    }

    [HttpGet("shots/{shotId:guid}/world-continuity")]
    public async Task<IActionResult> GetShotWorldContinuity(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await worldContinuity.GetShotAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpGet("shots/{shotId:guid}/production")]
    public async Task<IActionResult> GetShotProduction(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await movies.GetShotProductionAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/storyboard")]
    public async Task<IActionResult> GetStoryboard(Guid id, CancellationToken cancellationToken)
    {
        var result = await movies.GetStoryboardAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("shots/{shotId:guid}/production/versions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProductionVersion(Guid shotId, MovieProductionVersionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.CreateProductionVersionAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("production/versions/{versionId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewProductionVersion(Guid versionId, MovieProductionReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.ReviewProductionVersionAsync(GetUserId(), versionId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_VERSION_NOT_FOUND", "Production version not found.") : Ok(result);
        }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/production/motion-preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMotionPreview(Guid shotId, MovieProductionMotionPreviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.CreateMotionPreviewAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/production/render")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> QueueProductionRender(Guid shotId, MovieProductionRenderRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.QueueProductionRenderAsync(GetUserId(), shotId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("production/versions/{versionId:guid}/take")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProductionTake(Guid versionId, MovieProductionTakeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.CreateTakeFromProductionAsync(GetUserId(), versionId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_VERSION_NOT_FOUND", "Production version not found.") : Ok(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/regeneration-requests")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRegenerationRequest(Guid shotId, MovieRegenerationRequestInput request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.CreateRegenerationRequestAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("regeneration-requests/{requestId:guid}")]
    public async Task<IActionResult> GetRegenerationRequest(Guid requestId, CancellationToken cancellationToken)
    {
        var result = await movies.GetRegenerationRequestAsync(GetUserId(), requestId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_REGENERATION_NOT_FOUND", "Selective regeneration request not found.") : Ok(result);
    }

    [HttpPost("regeneration-requests/{requestId:guid}/confirm")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> ConfirmRegeneration(Guid requestId, MovieRegenerationConfirmationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.ConfirmRegenerationAsync(GetUserId(), requestId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? ApiResults.Error(this, 404, "MOVIE_REGENERATION_NOT_FOUND", "Selective regeneration request not found.") : Accepted(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("projects/{id:guid}/scenes/{sceneId:guid}/generate")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> GenerateScene(Guid id, Guid sceneId, MovieStudioGenerationRequest request, CancellationToken cancellationToken)
    {
        var result = await movies.GenerateSceneAsync(GetUserId(), id, sceneId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Accepted(result);
    }

    [HttpPost("shots/{shotId:guid}/generate")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> GenerateShot(Guid shotId, MovieStudioGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.GenerateShotAsync(GetUserId(), shotId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_NOT_READY", exception.Message); }
    }

    [HttpGet("projects/{movieProjectId:guid}/story")]
    public async Task<IActionResult> GetStory(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await stories.GetAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_STORY_NOT_FOUND", "Movie story not found.") : Ok(result);
    }

    [HttpGet("projects/{movieProjectId:guid}/story/revisions")]
    public async Task<IActionResult> ListStoryRevisions(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await stories.ListRevisionsAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_STORY_NOT_FOUND", "Movie story not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/story/revisions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateStoryRevision(Guid movieProjectId, MovieStoryRevisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await stories.CreateRevisionAsync(GetUserId(), movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : CreatedAtAction(nameof(GetStoryRevision), new { movieProjectId, revisionId = result.CurrentRevisionId }, result);
        }
        catch (MovieStoryValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_STORY_INVALID", exception.Message); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpPatch("projects/{movieProjectId:guid}/story/revisions/{revisionId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStoryDraft(Guid movieProjectId, Guid revisionId, MovieStoryRevisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await stories.UpdateDraftAsync(GetUserId(), movieProjectId, revisionId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_STORY_REVISION_NOT_FOUND", "Movie story revision not found.") : Ok(result);
        }
        catch (MovieStoryValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_STORY_INVALID", exception.Message); }
        catch (MovieStoryWorkflowException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpGet("projects/{movieProjectId:guid}/story/revisions/{revisionId:guid}")]
    public async Task<IActionResult> GetStoryRevision(Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken)
    {
        var result = await stories.GetRevisionAsync(GetUserId(), movieProjectId, revisionId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_STORY_REVISION_NOT_FOUND", "Movie story revision not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/story/revisions/{revisionId:guid}/submit")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> SubmitStoryRevision(Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) => TransitionStoryRevision(() => stories.SubmitAsync(GetUserId(), movieProjectId, revisionId, cancellationToken), "MOVIE_STORY_REVISION_NOT_FOUND");

    [HttpPost("projects/{movieProjectId:guid}/story/revisions/{revisionId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ApproveStoryRevision(Guid movieProjectId, Guid revisionId, CancellationToken cancellationToken) => TransitionStoryRevision(() => stories.ApproveAsync(GetUserId(), movieProjectId, revisionId, cancellationToken), "MOVIE_STORY_REVISION_NOT_FOUND");

    [HttpPost("projects/{movieProjectId:guid}/story/revisions/{revisionId:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectStoryRevision(Guid movieProjectId, Guid revisionId, MovieStoryRejectRequest request, CancellationToken cancellationToken)
    {
        try { return await TransitionStoryRevision(() => stories.RejectAsync(GetUserId(), movieProjectId, revisionId, request.Reason, cancellationToken), "MOVIE_STORY_REVISION_NOT_FOUND"); }
        catch (MovieStoryWorkflowException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    private async Task<IActionResult> TransitionStoryRevision(Func<Task<MovieStoryRevisionDto?>> transition, string notFoundCode)
    {
        try
        {
            var result = await transition();
            return result is null ? ApiResults.Error(this, 404, notFoundCode, "Movie story revision not found.") : Ok(result);
        }
        catch (MovieStoryWorkflowException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

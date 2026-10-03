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
public sealed class MovieStudioController(IMovieStudioService movies, IMovieProductionComplexityService complexity, IMovieDurationBudgetService durationBudgets, IMovieGuideService guides, IMovieStoryService stories, IMovieStoryCastService storyCast, IMovieCharacterContinuityService continuity, IMovieWorldContinuityService worldContinuity, IMovieProductionContinuityService productionContinuity, IMovieProductionReferencePackageService productionReferences, IMovieProductionPreflightService productionPreflight, IMovieTimelineService timeline, IMovieTimelineTransitionService transitionEdits, IMovieTakeSelectService takeSelects, IMovieShotExecutionService shotExecution, MovieAuthorizationService authorization, MovieShotImportanceService shotImportance, IMovieCinematographyPlanningService cinematographyPlanning, IMovieCharacterProductionSheetService productionSheets, IMovieLocationGeographySheetService geographySheets, IMoviePropBibleService propBible, IMovieReferenceReadinessService referenceReadiness, IMovieMissingInsertPlannerService insertPlanner) : ControllerBase
{
    [HttpGet("cinematography/presets")]
    public IActionResult CinematographyPresets() => Ok(CinematographyPresetCatalog.All);

    [HttpGet("cinematography/planning-values")]
    public IActionResult CinematographyPlanningValues() => Ok(CinematographyPlanningValueCatalog.Current);

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

    [HttpGet("projects/{id:guid}/duration-budget")]
    public async Task<IActionResult> GetDurationBudget(Guid id, CancellationToken cancellationToken)
    {
        var result = await durationBudgets.GetAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }
    [HttpGet("projects/{id:guid}/timeline")]
    public async Task<IActionResult> GetTimeline(Guid id, CancellationToken cancellationToken)
    {
        var result = await timeline.GetAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_NOT_FOUND", "Movie timeline not found.") : Ok(result);
    }
    [HttpGet("projects/{id:guid}/timeline/transitions")]
    public async Task<IActionResult> GetTimelineTransitions(Guid id, CancellationToken cancellationToken)
    {
        var result = await transitionEdits.GetLatestAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_TRANSITIONS_NOT_FOUND", "No persisted timeline transition edit was found.") : Ok(result);
    }
    [HttpPost("projects/{id:guid}/timeline/transitions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyTimelineTransition(Guid id, MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await transitionEdits.ApplyAsync(GetUserId(), id, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_NOT_FOUND", "Movie timeline not found.") : Ok(result);
        }
        catch (MovieTimelineValidationException exception)
        {
            return ApiResults.Error(this, exception.Code == MovieTimelineValidationCodes.TimelineVersionConflict ? 409 : 400, exception.Code, exception.Message);
        }
    }

    [HttpGet("projects/{movieProjectId:guid}/takes/{takeId:guid}/selects")]
    public async Task<IActionResult> GetTakeSelects(Guid movieProjectId, Guid takeId, CancellationToken cancellationToken)
    {
        var result = await takeSelects.ListAsync(GetUserId(), movieProjectId, takeId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_TAKE_SELECTS_NOT_FOUND", "Movie take or selects not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/takes/{takeId:guid}/selects")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTakeSelect(Guid movieProjectId, Guid takeId, MovieTakeSelectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await takeSelects.CreateAsync(GetUserId(), movieProjectId, takeId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TAKE_NOT_FOUND", "Movie take not found.") : Ok(result);
        }
        catch (MovieTakeSelectValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("projects/{movieProjectId:guid}/takes/{takeId:guid}/selects/{selectId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewTakeSelect(Guid movieProjectId, Guid takeId, Guid selectId, MovieTakeSelectReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await takeSelects.ReviewAsync(GetUserId(), movieProjectId, takeId, selectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TAKE_SELECT_NOT_FOUND", "Movie take select not found.") : Ok(result);
        }
        catch (MovieTakeSelectValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("projects/{id:guid}/insert-planner")]
    public async Task<IActionResult> GetMissingInsertPlan(Guid id, [FromQuery] Guid? revisionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await insertPlanner.GetAsync(GetUserId(), id, revisionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieMissingInsertPlannerException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("projects/{id:guid}/timeline/revisions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTimelineRevision(Guid id, MovieTimelineRevisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await timeline.CreateRevisionAsync(GetUserId(), id, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieTimelineValidationException exception) { return ApiResults.Error(this, exception.Code == MovieTimelineErrors.Locked ? 409 : 400, exception.Code, exception.Message); }
    }

    [HttpPost("timeline/revisions/{revisionId:guid}/tracks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTimelineTrack(Guid revisionId, MovieTimelineTrackRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await timeline.AddTrackAsync(GetUserId(), revisionId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_REVISION_NOT_FOUND", "Movie timeline revision not found.") : Ok(result);
        }
        catch (MovieTimelineValidationException exception) { return ApiResults.Error(this, exception.Code == MovieTimelineErrors.Locked ? 409 : 400, exception.Code, exception.Message); }
    }

    [HttpPost("timeline/tracks/{trackId:guid}/items")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTimelineItem(Guid trackId, MovieTimelineItemRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await timeline.AddItemAsync(GetUserId(), trackId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_TRACK_NOT_FOUND", "Movie timeline track not found.") : Ok(result);
        }
        catch (MovieTimelineValidationException exception) { return ApiResults.Error(this, exception.Code == MovieTimelineErrors.Locked ? 409 : 400, exception.Code, exception.Message); }
    }

    [HttpPost("timeline/revisions/{revisionId:guid}/lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockTimelineRevision(Guid revisionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await timeline.LockRevisionAsync(GetUserId(), revisionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_TIMELINE_REVISION_NOT_FOUND", "Movie timeline revision not found.") : Ok(result);
        }
        catch (MovieTimelineValidationException exception) { return ApiResults.Error(this, exception.Code == MovieTimelineErrors.Locked ? 409 : 400, exception.Code, exception.Message); }
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

    [HttpGet("characters/{characterId:guid}/production-sheet")]
    public async Task<IActionResult> GetProductionSheet(Guid characterId, CancellationToken cancellationToken)
    {
        var result = await productionSheets.GetAsync(GetUserId(), characterId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_PRODUCTION_SHEET_NOT_FOUND", "Character production sheet not found.") : Ok(result);
    }

    [HttpPut("characters/{characterId:guid}/production-sheet")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProductionSheet(Guid characterId, MovieCharacterProductionSheetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await productionSheets.SaveDraftAsync(GetUserId(), characterId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_NOT_FOUND", "Movie character not found.") : Ok(result);
        }
        catch (MovieCharacterProductionSheetValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieCharacterProductionSheetLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_CHARACTER_PRODUCTION_SHEET_LOCKED", exception.Message); }
    }

    [HttpPost("production-sheets/{sheetId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveProductionSheet(Guid sheetId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await productionSheets.ApproveAsync(GetUserId(), sheetId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_PRODUCTION_SHEET_NOT_FOUND", "Character production sheet not found.") : Ok(result);
        }
        catch (MovieCharacterProductionSheetValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieCharacterProductionSheetLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_CHARACTER_PRODUCTION_SHEET_LOCKED", exception.Message); }
    }

    [HttpPost("production-sheets/{sheetId:guid}/lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockProductionSheet(Guid sheetId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await productionSheets.LockAsync(GetUserId(), sheetId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_PRODUCTION_SHEET_NOT_FOUND", "Character production sheet not found.") : Ok(result);
        }
        catch (MovieCharacterProductionSheetValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("production-sheets/{sheetId:guid}/unlock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockProductionSheet(Guid sheetId, CancellationToken cancellationToken)
    {
        var result = await productionSheets.UnlockAsync(GetUserId(), sheetId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_CHARACTER_PRODUCTION_SHEET_NOT_FOUND", "Character production sheet not found.") : Ok(result);
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

    [HttpGet("locations/{locationId:guid}/geography-sheet")]
    public async Task<IActionResult> GetLocationGeographySheet(Guid locationId, CancellationToken cancellationToken)
    {
        var result = await geographySheets.GetAsync(GetUserId(), locationId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_GEOGRAPHY_SHEET_NOT_FOUND", "Location geography sheet not found.") : Ok(result);
    }

    [HttpPut("locations/{locationId:guid}/geography-sheet")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpsertLocationGeographySheet(Guid locationId, MovieLocationGeographySheetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await geographySheets.UpsertAsync(GetUserId(), locationId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_NOT_FOUND", "Movie location not found.") : Ok(result);
        }
        catch (MovieLocationGeographySheetLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_LOCATION_GEOGRAPHY_SHEET_LOCKED", exception.Message); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_GEOGRAPHY_SHEET_INVALID", exception.Message); }
    }

    [HttpPost("locations/{locationId:guid}/geography-sheet/variants")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLocationGeographyVariant(Guid locationId, MovieLocationGeographyVariantRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await geographySheets.AddVariantAsync(GetUserId(), locationId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_NOT_FOUND", "Movie location not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_GEOGRAPHY_VARIANT_INVALID", exception.Message); }
    }

    [HttpPost("locations/{locationId:guid}/geography-sheet/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveLocationGeographySheet(Guid locationId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await geographySheets.ApproveSheetAsync(GetUserId(), locationId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_GEOGRAPHY_SHEET_NOT_FOUND", "Location geography sheet not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_GEOGRAPHY_SHEET_INVALID", exception.Message); }
    }

    [HttpPost("location-geography-variants/{variantId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveLocationGeographyVariant(Guid variantId, CancellationToken cancellationToken)
    {
        var result = await geographySheets.ApproveVariantAsync(GetUserId(), variantId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_LOCATION_GEOGRAPHY_VARIANT_NOT_FOUND", "Location geography variant not found.") : Ok(result);
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

    [HttpGet("projects/{id:guid}/prop-bible")]
    public async Task<IActionResult> GetPropBible(Guid id, CancellationToken cancellationToken)
    {
        var result = await propBible.GetProjectAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{id:guid}/prop-bible/recurring-detection")]
    [HttpGet("projects/{id:guid}/prop-bible/recurring-props")]
    public async Task<IActionResult> DetectRecurringProps(Guid id, CancellationToken cancellationToken)
    {
        var result = await propBible.DetectRecurringAsync(GetUserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("props/{propId:guid}/production-sheet")]
    [HttpGet("props/{propId:guid}/prop-bible")]
    public async Task<IActionResult> GetPropProductionSheet(Guid propId, CancellationToken cancellationToken)
    {
        var result = await propBible.GetPropAsync(GetUserId(), propId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_NOT_FOUND", "Movie prop not found.") : Ok(result);
    }

    [HttpPut("props/{propId:guid}/production-sheet")]
    [HttpPut("props/{propId:guid}/prop-bible")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePropProductionSheet(Guid propId, MoviePropBibleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.UpsertAsync(GetUserId(), propId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_NOT_FOUND", "Movie prop not found.") : Ok(result);
        }
        catch (MoviePropBibleLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_PROP_BIBLE_LOCKED", exception.Message); }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
    }

    [HttpPost("props/{propId:guid}/production-sheet/references")]
    [HttpPost("props/{propId:guid}/prop-bible/references")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPropBibleReference(Guid propId, MoviePropBibleReferenceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.AddReferenceAsync(GetUserId(), propId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_NOT_FOUND", "Movie prop not found.") : Ok(result);
        }
        catch (MoviePropBibleLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_PROP_BIBLE_LOCKED", exception.Message); }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
    }

    [HttpPost("props/{propId:guid}/production-sheet/variants")]
    [HttpPost("props/{propId:guid}/prop-bible/variants")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPropBibleVariant(Guid propId, MoviePropBibleVariantRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.AddVariantAsync(GetUserId(), propId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_NOT_FOUND", "Movie prop not found.") : Ok(result);
        }
        catch (MoviePropBibleLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_PROP_BIBLE_LOCKED", exception.Message); }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
    }

    [HttpPost("props/{propId:guid}/production-sheet/versions")]
    [HttpPost("props/{propId:guid}/prop-bible/versions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePropBibleVersion(Guid propId, MoviePropBibleVersionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.CreateVersionAsync(GetUserId(), propId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_NOT_FOUND", "Movie prop not found.") : Ok(result);
        }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
    }

    [HttpPost("prop-bible/versions/{versionId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewPropBibleVersion(Guid versionId, MoviePropBibleReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.ReviewVersionAsync(GetUserId(), versionId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_BIBLE_VERSION_NOT_FOUND", "Prop Bible version not found.") : Ok(result);
        }
        catch (MoviePropBibleLockedException exception) { return ApiResults.Error(this, 409, "MOVIE_PROP_BIBLE_LOCKED", exception.Message); }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
    }

    [HttpPost("prop-bible/versions/{versionId:guid}/lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockPropBibleVersion(Guid versionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await propBible.LockVersionAsync(GetUserId(), versionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROP_BIBLE_VERSION_NOT_FOUND", "Prop Bible version not found.") : Ok(result);
        }
        catch (MoviePropBibleValidationException exception) { return ApiResults.Error(this, 409, "MOVIE_PROP_BIBLE_INVALID", exception.Message); }
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

    [HttpGet("shots/{shotId:guid}/production-complexity")]
    public async Task<IActionResult> GetProductionComplexity(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await complexity.GetAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? NoContent() : Ok(result);
    }

    [HttpPut("shots/{shotId:guid}/production-complexity")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveProductionComplexity(Guid shotId, MovieProductionComplexityProfileRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await complexity.SaveAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieProductionComplexityValidationException exception)
        {
            return ApiResults.Error(this, 400, "MOVIE_PRODUCTION_COMPLEXITY_INVALID", string.Join(" ", exception.Result.Findings.Select(item => item.Message)));
        }
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
    [HttpPost("shots/{shotId:guid}/cinematography/plan")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlanCinematography(Guid shotId, MovieCinematographyPlanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await cinematographyPlanning.PlanAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CINEMATOGRAPHY_PLAN_INVALID", exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/camera-profile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PlanCameraProfile(Guid shotId, MovieCinematographyPlanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await cinematographyPlanning.PlanAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CAMERA_PROFILE_INVALID", exception.Message); }
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

    [HttpGet("projects/{id:guid}/continuity-review")]
    public async Task<IActionResult> ReviewProductionContinuity(Guid id, [FromQuery] Guid? sceneId, [FromQuery] Guid? shotId, CancellationToken cancellationToken)
    {
        var result = await productionContinuity.ReviewProjectAsync(GetUserId(), id, sceneId, shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("scenes/{sceneId:guid}/continuity-review")]
    public async Task<IActionResult> ReviewSceneProductionContinuity(Guid sceneId, CancellationToken cancellationToken)
    {
        var result = await productionContinuity.ReviewSceneAsync(GetUserId(), sceneId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result);
    }

    [HttpGet("shots/{shotId:guid}/continuity-review")]
    public async Task<IActionResult> ReviewShotProductionContinuity(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await productionContinuity.ReviewShotAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }
    [HttpGet("shots/{shotId:guid}/production-references")]
    public async Task<IActionResult> GetShotProductionReferences(Guid shotId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await productionReferences.GetForShotAsync(GetUserId(), shotId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieProductionReferenceException exception) { return ApiResults.Error(this, 400, "MOVIE_PRODUCTION_REFERENCES_INVALID", exception.Message); }
    }

    [HttpGet("shots/{shotId:guid}/production/preflight")]
    public async Task<IActionResult> GetProductionPreflight(
        Guid shotId,
        [FromQuery] Guid? sourceVersionId,
        [FromQuery] string? targetResolution,
        [FromQuery] string? qualityTier,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await productionPreflight.EvaluateAsync(
                GetUserId(),
                shotId,
                new MovieProductionPreflightRequest(sourceVersionId, targetResolution, qualityTier),
                cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionReferenceException exception) { return ApiResults.Error(this, 400, "MOVIE_PRODUCTION_REFERENCES_INVALID", exception.Message); }
    }

    [HttpGet("shots/{shotId:guid}/reference-readiness")]
    public async Task<IActionResult> GetReferenceReadiness(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await referenceReadiness.GetAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPost("shots/{shotId:guid}/reference-readiness/override")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OverrideReferenceReadiness(Guid shotId, MovieReferenceReadinessOverrideRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await referenceReadiness.RecordOverrideAsync(GetUserId(), shotId, request, MovieReferenceReadinessOverrideSources.ProductionRender, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
        }
        catch (MovieReferenceReadinessException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
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

    [HttpPost("shots/{shotId:guid}/production/keyframe")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> GenerateKeyframe(Guid shotId, MovieKeyframeGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.QueueKeyframeGenerationAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpPost("production/versions/{versionId:guid}/select-keyframe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectKeyframe(Guid versionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await movies.SelectKeyframeAsync(GetUserId(), versionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_VERSION_NOT_FOUND", "Production version not found.") : Ok(result);
        }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
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
        catch (MovieReferenceReadinessException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
    }

    [HttpPost("shots/{shotId:guid}/production/execute")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> ExecuteProductionShot(Guid shotId, MovieShotExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await shotExecution.QueueAsync(GetUserId(), shotId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieShotExecutionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieReferenceReadinessException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
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
        try
        {
            var result = await movies.GenerateSceneAsync(GetUserId(), id, sceneId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Accepted(result);
        }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SCENE_NOT_READY", exception.Message); }
        catch (MovieReferenceReadinessException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
        catch (MovieStudioCostGuardException exception) { return ApiResults.Error(this, 402, exception.Code, exception.Message); }
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
        catch (MovieReferenceReadinessException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
        catch (MovieStudioCostGuardException exception) { return ApiResults.Error(this, 402, exception.Code, exception.Message); }
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

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
public sealed class MovieStudioController(IMovieStudioService movies, IMovieStoryService stories) : ControllerBase
{
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

    [HttpPatch("projects/{id:guid}/guide")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGuide(Guid id, MovieStudioGuideRequest request, CancellationToken cancellationToken)
    {
        var result = await movies.UpdateGuideAsync(GetUserId(), id, request, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
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

    [HttpPost("projects/{id:guid}/locations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLocation(Guid id, MovieStudioLocationRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddLocationAsync(GetUserId(), id, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_LOCATION_INVALID", exception.Message); }
    }

    [HttpPost("scenes/{sceneId:guid}/shots")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddShot(Guid sceneId, MovieStudioShotRequest request, CancellationToken cancellationToken)
    {
        try { var result = await movies.AddShotAsync(GetUserId(), sceneId, request, cancellationToken); return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result); }
        catch (MovieStudioValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_SHOT_INVALID", exception.Message); }
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
        var result = await movies.GenerateShotAsync(GetUserId(), shotId, request, cancellationToken, Request.Headers["Idempotency-Key"].FirstOrDefault());
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
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
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

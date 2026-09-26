using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Generation;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieStudioController(IMovieStudioService movies) : ControllerBase
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

    [HttpGet("shots/{shotId:guid}/production")]
    public async Task<IActionResult> GetShotProduction(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await movies.GetShotProductionAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
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
        catch (MovieProductionValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
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

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

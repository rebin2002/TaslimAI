using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Taslim.Api.Generation;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-sound")]
public sealed class MovieSoundController(IMovieSoundService sounds) : ControllerBase
{
    [HttpGet("projects/{movieProjectId:guid}/library")]
    public async Task<IActionResult> GetLibrary(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await sounds.GetLibraryAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/library")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLibraryReference(Guid movieProjectId, MovieSoundLibraryReferenceRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sounds.AddLibraryReferenceAsync(GetUserId(), movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("scenes/{sceneId:guid}/tracks")]
    public async Task<IActionResult> GetSceneTracks(Guid sceneId, CancellationToken cancellationToken)
    {
        var result = await sounds.GetForSceneAsync(GetUserId(), sceneId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result);
    }

    [HttpPost("scenes/{sceneId:guid}/tracks")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> CreateSceneTrack(Guid sceneId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sounds.CreateForSceneAsync(GetUserId(), sceneId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Accepted(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("shots/{shotId:guid}/tracks")]
    public async Task<IActionResult> GetShotTracks(Guid shotId, CancellationToken cancellationToken)
    {
        var result = await sounds.GetForShotAsync(GetUserId(), shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPost("shots/{shotId:guid}/tracks")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> CreateShotTrack(Guid shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sounds.CreateForShotAsync(GetUserId(), shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("tracks/{trackId:guid}")]
    public async Task<IActionResult> GetTrack(Guid trackId, CancellationToken cancellationToken)
    {
        var result = await sounds.GetAsync(GetUserId(), trackId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SOUND_TRACK_NOT_FOUND", "Sound track not found.") : Ok(result);
    }

    [HttpPost("tracks/{trackId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid trackId, MovieSoundReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await sounds.ReviewAsync(GetUserId(), trackId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SOUND_TRACK_NOT_FOUND", "Sound track not found.") : Ok(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

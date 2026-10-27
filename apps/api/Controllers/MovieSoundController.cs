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
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await sounds.GetLibraryAsync(userId, movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/library")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLibraryReference(Guid movieProjectId, MovieSoundLibraryReferenceRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await sounds.AddLibraryReferenceAsync(userId, movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    [HttpGet("scenes/{sceneId:guid}/tracks")]
    public async Task<IActionResult> GetSceneTracks(Guid sceneId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await sounds.GetForSceneAsync(userId, sceneId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Ok(result);
    }

    [HttpPost("scenes/{sceneId:guid}/tracks")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> CreateSceneTrack(Guid sceneId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await sounds.CreateForSceneAsync(userId, sceneId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SCENE_NOT_FOUND", "Movie scene not found.") : Accepted(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("shots/{shotId:guid}/tracks")]
    public async Task<IActionResult> GetShotTracks(Guid shotId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await sounds.GetForShotAsync(userId, shotId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    }

    [HttpPost("shots/{shotId:guid}/tracks")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> CreateShotTrack(Guid shotId, MovieSoundTrackRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await sounds.CreateForShotAsync(userId, shotId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Accepted(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("tracks/{trackId:guid}")]
    public async Task<IActionResult> GetTrack(Guid trackId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await sounds.GetAsync(userId, trackId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_SOUND_TRACK_NOT_FOUND", "Sound track not found.") : Ok(result);
    }

    [HttpPost("tracks/{trackId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Review(Guid trackId, MovieSoundReviewRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await sounds.ReviewAsync(userId, trackId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_SOUND_TRACK_NOT_FOUND", "Sound track not found.") : Ok(result);
        }
        catch (MovieSoundValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieCaptionsController(IMovieCaptionService captions) : ControllerBase
{
    [HttpGet("projects/{movieProjectId:guid}/caption-tracks")]
    public async Task<IActionResult> Tracks(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await captions.GetTracksAsync(UserId(), movieProjectId, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpGet("projects/{movieProjectId:guid}/caption-timeline")]
    public async Task<IActionResult> Timeline(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await captions.GetTimelineAsync(UserId(), movieProjectId, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("projects/{movieProjectId:guid}/caption-tracks")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTrack(Guid movieProjectId, MovieCaptionTrackRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await captions.CreateTrackAsync(UserId(), movieProjectId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpGet("caption-tracks/{trackId:guid}")]
    public async Task<IActionResult> Track(Guid trackId, CancellationToken cancellationToken)
    {
        var result = await captions.GetTrackAsync(UserId(), trackId, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_CAPTION_TRACK_NOT_FOUND", "Caption track not found.") : Ok(result);
    }

    [HttpPost("caption-tracks/{trackId:guid}/cues")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCue(Guid trackId, MovieCaptionCueRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await captions.AddCueAsync(UserId(), trackId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_CAPTION_TRACK_NOT_FOUND", "Caption track not found.") : Ok(result);
    });

    [HttpPatch("caption-cues/{cueId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCue(Guid cueId, MovieCaptionCueRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await captions.UpdateCueAsync(UserId(), cueId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_CAPTION_CUE_NOT_FOUND", "Caption cue not found.") : Ok(result);
    });

    [HttpDelete("caption-cues/{cueId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCue(Guid cueId, CancellationToken cancellationToken) => await Execute(async () =>
    {
        await captions.DeleteCueAsync(UserId(), cueId, cancellationToken);
        return NoContent();
    });

    [HttpPost("projects/{movieProjectId:guid}/caption-tracks/import")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(Guid movieProjectId, MovieCaptionImportRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await captions.ImportAsync(UserId(), movieProjectId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpGet("caption-tracks/{trackId:guid}/export")]
    public async Task<IActionResult> Export(Guid trackId, [FromQuery] string format = MovieCaptionFormats.Srt, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await captions.ExportAsync(UserId(), trackId, format, cancellationToken);
            return result is null ? NotFoundResult("MOVIE_CAPTION_TRACK_NOT_FOUND", "Caption track not found.") : File(System.Text.Encoding.UTF8.GetBytes(result.Content), result.ContentType, result.FileName);
        }
        catch (MovieCaptionFormatException exception) { return ApiResults.Error(this, 400, "MOVIE_CAPTION_FORMAT_INVALID", exception.Message); }
    }

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (MovieCaptionValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_CAPTION_REQUEST_INVALID", exception.Message); }
        catch (MovieCaptionFormatException exception) { return ApiResults.Error(this, 400, "MOVIE_CAPTION_FORMAT_INVALID", exception.Message); }
    }

    private IActionResult NotFoundResult(string code, string message) => ApiResults.Error(this, 404, code, message);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

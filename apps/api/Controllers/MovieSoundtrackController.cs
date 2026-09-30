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
public sealed class MovieSoundtrackController(IMovieSoundtrackService soundtrack) : ControllerBase
{
    [HttpGet("projects/{projectId:guid}/soundtrack")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.GetProjectAsync(UserId(), projectId, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPost("projects/{projectId:guid}/soundtrack/cues")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCue(Guid projectId, MovieSoundtrackCueRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.CreateCueAsync(UserId(), projectId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : CreatedAtAction(nameof(GetCue), new { cueId = result.Id }, result);
    });

    [HttpGet("soundtrack/cues/{cueId:guid}")]
    public async Task<IActionResult> GetCue(Guid cueId, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.GetCueAsync(UserId(), cueId, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SOUNDTRACK_CUE_NOT_FOUND", "Soundtrack cue not found.") : Ok(result);
    });

    [HttpPatch("soundtrack/cues/{cueId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCue(Guid cueId, MovieSoundtrackCueUpdateRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.UpdateCueAsync(UserId(), cueId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SOUNDTRACK_CUE_NOT_FOUND", "Soundtrack cue not found.") : Ok(result);
    });

    [HttpPost("soundtrack/cues/{cueId:guid}/versions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateVersion(Guid cueId, MovieSoundtrackCueVersionRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.CreateVersionAsync(UserId(), cueId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SOUNDTRACK_CUE_NOT_FOUND", "Soundtrack cue not found.") : Ok(result);
    });

    [HttpPost("soundtrack/versions/{versionId:guid}/review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewVersion(Guid versionId, MovieSoundtrackCueVersionReviewRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await soundtrack.ReviewVersionAsync(UserId(), versionId, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SOUNDTRACK_VERSION_NOT_FOUND", "Soundtrack cue version not found.") : Ok(result);
    });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (MovieSoundtrackValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    private IActionResult NotFoundResult(string code, string message) => ApiResults.Error(this, 404, code, message);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

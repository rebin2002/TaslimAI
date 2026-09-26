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
public sealed class MovieStudioV2Controller(IMovieV2Service movies) : ControllerBase
{
    [HttpGet("projects/{id:guid}/hierarchy")]
    public async Task<IActionResult> Hierarchy(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.GetHierarchyAsync(UserId(), id, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpGet("projects/{id:guid}/overview")]
    public async Task<IActionResult> Overview(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.GetOverviewAsync(UserId(), id, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPost("projects/{id:guid}/acts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAct(Guid id, MovieV2ActRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.AddActAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPost("acts/{id:guid}/sequences")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSequence(Guid id, MovieV2SequenceRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.AddSequenceAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_ACT_NOT_FOUND", "Movie act not found.") : Ok(result);
    });

    [HttpPost("sequences/{id:guid}/scenes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddScene(Guid id, MovieV2SceneRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.AddSceneAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SEQUENCE_NOT_FOUND", "Movie sequence not found.") : Ok(result);
    });

    [HttpPost("shots/{id:guid}/takes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTake(Guid id, MovieV2TakeRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.AddTakeAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_SHOT_NOT_FOUND", "Movie shot not found.") : Ok(result);
    });

    [HttpPatch("projects/{id:guid}/settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSettings(Guid id, MovieV2SettingsRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.UpdateSettingsAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPost("projects/{id:guid}/archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.SetProjectArchivedAsync(UserId(), id, true, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPost("projects/{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.SetProjectArchivedAsync(UserId(), id, false, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    });

    [HttpPatch("{entity:regex(^acts$|^sequences$|^scenes$|^shots$|^takes$)}/{id:guid}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(string entity, Guid id, MovieV2StatusRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        await movies.SetStatusAsync(UserId(), entity[..^1], id, request.Status, cancellationToken);
        return NoContent();
    });

    [HttpPost("takes/{id:guid}/select")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectTake(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        await movies.SelectTakeAsync(UserId(), id, false, cancellationToken);
        return NoContent();
    });

    [HttpPost("takes/{id:guid}/finalize")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> FinalizeTake(Guid id, CancellationToken cancellationToken) => await Execute(async () =>
    {
        await movies.SelectTakeAsync(UserId(), id, true, cancellationToken);
        return NoContent();
    });

    [HttpPost("takes/{id:guid}/approvals")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveTake(Guid id, MovieV2ApprovalRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        var result = await movies.ApproveTakeAsync(UserId(), id, request, cancellationToken);
        return result is null ? NotFoundResult("MOVIE_TAKE_NOT_FOUND", "Movie take not found.") : Ok(result);
    });

    [HttpPatch("{entity:regex(^acts$|^sequences$|^scenes$|^shots$|^takes$)}/{id:guid}/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reorder(string entity, Guid id, MovieV2OrderRequest request, CancellationToken cancellationToken) => await Execute(async () =>
    {
        await movies.ReorderAsync(UserId(), entity[..^1], id, request.Sequence, cancellationToken);
        return NoContent();
    });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (MovieV2ValidationException exception) { return ApiResults.Error(this, 400, "MOVIE_V2_REQUEST_INVALID", exception.Message); }
        catch (MovieV2NotFoundException) { return NotFoundResult("MOVIE_RESOURCE_NOT_FOUND", "Movie resource not found."); }
    }

    private IActionResult NotFoundResult(string code, string message) => ApiResults.Error(this, 404, code, message);
    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

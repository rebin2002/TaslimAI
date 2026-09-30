using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Generation;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieDialogueController(IMovieDialogueProductionService dialogue) : ControllerBase
{
    [HttpGet("clips/{clipId:guid}/dialogue")]
    public async Task<IActionResult> Get(Guid clipId, CancellationToken cancellationToken)
    {
        var result = await dialogue.GetClipAsync(UserId(), clipId, cancellationToken);
        return result is null ? ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_CLIP_NOT_FOUND", "Movie clip not found.") : Ok(result);
    }

    [HttpPost("clips/{clipId:guid}/dialogue")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLine(Guid clipId, MovieDialogueLineRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dialogue.AddLineAsync(UserId(), clipId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_CLIP_NOT_FOUND", "Movie clip not found.") : Ok(result);
        }
        catch (MovieDialogueValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
    }

    [HttpPost("dialogue/{lineId:guid}/takes")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> QueueTake(Guid lineId, MovieDialogueTakeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dialogue.QueueTakeAsync(UserId(), lineId, request, Request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            return result is null ? ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_DIALOGUE_LINE_NOT_FOUND", "Dialogue line not found.") : Accepted(result);
        }
        catch (MovieDialogueValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
        catch (GenerationJobForbiddenException)
        {
            return Forbid();
        }
    }

    [HttpPost("dialogue/takes/{takeId:guid}/approval")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid takeId, MovieDialogueTakeApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dialogue.ApproveTakeAsync(UserId(), takeId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_DIALOGUE_TAKE_NOT_FOUND", "Dialogue take not found.") : Ok(result);
        }
        catch (MovieDialogueValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    [HttpPost("dialogue/takes/{takeId:guid}/select")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Select(Guid takeId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await dialogue.SelectTakeAsync(UserId(), takeId, cancellationToken);
            return result is null ? ApiResults.Error(this, StatusCodes.Status404NotFound, "MOVIE_DIALOGUE_TAKE_NOT_FOUND", "Dialogue take not found.") : Ok(result);
        }
        catch (MovieDialogueValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

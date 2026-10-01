using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio/projects/{movieProjectId:guid}/production-kit")]
public sealed class MovieProductionKitController(IMovieProductionKitService kits) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await kits.GetAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_KIT_NOT_FOUND", "Production Kit not found.") : Ok(result);
    }

    [HttpGet("history")]
    public Task<IActionResult> History(Guid movieProjectId, CancellationToken cancellationToken) => Get(movieProjectId, cancellationToken);

    [HttpPost("revisions")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRevision(Guid movieProjectId, MovieProductionKitRevisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kits.CreateRevisionAsync(GetUserId(), movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : CreatedAtAction(nameof(Get), new { movieProjectId }, result);
        }
        catch (MovieProductionKitValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionKitLifecycleException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message, null); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpPost("review")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitForReview(Guid movieProjectId, MovieProductionKitReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kits.SubmitForReviewAsync(GetUserId(), movieProjectId, request.RevisionNumber ?? 0, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_KIT_NOT_FOUND", "Production Kit not found.") : Ok(result);
        }
        catch (MovieProductionKitValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionKitLifecycleException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message, null); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpPost("approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid movieProjectId, MovieProductionKitReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kits.ApproveAsync(GetUserId(), movieProjectId, request.RevisionNumber ?? 0, request.Note, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_KIT_NOT_FOUND", "Production Kit not found.") : Ok(result);
        }
        catch (MovieProductionKitValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionKitLifecycleException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message, null); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpPost("lock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Lock(Guid movieProjectId, MovieProductionKitReviewRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kits.LockAsync(GetUserId(), movieProjectId, request.RevisionNumber ?? 0, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_KIT_NOT_FOUND", "Production Kit not found.") : Ok(result);
        }
        catch (MovieProductionKitValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionKitLifecycleException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message, null); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    [HttpPost("unlock")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(Guid movieProjectId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await kits.UnlockAsync(GetUserId(), movieProjectId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PRODUCTION_KIT_NOT_FOUND", "Production Kit not found.") : Ok(result);
        }
        catch (MovieProductionKitValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieProductionKitLifecycleException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message, null); }
        catch (MovieCollaborationForbiddenException) { return Forbid(); }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

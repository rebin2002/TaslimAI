using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Generation;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio")]
public sealed class MovieFinalAssemblyController(IMovieFinalAssemblyService assemblies) : ControllerBase
{
    [HttpPost("projects/{id:guid}/final-assembly")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Queue(Guid id, MovieFinalAssemblyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await assemblies.RequestAsync(UserId(), id, request, HttpContext.Request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Accepted(result);
        }
        catch (MovieFinalAssemblyValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (MovieCollaborationForbiddenException exception) { return ApiResults.Error(this, 403, "MOVIE_PERMISSION_DENIED", exception.Message); }
        catch (GenerationJobValidationException exception) { return ApiResults.Error(this, 400, exception.Code, exception.Message); }
        catch (GenerationJobForbiddenException) { return Forbid(); }
    }

    [HttpGet("final-assemblies/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await assemblies.GetAsync(UserId(), id, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_FINAL_ASSEMBLY_NOT_FOUND", "Final assembly not found.") : Ok(result);
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

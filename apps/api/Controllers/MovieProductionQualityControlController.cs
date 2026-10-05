using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

/// <summary>
/// Provider-neutral QC review surface. Previewing a decision evaluates supplied evidence only;
/// it does not persist a decision, queue work, charge usage, or call a media provider.
/// </summary>
[ApiController]
[Authorize]
[Route("api/movie-studio/projects/{movieProjectId:guid}/production-qc")]
public sealed class MovieProductionQualityControlController(
    IMovieProductionQualityControl qualityControl,
    MovieAuthorizationService authorization) : ControllerBase
{
    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(
        Guid movieProjectId,
        MovieProductionQcRequest? request,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanPermissionAsync(UserId(), movieProjectId, MoviePermissions.View, cancellationToken))
            return ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.");

        try
        {
            return Ok(qualityControl.Evaluate(request!));
        }
        catch (ArgumentException exception)
        {
            return ApiResults.Error(this, 400, "MOVIE_QC_REQUEST_INVALID", exception.Message);
        }
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

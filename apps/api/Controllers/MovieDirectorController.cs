using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-director")]
public sealed class MovieDirectorController(IMovieDirectorService director) : ControllerBase
{
    [HttpPost("projects/{movieProjectId:guid}/proposals")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateProposal(Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await director.CreateProposalAsync(GetUserId(), movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 400, "DIRECTOR_REQUEST_INVALID", exception.Message); }
    }

    [HttpGet("proposals/{proposalId:guid}")]
    public async Task<IActionResult> GetProposal(Guid proposalId, CancellationToken cancellationToken)
    {
        var result = await director.GetProposalAsync(GetUserId(), proposalId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
    }

    [HttpGet("projects/{movieProjectId:guid}/history")]
    public async Task<IActionResult> History(Guid movieProjectId, CancellationToken cancellationToken)
    {
        var result = await director.GetHistoryAsync(GetUserId(), movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("proposals/{proposalId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid proposalId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await director.ApproveProposalAsync(GetUserId(), proposalId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_NOT_PENDING", exception.Message); }
    }

    [HttpPost("proposals/{proposalId:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid proposalId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await director.RejectProposalAsync(GetUserId(), proposalId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_NOT_PENDING", exception.Message); }
    }

    [HttpPost("actions/{actionId:guid}/execute")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(Guid actionId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await director.ExecuteActionAsync(GetUserId(), actionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_ACTION_NOT_FOUND", "Director action not found.") : Ok(result);
        }
        catch (DirectorActionNotApprovedException exception) { return ApiResults.Error(this, 409, "DIRECTOR_APPROVAL_REQUIRED", exception.Message); }
        catch (DirectorActionExecutionException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

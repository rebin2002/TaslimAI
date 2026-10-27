using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-studio/projects/{movieProjectId:guid}/collaboration")]
public sealed class MovieCollaborationController(IMovieCollaborationService collaboration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await collaboration.GetAsync(userId, movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "MOVIE_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("team")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(Guid movieProjectId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.AddMemberAsync(userId, movieProjectId, request, cancellationToken));
    }

    [HttpPatch("team/{memberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMember(Guid movieProjectId, Guid memberId, MovieTeamMemberRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.UpdateMemberAsync(userId, movieProjectId, memberId, request, cancellationToken));
    }

    [HttpDelete("team/{memberId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(Guid movieProjectId, Guid memberId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var removed = await collaboration.RemoveMemberAsync(userId, movieProjectId, memberId, cancellationToken);
            return removed ? NoContent() : ApiResults.Error(this, 404, "MOVIE_TEAM_MEMBER_NOT_FOUND", "Movie team member not found.");
        }
        catch (Exception exception) { return MapError(exception); }
    }

    [HttpPost("comments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(Guid movieProjectId, MovieCommentRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.AddCommentAsync(userId, movieProjectId, request, cancellationToken));
    }

    [HttpPost("comments/{commentId:guid}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveComment(Guid movieProjectId, Guid commentId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.ResolveCommentAsync(userId, movieProjectId, commentId, cancellationToken));
    }

    [HttpPost("reviews")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestReview(Guid movieProjectId, MovieReviewRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.RequestReviewAsync(userId, movieProjectId, request, cancellationToken));
    }

    [HttpPost("reviews/{reviewId:guid}/decision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DecideReview(Guid movieProjectId, Guid reviewId, MovieReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.DecideReviewAsync(userId, movieProjectId, reviewId, request, cancellationToken));
    }

    [HttpPost("assignments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAssignment(Guid movieProjectId, MovieAssignmentRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.AddAssignmentAsync(userId, movieProjectId, request, cancellationToken));
    }

    [HttpPatch("assignments/{assignmentId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAssignment(Guid movieProjectId, Guid assignmentId, MovieAssignmentUpdateRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.UpdateAssignmentAsync(userId, movieProjectId, assignmentId, request, cancellationToken));
    }

    [HttpPost("credits")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCredit(Guid movieProjectId, MovieProductionCreditRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return await Execute(async () => await collaboration.AddCreditAsync(userId, movieProjectId, request, cancellationToken));
    }

    [HttpDelete("credits/{creditId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCredit(Guid movieProjectId, Guid creditId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var removed = await collaboration.RemoveCreditAsync(userId, movieProjectId, creditId, cancellationToken);
            return removed ? NoContent() : ApiResults.Error(this, 404, "MOVIE_CREDIT_NOT_FOUND", "Movie production credit not found.");
        }
        catch (Exception exception) { return MapError(exception); }
    }

    private async Task<IActionResult> Execute<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            var result = await action();
            return result is null ? ApiResults.Error(this, 404, "MOVIE_OBJECT_NOT_FOUND", "Movie collaboration object not found.") : Ok(result);
        }
        catch (Exception exception) { return MapError(exception); }
    }

    private IActionResult MapError(Exception exception) => exception switch
    {
        MovieCollaborationForbiddenException => Forbid(),
        MovieCollaborationValidationException validation => ApiResults.Error(this, 400, "MOVIE_COLLABORATION_INVALID", validation.Message),
        _ => throw exception,
    };

    private bool TryGetUserId(out Guid userId) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}

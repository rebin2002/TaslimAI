using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/movie-director")]
public sealed class MovieDirectorController(IMovieDirectorService director, IMovieSalvageDirector salvage, MovieAuthorizationService authorization, TaslimDbContext db) : ControllerBase
{
    [HttpPost("salvage-plan")]
    [ValidateAntiForgeryToken]
    public IActionResult CreateSalvagePlan(MovieSalvageDirectorRequest request)
    {
        try { return Ok(salvage.Plan(request)); }
        catch (ArgumentNullException exception) { return ApiResults.Error(this, 400, "MOVIE_SALVAGE_REQUEST_INVALID", exception.Message); }
        catch (ArgumentException exception) { return ApiResults.Error(this, 400, "MOVIE_SALVAGE_REQUEST_INVALID", exception.Message); }
    }

    [HttpPost("projects/{movieProjectId:guid}/proposals")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> CreateProposal(Guid movieProjectId, DirectorProposalRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await director.CreateProposalAsync(userId, movieProjectId, request, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 400, "DIRECTOR_REQUEST_INVALID", exception.Message); }
        catch (DirectorCreativeOutputValidationException) { return ApiResults.Error(this, 422, "DIRECTOR_CREATIVE_OUTPUT_INVALID", "The Director could not produce a usable Story proposal. Try a clearer goal or add the missing locked context."); }
        catch (DirectorStoryCreativeException exception) { return ApiResults.Error(this, exception.Code == DirectorStoryCreativeFailureCodes.Unavailable ? 503 : 422, exception.Code, exception.Message); }
        catch (DirectorScenePlanCreativeException exception) { return ApiResults.Error(this, exception.Code == DirectorScenePlanCreativeFailureCodes.Unavailable ? 503 : 422, exception.Code, exception.Message); }
        catch (DirectorShotPlanningException exception) { return ApiResults.Error(this, exception.Code == DirectorShotPlanningFailureCodes.Unavailable ? 503 : 422, exception.Code, exception.Message); }
        catch (DirectorContextTargetException exception) { return ApiResults.Error(this, 400, "DIRECTOR_CONTEXT_TARGET_INVALID", exception.Message); }
        catch (DirectorContextBudgetException exception) { return ApiResults.Error(this, 413, "DIRECTOR_CONTEXT_BUDGET_EXCEEDED", exception.Message); }
        catch (DirectorSynopsisGenerationException exception) { return ApiResults.Error(this, 503, "DIRECTOR_SYNOPSIS_UNAVAILABLE", exception.Message); }
    }

    [HttpPost("projects/{movieProjectId:guid}/intercut-proposals")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateIntercutProposal(Guid movieProjectId, MovieIntercutPlanRequest request, CancellationToken cancellationToken)
    {
        // The authentication middleware establishes identity, but an authenticated claim
        // can still be absent or malformed; do not let Guid.Parse turn it into HTTP 500.
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (!await authorization.CanAsync(userId, movieProjectId, MovieOperationalActions.DirectorProposalCreate, cancellationToken))
            return Forbid();
        try
        {
            if (request is null)
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipRequired, "Intercut coverage candidates are required.");
            if (request.TimelineId is Guid requestedTimelineId && requestedTimelineId != Guid.Empty)
            {
                var timeline = await db.MovieTimelines.AsNoTracking()
                    .Where(item => item.Id == requestedTimelineId)
                    .Select(item => new { item.MovieProjectId, item.CurrentRevisionNumber })
                    .SingleOrDefaultAsync(cancellationToken);
                if (timeline is null || timeline.MovieProjectId != movieProjectId)
                    throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.TimelineProjectMismatch, "The intercut timeline must belong to the requested movie project.");
                // A proposal based on an older canonical revision must be rebuilt before review.
                // This check is read-only and does not persist a timeline or start generation.
                if (timeline.CurrentRevisionNumber != request.BaseTimelineVersion)
                    throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.TimelineVersionMismatch, "The intercut timeline revision has changed; refresh the timeline before planning.");
            }

            // Pure validation bounds the request before any database lookup; this is still
            // review-only and never enqueues, persists, publishes media, or reserves usage.
            var proposal = MovieIntercutPlanner.Build(movieProjectId, request);
            var requestedClipIds = request.Clips.Select(item => item.ClipId).ToArray();
            var canonicalClips = await db.MovieClips.AsNoTracking()
                .Where(clip => requestedClipIds.Contains(clip.Id))
                .Select(clip => new
                {
                    clip.Id,
                    clip.MovieProjectId,
                    clip.MovieSceneId,
                    clip.MovieShotId,
                    SceneProjectId = clip.MovieScene != null ? (Guid?)clip.MovieScene.MovieProjectId : null,
                    SceneSequence = clip.MovieScene != null ? (int?)clip.MovieScene.Sequence : null,
                    ShotSceneId = clip.MovieShot != null ? (Guid?)clip.MovieShot.MovieSceneId : null,
                    ShotSequence = clip.MovieShot != null ? (int?)clip.MovieShot.Sequence : null,
                })
                .ToDictionaryAsync(clip => clip.Id, cancellationToken);
            if (canonicalClips.Count != request.Clips.Count ||
                request.Clips.Any(item =>
                    !canonicalClips.TryGetValue(item.ClipId, out var clip)
                    || clip.MovieProjectId != movieProjectId
                    || clip.MovieSceneId != item.MovieSceneId
                    || clip.MovieShotId != item.MovieShotId
                    || clip.SceneProjectId != movieProjectId
                    || clip.ShotSceneId != item.MovieSceneId
                    || clip.SceneSequence != item.SceneSequence
                    || clip.ShotSequence != item.ShotSequence))
                throw new MovieIntercutPlanningException(MovieIntercutPlanCodes.ClipOwnershipMismatch, "Intercut coverage must reference canonical clips, shots, and scenes in the requested movie project.");

            // The pure planner cannot check database ownership; the controller has now verified it.
            var verifiedFindings = proposal.Findings
                .Where(item => item != "canonical_clip_and_shot_ownership_not_verified_in_review_only_proposal")
                .Append("canonical_clip_shot_scene_ownership_verified")
                .ToArray();
            return Ok(proposal with { Findings = verifiedFindings });
        }
        catch (MovieIntercutPlanningException exception) { return ApiResults.Error(this, exception.Code == MovieIntercutPlanCodes.TimelineVersionMismatch ? 409 : 400, exception.Code, exception.Message); }
    }

    [HttpGet("proposals/{proposalId:guid}")]
    public async Task<IActionResult> GetProposal(Guid proposalId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await director.GetProposalAsync(userId, proposalId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
    }

    [HttpGet("projects/{movieProjectId:guid}/history")]
    public async Task<IActionResult> History(Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await director.GetHistoryAsync(userId, movieProjectId, cancellationToken);
        return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROJECT_NOT_FOUND", "Movie project not found.") : Ok(result);
    }

    [HttpPost("proposals/{proposalId:guid}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid proposalId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await director.ApproveProposalAsync(userId, proposalId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_NOT_PENDING", exception.Message); }
        catch (DirectorProposalStaleException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_STALE", exception.Message); }
    }

    [HttpPost("proposals/{proposalId:guid}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid proposalId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await director.RejectProposalAsync(userId, proposalId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_PROPOSAL_NOT_FOUND", "Director proposal not found.") : Ok(result);
        }
        catch (DirectorValidationException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_NOT_PENDING", exception.Message); }
    }

    [HttpPost("actions/{actionId:guid}/execute")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Execute(Guid actionId, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        try
        {
            var result = await director.ExecuteActionAsync(userId, actionId, cancellationToken);
            return result is null ? ApiResults.Error(this, 404, "DIRECTOR_ACTION_NOT_FOUND", "Director action not found.") : Ok(result);
        }
        catch (DirectorActionNotApprovedException exception) { return ApiResults.Error(this, 409, "DIRECTOR_APPROVAL_REQUIRED", exception.Message); }
        catch (DirectorProposalStaleException exception) { return ApiResults.Error(this, 409, "DIRECTOR_PROPOSAL_STALE", exception.Message); }
        catch (DirectorActionExecutionException exception) { return ApiResults.Error(this, 409, exception.Code, exception.Message); }
    }

    private bool TryGetUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId) && userId != Guid.Empty;
}

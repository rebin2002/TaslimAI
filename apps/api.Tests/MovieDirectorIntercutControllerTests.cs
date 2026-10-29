using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorIntercutControllerTests
{
    [Fact]
    public async Task Foreign_and_missing_timeline_ids_are_rejected_but_owned_timeline_remains_review_only()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var requestedProjectId = Guid.NewGuid();
        var foreignProjectId = Guid.NewGuid();
        var implicitProjectId = Guid.NewGuid();
        var foreignTimelineId = Guid.NewGuid();
        var ownedTimelineId = Guid.NewGuid();
        var ownedSceneId = Guid.NewGuid();
        var ownedShotId = Guid.NewGuid();
        var ownedClipId = Guid.NewGuid();
        var foreignSceneId = Guid.NewGuid();
        var foreignShotId = Guid.NewGuid();
        var foreignClipId = Guid.NewGuid();
        var implicitSceneId = Guid.NewGuid();
        var implicitShotId = Guid.NewGuid();
        var implicitClipId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "intercut-owner", NormalizedUserName = "INTERCUT-OWNER", DisplayName = "Intercut Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Intercut Workspace", Slug = $"intercut-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.AddRange(
            new MovieProject { Id = requestedProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Requested", Description = "Requested.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = foreignProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Foreign", Description = "Foreign.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = implicitProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Implicit Timeline", Description = "Implicit timeline fixture.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = requestedProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = implicitProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieTimelines.AddRange(
            new MovieTimeline { Id = foreignTimelineId, MovieProjectId = foreignProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now },
            new MovieTimeline { Id = ownedTimelineId, MovieProjectId = requestedProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.AddRange(
            new MovieScene { Id = ownedSceneId, MovieProjectId = requestedProjectId, Sequence = 1, Title = "Owned Scene", CreatedAt = now, UpdatedAt = now },
            new MovieScene { Id = foreignSceneId, MovieProjectId = foreignProjectId, Sequence = 1, Title = "Foreign Scene", CreatedAt = now, UpdatedAt = now },
            new MovieScene { Id = implicitSceneId, MovieProjectId = implicitProjectId, Sequence = 1, Title = "Implicit Scene", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.AddRange(
            new MovieShot { Id = ownedShotId, MovieSceneId = ownedSceneId, Sequence = 1, Description = "Owned shot", CreatedAt = now, UpdatedAt = now },
            new MovieShot { Id = foreignShotId, MovieSceneId = foreignSceneId, Sequence = 1, Description = "Foreign shot", CreatedAt = now, UpdatedAt = now },
            new MovieShot { Id = implicitShotId, MovieSceneId = implicitSceneId, Sequence = 1, Description = "Implicit shot", CreatedAt = now, UpdatedAt = now });
        db.MovieClips.AddRange(
            new MovieClip { Id = ownedClipId, MovieProjectId = requestedProjectId, MovieSceneId = ownedSceneId, MovieShotId = ownedShotId, DurationSeconds = 2, CreatedAt = now, UpdatedAt = now },
            new MovieClip { Id = foreignClipId, MovieProjectId = foreignProjectId, MovieSceneId = foreignSceneId, MovieShotId = foreignShotId, DurationSeconds = 2, CreatedAt = now, UpdatedAt = now },
            new MovieClip { Id = implicitClipId, MovieProjectId = implicitProjectId, MovieSceneId = implicitSceneId, MovieShotId = implicitShotId, DurationSeconds = 2, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var authorization = new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
        var controller = new MovieDirectorController(null!, null!, authorization, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };

        var request = new MovieIntercutPlanRequest
        {
            TimelineId = foreignTimelineId,
            Clips =
            [
                new MovieIntercutCoverageRequest
                {
                    ClipId = ownedClipId, MovieShotId = ownedShotId, MovieSceneId = ownedSceneId,
                    SceneSequence = 1, ShotSequence = 1, DurationSeconds = 2,
                },
            ],
        };

        foreach (var invalidId in new string?[] { null, "", "invalid-guid", Guid.Empty.ToString() })
        {
            var claims = new List<Claim>();
            if (invalidId is not null)
                claims.Add(new Claim(ClaimTypes.NameIdentifier, invalidId));
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            var invalidResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
            Assert.IsType<UnauthorizedResult>(invalidResult);
        }
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

        var foreignResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var foreignError = Assert.IsType<ObjectResult>(foreignResult);
        Assert.Equal(400, foreignError.StatusCode);

        request.TimelineId = Guid.NewGuid();
        var missingResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var missingError = Assert.IsType<ObjectResult>(missingResult);
        Assert.Equal(400, missingError.StatusCode);

        request.TimelineId = ownedTimelineId;
        request.BaseTimelineVersion = 2;
        var staleResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var staleError = Assert.IsType<ObjectResult>(staleResult);
        Assert.Equal(409, staleError.StatusCode);

        request.BaseTimelineVersion = 1;
        var ownedResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var success = Assert.IsType<OkObjectResult>(ownedResult);
        var proposal = Assert.IsType<MovieIntercutPlanDto>(success.Value);
        Assert.Equal(requestedProjectId, proposal.MovieProjectId);
        Assert.Equal(ownedTimelineId, proposal.TimelineId);
        Assert.Equal(2, proposal.ProposedTimelineVersion);
        Assert.Contains("canonical_clip_shot_scene_ownership_verified", proposal.Findings);
        Assert.DoesNotContain("canonical_clip_and_shot_ownership_not_verified_in_review_only_proposal", proposal.Findings);

        // Invalid references must not be able to smuggle another project's clip or
        // misrepresent the canonical scene/shot sequence, even for a review-only cut.
        async Task AssertInvalidCoverageAsync()
        {
            var rejected = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
            Assert.Equal(400, Assert.IsType<ObjectResult>(rejected).StatusCode);
        }

        var candidate = Assert.Single(request.Clips);
        candidate.ClipId = foreignClipId;
        await AssertInvalidCoverageAsync();
        candidate.ClipId = Guid.NewGuid();
        await AssertInvalidCoverageAsync();
        candidate.ClipId = ownedClipId;
        candidate.MovieShotId = foreignShotId;
        await AssertInvalidCoverageAsync();
        candidate.MovieShotId = ownedShotId;
        candidate.MovieSceneId = foreignSceneId;
        await AssertInvalidCoverageAsync();
        candidate.MovieSceneId = ownedSceneId;
        candidate.SceneSequence = 2;
        await AssertInvalidCoverageAsync();
        candidate.SceneSequence = 1;
        candidate.ShotSequence = 2;
        await AssertInvalidCoverageAsync();
        candidate.ShotSequence = 1;

        // The review proposal must not invent additional footage beyond the
        // canonical source clip. A shorter editorial trim is still allowed.
        candidate.DurationSeconds = 3;
        await AssertInvalidCoverageAsync();
        candidate.DurationSeconds = 1;
        var trimmedResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var trimmed = Assert.IsType<MovieIntercutPlanDto>(Assert.IsType<OkObjectResult>(trimmedResult).Value);
        Assert.Equal(1m, Assert.Single(trimmed.ProposedTimeline.Clips).DurationSeconds);
        candidate.DurationSeconds = 2;

        request.TimelineId = null;
        candidate.ClipId = foreignClipId;
        await AssertInvalidCoverageAsync();

        // Omitted and empty timeline ids resolve to the same stable, project-owned
        // timeline. Once persisted, these implicit requests must not bypass
        // optimistic revision checks, even though no timeline id was supplied.
        // A MovieProject has at most one persisted MovieTimeline. Use a separate
        // project for the missing/default-timeline scenario instead of inserting
        // a second timeline for the already-owned project (an invalid fixture).
        candidate.ClipId = implicitClipId;
        candidate.MovieSceneId = implicitSceneId;
        candidate.MovieShotId = implicitShotId;
        var defaultPlan = MovieIntercutPlanner.Build(implicitProjectId, request);
        var noPersistedDefault = await controller.CreateIntercutProposal(implicitProjectId, request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(noPersistedDefault);
        db.MovieTimelines.Add(new MovieTimeline
        {
            Id = defaultPlan.TimelineId, MovieProjectId = implicitProjectId,
            CurrentRevisionNumber = 3, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // Capture the actually persisted timeline IDs before further review-only
        // requests. The endpoint must not insert, delete, or replace a timeline;
        // this is more precise than assuming a global row count across project scopes.
        var persistedTimelineIdsBeforeReview = await db.MovieTimelines.AsNoTracking()
            .Select(item => item.Id)
            .OrderBy(id => id)
            .ToArrayAsync();
        Assert.Contains(defaultPlan.TimelineId, persistedTimelineIdsBeforeReview);

        var implicitStale = await controller.CreateIntercutProposal(implicitProjectId, request, CancellationToken.None);
        Assert.Equal(409, Assert.IsType<ObjectResult>(implicitStale).StatusCode);
        request.TimelineId = Guid.Empty;
        var explicitEmptyStale = await controller.CreateIntercutProposal(implicitProjectId, request, CancellationToken.None);
        Assert.Equal(409, Assert.IsType<ObjectResult>(explicitEmptyStale).StatusCode);
        request.BaseTimelineVersion = 3;
        var currentDefault = await controller.CreateIntercutProposal(implicitProjectId, request, CancellationToken.None);
        var currentDefaultPlan = Assert.IsType<MovieIntercutPlanDto>(Assert.IsType<OkObjectResult>(currentDefault).Value);
        Assert.Equal(defaultPlan.TimelineId, currentDefaultPlan.TimelineId);
        Assert.Equal(4, currentDefaultPlan.ProposedTimelineVersion);

        // This endpoint builds a review-only proposal. It must not persist a new timeline or revision.
        var persistedTimelineIdsAfterReview = await db.MovieTimelines.AsNoTracking()
            .Select(item => item.Id)
            .OrderBy(id => id)
            .ToArrayAsync();
        Assert.Equal(persistedTimelineIdsBeforeReview, persistedTimelineIdsAfterReview);
        Assert.Equal(3, (await db.MovieTimelines.SingleAsync(item => item.Id == defaultPlan.TimelineId)).CurrentRevisionNumber);
        Assert.Equal(3, await db.MovieTimelines.CountAsync());
        Assert.Equal(3, await db.MovieScenes.CountAsync());
        Assert.Equal(3, await db.MovieShots.CountAsync());
        Assert.Equal(3, await db.MovieClips.CountAsync());
        var persisted = await db.MovieTimelines.SingleAsync(item => item.Id == ownedTimelineId);
        Assert.Equal(1, persisted.CurrentRevisionNumber);
    }

    [Fact]
    public async Task Malformed_identity_fails_closed_on_all_director_service_endpoints()
    {
        // The controller must reject broken authenticated principals before
        // it calls any Director service (which are intentionally absent here).
        var controller = new MovieDirectorController(null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        var projectId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        foreach (var invalidId in new string?[] { null, "", "not-a-guid", Guid.Empty.ToString() })
        {
            var claims = new List<Claim>();
            if (invalidId is not null)
                claims.Add(new Claim(ClaimTypes.NameIdentifier, invalidId));
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

            Assert.IsType<UnauthorizedResult>(controller.CreateSalvagePlan(null!));
            Assert.IsType<UnauthorizedResult>(await controller.CreateProposal(projectId, null!, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.CreateIntercutProposal(projectId, null!, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.GetProposal(proposalId, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.History(projectId, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.Approve(proposalId, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.Reject(proposalId, CancellationToken.None));
            Assert.IsType<UnauthorizedResult>(await controller.Execute(actionId, CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(nameof(MovieDirectorController.CreateSalvagePlan))]
    [InlineData(nameof(MovieDirectorController.CreateIntercutProposal))]
    public void Review_planning_endpoints_enforce_generation_rate_limit(string methodName)
    {
        // Both routes can parse substantial caller-supplied story coverage and the
        // intercut route also reads canonical clips. They must not bypass the
        // established generation policy used by other Director proposal routes.
        var method = typeof(MovieDirectorController).GetMethod(methodName)!;
        var limiter = Assert.Single(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), true)
            .Cast<EnableRateLimitingAttribute>());
        Assert.Equal(RateLimiting.Generation, limiter.PolicyName);
    }
}

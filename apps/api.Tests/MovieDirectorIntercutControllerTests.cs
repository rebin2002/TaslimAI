using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
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
        var foreignTimelineId = Guid.NewGuid();
        var ownedTimelineId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "intercut-owner", NormalizedUserName = "INTERCUT-OWNER", DisplayName = "Intercut Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Intercut Workspace", Slug = $"intercut-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.AddRange(
            new MovieProject { Id = requestedProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Requested", Description = "Requested.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = foreignProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Foreign", Description = "Foreign.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = requestedProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieTimelines.AddRange(
            new MovieTimeline { Id = foreignTimelineId, MovieProjectId = foreignProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now },
            new MovieTimeline { Id = ownedTimelineId, MovieProjectId = requestedProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now });
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
                    ClipId = Guid.NewGuid(), MovieShotId = Guid.NewGuid(), MovieSceneId = Guid.NewGuid(),
                    SceneSequence = 1, ShotSequence = 1, DurationSeconds = 2,
                },
            ],
        };

        var foreignResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var foreignError = Assert.IsType<ObjectResult>(foreignResult);
        Assert.Equal(400, foreignError.StatusCode);

        request.TimelineId = Guid.NewGuid();
        var missingResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var missingError = Assert.IsType<ObjectResult>(missingResult);
        Assert.Equal(400, missingError.StatusCode);

        request.TimelineId = ownedTimelineId;
        var ownedResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var success = Assert.IsType<OkObjectResult>(ownedResult);
        var proposal = Assert.IsType<MovieIntercutPlanDto>(success.Value);
        Assert.Equal(requestedProjectId, proposal.MovieProjectId);
        Assert.Equal(ownedTimelineId, proposal.TimelineId);
        Assert.Equal(2, proposal.ProposedTimelineVersion);

        // This endpoint builds a review-only proposal. It must not persist a new timeline or revision.
        Assert.Equal(2, await db.MovieTimelines.CountAsync());
        var persisted = await db.MovieTimelines.SingleAsync(item => item.Id == ownedTimelineId);
        Assert.Equal(1, persisted.CurrentRevisionNumber);
    }
}

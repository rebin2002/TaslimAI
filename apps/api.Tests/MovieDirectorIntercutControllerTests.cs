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
    public async Task Cross_project_timeline_id_is_rejected_before_intercut_proposal_is_built()
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
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "intercut-owner", NormalizedUserName = "INTERCUT-OWNER", DisplayName = "Intercut Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Intercut Workspace", Slug = $"intercut-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.AddRange(
            new MovieProject { Id = requestedProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Requested", Description = "Requested.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = foreignProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Foreign", Description = "Foreign.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = requestedProjectId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieTimelines.Add(new MovieTimeline { Id = foreignTimelineId, MovieProjectId = foreignProjectId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now });
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

        var result = await controller.CreateIntercutProposal(
            requestedProjectId,
            new MovieIntercutPlanRequest
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
            },
            CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, error.StatusCode);
    }
}

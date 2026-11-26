using System.Security.Claims;
using System.Text.Json;
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
    public async Task Foreign_and_missing_timeline_ids_are_rejected_without_disclosure_while_owned_timeline_plans()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var foreignOwnerId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        var requestedProjectId = Guid.NewGuid();
        var foreignProjectId = Guid.NewGuid();
        var foreignTimelineId = Guid.NewGuid();
        var ownedTimelineId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "intercut-owner", NormalizedUserName = "INTERCUT-OWNER", DisplayName = "Intercut Owner", CreatedAt = now, UpdatedAt = now },
            new ApplicationUser { Id = foreignOwnerId, UserName = "foreign-intercut-owner", NormalizedUserName = "FOREIGN-INTERCUT-OWNER", DisplayName = "Foreign Intercut Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.AddRange(
            new Workspace { Id = workspaceId, Name = "Intercut Workspace", Slug = $"intercut-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = foreignWorkspaceId, Name = "Unrelated Test Workspace", Slug = $"foreign-intercut-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.AddRange(
            new MovieProject { Id = requestedProjectId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Requested", Description = "Requested.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = foreignProjectId, WorkspaceId = foreignWorkspaceId, CreatedByUserId = foreignOwnerId, Title = "Foreign Project Detail", Description = "Synthetic foreign fixture.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
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
            BaseTimelineVersion = 1,
            Clips =
            [
                new MovieIntercutCoverageRequest
                {
                    ClipId = clipId,
                    MovieShotId = shotId,
                    MovieSceneId = sceneId,
                    SceneSequence = 1,
                    ShotSequence = 1,
                    DurationSeconds = 2,
                },
            ],
        };

        // This is an authenticated synthetic user authorized for the requested project.
        // A foreign timeline in an unrelated workspace must not disclose its owner.
        var foreignResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var foreignError = Assert.IsType<ObjectResult>(foreignResult);
        Assert.Equal(400, foreignError.StatusCode);
        var foreignEnvelope = Assert.IsType<ErrorEnvelope>(foreignError.Value);
        Assert.Equal(MovieIntercutPlanCodes.TimelineProjectMismatch, foreignEnvelope.Error.Code);
        var foreignPayload = JsonSerializer.Serialize(foreignEnvelope);
        Assert.False(foreignPayload.Contains(foreignProjectId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.False(foreignPayload.Contains(foreignTimelineId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.False(foreignPayload.Contains("Foreign Project Detail", StringComparison.OrdinalIgnoreCase));

        // An unknown ID receives the identical generic response; the caller cannot
        // distinguish a foreign timeline from a nonexistent one through the envelope.
        request.TimelineId = Guid.NewGuid();
        var missingResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var missingError = Assert.IsType<ObjectResult>(missingResult);
        Assert.Equal(400, missingError.StatusCode);
        var missingEnvelope = Assert.IsType<ErrorEnvelope>(missingError.Value);
        Assert.Equal(foreignEnvelope.Error, missingEnvelope.Error);

        // A normal proposal against the same authorized project remains available.
        request.TimelineId = ownedTimelineId;
        var ownedResult = await controller.CreateIntercutProposal(requestedProjectId, request, CancellationToken.None);
        var ownedProposal = Assert.IsType<MovieIntercutPlanDto>(Assert.IsType<OkObjectResult>(ownedResult).Value);
        Assert.Equal(requestedProjectId, ownedProposal.MovieProjectId);
        Assert.Equal(ownedTimelineId, ownedProposal.TimelineId);
    }
}

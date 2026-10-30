using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieAuthorizationDenyOverrideTests
{
    [Fact]
    public async Task Explicit_edit_deny_override_blocks_writer_capabilities_and_commands()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "writer-with-deny-override",
            NormalizedUserName = "WRITER-WITH-DENY-OVERRIDE",
            DisplayName = "Writer",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Movie Authorization Workspace",
            Slug = $"movie-auth-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Owner,
        });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Deny Override Test",
            Description = "Provider-free authorization regression.",
            DurationSeconds = 30,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = memberId,
            MovieProjectId = movieId,
            UserId = userId,
            Role = MovieTeamRoles.Writer,
            IsProjectOwner = false,
            PermissionOverrides =
            [
                new MovieTeamMemberPermission
                {
                    Id = Guid.NewGuid(),
                    MovieTeamMemberId = memberId,
                    Permission = MoviePermissions.Edit,
                    Granted = false,
                },
            ],
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        Assert.Contains(MoviePermissions.Edit, MoviePermissions.ForRole(MovieTeamRoles.Writer));

        var authorization = new MovieAuthorizationService(
            new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
        var capabilities = await authorization.GetCapabilitiesAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(capabilities);
        Assert.DoesNotContain(MoviePermissions.Edit, capabilities!.Permissions);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.StoryEdit]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.ShotEdit]);
        Assert.True(capabilities.Capabilities[MovieOperationalActions.Comments]);
        Assert.False(await authorization.CanAsync(userId, movieId, MovieOperationalActions.StoryEdit, CancellationToken.None));
        await Assert.ThrowsAsync<MovieCollaborationForbiddenException>(
            () => authorization.RequireAsync(userId, movieId, MovieOperationalActions.StoryEdit, CancellationToken.None));
    }
}

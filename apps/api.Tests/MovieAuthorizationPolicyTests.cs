using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieAuthorizationPolicyTests
{
    [Fact]
    public void Operational_actions_map_only_to_established_movie_permissions()
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MovieOperationalActions.StoryEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.StoryApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.GuideEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.GuideApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.CastEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.WorldEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.SceneEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.ShotEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.ProductionVersionEdit] = MoviePermissions.Edit,
            [MovieOperationalActions.Generate] = MoviePermissions.Generate,
            [MovieOperationalActions.RenderTake] = MoviePermissions.Generate,
            [MovieOperationalActions.StoryboardApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.KeyframeApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.ProductionReview] = MoviePermissions.Approve,
            [MovieOperationalActions.TakeCreate] = MoviePermissions.Edit,
            [MovieOperationalActions.TakeSelect] = MoviePermissions.Edit,
            [MovieOperationalActions.TakeApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.TakeFinalization] = MoviePermissions.FinalApproval,
            [MovieOperationalActions.DirectorProposalCreate] = MoviePermissions.Generate,
            [MovieOperationalActions.DirectorProposalApproval] = MoviePermissions.Approve,
            [MovieOperationalActions.DirectorProposalExecution] = MoviePermissions.Generate,
            [MovieOperationalActions.Comments] = MoviePermissions.Comment,
            [MovieOperationalActions.ReviewsRequest] = MoviePermissions.Comment,
            [MovieOperationalActions.ReviewsDecision] = MoviePermissions.Approve,
            [MovieOperationalActions.FinalReviewDecision] = MoviePermissions.FinalApproval,
            [MovieOperationalActions.TeamManagement] = MoviePermissions.ManageTeam,
            [MovieOperationalActions.BudgetManagement] = MoviePermissions.ManageBudget,
        };

        Assert.Equal(expected.Count, MovieOperationalPolicies.All().Count);
        foreach (var (action, permission) in expected)
            Assert.Equal(permission, MovieOperationalPolicies.RequiredPermission(action));
    }

    [Fact]
    public async Task Reviewer_capabilities_do_not_leak_generation_budget_or_team_authority()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "reviewer", NormalizedUserName = "REVIEWER", DisplayName = "Reviewer", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Reviewer Workspace", Slug = $"reviewer-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Review Movie", Description = "A movie.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Writer,
            PermissionOverrides = [new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Approve, Granted = true }],
            CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var authorization = new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
        var capabilities = await authorization.GetCapabilitiesAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(capabilities);
        Assert.True(capabilities!.Capabilities[MovieOperationalActions.StoryApproval]);
        Assert.True(capabilities.Capabilities[MovieOperationalActions.Comments]);
        Assert.True(capabilities.Capabilities[MovieOperationalActions.ReviewsDecision]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.Generate]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.BudgetManagement]);
        Assert.False(capabilities.Capabilities[MovieOperationalActions.TeamManagement]);
    }

    [Fact]
    public async Task Capability_snapshot_is_project_scoped_even_when_the_user_shares_the_workspace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var ownMovieId = Guid.NewGuid();
        var otherMovieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "scoped", NormalizedUserName = "SCOPED", DisplayName = "Scoped", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Shared Workspace", Slug = $"shared-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.AddRange(
            new MovieProject { Id = ownMovieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Own", Description = "Own.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now },
            new MovieProject { Id = otherMovieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Other", Description = "Other.", DurationSeconds = 30, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now }, CreatedAt = now, UpdatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = ownMovieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var authorization = new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        Assert.NotNull(await authorization.GetCapabilitiesAsync(userId, ownMovieId, CancellationToken.None));
        Assert.Null(await authorization.GetCapabilitiesAsync(userId, otherMovieId, CancellationToken.None));
        Assert.False(await authorization.CanAsync(userId, otherMovieId, MovieOperationalActions.Generate, CancellationToken.None));
    }
}

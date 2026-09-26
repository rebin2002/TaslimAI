using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieScenesTests
{
    [Fact]
    public async Task Approved_screenplay_breakdown_creates_canonical_hierarchy_once_without_destroying_existing_work()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, revisionId, screenplaySceneId) = await SeedApprovedScreenplayAsync(db);
        var service = CreateService(db);

        var first = await service.BreakDownApprovedScreenplayAsync(userId, movieId, CancellationToken.None);
        var createdSceneId = first!.Workspace.Acts.Single().Sequences.Single().Scenes.Single().Id;
        db.MovieShots.Add(new MovieShot { Id = Guid.NewGuid(), MovieSceneId = createdSceneId, Sequence = 1, Description = "Existing shot work", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var second = await service.BreakDownApprovedScreenplayAsync(userId, movieId, CancellationToken.None);
        var linked = await db.MovieScreenplayScenes.SingleAsync(item => item.Id == screenplaySceneId);

        Assert.Equal(revisionId, first.ApprovedRevisionId);
        Assert.Equal(1, first.CreatedSceneCount);
        Assert.Equal(0, second!.CreatedSceneCount);
        Assert.Equal(1, second.ExistingLinkedSceneCount);
        Assert.Equal(createdSceneId, linked.MovieSceneId);
        Assert.Equal(1, await db.MovieShots.CountAsync(item => item.MovieSceneId == createdSceneId));
    }

    [Fact]
    public async Task Scenes_workspace_hides_cross_workspace_projects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, _, _) = await SeedApprovedScreenplayAsync(db);
        var outsiderId = Guid.NewGuid();

        var workspace = await CreateService(db).GetWorkspaceAsync(outsiderId, movieId, CancellationToken.None);

        Assert.Null(workspace);
        Assert.Null(await CreateService(db).BreakDownApprovedScreenplayAsync(outsiderId, movieId, CancellationToken.None));
    }

    private static MovieScenesService CreateService(TaslimDbContext db) => new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid UserId, Guid MovieId, Guid RevisionId, Guid ScreenplaySceneId)> SeedApprovedScreenplayAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var storyId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var screenplaySceneId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "scenes-owner", NormalizedUserName = "SCENES-OWNER", DisplayName = "Scenes Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Scenes Workspace", Slug = $"scenes-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Scenes Film", Description = "A film.", DurationSeconds = 60, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieStories.Add(new MovieStory { Id = storyId, MovieProjectId = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, ApprovalState = MovieStoryApprovalStates.Approved, ApprovedRevisionId = revisionId, CurrentRevisionId = revisionId, CreatedAt = now, UpdatedAt = now, Revisions = [new MovieStoryRevision { Id = revisionId, MovieStoryId = storyId, CreatedByUserId = userId, RevisionNumber = 2, Premise = "Premise", Logline = "Logline", Synopsis = "Synopsis", Treatment = "Treatment", Status = MovieStoryRevisionStatuses.Approved, Authorship = MovieStoryAuthorship.Human, CreatedAt = now, UpdatedAt = now, Scenes = [new MovieScreenplayScene { Id = screenplaySceneId, Ordinal = 1, SceneIdentifier = "1", ActNumber = 1, SequenceNumber = 1, Slugline = "INT. WORKSHOP - DAY", Synopsis = "A decision is made.", CreatedAt = now, Elements = [new MovieScreenplayElement { Id = Guid.NewGuid(), Ordinal = 1, ElementType = MovieScreenplayElementTypes.Dialogue, Content = "We begin.", CharacterName = "Mara", CreatedAt = now }] }] }] });
        await db.SaveChangesAsync();
        return (userId, movieId, revisionId, screenplaySceneId);
    }
}

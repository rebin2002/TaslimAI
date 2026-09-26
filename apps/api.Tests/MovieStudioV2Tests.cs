using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieStudioV2Tests
{
    [Fact]
    public async Task Canonical_hierarchy_orders_children_and_persists_quality_and_auto_director_separately()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var act = await service.AddActAsync(userId, movieId, new MovieV2ActRequest { Title = "Act One" }, CancellationToken.None);
        var sequence = await service.AddSequenceAsync(userId, act!.Id, new MovieV2SequenceRequest { Title = "Opening" }, CancellationToken.None);
        var scene = await service.AddSceneAsync(userId, sequence!.Id, new MovieV2SceneRequest { Title = "Dawn", Summary = "The story begins." }, CancellationToken.None);
        var canonicalShot = new MovieShot { Id = Guid.NewGuid(), MovieSceneId = scene!.Id, Sequence = 1, Description = "Canonical wide shot.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MovieShots.Add(canonicalShot);
        await db.SaveChangesAsync();
        var take = await service.AddTakeAsync(userId, canonicalShot.Id, new MovieV2TakeRequest { QualityLevel = MovieQualityLevels.Cinematic, AutoDirectorEnabled = true }, CancellationToken.None);
        var secondAct = await service.AddActAsync(userId, movieId, new MovieV2ActRequest { Title = "Act Two" }, CancellationToken.None);
        await service.ReorderAsync(userId, "act", secondAct!.Id, 1, CancellationToken.None);

        var hierarchy = await service.GetHierarchyAsync(userId, movieId, CancellationToken.None);

        Assert.Equal(MovieQualityLevels.Standard, hierarchy!.QualityLevel);
        Assert.False(hierarchy.AutoDirectorEnabled);
        Assert.Equal(secondAct.Id, hierarchy.Acts[0].Id);
        Assert.Equal(act.Id, hierarchy.Acts[1].Id);
        Assert.Equal(sequence.Id, hierarchy.Acts[1].Sequences.Single().Id);
        Assert.Equal(scene.Id, hierarchy.Acts[1].Sequences.Single().Scenes.Single().Id);
        Assert.Equal(take!.Id, hierarchy.Acts[1].Sequences.Single().Scenes.Single().Shots.Single().Takes.Single().Id);
        Assert.Equal(MovieQualityLevels.Cinematic, take.QualityLevel);
        Assert.True(take.AutoDirectorEnabled);
    }

    [Fact]
    public async Task Approval_then_finalize_persists_selected_and_final_take_audit_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest(), CancellationToken.None);

        var approved = await service.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved, Comment = "Looks good." }, CancellationToken.None);
        await service.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        await service.SelectTakeAsync(userId, take.Id, true, CancellationToken.None);
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);

        Assert.Equal(MovieTakeStatuses.Approved, approved!.Status);
        Assert.Equal(take.Id, shot.SelectedTakeId);
        Assert.Equal(take.Id, shot.FinalTakeId);
        Assert.NotNull(await db.MovieTakeApprovals.SingleOrDefaultAsync(item => item.MovieTakeId == take.Id && item.Decision == MovieApprovalDecisions.Approved));
        Assert.NotNull(await db.MovieTakes.Where(item => item.Id == take.Id).Select(item => item.FinalizedAt).SingleAsync());
    }

    [Fact]
    public async Task Non_member_cannot_read_or_mutate_movie_hierarchy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (_, movieId, shotId) = await SeedAsync(db);
        var outsiderId = Guid.NewGuid();
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        Assert.Null(await service.GetHierarchyAsync(outsiderId, movieId, CancellationToken.None));
        Assert.Null(await service.AddTakeAsync(outsiderId, shotId, new MovieV2TakeRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Overview_returns_bounded_progress_actions_and_warnings_without_crossing_workspace_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, _) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var overview = await service.GetOverviewAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(overview);
        Assert.Equal(1, overview!.Scenes.Total);
        Assert.Equal(1, overview.Shots.Total);
        Assert.Equal(0, overview.Progress.Production.Completed);
        Assert.Equal("Complete Story", overview.NextActions[0].Label);
        Assert.Contains(overview.Warnings, item => item.Key == "story-not-approved");
        Assert.Contains(overview.RecentActivity, item => item.Module == "scenes");
        Assert.Null(await service.GetOverviewAsync(Guid.NewGuid(), movieId, CancellationToken.None));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid UserId, Guid MovieId, Guid ShotId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "movie-owner", NormalizedUserName = "MOVIE-OWNER", DisplayName = "Movie Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Movie Workspace", Slug = $"movie-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Foundation", Description = "A movie.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Legacy scene", Summary = "Existing scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Wide shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, movieId, shotId);
    }
}

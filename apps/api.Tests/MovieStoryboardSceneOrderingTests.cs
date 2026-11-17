using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Movie storyboard scene ordering and unique-ordinal enforcement.
///
/// Exercise surface: <c>MovieV2Service.ReorderAsync(userId, "scene", ...)</c> — the service
/// behind <c>PATCH /api/movie-studio/scenes/{id}/order</c>, the endpoint the storyboard scene
/// strip drives ("Move up"/"Move down" in FullMovieWorkspaceView ->
/// <c>api.reorderMovieEntity("scenes", scene.id, scene.sequence + delta)</c>).
///
/// Defect reproduced here: the sibling set that receives the renumbered ordinals was selected
/// with <c>Where(item => item.MovieSequenceId == scene.MovieSequenceId)</c> only. A production
/// scene's parent sequence is nullable by design (<c>OnDelete.SetNull</c> on the FK, plus legacy
/// scenes that predate the act/sequence hierarchy — <c>MovieScenesService</c> itself leaves
/// scenes detached when their sequence row is removed), so reordering a scene detached from its
/// sequence matched EVERY detached scene of EVERY project and workspace in the database: their
/// unique ordinals were reassigned, their <c>UpdatedAt</c> audit stamps rewritten, and the
/// requesting project's own strip lost its contiguous 1..n layout — after which the next scene
/// admission in the untouched project (<c>Count + 1</c>) collides with a surviving ordinal on
/// the <c>(MovieSequenceId, Sequence)</c> unique index. The shot/take sibling queries in the
/// same method are naturally scoped by their non-nullable parents; the scene case must be
/// scoped to its project explicitly, preserving the same ordinal-scope invariant.
///
/// Non-duplication evidence (checked 2026-11-17 against all 200 open PR changed-file lists):
/// no open PR touches <c>apps/api/Movies/MovieV2Service.cs</c>, this file's path, or the
/// storyboard scene-order routes. <c>MovieSceneReorderLongFormTimelineTests.cs</c> (open,
/// issue #685 slice) reorders scenes attached to real sequences inside one project and asserts
/// timeline totals in the same context; it cannot see the cross-project ordinal leak and is
/// untouched here. <c>MovieStudioV2Tests.cs</c> reorders an ACT only; untouched.
/// <c>fix/movie-audio-window-ordering-boundary</c> (#760) is timeline audio ordering, a
/// different subsystem; untouched. This lane deliberately stays out of the video-clip
/// permission and dialogue-subtitle lanes.
///
/// Synthetic and provider-free: in-memory SQLite, two synthetic projects in separate personal
/// workspaces. No `IMovieVideoProvider`, no cost reservation, no generation job, no billing,
/// no network, no user data.
/// </summary>
public sealed class MovieStoryboardSceneOrderingTests
{
    private record SceneRow(Guid Id, int Sequence, DateTime UpdatedAt);

    [Fact]
    public async Task Reordering_a_scene_detached_from_its_sequence_only_renumbers_that_projects_strip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var projectA = await SeedProjectAsync(db, now, "Alpha");
        var projectB = await SeedProjectAsync(db, now, "Beta");

        // Two detached legacy scenes in the caller's project (no parent sequence at all) and
        // two in a completely different user's project. All four share MovieSequenceId == null.
        var movedScene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = projectA.MovieId, Sequence = 2, Title = "Aurora Departure", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var siblingScene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = projectA.MovieId, Sequence = 1, Title = "Aurora Arrival", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var foreignFirst = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = projectB.MovieId, Sequence = 1, Title = "Bazaar Contact", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        var foreignSecond = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = projectB.MovieId, Sequence = 2, Title = "Bazaar Pursuit", Summary = "Synthetic.", CreatedAt = now, UpdatedAt = now };
        db.MovieScenes.AddRange(movedScene, siblingScene, foreignFirst, foreignSecond);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.ReorderAsync(projectA.UserId, "scene", movedScene.Id, 1, CancellationToken.None);

        // The caller's strip really moved to the requested order with contiguous positive ordinals.
        await using (var verify = CreateDb(connection))
        {
            var ownRows = await ReadScenesAsync(verify, projectA.MovieId);
            Assert.Equal([movedScene.Id, siblingScene.Id], ownRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], ownRows.Select(row => row.Sequence).ToArray());
        }

        // The untouched project keeps its exact ordinals AND its audit stamps: no foreign
        // storyboard can be renumbered through another workspace's reorder request.
        await using (var verify = CreateDb(connection))
        {
            var foreignRows = await ReadScenesAsync(verify, projectB.MovieId);
            Assert.Equal([foreignFirst.Id, foreignSecond.Id], foreignRows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2], foreignRows.Select(row => row.Sequence).ToArray());
            Assert.All(foreignRows, row => Assert.Equal(row.UpdatedAt, now));
        }
    }

    [Fact]
    public async Task Scene_reorder_inside_a_sequence_persists_contiguous_unique_ordinals_in_the_committed_rows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var project = await SeedProjectAsync(db, now, "Gamma");
        var service = CreateService(db);
        var act = await service.AddActAsync(project.UserId, project.MovieId, new MovieV2ActRequest { Title = "Act One" }, CancellationToken.None);
        var sequence = await service.AddSequenceAsync(project.UserId, act!.Id, new MovieV2SequenceRequest { Title = "Opening Strip" }, CancellationToken.None);
        var sceneIds = new List<Guid>();
        foreach (var title in new[] { "Market Dawn", "Chase Begins", "Rooftop Standoff", "Escape at Dusk" })
        {
            var scene = await service.AddSceneAsync(project.UserId, sequence!.Id, new MovieV2SceneRequest { Title = title, Summary = "Synthetic scene." }, CancellationToken.None);
            sceneIds.Add(scene!.Id);
        }

        // Baseline: the authored strip really committed as 1..4.
        await using (var verify = CreateDb(connection))
        {
            var seeded = await ReadScenesAsync(verify, project.MovieId);
            Assert.Equal([1, 2, 3, 4], seeded.Select(row => row.Sequence).ToArray());
        }

        // Move the last scene of the strip to the front.
        await service.ReorderAsync(project.UserId, "scene", sceneIds[3], 1, CancellationToken.None);

        // Read the committed state through a fresh context so no tracked in-memory values can
        // mask what the transaction actually wrote.
        await using (var after = CreateDb(connection))
        {
            var rows = await ReadScenesAsync(after, project.MovieId);
            Assert.Equal([sceneIds[3], sceneIds[0], sceneIds[1], sceneIds[2]], rows.Select(row => row.Id).ToArray());
            Assert.Equal([1, 2, 3, 4], rows.Select(row => row.Sequence).ToArray());
            Assert.All(rows, row => Assert.True(row.Sequence >= 1, "Storyboard scene ordinals must stay positive and unique."));
            Assert.Equal(rows.Length, rows.Select(row => row.Sequence).Distinct().Count());
        }

        // The reordered strip still admits the next scene without an ordinal collision.
        var added = await service.AddSceneAsync(project.UserId, sequence.Id, new MovieV2SceneRequest { Title = "Curtain Call", Summary = "Synthetic scene." }, CancellationToken.None);
        Assert.NotNull(added);
        await using var final = CreateDb(connection);
        var finalRows = await ReadScenesAsync(final, project.MovieId);
        Assert.Equal(5, finalRows.Length);
        Assert.Equal([1, 2, 3, 4, 5], finalRows.Select(row => row.Sequence).ToArray());
    }

    private sealed record SeededProject(Guid UserId, Guid WorkspaceId, Guid MovieId);

    private static MovieV2Service CreateService(TaslimDbContext db) => new(db, new WorkspaceAccessService(db), new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SceneRow[]> ReadScenesAsync(TaslimDbContext db, Guid movieProjectId) => await db.MovieScenes.AsNoTracking()
        .Where(item => item.MovieProjectId == movieProjectId)
        .OrderBy(item => item.Sequence)
        .Select(item => new SceneRow(item.Id, item.Sequence, item.UpdatedAt))
        .ToArrayAsync();

    private static async Task<SeededProject> SeedProjectAsync(TaslimDbContext db, DateTime now, string tag)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"storyboard-{tag}-{Guid.NewGuid():N}".ToLowerInvariant(), NormalizedUserName = $"STORYBOARD-{tag}".ToUpperInvariant(), DisplayName = $"Storyboard {tag}", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = $"Storyboard {tag}", Slug = $"storyboard-{tag}-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = $"Project {tag}", Description = "A synthetic feature.", DurationSeconds = 120, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new SeededProject(userId, workspaceId, movieId);
    }
}

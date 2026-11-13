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
/// Issue #685 — scene-reorder slice. Question answered: does reordering scenes inside a
/// 120-minute (7,200-second) project preserve cumulative timeline metadata and shot boundaries?
///
/// Non-duplication evidence (checked 2026-11-13 against all 534 open PR changed-file lists):
/// - PR #269 `MovieProjectDurationBoundaryTests.cs` proves 7,200-second create/read/persist,
///   duration-budget totals and draft duration edits. It never reorders a scene.
/// - PR #781 `MovieLongFormProjectMetadataReadModelTests.cs` pins the 7,200-second value through the
///   shell/detail read models. It never reorders a scene ("reordered" in its body refers to a
///   positional C# record change, not scene ordering).
/// - `MovieStudioV2Tests.cs` reorders an ACT only, asserts ordering only, on a 30-second project.
/// - `MovieShotPlanningApiTests.cs` reorders a SHOT within one scene and asserts counts only.
/// No open PR claims this file path; no core/owned file is edited.
///
/// Exercise surface: `MovieV2Service.ReorderAsync(userId, "scene", ...)` — the service behind
/// `PATCH /api/movie-studio/scenes/{id}/order`, the endpoint the Movie Studio scene list drives
/// (`api.reorderMovieEntity("scenes", ...)`, FullMovieWorkspaceView `reorderScene`).
///
/// Provider-free: in-memory SQLite plus the hierarchy service. No `IMovieVideoProvider`, no cost
/// reservation, no generation job, no asset, no network. The project row is seeded at 7,200 seconds
/// because the create-request validator on main still caps briefs at 3,600; that validator is owned
/// by PR #269/#270 and is deliberately not edited here.
/// </summary>
public sealed class MovieSceneReorderLongFormTimelineTests
{
    private const int TwoHourSeconds = 7_200;

    private static readonly Guid SequenceOne = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SequenceTwo = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActOne = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ActTwo = new("44444444-4444-4444-4444-444444444444");

    // Act/sequence/scene runtime plan: 3,600 s in the reorderable sequence plus 3,600 s elsewhere.
    // Each scene's provider-supported 10-30 second planned shots sum exactly to that scene duration,
    // so all 7,200 seconds are covered by plan rows only, never by a provider submission.
    private static readonly (Guid SequenceId, int SceneSequence, int DurationSeconds, int ShotSeconds)[] FilmPlan =
    [
        (SequenceOne, 1, 900, 10),
        (SequenceOne, 2, 900, 10),
        (SequenceOne, 3, 900, 10),
        (SequenceOne, 4, 900, 30),
        (SequenceTwo, 1, 1_500, 10),
        (SequenceTwo, 2, 1_500, 10),
        (SequenceTwo, 3, 600, 30),
    ];

    [Fact]
    public async Task Scene_reorder_in_a_two_hour_project_preserves_cumulative_metadata_and_shot_boundaries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId) = await SeedTwoHourProjectAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var before = await ReadTimelineAsync(db, movieId);
        Assert.Equal(TwoHourSeconds, before.ProjectDurationSeconds);
        Assert.Equal(TwoHourSeconds, before.CumulativeSceneSeconds);
        Assert.Equal(TwoHourSeconds, before.CumulativeShotSeconds);

        var openingBefore = OrderedSceneIds(before, SequenceOne);
        Assert.Equal(4, openingBefore.Length);
        var movedSceneId = openingBefore[^1];

        // The user moves the last scene of the opening sequence to the front: 1,2,3,4 -> 4,1,2,3.
        await service.ReorderAsync(userId, "scene", movedSceneId, 1, CancellationToken.None);

        var after = await ReadTimelineAsync(db, movieId);

        // 1. Cumulative timeline metadata survives the reorder unchanged.
        Assert.Equal(before.ProjectDurationSeconds, after.ProjectDurationSeconds);
        Assert.Equal(before.CumulativeSceneSeconds, after.CumulativeSceneSeconds);
        Assert.Equal(before.CumulativeShotSeconds, after.CumulativeShotSeconds);
        Assert.Equal(before.SceneCount, after.SceneCount);
        Assert.Equal(before.ShotCount, after.ShotCount);

        // 2. Shot boundaries are intact: every scene keeps its own runtime, its own shot list and
        //    the exact same intra-scene shot offsets, so no shot is resized, dropped, duplicated
        //    or re-parented by reordering.
        Assert.Equal(
            before.Scenes.Select(item => (item.SceneId, item.DurationSeconds, item.ShotIdSignature, item.ShotBoundarySignature)).OrderBy(item => item.SceneId),
            after.Scenes.Select(item => (item.SceneId, item.DurationSeconds, item.ShotIdSignature, item.ShotBoundarySignature)).OrderBy(item => item.SceneId));

        // 3. The sibling sequence is untouched, and the moved scene carries its own shots with it.
        Assert.Equal(
            before.SceneOrder.Where(item => item.SequenceId == SequenceTwo).Select(item => (item.SceneId, item.Sequence)).OrderBy(item => item.Sequence),
            after.SceneOrder.Where(item => item.SequenceId == SequenceTwo).Select(item => (item.SceneId, item.Sequence)).OrderBy(item => item.Sequence));
        Assert.Equal(
            before.Scenes.Single(item => item.SceneId == movedSceneId).ShotIdSignature,
            after.Scenes.Single(item => item.SceneId == movedSceneId).ShotIdSignature);

        // 4. Ordering really did change, and the reordered sequence stays a contiguous 1..N block:
        //    no stale gaps and no leftover negative positions from the two-phase reorder write.
        Assert.Equal([movedSceneId, openingBefore[0], openingBefore[1], openingBefore[2]], OrderedSceneIds(after, SequenceOne));
        Assert.NotEqual(openingBefore, OrderedSceneIds(after, SequenceOne));
        Assert.Equal([1, 2, 3, 4], after.SceneOrder.Where(item => item.SequenceId == SequenceOne).Select(item => item.Sequence).OrderBy(item => item).ToArray());

        // 5. Reordering is metadata-only: no clip, take, job, asset, timeline revision or assembly appears.
        Assert.Equal(0, await db.MovieClips.CountAsync());
        Assert.Equal(0, await db.MovieTakes.CountAsync());
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
        Assert.Equal(0, await db.Assets.CountAsync());
        Assert.Equal(0, await db.MovieTimelines.CountAsync());
        Assert.Equal(0, await db.MovieAssemblies.CountAsync());
    }

    [Fact]
    public async Task Rejected_scene_reorder_leaves_the_two_hour_budget_and_shot_boundaries_untouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId) = await SeedTwoHourProjectAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var before = await ReadTimelineAsync(db, movieId);
        var lastSceneId = OrderedSceneIds(before, SequenceOne)[^1];

        // A zero/negative target is rejected before any hierarchy write, and an over-long target is
        // clamped rather than dropped. Neither may lose runtime or move a shot.
        await Assert.ThrowsAsync<MovieV2ValidationException>(() => service.ReorderAsync(userId, "scene", lastSceneId, 0, CancellationToken.None));
        await service.ReorderAsync(userId, "scene", lastSceneId, TwoHourSeconds, CancellationToken.None);

        var after = await ReadTimelineAsync(db, movieId);
        Assert.Equal(TwoHourSeconds, after.ProjectDurationSeconds);
        Assert.Equal(TwoHourSeconds, after.CumulativeSceneSeconds);
        Assert.Equal(TwoHourSeconds, after.CumulativeShotSeconds);
        Assert.Equal(
            before.Scenes.Select(item => (item.SceneId, item.DurationSeconds, item.ShotIdSignature, item.ShotBoundarySignature)).OrderBy(item => item.SceneId),
            after.Scenes.Select(item => (item.SceneId, item.DurationSeconds, item.ShotIdSignature, item.ShotBoundarySignature)).OrderBy(item => item.SceneId));
        Assert.Equal(OrderedSceneIds(before, SequenceOne), OrderedSceneIds(after, SequenceOne));
        Assert.Equal([1, 2, 3, 4], after.SceneOrder.Where(item => item.SequenceId == SequenceOne).Select(item => item.Sequence).OrderBy(item => item).ToArray());
        Assert.Equal(0, await db.GenerationJobs.CountAsync());
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static Guid[] OrderedSceneIds(TimelineSnapshot snapshot, Guid sequenceId) =>
        snapshot.SceneOrder.Where(item => item.SequenceId == sequenceId).OrderBy(item => item.Sequence).Select(item => item.SceneId).ToArray();

    private static async Task<(Guid UserId, Guid MovieId)> SeedTwoHourProjectAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "longform-owner", NormalizedUserName = "LONGFORM-OWNER", DisplayName = "Longform Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Longform Workspace", Slug = $"longform-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject
        {
            Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Two Hour Feature",
            Description = "120-minute project timeline metadata.", DurationSeconds = TwoHourSeconds,
            Mode = MovieProjectModes.Full, CreatedAt = now, UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
        });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieActs.AddRange(
            new MovieAct { Id = ActOne, MovieProjectId = movieId, Sequence = 1, Title = "Act One", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now },
            new MovieAct { Id = ActTwo, MovieProjectId = movieId, Sequence = 2, Title = "Act Two", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now });
        db.MovieSequences.AddRange(
            new MovieSequence { Id = SequenceOne, MovieActId = ActOne, Sequence = 1, Title = "Opening", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now },
            new MovieSequence { Id = SequenceTwo, MovieActId = ActTwo, Sequence = 1, Title = "Resolution", Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now });

        var scenes = new List<MovieScene>();
        var shots = new List<MovieShot>();
        foreach (var (sequenceId, sceneSequence, duration, shotSeconds) in FilmPlan)
        {
            var sceneId = Guid.NewGuid();
            scenes.Add(new MovieScene
            {
                Id = sceneId, MovieProjectId = movieId, MovieSequenceId = sequenceId, Sequence = sceneSequence,
                Title = $"Scene {sceneSequence}", Summary = "Long-form planned scene.",
                DurationSeconds = duration, Status = MovieHierarchyStatuses.Planned, CreatedAt = now, UpdatedAt = now,
            });
            for (var shotSequence = 1; shotSequence * shotSeconds <= duration; shotSequence++)
                shots.Add(new MovieShot
                {
                    Id = Guid.NewGuid(), MovieSceneId = sceneId, Sequence = shotSequence,
                    Description = $"Planned {shotSeconds}-second shot {shotSequence}.",
                    DurationSeconds = shotSeconds, Status = MovieShotStatuses.Planned, CreatedAt = now, UpdatedAt = now,
                });
        }

        db.MovieScenes.AddRange(scenes);
        db.MovieShots.AddRange(shots);
        await db.SaveChangesAsync();
        return (userId, movieId);
    }

    /// <summary>
    /// Cumulative timeline metadata derived from the canonical hierarchical order
    /// (act sequence, sequence, scene sequence, shot sequence).
    /// </summary>
    private static async Task<TimelineSnapshot> ReadTimelineAsync(TaslimDbContext db, Guid movieId)
    {
        var projectDurationSeconds = await db.MovieProjects.AsNoTracking()
            .Where(item => item.Id == movieId).Select(item => item.DurationSeconds).SingleAsync();

        var actPositions = (await db.MovieActs.AsNoTracking()
                .Where(item => item.MovieProjectId == movieId).ToListAsync())
            .ToDictionary(item => item.Id, item => item.Sequence);
        var sequencePositions = (await db.MovieSequences.AsNoTracking()
                .Where(item => item.MovieAct.MovieProjectId == movieId).ToListAsync())
            .ToDictionary(item => item.Id, item => (ActSequence: actPositions.TryGetValue(item.MovieActId, out var actSequence) ? actSequence : 0, item.Sequence));

        var sceneEntities = await db.MovieScenes.AsNoTracking()
            .Where(item => item.MovieProjectId == movieId)
            .Select(item => new SceneRow(item.Id, item.MovieSequenceId ?? Guid.Empty, item.Sequence, item.DurationSeconds ?? 0))
            .ToListAsync();

        var shotEntities = await db.MovieShots.AsNoTracking()
            .Where(item => item.Scene.MovieProjectId == movieId && item.Status != MovieShotStatuses.Archived)
            .Select(item => new ShotRow(item.Id, item.MovieSceneId, item.Sequence, item.DurationSeconds ?? 0))
            .ToListAsync();
        var shotsByScene = shotEntities
            .GroupBy(item => item.SceneId)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Sequence).ToList());

        var ordered = sceneEntities
            .OrderBy(item => sequencePositions.TryGetValue(item.SequenceId, out var head) ? head.ActSequence : 0)
            .ThenBy(item => sequencePositions.TryGetValue(item.SequenceId, out var mid) ? mid.Sequence : 0)
            .ThenBy(item => item.Sequence)
            .ToList();

        var scenes = new List<SceneSnapshot>();
        var sceneOrder = new List<(Guid SceneId, Guid SequenceId, int Sequence)>();
        var cumulativeShotSeconds = 0;
        foreach (var scene in ordered)
        {
            var sceneShots = shotsByScene.TryGetValue(scene.Id, out var found) ? found : new List<ShotRow>();
            var shotIds = new List<string>();
            var boundaries = new List<string>();
            var sceneCursor = 0;
            foreach (var shot in sceneShots)
            {
                shotIds.Add(shot.Id.ToString("N"));
                boundaries.Add($"{shot.Id:N}@{sceneCursor}-{sceneCursor + shot.DurationSeconds}");
                sceneCursor += shot.DurationSeconds;
                cumulativeShotSeconds += shot.DurationSeconds;
            }

            scenes.Add(new SceneSnapshot(
                scene.Id, scene.SequenceId, scene.Sequence, scene.DurationSeconds, sceneShots.Count,
                string.Join("|", shotIds), string.Join("|", boundaries)));
            sceneOrder.Add((scene.Id, scene.SequenceId, scene.Sequence));
        }

        return new TimelineSnapshot(
            projectDurationSeconds,
            scenes.Sum(item => item.DurationSeconds),
            cumulativeShotSeconds,
            scenes.Count,
            scenes.Sum(item => item.ShotCount),
            [.. sceneOrder],
            [.. scenes]);
    }

    private sealed record SceneRow(Guid Id, Guid SequenceId, int Sequence, int DurationSeconds);

    private sealed record ShotRow(Guid Id, Guid SceneId, int Sequence, int DurationSeconds);

    private sealed record SceneSnapshot(
        Guid SceneId,
        Guid SequenceId,
        int Sequence,
        int DurationSeconds,
        int ShotCount,
        string ShotIdSignature,
        string ShotBoundarySignature);

    private sealed record TimelineSnapshot(
        int ProjectDurationSeconds,
        int CumulativeSceneSeconds,
        int CumulativeShotSeconds,
        int SceneCount,
        int ShotCount,
        (Guid SceneId, Guid SequenceId, int Sequence)[] SceneOrder,
        SceneSnapshot[] Scenes);
}

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
/// Story revision current-pointer integrity. A Director proposal (or a second collaborator's
/// autosave) can create a revision whose parent is not the revision that is current at write
/// time. CreateRevisionAsync must not silently downgrade that unrelated draft, and the advance
/// of MovieStory.CurrentRevisionId must be an atomic compare-and-set rather than a last-write-wins.
/// </summary>
public sealed class MovieStoryRevisionConcurrencyTests
{
    [Fact]
    public async Task Stale_parent_create_does_not_supersede_an_unrelated_current_draft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await Harness.OpenAsync(connection);
        var seeded = await Harness.SeedAsync(db);
        var service = Harness.Service(db);

        var first = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("First premise.", null), TestToken);
        var firstId = first!.CurrentRevisionId!.Value;

        var second = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Second premise.", firstId), TestToken);
        var secondId = second!.CurrentRevisionId!.Value;
        Assert.Equal(MovieStoryRevisionStatuses.Superseded, (await RevisionAsync(db, firstId)).Status);

        // A third actor that loaded the story before `second` landed still believes `firstId` is
        // current, so it branches from it. This is the lost-update window.
        var third = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Third premise.", firstId), TestToken);
        var thirdId = third!.CurrentRevisionId!.Value;

        Assert.Equal(thirdId, (await StoryAsync(db)).CurrentRevisionId);
        // `second` was never the parent of `third`, so its draft must survive untouched:
        // silently marking it Superseded hides a collaborator's work from the current chain.
        Assert.Equal(MovieStoryRevisionStatuses.Draft, (await RevisionAsync(db, secondId)).Status);
        // Exactly one revision is superseded: `first`, which `third` genuinely replaces as a parent.
        Assert.Equal(1, await db.MovieStoryRevisions.CountAsync(item => item.Status == MovieStoryRevisionStatuses.Superseded));
        Assert.Equal(MovieStoryRevisionStatuses.Draft, (await RevisionAsync(db, thirdId)).Status);
    }

    [Fact]
    public async Task Normal_chained_create_still_supersedes_the_parent_it_replaces()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await Harness.OpenAsync(connection);
        var seeded = await Harness.SeedAsync(db);
        var service = Harness.Service(db);

        var first = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise one.", null), TestToken);
        var firstId = first!.CurrentRevisionId!.Value;
        var second = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise two.", firstId), TestToken);
        var secondId = second!.CurrentRevisionId!.Value;
        var third = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise three.", secondId), TestToken);

        Assert.NotNull(third);
        Assert.Equal(MovieStoryRevisionStatuses.Superseded, (await RevisionAsync(db, firstId)).Status);
        Assert.Equal(MovieStoryRevisionStatuses.Superseded, (await RevisionAsync(db, secondId)).Status);
        Assert.Equal(3, (await RevisionAsync(db, third!.CurrentRevisionId!.Value)).RevisionNumber);
    }

    [Fact]
    public async Task Guarded_advance_only_moves_a_current_pointer_that_was_not_changed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await Harness.OpenAsync(connection);
        var seeded = await Harness.SeedAsync(db);
        var service = Harness.Service(db);

        var first = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise one.", null), TestToken);
        var firstId = first!.CurrentRevisionId!.Value;
        var second = await service.CreateRevisionAsync(seeded.UserId, seeded.MovieId, Request("Premise two.", firstId), TestToken);
        var secondId = second!.CurrentRevisionId!.Value;
        var storyId = (await StoryAsync(db)).Id;

        var candidate = new MovieStoryRevision
        {
            Id = Guid.NewGuid(), MovieStoryId = storyId, ParentRevisionId = firstId, CreatedByUserId = seeded.UserId,
            RevisionNumber = 3, Premise = "Candidate premise.", Logline = "Candidate logline.", Synopsis = "Candidate synopsis.",
            Treatment = "Candidate treatment.", Status = MovieStoryRevisionStatuses.Draft, Authorship = MovieStoryAuthorship.Human,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Scenes = [],
        };
        db.MovieStoryRevisions.Add(candidate);
        await db.SaveChangesAsync(TestToken);

        // Stale expectation (someone else already advanced to `secondId`): the pointer must not move.
        Assert.False(await MovieStoryService.TryAdvanceCurrentRevisionAsync(db, storyId, firstId, candidate, DateTime.UtcNow, TestToken));
        Assert.Equal(secondId, (await StoryAsync(db)).CurrentRevisionId);

        // Correct expectation: exactly one row advances.
        Assert.True(await MovieStoryService.TryAdvanceCurrentRevisionAsync(db, storyId, secondId, candidate, DateTime.UtcNow, TestToken));
        var advanced = await StoryAsync(db);
        Assert.Equal(candidate.Id, advanced.CurrentRevisionId);
        Assert.Equal("Candidate premise.", advanced.Premise);
        Assert.Equal(MovieStoryApprovalStates.Draft, advanced.ApprovalState);
    }

    private static readonly CancellationToken TestToken = CancellationToken.None;

    private static MovieStoryRevisionRequest Request(string premise, Guid? parentId) => new()
    {
        Premise = premise,
        Logline = "Mara carries one message through the blackout.",
        Synopsis = "Mara reaches the harbor before dawn.",
        Treatment = "The harbor crossing becomes a test of resolve.",
        Authorship = MovieStoryAuthorship.Human,
        ParentRevisionId = parentId,
        ChangeSummary = "Concurrency harness pass",
        Scenes = [new MovieStorySceneRequest
        {
            SceneIdentifier = "SCENE-1",
            Slugline = "EXT. HARBOR - NIGHT",
            Synopsis = "Mara reaches the harbor.",
            Elements = [new MovieScreenplayElementRequest { ElementType = MovieScreenplayElementTypes.Action, Content = "Mara walks the pier." }],
        }],
    };

    private static async Task<MovieStoryRevision> RevisionAsync(TaslimDbContext db, Guid id) =>
        await db.MovieStoryRevisions.AsNoTracking().SingleAsync(item => item.Id == id);

    private static async Task<MovieStory> StoryAsync(TaslimDbContext db) =>
        await db.MovieStories.AsNoTracking().SingleAsync();

    private static class Harness
    {
        public static async Task<TaslimDbContext> OpenAsync(SqliteConnection connection)
        {
            var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return db;
        }

        public static MovieStoryService Service(TaslimDbContext db) =>
            new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        public static async Task<Seeded> SeedAsync(TaslimDbContext db)
        {
            var now = DateTime.UtcNow;
            var userId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            var movieId = Guid.NewGuid();
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = $"story-concurrency-{userId:N}",
                NormalizedUserName = "STORY-CONCURRENCY",
                DisplayName = "Story Concurrency Owner",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "Story Concurrency Workspace",
                Slug = $"story-concurrency-{Guid.NewGuid():N}",
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
                Title = "Concurrency film",
                Description = "A deterministic story revision concurrency fixture.",
                DurationSeconds = 30,
                AspectRatio = "16:9",
                Language = "en",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieTeamMembers.Add(new MovieTeamMember
            {
                Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId,
                Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return new Seeded(userId, movieId);
        }

        public sealed record Seeded(Guid UserId, Guid MovieId);
    }
}

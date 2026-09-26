using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCharacterContinuityTests
{
    [Fact]
    public async Task Projection_only_returns_characters_relevant_to_the_target_scene_and_shot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seeded = await SeedAsync(db, "Mara reaches the ferry terminal.", "Mara checks the evidence.");
        var service = new MovieCharacterContinuityService(db, new WorkspaceAccessService(db));

        var projection = await service.ProjectAsync(seeded.UserId, seeded.MovieId, seeded.SceneId, seeded.ShotId);

        var character = Assert.Single(projection!.Characters);
        Assert.Equal("Mara", character.Name);
        Assert.Equal(seeded.SceneId, projection.MovieSceneId);
        Assert.Equal(seeded.ShotId, projection.MovieShotId);
    }

    [Fact]
    public async Task Snapshot_is_bounded_and_reports_locked_fact_conflicts_with_sources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seeded = await SeedAsync(db, "Mara waits.", "Mara waits.");
        var mara = await db.MovieCharacters.SingleAsync(item => item.MovieProjectId == seeded.MovieId && item.Name == "Mara");
        mara.Wardrobe = "Blue coat";
        var cardLock = new MovieCharacterContinuityLock
        {
            Id = Guid.NewGuid(), MovieCharacterId = mara.Id, FieldKey = "wardrobe", LockedValue = "Red coat", ApprovedByUserId = seeded.UserId, ApprovedAt = DateTime.UtcNow,
        };
        db.MovieCharacterContinuityLocks.Add(cardLock);
        var state = new MovieCharacterState { Id = Guid.NewGuid(), MovieCharacterId = mara.Id, Key = "injured", InjuryOrCondition = "Bruised shoulder", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var stateLock = new MovieCharacterContinuityLock
        {
            Id = Guid.NewGuid(), MovieCharacterId = mara.Id, MovieCharacterStateId = state.Id, FieldKey = "injuryOrCondition", LockedValue = "Broken arm", ApprovedByUserId = seeded.UserId, ApprovedAt = DateTime.UtcNow,
        };
        db.MovieCharacterContinuityLocks.Add(stateLock);
        db.MovieCharacterStates.Add(state);
        await db.SaveChangesAsync();
        var service = new MovieCharacterContinuityService(db, new WorkspaceAccessService(db));

        var snapshot = await service.BuildSnapshotForTargetAsync(seeded.MovieId, seeded.SceneId, seeded.ShotId, false);

        Assert.Null(snapshot.SnapshotId);
        Assert.True(snapshot.SnapshotJson.Length <= MovieContinuitySnapshotLimits.MaxSnapshotJsonLength);
        Assert.Contains(snapshot.Warnings, item => item.Code == "locked_character_fact_conflict" && item.CharacterName == "Mara" && item.SourceId == cardLock.Id);
        Assert.Contains(snapshot.Warnings, item => item.Code == "locked_character_state_conflict" && item.Field == "injuryOrCondition");
    }

    [Fact]
    public async Task Cross_project_scene_is_rejected_before_projection_or_snapshot_creation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var seeded = await SeedAsync(db, "Mara waits.", "Mara waits.");
        var otherMovie = new MovieProject { Id = Guid.NewGuid(), WorkspaceId = seeded.WorkspaceId, CreatedByUserId = seeded.UserId, Title = "Other", Description = "Other movie", DurationSeconds = 30, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = DateTime.UtcNow } };
        var otherScene = new MovieScene { Id = Guid.NewGuid(), MovieProjectId = otherMovie.Id, Sequence = 1, Title = "Other scene", Summary = "Other.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MovieProjects.Add(otherMovie);
        db.MovieScenes.Add(otherScene);
        await db.SaveChangesAsync();
        var service = new MovieCharacterContinuityService(db, new WorkspaceAccessService(db));

        await Assert.ThrowsAsync<MovieContinuityTargetException>(() => service.BuildSnapshotForTargetAsync(seeded.MovieId, otherScene.Id, null, false));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SeededMovie> SeedAsync(TaslimDbContext db, string sceneSummary, string shotDescription)
    {
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"continuity-{userId:N}", NormalizedUserName = $"CONTINUITY-{userId:N}".ToUpperInvariant(), DisplayName = "Continuity Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Continuity Workspace", Slug = $"continuity-{userId:N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Continuity", Description = "Continuity test movie", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Ferry", Summary = sceneSummary, CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = shotDescription, CreatedAt = now, UpdatedAt = now });
        db.MovieCharacters.Add(new MovieCharacter { Id = Guid.NewGuid(), MovieProjectId = movieId, Name = "Mara", Description = "Lead investigator", Wardrobe = "Charcoal coat", CreatedAt = now, UpdatedAt = now });
        db.MovieCharacters.Add(new MovieCharacter { Id = Guid.NewGuid(), MovieProjectId = movieId, Name = "Ilan", Description = "Witness", Wardrobe = "Dock jacket", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new SeededMovie(userId, workspaceId, movieId, sceneId, shotId);
    }

    private sealed record SeededMovie(Guid UserId, Guid WorkspaceId, Guid MovieId, Guid SceneId, Guid ShotId);
}

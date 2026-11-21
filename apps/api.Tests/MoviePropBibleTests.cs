using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MoviePropBibleTests
{
    [Fact]
    public async Task Invalid_first_variant_does_not_persist_an_empty_prop_bible()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "prop-owner", NormalizedUserName = "PROP-OWNER", DisplayName = "Prop Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Prop Workspace", Slug = $"prop-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Prop Film", Description = "A prop test.", DurationSeconds = 60, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieProps.Add(new MovieProp { Id = propId, MovieProjectId = movieId, Name = "Red notebook", Description = "The canonical notebook.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var service = new MoviePropBibleService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), new DeterministicMovieRecurringPropDetector());

        await Assert.ThrowsAsync<MoviePropBibleValidationException>(() => service.AddVariantAsync(userId, propId, new MoviePropBibleVariantRequest("", "", null, null, null, null), CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.False(await db.MoviePropBibles.AsNoTracking().AnyAsync(item => item.MoviePropId == propId));

        var persisted = await service.AddVariantAsync(userId, propId, new MoviePropBibleVariantRequest("hero", "Hero notebook", null, null, null, null), CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Single(persisted!.Variants);
    }

    [Fact]
    public void Deterministic_detector_marks_a_prop_recurring_from_repeated_scene_usage()
    {
        var propId = Guid.NewGuid();
        var sceneOne = Guid.NewGuid();
        var sceneTwo = Guid.NewGuid();
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(propId, "Red notebook")],
            [
                new MoviePropDetectionUsageInput(propId, sceneOne, Guid.NewGuid()),
                new MoviePropDetectionUsageInput(propId, sceneTwo, Guid.NewGuid()),
            ],
            [],
            DateTime.UnixEpoch);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(result.ProviderFree);
        Assert.Equal(1, result.DetectorVersion);
        Assert.True(candidate.IsRecurring);
        Assert.Equal(2, candidate.SceneCount);
        Assert.Equal("The prop is present across multiple scenes.", candidate.Reason);
    }

    [Fact]
    public void Deterministic_detector_uses_bounded_shot_text_without_inventing_records()
    {
        var propId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(propId, "Brass compass")],
            [],
            [
                new MoviePropDetectionShotInput(sceneId, shotId, "The brass compass rests on the crate."),
                new MoviePropDetectionShotInput(Guid.NewGuid(), Guid.NewGuid(), "The brass compass remains in frame."),
            ],
            DateTime.UnixEpoch);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.IsRecurring);
        Assert.Equal(2, candidate.UsageCount);
        Assert.Equal(2, candidate.SceneCount);
        Assert.True(result.ProviderFree);
    }

    [Fact]
    public void Detector_does_not_report_single_occurrence_as_recurring()
    {
        var result = new DeterministicMovieRecurringPropDetector().Detect(
            [new MoviePropDetectionInput(Guid.NewGuid(), "One-off cup")],
            [new MoviePropDetectionUsageInput(Guid.NewGuid(), Guid.NewGuid(), null)],
            [],
            DateTime.UnixEpoch);

        Assert.Empty(result.Candidates);
    }
}

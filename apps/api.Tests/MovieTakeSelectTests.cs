using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieTakeSelectTests
{
    [Fact]
    public async Task Selects_support_multiple_ranges_and_independent_review_without_changing_take_selection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieTakeSelectService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        var first = await service.CreateAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, new MovieTakeSelectRequest
        {
            Label = "Usable opening",
            StartMilliseconds = 1_000,
            EndMilliseconds = 2_500,
            Notes = "Clean entrance and eyeline.",
        }, CancellationToken.None);
        var second = await service.CreateAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, new MovieTakeSelectRequest
        {
            Label = "Usable reaction",
            StartMilliseconds = 4_000,
            EndMilliseconds = 5_250,
        }, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first!.SelectNumber);
        Assert.Equal(2, second!.SelectNumber);
        Assert.Equal(MovieTakeSelectStatuses.Draft, first.Status);
        Assert.Contains(fixture.TakeId.ToString(), first.ProvenanceJson, StringComparison.OrdinalIgnoreCase);

        var approved = await service.ReviewAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, first.Id,
            new MovieTakeSelectReviewRequest { Decision = MovieTakeSelectStatuses.Approved, Comment = "Approved range only." }, CancellationToken.None);

        Assert.Equal(MovieTakeSelectStatuses.Approved, approved!.Status);
        Assert.Equal(fixture.UserId, approved.ReviewedByUserId);
        var take = await db.MovieTakes.SingleAsync(item => item.Id == fixture.TakeId);
        var shot = await db.MovieShots.SingleAsync(item => item.Id == take.MovieShotId);
        Assert.Equal(MovieTakeStatuses.Selected, take.Status);
        Assert.Equal(fixture.TakeId, shot.SelectedTakeId);
        Assert.Null(take.FinalizedAt);

        var listed = await service.ListAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, CancellationToken.None);
        Assert.Equal(2, listed!.Count);
        Assert.Equal(MovieTakeSelectStatuses.Draft, listed[1].Status);
        Assert.Null(await service.ListAsync(fixture.UserId, Guid.NewGuid(), fixture.TakeId, CancellationToken.None));
    }

    [Fact]
    public async Task Timeline_uses_approved_select_boundaries_and_rejects_unreviewed_selects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var access = new MovieCollaborationAccess(db, new WorkspaceAccessService(db));
        var selects = new MovieTakeSelectService(db, access);
        var timeline = new MovieTimelineService(db, access);

        var draft = await selects.CreateAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, new MovieTakeSelectRequest
        {
            Label = "Needs review",
            StartMilliseconds = 250,
            EndMilliseconds = 1_500,
        }, CancellationToken.None);
        Assert.NotNull(draft);
        await Assert.ThrowsAsync<MovieTimelineValidationException>(() => timeline.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceSelectId = draft!.Id, TimelineInMilliseconds = 0 }],
            }],
        }, CancellationToken.None));

        var approved = await selects.ReviewAsync(fixture.UserId, fixture.MovieId, fixture.TakeId, draft.Id,
            new MovieTakeSelectReviewRequest { Decision = MovieTakeSelectStatuses.Approved }, CancellationToken.None);
        var revision = await timeline.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceSelectId = approved!.Id, TimelineInMilliseconds = 2_000 }],
            }],
        }, CancellationToken.None);

        var item = Assert.Single(Assert.Single(revision!.Tracks).Items);
        Assert.Equal(fixture.TakeId, item.SourceTakeId);
        Assert.Equal(approved.Id, item.SourceSelectId);
        Assert.Equal(250, item.SourceInMilliseconds);
        Assert.Equal(1_500, item.SourceOutMilliseconds);
        Assert.Equal(1_250, item.DurationMilliseconds);
        Assert.Equal(3_250, item.TimelineOutMilliseconds);

        await Assert.ThrowsAsync<MovieTimelineValidationException>(() => timeline.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceSelectId = approved.Id, TimelineInMilliseconds = 0, SourceInMilliseconds = 0 }],
            }],
        }, CancellationToken.None));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<SelectFixture> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "take.mp4", StoredFileName = "take.mp4",
            ContentType = "video/mp4", Extension = ".mp4", SizeBytes = 128, StorageKey = $"test/{Guid.NewGuid():N}",
            StorageProvider = FileStorageProviders.Local, Status = StoredFileStatus.Ready, CreatedAt = now, ProcessedAt = now,
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = file.Id,
            Name = "take", AssetType = AssetTypes.Video, MimeType = "video/mp4", MetadataJson = "{\"durationMilliseconds\":8000}",
            Status = AssetStatus.Active, CreatedAt = now, UpdatedAt = now,
        };
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "select-owner", NormalizedUserName = "SELECT-OWNER", DisplayName = "Select Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Select Workspace", Slug = $"select-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Select Movie", Description = "Select fixture.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Opening", Summary = "Select opening.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Selected shot.", DurationSeconds = 8, CreatedAt = now, UpdatedAt = now });
        db.StoredFiles.Add(file);
        db.Assets.Add(asset);
        db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "Selected take", Status = MovieTakeStatuses.Selected, AssetId = asset.Id, SelectedAt = now, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);
        shot.SelectedTakeId = takeId;
        await db.SaveChangesAsync();
        return new SelectFixture(userId, movieId, takeId);
    }

    private sealed record SelectFixture(Guid UserId, Guid MovieId, Guid TakeId);
}

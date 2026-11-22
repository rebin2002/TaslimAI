using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieTimelineTransitionPersistenceTests
{
    [Fact]
    public async Task Concurrent_transition_edits_cannot_materialize_the_same_next_version()
    {
        const string connectionString = "Data Source=timeline-transition-cas;Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using var setupDb = CreateDb(keeper);
        var fixture = await SeedAsync(setupDb);
        var timelineService = new MovieTimelineService(setupDb, new MovieCollaborationAccess(setupDb, new WorkspaceAccessService(setupDb)));
        await timelineService.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 0, SourceInMilliseconds = 0, SourceOutMilliseconds = 8_000 }],
            }],
        }, CancellationToken.None);

        var timelineId = await setupDb.MovieTimelines.Where(item => item.MovieProjectId == fixture.MovieId).Select(item => item.Id).SingleAsync();
        var shotId = await setupDb.MovieTakes.Where(item => item.Id == fixture.TakeId).Select(item => item.MovieShotId).SingleAsync();
        var clipId = Guid.NewGuid();
        var canonical = new MovieCanonicalTimelineContract(timelineId, 1,
            [new MovieTimelineClipContract(clipId, shotId, 1, 0m, 8m)], []);
        var proposed = new MovieTimelineTransitionContract(Guid.NewGuid(), MovieTimelineTransitionTypes.FadeIn, null, clipId, 0m, 1m);
        var barrier = new InitialTransitionReadBarrier();
        await using var firstDb = CreateDb(connectionString, barrier);
        await using var secondDb = CreateDb(connectionString, barrier);
        var firstService = CreateTransitionService(firstDb);
        var secondService = CreateTransitionService(secondDb);

        var outcomes = await Task.WhenAll(
            CaptureAsync(() => firstService.ApplyAsync(fixture.UserId, fixture.MovieId, Request(canonical, proposed, Guid.NewGuid()), CancellationToken.None)),
            CaptureAsync(() => secondService.ApplyAsync(fixture.UserId, fixture.MovieId, Request(canonical, proposed, Guid.NewGuid()), CancellationToken.None)));

        Assert.Single(outcomes, item => item.Result is not null);
        var conflict = Assert.Single(outcomes, item => item.Error is MovieTimelineValidationException);
        Assert.Equal(MovieTimelineValidationCodes.TimelineVersionConflict, ((MovieTimelineValidationException)conflict.Error!).Code);
        var persisted = await setupDb.MovieTimelineTransitionEdits.AsNoTracking().Where(item => item.MovieTimelineId == timelineId).ToListAsync();
        Assert.Single(persisted);
        Assert.Equal(2, persisted[0].ResultTimelineVersion);
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static TaslimDbContext CreateDb(string connectionString, DbCommandInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connectionString).AddInterceptors(interceptor).Options);

    private static MovieTimelineTransitionService CreateTransitionService(TaslimDbContext db)
    {
        var collaboration = new MovieCollaborationAccess(db, new WorkspaceAccessService(db));
        return new MovieTimelineTransitionService(db, new MovieAuthorizationService(collaboration), collaboration);
    }

    private static Taslim.Api.Movies.MovieTimelineTransitionEditRequest Request(MovieCanonicalTimelineContract timeline, MovieTimelineTransitionContract proposed, Guid decisionId)
    {
        var overrideRecord = new MovieUserTransitionOverrideContract(Guid.NewGuid(), timeline.TimelineId, timeline.Version, null, proposed, "Approve this transition", null);
        return new Taslim.Api.Movies.MovieTimelineTransitionEditRequest
        {
            Timeline = timeline,
            Decision = new MovieTimelineEditDecisionContract(decisionId, timeline.TimelineId, timeline.Version,
                MovieTimelineEditActions.Add, null, proposed, null, overrideRecord),
        };
    }

    private static async Task<(MovieTimelineTransitionEditDto? Result, Exception? Error)> CaptureAsync(Func<Task<MovieTimelineTransitionEditDto?>> action)
    {
        try { return (await action(), null); }
        catch (Exception exception) { return (null, exception); }
    }

    private static async Task<TimelineFixture> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "timeline-owner", NormalizedUserName = "TIMELINE-OWNER", DisplayName = "Timeline Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Timeline Workspace", Slug = $"timeline-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Timeline Movie", Description = "A timeline fixture.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Opening", Summary = "Timeline opening.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Approved shot.", DurationSeconds = 8, CreatedAt = now, UpdatedAt = now });
        var videoFile = NewFile(workspaceId, userId, "shot.mp4", "video/mp4", now);
        var audioFile = NewFile(workspaceId, userId, "music.mp3", "audio/mpeg", now);
        var captionFile = NewFile(workspaceId, userId, "captions.vtt", "text/vtt", now);
        db.StoredFiles.AddRange(videoFile, audioFile, captionFile);
        var videoAsset = NewAsset(workspaceId, userId, videoFile, "shot", AssetTypes.Video, "video/mp4", "{\"durationMilliseconds\":8000}", now);
        var audioAsset = NewAsset(workspaceId, userId, audioFile, "music", AssetTypes.Music, "audio/mpeg", "{\"durationMilliseconds\":6500}", now);
        var captionAsset = NewAsset(workspaceId, userId, captionFile, "captions", AssetTypes.File, "text/vtt", "{\"durationMilliseconds\":2500,\"approved\":true}", now);
        db.Assets.AddRange(videoAsset, audioAsset, captionAsset);
        db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "Selected take", Status = MovieTakeStatuses.Selected, AssetId = videoAsset.Id, SelectedAt = now, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);
        shot.SelectedTakeId = takeId;
        await db.SaveChangesAsync();
        return new TimelineFixture(userId, movieId, takeId, audioAsset.Id, captionAsset.Id);
    }

    private static StoredFile NewFile(Guid workspaceId, Guid userId, string name, string contentType, DateTime now) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, OriginalFileName = name, StoredFileName = name,
        ContentType = contentType, Extension = Path.GetExtension(name), SizeBytes = 128, StorageKey = $"test/{Guid.NewGuid():N}",
        StorageProvider = FileStorageProviders.Local, Status = StoredFileStatus.Ready, CreatedAt = now, ProcessedAt = now,
    };

    private static Asset NewAsset(Guid workspaceId, Guid userId, StoredFile file, string name, string type, string mime, string metadata, DateTime now) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, StoredFileId = file.Id, Name = name,
        AssetType = type, MimeType = mime, MetadataJson = metadata, Status = AssetStatus.Active, CreatedAt = now, UpdatedAt = now,
    };

    private sealed record TimelineFixture(Guid UserId, Guid MovieId, Guid TakeId, Guid AudioAssetId, Guid CaptionAssetId);

    private sealed class InitialTransitionReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("MovieTimelineTransitionEdits", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref arrivals) <= 2)
            {
                if (Volatile.Read(ref arrivals) == 2) released.TrySetResult(true);
                await released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }
}

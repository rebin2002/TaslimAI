using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieTimelineTests
{
    [Fact]
    public async Task Canonical_transition_edit_persists_assembly_provenance_and_replays_idempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieTimelineService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
        var timelineId = Guid.NewGuid();
        var firstClipId = Guid.NewGuid();
        var secondClipId = Guid.NewGuid();
        var baseTimeline = new MovieCanonicalTimelineContract(
            timelineId,
            10,
            [
                new(firstClipId, fixture.ShotId, 1, 0m, 10m),
                new(secondClipId, fixture.ShotId, 2, 10m, 8m),
            ],
            [new(Guid.NewGuid(), MovieTimelineTransitionTypes.Cut, firstClipId, secondClipId, 10m, 0m)]);
        var baseRevision = await service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            CanonicalTimelineJson = JsonSerializer.Serialize(baseTimeline, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        }, CancellationToken.None);
        Assert.NotNull(baseRevision);

        var addedTransition = new MovieTimelineTransitionContract(Guid.NewGuid(), MovieTimelineTransitionTypes.FadeOut, secondClipId, null, 16m, 2m);
        var decision = new MovieTimelineEditDecisionContract(
            Guid.NewGuid(), timelineId, 10, MovieTimelineEditActions.Add, null, addedTransition, null,
            new MovieUserTransitionOverrideContract(Guid.NewGuid(), timelineId, 10, null, addedTransition, "The outgoing beat needs a longer fade."));
        var request = new Taslim.Api.Contracts.MovieTimelineTransitionEditRequest { BaseRevisionId = baseRevision!.Id, Decision = decision };

        var applied = await service.ApplyTransitionEditAsync(fixture.UserId, fixture.MovieId, request, CancellationToken.None);
        Assert.NotNull(applied);
        Assert.Equal(2, applied!.RevisionNumber);

        var persisted = await db.MovieTimelineTransitionEdits.SingleAsync();
        Assert.Equal(10, persisted.BaseTimelineVersion);
        Assert.Equal(11, persisted.ResultTimelineVersion);
        Assert.Equal(decision.DecisionId, persisted.DecisionId);
        var persistedTimeline = JsonSerializer.Deserialize<MovieCanonicalTimelineContract>(persisted.ResultTimelineJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(persistedTimeline);
        Assert.Equal(2, persistedTimeline!.Transitions.Count);
        Assert.Contains(persistedTimeline.Transitions, item => item.Id == addedTransition.Id);

        var replayed = await service.ApplyTransitionEditAsync(fixture.UserId, fixture.MovieId, request, CancellationToken.None);
        Assert.NotNull(replayed);
        Assert.Equal(applied.Id, replayed!.Id);
        Assert.Equal(2, await db.MovieTimelineRevisions.CountAsync());
        Assert.Single(await db.MovieTimelineTransitionEdits.ToListAsync());
    }

    [Fact]
    public async Task Timeline_maps_selected_take_and_approved_assets_with_trimmed_ranges_gaps_and_cross_track_overlap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieTimelineService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        var revision = await service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Label = "Assembly pass 1",
            ChangeSummary = "Place the approved takes and sound bed.",
            Tracks =
            [
                new MovieTimelineTrackRequest
                {
                    Kind = MovieTimelineTrackKinds.Video,
                    Name = "Picture",
                    Items =
                    [
                        new MovieTimelineItemRequest
                        {
                            Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId,
                            TimelineInMilliseconds = 1_000, SourceInMilliseconds = 500, SourceOutMilliseconds = 3_500,
                        },
                        new MovieTimelineItemRequest
                        {
                            Kind = MovieTimelineItemKinds.Gap, TimelineInMilliseconds = 4_000, TimelineOutMilliseconds = 4_500,
                        },
                        new MovieTimelineItemRequest
                        {
                            Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId,
                            TimelineInMilliseconds = 4_500, SourceInMilliseconds = 0, SourceOutMilliseconds = 2_000,
                        },
                    ],
                },
                new MovieTimelineTrackRequest
                {
                    Kind = MovieTimelineTrackKinds.Audio,
                    Name = "Mix",
                    Items =
                    [new MovieTimelineItemRequest
                    {
                        Kind = MovieTimelineItemKinds.AudioAsset, SourceAssetId = fixture.AudioAssetId,
                        TimelineInMilliseconds = 0, TimelineOutMilliseconds = 6_500,
                    }],
                },
                new MovieTimelineTrackRequest
                {
                    Kind = MovieTimelineTrackKinds.Captions,
                    Name = "English captions",
                    Items =
                    [new MovieTimelineItemRequest
                    {
                        Kind = MovieTimelineItemKinds.CaptionAsset, SourceAssetId = fixture.CaptionAssetId,
                        TimelineInMilliseconds = 1_500, TimelineOutMilliseconds = 4_000,
                    }],
                },
            ],
        }, CancellationToken.None);

        Assert.NotNull(revision);
        Assert.Equal(6_500, revision!.DurationMilliseconds);
        Assert.Equal(3, revision.Tracks.Count);
        var picture = revision.Tracks.Single(item => item.Kind == MovieTimelineTrackKinds.Video);
        Assert.Equal(3, picture.Items.Count);
        Assert.Equal(3_000, picture.Items[0].DurationMilliseconds);
        Assert.True(picture.Items[1].IsGap);
        Assert.Equal(2_000, picture.Items[2].DurationMilliseconds);
        Assert.Equal(0, revision.Tracks.Single(item => item.Kind == MovieTimelineTrackKinds.Audio).Items[0].TimelineInMilliseconds);
        Assert.Equal(1_500, revision.Tracks.Single(item => item.Kind == MovieTimelineTrackKinds.Captions).Items[0].TimelineInMilliseconds);

        var timeline = await service.GetAsync(fixture.UserId, fixture.MovieId, CancellationToken.None);
        Assert.NotNull(timeline);
        Assert.Equal(revision.Id, timeline!.CurrentRevisionId);
        Assert.Equal(revision.Id, timeline.CurrentRevision!.Id);
    }

    [Fact]
    public async Task Timeline_revisions_clone_without_mutating_history_and_locking_requires_a_new_draft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieTimelineService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));
        var first = await service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 0, SourceInMilliseconds = 0, SourceOutMilliseconds = 1_000 }],
            }],
        }, CancellationToken.None);
        var locked = await service.LockRevisionAsync(fixture.UserId, first!.Id, CancellationToken.None);
        Assert.Equal(MovieTimelineRevisionStatuses.Locked, locked!.Status);

        await Assert.ThrowsAsync<MovieTimelineValidationException>(() => service.AddItemAsync(fixture.UserId, first.Tracks[0].Id,
            new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 1_000, SourceInMilliseconds = 0, SourceOutMilliseconds = 500 }, CancellationToken.None));

        var second = await service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            BaseRevisionId = first.Id,
            Label = "Assembly pass 2",
        }, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, second!.RevisionNumber);
        Assert.Equal(first.Id, second.BaseRevisionId);
        Assert.Single(second.Tracks);
        Assert.NotEqual(first.Tracks[0].Id, second.Tracks[0].Id);
        Assert.Equal(MovieTimelineRevisionStatuses.Locked, (await service.GetAsync(fixture.UserId, fixture.MovieId, CancellationToken.None))!.Revisions[0].Status);
    }

    [Fact]
    public async Task Timeline_rejects_same_track_overlap_and_unapproved_or_unselected_visual_sources()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieTimelineService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        await Assert.ThrowsAsync<MovieTimelineValidationException>(() => service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items =
                [
                    new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 0, SourceInMilliseconds = 0, SourceOutMilliseconds = 2_000 },
                    new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 1_999, SourceInMilliseconds = 0, SourceOutMilliseconds = 1_000 },
                ],
            }],
        }, CancellationToken.None));

        var unselectedTake = await db.MovieTakes.SingleAsync(item => item.Id == fixture.TakeId);
        unselectedTake.SelectedAt = null;
        unselectedTake.Status = MovieTakeStatuses.Approved;
        (await db.MovieShots.SingleAsync(item => item.Id == unselectedTake.MovieShotId)).SelectedTakeId = null;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<MovieTimelineValidationException>(() => service.CreateRevisionAsync(fixture.UserId, fixture.MovieId, new MovieTimelineRevisionRequest
        {
            Tracks = [new MovieTimelineTrackRequest
            {
                Kind = MovieTimelineTrackKinds.Video,
                Items = [new MovieTimelineItemRequest { Kind = MovieTimelineItemKinds.VisualTake, SourceTakeId = fixture.TakeId, TimelineInMilliseconds = 0, SourceInMilliseconds = 0, SourceOutMilliseconds = 1_000 }],
            }],
        }, CancellationToken.None));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

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
        return new TimelineFixture(userId, movieId, shotId, takeId, audioAsset.Id, captionAsset.Id);
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

    private sealed record TimelineFixture(Guid UserId, Guid MovieId, Guid ShotId, Guid TakeId, Guid AudioAssetId, Guid CaptionAssetId);
}

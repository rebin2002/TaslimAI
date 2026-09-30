using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCaptionFormatTests
{
    [Fact]
    public void Timecodes_round_trip_with_srt_and_vtt_precision()
    {
        Assert.Equal(3_723_456, MovieCaptionTimecode.Parse("01:02:03,456"));
        Assert.Equal(3_723_456, MovieCaptionTimecode.Parse("01:02:03.456"));
        Assert.Equal("01:02:03,456", MovieCaptionTimecode.FormatSrt(3_723_456));
        Assert.Equal("01:02:03.456", MovieCaptionTimecode.FormatVtt(3_723_456));
    }

    [Fact]
    public void Srt_and_vtt_adapters_preserve_multiline_text_and_speaker_metadata()
    {
        var document = new MovieCaptionDocument([new MovieCaptionDocumentCue(1_250, 3_500, "Hello\nworld", "Mara")]);
        var srt = new SrtMovieCaptionFormatAdapter();
        var vtt = new WebVttMovieCaptionFormatAdapter();

        var parsedSrt = srt.Parse(srt.Serialize(document));
        var parsedVtt = vtt.Parse(vtt.Serialize(document));

        Assert.Equal(document.Cues[0].StartMilliseconds, parsedSrt.Cues[0].StartMilliseconds);
        Assert.Equal(document.Cues[0].Text, parsedSrt.Cues[0].Text);
        Assert.Null(parsedSrt.Cues[0].SpeakerName);
        Assert.Equal(document.Cues[0], parsedVtt.Cues[0]);
        Assert.StartsWith("WEBVTT\r\n", vtt.Serialize(document), StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_reject_invalid_time_ranges()
    {
        var adapter = new SrtMovieCaptionFormatAdapter();
        Assert.Throws<MovieCaptionFormatException>(() => adapter.Parse("1\n00:00:02,000 --> 00:00:01,000\nInvalid"));
    }
}

public sealed class MovieCaptionServiceTests
{
    [Fact]
    public async Task Tracks_import_export_and_timeline_keep_canonical_links_and_rtl_metadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, sceneId, shotId, takeId, characterId) = await SeedAsync(db);
        var service = CreateService(db);

        var track = await service.ImportAsync(userId, movieId, new MovieCaptionImportRequest
        {
            Name = "Arabic subtitles",
            Language = "AR-SA",
            IsRtl = true,
            TrackType = MovieCaptionTrackTypes.Subtitle,
            Format = MovieCaptionFormats.Vtt,
            SourceFileName = "arabic.vtt",
            Content = "WEBVTT\n\n1\n00:00:00.500 --> 00:00:02.000\n<v Mara>مرحبا</v>\n",
        }, CancellationToken.None);

        Assert.NotNull(track);
        Assert.Equal("ar-SA", track!.Language);
        Assert.True(track.IsRtl);
        Assert.Equal("vtt", track.SourceFormat);
        Assert.Single(track.Cues);
        Assert.Equal("Mara", track.Cues[0].SpeakerName);

        var cue = await service.AddCueAsync(userId, track.Id, new MovieCaptionCueRequest
        {
            Sequence = 2,
            StartTimecode = "00:00:02.000",
            EndTimecode = "00:00:04.500",
            Text = "The handoff is ready.",
            SpeakerCharacterId = characterId,
            MovieSceneId = sceneId,
            MovieShotId = shotId,
            MovieTakeId = takeId,
        }, CancellationToken.None);

        Assert.NotNull(cue);
        Assert.Equal(characterId, cue!.SpeakerCharacterId);
        Assert.Equal(sceneId, cue.MovieSceneId);
        var timeline = await service.GetTimelineAsync(userId, movieId, CancellationToken.None);
        Assert.Equal(1, timeline!.TrackCount);
        Assert.Equal(2, timeline.Cues.Count);
        Assert.Equal(cue.Id, timeline.Cues[1].Cue.Id);

        var export = await service.ExportAsync(userId, track.Id, MovieCaptionFormats.Srt, CancellationToken.None);
        Assert.NotNull(export);
        Assert.Equal("application/x-subrip", export!.ContentType);
        Assert.Contains("00:00:02,000 --> 00:00:04,500", export.Content, StringComparison.Ordinal);
        Assert.Contains("The handoff is ready.", export.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_rejects_overlaps_cross_project_links_and_outsiders_without_external_adapters()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, sceneId, _, _, _) = await SeedAsync(db);
        var service = CreateService(db);
        var track = await service.CreateTrackAsync(userId, movieId, new MovieCaptionTrackRequest { Name = "English", Language = "en" }, CancellationToken.None);

        await service.AddCueAsync(userId, track!.Id, new MovieCaptionCueRequest { StartTimecode = "00:00:01.000", EndTimecode = "00:00:03.000", Text = "One" }, CancellationToken.None);
        await Assert.ThrowsAsync<MovieCaptionValidationException>(() => service.AddCueAsync(userId, track.Id, new MovieCaptionCueRequest { Sequence = 2, StartTimecode = "00:00:02.000", EndTimecode = "00:00:04.000", Text = "Overlap" }, CancellationToken.None));
        await Assert.ThrowsAsync<MovieCaptionValidationException>(() => service.AddCueAsync(userId, track.Id, new MovieCaptionCueRequest { Sequence = 2, StartTimecode = "00:00:04.000", EndTimecode = "00:00:05.000", Text = "Wrong scene", MovieSceneId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Null(await service.GetTracksAsync(Guid.NewGuid(), movieId, CancellationToken.None));
        Assert.NotNull(await service.GetTimelineAsync(userId, movieId, CancellationToken.None));
        _ = sceneId;
    }

    [Fact]
    public async Task Export_uses_injected_fake_adapter_for_seam_testing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, _, _, _, _) = await SeedAsync(db);
        var service = new MovieCaptionService(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), [new FakeCaptionAdapter(), new SrtMovieCaptionFormatAdapter(), new WebVttMovieCaptionFormatAdapter()]);
        var track = await service.CreateTrackAsync(userId, movieId, new MovieCaptionTrackRequest { Name = "Fake seam", Language = "en" }, CancellationToken.None);
        await service.AddCueAsync(userId, track!.Id, new MovieCaptionCueRequest { StartTimecode = "00:00:00.000", EndTimecode = "00:00:01.000", Text = "fixture" }, CancellationToken.None);
        var export = await service.ExportAsync(userId, track.Id, "srt", CancellationToken.None);
        Assert.Equal("fake-format", export!.Content);
    }

    private static MovieCaptionService CreateService(TaslimDbContext db) => new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), [new SrtMovieCaptionFormatAdapter(), new WebVttMovieCaptionFormatAdapter()]);
    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid UserId, Guid MovieId, Guid SceneId, Guid ShotId, Guid TakeId, Guid CharacterId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "caption-owner", NormalizedUserName = "CAPTION-OWNER", DisplayName = "Caption Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Caption Workspace", Slug = $"caption-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Caption Movie", Description = "A caption test.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Scene", Summary = "A scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Shot", CreatedAt = now, UpdatedAt = now });
        db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "Take 1", CreatedAt = now, UpdatedAt = now });
        db.MovieCharacters.Add(new MovieCharacter { Id = characterId, MovieProjectId = movieId, Name = "Mara", Description = "Courier", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, movieId, sceneId, shotId, takeId, characterId);
    }

    private sealed class FakeCaptionAdapter : IMovieCaptionFormatAdapter
    {
        public string Format => MovieCaptionFormats.Srt;
        public string ContentType => "text/fake";
        public MovieCaptionDocument Parse(string content) => new([]);
        public string Serialize(MovieCaptionDocument document) => "fake-format";
    }
}

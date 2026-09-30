using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieWave4IntegrationSeamTests
{
    [Fact]
    public void Timeline_order_and_provenance_are_deterministic_for_the_complete_track_set()
    {
        var project = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var shot = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var selectedTake = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var video = Guid.Parse("40000000-0000-0000-0000-000000000004");
        var voice = Guid.Parse("50000000-0000-0000-0000-000000000005");
        var music = Guid.Parse("60000000-0000-0000-0000-000000000006");
        var sfx = Guid.Parse("70000000-0000-0000-0000-000000000007");

        var input = new MovieWave4TimelineItem[]
        {
            new(MovieWave4TrackKinds.Captions, null, 0, 1_800, CaptionText: "The courier keeps moving."),
            new(MovieWave4TrackKinds.Music, music, 0, 5_000),
            new(MovieWave4TrackKinds.Video, video, 0, 5_000, selectedTake),
            new(MovieWave4TrackKinds.Voice, voice, 0, 1_800),
            new(MovieWave4TrackKinds.SoundEffect, sfx, 900, 300),
        };

        var first = MovieWave4ExportSeam.BuildTimeline(input, 5_000);
        var second = MovieWave4ExportSeam.BuildTimeline(input.Reverse(), 5_000);

        Assert.Equal(first.ProvenanceHash, second.ProvenanceHash);
        Assert.Equal(first.Items.Select(item => item.TrackKind), second.Items.Select(item => item.TrackKind));
        Assert.Equal(
            new[] { MovieWave4TrackKinds.Captions, MovieWave4TrackKinds.Music, MovieWave4TrackKinds.Video, MovieWave4TrackKinds.Voice, MovieWave4TrackKinds.SoundEffect },
            first.Items.Select(item => item.TrackKind));
        Assert.DoesNotContain("provider", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", JsonSerializer.Serialize(first), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_handoff_requires_the_selected_final_take_and_keeps_execution_pending()
    {
        var project = Guid.NewGuid();
        var shot = Guid.NewGuid();
        var keyframe = Guid.NewGuid();
        var selectedTake = Guid.NewGuid();
        var otherTake = Guid.NewGuid();
        var timeline = MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Video, Guid.NewGuid(), 0, 5_000, selectedTake),
            new(MovieWave4TrackKinds.Voice, Guid.NewGuid(), 0, 1_800),
            new(MovieWave4TrackKinds.SoundEffect, Guid.NewGuid(), 900, 300),
            new(MovieWave4TrackKinds.Music, Guid.NewGuid(), 0, 5_000),
            new(MovieWave4TrackKinds.Captions, null, 0, 1_800, CaptionText: "A deterministic caption."),
        ], 5_000);

        var blocked = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(project, shot, selectedTake, otherTake, keyframe, timeline));
        var ready = MovieWave4ExportSeam.Evaluate(new MovieWave4ExportRequest(project, shot, selectedTake, selectedTake, keyframe, timeline));

        Assert.False(blocked.Ready);
        Assert.Equal(MovieWave4ExportCodes.FinalTakeRequired, blocked.Code);
        Assert.True(ready.Ready);
        Assert.Equal(MovieWave4ExportCodes.ReadyForHandoff, ready.Code);
        Assert.False(string.IsNullOrWhiteSpace(ready.HandoffProvenanceHash));
        Assert.DoesNotContain("provider", ready.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", ready.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", ready.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timeline_validation_blocks_missing_media_and_overlapping_items()
    {
        Assert.Throws<MovieWave4IntegrationValidationException>(() => MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Video, null, 0, 1_000, Guid.NewGuid()),
        ], 1_000));

        Assert.Throws<MovieWave4IntegrationValidationException>(() => MovieWave4ExportSeam.BuildTimeline(
        [
            new(MovieWave4TrackKinds.Voice, Guid.NewGuid(), 0, 1_000),
            new(MovieWave4TrackKinds.Voice, Guid.NewGuid(), 500, 1_000),
        ], 2_000));
    }

    [Fact]
    public void Selected_take_status_is_ready_for_the_existing_upscale_audit_boundary()
    {
        var take = new MovieTake { Id = Guid.NewGuid(), Status = MovieTakeStatuses.Selected };
        var shot = new MovieShot { Id = Guid.NewGuid(), SelectedTakeId = take.Id, FinalTakeId = take.Id };

        var result = MovieTakeUpscaleEligibilityEvaluator.Evaluate(take, shot, MovieUpscaleResolutionCatalog.P4K);

        Assert.True(result.Eligible);
        Assert.True(result.IsSelected);
        Assert.True(result.IsFinal);
        Assert.Equal(MovieTakeUpscaleEligibilityCodes.Eligible, result.Code);
    }
}

using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDirectorEditRepairAudioTests
{
    private static readonly Guid TimelineId = Guid.Parse("11000000-0000-0000-0000-000000000001");
    private static readonly Guid ClipA = Guid.Parse("22000000-0000-0000-0000-000000000001");
    private static readonly Guid ClipB = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid CutId = Guid.Parse("33000000-0000-0000-0000-000000000001");

    [Fact]
    public void Plans_gap_bridge_and_hard_cut_smoothing_from_existing_capabilities()
    {
        var ambienceId = Guid.Parse("44000000-0000-0000-0000-000000000001");
        var sfxId = Guid.Parse("44000000-0000-0000-0000-000000000002");
        var gapTimeline = GapTimeline();
        var plan = MovieDirectorEditRepairAudioPlanner.Plan(gapTimeline, [
            new MovieDirectorAudioCapabilitySnapshot(ambienceId, MovieDirectorAudioBridgeKinds.Ambience, MovieDirectorAudioBridgeSourceKinds.Library, "Harbor air", true, MovieSoundLayers.Environment),
            new MovieDirectorAudioCapabilitySnapshot(sfxId, MovieDirectorAudioBridgeKinds.SoundEffect, MovieDirectorAudioBridgeSourceKinds.SoundTrack, "Transition hush", true, MovieSoundLayers.Foreground),
        ]);

        Assert.Contains(plan.Recommendations, item => item.Kind == MovieDirectorAudioBridgeKinds.Ambience && item.DurationSeconds == 2m && item.SuggestedSourceId == ambienceId);
        var cutPlan = MovieDirectorEditRepairAudioPlanner.Plan(Timeline(), [
            new MovieDirectorAudioCapabilitySnapshot(ambienceId, MovieDirectorAudioBridgeKinds.Ambience, MovieDirectorAudioBridgeSourceKinds.Library, "Harbor air", true, MovieSoundLayers.Environment),
            new MovieDirectorAudioCapabilitySnapshot(sfxId, MovieDirectorAudioBridgeKinds.SoundEffect, MovieDirectorAudioBridgeSourceKinds.SoundTrack, "Transition hush", true, MovieSoundLayers.Foreground),
        ]);
        Assert.Contains(cutPlan.Recommendations, item => item.Kind == MovieDirectorAudioBridgeKinds.SoundEffect && item.RelatedTransitionId == CutId);
        Assert.All(plan.Recommendations, item => Assert.True(item.RequiresUserOverride));
        Assert.Equal(TimelineId, plan.TimelineId);
        Assert.Equal(gapTimeline.Version, plan.TimelineVersion);
    }

    [Fact]
    public void Uses_approved_music_cue_when_no_ambience_is_available_and_reports_missing_material()
    {
        var musicId = Guid.Parse("44000000-0000-0000-0000-000000000003");
        var timeline = GapTimeline();
        var plan = MovieDirectorEditRepairAudioPlanner.Plan(timeline, [
            new MovieDirectorAudioCapabilitySnapshot(musicId, MovieDirectorAudioBridgeKinds.Music, MovieDirectorAudioBridgeSourceKinds.SoundtrackCue, "Quiet score", true, MovieSoundLayers.Background),
        ]);

        Assert.Contains(plan.Recommendations, item => item.Kind == MovieDirectorAudioBridgeKinds.Music && item.StartSeconds == 4m && item.DurationSeconds == 2m);
        Assert.DoesNotContain(plan.UnresolvedNeeds, item => item.Contains("no_approved_ambience_or_music", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disabled_provider_boundary_never_mutates_canonical_timeline_or_calls_generation()
    {
        var plan = MovieDirectorEditRepairAudioPlanner.Plan(Timeline(), []);
        var action = new DirectorAction
        {
            Id = Guid.NewGuid(),
            ActionType = DirectorActionTypes.EditRepairAudio,
            PayloadJson = JsonSerializer.Serialize(new DirectorEditRepairAudioActionPayload(DirectorEditRepairAudioActionTypes.PlanAudioBridges, plan), DirectorJson.Options),
        };
        var executor = new MovieDirectorEditRepairAudioActionExecutor();

        var result = await executor.ExecuteAsync(action, Guid.NewGuid());

        Assert.True(result.Succeeded);
        Assert.Contains("no provider was called", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canonicalTimelineMutated", result.ResultJson, StringComparison.Ordinal);
        Assert.Contains("userOverrideRequired", result.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Capability_mappers_keep_music_and_soundtrack_approval_provider_neutral()
    {
        var cue = new MovieSoundtrackCue
        {
            Id = Guid.NewGuid(),
            Title = "Closing score",
            ApprovalState = MovieSoundtrackApprovalStates.Approved,
            ApprovedVersionId = Guid.NewGuid(),
            TimelineStartSeconds = 3m,
            DurationSeconds = 5m,
        };

        var snapshot = MovieDirectorAudioCapabilityMapper.FromSoundtrackCue(cue);

        Assert.Equal(MovieDirectorAudioBridgeKinds.Music, snapshot.Kind);
        Assert.Equal(MovieDirectorAudioBridgeSourceKinds.SoundtrackCue, snapshot.SourceKind);
        Assert.True(snapshot.IsApproved);
        Assert.Equal(8m, snapshot.EndSeconds);
    }

    private static MovieCanonicalTimelineContract Timeline() => new(
        TimelineId,
        4,
        [
            new MovieTimelineClipContract(ClipA, Guid.NewGuid(), 1, 0m, 4m),
            new MovieTimelineClipContract(ClipB, Guid.NewGuid(), 2, 4m, 4m),
        ],
        [new MovieTimelineTransitionContract(CutId, MovieTimelineTransitionTypes.Cut, ClipA, ClipB, 4m, 0m)]);

    private static MovieCanonicalTimelineContract GapTimeline() => new(
        TimelineId,
        4,
        [
            new MovieTimelineClipContract(ClipA, Guid.NewGuid(), 1, 0m, 4m),
            new MovieTimelineClipContract(ClipB, Guid.NewGuid(), 2, 6m, 4m),
        ],
        []);
}

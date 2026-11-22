using System.Text.Json;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieFinalAssemblyTests
{
    [Fact]
    public void Profiles_are_bounded_and_provider_neutral()
    {
        Assert.True(MovieFinalAssemblyProfiles.TryGet(MovieFinalAssemblyProfiles.Uhd4K, out var profile));
        Assert.Equal(3_840, profile.Width);
        Assert.Equal(2_160, profile.Height);
        Assert.False(MovieFinalAssemblyProfiles.TryGet("vendor-model", out _));
    }

    [Fact]
    public void Ffmpeg_arguments_are_structured_and_do_not_use_shell_interpolation()
    {
        var videoAsset = Guid.NewGuid();
        var audioAsset = Guid.NewGuid();
        var input = Contract(videoAsset, audioAsset, MovieAssemblyCaptionModes.None);
        var videoPath = "/tmp/source; touch /tmp/should-not-run.mp4";
        var audioPath = "/tmp/music $(touch /tmp/should-not-run.wav).wav";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ProbedRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = videoPath,
                [audioAsset] = audioPath,
            }),
            "/tmp/output master.mp4");

        Assert.Contains(videoPath, arguments);
        Assert.Contains(audioPath, arguments);
        Assert.Contains("/tmp/output master.mp4", arguments);
        Assert.Equal(1, arguments.Count(item => item == videoPath));
        Assert.Equal(1, arguments.Count(item => item == audioPath));
        Assert.DoesNotContain(arguments, item => item.Contains("sh -c", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("-filter_complex", arguments);
        Assert.Contains("[audio]", arguments.Single(item => item.Contains("amix=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Burn_in_captions_require_a_source_path_and_are_escaped_inside_the_filter_argument()
    {
        var videoAsset = Guid.NewGuid();
        var captionsAsset = Guid.NewGuid();
        var input = Contract(videoAsset, null, MovieAssemblyCaptionModes.BurnIn) with
        {
            Captions = new MovieAssemblyCaptions(MovieAssemblyCaptionModes.BurnIn, captionsAsset, "en"),
        };

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ProbedRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = "/tmp/source.mp4",
                [captionsAsset] = "/tmp/captions:english's.vtt",
            }),
            "/tmp/output.mp4");

        var filter = arguments[Array.IndexOf(arguments.ToArray(), "-filter_complex") + 1];
        Assert.Contains("subtitles=filename='/tmp/captions\\:english\\'s.vtt'", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_control_accepts_exact_profile_and_rejects_wrong_resolution()
    {
        var input = Contract(Guid.NewGuid(), null, MovieAssemblyCaptionModes.None);
        var qc = new MovieFinalAssemblyQualityControl();
        var accepted = qc.Evaluate(input, Output(input.Profile.Width, input.Profile.Height, input.ExpectedDurationSeconds));
        var rejected = qc.Evaluate(input, Output(1_920, 1_080, input.ExpectedDurationSeconds));

        Assert.True(accepted.Passed);
        Assert.Equal(MovieFinalAssemblyQcStatuses.Passed, accepted.Status);
        Assert.False(rejected.Passed);
        Assert.Contains("MOVIE_ASSEMBLY_QC_RESOLUTION_MISMATCH", rejected.Findings);
    }

    [Fact]
    public void Final_assembly_input_contains_provenance_without_provider_or_model_controls()
    {
        var input = Contract(Guid.NewGuid(), Guid.NewGuid(), MovieAssemblyCaptionModes.Embedded);
        var json = JsonSerializer.Serialize(input);

        Assert.Contains(input.AssemblyId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(input.RequestFingerprint, json, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disabled_executor_never_attempts_execution()
    {
        var executor = new UnavailableMovieFinalAssemblyExecutor();

        Assert.False(executor.IsAvailable);
        await Assert.ThrowsAsync<MovieFinalAssemblyUnavailableException>(() => executor.ExecuteAsync(
            new MovieFinalAssemblyExecutionRequest(Contract(Guid.NewGuid(), null, MovieAssemblyCaptionModes.None), new Dictionary<Guid, string>()),
            new Progress<int>(),
            CancellationToken.None));
    }

    private static MovieFinalAssemblyInput Contract(Guid videoAsset, Guid? audioAsset, string captionsMode)
    {
        var profile = new MovieFinalAssemblyProfile(MovieFinalAssemblyProfiles.Uhd4K, 3_840, 2_160);
        return new(
            "final_assembly",
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile.Key,
            profile,
            [new MovieAssemblyTimelineItem(Guid.NewGuid(), videoAsset, 0, 0m, 2.5m, "continuity-hash")],
            audioAsset.HasValue ? [new MovieAssemblyAudioMixInput(audioAsset.Value, "music", -3m, null, null, true)] : [],
            new MovieAssemblyCaptions(captionsMode, captionsMode == MovieAssemblyCaptionModes.None ? null : Guid.NewGuid(), "en"),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "assembly:test",
            3);
    }

    [Fact]
    public void Ffmpeg_builder_fails_closed_without_per_source_probe_evidence()
    {
        var videoAsset = Guid.NewGuid();
        var input = Contract(videoAsset, null, MovieAssemblyCaptionModes.None);
        var paths = new Dictionary<Guid, string> { [videoAsset] = "/tmp/clip.mp4" };

        // Only the real executor probes each input before rendering, so the builder
        // must never infer that a source carries an audio stream and emit [i:a]
        // blindly. Absent evidence (null table), an empty table and a table that is
        // missing this asset all have to fail closed with the same code.
        foreach (var probes in new IReadOnlyDictionary<Guid, MovieFinalAssemblyVideoProbe>?[]
        {
            null,
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe>(),
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [Guid.NewGuid()] = new(600m, true) },
        })
        {
            var error = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
                FfmpegMovieFinalAssemblyCommandBuilder.Build(
                    new MovieFinalAssemblyExecutionRequest(input, paths, probes), "/tmp/master.mp4"));
            Assert.Equal("ASSEMBLY_SOURCE_PROBE_MISSING", error.Code);
        }
    }

    [Fact]
    public void Silent_source_gets_bounded_synthesised_silence_instead_of_an_audio_pad()
    {
        var videoAsset = Guid.NewGuid();
        var input = Contract(videoAsset, null, MovieAssemblyCaptionModes.None);
        var probes = new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [videoAsset] = new(600m, false) };

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string> { [videoAsset] = "/tmp/clip.mp4" }, probes),
            "/tmp/master.mp4").ToArray();

        var graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];
        // A probed-silent clip must never address a pad that does not exist, and the
        // replacement silence has to be bounded to the clip instead of running free.
        Assert.DoesNotContain("[0:a]", graph);
        Assert.Contains("anullsrc=r=48000:cl=stereo", graph);
        Assert.Contains("atrim=duration=", graph);
    }

    [Fact]
    public void Mixed_timeline_silences_only_the_probed_silent_clip_and_keeps_real_audio()
    {
        var withAudio = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var profile = new MovieFinalAssemblyProfile(MovieFinalAssemblyProfiles.Uhd4K, 3_840, 2_160);
        var input = new MovieFinalAssemblyInput(
            "final_assembly",
            Guid.NewGuid(),
            Guid.NewGuid(),
            profile.Key,
            profile,
            [
                new MovieAssemblyTimelineItem(Guid.NewGuid(), withAudio, 0, 0m, 2.5m, "continuity-hash"),
                new MovieAssemblyTimelineItem(Guid.NewGuid(), silent, 1, 0m, 2.5m, "continuity-hash"),
            ],
            [],
            new MovieAssemblyCaptions(MovieAssemblyCaptionModes.None, null, "en"),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "assembly:mixed-timeline",
            3);
        var probes = new Dictionary<Guid, MovieFinalAssemblyVideoProbe>
        {
            [withAudio] = new(600m, true),
            [silent] = new(600m, false),
        };

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
            {
                [withAudio] = "/tmp/sounded.mp4",
                [silent] = "/tmp/silent.mp4",
            }, probes),
            "/tmp/master.mp4").ToArray();

        var graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];
        Assert.Contains("[0:a]", graph);
        Assert.DoesNotContain("[1:a]", graph);
        Assert.Equal(1, graph.Split("anullsrc").Length - 1);
    }

    // The builder requires per-source probe evidence for every distinct timeline
    // source (MovieFinalAssemblyExecutionRequest.VideoProbes) because that evidence
    // is what keeps [i:a] mapping and duration bounds honest. Builder-contract tests
    // that assert filtergraph shape therefore have to supply it; the fail-closed
    // lanes above deliberately omit it to pin the rejection itself.
    private static MovieFinalAssemblyExecutionRequest ProbedRequest(
        MovieFinalAssemblyInput input,
        IReadOnlyDictionary<Guid, string> paths) =>
        new(input, paths, input.Timeline.Select(item => item.AssetId).Distinct()
            .ToDictionary(id => id, _ => new MovieFinalAssemblyVideoProbe(600m, true)));

    private static MovieFinalAssemblyExecutionResult Output(int width, int height, int duration) => new(
        "master.mp4",
        "video/mp4",
        42,
        _ => Task.FromResult<Stream>(new MemoryStream([0x00, 0x01])),
        new MovieResolution(width, height),
        duration,
        "sha256");
}

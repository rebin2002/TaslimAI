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
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
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
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
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

    private static MovieFinalAssemblyExecutionResult Output(int width, int height, int duration) => new(
        "master.mp4",
        "video/mp4",
        42,
        _ => Task.FromResult<Stream>(new MemoryStream([0x00, 0x01])),
        new MovieResolution(width, height),
        duration,
        "sha256");
}

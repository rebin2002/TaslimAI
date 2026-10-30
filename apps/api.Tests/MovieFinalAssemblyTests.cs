using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    public void Soundtrack_cues_are_trimmed_to_their_duration_and_delayed_to_timeline_start()
    {
        var videoAsset = Guid.NewGuid();
        var audioAsset = Guid.NewGuid();
        var input = Contract(videoAsset, audioAsset, MovieAssemblyCaptionModes.None) with
        {
            ExpectedDurationSeconds = 20,
            AudioMixInputs = [new MovieAssemblyAudioMixInput(audioAsset, "music", -3m, 12.5m, 19m, true)],
        };
        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = "/tmp/clip.mp4",
                [audioAsset] = "/tmp/cue.wav",
            }),
            "/tmp/master.mp4");

        var filter = arguments[Array.IndexOf(arguments.ToArray(), "-filter_complex") + 1];
        Assert.Contains("[1:a]asetpts=PTS-STARTPTS,atrim=duration=6.5,volume=", filter, StringComparison.Ordinal);
        Assert.Contains(",adelay=delays=12500:all=1[mix0]", filter, StringComparison.Ordinal);
        Assert.Contains("[timelineAudio][mix0]amix=inputs=2:duration=first", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpositioned_soundtrack_uses_source_from_start_without_delay()
    {
        var videoAsset = Guid.NewGuid();
        var audioAsset = Guid.NewGuid();
        var input = Contract(videoAsset, audioAsset, MovieAssemblyCaptionModes.None);
        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = "/tmp/clip.mp4",
                [audioAsset] = "/tmp/cue.wav",
            }),
            "/tmp/master.mp4");

        var filter = arguments[Array.IndexOf(arguments.ToArray(), "-filter_complex") + 1];
        Assert.Contains("[1:a]asetpts=PTS-STARTPTS,volume=", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("adelay=", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("atrim=duration=", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Soundtrack_cue_timestamps_are_bounded_before_delay_calculation()
    {
        var invalidBounds = new (decimal? Start, decimal? End)[]
        {
            (decimal.MaxValue, null),
            (86_401m, null),
            (3m, null),
            (null, 4m),
            (null, decimal.MaxValue),
            (8m, 7m),
        };
        foreach (var (start, end) in invalidBounds)
        {
            var videoAsset = Guid.NewGuid();
            var audioAsset = Guid.NewGuid();
            var input = Contract(videoAsset, audioAsset, MovieAssemblyCaptionModes.None) with
            {
                AudioMixInputs = [new MovieAssemblyAudioMixInput(audioAsset, "music", -3m, start, end, true)],
            };
            var exception = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
                FfmpegMovieFinalAssemblyCommandBuilder.Build(
                    new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
                    {
                        [videoAsset] = "/tmp/clip.mp4",
                        [audioAsset] = "/tmp/cue.wav",
                    }),
                    "/tmp/master.mp4"));
            Assert.Equal("ASSEMBLY_AUDIO_MIX_INVALID", exception.Code);
        }
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

    [Theory]
    [InlineData(7_200, 1, false, "MOVIE_ASSEMBLY_QC_DURATION_TRUNCATED")]
    [InlineData(7_200, 7_194, false, "MOVIE_ASSEMBLY_QC_DURATION_TRUNCATED")]
    [InlineData(7_200, 7_195, true, null)]
    [InlineData(7_200, 7_200, true, null)]
    [InlineData(7_200, 7_205, true, null)]
    [InlineData(7_200, 7_206, false, "MOVIE_ASSEMBLY_QC_DURATION_EXCEEDED")]
    public void Quality_control_does_not_publish_truncated_or_oversized_feature_masters(
        int expectedDuration, int measuredDuration, bool shouldPass, string? expectedFinding)
    {
        var input = Contract(Guid.NewGuid(), null, MovieAssemblyCaptionModes.None)
            with { ExpectedDurationSeconds = expectedDuration };
        var output = Output(input.Profile.Width, input.Profile.Height, measuredDuration);

        var result = new MovieFinalAssemblyQualityControl().Evaluate(input, output);

        Assert.Equal(shouldPass, result.Passed);
        Assert.Equal(shouldPass ? MovieFinalAssemblyQcStatuses.Passed : MovieFinalAssemblyQcStatuses.Failed, result.Status);
        if (expectedFinding is not null)
            Assert.Contains(expectedFinding, result.Findings);
        else
            Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void Quality_control_rejects_missing_or_negative_expected_master_runtime(int invalidDuration)
    {
        var input = Contract(Guid.NewGuid(), null, MovieAssemblyCaptionModes.None)
            with { ExpectedDurationSeconds = invalidDuration };
        var output = Output(input.Profile.Width, input.Profile.Height, 1);

        var result = new MovieFinalAssemblyQualityControl().Evaluate(input, output);

        Assert.False(result.Passed);
        Assert.Equal(MovieFinalAssemblyQcStatuses.Failed, result.Status);
        Assert.Contains("MOVIE_ASSEMBLY_QC_EXPECTED_DURATION_INVALID", result.Findings);
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

    [Fact]
    public void Assembly_idempotency_retry_classifier_rejects_unrelated_unique_conflicts()
    {
        var sqliteConflict = new DbUpdateException(
            "assembly idempotency conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieAssemblies.MovieProjectId, MovieAssemblies.IdempotencyKey'."));
        var postgresConflict = new DbUpdateException(
            "assembly PostgreSQL idempotency conflict",
            new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_MovieAssemblies_MovieProjectId_IdempotencyKey\""));
        var otherConflict = new DbUpdateException(
            "other conflict",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: MovieAssemblies.Id'."));

        Assert.True(MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation(sqliteConflict));
        Assert.True(MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation(postgresConflict));
        Assert.False(MovieFinalAssemblyService.IsAssemblyIdempotencyUniqueViolation(otherConflict));
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

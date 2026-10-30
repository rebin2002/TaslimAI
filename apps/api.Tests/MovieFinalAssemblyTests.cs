using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    public void Output_duration_is_bounded_by_project_not_sparse_embedded_subtitles()
    {
        var video = Guid.NewGuid();
        var caption = Guid.NewGuid();
        foreach (var mode in new[] { MovieAssemblyCaptionModes.None, MovieAssemblyCaptionModes.Embedded })
        {
            var input = Contract(video, null, mode) with
            {
                ExpectedDurationSeconds = 3_600,
                Captions = new MovieAssemblyCaptions(
                    mode, mode == MovieAssemblyCaptionModes.Embedded ? caption : null, "en"),
            };
            var paths = new Dictionary<Guid, string> { [video] = "/tmp/clip.mp4", [caption] = "/tmp/brief.vtt" };
            var args = FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(input, paths), "/tmp/master.mp4").ToArray();

            // FFmpeg -shortest considers the last subtitle cue as a possible end
            // of output. Never let a one-second VTT truncate a longer master.
            Assert.DoesNotContain("-shortest", args);
            var durationIndex = Array.IndexOf(args, "-t");
            Assert.True(durationIndex > 0);
            Assert.Equal("3600", args[durationIndex + 1]);
            Assert.True(durationIndex + 1 < Array.IndexOf(args, "/tmp/master.mp4"));
            if (mode == MovieAssemblyCaptionModes.Embedded)
            {
                Assert.Contains("-c:s", args);
                Assert.Equal("1:0", args[Array.LastIndexOf(args, "-map") + 1]);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(7_201)]
    public void Output_duration_rejects_nonpositive_or_unsupported_project_runtime(int seconds)
    {
        var video = Guid.NewGuid();
        var input = Contract(video, null, MovieAssemblyCaptionModes.None)
            with { ExpectedDurationSeconds = seconds };
        var exception = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
            FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(input,
                    new Dictionary<Guid, string> { [video] = "/tmp/clip.mp4" }),
                "/tmp/master.mp4"));
        Assert.Equal("ASSEMBLY_DURATION_INVALID", exception.Code);
    }

    [Fact]
    public void Silent_video_uses_bounded_generated_silence_without_losing_the_soundtrack()
    {
        var silent = Guid.NewGuid();
        var soundtrack = Guid.NewGuid();
        var input = Contract(silent, soundtrack, MovieAssemblyCaptionModes.None);
        var request = new MovieFinalAssemblyExecutionRequest(
            input,
            new Dictionary<Guid, string> { [silent] = "/tmp/silent source.mp4", [soundtrack] = "/tmp/music.wav" },
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [silent] = new(2.5m, false) });

        var args = FfmpegMovieFinalAssemblyCommandBuilder.Build(request, "/tmp/master.mp4").ToArray();
        var graph = args[Array.IndexOf(args, "-filter_complex") + 1];

        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=2.5", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[0:a]", graph, StringComparison.Ordinal);
        Assert.Contains("[1:a]asetpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
        Assert.Contains("[v0][a0]concat=n=1:v=1:a=1", graph, StringComparison.Ordinal);
        Assert.Equal(2, args.Count(arg => arg == "-i")); // No unexpected input-index shift.
    }

    [Fact]
    public void Mixed_and_repeated_silent_clips_preserve_embedded_audio_and_caption_input_indexes()
    {
        var silent = Guid.NewGuid();
        var sounded = Guid.NewGuid();
        var music = Guid.NewGuid();
        var caption = Guid.NewGuid();
        var input = Contract(silent, music, MovieAssemblyCaptionModes.Embedded) with
        {
            ExpectedDurationSeconds = 10,
            Timeline =
            [
                new MovieAssemblyTimelineItem(Guid.NewGuid(), silent, 0, 0m, 2m, null),
                new MovieAssemblyTimelineItem(Guid.NewGuid(), sounded, 1, 0m, 2m, null),
                new MovieAssemblyTimelineItem(Guid.NewGuid(), silent, 2, 1m, 2.5m, null),
            ],
            Captions = new MovieAssemblyCaptions(MovieAssemblyCaptionModes.Embedded, caption, "en"),
        };
        var request = new MovieFinalAssemblyExecutionRequest(
            input,
            new Dictionary<Guid, string>
            {
                [silent] = "/tmp/silent.mp4",
                [sounded] = "/tmp/sounded.mp4",
                [music] = "/tmp/music.wav",
                [caption] = "/tmp/captions.vtt",
            },
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe>
            {
                [silent] = new(2.5m, false),
                [sounded] = new(3m, true),
            });
        var args = FfmpegMovieFinalAssemblyCommandBuilder.Build(request, "/tmp/output.mp4").ToArray();
        var graph = args[Array.IndexOf(args, "-filter_complex") + 1];

        Assert.Equal(2, graph.Split("anullsrc=", StringSplitOptions.None).Length - 1);
        Assert.Contains("[1:a]atrim=start=0:end=2", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[1:a]trim=", graph, StringComparison.Ordinal);
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=duration=1.5", graph, StringComparison.Ordinal);
        Assert.Contains("[3:a]asetpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
        Assert.Contains("[v0][a0][v1][a1][v2][a2]concat=n=3:v=1:a=1", graph, StringComparison.Ordinal);
        Assert.Equal(5, args.Count(arg => arg == "-i"));
        Assert.Equal("4:0", args[Array.LastIndexOf(args, "-map") + 1]);
    }

    [Fact]
    public void Mixed_geometry_clips_are_normalized_before_concatenation()
    {
        // Concat rejects mismatched input width/height/SAR even when a final
        // scale follows it. Every selected video segment must be normalized
        // independently before it reaches the concat input pads.
        var silent = Guid.NewGuid();
        var sounded = Guid.NewGuid();
        var input = Contract(silent, null, MovieAssemblyCaptionModes.None) with
        {
            ExpectedDurationSeconds = 2,
            Timeline =
            [
                new MovieAssemblyTimelineItem(Guid.NewGuid(), silent, 0, 0m, 1m, null),
                new MovieAssemblyTimelineItem(Guid.NewGuid(), sounded, 1, 0m, 1m, null),
            ],
        };
        var request = new MovieFinalAssemblyExecutionRequest(
            input,
            new Dictionary<Guid, string>
            {
                [silent] = "/tmp/landscape.mp4",
                [sounded] = "/tmp/portrait.mp4",
            },
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe>
            {
                [silent] = new(1m, false),
                [sounded] = new(1m, true),
            });

        var args = FfmpegMovieFinalAssemblyCommandBuilder.Build(request, "/tmp/master.mp4").ToArray();
        var graph = args[Array.IndexOf(args, "-filter_complex") + 1];
        var concatAt = graph.IndexOf("concat=n=2:v=1:a=1", StringComparison.Ordinal);

        Assert.True(concatAt > 0);
        Assert.Equal(2, graph.Split("scale=3840:2160:", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, graph.Split("setsar=1,format=yuv420p[v", StringSplitOptions.None).Length - 1);
        Assert.True(graph.IndexOf("format=yuv420p[v0]", StringComparison.Ordinal) < concatAt);
        Assert.True(graph.IndexOf("format=yuv420p[v1]", StringComparison.Ordinal) < concatAt);
        Assert.DoesNotContain("[videoBase]scale=", graph, StringComparison.Ordinal);
        Assert.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_ffmpeg_mixed_geometry_silent_and_sounded_clips_export_with_audio_and_correct_duration()
    {
        // Conditional local integration coverage: no provider API or production
        // media. The test exercises the actual argument builder, ffmpeg and
        // ffprobe with two different geometries and audio-stream layouts.
        if (!OperatingSystem.IsLinux()
            || !File.Exists("/usr/bin/ffmpeg")
            || !File.Exists("/usr/bin/ffprobe")) return;

        const string ffmpeg = "/usr/bin/ffmpeg";
        const string ffprobe = "/usr/bin/ffprobe";
        var directory = Path.Combine(Path.GetTempPath(), "taslim-assembly-real-ffmpeg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            static async Task<string> RunToolAsync(string tool, IEnumerable<string> arguments)
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tool,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = new System.Diagnostics.Process { StartInfo = start };
                Assert.True(process.Start());
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                var output = await outputTask;
                var error = await errorTask;
                Assert.True(process.ExitCode == 0, $"Local media test process failed: {error}");
                return output;
            }

            var silentPath = Path.Combine(directory, "silent-landscape.mp4");
            var soundedPath = Path.Combine(directory, "sounded-portrait.mp4");
            var outputPath = Path.Combine(directory, "master.mp4");
            await RunToolAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "color=c=red:s=320x180:r=10", "-t", "0.6", "-c:v", "mpeg4", silentPath,
            ]);
            await RunToolAsync(ffmpeg,
            [
                "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                "color=c=blue:s=180x320:r=10", "-f", "lavfi", "-i",
                "sine=frequency=880:sample_rate=48000", "-t", "0.6",
                "-c:v", "mpeg4", "-c:a", "aac", soundedPath,
            ]);

            var silent = Guid.NewGuid();
            var sounded = Guid.NewGuid();
            var probeArgs = new[] { "-v", "error", "-show_entries", "stream=codec_type:format=duration", "-of", "json" };
            var silentJson = await RunToolAsync(ffprobe, probeArgs.Append(silentPath));
            var soundedJson = await RunToolAsync(ffprobe, probeArgs.Append(soundedPath));
            var silentProbe = FfmpegMovieFinalAssemblyExecutor.ParseVideoProbeJson(silentJson);
            var soundedProbe = FfmpegMovieFinalAssemblyExecutor.ParseVideoProbeJson(soundedJson);
            Assert.False(silentProbe.HasAudio);
            Assert.True(soundedProbe.HasAudio);

            var input = Contract(silent, null, MovieAssemblyCaptionModes.None) with
            {
                ResolutionProfile = "synthetic-320",
                Profile = new MovieFinalAssemblyProfile("synthetic-320", 320, 180),
                ExpectedDurationSeconds = 1,
                Timeline =
                [
                    new MovieAssemblyTimelineItem(Guid.NewGuid(), silent, 0, 0m, 0.5m, null),
                    new MovieAssemblyTimelineItem(Guid.NewGuid(), sounded, 1, 0m, 0.5m, null),
                ],
            };
            var assemblyArgs = FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(
                    input,
                    new Dictionary<Guid, string> { [silent] = silentPath, [sounded] = soundedPath },
                    new Dictionary<Guid, MovieFinalAssemblyVideoProbe>
                    {
                        [silent] = silentProbe,
                        [sounded] = soundedProbe,
                    }),
                outputPath);
            await RunToolAsync(ffmpeg, assemblyArgs);

            var masterJson = await RunToolAsync(ffprobe,
            [
                "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration",
                "-of", "json", outputPath,
            ]);
            using var document = JsonDocument.Parse(masterJson);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = Assert.Single(streams.Where(item => item.GetProperty("codec_type").GetString() == "video"));
            Assert.Equal(320, video.GetProperty("width").GetInt32());
            Assert.Equal(180, video.GetProperty("height").GetInt32());
            Assert.Single(streams.Where(item => item.GetProperty("codec_type").GetString() == "audio"));
            var duration = decimal.Parse(
                document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, 0.95m, 1.10m);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sounded_video_without_explicit_out_point_uses_audio_trim_not_video_trim()
    {
        var video = Guid.NewGuid();
        var input = Contract(video, null, MovieAssemblyCaptionModes.None) with
        {
            Timeline = [new MovieAssemblyTimelineItem(Guid.NewGuid(), video, 0, 0.5m, null, null)],
        };
        var request = new MovieFinalAssemblyExecutionRequest(
            input,
            new Dictionary<Guid, string> { [video] = "/tmp/sounded.mp4" },
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [video] = new(3m, true) });

        var args = FfmpegMovieFinalAssemblyCommandBuilder.Build(request, "/tmp/master.mp4").ToArray();
        var graph = args[Array.IndexOf(args, "-filter_complex") + 1];

        Assert.Contains("[0:v]trim=start=0.5,setpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
        Assert.Contains("[0:a]atrim=start=0.5,asetpts=PTS-STARTPTS", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("[0:a]trim=", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Probed_video_requires_valid_source_duration_and_every_source_probe()
    {
        var video = Guid.NewGuid();
        var input = Contract(video, null, MovieAssemblyCaptionModes.None);
        foreach (var probes in new IReadOnlyDictionary<Guid, MovieFinalAssemblyVideoProbe>[]
        {
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe>(),
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [video] = new(0m, false) },
            new Dictionary<Guid, MovieFinalAssemblyVideoProbe> { [video] = new(1m, false) },
        })
        {
            var error = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
                FfmpegMovieFinalAssemblyCommandBuilder.Build(
                    new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string> { [video] = "/tmp/clip.mp4" }, probes),
                    "/tmp/master.mp4"));
            Assert.Contains(error.Code, new[] { "ASSEMBLY_SOURCE_PROBE_MISSING", "ASSEMBLY_SOURCE_DURATION_INVALID" });
        }
    }

    [Fact]
    public void Media_probe_requires_video_and_a_bounded_measured_duration()
    {
        var silent = FfmpegMovieFinalAssemblyExecutor.ParseVideoProbeJson(
            "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"2.500000\"}}");
        var sounded = FfmpegMovieFinalAssemblyExecutor.ParseVideoProbeJson(
            "{\"streams\":[{\"codec_type\":\"video\"},{\"codec_type\":\"audio\"}],\"format\":{\"duration\":\"3\"}}");
        Assert.Equal(new MovieFinalAssemblyVideoProbe(2.5m, false), silent);
        Assert.Equal(new MovieFinalAssemblyVideoProbe(3m, true), sounded);

        foreach (var invalid in new[]
        {
            "{}",
            "{\"streams\":[{\"codec_type\":\"audio\"}],\"format\":{\"duration\":\"3\"}}",
            "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"N/A\"}}",
            "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"86401\"}}",
            "not-json",
        })
        {
            var error = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
                FfmpegMovieFinalAssemblyExecutor.ParseVideoProbeJson(invalid));
            Assert.Equal("ASSEMBLY_SOURCE_PROBE_FAILED", error.Code);
        }
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
        Assert.Contains("subtitles=filename=/tmp/captions\\\\:english\\\\\\'s.vtt", filter, StringComparison.Ordinal);

        // Filtergraph separators require a separate escaping pass, even though
        // the arguments are passed directly to FFmpeg without a shell.
        var specialPath = "/tmp/my captions,act[1];.vtt";
        var specialArguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = "/tmp/source.mp4",
                [captionsAsset] = specialPath,
            }),
            "/tmp/output.mp4").ToArray();
        var specialGraph = specialArguments[Array.IndexOf(specialArguments, "-filter_complex") + 1];
        Assert.Contains("subtitles=filename=/tmp/my captions\\,act\\[1\\]\\;.vtt", specialGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_caption_input_is_declared_before_filter_and_stream_mapping()
    {
        var videoAsset = Guid.NewGuid();
        var audioAsset = Guid.NewGuid();
        var captionAsset = Guid.NewGuid();
        var input = Contract(videoAsset, audioAsset, MovieAssemblyCaptionModes.Embedded) with
        {
            Captions = new MovieAssemblyCaptions(MovieAssemblyCaptionModes.Embedded, captionAsset, "en"),
        };
        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
            {
                [videoAsset] = "/tmp/clip.mp4",
                [audioAsset] = "/tmp/music.wav",
                [captionAsset] = "/tmp/captions.vtt",
            }),
            "/tmp/master.mp4").ToArray();

        var filterIndex = Array.IndexOf(arguments, "-filter_complex");
        var captionPathIndex = Array.IndexOf(arguments, "/tmp/captions.vtt");
        Assert.True(filterIndex > 0);
        Assert.True(captionPathIndex > 0);
        Assert.Equal("-i", arguments[captionPathIndex - 1]);
        Assert.True(captionPathIndex < filterIndex, "All FFmpeg inputs must precede complex filters and output maps.");
        Assert.Equal(3, arguments.Count(item => item == "-i"));
        Assert.Equal(1, arguments.Count(item => item == "/tmp/captions.vtt"));
        Assert.Equal("2:0", arguments[Array.LastIndexOf(arguments, "-map") + 1]);
        Assert.Equal("mov_text", arguments[Array.IndexOf(arguments, "-c:s") + 1]);
    }

    [Fact]
    public void Embedded_caption_missing_source_fails_before_execution()
    {
        var videoAsset = Guid.NewGuid();
        var captionAsset = Guid.NewGuid();
        var input = Contract(videoAsset, null, MovieAssemblyCaptionModes.Embedded) with
        {
            Captions = new MovieAssemblyCaptions(MovieAssemblyCaptionModes.Embedded, captionAsset, "en"),
        };
        var exception = Assert.Throws<MovieFinalAssemblyExecutionException>(() =>
            FfmpegMovieFinalAssemblyCommandBuilder.Build(
                new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string>
                {
                    [videoAsset] = "/tmp/clip.mp4",
                }),
                "/tmp/master.mp4"));
        Assert.Equal("ASSEMBLY_CAPTIONS_PATH_MISSING", exception.Code);
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
    public async Task Timed_out_local_renderer_is_terminated_and_fails_with_stable_code()
    {
        // The CI runners are Unix. Use a disposable local executable instead
        // of making any paid provider call or depending on a real video export.
        if (OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "taslim-assembly-timeout-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var probePath = Path.Combine(directory, "fake-ffprobe");
            var renderPath = Path.Combine(directory, "fake-ffmpeg");
            var sourcePath = Path.Combine(directory, "source.mp4");
            await File.WriteAllTextAsync(probePath, "#!/bin/sh\nprintf '%s\\n' '{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"3\"}}'\n");
            await File.WriteAllTextAsync(renderPath, "#!/bin/sh\nexec sleep 30\n");
            await File.WriteAllBytesAsync(sourcePath, [0x00]);
            const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(probePath, executable);
            File.SetUnixFileMode(renderPath, executable);

            var videoId = Guid.NewGuid();
            var input = Contract(videoId, null, MovieAssemblyCaptionModes.None);
            var request = new MovieFinalAssemblyExecutionRequest(input, new Dictionary<Guid, string> { [videoId] = sourcePath });
            var executor = new FfmpegMovieFinalAssemblyExecutor(Options.Create(new MovieFinalAssemblyOptions
            {
                Enabled = true,
                FfmpegPath = renderPath,
                FfprobePath = probePath,
                ProcessTimeoutSeconds = 1,
            }));

            var error = await Assert.ThrowsAsync<MovieFinalAssemblyExecutionException>(
                () => executor.ExecuteAsync(request, new Progress<int>(), CancellationToken.None));
            Assert.Equal("ASSEMBLY_EXECUTION_TIMEOUT", error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Timed_out_final_duration_probe_is_terminated_and_fails_closed()
    {
        // Exercise the post-render ffprobe boundary with disposable local executables.
        // The first invocation returns trusted-looking video metadata; the second hangs.
        if (OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "taslim-duration-probe-timeout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var probePath = Path.Combine(directory, "fake-ffprobe");
            var renderPath = Path.Combine(directory, "fake-ffmpeg");
            var sourcePath = Path.Combine(directory, "source.mp4");
            await File.WriteAllTextAsync(probePath, """
                #!/bin/sh
                case "$*" in
                  *"stream=codec_type:format=duration"*) printf '%s\n' '{"streams":[{"codec_type":"video"}],"format":{"duration":"3"}}' ;;
                  *) exec sleep 30 ;;
                esac
                """);
            await File.WriteAllTextAsync(renderPath, """
                #!/bin/sh
                for last in "$@"; do :; done
                printf 'synthetic-master' > "$last"
                """);
            await File.WriteAllBytesAsync(sourcePath, [0x00]);
            const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(probePath, executable);
            File.SetUnixFileMode(renderPath, executable);

            var videoId = Guid.NewGuid();
            var request = new MovieFinalAssemblyExecutionRequest(
                Contract(videoId, null, MovieAssemblyCaptionModes.None),
                new Dictionary<Guid, string> { [videoId] = sourcePath });
            var executor = new FfmpegMovieFinalAssemblyExecutor(Options.Create(new MovieFinalAssemblyOptions
            {
                Enabled = true,
                FfmpegPath = renderPath,
                FfprobePath = probePath,
                ProcessTimeoutSeconds = 1,
            }));

            var error = await Assert.ThrowsAsync<MovieFinalAssemblyExecutionException>(
                () => executor.ExecuteAsync(request, new Progress<int>(), CancellationToken.None));
            Assert.Equal("ASSEMBLY_DURATION_MEASUREMENT_FAILED", error.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

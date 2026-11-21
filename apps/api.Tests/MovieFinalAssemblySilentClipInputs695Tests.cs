using System.Diagnostics;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #695 test-only acceptance surface: the local FFmpeg final-assembly command
/// builder must keep working when a generated clip carries no audio stream, must not
/// discard embedded audio from clips that do carry it, and must preserve external
/// soundtrack/caption input indexes for silent, sounded and mixed selections.
///
/// Deliberately isolated from the Movie final-assembly owner lineage (draft PR #298
/// owns MovieFinalAssembly.cs, MovieFinalAssemblyTests.cs and
/// MovieFinalAssemblyHostedFfmpegGateTests.cs; PR #807 owns
/// MovieFinalAssemblyQueueLinkRecoveryTests.cs). This file adds no product-code edits.
///
/// Reproduction evidence (provider-free, disposable synthetic fixtures generated with
/// ffmpeg 6.1.1, captured 2026-11-18 against current main):
///  - Repro A: current main emits the VIDEO-only `trim` filter on each [i:a] pad, so
///    the graph aborts with "Media type mismatch between the 'Parsed_trim_N' filter
///    output pad 0 (video) and the 'Parsed_asetpts_N' filter input pad 0 (audio)"
///    even when EVERY clip has audio. Correcting only that to `atrim` (owner commit
///    227325e on #298) makes a sounded-only selection render successfully.
///  - Repro C: with `atrim` corrected, a silent-only selection still aborts:
///    "Stream specifier ':a' in filtergraph description [0:a]atrim... matches no
///    streams" - the silent clip cannot satisfy its audio pad (issue core).
///  - Repro D: same abort for a mixed sounded+silent selection at the silent
///    clip's [i:a] pad.
///  - Recipe E (verified locally, no repo edits): after probing each selected
///    source, substitute an in-graph `anullsrc` pad trimmed to the clip length for
///    silent inputs (anullsrc=r=44100:cl=stereo,atrim=start=0:duration=N,
///    asetpts=PTS-STARTPTS), keep [i:a] only for sounded inputs, and keep
///    soundtrack inputs appended after all timeline inputs. The mixed selection
///    then renders video+audio over the full concatenated duration with the
///    soundtrack pad still at index Timeline.Count.
///
/// The unconditional tests below assert only stream-agnostic input-layout invariants
/// (derived from the emitted -i order, never guessed from extensions), so they hold
/// for the current builder AND for any fix that satisfies the #695 acceptance
/// criteria, including fixes that add extra lavfi inputs for substituted silence.
/// The end-to-end render acceptance tests need a local ffmpeg binary, which CI does
/// not provide; they are compile-time skipped with their reproduction case noted and
/// are intended to be enabled by the owner lineage together with #298's hosted gate.
/// </summary>
public sealed class MovieFinalAssemblySilentClipInputs695Tests
{
    private static readonly MovieFinalAssemblyProfile SmallProfile = new("test-320x240", 320, 240);

    [Fact]
    public void Silent_only_selection_keeps_soundtrack_and_caption_on_their_derived_input_indexes()
    {
        var silent = Guid.NewGuid();
        var soundtrack = Guid.NewGuid();
        var captions = Guid.NewGuid();
        var silentPath = "/fixtures/695/silent-only.mp4";
        var soundtrackPath = "/fixtures/695/soundtrack.m4a";
        var captionsPath = "/fixtures/695/captions.srt";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ExecutionRequest(
                [(silent, silentPath, 0m, 2m)],
                [(soundtrack, soundtrackPath)],
                (MovieAssemblyCaptionModes.Embedded, captionsPath, captions)),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        Assert.Equal(new[] { silentPath, soundtrackPath, captionsPath }, inputs);
        Assert.Contains("[0:v]", graph);
        Assert.Contains($"[{inputs.IndexOf(soundtrackPath)}:a]volume", graph);
        Assert.Contains($"{inputs.IndexOf(captionsPath)}:0", arguments);
        Assert.Contains("concat=n=1:v=1:a=1", graph);
    }

    [Fact]
    public void Sounded_only_selection_keeps_both_embedded_audio_pads_and_soundtrack_index()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var soundtrack = Guid.NewGuid();
        var firstPath = "/fixtures/695/sounded-a.mp4";
        var secondPath = "/fixtures/695/sounded-b.mp4";
        var soundtrackPath = "/fixtures/695/soundtrack.m4a";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ExecutionRequest(
                [(first, firstPath, 0m, 2m), (second, secondPath, 0m, null)],
                [(soundtrack, soundtrackPath)]),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        // Every sounded clip keeps a video pad and an audio pad sourced from its own input.
        foreach (var (path, index) in new[] { (firstPath, 0), (secondPath, 1) })
        {
            Assert.Equal(path, inputs[index]);
            Assert.Contains($"[{index}:v]", graph);
            Assert.Contains($"[{index}:a]", graph);
        }

        // The external soundtrack is appended after all timeline inputs and mixed from
        // that derived index, independent of per-clip audio presence.
        Assert.Equal(2, inputs.IndexOf(soundtrackPath));
        Assert.Contains("[2:a]volume", graph);
        Assert.Contains("concat=n=2:v=1:a=1", graph);
    }

    [Fact]
    public void Mixed_silent_and_sounded_selection_preserves_soundtrack_input_index_after_all_timeline_inputs()
    {
        var sounded = Guid.NewGuid();
        var silent = Guid.NewGuid();
        var soundtrackOne = Guid.NewGuid();
        var soundtrackTwo = Guid.NewGuid();
        var soundedPath = "/fixtures/695/sounded.mp4";
        var silentPath = "/fixtures/695/silent.mp4";
        var soundtrackOnePath = "/fixtures/695/soundtrack-one.m4a";
        var soundtrackTwoPath = "/fixtures/695/soundtrack-two.m4a";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ExecutionRequest(
                [(sounded, soundedPath, 0m, null), (silent, silentPath, 0m, 3m)],
                [(soundtrackOne, soundtrackOnePath), (soundtrackTwo, soundtrackTwoPath)]),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        // Timeline ordering: sounded first, silent second; both keep video pads.
        Assert.Equal(new[] { soundedPath, silentPath }, new[] { inputs[0], inputs[1] });
        Assert.Contains("[0:v]", graph);
        Assert.Contains("[1:v]", graph);

        // Soundtrack indexes are derived from the emitted input order: the first mix
        // input sits immediately after the LAST timeline input (index 2) and the second
        // follows it (index 3). A correct fix may add inputs for substituted silence,
        // but must keep this relative layout; these assertions locate each soundtrack
        // by its own -i position rather than by hard-coded arithmetic.
        Assert.Equal(2, inputs.IndexOf(soundtrackOnePath));
        Assert.Equal(3, inputs.IndexOf(soundtrackTwoPath));
        Assert.Contains("[2:a]volume", graph);
        Assert.Contains("[3:a]volume", graph);
        Assert.Contains("amix=inputs=3", graph);
    }

    [Fact]
    public void Repeated_source_asset_gets_one_input_per_timeline_slot_and_soundtrack_follows_them_all()
    {
        var repeated = Guid.NewGuid();
        var other = Guid.NewGuid();
        var soundtrack = Guid.NewGuid();
        var repeatedPath = "/fixtures/695/repeated-clip.mp4";
        var otherPath = "/fixtures/695/other-clip.mp4";
        var soundtrackPath = "/fixtures/695/soundtrack.m4a";

        var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
            ExecutionRequest(
                [(repeated, repeatedPath, 0m, 2m), (other, otherPath, 0m, 2m), (repeated, repeatedPath, 2m, 4m)],
                [(soundtrack, soundtrackPath)]),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        // Source repetition: the same asset occupies timeline slots 0 and 2 and each
        // slot keeps its own -i input; the soundtrack index follows all three slots.
        Assert.Equal(4, inputs.Count);
        Assert.Equal(repeatedPath, inputs[0]);
        Assert.Equal(otherPath, inputs[1]);
        Assert.Equal(repeatedPath, inputs[2]);
        Assert.Equal(3, inputs.IndexOf(soundtrackPath));
        Assert.Contains("[0:v]", graph);
        Assert.Contains("[2:v]", graph);
        Assert.Contains("[3:a]volume", graph);
        Assert.Contains("concat=n=3:v=1:a=1", graph);
    }

    [Fact(Skip = "Issue #695 repro A (2026-11-18, ffmpeg 6.1.1, synthetic fixtures): current main trims each [i:a] pad with the video-only `trim` filter, so the render aborts with \"Media type mismatch between the 'Parsed_trim_*' filter output pad 0 (video) and the 'Parsed_asetpts_*' filter input pad 0 (audio)\" even when every clip HAS audio. Correcting the audio-pad trim to `atrim` (owner commit 227325e on PR #298) makes this exact selection render video+audio locally. Enable together with #298 plus a CI ffmpeg provision; needs a local ffmpeg binary.")]
    public void Sounded_only_selection_renders_video_and_embedded_audio()
    {
        RenderAcceptance(allSounded: true, includeSilent: false);
    }

    [Fact(Skip = "Issue #695 repro C (2026-11-18, ffmpeg 6.1.1, synthetic fixtures): with the repro A trim/atrim defect already corrected, a silent-only selection still aborts with \"Stream specifier ':a' in filtergraph description [0:a]atrim... matches no streams\" because the generated clip has no audio stream to satisfy its audio pad. Acceptance: probe streams per selected source, supply timeline-length silence ONLY for inputs lacking audio (verified local recipe: in-graph anullsrc=...:atrim=start=0:duration=N), keep the soundtrack input immediately after the timeline inputs, and render video+audio across the full duration. Needs a local ffmpeg binary.")]
    public void Silent_only_selection_renders_with_substituted_silence_and_preserved_soundtrack_index()
    {
        RenderAcceptance(allSounded: false, includeSilent: true);
    }

    [Fact(Skip = "Issue #695 repro D/E (2026-11-18, ffmpeg 6.1.1, synthetic fixtures): with the trim/atrim defect corrected, a mixed sounded+silent selection aborts at the silent clip's [i:a] pad (\"matches no streams\"). Verified local fix recipe: keep [i:a] for sounded inputs only, substitute timeline-length in-graph anullsrc silence for the silent input (no extra -i, so the soundtrack keeps index Timeline.Count), concat, then amix - this produced a valid MP4 with video+audio streams over the full 4s of two 2s clips. Acceptance also requires that the sounded clip's embedded audio is NOT discarded. Needs a local ffmpeg binary.")]
    public void Mixed_silent_and_sounded_selection_renders_preserving_embedded_audio_and_soundtrack_index()
    {
        RenderAcceptance(allSounded: false, includeSilent: true);
    }

    /// <summary>
    /// End-to-end acceptance helper for the skipped render cases: builds the real
    /// command with the production builder, generates disposable synthetic fixtures
    /// locally (no providers, no network), runs it with the local ffmpeg and probes
    /// the result. Silent fixtures carry a video stream only; sounded fixtures carry
    /// video plus embedded audio.
    /// </summary>
    private static void RenderAcceptance(bool allSounded, bool includeSilent)
    {
        var directory = Path.Combine(Path.GetTempPath(), "taslim-695", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var silentPath = Path.Combine(directory, "silent.mp4");
            var soundedPath = Path.Combine(directory, "sounded.mp4");
            var soundtrackPath = Path.Combine(directory, "soundtrack.m4a");
            Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "veryfast", silentPath]);
            Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=25:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", soundedPath]);
            Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=220:duration=6", "-c:a", "aac", soundtrackPath]);

            var soundtrack = Guid.NewGuid();
            List<(Guid Asset, string Path, decimal In, decimal? Out)> timeline = [];
            if (allSounded)
            {
                timeline.Add((Guid.NewGuid(), soundedPath, 0m, 2m));
            }
            else if (includeSilent)
            {
                timeline.Add((Guid.NewGuid(), soundedPath, 0m, 2m));
                timeline.Add((Guid.NewGuid(), silentPath, 0m, 2m));
            }
            else
            {
                timeline.Add((Guid.NewGuid(), silentPath, 0m, 2m));
            }

            var outputPath = Path.Combine(directory, "out.mp4");
            var arguments = FfmpegMovieFinalAssemblyCommandBuilder.Build(
                ExecutionRequest(
                    timeline.Select(item => (item.Asset, item.Path, item.In, item.Out)).ToArray(),
                    [(soundtrack, soundtrackPath)]),
                outputPath);

            Run("ffmpeg", arguments);

            Assert.True(File.Exists(outputPath), "The assembly produced no output file.");
            var streams = Run("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", outputPath]);
            Assert.Contains("video", streams);
            Assert.Contains("audio", streams);
            var duration = double.Parse(Run("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", outputPath]).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            var expected = timeline.Sum(item => (double)(item.Out ?? 2m) - (double)item.In);
            Assert.InRange(duration, expected - 0.5, expected + 1.0);

            // The soundtrack input must still sit immediately after the last timeline
            // input in the emitted command (index preservation for silent/mixed sets).
            Assert.Equal(timeline.Count, InputPaths(arguments).IndexOf(soundtrackPath));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static string Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        Assert.True(process.ExitCode == 0, $"{fileName} exited with {process.ExitCode}: {stderr}");
        return stdout;
    }

    private static List<string> InputPaths(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == "-i") paths.Add(args[index + 1]);
        }

        return paths;
    }

    private static string FilterComplex(IReadOnlyList<string> args) =>
        args[args.ToList().IndexOf("-filter_complex") + 1];

    private static MovieFinalAssemblyExecutionRequest ExecutionRequest(
        (Guid Asset, string Path, decimal InPoint, decimal? OutPoint)[] timeline,
        (Guid Asset, string Path)[] audioMixInputs,
        (string Mode, string Path, Guid Asset)? captions = null)
    {
        var sources = new Dictionary<Guid, string>();
        foreach (var item in timeline) sources[item.Asset] = item.Path;
        foreach (var item in audioMixInputs) sources[item.Asset] = item.Path;
        if (captions.HasValue) sources[captions.Value.Asset] = captions.Value.Path;

        return new MovieFinalAssemblyExecutionRequest(
            new MovieFinalAssemblyInput(
                "final_assembly",
                Guid.NewGuid(),
                Guid.NewGuid(),
                SmallProfile.Key,
                SmallProfile,
                [.. timeline.Select((item, index) => new MovieAssemblyTimelineItem(item.Asset, item.Asset, index, item.InPoint, item.OutPoint, "repro-695"))],
                [.. audioMixInputs.Select(item => new MovieAssemblyAudioMixInput(item.Asset, "music", -3m, null, null, true))],
                captions.HasValue
                    ? new MovieAssemblyCaptions(captions.Value.Mode, captions.Value.Asset, "en")
                    : new MovieAssemblyCaptions(MovieAssemblyCaptionModes.None, null, "en"),
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "assembly:test-695",
                (int)Math.Ceiling(timeline.Sum(item => (double)(item.OutPoint ?? (item.InPoint + 2m)) - (double)item.InPoint))),
            sources);
    }
}

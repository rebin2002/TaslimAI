using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>Issue #695. Verify emitted timeline, soundtrack and caption inputs, then render
/// disposable fixtures through the production executor to measure the audio preserved in each slot.</summary>
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
                (MovieAssemblyCaptionModes.Embedded, captionsPath, captions),
                videoProbes: new() { [silent] = new(2m, false) }),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        Assert.Equal(new[] { silentPath, soundtrackPath, captionsPath }, inputs);
        Assert.Contains("[0:v]", graph);
        Assert.DoesNotContain("[0:a]", graph);
        AssertSoundtrackPad(graph, inputs.IndexOf(soundtrackPath), 0);
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
                [(soundtrack, soundtrackPath)],
                videoProbes: new() { [first] = new(2m, true), [second] = new(2m, true) }),
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
        AssertSoundtrackPad(graph, inputs.IndexOf(soundtrackPath), 0);
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
                [(soundtrackOne, soundtrackOnePath), (soundtrackTwo, soundtrackTwoPath)],
                videoProbes: new() { [sounded] = new(2m, true), [silent] = new(3m, false) }),
            "/fixtures/695/out.mp4");

        var inputs = InputPaths(arguments);
        var graph = FilterComplex(arguments);

        // Timeline ordering: sounded first, silent second; both keep video pads.
        Assert.Equal(new[] { soundedPath, silentPath }, new[] { inputs[0], inputs[1] });
        Assert.Contains("[0:v]", graph);
        Assert.Contains("[1:v]", graph);
        Assert.Contains("[0:a]", graph);
        Assert.DoesNotContain("[1:a]", graph);

        // Soundtrack indexes are derived from the emitted input order: the first mix
        // input sits immediately after the LAST timeline input (index 2) and the second
        // follows it (index 3). A correct fix may add inputs for substituted silence,
        // but must keep this relative layout; these assertions locate each soundtrack
        // by its own -i position rather than by hard-coded arithmetic.
        Assert.Equal(2, inputs.IndexOf(soundtrackOnePath));
        Assert.Equal(3, inputs.IndexOf(soundtrackTwoPath));
        AssertSoundtrackPad(graph, inputs.IndexOf(soundtrackOnePath), 0);
        AssertSoundtrackPad(graph, inputs.IndexOf(soundtrackTwoPath), 1);
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
                [(soundtrack, soundtrackPath)],
                videoProbes: new() { [repeated] = new(4m, true), [other] = new(2m, false) }),
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
        AssertSoundtrackPad(graph, inputs.IndexOf(soundtrackPath), 0);
        Assert.Contains("concat=n=3:v=1:a=1", graph);
    }

    [LocalMediaFact]
    public Task Sounded_only_selection_renders_video_and_embedded_audio() => RenderAcceptance(true);

    [LocalMediaFact]
    public Task Silent_only_selection_renders_with_substituted_silence_and_preserved_soundtrack_index() => RenderAcceptance(false);

    [LocalMediaFact]
    public Task Mixed_silent_and_sounded_selection_renders_preserving_embedded_audio_and_soundtrack_index() => RenderAcceptance(true, false);

    private static FfmpegMovieFinalAssemblyExecutor CreateExecutor() => new(Options.Create(
        new MovieFinalAssemblyOptions { Enabled = true, ProcessTimeoutSeconds = 30 }));

    private sealed class LocalMediaFactAttribute : FactAttribute
    {
        public LocalMediaFactAttribute()
        {
            var hosted = new[] { "CI", "GITHUB_ACTIONS" }.Any(variable =>
                string.Equals(Environment.GetEnvironmentVariable(variable), "true", StringComparison.OrdinalIgnoreCase));
            if (!hosted && !CreateExecutor().IsAvailable)
                Skip = "The local FFmpeg and FFprobe executables are required for real rendering.";
        }
    }

    private static void AssertSoundtrackPad(string graph, int inputIndex, int mixIndex)
    {
        var pad = Assert.Single(graph.Split(';'), filter => filter.StartsWith($"[{inputIndex}:a]", StringComparison.Ordinal));
        Assert.Contains("volume=", pad);
        Assert.EndsWith($"[mix{mixIndex}]", pad);
    }

    private static async Task RenderAcceptance(params bool[] hasEmbeddedAudio)
    {
        var executor = CreateExecutor();
        Assert.True(executor.IsAvailable, "Hosted media acceptance requires FFmpeg and FFprobe on PATH.");
        var directory = Directory.CreateTempSubdirectory("taslim-assembly-695-");
        try
        {
            var timeline = new List<(Guid Asset, string Path, decimal InPoint, decimal? OutPoint)>();
            for (var slot = 0; slot < hasEmbeddedAudio.Length; slot++)
            {
                var path = Path.Combine(directory.FullName, $"clip-{slot}.mp4");
                var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x240:r=12" };
                if (hasEmbeddedAudio[slot]) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000", "-c:a", "aac"]);
                args.AddRange(["-t", "2", "-c:v", "mpeg4", path]);
                await RunTool("ffmpeg", args);
                timeline.Add((Guid.NewGuid(), path, 0m, 2m));
            }

            var soundtrackPath = Path.Combine(directory.FullName, "soundtrack.m4a");
            await RunTool("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=48000", "-t", "6", "-c:a", "aac", soundtrackPath]);
            var request = ExecutionRequest([.. timeline], [(Guid.NewGuid(), soundtrackPath)]);
            var result = await executor.ExecuteAsync(request, new Progress<int>(), CancellationToken.None);
            var outputPath = Path.Combine(directory.FullName, "master.mp4");
            await using (var stream = await result.OpenReadAsync(CancellationToken.None))
            await using (var output = File.Create(outputPath))
            {
                await stream.CopyToAsync(output);
            }

            var probe = await RunTool("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "json", outputPath]);
            using var document = JsonDocument.Parse(probe.Output);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Equal(320, video.GetProperty("width").GetInt32());
            Assert.Equal(240, video.GetProperty("height").GetInt32());
            Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
            var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Assert.InRange(duration, timeline.Count * 2 - 0.15, timeline.Count * 2 + 0.15);

            for (var slot = 0; slot < timeline.Count; slot++)
            {
                var soundtrack = await ToneLevel(outputPath, slot, 220);
                Assert.True(soundtrack > -40, $"Slot {slot} lost its 220 Hz soundtrack ({soundtrack} dB).");
                var embedded = await ToneLevel(outputPath, slot, 880);
                if (hasEmbeddedAudio[slot])
                    Assert.True(embedded > -40, $"Slot {slot} lost its embedded 880 Hz tone ({embedded} dB).");
                else
                    Assert.True(embedded < -48, $"Silent slot {slot} contains an unexpected 880 Hz tone ({embedded} dB).");
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<double> ToneLevel(string path, int slot, int frequency)
    {
        var start = (slot * 2 + 0.5).ToString(CultureInfo.InvariantCulture);
        var level = await RunTool("ffmpeg", ["-hide_banner", "-nostats", "-ss", start, "-i", path, "-t", "0.5", "-vn", "-af", $"bandpass=f={frequency}:t=h:w=20,volumedetect", "-f", "null", "-"]);
        var match = Regex.Match(level.Error, @"mean_volume:\s*(-?[0-9.]+) dB");
        Assert.True(match.Success, $"Could not measure {frequency} Hz in slot {slot}.");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<(string Output, string Error)> RunTool(string name, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(name) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {name}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        var stderr = await error;
        Assert.True(process.ExitCode == 0, $"{name} exited with {process.ExitCode}: {stderr}");
        return (await output, stderr);
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
        (string Mode, string Path, Guid Asset)? captions = null,
        Dictionary<Guid, MovieFinalAssemblyVideoProbe>? videoProbes = null)
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
            sources, videoProbes);
    }
}

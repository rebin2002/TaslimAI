using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Mandatory provider-free media execution evidence in the hosted Linux CI job.
/// The original local media test can return successfully if FFmpeg is missing;
/// this independent gate fails closed before invoking its real assembly fixture.
/// </summary>
public sealed class MovieFinalAssemblyHostedFfmpegGateTests
{
    [Fact]
    public async Task Hosted_ci_executes_real_mixed_audio_video_assembly_and_probes_output()
    {
        // Keep ordinary development machines independent of platform media tools.
        // GitHub Actions sets CI and GITHUB_ACTIONS, so hosted qualification never
        // silently passes when its media toolchain is absent.
        var hosted = string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                StringComparison.OrdinalIgnoreCase);
        if (!hosted)
            return;

        Assert.True(OperatingSystem.IsLinux(),
            "Movie media release qualification requires a Linux CI runner.");
        Assert.True(File.Exists("/usr/bin/ffmpeg"),
            "Movie media release qualification requires real /usr/bin/ffmpeg.");
        Assert.True(File.Exists("/usr/bin/ffprobe"),
            "Movie media release qualification requires real /usr/bin/ffprobe.");

        // This fixture creates synthetic silent landscape and sounded portrait
        // media, assembles them with actual FFmpeg, probes the resulting audio,
        // dimensions and bounded runtime, and exercises caption burn-in.
        // No paid provider, customer asset, credential or production object is used.
        await new MovieFinalAssemblyTests()
            .Real_ffmpeg_mixed_geometry_silent_and_sounded_clips_export_with_audio_and_correct_duration();
    }
}

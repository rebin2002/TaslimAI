using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ManusMovieVideoCostAdmissionTests
{
    [Theory]
    [InlineData("720p", null, null, false)]
    [InlineData(null, "720p", "standard", false)]
    [InlineData("720p", "1080p", "cinema", false)]
    [InlineData(null, null, null, true)]
    public async Task Scene_clip_with_unenforced_priced_output_intent_never_submits_task(
        string? sourceResolution,
        string? targetResolution,
        string? qualityTier,
        bool upscalingRequested)
    {
        var handler = new RejectingHandler();
        var provider = CreateProvider(handler);
        var request = CreateRequest() with
        {
            SourceResolution = sourceResolution,
            TargetResolution = targetResolution,
            QualityTier = qualityTier,
            UpscalingRequested = upscalingRequested,
        };

        await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Scene_clip_with_unmapped_reference_frames_never_submits_task()
    {
        var handler = new RejectingHandler();
        var provider = CreateProvider(handler);
        var request = CreateRequest() with
        {
            FirstFrameImageUri = "https://assets.example.test/first.png",
            LastFrameImageUri = "https://assets.example.test/last.png",
            ReferenceImages = [new MovieVideoReferenceImage("https://assets.example.test/reference.png")],
        };

        await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    private static ManusMovieVideoProvider CreateProvider(HttpMessageHandler handler)
    {
        var options = new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = "manus",
            Manus = new ManusMovieVideoOptions
            {
                Enabled = true,
                ApiKey = "synthetic-test-token",
                ApiBaseUrl = "https://api.manus.ai/",
                AgentProfile = "standard",
            },
        };
        return new ManusMovieVideoProvider(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<ManusMovieVideoProvider>.Instance);
    }

    private static MovieVideoGenerationRequest CreateRequest() => new(
        MovieStudioOperations.SceneClip,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "Synthetic movie shot for contract validation.",
        5,
        "16:9",
        "cinematic",
        "en",
        null,
        null,
        null,
        null);

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("A priced, unsupported scene clip reached the outbound HTTP boundary.");
        }
    }
}

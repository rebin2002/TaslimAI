using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Files;
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

    [Fact]
    public async Task Unsupported_priced_scene_clip_does_not_resolve_source_image_host()
    {
        var handler = new RejectingHandler();
        var urlPolicy = new CountingUrlPolicy();
        var provider = CreateProvider(handler, urlPolicy);
        var request = CreateRequest() with
        {
            TargetResolution = "1080p",
            SourceImageUri = "https://assets.example.test/guide.png",
        };

        var failure = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, failure.Code);
        Assert.False(failure.IsTransient);
        Assert.Equal(0, urlPolicy.Calls);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Oversized_Manus_director_prompt_rejects_before_image_host_lookup_or_task_creation()
    {
        // The old prompt builder truncated continuity/direction silently.
        // A potentially billable task must not omit unreviewed source intent.
        var handler = new RejectingHandler();
        var urlPolicy = new CountingUrlPolicy();
        var provider = CreateProvider(handler, urlPolicy);
        var request = CreateRequest() with
        {
            AdditionalInstructions = new string('X', 13_000),
            SourceImageUri = "https://assets.example.test/guide.png",
        };

        var failure = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => provider.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, failure.Code);
        Assert.False(failure.IsTransient);
        Assert.Equal(0, urlPolicy.Calls);
        Assert.Equal(0, handler.RequestCount);
    }

    private static ManusMovieVideoProvider CreateProvider(HttpMessageHandler handler, IProviderUrlPolicy? urlPolicy = null)
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
            NullLogger<ManusMovieVideoProvider>.Instance,
            urlPolicy);
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

    private sealed class CountingUrlPolicy : IProviderUrlPolicy
    {
        public int Calls { get; private set; }

        public Task EnsureSafeAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

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

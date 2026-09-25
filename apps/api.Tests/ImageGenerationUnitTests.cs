using System.Net;
using System.Net.Http;
using System.Text;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationUnitTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void Prompt_builder_preserves_exact_text_and_does_not_add_personal_context()
    {
        var builder = new TaslimImagePromptBuilder();
        var result = builder.Build(new ImageGenerationInput(
            "A clean product hero for a ceramic cup.",
            "product",
            "landscape",
            "high",
            "Cup hero",
            "calm",
            "soft stone background",
            "Taslim",
            null,
            null));

        Assert.Contains("A clean product hero for a ceramic cup.", result.Prompt);
        Assert.Contains("Render exactly this requested text inside the image: \"Taslim\".", result.Prompt);
        Assert.Contains("landscape", result.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory", result.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", result.Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capability_validation_rejects_requests_the_selected_provider_cannot_execute()
    {
        var capabilities = new ImageGenerationCapabilities(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Square },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Standard },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" },
            false,
            1);
        var request = new ImageGenerationInput("A test image", "auto", "portrait", "standard", null, null, null, null, null, null);

        var exception = Assert.Throws<ImageRequestValidationException>(() => ImageGenerationRequestValidator.Validate(request, new ImageGenerationOptions(), capabilities));

        Assert.Equal("IMAGE_ASPECT_RATIO_UNSUPPORTED", exception.Code);
    }

    [Fact]
    public void Image_binary_inspector_accepts_png_and_rejects_non_image_payloads()
    {
        var info = ImageBinaryInspector.Read(Png);
        Assert.NotNull(info);
        Assert.Equal("image/png", info.ContentType);
        Assert.Equal(1, info.Width);
        Assert.Equal(1, info.Height);
        Assert.Null(ImageBinaryInspector.Read("<html>error</html>"u8));
    }

    [Fact]
    public async Task Handler_rejects_invalid_provider_output_before_returning_success()
    {
        var handler = new ImageGenerationJobHandler(
            [new InvalidImageProvider()],
            new TaslimImagePromptBuilder(),
            Options.Create(new ImageGenerationOptions { Enabled = true }),
            NullLogger<ImageGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.ImageGenerate,
            InputJson = System.Text.Json.JsonSerializer.Serialize(new ImageGenerationInput("A test image", "auto", "square", "standard", null, null, null, null, null, null)),
        };

        await Assert.ThrowsAsync<ImageOutputInvalidException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task OpenAi_provider_uses_standard_output_tokens_when_image_details_are_absent()
    {
        var png = Convert.ToBase64String(Png);
        using var httpClient = new HttpClient(new StubImageResponseHandler($"{{\"data\":[{{\"b64_json\":\"{png}\"}}],\"usage\":{{\"input_tokens\":139,\"output_tokens\":439,\"input_tokens_details\":{{\"text_tokens\":139,\"image_tokens\":0}}}}}}"))
        {
            BaseAddress = new Uri("https://example.test/"),
        };
        var aiOptions = Options.Create(new AiOptions
        {
            OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" },
        });
        var imageOptions = Options.Create(new ImageGenerationOptions { Enabled = true, ProviderKey = "openai", Model = "gpt-image-2.5-sunburst" });
        var provider = new OpenAiImageGenerationProvider(httpClient, aiOptions, imageOptions, NullLogger<OpenAiImageGenerationProvider>.Instance);
        var prompt = new TaslimImagePromptBuilder().Build(new ImageGenerationInput("A test image", "auto", "square", "standard", null, null, null, null, null, null));

        var result = await provider.GenerateAsync(new ImageGenerationInput("A test image", "auto", "square", "standard", null, null, null, null, null, null), prompt);

        Assert.Equal(139, result.Usage.InputTokens);
        Assert.Equal(439, result.Usage.OutputTokens);
        Assert.Null(result.Usage.ImageOutputTokens);
        Assert.Equal(0.013865m, result.Usage.ActualCostUsd);
        Assert.Equal(UsageCostBasis.Actual, result.Usage.CostBasis);
        Assert.True(result.Usage.LatencyMs >= 1);
    }

    [Fact]
    public async Task OpenAi_provider_reuses_a_stable_idempotency_key_across_retries()
    {
        var handler = new RecordingImageResponseHandler(Png);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var prompt = CreatePrompt(generationJobId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await provider.GenerateAsync(prompt.Request, prompt.Prompt);

        Assert.Equal(2, handler.IdempotencyKeys.Count);
        Assert.All(handler.IdempotencyKeys, key => Assert.Equal("taslim-image-11111111111111111111111111111111", key));
        Assert.Equal("gpt-image-2.5-sunburst", handler.Model);
    }

    [Fact]
    public async Task OpenAi_provider_maps_timeout_to_safe_timeout_exception()
    {
        using var httpClient = new HttpClient(new BlockingImageResponseHandler());
        var provider = CreateProvider(httpClient, timeoutSeconds: 1);
        var prompt = CreatePrompt();

        await Assert.ThrowsAsync<ImageProviderTimeoutException>(() => provider.GenerateAsync(prompt.Request, prompt.Prompt));
    }

    [Fact]
    public async Task OpenAi_provider_preserves_caller_cancellation()
    {
        using var httpClient = new HttpClient(new BlockingImageResponseHandler());
        var provider = CreateProvider(httpClient, timeoutSeconds: 2);
        var prompt = CreatePrompt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateAsync(prompt.Request, prompt.Prompt, cancellation.Token));
    }

    [Fact]
    public async Task OpenAi_provider_retries_transient_network_failure_then_returns_safe_unavailable_error()
    {
        using var httpClient = new HttpClient(new ThrowingImageResponseHandler());
        var provider = CreateProvider(httpClient);
        var prompt = CreatePrompt();

        await Assert.ThrowsAsync<ImageProviderUnavailableException>(() => provider.GenerateAsync(prompt.Request, prompt.Prompt));
    }

    [Fact]
    public async Task OpenAi_provider_retries_rate_limit_then_returns_safe_rate_limit_error()
    {
        using var httpClient = new HttpClient(new StatusImageResponseHandler(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"sensitive upstream text\"}}"));
        var provider = CreateProvider(httpClient);
        var prompt = CreatePrompt();

        await Assert.ThrowsAsync<ImageProviderRateLimitException>(() => provider.GenerateAsync(prompt.Request, prompt.Prompt));
    }

    private static (ImageGenerationInput Request, ImagePromptBuildResult Prompt) CreatePrompt(Guid? generationJobId = null)
    {
        var request = new ImageGenerationInput("A test image", "auto", "square", "standard", null, null, null, null, null, null, generationJobId);
        return (request, new TaslimImagePromptBuilder().Build(request));
    }

    private static OpenAiImageGenerationProvider CreateProvider(HttpClient client, int timeoutSeconds = 10) => new(
        client,
        Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
        Options.Create(new ImageGenerationOptions { Enabled = true, ProviderKey = "openai", Model = "gpt-image-2.5-sunburst", ProviderTimeoutSeconds = timeoutSeconds }),
        NullLogger<OpenAiImageGenerationProvider>.Instance);

    private sealed class InvalidImageProvider : IImageGenerationProvider
    {
        public string Key => "openai";
        public Task<ImageProviderResult> GenerateAsync(ImageGenerationInput request, ImagePromptBuildResult prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ImageProviderResult("not-an-image"u8.ToArray(), "text/plain", "txt", null, null, new ImageProviderUsage(null, null, null, null, 0m)));
    }

    private sealed class StubImageResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class RecordingImageResponseHandler(byte[] png) : HttpMessageHandler
    {
        public List<string> IdempotencyKeys { get; } = [];
        public string? Model { get; private set; }
        private bool firstAttempt = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IdempotencyKeys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Model = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("model").GetString();
            if (firstAttempt)
            {
                firstAttempt = false;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"temporarily_unavailable\"}}", Encoding.UTF8, "application/json"),
                };
            }
            var encoded = Convert.ToBase64String(png);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"data\":[{{\"b64_json\":\"{encoded}\"}}]}}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class BlockingImageResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingImageResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("sensitive upstream error"));
    }

    private sealed class StatusImageResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

using System.Net;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Images;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationUnitTests
{
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
    public void Image_binary_inspector_accepts_png_and_rejects_non_image_payloads()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var info = ImageBinaryInspector.Read(png);
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
        var png = Convert.ToBase64String(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
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
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}

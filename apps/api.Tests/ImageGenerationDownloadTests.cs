using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Synthetic regressions for the bounded Image Studio provider download. Every response
/// stream here is fabricated in-process: no network, provider credential, database, or
/// billable provider call is involved.
/// </summary>
public sealed class ImageGenerationDownloadTests
{
    [Fact]
    public async Task OpenAi_provider_maps_a_mid_download_transport_failure_to_the_safe_provider_failure()
    {
        // A connection reset or a response that ends prematurely surfaces as IOException
        // while the bounded body read is in progress, not as HttpRequestException.
        var handler = new MidDownloadImageResponseHandler(
            new ThrowingMidDownloadStream(ValidJsonPrefix(), new IOException("The response ended prematurely.")));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var prompt = CreatePrompt();

        var exception = await Assert.ThrowsAsync<ImageProviderUnavailableException>(
            () => provider.GenerateAsync(prompt.Request, prompt.Prompt));

        Assert.Equal("The configured image provider is unavailable.", exception.Message);
        Assert.DoesNotContain("premature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenAi_provider_keeps_the_specific_network_mapping_for_a_failure_raised_while_reading()
    {
        // HttpRequestException derives from IOException; the pre-existing, more specific
        // transport mapping must keep winning over the bounded-read fallback.
        var handler = new MidDownloadImageResponseHandler(
            new ThrowingMidDownloadStream(ValidJsonPrefix(), new HttpRequestException("The ssl connection could not be established.")));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient);
        var prompt = CreatePrompt();

        await Assert.ThrowsAsync<ImageProviderUnavailableException>(
            () => provider.GenerateAsync(prompt.Request, prompt.Prompt));
    }

    [Fact]
    public async Task OpenAi_provider_maps_a_provider_timeout_hit_while_downloading_to_the_safe_timeout()
    {
        var handler = new MidDownloadImageResponseHandler(new StallingMidDownloadStream(ValidJsonPrefix()));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, timeoutSeconds: 1);
        var prompt = CreatePrompt();

        await Assert.ThrowsAsync<ImageProviderTimeoutException>(
            () => provider.GenerateAsync(prompt.Request, prompt.Prompt));
    }

    [Fact]
    public async Task OpenAi_provider_preserves_caller_cancellation_raised_while_downloading()
    {
        var handler = new MidDownloadImageResponseHandler(new StallingMidDownloadStream(ValidJsonPrefix()));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(httpClient, timeoutSeconds: 5);
        var prompt = CreatePrompt();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GenerateAsync(prompt.Request, prompt.Prompt, cancellation.Token));
    }

    [Fact]
    public void Mid_download_failure_is_only_retryable_when_it_is_classified_as_a_provider_failure()
    {
        // This pins the operational consequence of the mapping above: an unmapped transport
        // exception was persisted as the generic generation failure, which the classifier
        // reports as a non-retryable internal fault instead of a retryable provider fault.
        var providerFailure = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.ImageProviderUnavailable);
        Assert.Equal("provider", providerFailure.Category);
        Assert.True(providerFailure.Retryable);

        var genericFailure = GenerationFailureClassifier.Classify(GenerationJobErrorCodes.ImageGenerationFailed);
        Assert.Equal("internal", genericFailure.Category);
        Assert.False(genericFailure.Retryable);
    }

    private static byte[] ValidJsonPrefix() =>
        Encoding.UTF8.GetBytes("{\"data\":[{\"b64_json\":\"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4");

    private static (ImageGenerationInput Request, ImagePromptBuildResult Prompt) CreatePrompt()
    {
        var request = new ImageGenerationInput("A test image", "auto", "square", "standard", null, null, null, null, null, null);
        return (request, new TaslimImagePromptBuilder().Build(request));
    }

    private static OpenAiImageGenerationProvider CreateProvider(HttpClient client, int timeoutSeconds = 10) => new(
        client,
        Options.Create(new AiOptions { OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-key", BaseUrl = "https://example.test/v1" } }),
        Options.Create(new ImageGenerationOptions { Enabled = true, ProviderKey = "openai", Model = "gpt-image-2.5-sunburst", ProviderTimeoutSeconds = timeoutSeconds }),
        NullLogger<OpenAiImageGenerationProvider>.Instance);

    private sealed class MidDownloadImageResponseHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    /// <summary>Yields the declared prefix, then fails the way a broken transport does.</summary>
    private sealed class ThrowingMidDownloadStream(byte[] prefix, Exception failure) : MidDownloadStreamBase(prefix)
    {
        protected override int ReadBeyondPrefix() => throw failure;

        protected override ValueTask<int> ReadBeyondPrefixAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromException<int>(failure);
    }

    /// <summary>Yields the declared prefix, then blocks until the read token fires.</summary>
    private sealed class StallingMidDownloadStream(byte[] prefix) : MidDownloadStreamBase(prefix)
    {
        protected override int ReadBeyondPrefix() =>
            ReadBeyondPrefixAsync(Memory<byte>.Empty, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        protected override async ValueTask<int> ReadBeyondPrefixAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private abstract class MidDownloadStreamBase : Stream
    {
        private readonly MemoryStream prefix = new();

        protected MidDownloadStreamBase(byte[] prefixBytes)
        {
            prefix.Write(prefixBytes, 0, prefixBytes.Length);
            prefix.Position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = prefix.Read(buffer);
            return read > 0 ? read : ReadBeyondPrefix();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await prefix.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            return await ReadBeyondPrefixAsync(buffer, cancellationToken);
        }

        protected abstract int ReadBeyondPrefix();

        protected abstract ValueTask<int> ReadBeyondPrefixAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    }
}

using System.Net;
using System.Text;
using Taslim.Api.Movies;
using Taslim.Api.Tests.ProviderTesting;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DirectVideoProviderFoundationTests
{
    [Fact]
    public void Configuration_is_fail_closed_and_sanitized()
    {
        var defaults = new DirectVideoProviderOptions();
        var configuration = defaults.ToConfiguration();

        Assert.False(configuration.IsConfigured);
        Assert.False(configuration.HasCredentials);
        Assert.Contains("credentialsConfigured=False", configuration.SanitizedDescription());
        Assert.DoesNotContain("secret", configuration.SanitizedDescription(), StringComparison.OrdinalIgnoreCase);

        var configured = new DirectVideoProviderOptions
        {
            Enabled = true,
            ProviderKey = "internal-adapter",
            ModelKey = "internal-model",
            ApiBaseUrl = "https://provider.invalid/api/",
            ApiKey = "synthetic-secret",
        }.ToConfiguration();
        var safe = configured.SanitizedDescription();

        Assert.True(configured.IsConfigured);
        Assert.Contains("credentialsConfigured=True", safe);
        Assert.DoesNotContain("synthetic-secret", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("internal-model", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_normalization_builds_a_bounded_provider_neutral_request()
    {
        var request = Request(
            description: "A camera moves through a forest.",
            additionalInstructions: new string('x', 300),
            sourceImageUri: "https://assets.invalid/frame.png",
            worldContextJson: "{\"location\":\"forest\"}");

        var normalized = DirectVideoRequestNormalizer.Normalize(
            request,
            Capabilities(),
            requestedResolution: "1920x1080",
            upscaleRequested: true,
            maxPromptCharacters: 256);

        Assert.Equal(DirectVideoResolutions.FullHd, normalized.Resolution);
        Assert.True(normalized.UpscaleRequested);
        Assert.Equal("https://assets.invalid/frame.png", normalized.SourceImageUri);
        Assert.True(normalized.PromptText.Length <= 256);
        Assert.EndsWith("...", normalized.PromptText, StringComparison.Ordinal);
        Assert.Contains("world", normalized.ContinuityContextJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Request_normalization_rejects_unsupported_capabilities_and_unsafe_inputs()
    {
        var capabilities = Capabilities();

        Assert.Equal(DirectVideoErrorCodes.CapabilityUnsupported, Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(Request(durationSeconds: 30), capabilities)).Code);
        Assert.Equal(DirectVideoErrorCodes.CapabilityUnsupported, Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(Request(), capabilities, requestedResolution: "2160p")).Code);
        Assert.Equal(DirectVideoErrorCodes.RequestInvalid, Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(Request(sourceImageUri: "http://insecure.invalid/frame.png"), capabilities)).Code);
        Assert.Equal(DirectVideoErrorCodes.CapabilityUnsupported, Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(Request(continuationProviderJobId: "continuation-id"), CapabilitiesWithoutContinuation())).Code);
        Assert.Equal(DirectVideoErrorCodes.RequestInvalid, Assert.Throws<DirectVideoRequestNormalizationException>(() =>
            DirectVideoRequestNormalizer.Normalize(Request(continuationProviderJobId: new string('j', 241)), capabilities)).Code);
    }

    [Fact]
    public void Result_normalization_maps_states_clamps_progress_and_discards_unsafe_metadata()
    {
        var queued = DirectVideoResultNormalizer.NormalizeStatus(new DirectVideoProviderStatusResponse("processing", 175));
        var failed = DirectVideoResultNormalizer.NormalizeStatus(new DirectVideoProviderStatusResponse("failed", -10, SafeMetadataJson: "{\"providerBody\":\"private\"}"));
        var invalidMetadata = DirectVideoResultNormalizer.NormalizeStatus(new DirectVideoProviderStatusResponse("succeeded", 100, "video/mp4", "clip.mp4", 12, 2, SafeMetadataJson: "not-json"));

        Assert.Equal(DirectVideoJobStatus.Running, queued.Status);
        Assert.Equal(100, queued.ProgressPercent);
        Assert.Equal(DirectVideoJobStatus.Failed, failed.Status);
        Assert.Equal(0, failed.ProgressPercent);
        Assert.Contains("providerBody", failed.SafeMetadataJson, StringComparison.Ordinal);
        Assert.Null(invalidMetadata.SafeMetadataJson);
    }

    [Fact]
    public async Task Output_normalization_sanitizes_file_names_and_rejects_invalid_media()
    {
        var output = DirectVideoResultNormalizer.NormalizeOutput(
            "VIDEO/MP4",
            "../../clip.mp4",
            4,
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("clip"))),
            2,
            null,
            0m,
            "usd",
            "Actual",
            "{\"safe\":true}",
            "internal-model",
            1_024);

        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal("clip.mp4", output.FileName);
        Assert.Equal("USD", output.Currency);
        Assert.Equal("Actual", output.CostBasis);
        Assert.Equal("{\"safe\":true}", output.SafeMetadataJson);
        await using var stream = await output.OpenReadAsync(CancellationToken.None);
        using var reader = new StreamReader(stream);
        Assert.Equal("clip", await reader.ReadToEndAsync());

        Assert.Throws<DirectVideoOutputNormalizationException>(() => DirectVideoResultNormalizer.NormalizeOutput(
            "text/plain", "clip.txt", 4, _ => Task.FromResult<Stream>(Stream.Null), 1, null, null, null, null, null, null, 1_024));
    }

    [Fact]
    public void Error_normalization_is_stable_and_never_uses_provider_response_bodies()
    {
        var auth = DirectVideoErrorNormalizer.FromStatusCode(HttpStatusCode.Unauthorized);
        var rateLimit = DirectVideoErrorNormalizer.FromStatusCode(HttpStatusCode.TooManyRequests);
        var server = DirectVideoErrorNormalizer.FromStatusCode(HttpStatusCode.InternalServerError);
        var exception = DirectVideoErrorNormalizer.FromException(new InvalidOperationException("provider secret response body"));

        Assert.Equal(DirectVideoErrorCodes.ProviderAuthentication, auth.Code);
        Assert.False(auth.Retryable);
        Assert.Equal(DirectVideoErrorCodes.ProviderRateLimited, rateLimit.Code);
        Assert.True(rateLimit.Retryable);
        Assert.Equal(DirectVideoErrorCodes.ProviderTransientFailure, server.Code);
        Assert.True(server.Retryable);
        Assert.Equal(DirectVideoErrorCodes.ProviderFailure, exception.Code);
        Assert.DoesNotContain("provider secret response body", exception.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_hook_exposes_configuration_and_probe_states_without_raw_errors()
    {
        var disabled = new DirectVideoProviderHealthHook(new DirectVideoProviderOptions().ToConfiguration());
        var unconfigured = new DirectVideoProviderHealthHook(new DirectVideoProviderOptions { Enabled = true }.ToConfiguration());
        var healthy = new DirectVideoProviderHealthHook(ConfiguredOptions().ToConfiguration(), _ => Task.CompletedTask);
        var unhealthy = new DirectVideoProviderHealthHook(ConfiguredOptions().ToConfiguration(), _ => throw new InvalidOperationException("private provider body"));

        Assert.Equal(DirectVideoHealthStatus.Disabled, (await disabled.CheckAsync()).Status);
        Assert.Equal(DirectVideoHealthStatus.Unconfigured, (await unconfigured.CheckAsync()).Status);
        Assert.True((await healthy.CheckAsync()).Ready);
        var failed = await unhealthy.CheckAsync();
        Assert.Equal(DirectVideoHealthStatus.Unhealthy, failed.Status);
        Assert.Equal(DirectVideoErrorCodes.ProviderFailure, failed.ErrorCode);
        Assert.DoesNotContain("private provider body", failed.ErrorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mock_adapter_supports_async_status_retrieval_and_private_output_contract()
    {
        var adapter = new MockDirectVideoProviderAdapter(MockDirectVideoScenario.QueuedThenSuccess);
        var normalizedRequest = DirectVideoRequestNormalizer.Normalize(Request(), adapter.Capabilities, "720p");
        var submission = await adapter.SubmitAsync(normalizedRequest);
        var queued = await adapter.GetStatusAsync(submission.ProviderJobId);
        var complete = await adapter.GetStatusAsync(submission.ProviderJobId);
        var output = await adapter.RetrieveAsync(submission.ProviderJobId, complete);

        Assert.True(adapter.IsAvailable);
        Assert.Equal(DirectVideoJobStatus.Queued, queued.Status);
        Assert.Equal(DirectVideoJobStatus.Succeeded, complete.Status);
        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal(1, adapter.SubmitCount);
        Assert.Equal(2, adapter.StatusCount);
        Assert.Equal(1, adapter.RetrieveCount);
        Assert.True((await adapter.Health.CheckAsync()).Ready);
    }

    private static DirectVideoProviderOptions ConfiguredOptions() => new()
    {
        Enabled = true,
        ProviderKey = "internal-adapter",
        ModelKey = "internal-model",
        ApiBaseUrl = "https://provider.invalid/api/",
        ApiKey = "synthetic-secret",
    };

    private static DirectVideoCapabilityDeclaration Capabilities() => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MovieStudioOperations.SceneClip },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DirectVideoResolutions.Hd720, DirectVideoResolutions.FullHd },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
        2,
        10,
        SupportsReferenceImage: true,
        SupportsContinuation: true,
        SupportsUpscaling: true,
        SupportsNativeAudio: false);

    private static DirectVideoCapabilityDeclaration CapabilitiesWithoutContinuation() => Capabilities() with { SupportsContinuation = false };

    private static MovieVideoGenerationRequest Request(
        int durationSeconds = 5,
        string description = "A camera moves slowly through a forest.",
        string? additionalInstructions = "No text overlays.",
        string? sourceImageUri = null,
        string? continuationProviderJobId = null,
        string? worldContextJson = null) => new(
            MovieStudioOperations.SceneClip,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            description,
            durationSeconds,
            "16:9",
            "cinematic",
            "en",
            additionalInstructions,
            "Visual language: natural light",
            "Scene: forest",
            "Shot: slow dolly",
            sourceImageUri,
            continuationProviderJobId,
            worldContextJson);
}

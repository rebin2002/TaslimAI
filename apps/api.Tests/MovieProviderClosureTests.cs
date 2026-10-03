using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Movies;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueVoiceProviderAdapterTests
{
    [Fact]
    public async Task Disabled_movie_or_generic_voice_does_not_call_inner_provider()
    {
        var inner = new RecordingVoiceProvider();
        var adapter = new MovieDialogueVoiceProviderAdapter(
            inner,
            Options.Create(new MovieDialogueVoiceOptions { Enabled = false, ProviderKey = "fake" }),
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "test-model" }));

        await Assert.ThrowsAsync<MovieDialogueVoiceProviderUnavailableException>(() => adapter.GenerateAsync(Request()));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Adapter_maps_movie_semantics_and_preserves_voice_duration_and_usage()
    {
        var inner = new RecordingVoiceProvider
        {
            Result = new VoiceProviderResult(
                new byte[] { 0x49, 0x44, 0x33, 0x04 },
                "audio/mpeg",
                "mp3",
                1_350,
                null,
                new VoiceProviderUsage("voice-test-model", 11, 4, null, 7, CostBasis: UsageCostBasis.Estimated, SafeMetadataJson: "{\"synthetic\":true}", EstimatedCostUsd: 0.02m)),
        };
        var adapter = new MovieDialogueVoiceProviderAdapter(
            inner,
            Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake", MaxDurationMilliseconds = 10_000 }),
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "voice-test-model", MaxProviderTextCharacters = 1_000 }));

        var result = await adapter.GenerateAsync(Request() with { DeliveryNotes = "quiet, deliberate delivery" });

        Assert.Equal(1, inner.Calls);
        Assert.Equal(MovieDialogueLanguages.Arabic, inner.LastRequest!.Language);
        Assert.Equal("Hello from Movie Studio.", inner.LastRequest.Text);
        Assert.Equal("quiet, deliberate delivery", inner.LastRequest.Instructions!["Dialogue delivery notes: ".Length..]);
        Assert.Equal(MovieDialogueVoiceProviderAdapterTestsProjectId, inner.LastRequest.WorkspaceId);
        Assert.Equal(1_350, result.DurationMilliseconds);
        Assert.Equal("voice-test-model", result.Usage.ModelKey);
        Assert.Equal(0.02m, result.Usage.EstimatedCostUsd);
        Assert.Equal(UsageCostBasis.Estimated, result.Usage.CostBasis);
        Assert.Equal("{\"synthetic\":true}", result.Usage.SafeMetadataJson);
    }

    [Fact]
    public async Task Adapter_uses_movie_timing_when_generic_provider_has_no_duration()
    {
        var inner = new RecordingVoiceProvider
        {
            Result = new VoiceProviderResult(
                new byte[] { 0x49, 0x44, 0x33, 0x04 },
                "audio/mpeg",
                "mp3",
                null,
                null,
                new VoiceProviderUsage("voice-test-model", 5, 4, null, 1)),
        };
        var adapter = new MovieDialogueVoiceProviderAdapter(
            inner,
            Options.Create(new MovieDialogueVoiceOptions { Enabled = true, ProviderKey = "fake" }),
            Options.Create(new VoiceGenerationOptions { Enabled = true, ProviderKey = "fake", Model = "voice-test-model" }));

        var result = await adapter.GenerateAsync(Request() with { StartMilliseconds = 250, EndMilliseconds = 1_250 });

        Assert.Equal(1_000, result.DurationMilliseconds);
    }

    private static readonly Guid MovieDialogueVoiceProviderAdapterTestsProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MovieDialogueVoiceProviderRequest Request() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        null,
        "Narrator",
        "Hello from Movie Studio.",
        MovieDialogueLanguages.Arabic,
        0,
        2_000,
        null,
        MovieDialogueVoiceProviderAdapterTestsProjectId);

    private sealed class RecordingVoiceProvider : IVoiceGenerationProvider
    {
        public string Key => "fake";
        public int Calls { get; private set; }
        public VoiceGenerationInput? LastRequest { get; private set; }
        public VoiceProviderResult Result { get; set; } = new(
            new byte[] { 0x49, 0x44, 0x33, 0x04 },
            "audio/mpeg",
            "mp3",
            1_000,
            null,
            new VoiceProviderUsage("voice-test-model", 5, 4, null, 1));

        public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}

public sealed class MovieProviderProductionConfigurationTests
{
    [Fact]
    public void Safe_production_defaults_remain_valid()
    {
        ProductionConfigurationValidator.Validate(Configuration(BaseValues()), ProductionEnvironment());
    }

    [Fact]
    public void Enabled_movie_dialogue_requires_matching_complete_generic_voice_configuration()
    {
        var values = BaseValues();
        values["MovieDialogueVoice:Enabled"] = "true";
        values["MovieDialogueVoice:ProviderKey"] = "openai";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(Configuration(values), ProductionEnvironment()));

        Assert.Contains("VoiceGeneration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_movie_dialogue_openai_configuration_is_accepted_without_network_work()
    {
        var values = BaseValues();
        values["Ai:OpenAI:Enabled"] = "true";
        values["Ai:OpenAI:ApiKey"] = "test-only-key";
        values["Ai:OpenAI:BaseUrl"] = "https://example.test/v1";
        values["VoiceGeneration:Enabled"] = "true";
        values["VoiceGeneration:ProviderKey"] = "openai";
        values["VoiceGeneration:Model"] = "test-voice-model";
        values["MovieDialogueVoice:Enabled"] = "true";
        values["MovieDialogueVoice:ProviderKey"] = "openai";

        ProductionConfigurationValidator.Validate(Configuration(values), ProductionEnvironment());
    }

    [Fact]
    public void Enabled_movie_video_requires_provider_credentials_and_https_endpoint()
    {
        var values = BaseValues();
        values["MovieVideo:Enabled"] = "true";
        values["MovieVideo:ProviderKey"] = "runway";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(Configuration(values), ProductionEnvironment()));

        Assert.Contains("MovieVideo:ApiBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_music_requires_provider_specific_credentials()
    {
        var values = BaseValues();
        values["MusicGeneration:Enabled"] = "true";
        values["MusicGeneration:ProviderKey"] = "mubert";
        values["MusicGeneration:Model"] = "mubert-test-model";
        values["MusicGeneration:MubertApiBaseUrl"] = "https://music.example.test/";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(Configuration(values), ProductionEnvironment()));

        Assert.Contains("MubertCustomerId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Movie_sfx_cannot_be_enabled_without_an_approved_provider_contract()
    {
        var values = BaseValues();
        values["MovieSoundGeneration:Enabled"] = "true";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(Configuration(values), ProductionEnvironment()));

        Assert.Contains("SFX", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment ProductionEnvironment() => new StubEnvironment();

    private static Dictionary<string, string?> BaseValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ConnectionStrings:Postgres"] = "Host=db.internal;Port=5432;Database=taslim;Username=taslim;Password=test-only",
        ["AllowedOrigins:0"] = "https://app.example.test",
        ["Files:StorageProvider"] = "S3Compatible",
        ["Files:S3Endpoint"] = "https://s3.example.test",
        ["Files:S3Region"] = "auto",
        ["Files:S3Bucket"] = "taslim-test",
        ["Files:S3AccessKey"] = "test-access-key",
        ["Files:S3SecretKey"] = "test-secret-key",
        ["Billing:CustomerChargingEnabled"] = "false",
        ["Autopilot:ChargingEnabled"] = "false",
        ["Autopilot:PaidProvidersEnabled"] = "false",
        ["Autopilot:Enabled"] = "false",
        ["Autopilot:RequireSignedEvents"] = "true",
    };

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Taslim.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

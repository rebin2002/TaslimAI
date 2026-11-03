using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MusicSongCapabilityApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "test");
        builder.UseSetting("MusicGeneration:Model", "music-test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMusicGenerationProvider>();
            services.AddSingleton<NoCallMusicProvider>();
            services.AddSingleton<IMusicGenerationProvider>(provider => provider.GetRequiredService<NoCallMusicProvider>());
            services.RemoveAll<IUsageCostControl>();
            services.AddSingleton<CountingUsageCostControl>();
            services.AddSingleton<IUsageCostControl>(provider => provider.GetRequiredService<CountingUsageCostControl>());
        });
    }
}

public sealed class MusicGenerationSongContractTests : IClassFixture<MusicSongCapabilityApiFactory>
{
    private readonly MusicSongCapabilityApiFactory factory;

    public MusicGenerationSongContractTests(MusicSongCapabilityApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public void Current_provider_capabilities_fail_closed_for_unimplemented_modes()
    {
        foreach (var providerKey in new[] { "mubert", "stable-audio", "test" })
        {
            var capabilities = MusicGenerationCapabilityCatalog.ForProvider(providerKey);
            Assert.True(capabilities.SupportsInstrumental);
            Assert.False(capabilities.SupportsLyricsToVocals);
            Assert.False(capabilities.SupportsAudioReferenceConditioning);
            Assert.False(capabilities.SupportsVideoAudioReference);
            Assert.False(capabilities.SupportsAuthorizedCoverOrRearrangement);
            Assert.False(capabilities.SupportsRealSingerVoiceCloning);
            Assert.Empty(capabilities.SupportedLyricsLanguages);
            Assert.Empty(capabilities.SupportedLyricsDialects);
        }

        var unrecognizedProvider = MusicGenerationCapabilityCatalog.ForProvider("unrecognized-provider");
        Assert.True(unrecognizedProvider.SupportsInstrumental);
        Assert.False(unrecognizedProvider.SupportsLyricsToVocals);
        Assert.False(unrecognizedProvider.SupportsAudioReferenceConditioning);
        Assert.False(unrecognizedProvider.SupportsVideoAudioReference);
        Assert.False(unrecognizedProvider.SupportsAuthorizedCoverOrRearrangement);
        Assert.False(unrecognizedProvider.SupportsRealSingerVoiceCloning);
        Assert.Empty(unrecognizedProvider.SupportedLyricsDialects);
        var unverifiedSuno = MusicGenerationCapabilityCatalog.ForProvider("suno");
        Assert.False(unverifiedSuno.SupportsInstrumental);
        Assert.False(unverifiedSuno.SupportsLyricsToVocals);
        Assert.False(unverifiedSuno.SupportsAudioReferenceConditioning);
        Assert.False(unverifiedSuno.SupportsVideoAudioReference);
        Assert.False(unverifiedSuno.SupportsAuthorizedCoverOrRearrangement);
        Assert.False(unverifiedSuno.SupportsRealSingerVoiceCloning);
        Assert.Empty(unverifiedSuno.SupportedLyricsLanguages);
        Assert.Empty(unverifiedSuno.SupportedLyricsDialects);
        Assert.False(MusicGenerationCapabilityCatalog.ForProvider("unconfigured").SupportsInstrumental);
    }

    [Fact]
    public void Unverified_suno_provider_is_unavailable_before_instrumental_generation()
    {
        var exception = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(InstrumentalInput(), Options("suno")));

        Assert.Equal(GenerationJobErrorCodes.MusicProviderUnavailable, exception.Code);
    }

    [Fact]
    public void Structured_lyrics_are_mapped_without_adding_voice_identity_controls()
    {
        var request = new MusicGenerationRequest
        {
            Description = "An uplifting pop song",
            Purpose = "An original product launch song",
            Genre = "pop",
            Mood = "uplifting",
            DurationSeconds = 60,
            CreationMode = MusicGenerationValues.SongWithVocalsMode,
            VocalPreference = MusicGenerationValues.Vocal,
            Language = "en",
            LyricsLanguage = "ku",
            LyricsDialect = "Sorani",
            LyricsRightsConfirmed = true,
            Lyrics = [new MusicLyricsSection("verse", ["test-only fixture; not generated or dialect-reviewed"])],
        };

        var input = MusicGenerationContractMapper.ToInput(request);

        Assert.Equal(MusicGenerationValues.SongWithVocalsMode, input.CreationMode);
        Assert.Equal("ku", input.LyricsLanguage);
        Assert.Equal("sorani", input.LyricsDialect);
        Assert.True(input.LyricsRightsConfirmed);
        var lyricsSection = Assert.IsType<MusicLyricsSection>(Assert.Single(input.Lyrics!));
        Assert.Equal("verse", lyricsSection.Section);
        Assert.Equal("test-only fixture; not generated or dialect-reviewed", Assert.IsType<string>(Assert.Single(lyricsSection.Lines!)));
        Assert.DoesNotContain("voiceId", JsonSerializer.Serialize(input), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("singerId", JsonSerializer.Serialize(input), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ku", "sorani", "[TEST ONLY] ڕۆژ — کوردی")]
    [InlineData("ar", "iraqi_baghdadi", "[TEST ONLY] بغداد — عراقي")]
    public void Unicode_lyrics_lines_and_section_order_are_preserved_verbatim_by_contract_mapper(
        string lyricsLanguage,
        string lyricsDialect,
        string lyricLine)
    {
        var request = new MusicGenerationRequest
        {
            Description = "Synthetic Unicode contract fixture",
            Purpose = "Test request mapping only",
            Genre = "folk",
            Mood = "calm",
            DurationSeconds = 60,
            CreationMode = MusicGenerationValues.SongWithVocalsMode,
            VocalPreference = MusicGenerationValues.Vocal,
            Language = "en",
            LyricsLanguage = lyricsLanguage,
            LyricsDialect = lyricsDialect,
            LyricsRightsConfirmed = true,
            Lyrics =
            [
                new MusicLyricsSection("verse", [lyricLine]),
                new MusicLyricsSection("chorus", ["[TEST ONLY] repeated section"]),
            ],
        };

        var mapped = MusicGenerationContractMapper.ToInput(request);

        Assert.Equal(lyricsLanguage, mapped.LyricsLanguage);
        Assert.Equal(lyricsDialect, mapped.LyricsDialect);
        Assert.NotNull(mapped.Lyrics);
        Assert.Equal(new[] { "verse", "chorus" }, mapped.Lyrics.Select(section => section!.Section));
        Assert.Equal(lyricLine, Assert.IsType<string>(Assert.Single(mapped.Lyrics[0]!.Lines!)));
        Assert.Equal("[TEST ONLY] repeated section", Assert.IsType<string>(Assert.Single(mapped.Lyrics[1]!.Lines!)));
    }

    [Theory]
    [InlineData("en", null)]
    [InlineData("ar", "iraqi_baghdadi")]
    [InlineData("ar", "iraqi_mosul")]
    [InlineData("ar", "iraqi_basra")]
    [InlineData("ar", "gulf_arabic")]
    [InlineData("ar", "levantine_arabic")]
    [InlineData("ku", "sorani")]
    public void Valid_structured_lyrics_and_dialects_are_rejected_when_current_provider_cannot_sing_them(string lyricsLanguage, string? lyricsDialect)
    {
        var exception = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(lyricsLanguage: lyricsLanguage, lyricsDialect: lyricsDialect), Options("suno")));

        Assert.Equal(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, exception.Code);
        Assert.Contains("does not support sung lyrics", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lyrics_require_rights_attestation_and_supported_language_and_structure()
    {
        var noAttestation = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(rightsConfirmed: false), Options("mubert")));
        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, noAttestation.Code);

        var unsupportedLanguage = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(lyricsLanguage: "fr"), Options("mubert")));
        Assert.Equal(GenerationJobErrorCodes.MusicLanguageUnsupported, unsupportedLanguage.Code);

        var invalidSection = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(section: "artist-name"), Options("mubert")));
        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, invalidSection.Code);

        var overlongLine = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(line: new string('x', 201)), Options("mubert")));
        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, overlongLine.Code);
    }

    [Theory]
    [InlineData("ar", "sorani")]
    [InlineData("ku", "iraqi_baghdadi")]
    [InlineData("ar", null)]
    [InlineData("ku", null)]
    [InlineData("en", "gulf_arabic")]
    [InlineData("ar", "unknown_dialect")]
    [InlineData("fr", "iraqi_basra")]
    public void Lyrics_dialect_must_be_known_and_match_the_language(string lyricsLanguage, string? lyricsDialect)
    {
        var exception = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(SongInput(lyricsLanguage: lyricsLanguage, lyricsDialect: lyricsDialect), Options("mubert")));

        Assert.Equal(GenerationJobErrorCodes.MusicLanguageUnsupported, exception.Code);
    }

    [Theory]
    [InlineData(MusicGenerationValues.Vocal)]
    [InlineData(MusicGenerationValues.Either)]
    public void Instrumental_mode_never_silently_accepts_a_vocal_preference(string vocalPreference)
    {
        var request = InstrumentalInput() with { VocalPreference = vocalPreference };

        var exception = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(request, Options("mubert")));

        Assert.Equal(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, exception.Code);
    }

    [Fact]
    public void Instrumental_mode_rejects_lyrics_instead_of_ignoring_them()
    {
        var request = InstrumentalInput() with
        {
            Lyrics = [new MusicLyricsSection("verse", ["Do not discard these words"])],
            LyricsLanguage = "en",
            LyricsRightsConfirmed = true,
        };

        var exception = Assert.Throws<MusicRequestValidationException>(() =>
            MusicGenerationRequestValidator.Validate(request, Options("mubert")));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }

    [Theory]
    [InlineData("ku", "sorani", "[TEST ONLY] ڕۆژ — کوردی")]
    [InlineData("ar", "iraqi_baghdadi", "[TEST ONLY] بغداد — عراقي")]
    [InlineData("ar", "iraqi_mosul", "[TEST ONLY] الموصل — عربي")]
    [InlineData("ar", "iraqi_basra", "[TEST ONLY] البصرة — عربي")]
    public async Task Unsupported_lyric_song_fails_before_cost_preflight_job_creation_or_provider_call(
        string lyricsLanguage,
        string lyricsDialect,
        string lyricLine)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-song-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "An uplifting original pop song",
            purpose = "A product launch video",
            genre = "pop",
            mood = "uplifting",
            durationSeconds = 60,
            creationMode = "song_with_vocals",
            vocalPreference = "vocal",
            language = "en",
            lyricsLanguage,
            lyricsDialect,
            lyricsRightsConfirmed = true,
            lyrics = new[]
            {
                new { section = "verse", lines = new[] { lyricLine } },
                new { section = "chorus", lines = new[] { "SYNTHETIC TEST FIXTURE ONLY" } },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, factory.Services.GetRequiredService<CountingUsageCostControl>().PreflightCalls);
        Assert.Equal(0, factory.Services.GetRequiredService<NoCallMusicProvider>().Calls);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item => item.UserId == auth.User.Id));
    }

    private static MusicGenerationOptions Options(string providerKey) => new() { ProviderKey = providerKey };

    private static MusicGenerationInput InstrumentalInput() => new(
        "A calm instrumental cue",
        "A short product video",
        "ambient",
        "calm",
        60,
        MusicGenerationValues.Instrumental,
        "auto",
        null,
        null,
        null);

    private static MusicGenerationInput SongInput(
        string lyricsLanguage = "en",
        string? lyricsDialect = null,
        bool rightsConfirmed = true,
        string section = "verse",
        string line = "We begin again today") => new(
            "An uplifting original song",
            "A product launch video",
            "pop",
            "uplifting",
            60,
            MusicGenerationValues.Vocal,
            "en",
            null,
            null,
            null,
            MusicGenerationValues.SongWithVocalsMode,
            [new MusicLyricsSection(section, [line])],
            lyricsLanguage,
            rightsConfirmed,
            lyricsDialect);

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Music Song Tester",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/music-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

internal sealed class CountingUsageCostControl : IUsageCostControl
{
    private int preflightCalls;

    public int PreflightCalls => Volatile.Read(ref preflightCalls);

    public Task<UsagePreflightResult> CheckPreflightAsync(
        Guid workspaceId,
        UsageFeature feature,
        decimal? estimatedProviderCostUsd,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref preflightCalls);
        return Task.FromResult(new UsagePreflightResult(true, estimatedProviderCostUsd));
    }

    public Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NoCallMusicProvider : IMusicGenerationProvider
{
    private int calls;

    public string Key => "test";
    public int Calls => Volatile.Read(ref calls);

    public Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        throw new InvalidOperationException("The test provider must not run for an unsupported song mode.");
    }
}

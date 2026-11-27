using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Voice Studio admission must honour the capability the configured provider declares:
/// <c>VoiceGeneration:SupportedLanguages</c> and <c>VoiceGeneration:MaxProviderTextCharacters</c>.
/// Both shipped adapters already refuse a locale outside that list and a script above that
/// ceiling without contacting the network, so a request that can never succeed must be
/// refused synchronously instead of being reserved, queued, and failed as a durable job.
/// </summary>
public sealed class VoiceProviderAdmissionApiFactory : GenerationJobsApiFactory
{
    internal const int ProviderTextCeiling = 50;

    public RecordingVoiceProvider Provider { get; } = new();

    public CountingVoicePreflight CostControl { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", RecordingVoiceProvider.ProviderKey);
        builder.UseSetting("VoiceGeneration:Model", "voice-admission-model");
        builder.UseSetting("VoiceGeneration:SupportedLanguages:0", VoiceGenerationValues.English);
        builder.UseSetting("VoiceGeneration:SupportedLanguages:1", VoiceGenerationValues.Arabic);
        builder.UseSetting("VoiceGeneration:MaxTextCharacters", "10000");
        builder.UseSetting("VoiceGeneration:MaxProviderTextCharacters", ProviderTextCeiling.ToString());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IVoiceGenerationProvider>(Provider);
            services.RemoveAll<IUsageCostControl>();
            services.AddSingleton<IUsageCostControl>(CostControl);
        });
    }
}

public sealed class VoiceGenerationProviderAdmissionTests : IClassFixture<VoiceProviderAdmissionApiFactory>
{
    private readonly VoiceProviderAdmissionApiFactory factory;

    public VoiceGenerationProviderAdmissionTests(VoiceProviderAdmissionApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Locale_the_configured_provider_cannot_serve_is_refused_before_a_job_is_created()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Voice Admission Locale");
        var dispatches = factory.Provider.Calls;
        var preflightChecks = factory.CostControl.CallCount;

        var response = await Send(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "سڵاو بۆ ئێوە لە تەسلیم",
            language = LanguageCodes.Kurdish,
            voiceStyle = VoiceGenerationValues.Neutral,
            speakingStyle = VoiceGenerationValues.Clear,
            title = "Sorani narration",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            GenerationJobErrorCodes.VoiceLanguageUnsupported,
            await ReadErrorCode(response));
        await AssertNothingWasReservedOrQueued(auth.User.Id);
        Assert.Equal(dispatches, factory.Provider.Calls);
        Assert.Equal(preflightChecks, factory.CostControl.CallCount);
    }

    [Fact]
    public async Task Script_above_the_provider_ceiling_is_refused_before_a_job_is_created()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Voice Admission Length");
        var dispatches = factory.Provider.Calls;
        var preflightChecks = factory.CostControl.CallCount;

        var response = await Send(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = new string('a', VoiceProviderAdmissionApiFactory.ProviderTextCeiling + 1),
            language = VoiceGenerationValues.English,
            voiceStyle = VoiceGenerationValues.Neutral,
            speakingStyle = VoiceGenerationValues.Clear,
            title = "Over-long narration",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            GenerationJobErrorCodes.VoiceProviderUnsupportedRequest,
            await ReadErrorCode(response));
        await AssertNothingWasReservedOrQueued(auth.User.Id);
        Assert.Equal(dispatches, factory.Provider.Calls);
        Assert.Equal(preflightChecks, factory.CostControl.CallCount);
    }

    [Fact]
    public async Task Request_the_provider_can_serve_is_still_admitted_and_queued()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Voice Admission Accepted");
        var preflightChecks = factory.CostControl.CallCount;

        var response = await Send(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = new string('b', VoiceProviderAdmissionApiFactory.ProviderTextCeiling),
            language = VoiceGenerationValues.Arabic,
            voiceStyle = VoiceGenerationValues.Warm,
            speakingStyle = VoiceGenerationValues.Clear,
            title = "Admissible narration",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateVoiceGenerationResponse>())!;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.True(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.Id == created.Job.Id));
        Assert.True(
            factory.CostControl.CallCount > preflightChecks,
            "An admissible request must still reach the cost guard.");
        await WaitUntilDispatchedAsync();
    }

    private async Task WaitUntilDispatchedAsync()
    {
        for (var attempt = 0; attempt < 200 && factory.Provider.Calls == 0; attempt++)
        {
            await Task.Delay(25);
        }
        Assert.True(factory.Provider.Calls > 0, "The admitted request should have reached the provider.");
    }

    private async Task AssertNothingWasReservedOrQueued(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.CreatedByUserId == userId));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item => item.UserId == userId));
    }

    private static async Task<string> ReadErrorCode(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"voice-admission-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}

/// <summary>
/// Deterministic in-process provider that records every dispatch. It never contacts a real
/// provider and its usage estimate leaves the customer charge at zero.
/// </summary>
public sealed class RecordingVoiceProvider : IVoiceGenerationProvider
{
    internal const string ProviderKey = "recording";
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];

    private int calls;

    public int Calls => Volatile.Read(ref calls);

    public string Key => ProviderKey;

    public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref calls);
        return Task.FromResult(new VoiceProviderResult(
            Mp3,
            "audio/mpeg",
            "mp3",
            1000,
            24000,
            new VoiceProviderUsage("voice-admission-model", request.Text.Length, Mp3.Length, 0m, 3, CostBasis: UsageCostBasis.Estimated)));
    }
}

/// <summary>
/// Counts cost-guard passes without consulting the ledger, so a refusal that happens before
/// the guard is observable. It always allows, keeping customer charging at zero.
/// </summary>
public sealed class CountingVoicePreflight : IUsageCostControl
{
    private int callCount;

    public int CallCount => Volatile.Read(ref callCount);

    public Task<UsagePreflightResult> CheckPreflightAsync(
        Guid workspaceId,
        UsageFeature feature,
        decimal? estimatedProviderCostUsd,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        return Task.FromResult(new UsagePreflightResult(true, null));
    }

    public Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>
/// The admission gate mirrors the capability the shipped adapters enforce, so it must never
/// admit less than a provider serves or admit what a provider will refuse.
/// </summary>
public sealed class VoiceGenerationAdmissionPolicyTests
{
    [Theory]
    [InlineData("en", true)]
    [InlineData("EN", true)]
    [InlineData("ar", true)]
    [InlineData("ku", false)]
    [InlineData("fr", false)]
    public void Locale_admission_matches_the_configured_provider_capability(string language, bool supported)
    {
        var options = new VoiceGenerationOptions { SupportedLanguages = ["en", "ar"] };
        var input = Input(language: language, text: "short script");

        if (supported)
        {
            VoiceGenerationAdmissionPolicy.ValidateProviderSupport(input, options);
            return;
        }

        var exception = Assert.Throws<VoiceRequestValidationException>(
            () => VoiceGenerationAdmissionPolicy.ValidateProviderSupport(input, options));
        Assert.Equal(GenerationJobErrorCodes.VoiceLanguageUnsupported, exception.Code);
    }

    [Fact]
    public void An_empty_capability_list_fails_closed_for_every_locale()
    {
        var options = new VoiceGenerationOptions { SupportedLanguages = [] };
        var input = Input(language: VoiceGenerationValues.English, text: "short script");

        var exception = Assert.Throws<VoiceRequestValidationException>(
            () => VoiceGenerationAdmissionPolicy.ValidateProviderSupport(input, options));
        Assert.Equal(GenerationJobErrorCodes.VoiceLanguageUnsupported, exception.Code);
    }

    [Theory]
    [InlineData(49)]
    [InlineData(50)]
    public void Script_up_to_the_provider_ceiling_is_admitted(int length)
    {
        var input = Input(text: new string('a', length));

        VoiceGenerationAdmissionPolicy.ValidateProviderSupport(input, new VoiceGenerationOptions { MaxProviderTextCharacters = 50 });
    }

    [Theory]
    [InlineData(51)]
    [InlineData(10_000)]
    public void Script_above_the_provider_ceiling_is_refused_before_the_ceiling_is_clamped(int length)
    {
        var input = Input(text: new string('a', length));
        var options = new VoiceGenerationOptions { MaxProviderTextCharacters = 50 };

        var exception = Assert.Throws<VoiceRequestValidationException>(
            () => VoiceGenerationAdmissionPolicy.ValidateProviderSupport(input, options));
        Assert.Equal(GenerationJobErrorCodes.VoiceProviderUnsupportedRequest, exception.Code);
    }

    [Fact]
    public void A_non_positive_provider_ceiling_is_clamped_to_one_character()
    {
        VoiceGenerationAdmissionPolicy.ValidateProviderSupport(Input(text: "a"), new VoiceGenerationOptions { MaxProviderTextCharacters = 0 });

        Assert.Throws<VoiceRequestValidationException>(
            () => VoiceGenerationAdmissionPolicy.ValidateProviderSupport(Input(text: "ab"), new VoiceGenerationOptions { MaxProviderTextCharacters = 0 }));
    }

    private static VoiceGenerationInput Input(string language = "en", string text = "short script") => new(
        Guid.NewGuid(),
        null,
        text,
        language,
        VoiceGenerationValues.Neutral,
        VoiceGenerationValues.Clear,
        null,
        null);
}

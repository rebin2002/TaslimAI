using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MusicGenerationApiFactory : GenerationJobsApiFactory
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
            services.AddSingleton<IMusicGenerationProvider, DeterministicMusicProvider>();
        });
    }
}

public sealed class MusicUnavailableApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "unconfigured");
        builder.UseSetting("MusicGeneration:Model", "unconfigured");
    }
}

public sealed class MusicGenerationTests : IClassFixture<MusicGenerationApiFactory>
{
    private readonly MusicGenerationApiFactory factory;

    public MusicGenerationTests(MusicGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Music_job_publishes_private_asset_and_records_music_usage_at_zero_customer_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Calm launch bed",
            description = "A calm instrumental bed with warm piano and soft strings.",
            purpose = "Background music for a product launch video.",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "auto",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", responseText, StringComparison.OrdinalIgnoreCase);
        var envelope = JsonSerializer.Deserialize<CreateMusicGenerationResponse>(responseText, JsonOptions)!;
        var completed = await WaitForTerminal(client, envelope.Job.Id);

        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), completed.Status);
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("assetId", completed.ResultJson);
        Assert.Single(completed.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{envelope.Job.Id:N}");
        Assert.Equal(UsageFeature.Music, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0.004m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal("test", usage.Provider);
        Assert.Equal("music-test", usage.Model);

        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == envelope.Job.Id);
        Assert.Equal(AssetTypes.Music, asset.AssetType);
        Assert.Equal("audio/mpeg", asset.MimeType);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile!.Status);
        Assert.Equal(".mp3", asset.StoredFile.Extension);
    }

    [Fact]
    public async Task Music_asset_supports_authenticated_inline_playback_and_is_workspace_isolated()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, $"music-playback-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(owner, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            description = "A short instrumental cue",
            purpose = "Inline playback authorization test",
            genre = "ambient",
            mood = "focused",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
        });
        var created = (await response.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>())!;
        var completed = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.AsNoTracking().SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);

        var playback = await owner.GetAsync($"/api/assets/{asset.Id}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, playback.StatusCode);
        Assert.Equal("audio/mpeg", playback.Content.Headers.ContentType?.MediaType);
Assert.True((await playback.Content.ReadAsByteArrayAsync()).Length > 0);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(0m, usage.ChargedAmount);

        using var other = factory.CreateClient();
        await Register(other, $"music-other-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assets/{asset.Id}/download?inline=true")).StatusCode);
    }

    [Fact]
    public async Task Music_validation_rejects_unsupported_controls_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-validation-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A track",
            purpose = "A video",
            genre = "secret-genre",
            mood = "calm",
            durationSeconds = 60,
            vocalPreference = "instrumental",
            language = "en",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.MusicGenreUnsupported, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Music_submission_is_idempotent_across_retries()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"music-idempotent-{Guid.NewGuid():N}@example.com");
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A calm instrumental bed",
            purpose = "A product launch video",
            genre = "cinematic",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
        };
        var key = $"music-retry-{Guid.NewGuid():N}";

        var first = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", payload, key);
        var second = await SendWithCsrf(client, HttpMethod.Post, "/api/music-generation/jobs", payload, key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJob = (await first.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>())!.Job;
        var secondJob = (await second.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>())!.Job;
        Assert.Equal(firstJob.Id, secondJob.Id);

        await WaitForTerminal(client, firstJob.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == key));
        Assert.Equal(1, await db.UsageTransactions.CountAsync(item => item.RequestId == $"generation:{firstJob.Id:N}"));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Music Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed class DeterministicMusicProvider : IMusicGenerationProvider
{
    private static readonly byte[] Mp3 = CreateMinimalMp3();

    public string Key => "test";

    public async Task<MusicProviderResult> GenerateAsync(MusicGenerationInput request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(30, cancellationToken);
        return new MusicProviderResult(
Mp3,
            "audio/mpeg",
            "mp3",
            request.DurationSeconds,
            new MusicProviderUsage(100, 200, 0.004m, 0.004m, 12, CostBasis: UsageCostBasis.Actual));
    }

    private static byte[] CreateMinimalMp3()
    {
        var bytes = new byte[417];
        bytes[0] = 0xFF;
        bytes[1] = 0xFB;
        bytes[2] = 0x90;
        bytes[3] = 0x64;
        return bytes;
    }
}

public sealed class MusicUnavailableTests : IClassFixture<MusicUnavailableApiFactory>
{
    private readonly MusicUnavailableApiFactory factory;

    public MusicUnavailableTests(MusicUnavailableApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Missing_provider_fails_asynchronously_without_publishing_asset_or_exposing_internals()
    {
        using var client = factory.CreateClient();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var register = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        register.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        register.Content = JsonContent.Create(new { displayName = "Unavailable Music", email = $"music-unavailable-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var registered = await client.SendAsync(register);
        var auth = (await registered.Content.ReadFromJsonAsync<AuthResponse>())!;

        var createCsrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/music-generation/jobs");
        create.Headers.Add("X-CSRF-TOKEN", createCsrf.GetProperty("token").GetString());
        create.Content = JsonContent.Create(new { workspaceId = auth.PersonalWorkspace.Id, description = "A calm track", purpose = "A short film", genre = "ambient", mood = "calm", durationSeconds = 30, vocalPreference = "instrumental", language = "auto" });
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var envelope = await created.Content.ReadFromJsonAsync<CreateMusicGenerationResponse>();
        var failed = await WaitForTerminal(client, envelope!.Job.Id);

        Assert.Equal(GenerationJobStatus.Failed.ToString(), failed.Status);
        Assert.Equal(GenerationJobErrorCodes.MusicProviderUnavailable, failed.ErrorCode);
        Assert.DoesNotContain("provider", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == failed.Id));
        Assert.Equal(UsageTransactionStatus.Failed, await db.UsageTransactions.Where(item => item.GenerationJobId == failed.Id).Select(item => item.Status).SingleAsync());
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Music job {id} did not reach a terminal state.");
    }
}

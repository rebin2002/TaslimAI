using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VoiceGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", "test");
        builder.UseSetting("VoiceGeneration:Model", "voice-test-model");
        builder.ConfigureServices(services => services.AddSingleton<IVoiceGenerationProvider, DeterministicVoiceProvider>());
    }
}

public sealed class VoiceBlockingApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("VoiceGeneration:Enabled", "true");
        builder.UseSetting("VoiceGeneration:ProviderKey", "blocking");
        builder.UseSetting("VoiceGeneration:Model", "voice-test-model");
        builder.ConfigureServices(services => services.AddSingleton<IVoiceGenerationProvider, BlockingVoiceProvider>());
    }
}

public sealed class VoiceGenerationTests : IClassFixture<VoiceGenerationApiFactory>
{
    private readonly VoiceGenerationApiFactory factory;

    public VoiceGenerationTests(VoiceGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Successful_voice_job_publishes_private_project_audio_asset_activity_and_zero_customer_charge()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Voice Owner", "ar");
        var project = await CreateProject(owner, auth.PersonalWorkspace.Id, "Narration Project");
        var create = await SendWithCsrf(owner, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId = project.Id,
            title = "Welcome narration",
            text = "مرحبا بكم في تسليم",
            language = "ar",
            voiceStyle = "warm",
            speakingStyle = "clear",
            instructions = "Speak clearly.",
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreateVoiceGenerationResponse>())!;
        var completed = await WaitForTerminal(owner, created.Job.Id);
        Assert.Equal("Succeeded", completed.Status);
        Assert.Single(completed.Outputs);

        using var result = JsonDocument.Parse(completed.ResultJson!);
        var assetId = result.RootElement.GetProperty("assetId").GetGuid();
        Assert.Equal("audio", result.RootElement.GetProperty("assetType").GetString());
        Assert.DoesNotContain("openai", completed.ResultJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", completed.ResultJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("voice-test-model", completed.ResultJson!, StringComparison.OrdinalIgnoreCase);

        var assets = await owner.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&projectId={project.Id}&assetType=audio&status=Active");
        var asset = Assert.Single(assets!.Items);
        Assert.Equal(assetId, asset.Id);
        Assert.Equal(project.Id, asset.ProjectId);
        Assert.Equal("audio", asset.AssetType);
        Assert.Equal("audio/mpeg", asset.MimeType);

        var inline = await owner.GetAsync($"/api/assets/{asset.Id}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("audio/mpeg", inline.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64 }, await inline.Content.ReadAsByteArrayAsync());
        var download = await owner.GetAsync($"/api/assets/{asset.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var activity = await owner.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=voice.generate");
        var item = Assert.Single(activity!.Items);
        Assert.Equal("voice", item.JobType);
        Assert.Equal("Completed", item.Status);
        Assert.Equal(asset.Id, item.AssetId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var stored = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.Id == asset.Id);
        Assert.Equal(StoredFileStatus.Ready, stored.StoredFile!.Status);
        Assert.Equal(project.Id, stored.StoredFile.ProjectId);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Voice, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0.004m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);

        using var other = factory.CreateClient();
        await Register(other, "Other Voice User", "en");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/generation/jobs/{created.Job.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assets/{asset.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Invalid_voice_request_is_rejected_without_creating_a_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Voice Invalid", "en");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "hello",
            language = "fr",
            voiceStyle = "neutral",
            speakingStyle = "clear",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.VoiceRequestInvalid, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string language)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"voice-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = language,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name) =>
        SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Voice job {id} did not reach a terminal state.");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class VoiceGenerationCancellationTests : IClassFixture<VoiceBlockingApiFactory>
{
    private readonly VoiceBlockingApiFactory factory;

    public VoiceGenerationCancellationTests(VoiceBlockingApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Running_voice_job_can_be_cancelled_without_asset_and_with_cancelled_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "This request will be cancelled.",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
        });
        var created = (await response.Content.ReadFromJsonAsync<CreateVoiceGenerationResponse>())!;
        GenerationJobDto? running = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            running = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
            if (running?.Status == "Running") break;
            await Task.Delay(25);
        }
        Assert.Equal("Running", running?.Status);

        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Cancelled", terminal.Status);
        Assert.Empty(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Voice, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Voice Cancellation",
            email = $"voice-cancel-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job!.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Voice job {id} did not reach a terminal state.");
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

internal sealed class DeterministicVoiceProvider : IVoiceGenerationProvider
{
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x64];
    public string Key => "test";

    public Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VoiceProviderResult(
            Mp3,
            "audio/mpeg",
            "mp3",
            1000,
            24000,
            new VoiceProviderUsage("voice-test-model", request.Text.Length, 11, 0.004m, 3, CostBasis: UsageCostBasis.Actual)));
}

internal sealed class BlockingVoiceProvider : IVoiceGenerationProvider
{
    public string Key => "blocking";

    public async Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation should have ended voice generation.");
    }
}

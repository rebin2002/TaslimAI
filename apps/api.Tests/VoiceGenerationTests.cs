using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Voice;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VoiceProviderApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVoiceGenerationProvider>();
            services.AddSingleton<IVoiceGenerationProvider, TestVoiceGenerationProvider>();
            services.PostConfigure<VoiceGenerationOptions>(options =>
            {
                options.Enabled = true;
                options.ProviderKey = "test";
            });
        });
    }
}

public sealed class VoiceGenerationTests : IClassFixture<GenerationJobsApiFactory>, IClassFixture<VoiceProviderApiFactory>
{
    private readonly GenerationJobsApiFactory unavailableFactory;
    private readonly VoiceProviderApiFactory providerFactory;

    public VoiceGenerationTests(GenerationJobsApiFactory unavailableFactory, VoiceProviderApiFactory providerFactory)
    {
        this.unavailableFactory = unavailableFactory;
        this.providerFactory = providerFactory;
        using var unavailableScope = unavailableFactory.Services.CreateScope();
        unavailableScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var providerScope = providerFactory.Services.CreateScope();
        providerScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Voice_endpoint_creates_durable_job_and_safe_unavailable_result_without_asset_or_customer_charge()
    {
        using var client = unavailableFactory.CreateClient();
        var authResponse = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Voice Tester",
            email = $"voice-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "ku",
        });
        Assert.Equal(HttpStatusCode.OK, authResponse.StatusCode);
        var auth = (await authResponse.Content.ReadFromJsonAsync<AuthResponse>())!;

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "بەخێربێن بۆ تسلیم",
            language = "ku",
            voiceStyle = "warm",
            speakingStyle = "clear",
            instructions = "Speak clearly.",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreateVoiceGenerationResponse>())!;
        Assert.Equal(GenerationJobTypes.VoiceGenerate, created.Job.JobType);

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.VoiceProviderUnavailable, terminal.ErrorCode);
        Assert.DoesNotContain("openai", terminal.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(terminal.Outputs);

        using var scope = unavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Voice, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
    }

    [Fact]
    public async Task Successful_voice_job_publishes_private_project_asset_and_activity_item_with_zero_charge()
    {
        using var client = providerFactory.CreateClient();
        var auth = await Register(client, "Voice Owner", "en");
        var project = await SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", new { name = "Narration Project", type = "General" });
        var created = await SendWithCsrf<CreateVoiceGenerationResponse>(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId = project.Id,
            text = "Hello Taslim",
            language = "en",
            voiceStyle = "warm",
            speakingStyle = "clear",
            instructions = "Speak naturally.",
            title = "Project narration",
        });

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Single(terminal.Outputs);
        Assert.NotNull(terminal.Outputs[0].StoredFileId);
        Assert.DoesNotContain("provider", terminal.ResultJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", terminal.ResultJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        using var scope = providerFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
        Assert.Equal(AssetTypes.Audio, asset.AssetType);
        Assert.Equal(project.Id, asset.ProjectId);
        Assert.Equal(terminal.Outputs[0].StoredFileId, asset.StoredFileId);
        Assert.Equal(project.Id, asset.StoredFile!.ProjectId);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile.Status);
        Assert.Equal("audio/mpeg", asset.StoredFile.ContentType);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Voice, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal("Unreported", usage.CostBasis);

        using var inline = await client.GetAsync($"/api/assets/{asset.Id}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
        Assert.Equal("audio/mpeg", inline.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ID3mock-mp3", await inline.Content.ReadAsStringAsync());
        using var download = await client.GetAsync($"/api/assets/{asset.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.NotNull(download.Content.Headers.ContentDisposition);

        var activity = await client.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=voice.generate");
        var item = Assert.Single(activity!.Items);
        Assert.Equal("voice", item.JobType);
        Assert.Equal(asset.Id, item.AssetId);
    }

    [Fact]
    public async Task Voice_job_enforces_authorization_and_invalid_requests_never_enter_queue()
    {
        using var owner = providerFactory.CreateClient();
        var auth = await Register(owner, "Voice Authorization Owner", "en");
        var invalid = await SendWithCsrf(owner, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "Hello",
            language = "fr",
            voiceStyle = "warm",
            speakingStyle = "clear",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.VoiceRequestInvalid, invalidBody.GetProperty("error").GetProperty("code").GetString());

        using var other = providerFactory.CreateClient();
        var otherAuth = await Register(other, "Voice Authorization Other", "en");
        var forbidden = await SendWithCsrf(other, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "Hello",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.NotEqual(auth.PersonalWorkspace.Id, otherAuth.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Running_voice_job_can_be_cancelled_without_publishing_asset_or_customer_charge()
    {
        using var client = providerFactory.CreateClient();
        var auth = await Register(client, "Voice Cancellation Owner", "en");
        var created = await SendWithCsrf<CreateVoiceGenerationResponse>(client, HttpMethod.Post, "/api/voice-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            text = "cancel this voice request",
            language = "en",
            voiceStyle = "neutral",
            speakingStyle = "clear",
        });

        for (var attempt = 0; attempt < 80; attempt++)
        {
            var current = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
            if (current?.Status == "Running") break;
            await Task.Delay(10);
        }
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.True(cancel.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, await cancel.Content.ReadAsStringAsync());
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Cancelled", terminal.Status);

        using var scope = providerFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName, string preferredLanguage)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"voice-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            if (job?.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job {id} did not reach a terminal state.");
    }
}

internal sealed class TestVoiceGenerationProvider : IVoiceGenerationProvider
{
    public string Key => "test";

    public async Task<VoiceProviderResult> GenerateAsync(VoiceGenerationInput request, CancellationToken cancellationToken = default)
    {
        if (request.Text.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var bytes = "ID3mock-mp3"u8.ToArray();
        return new VoiceProviderResult(
            bytes,
            "audio/mpeg",
            "mp3",
            900,
            24_000,
            new VoiceProviderUsage(Key, request.Text.Length, bytes.Length, null, 1, CostBasis: "Unreported", Currency: "USD"));
    }
}

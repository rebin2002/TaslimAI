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
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MusicAuthorizationApiFactory : GenerationJobsApiFactory
{
    public RejectingMusicPreflight CostControl { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("MusicGeneration:Enabled", "true");
        builder.UseSetting("MusicGeneration:ProviderKey", "synthetic");
        builder.UseSetting("MusicGeneration:Model", "synthetic-music-model");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUsageCostControl>();
            services.AddSingleton(CostControl);
            services.AddSingleton<IUsageCostControl>(services => services.GetRequiredService<RejectingMusicPreflight>());
        });
    }
}

public sealed class RejectingMusicPreflight : IUsageCostControl
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
        return Task.FromResult(new UsagePreflightResult(
            false,
            null,
            "SYNTHETIC_PREFLIGHT_REACHED",
            "The synthetic cost guard should not run before workspace authorization."));
    }

    public Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class MusicGenerationAuthorizationTests : IClassFixture<MusicAuthorizationApiFactory>
{
    private readonly MusicAuthorizationApiFactory factory;

    public MusicGenerationAuthorizationTests(MusicAuthorizationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Non_member_is_forbidden_before_music_cost_preflight_and_queueing()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await RegisterAsync(ownerClient, "Music workspace owner");

        using var intruderClient = factory.CreateClient();
        await RegisterAsync(intruderClient, "Music workspace intruder");

        var response = await SendWithCsrfAsync(intruderClient, new
        {
            workspaceId = owner.PersonalWorkspace.Id,
            description = "This request must not reach the cost guard.",
            purpose = "Unauthorized music request",
            genre = "ambient",
            mood = "calm",
            durationSeconds = 30,
            vocalPreference = "instrumental",
            language = "auto",
            title = "Unauthorized music request",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.CostControl.CallCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.GenerationJobs.AsNoTracking().Where(item => item.WorkspaceId == owner.PersonalWorkspace.Id).ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking().Where(item => item.WorkspaceId == owner.PersonalWorkspace.Id).ToListAsync());
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string displayName)
    {
        var response = await SendWithCsrfAsync(client, new
        {
            displayName,
            email = $"music-auth-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, object payload, string path = "/api/music-generation/jobs") =>
        SendWithCsrfAsyncCore(client, path, payload);

    private static async Task<HttpResponseMessage> SendWithCsrfAsyncCore(HttpClient client, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
}

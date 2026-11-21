using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Taslim.Api.Social;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class SocialPreflightAuthorizationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("SocialGeneration:Enabled", "true");
        builder.UseSetting("SocialGeneration:EstimatedOutputUsdPer1KTokens", "1");
        builder.UseSetting("UsageControls:GuardrailsEnabled", "true");
        builder.UseSetting("UsageControls:MaxEstimatedProviderCostPerGenerationUsd", "0.01");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISocialGenerationProvider>();
            services.AddSingleton<ISocialGenerationProvider, DeterministicSocialProvider>();
        });
    }
}

public sealed class SocialGenerationAuthorizationTests : IClassFixture<SocialPreflightAuthorizationApiFactory>
{
    private readonly SocialPreflightAuthorizationApiFactory factory;

    public SocialGenerationAuthorizationTests(SocialPreflightAuthorizationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Social_creation_denies_cross_workspace_before_cost_preflight()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "social-preflight-owner");
        using var other = factory.CreateClient();
        await Register(other, "social-preflight-other");

        var response = await SendWithCsrf(other, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            prompt = "A valid social announcement",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.WorkspaceId == ownerAuth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Social_creation_keeps_cost_guardrail_for_workspace_member()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "social-preflight-member");

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            prompt = "A valid social announcement",
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("COST_ESTIMATE_EXCEEDS_LIMIT", body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string suffix)
    {
        var response = await SendWithCsrf(client, new
        {
            displayName = "Social Authorization Tester",
            email = $"{suffix}-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/social-generation/jobs", string? idempotencyKey = "social-auth-test-request")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

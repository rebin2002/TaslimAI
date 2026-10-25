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
using Taslim.Api.Research;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchPreflightAuthorizationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ResearchGeneration:Enabled", "true");
        builder.UseSetting("ResearchGeneration:EstimatedOutputUsdPer1KTokens", "1");
        builder.UseSetting("UsageControls:GuardrailsEnabled", "true");
        builder.UseSetting("UsageControls:MaxEstimatedProviderCostPerGenerationUsd", "0.01");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IResearchSearchProvider>();
            services.RemoveAll<IResearchReportProvider>();
            services.AddSingleton<IResearchSearchProvider, FakeResearchSearchProvider>();
            services.AddSingleton<IResearchReportProvider, FakeResearchReportProvider>();
        });
    }
}

public sealed class ResearchGenerationAuthorizationTests : IClassFixture<ResearchPreflightAuthorizationApiFactory>
{
    private readonly ResearchPreflightAuthorizationApiFactory factory;

    public ResearchGenerationAuthorizationTests(ResearchPreflightAuthorizationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Non_member_is_forbidden_before_research_cost_preflight_and_queueing()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "research-preflight-owner");
        using var other = factory.CreateClient();
        await Register(other, "research-preflight-other");

        var response = await SendWithCsrf(other, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            question = "A valid research question",
            title = "Unauthorized research request",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.GenerationJobs.AsNoTracking().Where(job => job.WorkspaceId == ownerAuth.PersonalWorkspace.Id).ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking().Where(usage => usage.WorkspaceId == ownerAuth.PersonalWorkspace.Id).ToListAsync());
    }

    [Fact]
    public async Task Workspace_member_still_reaches_the_research_cost_guardrail()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "research-preflight-member");

        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            question = "A valid research question",
            title = "Guardrailed research request",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = true,
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
            displayName = "Research Authorization Tester",
            email = $"{suffix}-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }, "/api/auth/register");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string path = "/api/research-generation/jobs")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentAuthorizationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DocumentGeneration:Enabled", "true");
        builder.UseSetting("UsageControls:GuardrailsEnabled", "true");
        builder.UseSetting("UsageControls:MaxEstimatedProviderCostPerGenerationUsd", "0.01");
    }
}

public sealed class DocumentGenerationAuthorizationTests : IClassFixture<DocumentAuthorizationApiFactory>
{
    private readonly DocumentAuthorizationApiFactory factory;

    public DocumentGenerationAuthorizationTests(DocumentAuthorizationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Document_create_does_not_expose_foreign_workspace_cost_guardrail_state()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Document Cost Owner");
        using var foreign = factory.CreateClient();
        await Register(foreign, "Document Cost Probe");

        var response = await SendWithCsrf(foreign, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            description = "Create a concise professional report.",
            documentType = "report",
            length = "standard",
            tone = "professional",
            language = "en",
            outputFormat = "pdf",
            attachmentIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.WorkspaceId == ownerAuth.PersonalWorkspace.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"document-auth-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

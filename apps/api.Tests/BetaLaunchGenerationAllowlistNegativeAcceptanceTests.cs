using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// Provider-free P0 acceptance contract for the server-side beta generation boundary.
/// This intentionally fails until the source owner adds the shared allowlist.
public sealed class BetaLaunchGenerationAllowlistNegativeAcceptanceTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public BetaLaunchGenerationAllowlistNegativeAcceptanceTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Theory]
    [InlineData(GenerationJobTypes.ResearchGenerate)]
    [InlineData(GenerationJobTypes.SocialGenerate)]
    [InlineData(GenerationJobTypes.VoiceGenerate)]
    [InlineData(GenerationJobTypes.MusicGenerate)]
    public async Task Excluded_studio_types_are_denied_before_enqueue_and_usage(string jobType)
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"beta-negative-{jobType}-{Guid.NewGuid():N}@example.com");
        var idempotencyKey = $"beta-negative-{jobType}-{Guid.NewGuid():N}";

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType,
            inputJson = "{}",
        }, idempotencyKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BETA_MODULE_NOT_AVAILABLE", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item => item.CreatedByUserId == auth.User.Id && item.IdempotencyKey == idempotencyKey));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item => item.UserId == auth.User.Id && item.RequestId.StartsWith("generation:") && item.Feature != UsageFeature.Chat));
    }

    [Fact]
    public async Task Document_and_presentation_controls_remain_available()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"beta-controls-{Guid.NewGuid():N}@example.com");

        foreach (var jobType in new[] { GenerationJobTypes.DocumentGenerate, GenerationJobTypes.PresentationGenerate })
        {
            var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                jobType,
                inputJson = "{}",
            }, $"beta-control-{jobType}-{Guid.NewGuid():N}");

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Synthetic Beta Acceptance",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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
}

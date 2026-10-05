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

public sealed class ImageGenerationNoWorkerApiFactory : ImageGenerationApiFactory
{
    protected override bool WorkerEnabled => false;
}

public sealed class ImageGenerationConcurrencyTests : IClassFixture<ImageGenerationNoWorkerApiFactory>
{
    private readonly ImageGenerationNoWorkerApiFactory factory;

    public ImageGenerationConcurrencyTests(ImageGenerationNoWorkerApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Active_image_job_rejects_second_submission_without_creating_job_or_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"image-concurrency-{Guid.NewGuid():N}@example.com");
        var request = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image that remains queued while this guard is tested.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        };

        var first = await SendWithCsrf(client, request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A second image must not be queued concurrently.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IMAGE_GENERATION_IN_PROGRESS", body.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("provider", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(job =>
            job.WorkspaceId == auth.PersonalWorkspace.Id && job.JobType == GenerationJobTypes.ImageGenerate));
        Assert.Equal(1, await db.UsageTransactions.AsNoTracking().CountAsync(transaction =>
            transaction.WorkspaceId == auth.PersonalWorkspace.Id && transaction.Feature == UsageFeature.Image));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Image Concurrency Tester",
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageProviderUnavailableApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ImageGeneration:Enabled", "true");
        builder.UseSetting("ImageGeneration:ProviderKey", "openai");
        builder.UseSetting("Ai:OpenAI:Enabled", "false");
        builder.UseSetting("Ai:OpenAI:ApiKey", string.Empty);
    }
}

public sealed class ImageGenerationAvailabilityTests : IClassFixture<GenerationJobsApiFactory>, IClassFixture<ImageProviderUnavailableApiFactory>
{
    private readonly GenerationJobsApiFactory factory;
    private readonly ImageProviderUnavailableApiFactory providerUnavailableFactory;

    public ImageGenerationAvailabilityTests(GenerationJobsApiFactory factory, ImageProviderUnavailableApiFactory providerUnavailableFactory)
    {
        this.factory = factory;
        this.providerUnavailableFactory = providerUnavailableFactory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        using var providerScope = providerUnavailableFactory.Services.CreateScope();
        providerScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Disabled_image_studio_rejects_before_queueing_or_reserving_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"image-disabled-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image should not be queued while Image Studio is disabled.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IMAGE_STUDIO_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job =>
            job.WorkspaceId == auth.PersonalWorkspace.Id && job.JobType == GenerationJobTypes.ImageGenerate));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(transaction =>
            transaction.WorkspaceId == auth.PersonalWorkspace.Id && transaction.Feature == UsageFeature.Image));
    }

    [Fact]
    public async Task Unavailable_configured_provider_rejects_before_queueing_or_reserving_usage()
    {
        using var client = providerUnavailableFactory.CreateClient();
        var auth = await Register(client, $"image-provider-unavailable-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image should not be queued without a configured provider.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ImageProviderUnavailable, body.GetProperty("error").GetProperty("code").GetString());

        using var scope = providerUnavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job =>
            job.WorkspaceId == auth.PersonalWorkspace.Id && job.JobType == GenerationJobTypes.ImageGenerate));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(transaction =>
            transaction.WorkspaceId == auth.PersonalWorkspace.Id && transaction.Feature == UsageFeature.Image));
    }

    [Fact]
    public async Task Invalid_image_request_is_validated_before_unavailable_provider_response()
    {
        using var client = providerUnavailableFactory.CreateClient();
        var auth = await Register(client, $"image-provider-validation-order-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image",
            style = "unsupported-style",
            aspectRatio = "square",
            quality = "standard",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IMAGE_STYLE_UNSUPPORTED", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = providerUnavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(job =>
            job.WorkspaceId == auth.PersonalWorkspace.Id && job.JobType == GenerationJobTypes.ImageGenerate));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(transaction =>
            transaction.WorkspaceId == auth.PersonalWorkspace.Id && transaction.Feature == UsageFeature.Image));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Disabled Image Tester",
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

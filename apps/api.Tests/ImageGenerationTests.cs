using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public class ImageGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ImageGeneration:Enabled", "true");
        builder.UseSetting("ImageGeneration:ProviderKey", "openai");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IImageGenerationProvider>();
            services.AddSingleton<IImageGenerationProvider, DeterministicImageProvider>();
        });
    }
}

public sealed class ImageGenerationTests : IClassFixture<ImageGenerationApiFactory>
{
    private readonly ImageGenerationApiFactory factory;

    public ImageGenerationTests(ImageGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Image_job_reaches_success_persists_private_png_asset_and_records_real_provider_cost()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"image-{Guid.NewGuid():N}@example.com");
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Product hero",
            description = "A refined ceramic cup on a pale stone table for a product page.",
            style = "product",
            aspectRatio = "landscape",
            quality = "high",
            mood = "calm and premium",
            background = "soft morning light",
            textInImage = "Taslim",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provider", responseText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", responseText, StringComparison.OrdinalIgnoreCase);
        var envelope = JsonSerializer.Deserialize<CreateImageGenerationResponse>(responseText, JsonOptions)!;

        var completed = await WaitForTerminal(client, envelope.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), completed.Status);
        Assert.Equal(100, completed.ProgressPercent);
        Assert.Contains("assetId", completed.ResultJson);
        Assert.Single(completed.Outputs);
        Assert.NotNull(completed.Outputs[0].StoredFileId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{envelope.Job.Id:N}");
        Assert.Equal(UsageFeature.Image, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0.03168m, usage.ProviderCostUsd);
        Assert.Equal(0m, usage.ChargedAmount);

        var asset = await db.Assets.AsNoTracking().Include(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == envelope.Job.Id);
        Assert.Equal(AssetTypes.Image, asset.AssetType);
        Assert.Equal("image/png", asset.MimeType);
        Assert.Equal(AssetStatus.Active, asset.Status);
        Assert.Equal(StoredFileStatus.Ready, asset.StoredFile!.Status);
        Assert.StartsWith("image-", asset.StoredFile.OriginalFileName, StringComparison.Ordinal);
        Assert.Equal("image/png", asset.StoredFile.ContentType);
        Assert.Contains("landscape", asset.MetadataJson, StringComparison.Ordinal);
        Assert.Contains("width", asset.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_request_rejects_reference_file_and_unsupported_controls_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"image-validation-{Guid.NewGuid():N}@example.com");
        var reference = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
            referenceFileId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, reference.StatusCode);
        Assert.Equal("IMAGE_REFERENCE_NOT_SUPPORTED", (await reference.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var unsupported = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image",
            style = "secret-provider-style",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal("IMAGE_STYLE_UNSUPPORTED", (await unsupported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var aspect = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image",
            style = "product",
            aspectRatio = "panorama",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.BadRequest, aspect.StatusCode);
        Assert.Equal("IMAGE_ASPECT_RATIO_UNSUPPORTED", (await aspect.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var quality = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image",
            style = "product",
            aspectRatio = "square",
            quality = "ultra",
        });
        Assert.Equal(HttpStatusCode.BadRequest, quality.StatusCode);
        Assert.Equal("IMAGE_QUALITY_UNSUPPORTED", (await quality.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, $"image-project-{Guid.NewGuid():N}@example.com");
        var crossProject = await SendWithCsrf(client, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId = otherAuth.PersonalWorkspace.Id,
            description = "A product image",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);
        Assert.Equal("PROJECT_NOT_IN_WORKSPACE", (await crossProject.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Image_job_requires_workspace_membership_and_project_belongs_to_workspace()
    {
        using var first = factory.CreateClient();
        var firstAuth = await Register(first, $"image-first-{Guid.NewGuid():N}@example.com");
        using var second = factory.CreateClient();
        await Register(second, $"image-second-{Guid.NewGuid():N}@example.com");
        var forbidden = await SendWithCsrf(second, HttpMethod.Post, "/api/image-generation/jobs", new
        {
            workspaceId = firstAuth.PersonalWorkspace.Id,
            description = "A product image",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Image Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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
        throw new TimeoutException($"Image job {id} did not reach a terminal state.");
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed class DeterministicImageProvider : IImageGenerationProvider
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public string Key => "openai";

    public async Task<ImageProviderResult> GenerateAsync(ImageGenerationInput request, ImagePromptBuildResult prompt, CancellationToken cancellationToken = default)
    {
        await Task.Delay(30, cancellationToken);
        return new ImageProviderResult(Png, "image/png", "png", 1, 1, new ImageProviderUsage(100, 80, 0, 1056, 0.03168m, "completed", 12));
    }
}

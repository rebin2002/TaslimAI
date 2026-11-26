using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Images;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageGenerationReferenceBoundaryTests
{
    [Fact]
    public async Task Deferred_reference_id_is_rejected_before_a_reference_capable_provider_is_called()
    {
        var provider = new ReferenceCapableImageProvider();
        var handler = new ImageGenerationJobHandler(
            [provider],
            new TaslimImagePromptBuilder(),
            Options.Create(new ImageGenerationOptions
            {
                Enabled = true,
                ProviderKey = provider.Key,
            }),
            NullLogger<ImageGenerationJobHandler>.Instance);
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.ImageGenerate,
            InputJson = JsonSerializer.Serialize(new ImageGenerationInput(
                "Create a product image using the selected reference.",
                ImageGenerationValues.Product,
                ImageGenerationValues.Square,
                ImageGenerationValues.Standard,
                null,
                null,
                null,
                null,
                null,
                Guid.NewGuid())),
        };

        var exception = await Assert.ThrowsAsync<ImageRequestValidationException>(() =>
            handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.ImageReferenceNotSupported, exception.Code);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Http_admission_rejects_reference_id_before_preflight_job_usage_or_provider_call()
    {
        using var factory = new ReferenceCapableImageAdmissionApiFactory();
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);
        var response = await SendWithCsrfAsync(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A product image using an existing reference.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
            referenceFileId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.ImageReferenceNotSupported, error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Reference images are not available for this image request.", error.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, factory.Preflight.CallCount);
        Assert.Equal(0, factory.Provider.CallCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.GenerationJobs.AsNoTracking()
            .Where(job => job.WorkspaceId == auth.PersonalWorkspace.Id && job.JobType == GenerationJobTypes.ImageGenerate)
            .ToListAsync());
        Assert.Empty(await db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.WorkspaceId == auth.PersonalWorkspace.Id && transaction.Feature == UsageFeature.Image)
            .ToListAsync());
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Reference Admission Tester",
                email = $"image-reference-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/image-generation/jobs")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }

    private sealed class ReferenceCapableImageAdmissionApiFactory : ImageGenerationApiFactory
    {
        public ReferenceCapableImageProvider Provider { get; } = new();
        public CountingImagePreflight Preflight { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ImageGeneration:ProviderKey", Provider.Key);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IImageGenerationProvider>();
                services.AddSingleton<IImageGenerationProvider>(Provider);
                services.RemoveAll<IUsageCostControl>();
                services.AddSingleton<IUsageCostControl>(Preflight);
            });
        }
    }

    private sealed class CountingImagePreflight : IUsageCostControl
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
            return Task.FromResult(new UsagePreflightResult(true, estimatedProviderCostUsd));
        }

        public Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReferenceCapableImageProvider :
        IImageGenerationProvider,
        IImageGenerationProviderCapabilities,
        IImageGenerationProviderReadiness
    {
        public string Key => "synthetic-reference-provider";
        public bool IsAvailable => true;
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);
        public ImageGenerationCapabilities Capabilities => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Square },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ImageGenerationValues.Standard },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" },
            SupportsReferenceImages: true,
            MaxImagesPerRequest: 1);

        public Task<ImageProviderResult> GenerateAsync(
            ImageGenerationInput request,
            ImagePromptBuildResult prompt,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("The provider must not receive an unresolved reference ID.");
        }
    }
}

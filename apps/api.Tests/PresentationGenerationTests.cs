using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PresentationGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PresentationGeneration:Enabled", "true");
        builder.UseSetting("PresentationGeneration:ProviderTimeoutSeconds", "10");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPresentationGenerationProvider>();
            services.AddSingleton<IPresentationGenerationProvider, DeterministicPresentationProvider>();
        });
    }
}

public sealed class PresentationGenerationTests : IClassFixture<PresentationGenerationApiFactory>
{
    private readonly PresentationGenerationApiFactory factory;

    public PresentationGenerationTests(PresentationGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Presentation_job_publishes_one_editable_pptx_asset_with_zero_customer_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Launch story",
            description = "Present the product launch plan to the operating team.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "ku",
            includeAgenda = true,
            includeClosingNextSteps = true,
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Equal(100, terminal.ProgressPercent);
        Assert.Contains("assetId", terminal.ResultJson);
        Assert.Single(terminal.Outputs);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.Include(item => item.Representations).ThenInclude(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
        Assert.Equal(AssetTypes.Presentation, asset.AssetType);
        var representation = Assert.Single(asset.Representations);
        Assert.Equal(AssetRepresentationTypes.Pptx, representation.RepresentationType);
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", representation.ContentType);
        Assert.True(representation.SizeBytes > 500);
        Assert.Equal(StoredFileStatus.Ready, representation.StoredFile.Status);
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == created.Job.Id);
        Assert.Equal(UsageFeature.Presentation, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Presentation_generation_requires_idempotency_key()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Missing key",
            description = "This request must be rejected without a retry-safe key.",
            attachmentIds = Array.Empty<Guid>(),
        }, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Repeated_presentation_request_with_same_idempotency_key_returns_one_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Retry-safe presentation",
            description = "The same request should reuse its durable generation job.",
            presentationType = "business",
            attachmentIds = Array.Empty<Guid>(),
        };

        var first = await SendWithCsrf(client, payload, "presentation-idempotency-001");
        var second = await SendWithCsrf(client, payload, "presentation-idempotency-001");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        var firstJob = (await first.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!.Job;
        var secondJob = (await second.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!.Job;
        Assert.Equal(firstJob.Id, secondJob.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.CountAsync(item => item.IdempotencyKey == "presentation-idempotency-001"));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new { displayName = "Presentation Tester", email = $"presentation-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "ku" });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string? idempotencyKey = "presentation-test-request")
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Presentation job did not reach a terminal state.");
    }
}

internal sealed class DeterministicPresentationProvider : IPresentationGenerationProvider
{
    public Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default)
    {
        var draft = new PresentationDraft
        {
            Title = "چیرۆکی دەستپێکردن",
            Subtitle = "پلانێکی ڕوون",
            Language = "ku",
            PresentationType = "business",
            Slides = [
                new PresentationSlide { Order = 1, Type = PresentationSlideTypes.Title, Title = "چیرۆکی دەستپێکردن", Subtitle = "پێشکەشکردنێکی پیشەیی" },
                new PresentationSlide { Order = 2, Type = PresentationSlideTypes.Bullets, Title = "خاڵە سەرەکییەکان", Blocks = [new PresentationContentBlock { Type = PresentationBlockTypes.Bullets, Items = ["ئامانج", "هەنگاوەکان"] }] },
            ],
        };
        var usage = new AiUsageMetadata("presentation-test", "presentation-test", 100, null, 200, 0.002m, 0.002m, 8, "completed", false, PricingVersion: "presentation-test-v1", Currency: "USD", CostBasis: UsageCostBasis.Actual);
        return Task.FromResult(new PresentationProviderResult(draft, usage));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
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

public sealed class DisabledPresentationGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PresentationGeneration:Enabled", "false");
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
        using var resultDocument = JsonDocument.Parse(terminal.ResultJson!);
        var resultRoot = resultDocument.RootElement;
        Assert.NotEqual(Guid.Empty, resultRoot.GetProperty("assetId").GetGuid());
        var resultRepresentation = Assert.Single(resultRoot.GetProperty("representations").EnumerateArray());
        Assert.Equal("pptx", resultRepresentation.GetProperty("type").GetString());
        Assert.True(resultRepresentation.GetProperty("fileName").GetString()?.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("application/vnd.openxmlformats-officedocument.presentationml.presentation", resultRepresentation.GetProperty("contentType").GetString());
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
    public async Task Presentation_job_cannot_process_another_workspace_members_private_file()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner);
        var upload = await Upload(owner, ownerAuth.PersonalWorkspace.Id, "private-notes.txt", "PRIVATE_PRESENTATION_SOURCE");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var source = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;

        using var member = factory.CreateClient();
        var memberAuth = await Register(member);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(member, new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            title = "Private source presentation",
            description = "This job must not read another member's private file.",
            presentationType = "business",
            attachmentIds = new[] { source.Id },
        }, $"presentation-private-source-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;

        var terminal = await WaitForTerminal(member, created.Job.Id);
        Assert.Equal("Failed", terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.PresentationAttachmentUnavailable, terminal.ErrorCode);
        Assert.Empty(terminal.Outputs);

        using var verification = factory.Services.CreateScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await verificationDb.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
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

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, string name, string content)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
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

public sealed class PresentationGenerationAvailabilityTests : IClassFixture<DisabledPresentationGenerationApiFactory>
{
    private readonly DisabledPresentationGenerationApiFactory factory;

    public PresentationGenerationAvailabilityTests(DisabledPresentationGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Invalid_request_is_rejected_before_disabled_studio_response_without_creating_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Invalid presentation",
            description = "x",
            attachmentIds = Array.Empty<Guid>(),
        }, "presentation-invalid-disabled-001");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.PresentationRequestInvalid, body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.False(await db.UsageTransactions.AnyAsync(item => item.UserId == auth.User.Id && item.Feature == UsageFeature.Presentation));
    }

    [Fact]
    public async Task Valid_request_still_returns_disabled_studio_response_without_creating_job()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await SendWithCsrf(client, new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Valid presentation",
            description = "Present the validated launch plan.",
            attachmentIds = Array.Empty<Guid>(),
        }, "presentation-valid-disabled-001");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PRESENTATION_STUDIO_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AnyAsync(item => item.CreatedByUserId == auth.User.Id));
        Assert.False(await db.UsageTransactions.AnyAsync(item => item.UserId == auth.User.Id && item.Feature == UsageFeature.Presentation));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new { displayName = "Presentation Availability Tester", email = $"presentation-disabled-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, object payload, string idempotencyKey)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/presentation-generation/jobs");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
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

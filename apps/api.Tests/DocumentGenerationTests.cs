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
using Taslim.Api.Documents;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentGenerationApiFactory : GenerationJobsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DocumentGeneration:Enabled", "true");
        builder.UseSetting("DocumentGeneration:RequestedTier", "Smart");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDocumentGenerationProvider>();
            services.AddSingleton<IDocumentGenerationProvider, DeterministicDocumentProvider>();
        });
    }
}

public sealed class DocumentGenerationTests : IClassFixture<DocumentGenerationApiFactory>
{
    private readonly DocumentGenerationApiFactory factory;

    public DocumentGenerationTests(DocumentGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Document_job_renders_both_formats_under_one_asset_and_records_zero_customer_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var upload = await Upload(client, auth.PersonalWorkspace.Id, "brief.txt", "Taslim is a calm workspace. The launch goal is clarity.");
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var source = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Ready", source.TextExtractionStatus);

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Launch brief",
            prompt = "Create a concise professional brief for the launch team.",
            attachmentIds = new[] { source.Id },
            language = "en",
            outputFormat = "both",
            tone = "professional",
            includeTableOfContents = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>();
        Assert.NotNull(body);
        var job = await WaitForTerminal(client, body!.Job.Id);
        Assert.True(job.Status == GenerationJobStatus.Succeeded.ToString(), $"Status={job.Status}; Code={job.ErrorCode}; Message={job.ErrorMessage}");
        Assert.Equal(100, job.ProgressPercent);
        Assert.Contains("assetId", job.ResultJson);
        Assert.Equal(2, job.Outputs.Count);
        Assert.All(job.Outputs, output => Assert.NotNull(output.StoredFileId));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.Include(item => item.Representations).ThenInclude(item => item.StoredFile).SingleAsync(item => item.SourceGenerationJobId == body.Job.Id);
        Assert.Equal(AssetTypes.Document, asset.AssetType);
        Assert.Equal(2, asset.Representations.Count);
        Assert.Contains(asset.Representations, item => item.RepresentationType == AssetRepresentationTypes.Docx && item.StoredFile.ContentType.Contains("wordprocessingml"));
        Assert.Contains(asset.Representations, item => item.RepresentationType == AssetRepresentationTypes.Pdf && item.StoredFile.ContentType == "application/pdf");
        Assert.All(asset.Representations, item => Assert.Equal(StoredFileStatus.Ready, item.StoredFile.Status));

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{body.Job.Id:N}");
        Assert.Equal(UsageFeature.Document, usage.Feature);
        Assert.Equal(UsageTransactionStatus.Completed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal("document-test", usage.Provider);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
    }

    [Fact]
    public async Task Document_request_rejects_unsupported_format_and_missing_source_before_queueing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var unsupported = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Invalid",
            prompt = "Create a useful brief.",
            attachmentIds = new[] { Guid.NewGuid() },
            outputFormat = "html",
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal(GenerationJobErrorCodes.DocumentRequestInvalid, (await unsupported.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Document_representation_download_is_authenticated_and_safe()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner);
        var upload = await Upload(owner, auth.PersonalWorkspace.Id, "notes.txt", "A short source note.");
        var source = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;
        var response = await SendWithCsrf(owner, HttpMethod.Post, "/api/document-generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, title = "Notes", prompt = "Create a short document.", attachmentIds = new[] { source.Id }, outputFormat = "pdf" });
        var created = await response.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>();
        var job = await WaitForTerminal(owner, created!.Job.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var asset = await db.Assets.Include(item => item.Representations).SingleAsync(item => item.SourceGenerationJobId == created.Job.Id);
        var representation = Assert.Single(asset.Representations);
        var download = await owner.GetAsync($"/api/assets/{asset.Id}/representations/{representation.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.True((await download.Content.ReadAsByteArrayAsync()).Length > 100);

        using var other = factory.CreateClient();
        await Register(other);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/assets/{asset.Id}/representations/{representation.Id}/download")).StatusCode);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Document Tester", email = $"document-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
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

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        var final = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
        throw new TimeoutException($"Document job {id} did not reach a terminal state. Status={final?.Status}; Progress={final?.ProgressPercent}; Code={final?.ErrorCode}; Message={final?.ErrorMessage}");
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

internal sealed class DeterministicDocumentProvider : IDocumentGenerationProvider
{
    public Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
    {
        var draft = new DocumentDraft
        {
            Title = "Generated launch brief",
            Summary = "A concise brief generated from the selected source.",
            Sections = [new DocumentSection { Heading = "Key points", Blocks = [new DocumentBlock { Type = DocumentBlockTypes.BulletList, Items = ["Clarity is the launch goal.", "Taslim supports a calm workspace."] }] }],
        };
        var usage = new AiUsageMetadata("document-test", "document-test", 80, null, 120, 0.0012m, 0.0012m, 8, "completed", false, PricingVersion: "document-test-v1", Currency: "USD", CostBasis: UsageCostBasis.Actual);
        return Task.FromResult(new DocumentProviderResult(draft, usage));
    }
}

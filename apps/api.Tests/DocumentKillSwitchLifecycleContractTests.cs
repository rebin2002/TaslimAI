using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA lane for the Document Studio kill switch (draft PR #1005, head
/// <c>012382ca</c>). PR #1005 pins the fail-CLOSED half of the contract: create (both
/// routes), retry (route and service) and the worker claim all refuse to start or resume
/// provider spend while <c>DocumentGeneration:Enabled</c> is off.
///
/// This file pins the other half — the surfaces that must KEEP WORKING while the switch is
/// off, because they neither create nor resume provider work:
///
///   * cancel stays open, so pulling the switch still lets a caller drain an already queued
///     document job instead of being stranded with it;
///   * get and list stay open, so pre-existing document history is never hidden by an
///     availability toggle;
///   * asset download stays open, so output paid for before the switch was pulled remains
///     retrievable and is never refused with the kill-switch envelope.
///
/// None of that is asserted anywhere in PR #1005's own suite, and it is not hypothetical:
/// extending the same gate to the cancel route (a plausible follow-up, since cancel sits two
/// methods away from the retry route that #1005 does gate) leaves all 61 of #1005's targeted
/// tests green while making queued document jobs permanently un-cancellable. This lane fails
/// on that change.
///
/// Every fixture here is synthetic: a worker-less host, no provider invoked, no credit
/// charged, and no production file touched.
/// </summary>
public sealed class DocumentKillSwitchLifecycleContractTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    /// <summary>
    /// Spelled out rather than referenced from <c>DocumentStudioAvailability</c> so this
    /// lane compiles against current <c>main</c> as well as against PR #1005's head. The
    /// shared-constants form is noted in the PR thread as the follow-up once #1005 lands.
    /// </summary>
    private const string StudioUnavailableCode = "DOCUMENT_STUDIO_UNAVAILABLE";

    private const string StudioUnavailableMessage = "Document generation is not available right now.";

    private readonly GenerationJobsNoWorkerFactory factory;

    public DocumentKillSwitchLifecycleContractTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Cancel_remains_available_for_a_queued_document_job_while_the_studio_is_disabled()
    {
        // The shipped appsettings default keeps the studio disabled, and this fixture
        // mirrors it with the worker off, so the queued row below is exactly the
        // stranded-job situation an operator faces after pulling the switch.
        Assert.False(StudioEnabled());

        using var client = factory.CreateClient();
        var auth = await Register(client, $"killswitch-cancel-{Guid.NewGuid():N}@example.com");
        var jobId = await SeedDocumentJob(auth, GenerationJobStatus.Queued);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{jobId}/cancel");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(nameof(GenerationJobStatus.Cancelled), body.GetProperty("status").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var job = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Cancelled, job.Status);
        Assert.Equal(GenerationJobErrorCodes.DocumentCancelled, job.ErrorCode);
        Assert.NotNull(job.CancelledAt);
    }

    [Fact]
    public async Task Failed_document_job_stays_readable_through_get_and_list_while_the_studio_is_disabled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"killswitch-read-{Guid.NewGuid():N}@example.com");
        var jobId = await SeedDocumentJob(auth, GenerationJobStatus.Failed);

        var get = await client.GetAsync($"/api/generation/jobs/{jobId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getBody = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(jobId.ToString(), getBody.GetProperty("id").GetString());
        Assert.Equal(nameof(GenerationJobStatus.Failed), getBody.GetProperty("status").GetString());

        var list = await client.GetAsync($"/api/generation/jobs?workspaceId={auth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.DocumentGenerate}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(listBody.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString() == jobId.ToString());
    }

    [Fact]
    public async Task Cancelling_a_queued_document_job_while_disabled_is_terminal_and_keeps_the_dedicated_route_refused()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"killswitch-aftercancel-{Guid.NewGuid():N}@example.com");
        var jobId = await SeedDocumentJob(auth, GenerationJobStatus.Queued);

        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{jobId}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        // Drain-once is not an admission path: the dedicated Document Studio route keeps
        // the same bounded unavailable contract after a cancellation on that workspace.
        var dedicated = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A document brief that must stay refused after a cancellation while the studio is disabled.",
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dedicated.StatusCode);
        var body = await dedicated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(StudioUnavailableCode, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(StudioUnavailableMessage, body.GetProperty("error").GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        // Cancelled is terminal: the only job row on the workspace is the seeded one.
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Document_asset_download_stays_served_while_the_studio_is_disabled()
    {
        // Same DI container as the refused routes, so DocumentGeneration:Enabled is
        // provably off while this request is handled. Storage is a synthetic in-memory
        // provider; nothing is read from or written to real private storage.
        Assert.False(StudioEnabled());

        // A real account supplies the workspace and user the foreign keys demand; the
        // document, its stored file and its bytes are all synthetic.
        using var client = factory.CreateClient();
        var auth = await Register(client, $"killswitch-download-{Guid.NewGuid():N}@example.com");
        var userId = auth.User.Id;
        var workspaceId = auth.PersonalWorkspace.Id;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var syntheticPdf = Encoding.UTF8.GetBytes("%PDF-1.4 synthetic QA fixture, not a real document.");

        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Succeeded,
            Title = "Completed before the switch",
            InputJson = "{}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            CompletedAt = DateTime.UtcNow.AddMinutes(-9),
            ConcurrencyToken = Guid.NewGuid(),
        });
        db.StoredFiles.Add(new StoredFile
        {
            Id = storedFileId,
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = "qa-synthetic-document.pdf",
            StoredFileName = "qa-synthetic-document.pdf",
            ContentType = "application/pdf",
            Extension = ".pdf",
            SizeBytes = syntheticPdf.LongLength,
            StorageProvider = FileStorageProviders.Local,
            StorageKey = $"qa/1005/{assetId:N}.pdf",
            Status = StoredFileStatus.Ready,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
        });
        db.Assets.Add(new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            StoredFileId = storedFileId,
            SourceGenerationJobId = jobId,
            Name = "QA synthetic document",
            AssetType = AssetTypes.Document,
            MimeType = "application/pdf",
            Status = AssetStatus.Active,
            CreatedAt = DateTime.UtcNow.AddMinutes(-9),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-9),
        });
        await db.SaveChangesAsync();

        var controller = new AssetsController(
            scope.ServiceProvider.GetRequiredService<IAssetService>(),
            new InMemoryStorage(syntheticPdf),
            NullLogger<AssetsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        authenticationType: "test")),
                },
            },
        };

        var result = await controller.Download(assetId, cancellationToken: CancellationToken.None);

        // The switch guards boundaries that start or resume provider work; an already
        // published asset is neither. The download must stream, and must never be
        // refused with the kill-switch envelope (an ObjectResult error).
        var file = Assert.IsType<FileStreamResult>(result);
        await using var stream = file.FileStream;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(syntheticPdf, buffer.ToArray());
        Assert.Equal("application/pdf", file.ContentType);
        Assert.IsNotType<ObjectResult>(result);
    }

    private bool StudioEnabled()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IOptions<DocumentGenerationOptions>>().Value.Enabled;
    }

    private async Task<Guid> SeedDocumentJob(AuthResponse auth, GenerationJobStatus status)
    {
        var jobId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = status,
            Title = "Kill-switch lifecycle QA fixture",
            InputJson = DocumentInput(auth.PersonalWorkspace.Id),
            CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            QueuedAt = status == GenerationJobStatus.Queued ? DateTime.UtcNow.AddMinutes(-1) : null,
            FailedAt = status == GenerationJobStatus.Failed ? DateTime.UtcNow.AddSeconds(-30) : null,
            ErrorCode = status == GenerationJobStatus.Failed ? GenerationJobErrorCodes.DocumentProviderUnavailable : null,
            ErrorMessage = status == GenerationJobStatus.Failed ? "Document generation is temporarily unavailable. Please try again later." : null,
            ConcurrencyToken = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return jobId;
    }

    private static string DocumentInput(Guid workspaceId) => DocumentGenerationContractMapper.SerializeInput(new DocumentGenerationInput(
        workspaceId,
        null,
        "Lifecycle QA fixture",
        "Create a safe document for this deterministic test.",
        "report",
        "standard",
        null,
        null,
        [],
        "en",
        "pdf",
        "professional",
        true));

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Kill Switch Lifecycle QA",
            email,
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private sealed class InMemoryStorage(byte[] content) : IFileStorageService
    {
        public string ProviderKey => FileStorageProviders.Local;

        public Task StoreAsync(string storageKey, Stream stream, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream(content));

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}

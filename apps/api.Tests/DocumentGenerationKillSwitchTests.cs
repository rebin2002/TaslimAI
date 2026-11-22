using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The Document Studio kill switch has to hold at every boundary that can start or
/// resume provider work. Guarding only the dedicated Document Studio route still
/// lets a caller queue the same job through the generic Generation Job route,
/// resume a failed job through retry, or let an already queued job reach the paid
/// provider when the worker claims it after the switch was pulled.
/// </summary>
public sealed class DocumentGenerationKillSwitchHandlerTests
{
    [Fact]
    public async Task Disabled_studio_fails_a_queued_job_before_reaching_the_provider()
    {
        var provider = new CountingDocumentProvider();
        var handler = new DocumentGenerationJobHandler(
            null!,
            null!,
            provider,
            null!,
            Options.Create(new DocumentGenerationOptions { Enabled = false }));
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.DocumentGenerate,
            // The payload shape is irrelevant here: the switch must be read before
            // the handler deserializes, authorizes, renders, or calls anything.
            InputJson = "{}",
        };

        var exception = await Assert.ThrowsAsync<DocumentGenerationStageException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(DocumentGenerationStages.Validation, exception.Stage);
        Assert.Equal(GenerationJobErrorCodes.DocumentStudioUnavailable, exception.Code);
        Assert.Equal("Document generation is not available right now.", exception.Message);
        Assert.Null(exception.Usage);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Enabled_studio_still_reaches_request_validation()
    {
        var provider = new CountingDocumentProvider();
        var handler = new DocumentGenerationJobHandler(
            null!,
            null!,
            provider,
            null!,
            Options.Create(new DocumentGenerationOptions { Enabled = true }));
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            JobType = GenerationJobTypes.DocumentGenerate,
            InputJson = "{}",
        };

        // Contrast case: the empty payload now fails on the workspace rule instead
        // of the availability rule, proving the gate is conditional on the switch
        // rather than a blanket rejection of every document job.
        var exception = await Assert.ThrowsAsync<DocumentGenerationStageException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal("WORKSPACE_REQUIRED", exception.Code);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Kill_switch_code_maps_to_a_bounded_user_safe_message()
    {
        var message = GenerationJobErrorMessages.For(GenerationJobErrorCodes.DocumentStudioUnavailable);

        Assert.Equal("Document generation is not available right now.", message);
        Assert.DoesNotContain("provider", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CountingDocumentProvider : IDocumentGenerationProvider
    {
        public int CallCount { get; private set; }

        public Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The document provider must not be called while Document Studio is disabled.");
        }
    }
}

/// <summary>
/// Document Studio stays disabled in this fixture, matching the shipped
/// <c>appsettings.json</c> default, so the generic route must refuse document work.
/// </summary>
public sealed class DocumentGenerationDisabledRouteTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public DocumentGenerationDisabledRouteTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Generic_route_rejects_a_document_job_before_queueing_or_reserving_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"document-disabled-create-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.DocumentGenerate,
            title = "Disabled document",
            inputJson = DocumentInput(auth.PersonalWorkspace.Id),
        });

        await AssertUnavailable(response);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.GenerationJobs.AsNoTracking().AnyAsync(item =>
            item.WorkspaceId == auth.PersonalWorkspace.Id && item.JobType == GenerationJobTypes.DocumentGenerate));
        Assert.False(await db.UsageTransactions.AsNoTracking().AnyAsync(item =>
            item.WorkspaceId == auth.PersonalWorkspace.Id && item.Feature == UsageFeature.Document));
    }

    [Fact]
    public async Task Retry_rejects_a_document_job_without_consuming_the_idempotency_key()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"document-disabled-retry-{Guid.NewGuid():N}@example.com");
        var sourceId = await SeedFailedDocumentJob(auth);

        const string retryKey = "document-disabled-retry-001";
        var first = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{sourceId}/retry", null, retryKey);
        var second = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{sourceId}/retry", null, retryKey);

        await AssertUnavailable(first);
        await AssertUnavailable(second);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        // Only the seeded source job exists, the key is unconsumed, and no usage
        // reservation was written. The rejected attempt left nothing behind, so the
        // caller can replay the same key once the studio is re-enabled without
        // colliding with an IDEMPOTENCY_KEY_REUSED rejection.
        Assert.Equal(1, await verifyDb.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
        Assert.False(await verifyDb.GenerationJobs.AsNoTracking().AnyAsync(item => item.IdempotencyKey == retryKey));
        Assert.False(await verifyDb.UsageTransactions.AsNoTracking().AnyAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Dedicated_and_generic_routes_agree_on_the_unavailable_contract()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"document-disabled-contract-{Guid.NewGuid():N}@example.com");

        var dedicated = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A document brief that must be refused while Document Studio is disabled.",
        });
        var generic = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.DocumentGenerate,
            title = "Disabled document",
            inputJson = DocumentInput(auth.PersonalWorkspace.Id),
        });

        Assert.Equal(dedicated.StatusCode, generic.StatusCode);
        var dedicatedBody = await dedicated.Content.ReadFromJsonAsync<JsonElement>();
        var genericBody = await generic.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            dedicatedBody.GetProperty("error").GetProperty("code").GetString(),
            genericBody.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            dedicatedBody.GetProperty("error").GetProperty("message").GetString(),
            genericBody.GetProperty("error").GetProperty("message").GetString());
    }

    private async Task<Guid> SeedFailedDocumentJob(AuthResponse auth)
    {
        var sourceId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = sourceId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Failed,
            Title = "Disabled document source",
            InputJson = DocumentInput(auth.PersonalWorkspace.Id),
            ErrorCode = GenerationJobErrorCodes.DocumentProviderUnavailable,
            ErrorMessage = "Document generation is temporarily unavailable. Please try again later.",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            FailedAt = DateTime.UtcNow.AddSeconds(-30),
            ConcurrencyToken = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return sourceId;
    }

    private static string DocumentInput(Guid workspaceId) => DocumentGenerationContractMapper.SerializeInput(new DocumentGenerationInput(
        workspaceId,
        null,
        "Disabled document",
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

    private static async Task AssertUnavailable(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(GenerationJobErrorCodes.DocumentStudioUnavailable, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Document generation is not available right now.", body.GetProperty("error").GetProperty("message").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Document Kill Switch",
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

/// <summary>
/// Contrast fixture: with Document Studio opted in, the same create and retry
/// requests must still be admitted, so a passing rejection test cannot be
/// satisfied by a blanket block on the route.
/// </summary>
public sealed class DocumentGenerationEnabledRouteTests : IClassFixture<DocumentEnabledNoWorkerFactory>
{
    private readonly DocumentEnabledNoWorkerFactory factory;

    public DocumentGenerationEnabledRouteTests(DocumentEnabledNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Generic_route_still_queues_a_document_job_when_enabled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"document-enabled-create-{Guid.NewGuid():N}@example.com");

        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.DocumentGenerate,
            title = "Enabled document",
            inputJson = DocumentInput(auth.PersonalWorkspace.Id),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Retry_still_accepts_a_document_job_when_enabled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"document-enabled-retry-{Guid.NewGuid():N}@example.com");
        var sourceId = await SeedFailedDocumentJob(auth);

        const string retryKey = "document-enabled-retry-001";
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{sourceId}/retry", null, retryKey);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Queued", body.GetProperty("status").GetString());
        Assert.Equal(sourceId.ToString(), body.GetProperty("retryOfJobId").GetString());
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var retry = await verifyDb.GenerationJobs.AsNoTracking().SingleAsync(item => item.RetryOfJobId == sourceId);
        Assert.Equal(retryKey, retry.IdempotencyKey);
        Assert.Equal(1, retry.RetryCount);
    }

    private async Task<Guid> SeedFailedDocumentJob(AuthResponse auth)
    {
        var sourceId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = sourceId,
            WorkspaceId = auth.PersonalWorkspace.Id,
            CreatedByUserId = auth.User.Id,
            JobType = GenerationJobTypes.DocumentGenerate,
            Status = GenerationJobStatus.Failed,
            Title = "Enabled document source",
            InputJson = DocumentInput(auth.PersonalWorkspace.Id),
            ErrorCode = GenerationJobErrorCodes.DocumentProviderUnavailable,
            ErrorMessage = "Document generation is temporarily unavailable. Please try again later.",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            FailedAt = DateTime.UtcNow.AddSeconds(-30),
            ConcurrencyToken = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
        return sourceId;
    }

    private static string DocumentInput(Guid workspaceId) => DocumentGenerationContractMapper.SerializeInput(new DocumentGenerationInput(
        workspaceId,
        null,
        "Enabled document",
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
            displayName = "Document Availability",
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

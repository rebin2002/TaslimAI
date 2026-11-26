using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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


/// <summary>
/// Exercises the real worker claim/finalization path with Document Studio disabled.
/// This proves that an already-queued job is terminally rejected without reaching a
/// provider and that any usage reservation is finalized without a customer charge.
/// </summary>
public sealed class DocumentKillSwitchClaimOutcomeFactory : GenerationJobsApiFactory
{
    public KillSwitchCountingDocumentProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDocumentGenerationProvider>();
            services.AddSingleton<IDocumentGenerationProvider>(Provider);
        });
    }
}

/// <summary>A provider that fails the test if disabled work reaches it.</summary>
public sealed class KillSwitchCountingDocumentProvider : IDocumentGenerationProvider
{
    private int callCount;

    public int CallCount => Volatile.Read(ref callCount);

    public Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        throw new InvalidOperationException("The document provider must not be reached while Document Studio is disabled.");
    }
}

public sealed class DocumentKillSwitchClaimOutcomeTests : IClassFixture<DocumentKillSwitchClaimOutcomeFactory>
{
    private readonly DocumentKillSwitchClaimOutcomeFactory factory;

    public DocumentKillSwitchClaimOutcomeTests(DocumentKillSwitchClaimOutcomeFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Queued_document_job_claimed_after_the_switch_is_pulled_fails_closed_with_zero_charge_and_no_provider_call()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"killswitch-claim-{Guid.NewGuid():N}@example.com");
        Assert.False(StudioEnabled());

        // Seed a job that was queued before the switch was pulled. The worker is active,
        // but every admission route correctly refuses new document work while disabled.
        var jobId = Guid.NewGuid();
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            seedDb.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.DocumentGenerate,
                Status = GenerationJobStatus.Queued,
                Title = "Queued before the switch was pulled",
                InputJson = DocumentInput(auth.PersonalWorkspace.Id),
                CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                QueuedAt = DateTime.UtcNow.AddMinutes(-1),
                ConcurrencyToken = Guid.NewGuid(),
            });
            await seedDb.SaveChangesAsync();
        }

        var terminal = await WaitForTerminalStatusAsync(client, jobId);

        Assert.Equal(nameof(GenerationJobStatus.Failed), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.DocumentStudioUnavailable, terminal.ErrorCode);
        Assert.Equal("Document generation is not available right now.", terminal.ErrorMessage);
        Assert.DoesNotContain("provider", terminal.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", terminal.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.Provider.CallCount);

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var usage = await db.UsageTransactions.AsNoTracking()
            .SingleAsync(item => item.RequestId == $"generation:{jobId:N}");
        Assert.Equal(UsageTransactionStatus.Failed, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    private bool StudioEnabled()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IOptions<DocumentGenerationOptions>>().Value.Enabled;
    }

    private static async Task<GenerationJobDto> WaitForTerminalStatusAsync(HttpClient client, Guid jobId)
    {
        GenerationJobDto? job = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job is not null && job.Status is "Failed" or "Succeeded" or "Cancelled") return job;
            await Task.Delay(50);
        }
        Assert.Fail($"The claimed document job never reached a terminal state. JobId={jobId}; LastStatus={job?.Status ?? "<null>"}");
        throw new InvalidOperationException("unreachable");
    }

    private static string DocumentInput(Guid workspaceId) => DocumentGenerationContractMapper.SerializeInput(new DocumentGenerationInput(
        workspaceId,
        null,
        "Claim-boundary QA fixture",
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
            displayName = "Kill Switch Claim QA",
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
}

/// <summary>
/// Confirms that disabling Document Studio blocks new or resumed provider work without
/// disabling legitimate cancellation, job-history, or already-published asset access.
/// All rows and file bytes in these cases are synthetic and provider-free.
/// </summary>
public sealed class DocumentKillSwitchLifecycleContractTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
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

        var dedicated = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            description = "A synthetic document brief that must stay refused while Document Studio is disabled.",
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dedicated.StatusCode);
        var body = await dedicated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(StudioUnavailableCode, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(StudioUnavailableMessage, body.GetProperty("error").GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    [Fact]
    public async Task Document_asset_download_stays_served_while_the_studio_is_disabled()
    {
        Assert.False(StudioEnabled());

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

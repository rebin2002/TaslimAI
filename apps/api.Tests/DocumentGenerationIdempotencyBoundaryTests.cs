using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Abuse-boundary regression for the Document Studio create route.
///
/// docs/DOCUMENT_STUDIO_ARCHITECTURE.md states that a Document retry "must be coupled to an
/// idempotency key ... before it can safely create another draft, render, and Asset publication
/// attempt", and the two sibling expensive-AI creation routes already enforce that boundary at
/// admission (Presentation #52, Social). <c>POST /api/document-generation/jobs</c> forwarded the
/// header when present but accepted a request with none, so a submission that is retried after the
/// first attempt already landed - a dropped connection, a client-level retry, or a scripted caller -
/// silently queued a second full generation: a second provider call against the workspace cost
/// ceiling and a second Asset for the same document.
///
/// The shipped web client always sends a key (apps/web/src/lib/api.ts
/// createDocumentGenerationJob(..., idempotencyKey = requestId())), so requiring it fails closed
/// instead of breaking a legitimate caller. These facts pin the admission boundary with the repo's
/// deterministic provider: nothing here contacts a real provider and the customer charge stays zero.
/// </summary>
public sealed class DocumentGenerationIdempotencyBoundaryTests : IClassFixture<DocumentGenerationApiFactory>
{
    private readonly DocumentGenerationApiFactory factory;

    public DocumentGenerationIdempotencyBoundaryTests(DocumentGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Create_without_idempotency_key_is_rejected_before_queueing_or_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);

        var response = await Send(client, auth.PersonalWorkspace.Id, idempotencyKey: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
        await AssertNothingWasQueued(auth.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Create_with_blank_idempotency_key_is_rejected_before_queueing_or_usage()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);

        var response = await Send(client, auth.PersonalWorkspace.Id, idempotencyKey: " ");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", body.GetProperty("error").GetProperty("code").GetString());
        await AssertNothingWasQueued(auth.PersonalWorkspace.Id);
    }

    [Fact]
    public async Task Create_retried_with_the_same_idempotency_key_queues_exactly_one_job_and_one_asset()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var idempotencyKey = $"document-retry-{Guid.NewGuid():N}";

        var first = await Send(client, auth.PersonalWorkspace.Id, idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var firstJob = (await first.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;

        var retry = await Send(client, auth.PersonalWorkspace.Id, idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryJob = (await retry.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;

        Assert.Equal(firstJob.Job.Id, retryJob.Job.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));

        // One queued job means one provider call, one render pass and one published Asset.
        var terminal = await WaitForTerminal(client, firstJob.Job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded.ToString(), terminal.Status);
        Assert.Equal(1, await db.Assets.AsNoTracking().CountAsync(item => item.SourceGenerationJobId == firstJob.Job.Id));
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{firstJob.Job.Id:N}");
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact]
    public async Task Create_with_distinct_idempotency_keys_still_queues_the_deliberate_second_document()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);

        var first = await Send(client, auth.PersonalWorkspace.Id, $"document-distinct-a-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var second = await Send(client, auth.PersonalWorkspace.Id, $"document-distinct-b-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        var firstJob = (await first.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
        var secondJob = (await second.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
        Assert.NotEqual(firstJob.Job.Id, secondJob.Job.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(2, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    private async Task AssertNothingWasQueued(Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await db.GenerationJobs.AsNoTracking().CountAsync(item => item.WorkspaceId == workspaceId));
        Assert.Equal(0, await db.UsageTransactions.AsNoTracking().CountAsync(item => item.WorkspaceId == workspaceId));
    }

    private async Task<HttpResponseMessage> Send(HttpClient client, Guid workspaceId, string? idempotencyKey)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/document-generation/jobs")
        {
            Content = JsonContent.Create(new
            {
                workspaceId,
                title = "Retry safety brief",
                description = "Create a concise professional brief that proves retry safety.",
                documentType = "report",
                length = "short",
                tone = "professional",
                language = "en",
                outputFormat = "docx",
                attachmentIds = Array.Empty<Guid>(),
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Document Idempotency Tester",
                email = $"document-idempotency-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{id}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Document job {id} did not reach a terminal state.");
    }
}

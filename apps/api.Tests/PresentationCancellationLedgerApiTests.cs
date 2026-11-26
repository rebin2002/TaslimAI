using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Xunit;

namespace Taslim.Api.Tests;

// Direct database/usage-ledger terminal-state assertion for the Presentation Studio
// cancellation-vs-late-completion defect (#564/#565 family, #1013/#1051). The existing
// targeted tests (PresentationCancellationPublicationTests, PresentationGenerationProviderTests)
// prove the handler-level behavior through a zero-render observation; this regression closes
// the acceptance gap by driving the real API pipeline end to end - durable cancel endpoint,
// worker cancellation monitor, the production AiPresentationGenerationProvider re-assert point,
// and the worker's CancelRunningAsync finalization - and then reading the persisted terminal
// state directly: the job is Cancelled (never Failed, never Succeeded), no generated file was
// published as an output or asset, and the usage ledger row is finalized as Cancelled with a
// zero customer charge. Synthetic and deterministic: the scripted chat service emits its
// provider response only after the job's cancellation token has actually fired, so the
// response is always a late completion; no live provider call, no sleep-driven race, no real
// billing, no production data.
public sealed class PresentationCancellationLedgerApiFactory : GenerationJobsApiFactory
{
    public LateCompletionPresentationChatService ChatService { get; } = new();

    public RenderCounter Counter { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PresentationGeneration:Enabled", "true");
        builder.UseSetting("PresentationGeneration:ProviderTimeoutSeconds", "30");
        builder.ConfigureServices(services =>
        {
            // Keep the production AiPresentationGenerationProvider registered as
            // IPresentationGenerationProvider: the cancellation re-assert under test is
            // that class's post-completion ThrowIfCancellationRequested. Only the chat
            // completion beneath it is scripted.
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(ChatService);
            // Decorate the real renderer purely to observe whether a late completion was
            // ever taken into parse/validate/render - the exact work the provider's
            // post-completion re-assert exists to prevent. Rendering is the last step
            // before generated-file publication.
            services.RemoveAll<IPresentationRenderer>();
            services.AddScoped<IPresentationRenderer>(_ => new CountingPresentationRenderer(new PresentationRenderer(), Counter));
        });
    }
}

public sealed class RenderCounter
{
    private int count;

    public int Count => Volatile.Read(ref count);

    public void Increment() => Interlocked.Increment(ref count);
}

public sealed class CountingPresentationRenderer(PresentationRenderer inner, RenderCounter counter) : IPresentationRenderer
{
    public RenderedPresentation Render(PresentationDraft draft, PresentationGenerationInput input, PresentationGenerationOptions options)
    {
        counter.Increment();
        return inner.Render(draft, input, options);
    }
}

public sealed class LateCompletionPresentationChatService : IChatCompletionService
{
    // Same minimal valid draft the presentation unit tests use, so a regression that let
    // the response through would proceed to publication instead of failing on content.
    private const string ValidDraftJson = "{\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"language\":\"en\",\"theme\":\"professional-navy\",\"presentationType\":\"business\",\"slides\":[{\"order\":1,\"type\":\"title\",\"title\":\"Launch plan\",\"subtitle\":\"Quarterly review\",\"blocks\":[],\"notes\":null,\"visualSuggestion\":null,\"sourceRefs\":[]}]}";

    private readonly TaskCompletionSource<bool> providerCallEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Completes once the real handler has reached the provider call, i.e. the job is
    // Running and mid-flight; the test cancels only after this point.
    public Task ProviderCallEntered => providerCallEntered.Task;

    public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        providerCallEntered.TrySetResult(true);
        // The user's durable cancellation must land first: park the in-flight provider
        // call until the worker's cancellation monitor has fired the job token, then
        // deliver the response. By construction this completion is late - it returns
        // with the caller token already cancelled, exactly the state the production
        // re-assert must not let reach parse/validate/render/publication.
        await WaitUntilCancelledAsync(cancellationToken);
        return new AiGenerationResult(
            ValidDraftJson,
            new AiUsageMetadata("presentation-cancel-ledger", "presentation-cancel-ledger", 100, null, 200, 0.002m, 0.002m, 8, "completed", true));
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        yield break;
    }

    private static async Task WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        // The delay never completes on its own: it throws exactly when the token fires
        // (immediately if it is already cancelled), so the scripted response below is
        // only ever produced post-cancellation.
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class PresentationCancellationLedgerApiTests : IClassFixture<PresentationCancellationLedgerApiFactory>
{
    private readonly PresentationCancellationLedgerApiFactory factory;

    public PresentationCancellationLedgerApiTests(PresentationCancellationLedgerApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Late_provider_completion_after_durable_cancellation_persists_cancelled_with_no_output_and_no_charge()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client);

        var createResponse = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/presentation-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Launch plan",
            description = "Present the launch plan to the operating team.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "en",
            includeAgenda = true,
            includeClosingNextSteps = true,
            attachmentIds = Array.Empty<Guid>(),
        }, $"presentation-cancel-ledger-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<CreatePresentationGenerationResponse>())!;
        var jobId = created.Job.Id;

        // Deterministic handshake: the worker claimed the job (Running) and the real
        // handler is parked inside the scripted provider call.
        var running = await WaitForStatusAsync(client, jobId, "Running");
        Assert.Equal("Running", running.Status);
        using (var waitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var entered = await Task.WhenAny(factory.ChatService.ProviderCallEntered, Task.Delay(Timeout.Infinite, waitTimeout.Token));
            Assert.Same(factory.ChatService.ProviderCallEntered, entered);
        }

        // The user's cancellation is recorded durably through the public API before the
        // provider response is delivered.
        var cancelResponse = await SendWithCsrfAsync(client, HttpMethod.Post, $"/api/generation/jobs/{jobId}/cancel", new { }, idempotencyKey: null);
        Assert.Equal(HttpStatusCode.Accepted, cancelResponse.StatusCode);

        // Terminal state through the public read model: cancelled, not failed.
        var terminal = await WaitForStatusAsync(client, jobId, "Cancelled");
        Assert.Equal("Cancelled", terminal.Status);
        Assert.Null(terminal.ResultJson);
        Assert.Empty(terminal.Outputs);

        // Provider-level evidence: the production re-assert throws before the late
        // response can be parsed, validated, rendered or handed to publication, so the
        // real renderer must never have been reached. Removing the re-assert makes this
        // assertion fail even though the worker's pre-publication guard still protects
        // the durable state below.
        Assert.Equal(0, factory.Counter.Count);

        // Direct database assertion - the acceptance gap the targeted unit tests left open.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();

        var persisted = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Cancelled, persisted.Status);
        Assert.Equal(GenerationJobErrorCodes.PresentationCancelled, persisted.ErrorCode);
        Assert.NotNull(persisted.CancelledAt);
        Assert.Null(persisted.FailedAt);
        Assert.Null(persisted.ResultJson);

        // No published output: no job output rows and no generated asset for this job.
        Assert.Equal(0, await db.GenerationJobOutputs.CountAsync(item => item.GenerationJobId == jobId));
        Assert.Equal(0, await db.Assets.CountAsync(item => item.SourceGenerationJobId == jobId));

        // Usage ledger terminal state: the reservation was finalized as Cancelled with a
        // zero customer charge, never Completed.
        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.GenerationJobId == jobId);
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
        Assert.Equal(0m, usage.ProviderCostUsd);
        Assert.False(usage.IsBillable);
        Assert.Equal(GenerationJobErrorCodes.PresentationCancelled, usage.FailureCode);
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName = "Cancellation Ledger Tester",
            email = $"cancel-ledger-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object payload, string? idempotencyKey)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForStatusAsync(HttpClient client, Guid jobId, string status)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status == status) return job;
            Assert.True(
                attempt < 599,
                $"Job {jobId} never reached {status} (observed: {job.Status}, error: {job.ErrorCode}).");
            await Task.Delay(50);
        }

        throw new TimeoutException($"Job {jobId} never reached {status}.");
    }
}

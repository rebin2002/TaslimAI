using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Research;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent standard-QA regression lane for the Research export at the publication fence
/// of PR #1077 (head 396dc03dd2cc4430a8514c1594ce5a25f940d16d). The fence exits added by that
/// PR are shared by every job type, but <c>DocumentExportCancellationAttemptTests</c> only
/// parameterizes the Document lane; the prior QA review explicitly listed "Research-export
/// (non-Document) job types ... are not individually parameterized in the new file" and "No
/// explicit retry-after-cancel test" as remaining gaps. This file closes exactly those gaps for
/// the <c>research.generate</c> lane and touches no production file and no other test file.
///
/// Proven here, deterministically and provider-free:
/// 1. a cancellation committed before the Research publication phase finalizes the provider
///    attempt exactly once as Cancelled (fence exit 1);
/// 2. a cancellation committed while the delayed Research PDF publish callback is in flight
///    discards the publication, still finalizes the attempt exactly once, and never exposes a
///    downloadable artifact (fence exit 2 / linked-token path);
/// 3. a late success callback cannot reopen the terminal attempt or book provider cost;
/// 4. a legitimate same-idempotency-key replay after the cancel returns the cancelled job,
///    creates no second reservation (no double charge) and never revives the attempt or job.
///
/// Everything is synthetic: in-process gated fakes replace the research search and report
/// providers and decorate the asset publisher; no provider HTTP, no production endpoints,
/// credentials, customer data, migrations or charge operations. The assembly disables
/// parallel test collections, so the per-fixture gate holder is safe.
/// </summary>
public sealed class ResearchExportCancellationGateHolder
{
    public ResearchExportCancellationGate Gate { get; set; } = new();
}

public sealed class ResearchExportCancellationGate
{
    private readonly TaskCompletionSource providerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource providerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource pdfPublicationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource pdfPublicationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int firstPdfWindowClaimed;

    public Task ProviderStarted => providerStarted.Task;
    public Task PdfPublicationStarted => pdfPublicationStarted.Task;

    // Only the linked-token regression aborts the synthetic report provider on cancellation;
    // the other tests model a provider returning after cancellation so the publication-fence
    // finalizer added by #1077 is exercised rather than the pre-existing exception handler.
    public bool HonorProviderCancellation { get; set; }

    public void SignalProviderStarted() => providerStarted.TrySetResult();
    public void ReleaseProvider() => providerRelease.TrySetResult();
    public void SignalPdfPublicationStarted() => pdfPublicationStarted.TrySetResult();
    public void ReleasePdfPublication() => pdfPublicationRelease.TrySetResult();

    // The gate instance is per test (InstallGate swaps it on the holder), so the first PDF
    // publication window of the active gate is claimed exactly once even though every job
    // in this class shares the publisher decorator.
    public bool TryClaimFirstPdfPublicationWindow() => Interlocked.Exchange(ref firstPdfWindowClaimed, 1) == 0;

    public async Task<bool> WaitForProviderReleaseAsync(CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(providerRelease.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        return completed == providerRelease.Task;
    }

    public async Task<bool> WaitForPdfPublicationReleaseAsync(CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(pdfPublicationRelease.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        return completed == pdfPublicationRelease.Task;
    }
}

/// <summary>
/// Deterministic in-process stand-in for the web research search provider. Synthetic sources
/// and evidence only; performs no I/O. Mirrors the source/evidence shape the default planner,
/// content fetcher and evidence processor accept in the existing research test lane.
/// </summary>
public sealed class SyntheticResearchSearchProvider : IResearchSearchProvider
{
    public Task<ResearchSearchResult> SearchAsync(ResearchSearchRequest request, ResearchGenerationOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = new[]
        {
            new ResearchSourceCandidate("S1", "https://example.gov/publication-fence", "https://example.gov/publication-fence", "Publication fence bulletin", "example.gov", "Example Agency", null, DateTime.UtcNow, "web", "Synthetic cancellation evidence.", "Synthetic cancellation evidence.", request.Plan.SearchQueries[0], 1, true, null),
            new ResearchSourceCandidate("S2", "https://example.org/attempt-boundary", "https://example.org/attempt-boundary", "Attempt boundary outlook", "example.org", "Example Institute", null, DateTime.UtcNow, "web", "Synthetic finalization evidence.", "Synthetic finalization evidence.", request.Plan.SearchQueries[0], 2, true, null),
        };
        var evidence = sources.Select(source => new ResearchEvidenceCandidate(source.CitationId, "synthetic evidence", source.ExtractedText!, null, null)).ToArray();
        var usage = new AiUsageMetadata("test", "research-cancel-gate-search", 100, null, 50, 0m, 0m, 5, "completed", true, SafeMetadataJson: "{\"researchStage\":\"search\"}");
        return Task.FromResult(new ResearchSearchResult(sources, evidence, usage));
    }
}

/// <summary>
/// Holds the worker inside the report stage on demand so a cancellation can be committed
/// before the publication phase begins (fence exit 1) and the handler can then still return
/// late, exactly as a slow provider callback would.
/// </summary>
internal sealed class GatedResearchReportProvider(ResearchExportCancellationGateHolder holder) : IResearchReportProvider
{
    public async Task<ResearchReportProviderResult> GenerateAsync(ResearchReportPrompt prompt, ResearchGenerationOptions options, CancellationToken cancellationToken)
    {
        holder.Gate.SignalProviderStarted();
        if (!await holder.Gate.WaitForProviderReleaseAsync(holder.Gate.HonorProviderCancellation ? cancellationToken : CancellationToken.None))
            throw new OperationCanceledException(cancellationToken);

        var draft = new ResearchDraft
        {
            Title = "Research publication fence cancellation gate",
            Subtitle = "Synthetic cited overview",
            Language = "en",
            ExecutiveSummary = "The synthetic evidence shows a cancellation that must win over late publication.",
            KeyFindings = [new ResearchReportBlock { Type = ResearchBlockTypes.KeyFinding, Text = "The provider attempt must finalize exactly once as cancelled. [S1]", CitationIds = ["S1"] }],
            Sections = [new ResearchSection { Heading = "Boundary evidence", Blocks = [new ResearchReportBlock { Type = ResearchBlockTypes.Paragraph, Text = "A delayed publish callback cannot revive a cancelled export. [S2]", CitationIds = ["S2"] }] }],
            Conclusion = "Cancellation fences the publication and the attempt finalization alike.",
            Sources = ["S1", "S2"],
        };
        var usage = new AiUsageMetadata("test", "research-cancel-gate-report", 200, null, 150, 0.0011m, 0.0011m, 10, "completed", false, PricingVersion: "research-cancel-gate-v1", Currency: "USD", CostBasis: UsageCostBasis.Actual);
        return new ResearchReportProviderResult(draft, usage);
    }
}

/// <summary>
/// Delegates every publication call but pauses inside the first PDF representation
/// publication window so a test can commit a cancellation while the delayed Research PDF
/// publish callback is in flight. Resolves the real publisher lazily so the decorator never
/// captures services outside the worker scope that asked for it.
/// </summary>
internal sealed class GatedResearchAssetPublisher(
    IServiceProvider serviceProvider,
    ResearchExportCancellationGateHolder holder) : IGeneratedAssetPublisher
{
    public async Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default)
    {
        var gate = holder.Gate;
        if (string.Equals(output.FileArtifact?.RepresentationType, AssetRepresentationTypes.Pdf, StringComparison.OrdinalIgnoreCase)
            && gate.TryClaimFirstPdfPublicationWindow())
        {
            gate.SignalPdfPublicationStarted();
            if (!await gate.WaitForPdfPublicationReleaseAsync(cancellationToken))
                throw new OperationCanceledException(cancellationToken);
        }
        return await Inner.PrepareAsync(job, output, cancellationToken);
    }

    public Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default) =>
        Inner.DiscardAsync(publication, cancellationToken);

    private GeneratedAssetPublisher Inner =>
        (GeneratedAssetPublisher)serviceProvider.GetRequiredService(typeof(GeneratedAssetPublisher));
}

public class ResearchExportCancellationAttemptBoundaryApiFactory : GenerationJobsApiFactory
{
    private readonly ResearchExportCancellationGateHolder holder = new();

    public ResearchExportCancellationGate InstallGate()
    {
        var gate = new ResearchExportCancellationGate();
        holder.Gate = gate;
        return gate;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ResearchGeneration:Enabled", "true");
        builder.UseSetting("ResearchGeneration:MaxSourceCount", "8");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(holder);
            services.RemoveAll<IResearchSearchProvider>();
            services.RemoveAll<IResearchReportProvider>();
            services.AddSingleton<IResearchSearchProvider, SyntheticResearchSearchProvider>();
            services.AddSingleton<IResearchReportProvider, GatedResearchReportProvider>();
            services.RemoveAll<IGeneratedAssetPublisher>();
            services.AddScoped<GeneratedAssetPublisher>();
            services.AddScoped<IGeneratedAssetPublisher, GatedResearchAssetPublisher>();
        });
    }
}

public sealed class ResearchExportCancellationAttemptBoundaryTests : IClassFixture<ResearchExportCancellationAttemptBoundaryApiFactory>
{
    private readonly ResearchExportCancellationAttemptBoundaryApiFactory factory;

    public ResearchExportCancellationAttemptBoundaryTests(ResearchExportCancellationAttemptBoundaryApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact(Timeout = 90_000)]
    public async Task Research_cancellation_committed_before_publication_starts_finalizes_the_provider_attempt_once_as_cancelled()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateResearchJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.ResearchCancelled, terminal.ErrorCode);
        Assert.DoesNotContain("assetId", terminal.ResultJson ?? string.Empty);

        // The provider returned after the cancel was committed; the handler result never
        // reached the publication loop, so the PDF publish callback must not run at all.
        Assert.False(gate.PdfPublicationStarted.IsCompleted);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));

        var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.Equal("Cancelled", attempt.ResultClassification);
        Assert.Equal(GenerationJobErrorCodes.Cancelled, attempt.FailureCode);
        Assert.Null(attempt.ActualProviderCostUsd);
        Assert.False(attempt.ActualProviderCostKnown);
        Assert.NotNull(attempt.CompletedAt);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact(Timeout = 90_000)]
    public async Task Research_cancellation_committed_during_the_delayed_pdf_publish_callback_finalizes_the_attempt_once_and_never_publishes()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateResearchJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        gate.ReleaseProvider();
        await gate.PdfPublicationStarted.WaitAsync(TimeSpan.FromSeconds(20));

        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleasePdfPublication();

        // Both fence shapes converge on the same terminal contract: either the linked worker
        // token aborts the in-flight publication or the fenced completion loses the race and
        // discards it (#1077 fence exit 2). Neither may publish a downloadable artifact.
        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.ResearchCancelled, terminal.ErrorCode);
        Assert.DoesNotContain("assetId", terminal.ResultJson ?? string.Empty);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));

        var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.Equal("Cancelled", attempt.ResultClassification);
        Assert.Null(attempt.ActualProviderCostUsd);
        Assert.False(attempt.ActualProviderCostKnown);
        Assert.NotNull(attempt.CompletedAt);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact(Timeout = 90_000)]
    public async Task Late_pdf_publish_success_callback_after_a_cancelled_research_export_cannot_reopen_the_attempt_or_book_cost()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateResearchJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);

        // Fix-present precondition: the fence exit already finalized the attempt, so the
        // research job's attempt row is terminal while the job itself is terminal.
        // Without #1077 the row is still Started here and the late callback below reopens
        // it as a successful, costed attempt for an export that was never published.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        }

        // The shape of a delayed PDF publish-completion callback (or a retrying resilience
        // layer) arriving from an independent scope after the cancel committed: the atomic
        // compare-and-set in CompleteAttemptAsync must elect the committed Cancelled winner
        // and the late writer must adopt it instead of booking provider cost.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var budget = scope.ServiceProvider.GetRequiredService<IGenerationBudgetService>();
            var attempt = await db.GenerationProviderAttempts.SingleAsync(item => item.GenerationJobId == created.Job.Id);
            await budget.CompleteAttemptAsync(attempt, 0.0011m, true, GenerationProviderAttemptStatus.Succeeded, null, CancellationToken.None);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
            Assert.Equal("Cancelled", attempt.ResultClassification);
            Assert.Null(attempt.ActualProviderCostUsd);
            Assert.False(attempt.ActualProviderCostKnown);
            Assert.Equal(GenerationJobErrorCodes.Cancelled, attempt.FailureCode);

            Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
            Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
            Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Same_idempotency_key_replay_after_a_cancelled_research_export_reuses_the_job_without_double_charge_or_revival()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var idempotencyKey = $"research-cancel-replay-{Guid.NewGuid():N}";
        var created = await CreateResearchJob(client, auth.PersonalWorkspace.Id, idempotencyKey);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);

        // A legitimate retry that reuses the same idempotency key replays the cancelled job
        // instead of queueing a second execution: one job row for the key, one attempt, and
        // exactly one cancelled reservation with zero charge.
        var replayResponse = await SendWithCsrf(client, HttpMethod.Post, "/api/research-generation/jobs", BuildResearchPayload(auth.PersonalWorkspace.Id), idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        var replayed = (await replayResponse.Content.ReadFromJsonAsync<CreateResearchGenerationResponse>())!;
        Assert.Equal(created.Job.Id, replayed.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), replayed.Job.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var jobs = await db.GenerationJobs.AsNoTracking().Where(item => item.IdempotencyKey == idempotencyKey).ToListAsync();
            var job = Assert.Single(jobs);
            Assert.Equal(created.Job.Id, job.Id);
            Assert.Equal(GenerationJobStatus.Cancelled, job.Status);

            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
            Assert.Null(attempt.ActualProviderCostUsd);
            Assert.False(attempt.ActualProviderCostKnown);

            Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
            Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));

            var reservations = await db.UsageTransactions.AsNoTracking()
                .Where(item => item.RequestId == $"generation:{created.Job.Id:N}").ToListAsync();
            var reservation = Assert.Single(reservations);
            Assert.Equal(UsageTransactionStatus.Cancelled, reservation.Status);
            Assert.Equal(0m, reservation.ChargedAmount);
        }

        // The replay must not have re-queued the job: it stays cancelled and unpublished.
        var afterReplay = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), afterReplay!.Status);
        Assert.DoesNotContain("assetId", afterReplay.ResultJson ?? string.Empty);
    }

    private async Task<CreateResearchGenerationResponse> CreateResearchJob(HttpClient client, Guid workspaceId, string? idempotencyKey = null)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/research-generation/jobs", BuildResearchPayload(workspaceId), idempotencyKey);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateResearchGenerationResponse>())!;
    }

    // Byte-stable payload: the fingerprint gate on the idempotency replay requires the retry
    // to serialize to the same job input as the original request.
    private static object BuildResearchPayload(Guid workspaceId) => new
    {
        workspaceId,
        question = "How should a synthetic research export exercise the publication fence under cancellation?",
        depth = "standard",
        reportType = "research_report",
        language = "en",
        useWebSources = true,
        attachmentIds = Array.Empty<Guid>(),
    };

    private static async Task WaitForCancellationVisible(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job is { CancellationRequested: true }) return;
            Assert.False(job?.Status is "Succeeded" or "Failed" or "Cancelled", $"The job reached {job?.Status} before the cancellation was observable.");
            await Task.Delay(25);
        }
        throw new TimeoutException($"Cancellation for research job {jobId} never became observable.");
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job?.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Research job {jobId} did not reach a terminal state.");
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Research Cancel Boundary Tester", email = $"research-cancel-boundary-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

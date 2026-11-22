using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Cross-test coordination gate. The assembly disables parallel test collections,
/// so a single active gate holder is sufficient; each test installs its own gate.
/// </summary>
public sealed class DocumentExportCancellationGateHolder
{
    public DocumentExportCancellationGate Gate { get; set; } = new();
}

public sealed class DocumentExportCancellationGate
{
    private readonly TaskCompletionSource providerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource providerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource publisherStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource publisherRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ProviderStarted => providerStarted.Task;
    public Task PublisherStarted => publisherStarted.Task;

    // Only the linked-token regression should abort the synthetic provider on cancellation.
    // Other tests intentionally model a provider returning after cancellation to exercise
    // the publication-fence finalizer rather than the existing exception handler.
    public bool HonorProviderCancellation { get; set; }

    public void SignalProviderStarted() => providerStarted.TrySetResult();
    public void ReleaseProvider() => providerRelease.TrySetResult();
    public void SignalPublisherStarted() => publisherStarted.TrySetResult();
    public void ReleasePublisher() => publisherRelease.TrySetResult();

    public async Task<bool> WaitForProviderReleaseAsync(CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(providerRelease.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        return completed == providerRelease.Task;
    }

    public async Task<bool> WaitForPublisherReleaseAsync(CancellationToken cancellationToken)
    {
        var completed = await Task.WhenAny(publisherRelease.Task, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        return completed == publisherRelease.Task;
    }
}

internal sealed class GatedDocumentProvider(DocumentExportCancellationGateHolder holder) : IDocumentGenerationProvider
{
    public async Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken)
    {
        holder.Gate.SignalProviderStarted();
        if (!await holder.Gate.WaitForProviderReleaseAsync(holder.Gate.HonorProviderCancellation ? cancellationToken : CancellationToken.None))
            throw new OperationCanceledException(cancellationToken);

        var draft = new DocumentDraft
        {
            Title = "Cancellation gate brief",
            Summary = "A deterministic draft used to hold the worker at the publication boundary.",
            Sections =
            [
                new DocumentSection
                {
                    Heading = "Key points",
                    Blocks = [new DocumentBlock { Type = DocumentBlockTypes.BulletList, Items = ["Cancellation must win over late completion.", "Publication stays fenced."] }],
                },
            ],
        };
        var usage = new AiUsageMetadata("document-gate", "document-gate", 80, null, 120, 0.0012m, 0.0012m, 8, "completed", false, PricingVersion: "document-gate-v1", Currency: "USD", CostBasis: UsageCostBasis.Actual);
        return new DocumentProviderResult(draft, usage);
    }
}

/// <summary>
/// Delegates every publication call but pauses on the first representation
/// publication so a test can commit a cancellation inside the publication window.
/// Resolves the real publisher lazily so the decorator never captures services
/// outside the worker scope that asked for it.
/// </summary>
internal sealed class GatedDocumentAssetPublisher(
    IServiceProvider serviceProvider,
    DocumentExportCancellationGateHolder holder) : IGeneratedAssetPublisher
{
    private int prepareCount;

    public async Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref prepareCount) == 1)
        {
            holder.Gate.SignalPublisherStarted();
            if (!await holder.Gate.WaitForPublisherReleaseAsync(cancellationToken))
                throw new OperationCanceledException(cancellationToken);
        }
        return await Inner.PrepareAsync(job, output, cancellationToken);
    }

    public Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default) =>
        Inner.DiscardAsync(publication, cancellationToken);

    private GeneratedAssetPublisher Inner =>
        (GeneratedAssetPublisher)serviceProvider.GetRequiredService(typeof(GeneratedAssetPublisher));
}

public class DocumentExportCancellationAttemptApiFactory : GenerationJobsApiFactory
{
    private readonly DocumentExportCancellationGateHolder holder = new();

    public DocumentExportCancellationGate InstallGate()
    {
        var gate = new DocumentExportCancellationGate();
        holder.Gate = gate;
        return gate;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("DocumentGeneration:Enabled", "true");
        builder.UseSetting("DocumentGeneration:RequestedTier", "Fast");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(holder);
            services.RemoveAll<IDocumentGenerationProvider>();
            services.AddSingleton<IDocumentGenerationProvider, GatedDocumentProvider>();
            services.RemoveAll<IGeneratedAssetPublisher>();
            services.AddScoped<GeneratedAssetPublisher>();
            services.AddScoped<IGeneratedAssetPublisher, GatedDocumentAssetPublisher>();
        });
    }
}

public sealed class DocumentExportCancellationAttemptTests : IClassFixture<DocumentExportCancellationAttemptApiFactory>
{
    private readonly DocumentExportCancellationAttemptApiFactory factory;

    public DocumentExportCancellationAttemptTests(DocumentExportCancellationAttemptApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact(Timeout = 90_000)]
    public async Task Cancellation_committed_before_publication_starts_finalizes_the_provider_attempt_as_cancelled()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateDocumentJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.DocumentCancelled, terminal.ErrorCode);
        Assert.DoesNotContain("assetId", terminal.ResultJson ?? string.Empty);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));

        var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal("Cancelled", attempt.ResultClassification);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact(Timeout = 90_000)]
    public async Task Cancellation_committed_while_a_representation_publication_is_in_flight_discards_it_and_finalizes_the_attempt()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateDocumentJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        gate.ReleaseProvider();
        await gate.PublisherStarted.WaitAsync(TimeSpan.FromSeconds(20));

        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleasePublisher();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);
        Assert.Equal(GenerationJobErrorCodes.DocumentCancelled, terminal.ErrorCode);
        Assert.DoesNotContain("assetId", terminal.ResultJson ?? string.Empty);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
        Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));
        Assert.Equal(0, await db.StoredFiles.AsNoTracking().CountAsync(item => item.Status == StoredFileStatus.Ready && item.ConversationId == null && item.OriginalFileName.Contains("cancellation-gate-brief")));

        var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal("Cancelled", attempt.ResultClassification);

        var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
        Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
        Assert.Equal(0m, usage.ChargedAmount);
    }

    [Fact(Timeout = 90_000)]
    public async Task Cancellation_observed_by_the_linked_worker_token_still_finalizes_the_attempt_and_keeps_the_export_unpublished()
    {
        var gate = factory.InstallGate();
        gate.HonorProviderCancellation = true;
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateDocumentJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));

        var attempts = await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync();
        var attempt = Assert.Single(attempts);
        Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        Assert.NotNull(attempt.CompletedAt);
    }

    [Fact(Timeout = 90_000)]
    public async Task Late_success_finalization_after_a_fence_cancel_cannot_reopen_the_attempt_or_record_provider_cost()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateDocumentJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleaseProvider();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);

        // Fix-present precondition: the first fence exit has finalized the attempt, so
        // the job's attempt row is already terminal when the job itself is terminal.
        // Without the fix the row is still Started here, and the late finalization
        // below reopens it as a successful attempt with provider cost for an unpublished export.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        }

        // A late success from an independent scope - the shape a delayed provider
        // completion or a retrying resilience layer would produce - must not mutate
        // the terminal attempt or record an actual provider cost.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var budget = scope.ServiceProvider.GetRequiredService<IGenerationBudgetService>();
            var attempt = await db.GenerationProviderAttempts.SingleAsync(item => item.GenerationJobId == created.Job.Id);
            await budget.CompleteAttemptAsync(attempt, 0.0012m, true, GenerationProviderAttemptStatus.Succeeded, null, CancellationToken.None);
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
            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
            Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Concurrent_terminal_finalizations_after_a_publication_window_cancel_keep_one_cancelled_attempt_and_null_provider_cost()
    {
        var gate = factory.InstallGate();
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var created = await CreateDocumentJob(client, auth.PersonalWorkspace.Id);

        await gate.ProviderStarted.WaitAsync(TimeSpan.FromSeconds(20));
        gate.ReleaseProvider();
        await gate.PublisherStarted.WaitAsync(TimeSpan.FromSeconds(20));

        var cancel = await SendWithCsrf(client, HttpMethod.Post, $"/api/generation/jobs/{created.Job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await WaitForCancellationVisible(client, created.Job.Id);
        gate.ReleasePublisher();

        var terminal = await WaitForTerminal(client, created.Job.Id);
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), terminal.Status);

        // Fix-present precondition: the second fence exit (the fenced completion losing
        // the race) finalized the attempt before the job cancellation was committed.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking()
                .Where(item => item.GenerationJobId == created.Job.Id).ToListAsync());
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
        }

        // Race four terminal finalizations for the same attempt from four independent
        // scopes, the shape a late provider success, a resilience-layer failure and a
        // duplicate cancel path would produce. The terminal-state guard makes every
        // overlapping finalization a no-op, so a cancelled export can never accumulate
        // an actual provider cost regardless of which writer would have won.
        var finalizers = new[]
        {
            (Status: GenerationProviderAttemptStatus.Succeeded, Cost: (decimal?)0.0012m, FailureCode: (string?)null),
            (Status: GenerationProviderAttemptStatus.Succeeded, Cost: (decimal?)0.0009m, FailureCode: (string?)null),
            (Status: GenerationProviderAttemptStatus.Failed, Cost: (decimal?)0.0012m, FailureCode: (string?)"PROVIDER_TIMEOUT"),
            (Status: GenerationProviderAttemptStatus.Cancelled, Cost: (decimal?)null, FailureCode: (string?)GenerationJobErrorCodes.Cancelled),
        };
        await Task.WhenAll(finalizers.Select(async finalizer =>
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var budget = scope.ServiceProvider.GetRequiredService<IGenerationBudgetService>();
            var attempt = await db.GenerationProviderAttempts.SingleAsync(item => item.GenerationJobId == created.Job.Id);
            await budget.CompleteAttemptAsync(attempt, finalizer.Cost, finalizer.Cost.HasValue, finalizer.Status, finalizer.FailureCode, CancellationToken.None);
        }));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempts = await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == created.Job.Id).ToListAsync();
            var attempt = Assert.Single(attempts);
            Assert.Equal(1, attempt.AttemptNumber);
            Assert.Equal(GenerationProviderAttemptStatus.Cancelled, attempt.Status);
            Assert.Equal("Cancelled", attempt.ResultClassification);
            Assert.Null(attempt.ActualProviderCostUsd);
            Assert.False(attempt.ActualProviderCostKnown);
            Assert.NotNull(attempt.CompletedAt);

            Assert.False(await db.Assets.AsNoTracking().AnyAsync(item => item.SourceGenerationJobId == created.Job.Id));
            Assert.False(await db.GenerationJobOutputs.AsNoTracking().AnyAsync(item => item.GenerationJobId == created.Job.Id));

            var usage = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == $"generation:{created.Job.Id:N}");
            Assert.Equal(UsageTransactionStatus.Cancelled, usage.Status);
            Assert.Equal(0m, usage.ChargedAmount);
        }

        var afterRace = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{created.Job.Id}");
        Assert.Equal(GenerationJobStatus.Cancelled.ToString(), afterRace!.Status);
        Assert.DoesNotContain("assetId", afterRace.ResultJson ?? string.Empty);
    }

    [Fact(Timeout = 90_000)]
    public async Task Two_finalizers_that_both_observed_the_started_attempt_elect_one_winner_and_the_stale_writer_adopts_it()
    {
        // Synthetic deterministic contention: two independent scopes read the same
        // provider attempt while it is still Started - the interleaving the previous
        // read-check-write mishandled - and then each finalizes with a different
        // terminal outcome. The compare-and-set must let the first writer commit and
        // force the stale second writer to adopt it instead of last-write-winning.
        using var client = factory.CreateClient();
        var auth = await Register(client);

        Guid attemptId;
        var succeededCost = 0.0007m;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var job = new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                JobType = GenerationJobTypes.DocumentGenerate,
                Status = GenerationJobStatus.Cancelled,
                InputJson = "{}",
                CreatedAt = DateTime.UtcNow,
            };
            var attempt = new GenerationProviderAttempt
            {
                Id = Guid.NewGuid(),
                GenerationJobId = job.Id,
                JobConcurrencyToken = job.ConcurrencyToken,
                IdempotencyKey = $"generation:{job.Id:N}:attempt:1",
                Capability = job.JobType,
                AttemptNumber = 1,
                Provider = "synthetic-contender",
                Status = GenerationProviderAttemptStatus.Started,
                ResultClassification = "Started",
                EstimatedProviderCostUsd = succeededCost,
                EstimatedProviderCostKnown = true,
                FinalizationKey = $"generation:{job.Id:N}:attempt:1",
                StartedAt = DateTime.UtcNow,
            };
            db.GenerationJobs.Add(job);
            db.GenerationProviderAttempts.Add(attempt);
            await db.SaveChangesAsync();
            attemptId = attempt.Id;
        }

        using var staleScope = factory.Services.CreateScope();
        using var winnerScope = factory.Services.CreateScope();
        var staleDb = staleScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var winnerDb = winnerScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var staleCopy = await staleDb.GenerationProviderAttempts.SingleAsync(item => item.Id == attemptId);
        var winnerCopy = await winnerDb.GenerationProviderAttempts.SingleAsync(item => item.Id == attemptId);
        Assert.Equal(GenerationProviderAttemptStatus.Started, staleCopy.Status);
        Assert.Equal(GenerationProviderAttemptStatus.Started, winnerCopy.Status);

        await winnerScope.ServiceProvider.GetRequiredService<IGenerationBudgetService>()
            .CompleteAttemptAsync(winnerCopy, succeededCost, true, GenerationProviderAttemptStatus.Succeeded, null, CancellationToken.None);
        await staleScope.ServiceProvider.GetRequiredService<IGenerationBudgetService>()
            .CompleteAttemptAsync(staleCopy, null, false, GenerationProviderAttemptStatus.Cancelled, GenerationJobErrorCodes.Cancelled, CancellationToken.None);

        using (var verifyScope = factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var attempt = Assert.Single(await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.Id == attemptId).ToListAsync());
            Assert.Equal(GenerationProviderAttemptStatus.Succeeded, attempt.Status);
            Assert.Equal("Succeeded", attempt.ResultClassification);
            Assert.Equal(succeededCost, attempt.ActualProviderCostUsd);
            Assert.True(attempt.ActualProviderCostKnown);
            Assert.Null(attempt.FailureCode);
            Assert.NotNull(attempt.CompletedAt);
        }

        // The losing scope adopted the committed winner instead of carrying its own
        // superseded outcome, so any later use of its copy - and its own flush -
        // repeats the committed outcome rather than overwriting status or cost.
        Assert.Equal(GenerationProviderAttemptStatus.Succeeded, staleCopy.Status);
        Assert.Equal(succeededCost, staleCopy.ActualProviderCostUsd);
        Assert.Null(staleCopy.FailureCode);
    }

    [DisposablePostgresFact]
    public async Task Concurrent_postgres_finalizers_that_both_observed_the_started_attempt_commit_exactly_one_outcome()
    {
        // Deterministic contention regression against the disposable PostgreSQL the
        // API CI job provisions (skipped elsewhere; never against production data).
        // Two independent contexts read the attempt while it is still Started and,
        // gated by a command barrier so both compare-and-set UPDATEs reach the engine
        // while the other is in flight, finalize with different terminal outcomes.
        // Exactly one UPDATE may affect a row; the loser must adopt the committed
        // winner so the attempt status and provider-cost attribution never disagree.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var schema = $"attempt_cas_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        var costedOutcome = (Status: GenerationProviderAttemptStatus.Succeeded, Cost: (decimal?)0.0042m, CostKnown: true, FailureCode: (string?)null, Classification: "Succeeded");
        var cancelledOutcome = (Status: GenerationProviderAttemptStatus.Cancelled, Cost: (decimal?)null, CostKnown: false, FailureCode: (string?)GenerationJobErrorCodes.Cancelled, Classification: "Cancelled");
        try
        {
            var scopedBuilder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            Guid attemptId;
            var plainOptions = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scopedBuilder.ConnectionString).Options;
            await using (var db = new TaslimDbContext(plainOptions))
            {
                await db.Database.EnsureCreatedAsync();
                var now = DateTime.UtcNow;
                var userId = Guid.NewGuid();
                db.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = $"attempt-cas-{userId:N}@example.com",
                    NormalizedUserName = $"ATTEMPT-CAS-{userId:N}@EXAMPLE.COM",
                    Email = $"attempt-cas-{userId:N}@example.com",
                    NormalizedEmail = $"ATTEMPT-CAS-{userId:N}@EXAMPLE.COM",
                    DisplayName = "Attempt CAS Tester",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var workspaceId = Guid.NewGuid();
                db.Workspaces.Add(new Workspace
                {
                    Id = workspaceId,
                    Name = "Attempt CAS Workspace",
                    Slug = $"attempt-cas-{workspaceId:N}",
                    Type = WorkspaceType.Personal,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                var jobId = Guid.NewGuid();
                db.GenerationJobs.Add(new GenerationJob
                {
                    Id = jobId,
                    WorkspaceId = workspaceId,
                    CreatedByUserId = userId,
                    JobType = GenerationJobTypes.DocumentGenerate,
                    Status = GenerationJobStatus.Cancelled,
                    InputJson = "{}",
                    CreatedAt = now,
                });
                var attempt = new GenerationProviderAttempt
                {
                    Id = Guid.NewGuid(),
                    GenerationJobId = jobId,
                    JobConcurrencyToken = Guid.NewGuid(),
                    IdempotencyKey = $"generation:{jobId:N}:attempt:1",
                    Capability = GenerationJobTypes.DocumentGenerate,
                    AttemptNumber = 1,
                    Provider = "synthetic-contender",
                    Status = GenerationProviderAttemptStatus.Started,
                    ResultClassification = "Started",
                    EstimatedProviderCostUsd = 0.0042m,
                    EstimatedProviderCostKnown = true,
                    FinalizationKey = $"generation:{jobId:N}:attempt:1",
                    StartedAt = now,
                };
                db.GenerationProviderAttempts.Add(attempt);
                await db.SaveChangesAsync();
                attemptId = attempt.Id;
            }

            var barrier = new AttemptFinalizationCasBarrierInterceptor();
            var contendedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedBuilder.ConnectionString)
                .AddInterceptors(barrier)
                .Options;

            var loadCountdown = new CountdownEvent(2);

            async Task<GenerationProviderAttempt> FinalizeOnFreshContext(
                (GenerationProviderAttemptStatus Status, decimal? Cost, bool CostKnown, string? FailureCode, string Classification) outcome)
            {
                await using var db = new TaslimDbContext(contendedOptions);
                var budget = new GenerationBudgetService(db, Options.Create(new GenerationBudgetOptions()), NullLogger<GenerationBudgetService>.Instance);
                var attempt = await db.GenerationProviderAttempts.SingleAsync(item => item.Id == attemptId);
                Assert.Equal(GenerationProviderAttemptStatus.Started, attempt.Status);
                // Both finalizers must pass the read phase before either compare-and-set runs.
                loadCountdown.Signal();
                loadCountdown.Wait(TimeSpan.FromSeconds(30));
                await budget.CompleteAttemptAsync(attempt, outcome.Cost, outcome.CostKnown, outcome.Status, outcome.FailureCode, CancellationToken.None);
                return attempt;
            }

            var costedTask = Task.Run(() => FinalizeOnFreshContext(costedOutcome));
            var cancelledTask = Task.Run(() => FinalizeOnFreshContext(cancelledOutcome));
            await barrier.WaitForBothCasCommandsAsync(TimeSpan.FromSeconds(60));
            barrier.ReleaseCasCommands();
            var costedCopy = await costedTask;
            var cancelledCopy = await cancelledTask;

            // The signature of the atomic guard: exactly one compare-and-set committed.
            Assert.Equal(new[] { 0, 1 }, barrier.AffectedRows.OrderBy(count => count).ToArray());

            using (var verify = new TaslimDbContext(plainOptions))
            {
                var attempt = Assert.Single(await verify.GenerationProviderAttempts.AsNoTracking().Where(item => item.Id == attemptId).ToListAsync());
                Assert.NotNull(attempt.CompletedAt);
                var committedCosted = attempt.Status == costedOutcome.Status
                    && attempt.ActualProviderCostUsd == costedOutcome.Cost
                    && attempt.ActualProviderCostKnown == costedOutcome.CostKnown
                    && attempt.FailureCode == costedOutcome.FailureCode
                    && attempt.ResultClassification == costedOutcome.Classification;
                var committedCancelled = attempt.Status == cancelledOutcome.Status
                    && attempt.ActualProviderCostUsd == cancelledOutcome.Cost
                    && attempt.ActualProviderCostKnown == cancelledOutcome.CostKnown
                    && attempt.FailureCode == cancelledOutcome.FailureCode
                    && attempt.ResultClassification == cancelledOutcome.Classification;
                Assert.True(committedCosted || committedCancelled, "The committed attempt mixes status and cost attribution across finalizers.");

                // Whichever finalizer lost, its in-context copy adopted the durable
                // winner, so a later flush of that context cannot rewrite the outcome.
                Assert.Equal(attempt.Status, costedCopy.Status);
                Assert.Equal(attempt.ActualProviderCostUsd, costedCopy.ActualProviderCostUsd);
                Assert.Equal(attempt.FailureCode, costedCopy.FailureCode);
                Assert.Equal(attempt.Status, cancelledCopy.Status);
                Assert.Equal(attempt.ActualProviderCostUsd, cancelledCopy.ActualProviderCostUsd);
                Assert.Equal(attempt.FailureCode, cancelledCopy.FailureCode);
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Holds the two contending compare-and-set UPDATE statements in flight until both
    /// reach the engine, so the loser's Status = Started predicate is re-evaluated
    /// against the winner's committed row. Plain SaveChanges flushes (WHERE only on Id)
    /// pass through unheld.
    /// </summary>
    private sealed class AttemptFinalizationCasBarrierInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<int> affectedRows = new();
        private readonly TaskCompletionSource<bool> bothCasCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseCasCommands = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int casCommandCount;

        public int[] AffectedRows => affectedRows.ToArray();
        public Task WaitForBothCasCommandsAsync(TimeSpan timeout) => bothCasCommands.Task.WaitAsync(timeout);
        public void ReleaseCasCommands() => releaseCasCommands.TrySetResult(true);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!IsAttemptStatusCompareAndSet(command)) return result;
            var count = Interlocked.Increment(ref casCommandCount);
            if (count == 2) bothCasCommands.TrySetResult(true);
            if (count > 2) throw new InvalidOperationException("Expected exactly two compare-and-set finalization commands in the PostgreSQL contention test.");
            await releaseCasCommands.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (IsAttemptStatusCompareAndSet(command)) affectedRows.Enqueue(result);
            return ValueTask.FromResult(result);
        }

        private static bool IsAttemptStatusCompareAndSet(DbCommand command)
        {
            var sql = command.CommandText;
            if (!IsAttemptUpdate(command)) return false;
            var whereIndex = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            if (whereIndex < 0) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                sql[whereIndex..],
                @"""?Status""?\s*=",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        private static bool IsAttemptUpdate(DbCommand command) =>
            command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.CommandText.Contains("GenerationProviderAttempts", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CreateDocumentGenerationResponse> CreateDocumentJob(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/document-generation/jobs", new
        {
            workspaceId,
            title = "Cancellation gate brief",
            description = "Create a concise professional brief that reaches the publication boundary.",
            documentType = "report",
            length = "short",
            tone = "professional",
            language = "en",
            outputFormat = "both",
            attachmentIds = Array.Empty<Guid>(),
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateDocumentGenerationResponse>())!;
    }

    private static async Task WaitForCancellationVisible(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job is { CancellationRequested: true }) return;
            Assert.False(job?.Status is "Succeeded" or "Failed" or "Cancelled", $"The job reached {job?.Status} before the cancellation was observable.");
            await Task.Delay(25);
        }
        throw new TimeoutException($"Cancellation for document job {jobId} never became observable.");
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            if (job?.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Document job {jobId} did not reach a terminal state.");
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Export Cancel Tester", email = $"document-export-cancel-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        // Integration reconciliation (vehicle-only, 2026-11-22): main now fails closed without a
        // unique Idempotency-Key on document-generation creation (cost-safety guardrail merged
        // after this lane was cut; the shipped browser client always sends one). Mirror the
        // main-suite convention so the lane's cancellation assertions exercise the live contract.
        if (method == HttpMethod.Post && path == "/api/document-generation/jobs")
            request.Headers.TryAddWithoutValidation(DocumentTestRequests.IdempotencyHeader, DocumentTestRequests.NewKey());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

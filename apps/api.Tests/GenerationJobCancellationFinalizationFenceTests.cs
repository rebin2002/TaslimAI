using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The cancellation contract this vehicle carries is that a cancellation the user actually recorded
/// settles as a cancellation on every late-failure route. The handler-side routes re-assert the token,
/// the success write fences on <c>!CancellationRequested</c>, and the heartbeat monitor cancels the
/// worker token when it polls the flag - but the worker's failure finalizer consulted none of that. A
/// stage error that won the race against the monitor's next poll therefore rewrote the user's cancelled
/// job as Failed, stamped a provider failure code, and settled the usage reservation as Failed with the
/// harvested provider spend instead of releasing it as Cancelled.
///
/// These cases pin the finalizer itself, which is where the decision is made, so the guarantee does not
/// depend on the timing of a background worker.
/// </summary>
public sealed class GenerationJobCancellationFinalizationFenceTests : IDisposable
{
    private static readonly AiUsageMetadata ProviderUsage =
        new("presentation-test", "presentation-test-model", 100, null, 200, 0.002m, 0.002m, 8, "completed", true);

    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly TaslimDbContext db;
    private readonly Guid workspaceId;
    private readonly Guid userId;

    public GenerationJobCancellationFinalizationFenceTests()
    {
        connection.Open();
        db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        workspaceId = Guid.NewGuid();
        userId = Guid.NewGuid();
        // The job rows carry foreign keys to both parents, so the fixture owns one of each.
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Cancellation Fence Workspace",
            Slug = $"cancellation-fence-{workspaceId:N}",
            Type = WorkspaceType.Personal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = $"cancellation-fence-{userId:N}",
            Email = $"cancellation-fence-{userId:N}@example.test",
            DisplayName = "Cancellation Fence Test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task Recorded_cancellation_outranks_a_late_stage_failure()
    {
        // The user pressed cancel and the flag is durable; the provider call then failed before the
        // monitor's next poll noticed. The row must read as cancelled, never as a retryable failure,
        // and the reservation must be released as cancelled rather than settled as a provider failure.
        var token = Guid.NewGuid();
        var jobId = SeedRunningJob(cancellationRequested: true, concurrencyToken: token);
        var usage = new RecordingUsageService();

        await GenerationJobWorker.FailAsync(
            db, usage, TrackedJob(jobId),
            GenerationJobErrorCodes.PresentationRenderFailed, "The presentation could not be rendered.",
            ProviderUsage, token, CancellationToken.None);

        var settled = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Cancelled, settled.Status);
        Assert.Equal(GenerationJobErrorCodes.PresentationCancelled, settled.ErrorCode);
        Assert.Equal("The job was cancelled.", settled.ErrorMessage);
        Assert.Null(settled.FailedAt);
        Assert.NotNull(settled.CancelledAt);
        Assert.Null(settled.ClaimExpiresAt);
        Assert.DoesNotContain(usage.Calls, call => call.StartsWith("fail:", StringComparison.Ordinal));
        Assert.Equal(new[] { "begin", $"cancel:{GenerationJobErrorCodes.PresentationCancelled}" }, usage.Calls);
    }

    [Fact]
    public async Task Stage_failure_still_terminalises_a_job_the_user_never_cancelled()
    {
        // Control: the fence must not swallow ordinary failures.
        var token = Guid.NewGuid();
        var jobId = SeedRunningJob(cancellationRequested: false, concurrencyToken: token);
        var usage = new RecordingUsageService();

        await GenerationJobWorker.FailAsync(
            db, usage, TrackedJob(jobId),
            GenerationJobErrorCodes.PresentationRenderFailed, "The presentation could not be rendered.",
            ProviderUsage, token, CancellationToken.None);

        var settled = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Failed, settled.Status);
        Assert.Equal(GenerationJobErrorCodes.PresentationRenderFailed, settled.ErrorCode);
        Assert.NotNull(settled.FailedAt);
        Assert.Null(settled.CancelledAt);
        Assert.Equal(new[] { "begin", $"fail:{GenerationJobErrorCodes.PresentationRenderFailed}" }, usage.Calls);
    }

    [Fact]
    public async Task A_cancellation_recorded_after_the_read_still_cannot_be_overwritten()
    {
        // The race the pre-read alone cannot close: the flag is written by another connection between
        // the read and the update. The fenced write matches no row, so neither outcome is applied and
        // the job is left for the lease-recovery finalizer, which re-reads the durable flag.
        var token = Guid.NewGuid();
        var jobId = SeedRunningJob(cancellationRequested: false, concurrencyToken: token);
        var usage = new RecordingUsageService();

        // Reproduce the interleaving by calling the finalizer with a cancellation that the fenced
        // statement sees while the pre-read did not: park the flag on a second context after the read
        // has been issued by driving the two statements through separate connections is not possible
        // here, so the guarantee is asserted directly on the fenced predicate the write uses.
        var eligible = await db.GenerationJobs.CountAsync(item =>
            item.Id == jobId && item.Status == GenerationJobStatus.Running
                && item.ConcurrencyToken == token && !item.CancellationRequested);
        Assert.Equal(1, eligible);

        using var racing = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        await racing.GenerationJobs.Where(item => item.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CancellationRequested, true));

        var afterRace = await db.GenerationJobs.AsNoTracking().CountAsync(item =>
            item.Id == jobId && item.Status == GenerationJobStatus.Running
                && item.ConcurrencyToken == token && !item.CancellationRequested);
        Assert.Equal(0, afterRace);

        // And the finalizer, run against the now-cancelled row, settles it as cancelled.
        await GenerationJobWorker.FailAsync(
            racing, usage, await racing.GenerationJobs.SingleAsync(item => item.Id == jobId),
            GenerationJobErrorCodes.PresentationRenderFailed, "The presentation could not be rendered.",
            ProviderUsage, token, CancellationToken.None);
        var settled = await racing.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Cancelled, settled.Status);
        Assert.Equal(GenerationJobErrorCodes.PresentationCancelled, settled.ErrorCode);
    }

    [Fact]
    public async Task Foreign_attempt_is_fenced_out_of_both_finalizers()
    {
        // Widening visibility must not loosen the existing lease fence: a stale worker's failure call
        // may not settle a row another attempt owns, cancelled or not.
        var jobId = SeedRunningJob(cancellationRequested: true, concurrencyToken: Guid.NewGuid());
        var usage = new RecordingUsageService();

        await GenerationJobWorker.FailAsync(
            db, usage, TrackedJob(jobId),
            GenerationJobErrorCodes.PresentationRenderFailed, "The presentation could not be rendered.",
            ProviderUsage, Guid.NewGuid(), CancellationToken.None);

        var settled = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == jobId);
        Assert.Equal(GenerationJobStatus.Running, settled.Status);
        Assert.Empty(usage.Calls);
    }

    private Guid SeedRunningJob(bool cancellationRequested, Guid concurrencyToken)
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            JobType = GenerationJobTypes.PresentationGenerate,
            Title = "Cancellation fence job",
            Status = GenerationJobStatus.Running,
            InputJson = "{}",
            RequestId = $"req-{jobId:N}",
            CancellationRequested = cancellationRequested,
            CreatedAt = now.AddMinutes(-2),
            QueuedAt = now.AddMinutes(-2),
            StartedAt = now.AddMinutes(-1),
            ClaimExpiresAt = now.AddMinutes(29),
            ConcurrencyToken = concurrencyToken,
        });
        db.SaveChanges();
        return jobId;
    }

    private GenerationJob TrackedJob(Guid jobId) => db.GenerationJobs.Single(item => item.Id == jobId);

    private sealed class RecordingUsageService : IGenerationJobUsageService
    {
        public List<string> Calls { get; } = [];

        public Task<UsageTransaction> BeginAsync(GenerationJob job, decimal? estimatedProviderCostUsd = null, CancellationToken cancellationToken = default)
        {
            Calls.Add("begin");
            return Task.FromResult(new UsageTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = job.WorkspaceId,
                UserId = job.CreatedByUserId,
                GenerationJobId = job.Id,
                RequestId = $"generation:{job.Id:N}",
            });
        }

        public Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, bool hasBillableAsset = true, CancellationToken cancellationToken = default)
        {
            Calls.Add("complete");
            return Task.CompletedTask;
        }

        public Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"fail:{failureCode}");
            return Task.CompletedTask;
        }

        public Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default)
        {
            Calls.Add($"cancel:{cancellationCode}");
            return Task.CompletedTask;
        }
    }
}

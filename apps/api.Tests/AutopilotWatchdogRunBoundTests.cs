using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Bounds on the hourly Autopilot watchdog's stalled-run reconciliation pass. A wave
/// whose completion evidence never arrives is re-driven from the persisted task
/// evidence, so the pass has to terminate: it must skip a wave that still has a
/// non-terminal event outstanding, it must stop once the documented reconciliation
/// budget is spent, and that budget must be measured in reconciliation attempts rather
/// than in calendar days, so a run that was already stale before a UTC day rollover
/// keeps the same remaining budget afterwards. Every clock here is synthetic: the
/// tests step the injected <see cref="TimeProvider"/> across a UTC midnight boundary
/// and never read a wall clock, a provider, or the machine time zone.
/// </summary>
public sealed class AutopilotWatchdogRunBoundTests
{
    private const string Branch = "fix/autopilot-watchdog-run-reconciliation-bound";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "2222222222222222222222222222222222222222";

    /// <summary>21:00Z on 16 Nov 2026: the last persisted evidence instant of a stalled wave.</summary>
    private static readonly DateTime LastEvidenceUtc = new(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>22:00Z, one hour after the last evidence and therefore outside the 30 minute stale window.</summary>
    private static readonly DateTimeOffset FirstPassUtc = new(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Watchdog_reconciliation_completes_and_re_drives_every_stalled_run()
    {
        using var host = new WatchdogHost(maxReconciliations: 5);
        var recoverableA = host.SeedStalledRun("wave-watchdog-a", LastEvidenceUtc);
        var recoverableB = host.SeedStalledRun("wave-watchdog-b", LastEvidenceUtc.AddMinutes(-1));

        // A wave that still has a non-terminal event outstanding is the other half of the
        // same query: the event owns the outcome, so the watchdog must leave that run alone.
        var waitingForEvent = host.SeedStalledRun("wave-watchdog-c", LastEvidenceUtc);
        host.SeedPendingEvent("wave-watchdog-c");

        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ReconcileAsync("hourly_watchdog");

        using var db = host.CreateContext();
        Assert.Equal(1, (await db.AutopilotRuns.SingleAsync(item => item.Id == recoverableA)).ReconciliationCount);
        Assert.Equal(1, (await db.AutopilotRuns.SingleAsync(item => item.Id == recoverableB)).ReconciliationCount);
        Assert.Equal(0, (await db.AutopilotRuns.SingleAsync(item => item.Id == waitingForEvent)).ReconciliationCount);
        Assert.Equal(FirstPassUtc.UtcDateTime, (await db.AutopilotRuns.SingleAsync(item => item.Id == recoverableA)).UpdatedAt);
    }

    [Fact]
    public async Task Stalled_run_stops_being_reconciled_once_the_watchdog_budget_is_exhausted()
    {
        using var host = new WatchdogHost(maxReconciliations: 2);
        var runId = host.SeedStalledRun("wave-watchdog-budget", LastEvidenceUtc);

        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ReconcileAsync("hourly_watchdog");
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");

        // Crossing the UTC day boundary must neither re-arm the budget nor keep rewriting
        // the persisted scheduling timestamps of a run that has already spent it.
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 17, 0, 30, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 17, 6, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);
        Assert.Equal(2, run.ReconciliationCount);
        Assert.Equal(LastEvidenceUtc, run.LastEventAt);
        Assert.Equal(new DateTime(2026, 11, 16, 23, 30, 0, DateTimeKind.Utc), run.UpdatedAt);
        Assert.Equal(AutopilotRunStates.InProgress, run.State);

        // The stop is surfaced exactly once, so an operator can see why the watchdog went quiet.
        var exhausted = await db.AutopilotAuditEvents
            .Where(item => item.Action == AutopilotAuditActions.WatchdogReconciled
                && item.Outcome == AutopilotAuditOutcomes.Failed
                && item.Reason == "watchdog_reconciliation_exhausted"
                && item.TargetId == runId)
            .ToListAsync();
        Assert.Single(exhausted);
    }

    [Fact]
    public async Task Reconciliation_budget_is_not_rearmed_by_a_utc_day_rollover()
    {
        // This wave lost its evidence more than a day ago, so every pass lands on a later
        // UTC day than the last one. The attempt budget must still terminate the re-drive.
        var longStaleEvidence = LastEvidenceUtc.AddDays(-1).AddHours(-2);
        using var host = new WatchdogHost(maxReconciliations: 2);
        var runId = host.SeedStalledRun("wave-watchdog-long-stale", longStaleEvidence);

        for (var hour = 0; hour < 5; hour++)
        {
            host.Clock.AdvanceTo(FirstPassUtc.AddHours(hour));
            await host.ReconcileAsync("hourly_watchdog");
        }

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);
        Assert.Equal(2, run.ReconciliationCount);
        Assert.Equal(longStaleEvidence, run.LastEventAt);
    }

    [Fact]
    public async Task Stalled_run_is_still_reconciled_after_the_utc_day_boundary_while_budget_remains()
    {
        // Guards against "fixing" the unbounded re-drive with a calendar-day filter: a run
        // with budget left must keep being re-driven on the far side of midnight, and each
        // accepted pass must move the persisted scheduling timestamp forward.
        using var host = new WatchdogHost(maxReconciliations: 5);
        var runId = host.SeedStalledRun("wave-watchdog-rollover", LastEvidenceUtc);

        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 16, 22, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);
        Assert.Equal(3, run.ReconciliationCount);
        Assert.Equal(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), run.UpdatedAt);
        Assert.Equal(LastEvidenceUtc, run.LastEventAt);
        Assert.Equal(AutopilotRunStates.InProgress, run.State);
        Assert.Empty(await db.AutopilotGateEvaluations.Where(item => item.RunId == runId).ToListAsync());
    }

    [Fact]
    public async Task Freshly_arrived_evidence_re_arms_recovery_for_the_next_stall_episode()
    {
        // The budget is spent per stall episode, not per run lifetime. A parked run stays
        // parked - here across the UTC day boundary - until real evidence for the wave
        // arrives, and only that evidence makes the watchdog willing to recover it again.
        using var host = new WatchdogHost(maxReconciliations: 1);

        // Declared three tasks wide with one succeeded: the wave cannot conclude, which is
        // what keeps it stalled between watchdog passes.
        var runId = host.SeedStalledRun("wave-watchdog-rearm", LastEvidenceUtc, expectedTaskCount: 3);

        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ReconcileAsync("hourly_watchdog");
        using (var db = host.CreateContext())
        {
            Assert.Equal(1, (await db.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);
        }

        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");
        using (var db = host.CreateContext())
        {
            Assert.Equal(1, (await db.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);
        }

        // The second task reports in: genuine new evidence for the same wave.
        var evidenceAt = new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero);
        host.Clock.AdvanceTo(evidenceAt);
        var eventId = host.SeedCompletionEvent("wave-watchdog-rearm", "wave-watchdog-rearm-task-2");
        await host.ProcessEventAsync(eventId);
        using (var db = host.CreateContext())
        {
            var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);
            Assert.Equal(0, run.ReconciliationCount);
            Assert.Equal(evidenceAt.UtcDateTime, run.LastEventAt);
        }

        // The wave stalls again, and recovery is available for the new episode.
        host.Clock.AdvanceTo(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero));
        await host.ReconcileAsync("hourly_watchdog");
        using var afterStall = host.CreateContext();
        var reArmed = await afterStall.AutopilotRuns.SingleAsync(item => item.Id == runId);
        Assert.Equal(1, reArmed.ReconciliationCount);

        // Re-driving the state machine must never invent evidence for the wave.
        Assert.Equal(evidenceAt.UtcDateTime, reArmed.LastEventAt);
    }

    /// <summary>
    /// A SQLite-backed harness for the watchdog reconciliation pass. The follow-on wave
    /// launcher and the distributed lock are stubbed, so no launch provider is contacted
    /// and the only clock in the test is the injected one.
    /// </summary>
    private sealed class WatchdogHost : IDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public WatchdogHost(int maxReconciliations)
        {
            connection.Open();
            Clock = new SteppingClock(new DateTimeOffset(LastEvidenceUtc, TimeSpan.Zero));
            Options = new AutopilotOptions
            {
                Enabled = true,
                DryRun = true,
                SimulationMode = true,
                RequireSignedEvents = false,
                EventStaleAfterMinutes = 30,
                MaxWatchdogReconciliations = maxReconciliations,
            };
            Options.Normalize();
            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public AutopilotOptions Options { get; }

        public SteppingClock Clock { get; }

        public TaslimDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

        /// <summary>
        /// Seeds a declared wave that is parked in progress with one succeeded task and no
        /// outstanding events, which is exactly the shape the watchdog treats as "the
        /// completion event was lost". The wave is declared wider than the evidence on
        /// record, so the state machine can never conclude it between passes.
        /// </summary>
        public Guid SeedStalledRun(string waveKey, DateTime lastEvidenceUtc, int expectedTaskCount = 2)
        {
            var runId = Guid.NewGuid();
            using var db = CreateContext();
            db.AutopilotRuns.Add(new AutopilotRun
            {
                Id = runId,
                WaveKey = waveKey,
                RunKey = $"{waveKey}-run",
                State = AutopilotRunStates.InProgress,
                BaseSha = BaseSha,
                CandidateSha = CandidateSha,
                Branch = Branch,
                TaskCount = 1,
                SucceededTaskCount = 1,
                ExpectedTaskCount = expectedTaskCount,
                CreatedAt = lastEvidenceUtc,
                UpdatedAt = lastEvidenceUtc,
                LastEventAt = lastEvidenceUtc,
                ConcurrencyToken = Guid.NewGuid(),
            });
            db.AutopilotWaveTasks.Add(new AutopilotWaveTask
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                WaveKey = waveKey,
                TaskId = $"{waveKey}-task-1",
                State = AutopilotTaskStates.Succeeded,
                Attempt = 1,
                MaxAttempts = 3,
                Branch = Branch,
                BaseSha = BaseSha,
                CandidateSha = CandidateSha,
                CreatedAt = lastEvidenceUtc,
                UpdatedAt = lastEvidenceUtc,
                LastEventAt = lastEvidenceUtc,
                CompletedAt = lastEvidenceUtc,
            });
            db.SaveChanges();
            return runId;
        }

        /// <summary>
        /// Seeds a non-terminal event for a wave and defers it past the end of the test, so
        /// the only thing it can influence is the watchdog's pending-event test.
        /// </summary>
        public void SeedPendingEvent(string waveKey)
        {
            using var db = CreateContext();
            db.AutopilotEvents.Add(new AutopilotEvent
            {
                Id = Guid.NewGuid(),
                EventType = AutopilotEventTypes.WaveTaskCompleted,
                SourceSystem = "bridge",
                ExternalEventId = $"{waveKey}-pending-1",
                IdempotencyKey = $"{waveKey}-pending-1",
                PayloadHash = "sha256:pending",
                Status = AutopilotEventStatuses.Queued,
                WaveKey = waveKey,
                TaskId = $"{waveKey}-task-2",
                ReceivedAt = LastEvidenceUtc,
                QueuedAt = LastEvidenceUtc,
                NextAttemptAt = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
                ConcurrencyToken = Guid.NewGuid(),
            });
            db.SaveChanges();
        }

        /// <summary>
        /// Seeds a durable completion event for one task of a wave and returns its id, so a
        /// test can hand the controller real evidence for a wave the watchdog parked.
        /// </summary>
        public Guid SeedCompletionEvent(string waveKey, string taskId)
        {
            var eventId = Guid.NewGuid();
            var signalJson = JsonSerializer.Serialize(new
            {
                waveKey,
                taskId,
                outcome = "succeeded",
                branch = Branch,
                baseSha = BaseSha,
                candidateSha = CandidateSha,
                attempt = 1,
                evidence = "bounded evidence",
                expectedTaskCount = 3,
                checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["build"] = true,
                    ["unit_tests"] = true,
                    ["typecheck"] = true,
                },
            });
            using var db = CreateContext();
            db.AutopilotEvents.Add(new AutopilotEvent
            {
                Id = eventId,
                EventType = AutopilotEventTypes.WaveTaskCompleted,
                SourceSystem = "bridge",
                ExternalEventId = $"{taskId}-done",
                IdempotencyKey = $"{taskId}-done",
                PayloadHash = "sha256:completion",
                Status = AutopilotEventStatuses.Queued,
                WaveKey = waveKey,
                TaskId = taskId,
                Branch = Branch,
                BaseSha = BaseSha,
                CandidateSha = CandidateSha,
                Attempt = 1,
                ReceivedAt = Clock.GetUtcNow().UtcDateTime,
                QueuedAt = Clock.GetUtcNow().UtcDateTime,
                ConcurrencyToken = Guid.NewGuid(),
                SignalJson = signalJson,
            });
            db.SaveChanges();
            return eventId;
        }

        public Task<AutopilotCycleResult> ReconcileAsync(string reason) => RunCycleAsync(db => db.ReconcileAsync(reason));

        public Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId) => RunCycleAsync(db => db.ProcessEventAsync(eventId));

        private Task<AutopilotCycleResult> RunCycleAsync(Func<AutopilotOrchestrator, Task<AutopilotCycleResult>> action)
        {
            using var db = CreateContext();
            var orchestrator = new AutopilotOrchestrator(
                db,
                new AlwaysFreeLockService(),
                new IdleNextWaveService(),
                Options,
                Clock,
                NullLogger<AutopilotOrchestrator>.Instance);
            return action(orchestrator);
        }

        public void Dispose()
        {
            connection.Close();
            connection.Dispose();
        }
    }

    /// <summary>Synthetic UTC clock. Nothing in the reconciliation path reads the machine time zone.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset now;

        public SteppingClock(DateTimeOffset start) => now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void AdvanceTo(DateTimeOffset instant) => now = instant;
    }

    private sealed class IdleNextWaveService : IAutopilotNextWaveService
    {
        public Task<AutopilotNextWaveCycleResult> TryLaunchNextWavesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotNextWaveCycleResult.Idle("idle", "no_launch_enabled"));

        public Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotNextWaveCycleResult.Idle("idle", "no_launch_enabled"));
    }

    private sealed class AlwaysFreeLockService : IAutopilotLockService
    {
        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(resourceKey, "test-owner", 1, true, heldBy, DateTime.UnixEpoch));

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}

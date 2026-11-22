using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Concurrency fence between the watchdog's stalled-run recovery pass and the
/// event-processing path. Both write the same <see cref="AutopilotRun"/> row, so
/// both must serialize behind the same exclusive wave lease, and the lease must be
/// keyed by the same trimmed wave key the run itself is keyed by - a lease taken on
/// a raw, whitespace-padded signal spelling does not cover the trimmed run it
/// writes. Every case runs against the real database-backed lease over SQLite with
/// a synthetic orchestrator clock; contention is forced by moving the persisted
/// lease expiry, never by sleeping or by racing threads, so nothing is
/// wall-clock dependent and nothing reaches a provider, a wallet, or a webhook.
/// </summary>
public sealed class AutopilotWatchdogLeaseFenceTests : IDisposable
{
    private const string Branch = "fix/autopilot-watchdog-lease-fence";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "2222222222222222222222222222222222222222";

    /// <summary>21:00Z on 16 Nov 2026: the last persisted evidence instant of the stalled wave.</summary>
    private static readonly DateTime LastEvidenceUtc = new(2026, 11, 16, 21, 0, 0, DateTimeKind.Utc);

    /// <summary>22:00Z: one hour after the last evidence, outside the 30 minute stale window.</summary>
    private static readonly DateTimeOffset FirstPassUtc = new(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Watchdog_recovery_holds_and_releases_the_same_wave_lease_the_event_path_takes()
    {
        using var host = new LeaseHost(maxReconciliations: 5);
        var runId = host.SeedStalledRun("wave-lease-recover", LastEvidenceUtc);

        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ReconcileAsync("hourly_watchdog");

        // The recovery pass must create a lease row on exactly the run's wave resource
        // and leave it released: held by the watchdog role, but with no live owner.
        using var db = host.CreateContext();
        var lease = await db.AutopilotLocks.SingleAsync(
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-recover"));
        Assert.Equal("autopilot-watchdog", lease.HeldBy);
        Assert.Equal(string.Empty, lease.OwnerToken);
        Assert.Equal(1, lease.FencingToken);
        Assert.Equal(1, (await db.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);

        // A second pass must take over its own released lease, so fencing advances and
        // recovery remains the single writer of the run on every pass.
        host.Clock.AdvanceTo(FirstPassUtc.AddHours(1));
        await host.ReconcileAsync("hourly_watchdog");
        using var after = host.CreateContext();
        var fenced = await after.AutopilotLocks.SingleAsync(
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-recover"));
        Assert.Equal(2, fenced.FencingToken);
        Assert.Equal(string.Empty, fenced.OwnerToken);
        Assert.Equal(2, (await after.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);
    }

    [Fact]
    public async Task Watchdog_recovery_skips_a_run_whose_wave_lease_is_live_without_spending_budget()
    {
        using var host = new LeaseHost(maxReconciliations: 5);
        var runId = host.SeedStalledRun("wave-lease-held", LastEvidenceUtc);
        host.SeedLiveWaveLease("wave-lease-held", holder: "autopilot-controller", fencingToken: 5);

        host.Clock.AdvanceTo(FirstPassUtc);
        var result = await host.ReconcileAsync("hourly_watchdog");

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);

        // The live holder owns the run write: recovery must not touch the row, must not
        // consume the bounded budget for a skip it never performed, and must not
        // disturb the holder's lease.
        Assert.Equal(0, run.ReconciliationCount);
        Assert.Equal(LastEvidenceUtc, run.LastEventAt);
        Assert.Equal(LastEvidenceUtc, run.UpdatedAt);
        Assert.Equal(AutopilotRunStates.InProgress, run.State);
        Assert.Equal(1, result.EventsSkipped);

        var lease = await db.AutopilotLocks.SingleAsync(
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-held"));
        Assert.Equal("controller-holder-token", lease.OwnerToken);
        Assert.Equal(5, lease.FencingToken);
        Assert.Equal("autopilot-controller", lease.HeldBy);

        // The skip is surfaced so an operator can tell contention from a quiet pass.
        Assert.Single(await db.AutopilotAuditEvents
            .Where(item => item.Action == AutopilotAuditActions.LockContended
                && item.Outcome == AutopilotAuditOutcomes.Contended
                && item.Reason == "watchdog_wave_lease_contended"
                && item.TargetId == runId)
            .ToListAsync());
    }

    [Fact]
    public async Task Watchdog_recovery_resumes_through_a_fencing_takeover_once_the_holders_lease_expires()
    {
        using var host = new LeaseHost(maxReconciliations: 5);
        var runId = host.SeedStalledRun("wave-lease-takeover", LastEvidenceUtc);
        host.SeedLiveWaveLease("wave-lease-takeover", holder: "autopilot-controller", fencingToken: 5);

        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ReconcileAsync("hourly_watchdog");
        using (var db = host.CreateContext())
        {
            Assert.Equal(0, (await db.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);
        }

        // The holder died without releasing: forcing the persisted expiry into the past
        // is the only takeover trigger, and the taker is fenced to the next token.
        host.ExpireWaveLease("wave-lease-takeover");
        host.Clock.AdvanceTo(FirstPassUtc.AddHours(1));
        await host.ReconcileAsync("hourly_watchdog");

        using var after = host.CreateContext();
        Assert.Equal(1, (await after.AutopilotRuns.SingleAsync(item => item.Id == runId)).ReconciliationCount);
        var lease = await after.AutopilotLocks.SingleAsync(
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-takeover"));
        Assert.Equal(6, lease.FencingToken);
        Assert.Equal(string.Empty, lease.OwnerToken);
        Assert.Equal("autopilot-watchdog", lease.HeldBy);
    }

    [Fact]
    public async Task Event_lease_for_a_padded_wave_key_covers_the_trimmed_run_it_writes()
    {
        using var host = new LeaseHost(maxReconciliations: 5);

        // A bridge may post the same wave with surrounding whitespace; the run it
        // creates is keyed by the trimmed spelling, so the exclusive lease must be too.
        var eventId = host.SeedCompletionEvent("  wave-lease-pad  ", "wave-lease-pad-task-1");
        host.Clock.AdvanceTo(FirstPassUtc);
        await host.ProcessEventAsync(eventId);

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == "wave-lease-pad");
        Assert.Equal(AutopilotRunStates.InProgress, run.State);

        var leases = await db.AutopilotLocks.ToListAsync();

        // Exactly one lease exists and it guards the resource the run row is keyed by;
        // the padded spelling must never name a lease of its own.
        var trimmed = Assert.Single(leases,
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-pad"));
        Assert.Equal("autopilot-controller", trimmed.HeldBy);
        Assert.Equal(string.Empty, trimmed.OwnerToken);
        Assert.DoesNotContain(leases,
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("  wave-lease-pad  "));
    }

    [Fact]
    public async Task Padded_wave_key_event_defers_instead_of_writing_a_run_the_lease_holder_owns()
    {
        using var host = new LeaseHost(maxReconciliations: 5);
        var runId = host.SeedStalledRun("wave-lease-cross", LastEvidenceUtc);

        // The trimmed resource is leased live - the shape the watchdog recovery pass and
        // any trimmed-key event pass now contend on.
        host.SeedLiveWaveLease("wave-lease-cross", holder: "autopilot-watchdog", fencingToken: 3);

        // Same wave, raw padded spelling, arriving while the lease is live.
        var eventId = host.SeedCompletionEvent("wave-lease-cross ", "wave-lease-cross-task-2");
        host.Clock.AdvanceTo(FirstPassUtc);
        var result = await host.ProcessEventAsync(eventId);

        using var db = host.CreateContext();
        var deferred = await db.AutopilotEvents.SingleAsync(item => item.Id == eventId);
        var run = await db.AutopilotRuns.SingleAsync(item => item.Id == runId);

        // With both paths keyed to the trimmed resource, the event defers and the run
        // the holder is writing stays untouched. Keyed to raw spellings, this event
        // would silently take a different lease and rewrite the same run concurrently.
        Assert.Equal(AutopilotEventStatuses.Queued, deferred.Status);
        Assert.Equal("wave_lock_contended", deferred.Reason);
        Assert.Equal(FirstPassUtc.UtcDateTime.AddSeconds(Math.Max(5, host.Options.RetryBaseDelaySeconds)), deferred.NextAttemptAt);
        Assert.Equal(LastEvidenceUtc, run.LastEventAt);
        Assert.Equal(LastEvidenceUtc, run.UpdatedAt);
        Assert.Equal(0, run.ReconciliationCount);
        Assert.Equal("contended", result.Outcome);

        var lease = await db.AutopilotLocks.SingleAsync(
            item => item.ResourceKey == EfAutopilotLockService.WaveResource("wave-lease-cross"));
        Assert.Equal("watchdog-holder-token", lease.OwnerToken);
        Assert.Equal(3, lease.FencingToken);

        Assert.Single(await db.AutopilotAuditEvents
            .Where(item => item.Action == AutopilotAuditActions.LockContended
                && item.Reason == "wave_lock_contended"
                && item.TargetId == eventId)
            .ToListAsync());
    }

    public void Dispose()
    {
        // Nothing unmanaged is held at class level; each case owns its host via using.
    }

    /// <summary>
    /// A SQLite-backed harness for the lease-fenced controller paths. Unlike the bound
    /// tests, this host wires the real <see cref="EfAutopilotLockService"/> so the
    /// tests observe the same lease rows any second controller instance would contend
    /// against. The follow-on wave launcher is stubbed to idle, so no launch path is
    /// reachable; the orchestrator clock is synthetic and lease expiry is manipulated
    /// through the persisted row only.
    /// </summary>
    private sealed class LeaseHost : IDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public LeaseHost(int maxReconciliations)
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
        /// Seeds a wave parked in progress with one succeeded task, no outstanding
        /// events, and a declared width it has not reached - the shape the watchdog
        /// treats as "the completion event was lost".
        /// </summary>
        public Guid SeedStalledRun(string waveKey, DateTime lastEvidenceUtc)
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
                ExpectedTaskCount = 2,
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
        /// Seeds a live wave lease written by a different role, as an in-flight event
        /// pass on another controller instance would hold it. Expiry uses the lease
        /// service's own wall clock, matching how a real live lease reads.
        /// </summary>
        public void SeedLiveWaveLease(string waveKey, string holder, long fencingToken)
        {
            var now = DateTime.UtcNow;
            using var db = CreateContext();
            db.AutopilotLocks.Add(new AutopilotLock
            {
                Id = Guid.NewGuid(),
                ResourceKey = EfAutopilotLockService.WaveResource(waveKey),
                OwnerToken = holder == "autopilot-watchdog" ? "watchdog-holder-token" : "controller-holder-token",
                FencingToken = fencingToken,
                AcquiredAt = now,
                ExpiresAt = now.AddMinutes(10),
                HeldBy = holder,
            });
            db.SaveChanges();
        }

        /// <summary>Forces the persisted expiry into the past: the only takeover trigger.</summary>
        public void ExpireWaveLease(string waveKey)
        {
            using var db = CreateContext();
            db.AutopilotLocks
                .Where(item => item.ResourceKey == EfAutopilotLockService.WaveResource(waveKey))
                .ExecuteUpdate(setters => setters.SetProperty(item => item.ExpiresAt, DateTime.UtcNow.AddMinutes(-10)));
        }

        /// <summary>
        /// Seeds a durable completion event whose signal carries the wave key with the
        /// given (possibly whitespace-padded) spelling, exactly as intake persists it.
        /// </summary>
        public Guid SeedCompletionEvent(string signalWaveKey, string taskId)
        {
            var eventId = Guid.NewGuid();
            var signalJson = JsonSerializer.Serialize(new
            {
                waveKey = signalWaveKey,
                taskId,
                outcome = "succeeded",
                branch = Branch,
                baseSha = BaseSha,
                candidateSha = CandidateSha,
                attempt = 1,
                evidence = "bounded evidence",
                expectedTaskCount = 2,
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
                ExternalEventId = $"{eventId:N}",
                IdempotencyKey = $"lease-{eventId:N}",
                PayloadHash = "sha256:completion",
                Status = AutopilotEventStatuses.Queued,
                WaveKey = signalWaveKey,
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

        public Task<AutopilotCycleResult> ReconcileAsync(string reason) => RunCycleAsync(orchestrator => orchestrator.ReconcileAsync(reason));

        public Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId) => RunCycleAsync(orchestrator => orchestrator.ProcessEventAsync(eventId));

        private async Task<AutopilotCycleResult> RunCycleAsync(Func<AutopilotOrchestrator, Task<AutopilotCycleResult>> action)
        {
            using var db = CreateContext();
            var orchestrator = new AutopilotOrchestrator(
                db,
                new EfAutopilotLockService(db, Options),
                new IdleNextWaveService(),
                Options,
                Clock,
                NullLogger<AutopilotOrchestrator>.Instance);
            return await action(orchestrator);
        }

        public void Dispose()
        {
            connection.Close();
            connection.Dispose();
        }
    }

    /// <summary>Synthetic UTC clock for every orchestrator-side decision.</summary>
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
}

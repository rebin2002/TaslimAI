using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Synthetic concurrency proof for TaslimAI #1029 item 3: cross-controller
/// admission serialization. Two independently scheduled refill controllers
/// (each with its own allocator instance and DbContext, mirroring separate
/// processes) both observe an authoritative 19/20 active-worker inventory and
/// compete for the final global slot. Exactly one may be admitted; an
/// ambiguous provider create must retain the 20-slot ceiling and must never
/// authorize a second, duplicate launch. All fixtures are synthetic: no real
/// Manus task is created, no provider is called, and no production code path
/// is wired by this file.
///
/// This is deliberately additive over the #704 allocator component. The
/// PostgreSQL advisory-fence variant of this scenario already lives in
/// <see cref="AutopilotGlobalTaskSlotLeaseTests"/> but is gated on a live CI
/// database; the proofs below exercise the portable (SQLite/serializable)
/// admission path that always runs, pinning the 19/20 two-controller race and
/// the create-timeout ambiguity contract independently of that gate.
/// </summary>
public sealed class AutopilotCrossControllerAdmissionSerializationTests : IAsyncLifetime
{
    private const string ScopeId = "taslimai-autopilot";
    private const int ObservedExternalActiveWorkers = 19;
    private static readonly TimeSpan LaunchLease = TimeSpan.FromMinutes(5);

    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-1029-admission-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<TaslimDbContext> options;

    public AutopilotCrossControllerAdmissionSerializationTests()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
        options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connectionString)
            .EnableDetailedErrors()
            .Options;
    }

    public async Task InitializeAsync()
    {
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Two_independently_scheduled_controllers_competing_for_the_final_slot_admit_exactly_one()
    {
        // Two planned rows, one per simulated controller, each in its own wave.
        var (controllerARow, controllerBRow) = await SeedControllerTaskRowsAsync();
        var inventory = AuthoritativeInventoryWithObservedWorkers(ObservedExternalActiveWorkers);

        // Fire both controllers concurrently. Each uses its own DbContext and
        // allocator instance, the in-process equivalent of two separately
        // scheduled controllers sharing one durable slot ledger.
        var claims = await Task.WhenAll(
            RunControllerClaimAsync(controllerARow, inventory),
            RunControllerClaimAsync(controllerBRow, inventory));

        var admitted = claims.Where(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved).ToArray();
        var denied = claims.Where(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached).ToArray();
        Assert.Single(admitted);
        Assert.Single(denied);

        var winner = admitted[0];
        var loser = denied[0];
        Assert.True(winner.Result.SlotHeld);
        Assert.True(winner.Result.MayCallProvider);
        Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, winner.Result.ActiveSlotsObserved);
        Assert.False(loser.Result.SlotHeld);
        Assert.False(loser.Result.MayCallProvider);
        Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, loser.Result.ActiveSlotsObserved);

        // Count authorizations, not provider calls: this test never calls one.
        Assert.Equal(1, claims.Count(item => item.Result.MayCallProvider));
        Assert.All(claims, item => Assert.True(item.Result.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));

        await using var verify = new TaslimDbContext(options);
        var localActive = await verify.AutopilotWaveLaunchTasks
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched)
            .ToListAsync();
        // Only the winner holds a durable local reservation; the denied
        // controller must not leave a second occupancy marker behind.
        Assert.Single(localActive);
        Assert.Equal(winner.RowId, localActive[0].Id);
        Assert.Equal("global_slot_reserved", localActive[0].ReasonCode);
        var loserRow = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == controllerBRow);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, loserRow.Status);
        // 19 externally observed workers + exactly 1 new local reservation = 20, never more.
        Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, ObservedExternalActiveWorkers + localActive.Count);
    }

    [Fact]
    public async Task Ambiguous_provider_create_timeout_at_full_occupancy_retains_the_ceiling_and_never_authorizes_a_duplicate_launch()
    {
        var (controllerARow, controllerBRow) = await SeedControllerTaskRowsAsync();
        var inventory = AuthoritativeInventoryWithObservedWorkers(ObservedExternalActiveWorkers);
        var clock = new StableTimeProvider(DateTimeOffset.Parse("2026-11-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var authorizations = 0;

        string keyA;
        // Step 1: controller A wins the final slot.
        await using (var db = new TaslimDbContext(options))
        {
            keyA = await IdempotencyKeyAsync(db, controllerARow);
            var reserved = await new AutopilotGlobalTaskSlotAllocator(
                db, ScopeId, clock, AutopilotGlobalTaskSlotAllocator.HardMaximum)
                .ReserveOrRecoverLaunchAsync(controllerARow, keyA, inventory, LaunchLease, providerGuaranteesIdempotency: false);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reserved.Outcome);
            Assert.True(reserved.MayCallProvider);
            authorizations += reserved.MayCallProvider ? 1 : 0;
        }

        // Step 2: A's provider create call times out ambiguously (outcome
        // unknown; a task may or may not exist upstream).
        await using (var db = new TaslimDbContext(options))
        {
            var timedOut = await new AutopilotGlobalTaskSlotAllocator(
                db, ScopeId, clock, AutopilotGlobalTaskSlotAllocator.HardMaximum)
                .RecordAmbiguousAsync(controllerARow, keyA, "provider_create_timeout");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, timedOut.Outcome);
            Assert.True(timedOut.SlotHeld);
            Assert.False(timedOut.MayCallProvider);
        }

        // Step 3: controller B re-counts under the same 19-worker census and
        // still must be denied: the ambiguous row keeps occupying slot 20.
        await using (var db = new TaslimDbContext(options))
        {
            var keyB = await IdempotencyKeyAsync(db, controllerBRow);
            var denied = await new AutopilotGlobalTaskSlotAllocator(
                db, ScopeId, clock, AutopilotGlobalTaskSlotAllocator.HardMaximum)
                .ReserveOrRecoverLaunchAsync(controllerBRow, keyB, inventory, LaunchLease, providerGuaranteesIdempotency: false);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, denied.Outcome);
            Assert.False(denied.SlotHeld);
            Assert.False(denied.MayCallProvider);
            Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, denied.ActiveSlotsObserved);
        }

        // Step 4: A re-enters its own admission path inside the unexpired
        // lease. It must observe its in-flight launch, not a fresh permission.
        await using (var db = new TaslimDbContext(options))
        {
            var inFlight = await new AutopilotGlobalTaskSlotAllocator(
                db, ScopeId, clock, AutopilotGlobalTaskSlotAllocator.HardMaximum)
                .ReserveOrRecoverLaunchAsync(controllerARow, keyA, inventory, LaunchLease, providerGuaranteesIdempotency: false);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, inFlight.Outcome);
            Assert.True(inFlight.SlotHeld);
            Assert.False(inFlight.MayCallProvider);
        }

        // Step 5: the lease expires while the create outcome is still unknown
        // and provider idempotency is not proven. The allocator must fail
        // closed: retain the slot, refuse authorization, never auto-retry.
        clock.Advance(LaunchLease + TimeSpan.FromSeconds(1));
        await using (var db = new TaslimDbContext(options))
        {
            var expiredUnverified = await new AutopilotGlobalTaskSlotAllocator(
                db, ScopeId, clock, AutopilotGlobalTaskSlotAllocator.HardMaximum)
                .ReserveOrRecoverLaunchAsync(controllerARow, keyA, inventory, LaunchLease, providerGuaranteesIdempotency: false);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, expiredUnverified.Outcome);
            Assert.True(expiredUnverified.SlotHeld);
            Assert.False(expiredUnverified.MayCallProvider);
        }

        Assert.Equal(1, authorizations);

        await using var verify = new TaslimDbContext(options);
        var activeRows = await verify.AutopilotWaveLaunchTasks
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched)
            .ToListAsync();
        Assert.Single(activeRows);
        var held = activeRows[0];
        Assert.Equal(controllerARow, held.Id);
        Assert.Equal("global_slot_reserved", held.ReasonCode);
        Assert.StartsWith("slot_retained_until_authoritative_reconciliation:provider_create_timeout", held.LastStatusDetail);
        // No provider acceptance was ever recorded, so no second external task
        // identity exists anywhere in the ledger: 19 external + 1 retained
        // local slot = 20 with no duplicate.
        Assert.Null(held.ExternalTaskId);
        Assert.Equal(19, inventory.ActiveTasks.Count);
        Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, inventory.ActiveTasks.Count + activeRows.Count);
        Assert.Equal(1, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.WaveKey == held.WaveKey && item.TaskKey == held.TaskKey));
    }

    private sealed record ControllerClaim(Guid RowId, AutopilotGlobalTaskSlotResult Result);

    private async Task<ControllerClaim> RunControllerClaimAsync(Guid rowId, AutopilotGlobalTaskInventorySnapshot inventory)
    {
        await using var db = new TaslimDbContext(options);
        var key = await IdempotencyKeyAsync(db, rowId);
        var allocator = new AutopilotGlobalTaskSlotAllocator(
            db, ScopeId, TimeProvider.System, AutopilotGlobalTaskSlotAllocator.HardMaximum);
        var result = await allocator.ReserveOrRecoverLaunchAsync(
            rowId, key, inventory, LaunchLease, providerGuaranteesIdempotency: false);
        return new ControllerClaim(rowId, result);
    }

    private static async Task<string> IdempotencyKeyAsync(TaslimDbContext db, Guid rowId)
    {
        var task = await db.AutopilotWaveLaunchTasks.AsNoTracking().SingleAsync(item => item.Id == rowId);
        return AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
    }

    private async Task<(Guid ControllerA, Guid ControllerB)> SeedControllerTaskRowsAsync()
    {
        var now = DateTime.UtcNow;
        await using var db = new TaslimDbContext(options);
        var ids = new List<Guid>(2);
        for (var controller = 0; controller < 2; controller++)
        {
            var waveKey = $"synthetic-controller-{controller}-wave";
            var run = new AutopilotRun
            {
                Id = Guid.NewGuid(),
                WaveKey = waveKey,
                RunKey = $"run-{waveKey}",
                State = AutopilotRunStates.InProgress,
                BaseSha = new string('a', 40),
                CandidateSha = new string('b', 40),
                Branch = $"feature/{waveKey}",
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            };
            var batch = new AutopilotWaveLaunchBatch
            {
                Id = Guid.NewGuid(),
                SourceRunId = run.Id,
                SourceWaveKey = waveKey,
                WaveKey = $"{waveKey}-next",
                Status = AutopilotWaveLaunchStatuses.Planned,
                BaseSha = run.CandidateSha,
                Branch = run.Branch,
                PlanFingerprint = "synthetic-1029-fingerprint",
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            };
            db.AutopilotRuns.Add(run);
            db.AutopilotWaveLaunchBatches.Add(batch);
            var rowId = Guid.NewGuid();
            db.AutopilotWaveLaunchTasks.Add(new AutopilotWaveLaunchTask
            {
                Id = rowId,
                BatchId = batch.Id,
                SourceRunId = run.Id,
                SourceWaveKey = waveKey,
                WaveKey = batch.WaveKey,
                TaskKey = $"final-slot-candidate-{controller}",
                Title = $"Synthetic final-slot candidate {controller}",
                BacklogItemKey = $"synthetic-backlog-{controller}",
                Status = AutopilotWaveLaunchStatuses.Planned,
                Branch = run.Branch,
                BaseSha = run.CandidateSha,
                LaunchAttempt = 0,
                MaxLaunchAttempts = 3,
                ReconcileCount = 0,
                MaxReconciles = 10,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            });
            ids.Add(rowId);
        }

        await db.SaveChangesAsync();
        return (ids[0], ids[1]);
    }

    private static AutopilotGlobalTaskInventorySnapshot AuthoritativeInventoryWithObservedWorkers(int activeWorkers) =>
        new(
            ScopeId,
            IsAuthoritative: true,
            PagesRead: 1,
            HasMorePages: false,
            FailureCode: null,
            ActiveTasks: Enumerable.Range(0, activeWorkers)
                .Select(index => new AutopilotGlobalTaskInventoryItem(
                    TaskId: $"synthetic-active-worker-{index:D3}",
                    ScopeId: ScopeId,
                    Status: "running",
                    IdempotencyKey: null))
                .ToArray());

    private sealed class StableTimeProvider : TimeProvider
    {
        private DateTimeOffset current;

        public StableTimeProvider(DateTimeOffset initial) => current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan elapsed) => current = current.Add(elapsed);
    }
}

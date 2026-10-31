using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AutopilotGlobalTaskSlotLeaseTests : IAsyncLifetime
{
    private const string ScopeId = "taslimai-autopilot";
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-global-slots-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<TaslimDbContext> options;

    public AutopilotGlobalTaskSlotLeaseTests()
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
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Concurrent_claims_across_waves_never_exceed_the_global_twenty_slots()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 0);
        var inventory = CompleteInventory();

        var results = await Task.WhenAll(taskIds.Select(id => ClaimAsync(id, inventory)));

        Assert.Equal(20, results.Count(item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved));
        Assert.Single(results, item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached);
        Assert.All(results, item => Assert.True(item.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));

        await using var verify = CreateContext();
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task Verified_completion_frees_exactly_one_slot_and_replay_does_not_free_another()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 22, initiallyActive: 20, tasksPerWave: 22);
        var inventory = CompleteInventory();
        var firstExternalId = "external-task-0";
        string key;
        await using (var db = CreateContext())
        {
            var first = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotNextWavePolicy.LaunchIdempotencyKey(first.WaveKey, first.TaskKey);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var completed = await allocator.CompleteVerifiedAsync(taskIds[0], key, firstExternalId, terminalEvidenceVerified: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completed.Outcome);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var replay = await allocator.CompleteVerifiedAsync(taskIds[0], key, firstExternalId, terminalEvidenceVerified: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion, replay.Outcome);
        }

        var reacquireCompleted = await ClaimAsync(taskIds[0], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, reacquireCompleted.Outcome);
        Assert.False(reacquireCompleted.MayCallProvider);

        var replacement = await ClaimAsync(taskIds[20], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, replacement.Outcome);
        Assert.Equal(20, replacement.ActiveSlotsObserved);

        var twentyFirst = await ClaimAsync(taskIds[21], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, twentyFirst.Outcome);

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AutopilotWaveLaunchTasks.CountAsync(item => item.Status == AutopilotWaveLaunchStatuses.Completed));
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[1])).Status);
        var completedTask = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
        var sameWaveRefill = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[20]);
        Assert.Equal(completedTask.BatchId, sameWaveRefill.BatchId);
    }

    [Fact]
    public async Task Running_task_keeps_its_slot_after_launch_claim_lease_expires()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 20);
        await using (var db = CreateContext())
        {
            var active = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .ToListAsync();
            foreach (var task in active) task.UpdatedAt = DateTime.UtcNow.AddDays(-30);
            await db.SaveChangesAsync();
        }

        var result = await ClaimAsync(taskIds[20], CompleteInventory());

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, result.Outcome);
        Assert.Equal(20, result.ActiveSlotsObserved);
    }

    [Fact]
    public async Task Unknown_or_partial_external_occupancy_fails_closed_without_creating_a_claim()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var unknown = AutopilotGlobalTaskInventorySnapshot.Unknown(ScopeId, "inventory_page_limit_exceeded", pagesRead: 1);

        var result = await ClaimAsync(taskIds[0], unknown);

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, result.Outcome);
        await using var verify = CreateContext();
        var task = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
        Assert.Equal(0, task.LaunchAttempt);
    }

    [Fact]
    public async Task Inventory_reader_paginates_and_counts_only_the_exact_taslim_scope()
    {
        var source = new FakePageSource(
            new AutopilotGlobalTaskInventoryPage(ScopeId,
                [
                    new("taslim-1", ScopeId, "running", "autopilot:w1:t1"),
                    new("other-project", "unrelated-project", "running", "other:key"),
                ],
                "next-page"),
            new AutopilotGlobalTaskInventoryPage(ScopeId,
                [
                    new("taslim-done", ScopeId, "completed", "autopilot:w1:done"),
                    new("taslim-2", ScopeId, "awaiting_input", "autopilot:w2:t2"),
                ],
                null));
        var reader = new AutopilotGlobalTaskInventoryReader(source, ScopeId);

        var snapshot = await reader.ReadAuthoritativeSnapshotAsync();

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(2, snapshot.PagesRead);
        Assert.False(snapshot.HasMorePages);
        Assert.Equal(new[] { "taslim-1", "taslim-2" }, snapshot.ActiveTasks.Select(item => item.TaskId).ToArray());
        Assert.DoesNotContain(snapshot.ActiveTasks, item => item.TaskId == "other-project");
        Assert.DoesNotContain(snapshot.ActiveTasks, item => item.TaskId == "taslim-done");
    }

    [Fact]
    public async Task Repeated_pagination_cursor_is_unknown_not_zero_occupancy()
    {
        var source = new FakePageSource(
            new AutopilotGlobalTaskInventoryPage(ScopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), "same"),
            new AutopilotGlobalTaskInventoryPage(ScopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), "same"));
        var reader = new AutopilotGlobalTaskInventoryReader(source, ScopeId);

        var snapshot = await reader.ReadAuthoritativeSnapshotAsync();

        Assert.False(snapshot.IsAuthoritative);
        Assert.Equal("inventory_cursor_repeated_or_invalid", snapshot.FailureCode);
    }

    [Fact]
    public async Task Stale_ambiguous_launch_keeps_its_slot_and_retries_only_with_verified_idempotency()
    {
        var ids = await SeedLaunchTasksAsync(total: 2, initiallyActive: 1, staleActive: true);
        var inventory = CompleteInventory();
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            key = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var unsafeRetry = await allocator.ReserveOrRecoverLaunchAsync(ids[0], key, inventory, TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: false);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, unsafeRetry.Outcome);
            Assert.True(unsafeRetry.SlotHeld);
            Assert.False(unsafeRetry.MayCallProvider);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var safeRetry = await allocator.ReserveOrRecoverLaunchAsync(ids[0], key, inventory, TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, safeRetry.Outcome);
            Assert.True(safeRetry.SlotHeld);
            Assert.True(safeRetry.MayCallProvider);
            Assert.Equal(1, safeRetry.ActiveSlotsObserved);
        }

        await using var verify = CreateContext();
        var recovered = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, recovered.Status);
        Assert.Equal(2, recovered.LaunchAttempt);
    }

    [Fact]
    public async Task Existing_provider_task_is_reused_by_the_same_idempotency_key_after_restart()
    {
        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            key = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
        }

        await using (var db = CreateContext())
        {
            var claimed = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], key, CompleteInventory(), TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, claimed.Outcome);
        }
        await using (var db = CreateContext())
        {
            var accepted = await CreateAllocator(db).RecordAcceptedAsync(ids[0], key, "external-task-recovered");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, accepted.Outcome);
        }

        var inventory = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId,
            true,
            1,
            false,
            null,
            [new("external-task-recovered", ScopeId, "running", key)]);
        await using (var db = CreateContext())
        {
            var retried = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], key, inventory, TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, retried.Outcome);
            Assert.False(retried.MayCallProvider);
            Assert.Equal("external-task-recovered", retried.ExternalTaskId);
        }

        await using var verify = CreateContext();
        Assert.Equal(1, await verify.AutopilotWaveLaunchTasks.CountAsync(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task Fresh_controller_claim_is_promoted_to_a_global_lease_before_provider_call()
    {
        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.LaunchAttempt = 1; // The existing controller claims this before calling the provider.
            task.UpdatedAt = DateTime.UtcNow;
            key = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var result = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], key, CompleteInventory(), TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, result.Outcome);
            Assert.True(result.MayCallProvider);
            Assert.Equal(1, result.ActiveSlotsObserved);
        }

        await using var verify = CreateContext();
        var reserved = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal("global_slot_reserved", reserved.ReasonCode);
        Assert.Equal(1, reserved.LaunchAttempt);
    }

    private async Task<AutopilotGlobalTaskSlotResult> ClaimAsync(
        Guid taskId,
        AutopilotGlobalTaskInventorySnapshot inventory)
    {
        await using var db = CreateContext();
        var task = await db.AutopilotWaveLaunchTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var key = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
        return await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
            taskId,
            key,
            inventory,
            TimeSpan.FromMinutes(5),
            providerGuaranteesIdempotency: true);
    }

    private async Task<IReadOnlyList<Guid>> SeedLaunchTasksAsync(int total, int initiallyActive, bool staleActive = false, int tasksPerWave = 5)
    {
        var ids = Enumerable.Range(0, total).Select(_ => Guid.NewGuid()).ToArray();
        var now = DateTime.UtcNow;
        await using var db = CreateContext();
        var waves = new Dictionary<int, (AutopilotRun Run, AutopilotWaveLaunchBatch Batch)>();

        for (var i = 0; i < total; i++)
        {
            var waveNumber = i / tasksPerWave;
            if (!waves.TryGetValue(waveNumber, out var pair))
            {
                var sourceWave = $"source-wave-{waveNumber}";
                var run = new AutopilotRun
                {
                    Id = Guid.NewGuid(),
                    WaveKey = sourceWave,
                    RunKey = $"run-{waveNumber}",
                    State = AutopilotRunStates.InProgress,
                    BaseSha = new string('a', 40),
                    CandidateSha = new string('b', 40),
                    Branch = $"feature/wave-{waveNumber}",
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                };
                var batch = new AutopilotWaveLaunchBatch
                {
                    Id = Guid.NewGuid(),
                    SourceRunId = run.Id,
                    SourceWaveKey = sourceWave,
                    WaveKey = $"{sourceWave}-next",
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    BaseSha = run.CandidateSha,
                    Branch = run.Branch,
                    PlanFingerprint = "test-fingerprint",
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                };
                db.AutopilotRuns.Add(run);
                db.AutopilotWaveLaunchBatches.Add(batch);
                pair = (run, batch);
                waves.Add(waveNumber, pair);
            }

            var active = i < initiallyActive;
            db.AutopilotWaveLaunchTasks.Add(new AutopilotWaveLaunchTask
            {
                Id = ids[i],
                BatchId = pair.Batch.Id,
                SourceRunId = pair.Run.Id,
                SourceWaveKey = pair.Run.WaveKey,
                WaveKey = pair.Batch.WaveKey,
                TaskKey = $"task-{i}",
                Title = $"Task {i}",
                BacklogItemKey = $"backlog-{i}",
                Status = active
                    ? (staleActive && i == 0 ? AutopilotWaveLaunchStatuses.Launching : AutopilotWaveLaunchStatuses.Launched)
                    : AutopilotWaveLaunchStatuses.Planned,
                ExternalTaskId = active && !(staleActive && i == 0) ? $"external-task-{i}" : null,
                Branch = pair.Run.Branch,
                BaseSha = pair.Run.CandidateSha,
                LaunchAttempt = active ? 1 : 0,
                MaxLaunchAttempts = 3,
                ReconcileCount = 0,
                MaxReconciles = 10,
                CreatedAt = now,
                UpdatedAt = active && staleActive && i == 0 ? now.AddHours(-1) : now,
                LaunchedAt = active && !(staleActive && i == 0) ? now : null,
                ConcurrencyToken = Guid.NewGuid(),
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private AutopilotGlobalTaskSlotAllocator CreateAllocator(TaslimDbContext db) =>
        new(db, ScopeId, TimeProvider.System, AutopilotGlobalTaskSlotAllocator.HardMaximum);

    private static AutopilotGlobalTaskInventorySnapshot CompleteInventory() =>
        new(ScopeId, true, 1, false, null, Array.Empty<AutopilotGlobalTaskInventoryItem>());

    private TaslimDbContext CreateContext() => new(options);

    private sealed class FakePageSource(params AutopilotGlobalTaskInventoryPage?[] pages) : IAutopilotGlobalTaskInventoryPageSource
    {
        private int nextPage;

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(string scopeId, string? cursor, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nextPage >= pages.Length) return Task.FromResult<AutopilotGlobalTaskInventoryPage?>(null);
            return Task.FromResult(pages[nextPage++]);
        }
    }
}

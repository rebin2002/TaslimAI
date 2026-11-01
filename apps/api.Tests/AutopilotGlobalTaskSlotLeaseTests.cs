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
        Assert.DoesNotContain(results.Where(item => item.Outcome != AutopilotGlobalTaskSlotOutcomes.Reserved), item => item.MayCallProvider);

        await using var verify = CreateContext();
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task PostgreSql_fence_serializes_independent_allocator_contexts_when_ci_database_is_available()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schemaName = $"slot_test_{Guid.NewGuid():N}";
        var adminConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await using (var connection = new Npgsql.NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schemaName,
                Pooling = false,
            }.ConnectionString;
            var pgOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;

            await using (var db = new TaslimDbContext(pgOptions))
                await db.Database.EnsureCreatedAsync();
            var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 0, contextOptions: pgOptions);
            var results = await Task.WhenAll(taskIds.Select(id => ClaimAsync(id, CompleteInventory(), pgOptions)));

            Assert.Equal(20, results.Count(item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved));
            Assert.Single(results, item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached);
            Assert.All(results, item => Assert.True(item.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));
            Assert.DoesNotContain(results.Where(item => item.Outcome != AutopilotGlobalTaskSlotOutcomes.Reserved), item => item.MayCallProvider);

            await using (var verify = new TaslimDbContext(pgOptions))
                Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
                    item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));

            const string externalId = "pg-external-task-0";
            string completionKey;
            await using (var db = new TaslimDbContext(pgOptions))
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
                completionKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
                var accepted = await CreateAllocator(db).RecordAcceptedAsync(taskIds[0], completionKey, externalId);
                Assert.True(accepted.SlotHeld);
            }

            var completionEventId = await SeedCompletionEventAsync(taskIds[0], contextOptions: pgOptions);
            var completions = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var db = new TaslimDbContext(pgOptions);
                return await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], completionKey, externalId, completionEventId);
            }));
            Assert.Single(completions, item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.Completed);
            Assert.Single(completions, item => item.Outcome == AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion);

            await using var finalVerify = new TaslimDbContext(pgOptions);
            Assert.Equal(19, await finalVerify.AutopilotWaveLaunchTasks.CountAsync(item =>
                item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        }
        finally
        {
            await using var connection = new Npgsql.NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Exact_task_detail_terminal_receipt_releases_one_slot_and_replay_does_not_free_another()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 22, initiallyActive: 20, tasksPerWave: 22);
        var inventory = CompleteInventory();
        var firstExternalId = "external-task-0";
        string key;
        await using (var db = CreateContext())
        {
            var first = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(first.WaveKey, first.TaskKey);
        }
        var completionEventId = await SeedCompletionEventAsync(taskIds[0]);

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var completed = await allocator.CompleteVerifiedAsync(taskIds[0], key, firstExternalId, completionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completed.Outcome);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var replay = await allocator.CompleteVerifiedAsync(taskIds[0], key, firstExternalId, completionEventId);
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
    public async Task Hmac_outcome_alone_blocked_and_ask_events_cannot_release_a_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 1);
        var successWithoutReceipt = await SeedCompletionEventAsync(taskIds[0], includeTaskDetailReceipt: false);
        var blockedWithReceipt = await SeedCompletionEventAsync(taskIds[0], outcome: AutopilotTaskOutcomes.Blocked);
        var askOutcome = await SeedCompletionEventAsync(taskIds[0], outcome: "ask");
        string key;
        string externalTaskId;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;
            var allocator = CreateAllocator(db);

            foreach (var eventId in new[] { successWithoutReceipt, blockedWithReceipt, askOutcome })
            {
                var result = await allocator.CompleteVerifiedAsync(taskIds[0], key, externalTaskId, eventId);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, result.Outcome);
                Assert.True(result.SlotHeld);
                Assert.False(result.MayCallProvider);
            }
        }

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
    }

    [Fact]
    public async Task Ambiguous_processed_completion_without_task_detail_keeps_all_slots_and_denies_replacement()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 20);
        var eventId = await SeedCompletionEventAsync(taskIds[0], includeTaskDetailReceipt: false);
        string key;
        string externalTaskId;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;

            var completion = await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], key, externalTaskId, eventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, completion.Outcome);
            Assert.True(completion.SlotHeld);
            Assert.False(completion.MayCallProvider);
        }

        var replacement = await ClaimAsync(taskIds[20], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, replacement.Outcome);
        Assert.False(replacement.MayCallProvider);
        Assert.Equal(20, replacement.ActiveSlotsObserved);

        await using var verify = CreateContext();
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[20])).Status);
    }

    [Fact]
    public async Task Ask_nonterminal_mismatched_and_background_task_detail_receipts_cannot_release_a_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 1);
        var askReceipt = await SeedCompletionEventAsync(taskIds[0], taskDetailStopReason: "ask");
        var runningTask = await SeedCompletionEventAsync(taskIds[0], taskDetailAgentStatus: "running");
        var backgroundJobs = await SeedCompletionEventAsync(taskIds[0], taskDetailHasRunningBackgroundJobs: true);
        var differentTask = await SeedCompletionEventAsync(taskIds[0], taskDetailTaskIdOverride: "another-external-task");
        string key;
        string externalTaskId;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;
            var allocator = CreateAllocator(db);

            foreach (var eventId in new[] { askReceipt, runningTask, backgroundJobs, differentTask })
            {
                var result = await allocator.CompleteVerifiedAsync(taskIds[0], key, externalTaskId, eventId);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, result.Outcome);
                Assert.True(result.SlotHeld);
                Assert.False(result.MayCallProvider);
            }
        }

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
    }

    [Fact]
    public async Task Deterministic_provider_rejection_releases_one_reserved_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 19);
        var reservation = await ClaimAsync(taskIds[19], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reservation.Outcome);
        Assert.True(reservation.MayCallProvider);
        string key;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var rejected = await CreateAllocator(db).RecordRejectedAsync(taskIds[19], key, "provider_rejected");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, rejected.Outcome);
            Assert.False(rejected.SlotHeld);
            Assert.False(rejected.MayCallProvider);
        }

        var replacement = await ClaimAsync(taskIds[20], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, replacement.Outcome);
        Assert.True(replacement.MayCallProvider);
    }

    [Fact]
    public async Task Ambiguous_provider_outcome_retains_the_reserved_slot_until_reconciliation()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 19);
        var reservation = await ClaimAsync(taskIds[19], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reservation.Outcome);
        string key;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var ambiguous = await CreateAllocator(db).RecordAmbiguousAsync(taskIds[19], key);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, ambiguous.Outcome);
            Assert.True(ambiguous.SlotHeld);
            Assert.False(ambiguous.MayCallProvider);
        }

        var blocked = await ClaimAsync(taskIds[20], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, blocked.Outcome);
        Assert.False(blocked.MayCallProvider);
    }

    [Fact]
    public async Task Queued_completion_event_cannot_release_a_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 1);
        var eventId = await SeedCompletionEventAsync(taskIds[0], status: AutopilotEventStatuses.Queued);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var result = await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], key, task.ExternalTaskId!, eventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, result.Outcome);
            Assert.True(result.SlotHeld);
        }

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
    }

    [Fact]
    public async Task Unsigned_or_unreplayed_processed_completion_events_cannot_release_a_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 1);
        var unsignedEventId = await SeedCompletionEventAsync(taskIds[0], signatureVerified: false);
        var replayableEventId = await SeedCompletionEventAsync(taskIds[0], replayProtected: false);
        string key;
        string externalTaskId;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;

            var unsigned = await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], key, externalTaskId, unsignedEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, unsigned.Outcome);
            Assert.True(unsigned.SlotHeld);

            var unreplayed = await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], key, externalTaskId, replayableEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, unreplayed.Outcome);
            Assert.True(unreplayed.SlotHeld);
        }

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
    }

    [Fact]
    public async Task Processed_completion_event_for_another_task_cannot_release_a_slot()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 1);
        var eventId = await SeedCompletionEventAsync(taskIds[0], taskKeyOverride: "different-task");
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var result = await CreateAllocator(db).CompleteVerifiedAsync(taskIds[0], key, task.ExternalTaskId!, eventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, result.Outcome);
            Assert.True(result.SlotHeld);
        }

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
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
    public async Task Unmarked_inflight_launch_counts_toward_capacity_during_rolling_upgrade()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 19);
        await using (var db = CreateContext())
        {
            var uncertain = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            uncertain.Status = AutopilotWaveLaunchStatuses.Launching;
            uncertain.LaunchAttempt = 1;
            uncertain.ExternalTaskId = "unreported-provider-task";
            uncertain.UpdatedAt = DateTime.UtcNow;
            uncertain.ReasonCode = null;
            await db.SaveChangesAsync();
        }

        var result = await ClaimAsync(taskIds[20], CompleteInventory());

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, result.Outcome);
        Assert.Equal(20, result.ActiveSlotsObserved);
        await using var verify = CreateContext();
        Assert.Equal(19, await verify.AutopilotWaveLaunchTasks.CountAsync(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
        var uncertainAfter = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, uncertainAfter.Status);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[20])).Status);
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
        var source = new FakePageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                [
                    new("taslim-1", ScopeId, "running", "autopilot:w1:t1"),
                    new("other-project", "unrelated-project", "running", "other:key"),
                ],
                "next-page")),
            ("next-page", new AutopilotGlobalTaskInventoryPage(ScopeId,
                [
                    new("taslim-done", ScopeId, "completed", "autopilot:w1:done"),
                    new("taslim-2", ScopeId, "awaiting_input", "autopilot:w2:t2"),
                ],
                null)));
        var reader = new AutopilotGlobalTaskInventoryReader(source, ScopeId);

        var snapshot = await reader.ReadAuthoritativeSnapshotAsync();

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(2, snapshot.PagesRead);
        Assert.False(snapshot.HasMorePages);
        Assert.Equal(new[] { "taslim-1", "taslim-2" }, snapshot.ActiveTasks.Select(item => item.TaskId).ToArray());
        Assert.DoesNotContain(snapshot.ActiveTasks, item => item.TaskId == "other-project");
        Assert.DoesNotContain(snapshot.ActiveTasks, item => item.TaskId == "taslim-done");
        Assert.Equal(2, source.PagesRead);
    }

    [Fact]
    public async Task Repeated_pagination_cursor_is_unknown_not_zero_occupancy()
    {
        var source = new FakePageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage(ScopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), "same")),
            ("same", new AutopilotGlobalTaskInventoryPage(ScopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), "same")));
        var reader = new AutopilotGlobalTaskInventoryReader(source, ScopeId);

        var snapshot = await reader.ReadAuthoritativeSnapshotAsync();

        Assert.False(snapshot.IsAuthoritative);
        Assert.Equal("inventory_cursor_repeated_or_invalid", snapshot.FailureCode);
    }

    [Fact]
    public async Task Mis_scoped_page_response_fails_closed()
    {
        var source = new FakePageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage("unexpected-project", Array.Empty<AutopilotGlobalTaskInventoryItem>(), null)));
        var snapshot = await new AutopilotGlobalTaskInventoryReader(source, ScopeId).ReadAuthoritativeSnapshotAsync();

        Assert.False(snapshot.IsAuthoritative);
        Assert.Equal("inventory_scope_mismatch", snapshot.FailureCode);
        Assert.Equal(1, source.PagesRead);
    }

    [Fact]
    public async Task Stale_allocator_marked_launch_retries_only_with_verified_v2_idempotency()
    {
        var ids = await SeedLaunchTasksAsync(total: 2, initiallyActive: 1, staleActive: true);
        var inventory = CompleteInventory();
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            task.ReasonCode = "global_slot_reserved";
            await db.SaveChangesAsync();
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
    public async Task Stale_unmarked_legacy_claim_is_held_without_retry_under_a_new_key()
    {
        var ids = await SeedLaunchTasksAsync(total: 2, initiallyActive: 1, staleActive: true);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
        }

        await using (var db = CreateContext())
        {
            var result = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], key, CompleteInventory(), TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, result.Outcome);
            Assert.True(result.SlotHeld);
            Assert.False(result.MayCallProvider);
        }

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            Assert.Equal("global_slot_reserved", task.ReasonCode);
            Assert.Equal(1, task.LaunchAttempt);
            var replay = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], key, CompleteInventory(), TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, replay.Outcome);
            Assert.False(replay.MayCallProvider);
        }
    }

    [Fact]
    public async Task Existing_provider_task_is_reused_by_the_same_idempotency_key_after_restart()
    {
        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
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
    public async Task Hashed_idempotency_identity_prevents_recovery_by_a_truncated_legacy_collision()
    {
        var waveKey = new string('w', 120);
        var firstTaskKey = new string('a', 120);
        var secondTaskKey = new string('a', 119) + "b";
        var firstLegacyKey = AutopilotNextWavePolicy.LaunchIdempotencyKey(waveKey, firstTaskKey);
        var secondLegacyKey = AutopilotNextWavePolicy.LaunchIdempotencyKey(waveKey, secondTaskKey);
        Assert.Equal(firstLegacyKey, secondLegacyKey);

        var firstKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(waveKey, firstTaskKey);
        var secondKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(waveKey, secondTaskKey);
        Assert.NotEqual(firstKey, secondKey);
        Assert.StartsWith(AutopilotGlobalTaskSlotIdentity.IdempotencyKeyPrefix, firstKey, StringComparison.Ordinal);
        Assert.True(firstKey.Length <= 200);

        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            task.WaveKey = waveKey;
            task.TaskKey = firstTaskKey;
            await db.SaveChangesAsync();
        }

        var inventory = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId, true, 1, false, null,
            [new("external-for-other-task", ScopeId, "running", secondKey)]);
        var ambiguousLegacyInventory = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId, true, 1, false, null,
            [new("external-with-colliding-legacy-key", ScopeId, "running", firstLegacyKey)]);
        await using (var db = CreateContext())
        {
            var ambiguous = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], firstKey, ambiguousLegacyInventory, TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, ambiguous.Outcome);
            Assert.Equal("legacy_idempotency_identity_ambiguous_requires_reconciliation", ambiguous.ReasonCode);
        }

        await using (var db = CreateContext())
        {
            var result = await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
                ids[0], firstKey, inventory, TimeSpan.FromMinutes(5), providerGuaranteesIdempotency: true);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, result.Outcome);
            Assert.True(result.MayCallProvider);
            Assert.Null(result.ExternalTaskId);
        }

        await using var verify = CreateContext();
        var reserved = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Null(reserved.ExternalTaskId);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, reserved.Status);
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
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
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
        AutopilotGlobalTaskInventorySnapshot inventory,
        DbContextOptions<TaslimDbContext>? contextOptions = null)
    {
        await using var db = contextOptions is null ? CreateContext() : new TaslimDbContext(contextOptions);
        var task = await db.AutopilotWaveLaunchTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
        return await CreateAllocator(db).ReserveOrRecoverLaunchAsync(
            taskId,
            key,
            inventory,
            TimeSpan.FromMinutes(5),
            providerGuaranteesIdempotency: true);
    }

    private async Task<IReadOnlyList<Guid>> SeedLaunchTasksAsync(
        int total,
        int initiallyActive,
        bool staleActive = false,
        int tasksPerWave = 5,
        DbContextOptions<TaslimDbContext>? contextOptions = null)
    {
        var ids = Enumerable.Range(0, total).Select(_ => Guid.NewGuid()).ToArray();
        var now = DateTime.UtcNow;
        await using var db = contextOptions is null ? CreateContext() : new TaslimDbContext(contextOptions);
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

    private async Task<Guid> SeedCompletionEventAsync(
        Guid taskRowId,
        string status = AutopilotEventStatuses.Processed,
        bool signatureVerified = true,
        bool replayProtected = true,
        string? taskKeyOverride = null,
        DbContextOptions<TaslimDbContext>? contextOptions = null,
        string outcome = AutopilotTaskOutcomes.Succeeded,
        bool includeTaskDetailReceipt = true,
        string? taskDetailTaskIdOverride = null,
        string taskDetailAgentStatus = "stopped",
        string taskDetailStopReason = "finish",
        bool? taskDetailHasRunningBackgroundJobs = false)
    {
        await using var db = contextOptions is null ? CreateContext() : new TaslimDbContext(contextOptions);
        var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskRowId);
        var now = DateTime.UtcNow;
        var eventId = Guid.NewGuid();
        var eventTaskKey = taskKeyOverride ?? task.TaskKey;
        var receipt = new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            taskDetailTaskIdOverride ?? task.ExternalTaskId,
            taskDetailAgentStatus,
            taskDetailStopReason,
            taskDetailHasRunningBackgroundJobs);
        var signal = new AutopilotCompletionSignal
        {
            WaveKey = task.WaveKey,
            TaskId = eventTaskKey,
            Outcome = outcome,
            Evidence = includeTaskDetailReceipt ? System.Text.Json.JsonSerializer.Serialize(receipt) : null,
        };
        db.AutopilotEvents.Add(new AutopilotEvent
        {
            Id = eventId,
            EventType = AutopilotEventTypes.WaveTaskCompleted,
            SourceSystem = "test-completion-bridge",
            ExternalEventId = $"test-event-{eventId:N}",
            IdempotencyKey = $"test-event-key-{eventId:N}",
            PayloadHash = new string('a', 64),
            Status = status,
            SignatureVerified = signatureVerified,
            ReplayProtected = replayProtected,
            WaveKey = task.WaveKey,
            TaskId = eventTaskKey,
            ReceivedAt = now,
            ProcessedAt = string.Equals(status, AutopilotEventStatuses.Processed, StringComparison.OrdinalIgnoreCase) ? now : null,
            ConcurrencyToken = Guid.NewGuid(),
            SignalJson = System.Text.Json.JsonSerializer.Serialize(signal),
        });
        await db.SaveChangesAsync();
        return eventId;
    }

    private TaslimDbContext CreateContext() => new(options);

    private sealed class FakePageSource(
        string expectedScopeId,
        params (string? ExpectedCursor, AutopilotGlobalTaskInventoryPage? Response)[] pages) : IAutopilotGlobalTaskInventoryPageSource
    {
        private int nextPage;
        public int PagesRead => nextPage;

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(string scopeId, string? cursor, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(scopeId, expectedScopeId, StringComparison.Ordinal))
                throw new InvalidOperationException("Inventory reader requested an unexpected Taslim scope.");
            if (nextPage >= pages.Length)
                throw new InvalidOperationException("Inventory reader requested an unexpected extra page.");

            var page = pages[nextPage++];
            if (!string.Equals(cursor, page.ExpectedCursor, StringComparison.Ordinal))
                throw new InvalidOperationException("Inventory reader sent an unexpected pagination cursor.");
            return Task.FromResult(page.Response);
        }
    }
}

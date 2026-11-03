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
    public async Task Terminal_receipt_event_id_must_match_the_persisted_event_identity()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 20);
        var mismatchedEventId = await SeedCompletionEventAsync(
            taskIds[0], taskDetailEventIdOverride: "different-signed-event-id");
        var missingEventId = await SeedCompletionEventAsync(taskIds[0], includeTaskDetailEventId: false);
        string key;
        string externalTaskId;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;
            var allocator = CreateAllocator(db);

            foreach (var eventId in new[] { mismatchedEventId, missingEventId })
            {
                var result = await allocator.CompleteVerifiedAsync(taskIds[0], key, externalTaskId, eventId);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.TerminalEvidenceInvalid, result.Outcome);
                Assert.True(result.SlotHeld);
                Assert.False(result.MayCallProvider);
            }
        }

        var replacement = await ClaimAsync(taskIds[20], CompleteInventory());
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, replacement.Outcome);
        Assert.False(replacement.SlotHeld);
        Assert.False(replacement.MayCallProvider);
        Assert.Equal(20, replacement.ActiveSlotsObserved);

        await using var verify = CreateContext();
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
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
    public async Task Manus_task_list_adapter_uses_creator_scope_and_paginates_waiting_and_stopped_tasks_as_active()
    {
        var requests = new List<Uri>();
        var handler = new FakeHttpHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = Assert.IsType<Uri>(request.RequestUri);
            requests.Add(uri);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("api.manus.ai", uri.Host);
            Assert.Equal("/v2/task.list", uri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("unit-test-token", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("x-manus-api-key"));

            var query = ParseQuery(uri);
            Assert.Equal("100", query["limit"]);
            Assert.Equal("desc", query["order"]);
            Assert.False(query.ContainsKey("sort"));
            Assert.Equal("all", query["scope"]);
            Assert.Equal("taslim-open-app-id", query["oauth_client_id"]);
            Assert.False(query.ContainsKey("api_key_id"));

            var json = requests.Count switch
            {
                1 => """{"ok":true,"data":[{"id":"task-waiting","status":"waiting"},{"id":"task-stopped-ask","status":"stopped","has_running_background_jobs":false}],"has_more":true,"next_cursor":"cursor/2"}""",
                2 => """{"ok":true,"data":[{"id":"task-running","status":"running"},{"id":"task-completed","status":"completed"}],"has_more":false}""",
                _ => throw new InvalidOperationException("Unexpected extra page request."),
            };

            if (requests.Count == 1) Assert.False(query.ContainsKey("cursor"));
            else Assert.Equal("cursor/2", query["cursor"]);
            return Task.FromResult(JsonResponse(json));
        });
        using var source = new ManusTaskListInventoryPageSource(
            () => "unit-test-token",
            ScopeId,
            "taslim-open-app-id",
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.OAuthBearer,
            testHandler: handler);

        var snapshot = await new AutopilotGlobalTaskInventoryReader(source, ScopeId).ReadAuthoritativeSnapshotAsync();

        Assert.True(snapshot.IsAuthoritative);
        Assert.Equal(2, snapshot.PagesRead);
        Assert.Equal(new[] { "task-running", "task-stopped-ask", "task-waiting" }, snapshot.ActiveTasks.Select(item => item.TaskId).ToArray());
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task Manus_task_list_adapter_fails_closed_for_missing_credentials_or_truncated_pages()
    {
        var noCredentialHandler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Must not call without a credential."));
        using (var noCredentialSource = new ManusTaskListInventoryPageSource(
            () => null,
            ScopeId,
            "taslim-api-key-id",
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.ApiKey,
            testHandler: noCredentialHandler))
        {
            var noCredentialSnapshot = await new AutopilotGlobalTaskInventoryReader(noCredentialSource, ScopeId).ReadAuthoritativeSnapshotAsync();
            Assert.False(noCredentialSnapshot.IsAuthoritative);
        }

        var truncatedHandler = new FakeHttpHandler((request, _) =>
        {
            var query = ParseQuery(request.RequestUri!);
            Assert.Equal("100", query["limit"]);
            Assert.Equal("desc", query["order"]);
            Assert.Equal("taslim-api-key-id", query["api_key_id"]);
            Assert.False(query.ContainsKey("oauth_client_id"));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("unit-test-api-key", request.Headers.GetValues("x-manus-api-key").Single());
            return Task.FromResult(JsonResponse("""{"ok":true,"data":[],"has_more":true}"""));
        });
        using var truncatedSource = new ManusTaskListInventoryPageSource(
            () => "unit-test-api-key",
            ScopeId,
            "taslim-api-key-id",
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.ApiKey,
            testHandler: truncatedHandler);

        var truncated = await new AutopilotGlobalTaskInventoryReader(truncatedSource, ScopeId).ReadAuthoritativeSnapshotAsync();

        Assert.False(truncated.IsAuthoritative);
        Assert.Equal("inventory_page_invalid", truncated.FailureCode);
    }

    [Fact]
    public async Task Manus_task_list_unverified_creator_identity_and_page_timeout_are_unknown_not_empty_inventory()
    {
        var unverifiedCalls = 0;
        var unverifiedHandler = new FakeHttpHandler((_, _) =>
        {
            unverifiedCalls++;
            return Task.FromResult(JsonResponse("""{"ok":true,"data":[],"has_more":false}"""));
        });
        using (var unverifiedSource = new ManusTaskListInventoryPageSource(
            () => "unit-test-token",
            ScopeId,
            "unverified-or-mistyped-creator-id",
            creatorIdentityVerified: false,
            AutopilotManusTaskListCredentialKind.OAuthBearer,
            testHandler: unverifiedHandler))
        {
            var unverified = await new AutopilotGlobalTaskInventoryReader(unverifiedSource, ScopeId).ReadAuthoritativeSnapshotAsync();
            Assert.False(unverified.IsAuthoritative);
            Assert.Equal("inventory_page_invalid", unverified.FailureCode);
            Assert.Empty(unverified.ActiveTasks);
        }
        Assert.Equal(0, unverifiedCalls);

        var absentIdentityCalls = 0;
        var absentIdentityHandler = new FakeHttpHandler((_, _) =>
        {
            absentIdentityCalls++;
            return Task.FromResult(JsonResponse("""{"ok":true,"data":[],"has_more":false}"""));
        });
        using (var absentIdentitySource = new ManusTaskListInventoryPageSource(
            () => "unit-test-token",
            ScopeId,
            creatorId: null,
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.OAuthBearer,
            testHandler: absentIdentityHandler))
        {
            var absentIdentity = await new AutopilotGlobalTaskInventoryReader(absentIdentitySource, ScopeId).ReadAuthoritativeSnapshotAsync();
            Assert.False(absentIdentity.IsAuthoritative);
            Assert.Equal("inventory_page_invalid", absentIdentity.FailureCode);
            Assert.Empty(absentIdentity.ActiveTasks);
        }
        Assert.Equal(0, absentIdentityCalls);

        var timeoutCalls = 0;
        var timeoutHandler = new FakeHttpHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref timeoutCalls);
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"ok":true,"data":[],"has_more":false}""");
        });
        using var timeoutSource = new ManusTaskListInventoryPageSource(
            () => "unit-test-token",
            ScopeId,
            "verified-open-app-id",
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.OAuthBearer,
            pageTimeout: TimeSpan.FromMilliseconds(50),
            testHandler: timeoutHandler);

        var timedOut = await new AutopilotGlobalTaskInventoryReader(timeoutSource, ScopeId).ReadAuthoritativeSnapshotAsync();

        Assert.False(timedOut.IsAuthoritative);
        Assert.Equal("inventory_page_invalid", timedOut.FailureCode);
        Assert.Empty(timedOut.ActiveTasks);
        Assert.Equal(1, timeoutCalls);
    }

    [Fact]
    public async Task Verified_completion_deduplicates_stopped_inventory_for_only_its_exact_task()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 22, initiallyActive: 20);
        var stoppedInventory = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId,
            true,
            1,
            false,
            null,
            Enumerable.Range(0, 20)
                .Select(index => new AutopilotGlobalTaskInventoryItem($"external-task-{index}", ScopeId, "stopped", null))
                .ToArray());
        var completionEventId = await SeedCompletionEventAsync(taskIds[0]);
        string idempotencyKey;
        string externalTaskId;

        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            idempotencyKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            externalTaskId = task.ExternalTaskId!;
            var completion = await CreateAllocator(db).CompleteVerifiedAsync(
                taskIds[0], idempotencyKey, externalTaskId, completionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completion.Outcome);
        }

        var replacement = await ClaimAsync(taskIds[20], stoppedInventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, replacement.Outcome);
        Assert.Equal(20, replacement.ActiveSlotsObserved);
        Assert.True(replacement.MayCallProvider);

        var excess = await ClaimAsync(taskIds[21], stoppedInventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, excess.Outcome);
        Assert.False(excess.MayCallProvider);
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
        bool? taskDetailHasRunningBackgroundJobs = false,
        string? taskDetailEventIdOverride = null,
        bool includeTaskDetailEventId = true)
    {
        await using var db = contextOptions is null ? CreateContext() : new TaslimDbContext(contextOptions);
        var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskRowId);
        var now = DateTime.UtcNow;
        var eventId = Guid.NewGuid();
        var externalEventId = $"test-event-{eventId:N}";
        var eventTaskKey = taskKeyOverride ?? task.TaskKey;
        var receipt = new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            taskDetailTaskIdOverride ?? task.ExternalTaskId,
            taskDetailAgentStatus,
            taskDetailStopReason,
            taskDetailHasRunningBackgroundJobs,
            includeTaskDetailEventId ? taskDetailEventIdOverride ?? externalEventId : null);
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
            ExternalEventId = externalEventId,
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

    private static Dictionary<string, string> ParseQuery(Uri uri) => uri.Query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    private static System.Net.Http.HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class FakeHttpHandler(
        Func<System.Net.Http.HttpRequestMessage, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> responder) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }

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

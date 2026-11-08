using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AutopilotGlobalTaskSlotLeaseTests : IAsyncLifetime
{
    private const string ScopeId = "taslimai-autopilot";
    private const string ExpectedBridgeManusUserId = "manus-user-verified";
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
    public async Task PostgreSql_barrier_contention_and_ambiguous_refresh_heartbeat_preserve_user_wide_cap_when_ci_database_is_available()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schemaName = $"slot_heartbeat_{Guid.NewGuid():N}";
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
            var applicationName = $"slot_heartbeat_{Guid.NewGuid():N}";
            var scopedConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schemaName,
                Pooling = false,
                ApplicationName = applicationName,
            }.ConnectionString;
            var pgOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using (var db = new TaslimDbContext(pgOptions))
                await db.Database.EnsureCreatedAsync();

            var taskIds = await SeedLaunchTasksAsync(total: 3, initiallyActive: 0, tasksPerWave: 1, contextOptions: pgOptions);
            var inventoryScope = AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide;
            var inventoryPayload = BuildBridgeInventoryPayload(taskCount: 20);
            var inventoryRequestCount = 0;
            string? inventoryRequestUri = null;
            string? inventoryAuthorization = null;
            var inventoryHandler = new FakeHttpHandler((request, _) =>
            {
                inventoryRequestCount++;
                inventoryRequestUri = request.RequestUri?.AbsoluteUri;
                inventoryAuthorization = request.Headers.Authorization?.ToString();
                return Task.FromResult(JsonResponse(inventoryPayload));
            });
            using var inventorySource = new ManusBridgeUserWideTaskInventoryPageSource(
                () => "synthetic-bridge-token",
                new Uri("https://bridge.example/v1/tasks"),
                ExpectedBridgeManusUserId,
                testHandler: inventoryHandler);
            var inventory = await new AutopilotGlobalTaskInventoryReader(inventorySource, inventoryScope).ReadAuthoritativeSnapshotAsync();
            Assert.True(inventory.IsAuthoritative);
            Assert.Equal(inventoryScope, inventory.ScopeId);
            Assert.Equal(1, inventory.PagesRead);
            Assert.False(inventory.HasMorePages);
            Assert.Equal(19, inventory.ActiveTasks.Count);
            Assert.All(inventory.ActiveTasks, item => Assert.Equal(inventoryScope, item.ScopeId));
            Assert.Contains("\"api-key-0\"", inventoryPayload);
            Assert.Contains("\"api-key-1\"", inventoryPayload);
            Assert.Contains("\"created_by_api_key\":null", inventoryPayload);
            Assert.Equal(1, inventoryRequestCount);
            Assert.Equal("https://bridge.example/v1/tasks", inventoryRequestUri);
            Assert.Equal("Bearer synthetic-bridge-token", inventoryAuthorization);

            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
            using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var bothReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseContenders = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readyCount = 0;

            async Task<(Guid RowId, string Key, AutopilotGlobalTaskSlotResult Result)> ReserveAtBarrierAsync(Guid rowId)
            {
                await using var db = new TaslimDbContext(pgOptions);
                var row = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rowId, testDeadline.Token);
                var key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(row.WaveKey, row.TaskKey);
                var allocator = new AutopilotGlobalTaskSlotAllocator(
                    db,
                    inventoryScope,
                    clock,
                    AutopilotGlobalTaskSlotAllocator.HardMaximum);

                if (Interlocked.Increment(ref readyCount) == 2)
                    bothReady.TrySetResult(true);
                await releaseContenders.Task.WaitAsync(testDeadline.Token);

                var result = await allocator.ReserveOrRecoverLaunchAsync(
                    rowId,
                    key,
                    inventory,
                    TimeSpan.FromMinutes(1),
                    providerGuaranteesIdempotency: false,
                    cancellationToken: testDeadline.Token);
                return (rowId, key, result);
            }

            async Task<int> CountBlockedAllocatorSessionsAsync()
            {
                await using var monitorConnection = new Npgsql.NpgsqlConnection(adminConnectionString);
                await monitorConnection.OpenAsync(testDeadline.Token);
                await using var monitorCommand = monitorConnection.CreateCommand();
                monitorCommand.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE application_name = @application_name AND state = 'active' AND wait_event_type = 'Lock'";
                var applicationParameter = monitorCommand.CreateParameter();
                applicationParameter.ParameterName = "application_name";
                applicationParameter.Value = applicationName;
                monitorCommand.Parameters.Add(applicationParameter);
                var count = await monitorCommand.ExecuteScalarAsync(testDeadline.Token);
                return Convert.ToInt32(count, System.Globalization.CultureInfo.InvariantCulture);
            }

            Task<(Guid RowId, string Key, AutopilotGlobalTaskSlotResult Result)>[] contenders;
            int lockWaitersObserved;
            await using (var blockerConnection = new Npgsql.NpgsqlConnection(scopedConnectionString))
            {
                await blockerConnection.OpenAsync(testDeadline.Token);
                await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(testDeadline.Token);
                await using (var blockerCommand = blockerConnection.CreateCommand())
                {
                    blockerCommand.Transaction = blockerTransaction;
                    // This is the allocator's fixed cross-process transaction advisory fence.
                    blockerCommand.CommandText = "SELECT pg_advisory_xact_lock(1413560652, 20)";
                    await blockerCommand.ExecuteNonQueryAsync(testDeadline.Token);
                }

                contenders = taskIds.Take(2).Select(ReserveAtBarrierAsync).ToArray();
                await bothReady.Task.WaitAsync(testDeadline.Token);
                releaseContenders.TrySetResult(true);

                lockWaitersObserved = 0;
                var waitDeadline = System.Diagnostics.Stopwatch.StartNew();
                while (lockWaitersObserved < 2 && waitDeadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    lockWaitersObserved = await CountBlockedAllocatorSessionsAsync();
                    if (lockWaitersObserved < 2)
                        await Task.Delay(TimeSpan.FromMilliseconds(10), testDeadline.Token);
                }

                await blockerTransaction.CommitAsync(testDeadline.Token);
            }

            var racedClaims = await Task.WhenAll(contenders).WaitAsync(testDeadline.Token);
            Assert.Equal(2, lockWaitersObserved);

            Assert.Single(racedClaims, item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved);
            Assert.Single(racedClaims, item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached);
            Assert.All(racedClaims, item => Assert.True(item.Result.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));
            var winner = racedClaims.Single(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved);
            var denied = racedClaims.Single(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached);
            Assert.True(winner.Result.SlotHeld);
            Assert.True(winner.Result.MayCallProvider);
            Assert.False(denied.Result.MayCallProvider);
            // Count authorization decisions only; this test never calls a provider.
            var providerAuthorizations = racedClaims.Count(item => item.Result.MayCallProvider);
            Assert.Equal(1, providerAuthorizations);

            // RecordAmbiguousAsync refreshes UpdatedAt while retaining the slot;
            // this verifies the durable lease-refresh heartbeat under contention.
            clock.Advance(TimeSpan.FromSeconds(50));
            await using (var heartbeatDb = new TaslimDbContext(pgOptions))
            {
                var heartbeat = await new AutopilotGlobalTaskSlotAllocator(
                    heartbeatDb,
                    inventoryScope,
                    clock,
                    AutopilotGlobalTaskSlotAllocator.HardMaximum)
                    .RecordAmbiguousAsync(winner.RowId, winner.Key, "provider_timeout", testDeadline.Token);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, heartbeat.Outcome);
                Assert.True(heartbeat.SlotHeld);
                Assert.False(heartbeat.MayCallProvider);
            }

            await using (var verifyHeartbeat = new TaslimDbContext(pgOptions))
            {
                var held = await verifyHeartbeat.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == winner.RowId, testDeadline.Token);
                Assert.Equal(AutopilotWaveLaunchStatuses.Launching, held.Status);
                Assert.Equal("global_slot_reserved", held.ReasonCode);
                Assert.StartsWith("slot_retained_until_authoritative_reconciliation:", held.LastStatusDetail);
                Assert.Equal(clock.GetUtcNow().UtcDateTime, held.UpdatedAt);
            }

            // The original lease has elapsed, but the refreshed lease has not;
            // no second fake provider call may be authorized.
            clock.Advance(TimeSpan.FromSeconds(30));
            await using (var retryDb = new TaslimDbContext(pgOptions))
            {
                var beforeRefreshedExpiry = await new AutopilotGlobalTaskSlotAllocator(
                    retryDb,
                    inventoryScope,
                    clock,
                    AutopilotGlobalTaskSlotAllocator.HardMaximum)
                    .ReserveOrRecoverLaunchAsync(
                        winner.RowId,
                        winner.Key,
                        inventory,
                        TimeSpan.FromMinutes(1),
                        providerGuaranteesIdempotency: true,
                        cancellationToken: testDeadline.Token);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, beforeRefreshedExpiry.Outcome);
                Assert.True(beforeRefreshedExpiry.SlotHeld);
                Assert.False(beforeRefreshedExpiry.MayCallProvider);
            }

            clock.Advance(TimeSpan.FromSeconds(31));
            await using (var expiredDb = new TaslimDbContext(pgOptions))
            {
                var afterExpiryWithoutIdempotency = await new AutopilotGlobalTaskSlotAllocator(
                    expiredDb,
                    inventoryScope,
                    clock,
                    AutopilotGlobalTaskSlotAllocator.HardMaximum)
                    .ReserveOrRecoverLaunchAsync(
                        winner.RowId,
                        winner.Key,
                        inventory,
                        TimeSpan.FromMinutes(1),
                        providerGuaranteesIdempotency: false,
                        cancellationToken: testDeadline.Token);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, afterExpiryWithoutIdempotency.Outcome);
                Assert.True(afterExpiryWithoutIdempotency.SlotHeld);
                Assert.False(afterExpiryWithoutIdempotency.MayCallProvider);
            }

            await using (var overflowDb = new TaslimDbContext(pgOptions))
            {
                var overflowRow = await overflowDb.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[2], testDeadline.Token);
                var overflowKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(overflowRow.WaveKey, overflowRow.TaskKey);
                var twentyFirst = await new AutopilotGlobalTaskSlotAllocator(
                    overflowDb,
                    inventoryScope,
                    clock,
                    AutopilotGlobalTaskSlotAllocator.HardMaximum)
                    .ReserveOrRecoverLaunchAsync(
                        taskIds[2],
                        overflowKey,
                        inventory,
                        TimeSpan.FromMinutes(1),
                        providerGuaranteesIdempotency: false,
                        cancellationToken: testDeadline.Token);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, twentyFirst.Outcome);
                Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, twentyFirst.ActiveSlotsObserved);
                Assert.False(twentyFirst.MayCallProvider);
            }

            Assert.Equal(1, providerAuthorizations);
            await using var finalVerify = new TaslimDbContext(pgOptions);
            var localOccupied = await finalVerify.AutopilotWaveLaunchTasks.CountAsync(
                item => item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched,
                testDeadline.Token);
            Assert.Equal(1, localOccupied);
            Assert.Equal(AutopilotGlobalTaskSlotAllocator.HardMaximum, inventory.ActiveTasks.Count + localOccupied);
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

    [Theory]
    [InlineData("http_502")]
    [InlineData("timeout")]
    [InlineData("http_409")]
    [InlineData("provider_rejected")]
    [InlineData("definitive_no_create")]
    [InlineData("unrecognized_reason")]
    public async Task Unverified_rejection_reason_never_releases_slot_or_authorizes_a_retry(string reasonCode)
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 19);
        var inventory = CompleteInventory();
        var reservation = await ClaimAsync(taskIds[19], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reservation.Outcome);
        Assert.True(reservation.MayCallProvider);

        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var rejected = await CreateAllocator(db).RecordRejectedAsync(taskIds[19], key, reasonCode);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, rejected.Outcome);
            Assert.True(rejected.SlotHeld);
            Assert.False(rejected.MayCallProvider);

            var repeatedRejection = await CreateAllocator(db).RecordRejectedAsync(taskIds[19], key, reasonCode);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, repeatedRejection.Outcome);
            Assert.True(repeatedRejection.SlotHeld);
            Assert.False(repeatedRejection.MayCallProvider);

            var laterAmbiguous = await CreateAllocator(db).RecordAmbiguousAsync(taskIds[19], key, "http_502");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, laterAmbiguous.Outcome);
            Assert.True(laterAmbiguous.SlotHeld);
            Assert.False(laterAmbiguous.MayCallProvider);
        }

        // A process restart/lease expiry cannot turn this unverified result into
        // permission for another provider call, even when the caller asserts
        // that same-key provider idempotency is available.
        await using (var db = CreateContext())
        {
            var row = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            row.UpdatedAt = DateTime.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var replay = await ClaimAsync(taskIds[19], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, replay.Outcome);
        Assert.True(replay.SlotHeld);
        Assert.False(replay.MayCallProvider);

        var twentyFirst = await ClaimAsync(taskIds[20], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, twentyFirst.Outcome);
        Assert.False(twentyFirst.MayCallProvider);
        Assert.Equal(20, twentyFirst.ActiveSlotsObserved);

        await using var verify = CreateContext();
        var retained = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, retained.Status);
        Assert.Equal(1, retained.LaunchAttempt);
        Assert.Null(retained.CompletedAt);
        Assert.Equal("provider_rejection_unverified", retained.ReasonCode);
        Assert.Contains("no_create_proof", retained.LastStatusDetail, StringComparison.Ordinal);
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task Conflicting_rejection_identity_cannot_release_a_held_slot_or_allow_replacement()
    {
        var taskIds = await SeedLaunchTasksAsync(total: 21, initiallyActive: 19);
        var inventory = CompleteInventory();
        var reservation = await ClaimAsync(taskIds[19], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reservation.Outcome);

        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var conflict = await CreateAllocator(db).RecordRejectedAsync(taskIds[19], key + "-conflict", "definitive_no_create");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, conflict.Outcome);
            Assert.True(conflict.SlotHeld);
            Assert.False(conflict.MayCallProvider);
        }

        var sameTaskRetry = await ClaimAsync(taskIds[19], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, sameTaskRetry.Outcome);
        Assert.True(sameTaskRetry.SlotHeld);
        Assert.False(sameTaskRetry.MayCallProvider);
        var replacement = await ClaimAsync(taskIds[20], inventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, replacement.Outcome);
        Assert.False(replacement.MayCallProvider);

        await using var verify = CreateContext();
        var row = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[19]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, row.Status);
        Assert.Equal(1, row.LaunchAttempt);
        Assert.Null(row.CompletedAt);
        Assert.Equal("global_slot_reserved", row.ReasonCode);
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
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

    [Theory]
    [InlineData(19, 1)]
    [InlineData(20, 0)]
    public async Task Bridge_user_wide_inventory_counts_all_creators_and_stopped_background_jobs_before_claims(
        int externalActiveCount,
        int expectedReservations)
    {
        var scope = AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide;
        var taskIds = await SeedLaunchTasksAsync(total: 2, initiallyActive: 0, tasksPerWave: 2);
        var requestCount = 0;
        var handler = new FakeHttpHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref requestCount);
            Assert.Equal(System.Net.Http.HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("https://bridge.example/v1/tasks"), request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("bridge-test-token", request.Headers.Authorization?.Parameter);
            Assert.Empty(request.RequestUri!.Query);
            Assert.False(request.Headers.Contains("x-manus-api-key"));
            return Task.FromResult(JsonResponse(BuildBridgeInventoryPayload(externalActiveCount)));
        });
        using var source = new ManusBridgeUserWideTaskInventoryPageSource(
            () => "bridge-test-token",
            new Uri("https://bridge.example/v1/tasks"),
            ExpectedBridgeManusUserId,
            requestTimeout: TimeSpan.FromSeconds(2),
            testHandler: handler);
        var inventory = await new AutopilotGlobalTaskInventoryReader(source, scope).ReadAuthoritativeSnapshotAsync();

        Assert.True(inventory.IsAuthoritative);
        Assert.Equal(scope, inventory.ScopeId);
        Assert.Equal(1, inventory.PagesRead);
        Assert.False(inventory.HasMorePages);
        Assert.Equal(externalActiveCount, inventory.ActiveTasks.Count);
        Assert.Equal("running", inventory.ActiveTasks.Single(item => item.TaskId == BridgeNativeTaskId(0)).Status);
        if (externalActiveCount > 1)
            Assert.Equal("stopped", inventory.ActiveTasks.Single(item => item.TaskId == BridgeNativeTaskId(1)).Status);
        Assert.Equal(1, requestCount);

        var results = await Task.WhenAll(taskIds.Select(id => ClaimAsync(id, inventory, allocatorScopeId: scope)));
        var fakeProviderCalls = results.Count(result => result.MayCallProvider);
        Assert.Equal(expectedReservations, results.Count(result => result.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved));
        Assert.Equal(2 - expectedReservations, results.Count(result => result.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached));
        Assert.All(results, result => Assert.True(result.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));
        Assert.Equal(20, results.Max(result => result.ActiveSlotsObserved));
        Assert.Equal(expectedReservations, fakeProviderCalls);

        await using var verify = CreateContext();
        Assert.Equal(expectedReservations, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Fact]
    public async Task Bridge_user_wide_inventory_rejects_unproven_taslim_or_team_scope_without_http_request()
    {
        var calls = 0;
        var handler = new FakeHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(JsonResponse(BuildBridgeInventoryPayload(0)));
        });
        using var source = new ManusBridgeUserWideTaskInventoryPageSource(
            () => "bridge-test-token",
            new Uri("https://bridge.example/v1/tasks"),
            ExpectedBridgeManusUserId,
            testHandler: handler);

        foreach (var unprovenScope in new[] { ScopeId, "manus-team-wide", "" })
        {
            var snapshot = await new AutopilotGlobalTaskInventoryReader(source, unprovenScope).ReadAuthoritativeSnapshotAsync();
            Assert.False(snapshot.IsAuthoritative);
            Assert.Equal(string.IsNullOrWhiteSpace(unprovenScope) ? "inventory_scope_missing" : "inventory_page_invalid", snapshot.FailureCode);
            Assert.Empty(snapshot.ActiveTasks);
        }

        Assert.Equal(0, calls);
        var launchTaskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var unknownInventory = await new AutopilotGlobalTaskInventoryReader(source, ScopeId).ReadAuthoritativeSnapshotAsync();
        var claim = await ClaimAsync(launchTaskIds[0], unknownInventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, claim.Outcome);
        Assert.False(claim.MayCallProvider);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Bridge_user_wide_inventory_fails_closed_on_non_200_malformed_partial_duplicate_or_unknown_status()
    {
        var scope = AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide;
        var nativeId = BridgeNativeTaskId(0);
        var invalidResponses = new (System.Net.HttpStatusCode Status, string Body)[]
        {
            (System.Net.HttpStatusCode.BadGateway, "{\"manus_user_id\":\"manus-user-verified\",\"ok\":false,\"error\":\"task_inventory_incomplete\"}"),
            (System.Net.HttpStatusCode.OK, ""),
            (System.Net.HttpStatusCode.OK, "{not-json"),
            (System.Net.HttpStatusCode.OK, "{\"ok\":true,\"data\":[],\"has_more\":false}"),
            (System.Net.HttpStatusCode.OK, "{\"manus_user_id\":\"different-manus-user\",\"ok\":true,\"data\":[],\"has_more\":false}"),
            (System.Net.HttpStatusCode.OK, "{\"manus_user_id\":\"manus-user-verified\",\"ok\":false,\"ok\":true,\"data\":[],\"has_more\":false}"),
            (System.Net.HttpStatusCode.OK, "{\"manus_user_id\":\"manus-user-verified\",\"ok\":true,\"data\":[],\"has_more\":true,\"next_cursor\":\"page-2\"}"),
            (System.Net.HttpStatusCode.OK, "{\"manus_user_id\":\"manus-user-verified\",\"ok\":true,\"data\":[],\"has_more\":false,\"next_cursor\":null}"),
            (System.Net.HttpStatusCode.OK, $"{{\"manus_user_id\":\"manus-user-verified\",\"ok\":true,\"data\":[{{\"id\":\"{nativeId}\",\"status\":\"running\"}},{{\"id\":\"{nativeId}\",\"status\":\"waiting\"}}],\"has_more\":false}}"),
            (System.Net.HttpStatusCode.OK, $"{{\"manus_user_id\":\"manus-user-verified\",\"ok\":true,\"data\":[{{\"id\":\"{nativeId}\",\"status\":\"future_status\"}}],\"has_more\":false}}"),
            (System.Net.HttpStatusCode.OK, $"{{\"manus_user_id\":\"manus-user-verified\",\"ok\":true,\"data\":[{{\"id\":\"{nativeId}\",\"status\":\"stopped\",\"has_running_background_jobs\":\"false\"}}],\"has_more\":false}}"),
        };
        var taskIds = await SeedLaunchTasksAsync(total: invalidResponses.Length, initiallyActive: 0, tasksPerWave: invalidResponses.Length);
        var claims = new List<AutopilotGlobalTaskSlotResult>(invalidResponses.Length);
        var fakeProviderCalls = 0;

        for (var index = 0; index < invalidResponses.Length; index++)
        {
            var invalid = invalidResponses[index];
            using var source = new ManusBridgeUserWideTaskInventoryPageSource(
                () => "bridge-test-token",
                new Uri("https://bridge.example/v1/tasks"),
                ExpectedBridgeManusUserId,
                requestTimeout: TimeSpan.FromSeconds(2),
                testHandler: new FakeHttpHandler((_, _) => Task.FromResult(JsonResponse(invalid.Body, invalid.Status))));
            var snapshot = await new AutopilotGlobalTaskInventoryReader(
                source, scope).ReadAuthoritativeSnapshotAsync();

            Assert.False(snapshot.IsAuthoritative);
            Assert.Empty(snapshot.ActiveTasks);
            var claim = await ClaimAsync(taskIds[index], snapshot, allocatorScopeId: scope);
            if (claim.MayCallProvider) fakeProviderCalls++;
            claims.Add(claim);
        }

        Assert.All(claims, claim => Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, claim.Outcome));
        Assert.All(claims, claim => Assert.False(claim.MayCallProvider));
        Assert.Equal(0, fakeProviderCalls);

        await using var verify = CreateContext();
        var launchRows = await verify.AutopilotWaveLaunchTasks
            .Where(item => taskIds.Contains(item.Id))
            .ToArrayAsync();
        Assert.Equal(invalidResponses.Length, launchRows.Length);
        Assert.All(launchRows, row => Assert.Equal(AutopilotWaveLaunchStatuses.Planned, row.Status));
        Assert.All(launchRows, row => Assert.Equal(0, row.LaunchAttempt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("manus user id")]
    [InlineData("manus\nuser-id")]
    public void Bridge_user_wide_inventory_rejects_missing_or_malformed_expected_account_identity_before_http(string? expectedUserId)
    {
        var requests = 0;
        var handler = new FakeHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(JsonResponse(BuildBridgeInventoryPayload(0)));
        });

        var exception = Assert.Throws<ArgumentException>(() => new ManusBridgeUserWideTaskInventoryPageSource(
            () => "bridge-test-token",
            new Uri("https://bridge.example/v1/tasks"),
            expectedUserId!,
            testHandler: handler));

        Assert.Equal("expectedManusUserId", exception.ParamName);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Bridge_user_wide_inventory_timeout_and_missing_token_are_unknown_not_zero()
    {
        var scope = AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide;
        var taskIds = await SeedLaunchTasksAsync(total: 2, initiallyActive: 0, tasksPerWave: 2);
        var tokenCalls = 0;
        var missingTokenHandler = new FakeHttpHandler((_, _) => throw new InvalidOperationException("Must not request without Bridge token."));
        using (var missingTokenSource = new ManusBridgeUserWideTaskInventoryPageSource(
            () => { Interlocked.Increment(ref tokenCalls); return null; },
            new Uri("https://bridge.example/v1/tasks"),
            ExpectedBridgeManusUserId,
            testHandler: missingTokenHandler))
        {
            var missing = await new AutopilotGlobalTaskInventoryReader(
                missingTokenSource, scope).ReadAuthoritativeSnapshotAsync();
            Assert.False(missing.IsAuthoritative);
            Assert.Empty(missing.ActiveTasks);
            var claim = await ClaimAsync(taskIds[0], missing, allocatorScopeId: scope);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, claim.Outcome);
            Assert.False(claim.MayCallProvider);
        }

        var timeoutHandler = new FakeHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse(BuildBridgeInventoryPayload(0));
        });
        using var timeoutSource = new ManusBridgeUserWideTaskInventoryPageSource(
            () => "bridge-test-token",
            new Uri("https://bridge.example/v1/tasks"),
            ExpectedBridgeManusUserId,
            requestTimeout: TimeSpan.FromMilliseconds(50),
            testHandler: timeoutHandler);
        var timedOut = await new AutopilotGlobalTaskInventoryReader(
            timeoutSource, scope).ReadAuthoritativeSnapshotAsync();

        Assert.False(timedOut.IsAuthoritative);
        Assert.Empty(timedOut.ActiveTasks);
        Assert.Equal(1, tokenCalls);
        var timeoutClaim = await ClaimAsync(taskIds[1], timedOut, allocatorScopeId: scope);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, timeoutClaim.Outcome);
        Assert.False(timeoutClaim.MayCallProvider);

        await using var verify = CreateContext();
        var launchRows = await verify.AutopilotWaveLaunchTasks
            .Where(item => taskIds.Contains(item.Id))
            .ToArrayAsync();
        Assert.Equal(2, launchRows.Length);
        Assert.All(launchRows, row => Assert.Equal(AutopilotWaveLaunchStatuses.Planned, row.Status));
        Assert.All(launchRows, row => Assert.Equal(0, row.LaunchAttempt));
    }

    [Fact]
    public async Task Bridge_user_wide_inventory_requires_matching_account_identity_before_capacity_or_provider_authorization()
    {
        var launchTaskIds = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        var payloads = new[]
        {
            BuildBridgeInventoryPayload(0, reportedManusUserId: "different-manus-user"),
            BuildBridgeInventoryPayload(0, includeManusUserId: false),
            BuildBridgeInventoryPayload(0, reportedManusUserId: null),
            "{\"ok\":true,\"manus_user_id\":17,\"data\":[],\"has_more\":false}",
        };
        var fakeProviderCalls = 0;

        foreach (var payload in payloads)
        {
            using var source = new ManusBridgeUserWideTaskInventoryPageSource(
                () => "bridge-test-token",
                new Uri("https://bridge.example/v1/tasks"),
                ExpectedBridgeManusUserId,
                testHandler: new FakeHttpHandler((_, _) => Task.FromResult(JsonResponse(payload))));
            var snapshot = await new AutopilotGlobalTaskInventoryReader(
                source,
                AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide).ReadAuthoritativeSnapshotAsync();

            Assert.False(snapshot.IsAuthoritative);
            Assert.Empty(snapshot.ActiveTasks);
            Assert.Equal("inventory_page_invalid", snapshot.FailureCode);
            var claim = await ClaimAsync(
                launchTaskIds[0],
                snapshot,
                allocatorScopeId: AutopilotGlobalTaskInventoryScopeIds.ManusBridgeAuthenticatedUserWide);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, claim.Outcome);
            Assert.False(claim.MayCallProvider);
            if (claim.MayCallProvider) Interlocked.Increment(ref fakeProviderCalls);
        }

        await using var verify = CreateContext();
        var row = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launchTaskIds[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, row.Status);
        Assert.Equal(0, row.LaunchAttempt);
        Assert.Equal(0, fakeProviderCalls);
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

    [Theory]
    [InlineData("running")]
    [InlineData("waiting")]
    public async Task Locally_completed_task_resumed_as_running_or_waiting_still_occupies_its_global_slot(string resumedStatus)
    {
        var taskIds = await SeedLaunchTasksAsync(total: 22, initiallyActive: 20, tasksPerWave: 22);
        string resumedExternalTaskId;
        string completionKey;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            resumedExternalTaskId = task.ExternalTaskId!;
            completionKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
        }

        var completionEventId = await SeedCompletionEventAsync(taskIds[0]);
        await using (var db = CreateContext())
        {
            var completion = await CreateAllocator(db).CompleteVerifiedAsync(
                taskIds[0], completionKey, resumedExternalTaskId, completionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completion.Outcome);
        }

        var resumedInventory = new AutopilotGlobalTaskInventorySnapshot(
            ScopeId,
            true,
            1,
            false,
            null,
            Enumerable.Range(0, 20)
                .Select(index => new AutopilotGlobalTaskInventoryItem(
                    index == 0 ? resumedExternalTaskId : $"external-task-{index}",
                    ScopeId,
                    index == 0 ? resumedStatus : "running",
                    null))
                .ToArray());

        var fakeProviderLaunches = 0;
        var claim = await ClaimAsync(taskIds[20], resumedInventory);
        if (claim.MayCallProvider) fakeProviderLaunches++;

        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, claim.Outcome);
        Assert.Equal(20, claim.ActiveSlotsObserved);
        Assert.False(claim.MayCallProvider);
        Assert.Equal(0, fakeProviderLaunches);

        await using var verify = CreateContext();
        var completedTask = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
        var unlaunchedFollowUp = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[20]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Completed, completedTask.Status);
        Assert.Equal(resumedExternalTaskId, completedTask.ExternalTaskId);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, unlaunchedFollowUp.Status);
        Assert.Equal(0, unlaunchedFollowUp.LaunchAttempt);
        Assert.Null(unlaunchedFollowUp.ExternalTaskId);
        Assert.Equal(19, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    public async Task Concurrent_claims_count_resumed_background_task_and_partial_inventory_fails_closed(int initialExternalActiveCount)
    {
        var taskIds = await SeedLaunchTasksAsync(total: 26, initiallyActive: initialExternalActiveCount, tasksPerWave: 26);
        string resumedExternalTaskId;
        string siblingExternalTaskId;
        string siblingCompletionKey;
        await using (var db = CreateContext())
        {
            var resumed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0]);
            var sibling = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[1]);
            resumedExternalTaskId = resumed.ExternalTaskId!;
            siblingExternalTaskId = sibling.ExternalTaskId!;
            siblingCompletionKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(sibling.WaveKey, sibling.TaskKey);
        }

        var firstCompletionEventId = await SeedCompletionEventAsync(taskIds[0]);
        await using (var db = CreateContext())
        {
            var completed = await CreateAllocator(db).CompleteVerifiedAsync(
                taskIds[0],
                AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(
                    (await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).WaveKey,
                    (await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).TaskKey),
                resumedExternalTaskId,
                firstCompletionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completed.Outcome);
        }

        var resumedBackgroundJobs = false;
        var siblingIsStopped = false;
        var firstPageCount = (initialExternalActiveCount + 1) / 2;
        var handler = new FakeHttpHandler((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = ParseQuery(Assert.IsType<Uri>(request.RequestUri));
            var secondPage = query.TryGetValue("cursor", out var cursor);
            if (secondPage) Assert.Equal("page-2", cursor);
            else Assert.False(query.ContainsKey("cursor"));

            var start = secondPage ? firstPageCount : 0;
            var end = secondPage ? initialExternalActiveCount : firstPageCount;
            var data = Enumerable.Range(start, end - start)
                .Select(index => new
                {
                    id = $"external-task-{index}",
                    status = index == 0 || (index == 1 && siblingIsStopped) ? "stopped" : "running",
                    has_running_background_jobs = index == 0 && resumedBackgroundJobs,
                })
                .ToArray();
            var hasMore = end < initialExternalActiveCount;
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                data,
                has_more = hasMore,
                next_cursor = hasMore ? "page-2" : null,
            });
            return Task.FromResult(JsonResponse(payload));
        });
        using var pageSource = new ManusTaskListInventoryPageSource(
            () => "unit-test-token",
            ScopeId,
            "verified-test-creator-id",
            creatorIdentityVerified: true,
            AutopilotManusTaskListCredentialKind.OAuthBearer,
            pageTimeout: TimeSpan.FromSeconds(2),
            testHandler: handler);
        var reader = new AutopilotGlobalTaskInventoryReader(pageSource, ScopeId);

        var previouslyStoppedInventory = await reader.ReadAuthoritativeSnapshotAsync();
        Assert.True(previouslyStoppedInventory.IsAuthoritative);
        Assert.Equal(initialExternalActiveCount, previouslyStoppedInventory.ActiveTasks.Count);
        Assert.Equal("stopped", previouslyStoppedInventory.ActiveTasks.Single(item => item.TaskId == resumedExternalTaskId).Status);

        resumedBackgroundJobs = true;
        var resumedInventory = await reader.ReadAuthoritativeSnapshotAsync();
        Assert.True(resumedInventory.IsAuthoritative);
        Assert.Equal(2, resumedInventory.PagesRead);
        Assert.Equal(initialExternalActiveCount, resumedInventory.ActiveTasks.Count);
        Assert.Equal("running", resumedInventory.ActiveTasks.Single(item => item.TaskId == resumedExternalTaskId).Status);

        var fakeProviderCalls = 0;
        async Task<(Guid TaskId, AutopilotGlobalTaskSlotResult Result, bool ProviderCalled)> ClaimAndFakeProviderAsync(
            Guid taskId,
            AutopilotGlobalTaskInventorySnapshot inventory)
        {
            var result = await ClaimAsync(taskId, inventory);
            var providerCalled = result.MayCallProvider;
            if (providerCalled) Interlocked.Increment(ref fakeProviderCalls);
            return (taskId, result, providerCalled);
        }

        var raceClaimIds = new[] { taskIds[initialExternalActiveCount], taskIds[initialExternalActiveCount + 1] };
        var raceResults = await Task.WhenAll(raceClaimIds.Select(id => ClaimAndFakeProviderAsync(id, resumedInventory)));
        var expectedRaceAdmissions = 20 - initialExternalActiveCount;
        Assert.Equal(expectedRaceAdmissions, raceResults.Count(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.Reserved));
        Assert.Equal(2 - expectedRaceAdmissions, raceResults.Count(item => item.Result.Outcome == AutopilotGlobalTaskSlotOutcomes.CapacityReached));
        Assert.All(raceResults, item => Assert.Equal(item.Result.MayCallProvider, item.ProviderCalled));
        Assert.All(raceResults, item => Assert.True(item.Result.ActiveSlotsObserved <= AutopilotGlobalTaskSlotAllocator.HardMaximum));
        Assert.Equal(20, raceResults.Max(item => item.Result.ActiveSlotsObserved));
        Assert.Equal(expectedRaceAdmissions, fakeProviderCalls);

        var partialPageSource = new FakePageSource(ScopeId,
            (null, new AutopilotGlobalTaskInventoryPage(ScopeId,
                [new AutopilotGlobalTaskInventoryItem(resumedExternalTaskId, ScopeId, "running", null)],
                "page-2")),
            ("page-2", null));
        var partialInventory = await new AutopilotGlobalTaskInventoryReader(partialPageSource, ScopeId).ReadAuthoritativeSnapshotAsync();
        Assert.False(partialInventory.IsAuthoritative);
        Assert.Equal("inventory_page_invalid", partialInventory.FailureCode);
        Assert.Equal(2, partialInventory.PagesRead);
        Assert.Empty(partialInventory.ActiveTasks);

        var unknownClaimIds = new[] { taskIds[initialExternalActiveCount + 2], taskIds[initialExternalActiveCount + 3] };
        var unknownResults = await Task.WhenAll(unknownClaimIds.Select(id => ClaimAndFakeProviderAsync(id, partialInventory)));
        Assert.All(unknownResults, item => Assert.Equal(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, item.Result.Outcome));
        Assert.All(unknownResults, item => Assert.False(item.Result.MayCallProvider));
        Assert.All(unknownResults, item => Assert.False(item.ProviderCalled));
        Assert.Equal(expectedRaceAdmissions, fakeProviderCalls);

        var siblingCompletionEventId = await SeedCompletionEventAsync(taskIds[1]);
        await using (var db = CreateContext())
        {
            var completed = await CreateAllocator(db).CompleteVerifiedAsync(
                taskIds[1], siblingCompletionKey, siblingExternalTaskId, siblingCompletionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completed.Outcome);
        }

        siblingIsStopped = true;
        var postTerminalInventory = await reader.ReadAuthoritativeSnapshotAsync();
        Assert.True(postTerminalInventory.IsAuthoritative);
        Assert.Equal("running", postTerminalInventory.ActiveTasks.Single(item => item.TaskId == resumedExternalTaskId).Status);
        Assert.Equal("stopped", postTerminalInventory.ActiveTasks.Single(item => item.TaskId == siblingExternalTaskId).Status);

        var refill = await ClaimAndFakeProviderAsync(taskIds[24], postTerminalInventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, refill.Result.Outcome);
        Assert.Equal(20, refill.Result.ActiveSlotsObserved);
        Assert.True(refill.Result.MayCallProvider);
        Assert.True(refill.ProviderCalled);

        await using (var db = CreateContext())
        {
            var duplicate = await CreateAllocator(db).CompleteVerifiedAsync(
                taskIds[1], siblingCompletionKey, siblingExternalTaskId, siblingCompletionEventId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion, duplicate.Outcome);
        }

        var noDuplicateRefill = await ClaimAndFakeProviderAsync(taskIds[25], postTerminalInventory);
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, noDuplicateRefill.Result.Outcome);
        Assert.Equal(20, noDuplicateRefill.Result.ActiveSlotsObserved);
        Assert.False(noDuplicateRefill.Result.MayCallProvider);
        Assert.False(noDuplicateRefill.ProviderCalled);
        Assert.Equal(expectedRaceAdmissions + 1, fakeProviderCalls);

        await using var verify = CreateContext();
        Assert.Equal(AutopilotWaveLaunchStatuses.Completed,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[0])).Status);
        Assert.Equal(AutopilotWaveLaunchStatuses.Completed,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[1])).Status);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[24])).Status);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned,
            (await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskIds[25])).Status);
        Assert.Equal(19, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
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

        await using (var db = CreateContext())
        {
            var accepted = await CreateAllocator(db).RecordAcceptedAsync(ids[0], key, "external-task-after-safe-retry");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, accepted.Outcome);
            Assert.True(accepted.SlotHeld);
            Assert.False(accepted.MayCallProvider);
        }

        await using var verify = CreateContext();
        var recovered = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched, recovered.Status);
        Assert.Equal(2, recovered.LaunchAttempt);
        Assert.Equal("external-task-after-safe-retry", recovered.ExternalTaskId);
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
    public async Task Accepted_provider_id_without_durable_reservation_cannot_launch_or_free_a_global_slot()
    {
        var ids = await SeedLaunchTasksAsync(total: 21, initiallyActive: 20, tasksPerWave: 21);
        string key;
        const string attemptedExternalTaskId = "external-task-without-reservation";
        const string legacyDetail = "legacy_unmarked_launching_row";
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.ExternalTaskId = null;
            task.LaunchedAt = null;
            task.CompletedAt = null;
            task.ReasonCode = null;
            task.LastStatusDetail = legacyDetail;
            task.UpdatedAt = DateTime.UtcNow;
            task.ConcurrencyToken = Guid.NewGuid();
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            await db.SaveChangesAsync();
        }

        var fakeProviderCalls = 0;
        await using (var db = CreateContext())
        {
            var accepted = await CreateAllocator(db).RecordAcceptedAsync(ids[0], key, attemptedExternalTaskId);
            if (accepted.MayCallProvider) fakeProviderCalls++;
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, accepted.Outcome);
            Assert.True(accepted.SlotHeld);
            Assert.False(accepted.MayCallProvider);
            Assert.Equal("global_slot_reservation_provenance_missing", accepted.ReasonCode);
        }

        await using (var verifyUnmarked = CreateContext())
        {
            var stillUnmarked = await verifyUnmarked.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, stillUnmarked.Status);
            Assert.Null(stillUnmarked.ExternalTaskId);
            Assert.Null(stillUnmarked.ReasonCode);
            Assert.Equal(legacyDetail, stillUnmarked.LastStatusDetail);
        }

        await using (var db = CreateContext())
        {
            var allocator = CreateAllocator(db);
            var ambiguous = await allocator.RecordAmbiguousAsync(ids[0], key, "legacy_timeout");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, ambiguous.Outcome);
            Assert.True(ambiguous.SlotHeld);
            Assert.False(ambiguous.MayCallProvider);

            var acceptedAfterAmbiguous = await allocator.RecordAcceptedAsync(ids[0], key, attemptedExternalTaskId);
            if (acceptedAfterAmbiguous.MayCallProvider) fakeProviderCalls++;
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, acceptedAfterAmbiguous.Outcome);
            Assert.True(acceptedAfterAmbiguous.SlotHeld);
            Assert.False(acceptedAfterAmbiguous.MayCallProvider);
            Assert.Equal("global_slot_reservation_provenance_missing", acceptedAfterAmbiguous.ReasonCode);
        }

        var twentyFirst = await ClaimAsync(ids[20], CompleteInventory());
        if (twentyFirst.MayCallProvider) fakeProviderCalls++;
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.CapacityReached, twentyFirst.Outcome);
        Assert.Equal(20, twentyFirst.ActiveSlotsObserved);
        Assert.False(twentyFirst.MayCallProvider);
        Assert.Equal(0, fakeProviderCalls);

        await using var verify = CreateContext();
        var unmarked = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, unmarked.Status);
        Assert.Null(unmarked.ExternalTaskId);
        Assert.Null(unmarked.LaunchedAt);
        Assert.Null(unmarked.CompletedAt);
        Assert.Equal("global_slot_reserved", unmarked.ReasonCode);
        Assert.NotNull(unmarked.LastStatusDetail);
        Assert.StartsWith("slot_retained_until_authoritative_reconciliation:", unmarked.LastStatusDetail!, StringComparison.Ordinal);
        Assert.Equal(1, unmarked.LaunchAttempt);
        Assert.Equal(20, await verify.AutopilotWaveLaunchTasks.CountAsync(item =>
            item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        var deniedTask = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[20]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Planned, deniedTask.Status);
        Assert.Equal(0, deniedTask.LaunchAttempt);
        Assert.Null(deniedTask.ExternalTaskId);
    }

    [Theory]
    [InlineData("global_slot_claimed:forged")]
    [InlineData("global_slot_claimed:0/20")]
    [InlineData("global_slot_claimed:21/20")]
    [InlineData("global_slot_claimed:1/19")]
    [InlineData("global_slot_claimed:1/20/extra")]
    public async Task Malformed_or_forged_admission_detail_cannot_authorize_provider_acceptance(string detail)
    {
        var ids = await SeedLaunchTasksAsync(total: 1, initiallyActive: 0);
        string key;
        await using (var db = CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.LaunchAttempt = 1;
            task.ReasonCode = "global_slot_reserved";
            task.LastStatusDetail = detail;
            key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            await db.SaveChangesAsync();
        }

        const string attemptedExternalTaskId = "external-task-forged-marker";
        await using (var db = CreateContext())
        {
            var result = await CreateAllocator(db).RecordAcceptedAsync(ids[0], key, attemptedExternalTaskId);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, result.Outcome);
            Assert.True(result.SlotHeld);
            Assert.False(result.MayCallProvider);
            Assert.Equal("global_slot_reservation_provenance_missing", result.ReasonCode);
        }

        await using var verify = CreateContext();
        var unchanged = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == ids[0]);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, unchanged.Status);
        Assert.Equal(1, unchanged.LaunchAttempt);
        Assert.Equal("global_slot_reserved", unchanged.ReasonCode);
        Assert.Equal(detail, unchanged.LastStatusDetail);
        Assert.Null(unchanged.ExternalTaskId);
        Assert.Null(unchanged.LaunchedAt);
        Assert.Null(unchanged.CompletedAt);
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
            var allocator = CreateAllocator(db);
            var accepted = await allocator.RecordAcceptedAsync(ids[0], key, "external-task-recovered");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, accepted.Outcome);
            Assert.True(accepted.SlotHeld);
            Assert.False(accepted.MayCallProvider);

            var duplicate = await allocator.RecordAcceptedAsync(ids[0], key, "external-task-recovered");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, duplicate.Outcome);
            Assert.True(duplicate.SlotHeld);
            Assert.False(duplicate.MayCallProvider);

            var conflict = await allocator.RecordAcceptedAsync(ids[0], key, "different-external-task");
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, conflict.Outcome);
            Assert.True(conflict.SlotHeld);
            Assert.False(conflict.MayCallProvider);
            Assert.Equal("external-task-recovered", conflict.ExternalTaskId);
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
        DbContextOptions<TaslimDbContext>? contextOptions = null,
        string? allocatorScopeId = null)
    {
        await using var db = contextOptions is null ? CreateContext() : new TaslimDbContext(contextOptions);
        var task = await db.AutopilotWaveLaunchTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var key = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
        return await CreateAllocator(db, allocatorScopeId).ReserveOrRecoverLaunchAsync(
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

    private AutopilotGlobalTaskSlotAllocator CreateAllocator(TaslimDbContext db, string? allocatorScopeId = null) =>
        new(db, allocatorScopeId ?? ScopeId, TimeProvider.System, AutopilotGlobalTaskSlotAllocator.HardMaximum);

    private static AutopilotGlobalTaskInventorySnapshot CompleteInventory() =>
        new(ScopeId, true, 1, false, null, Array.Empty<AutopilotGlobalTaskInventoryItem>());

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset current;

        public ManualTimeProvider(DateTimeOffset initial) => current = initial;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan elapsed) => current = current.Add(elapsed);
    }

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

    private static string BuildBridgeInventoryPayload(
        int taskCount,
        string? reportedManusUserId = ExpectedBridgeManusUserId,
        bool includeManusUserId = true)
    {
        var tasks = Enumerable.Range(0, taskCount).Select(index =>
        {
            var task = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = BridgeNativeTaskId(index),
                ["status"] = index < 2 ? "stopped" : index % 2 == 0 ? "running" : "waiting",
                ["created_by_api_key"] = index % 3 == 2 ? null : new { id = $"api-key-{index % 2}", name = $"creator-{index % 2}" },
            };
            if (index == 0) task["has_running_background_jobs"] = true;
            else if (index == 1) task["has_running_background_jobs"] = false;
            return task;
        }).ToArray();

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["request_id"] = "synthetic-bridge-inventory",
            ["data"] = tasks,
            ["has_more"] = false,
        };
        if (includeManusUserId) payload["manus_user_id"] = reportedManusUserId;

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    private static string BridgeNativeTaskId(int index) => index.ToString("D22", System.Globalization.CultureInfo.InvariantCulture);

    private static System.Net.Http.HttpResponseMessage JsonResponse(
        string json,
        System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK) => new(statusCode)
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

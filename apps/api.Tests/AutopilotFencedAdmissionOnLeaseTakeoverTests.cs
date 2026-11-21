using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// PostgreSQL-backed admission coverage for lease takeover. Every Bridge response
/// is synthesized locally; this fixture never calls Manus or a deployed Bridge.
/// </summary>
public sealed class AutopilotFencedAdmissionOnLeaseTakeoverTests
{
    private const string ScopeId = "taslimai-autopilot";
    private const string ResourceKey = "autopilot:completion-launch:test-fenced-admission";

    [Fact]
    public async Task PostgreSql_takeover_blocks_stale_replacement_and_retry_creates_only_once()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        // Match the existing optional PostgreSQL integration-test convention: CI
        // supplies this connection string; local runs without it do not hit a live DB.
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schemaName = $"fenced_admission_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schemaName,
                Pooling = false,
            }.ConnectionString;
            var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            await using (var setup = new TaslimDbContext(dbOptions))
                await setup.Database.EnsureCreatedAsync();

            var leaseOptions = new AutopilotOptions { LockLeaseMinutes = 1 };
            AutopilotLockHandle staleFence;
            await using (var staleContext = new TaslimDbContext(dbOptions))
            {
                staleFence = await new EfAutopilotLockService(staleContext, leaseOptions)
                    .AcquireAsync(ResourceKey, "stale-holder");
                Assert.True(staleFence.IsAcquired);
            }

            await using (var expiryContext = new TaslimDbContext(dbOptions))
            {
                var expired = await expiryContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AutopilotLocks"
                    SET "ExpiresAt" = clock_timestamp() - interval '1 second'
                    WHERE "ResourceKey" = {ResourceKey}
                    """);
                Assert.Equal(1, expired);
            }

            AutopilotLockHandle takeoverFence;
            await using (var takeoverContext = new TaslimDbContext(dbOptions))
            {
                takeoverFence = await new EfAutopilotLockService(takeoverContext, leaseOptions)
                    .AcquireAsync(ResourceKey, "replacement-holder");
                Assert.True(takeoverFence.IsAcquired);
                Assert.True(takeoverFence.FencingToken > staleFence.FencingToken);
            }

            await using (var staleRenewalContext = new TaslimDbContext(dbOptions))
            {
                var staleRenewal = await new EfAutopilotLockService(staleRenewalContext, leaseOptions)
                    .RenewAsync(staleFence);
                Assert.False(staleRenewal.IsAcquired);
            }

            Guid launchTaskId;
            string idempotencyKey;
            await using (var seedContext = new TaslimDbContext(dbOptions))
            {
                (launchTaskId, idempotencyKey) = await SeedPlannedLaunchTaskAsync(seedContext);
            }

            // The fake Bridge has already observed the takeover epoch. A delayed
            // old holder reaches only this fake and is rejected before any create.
            var fakeBridge = new HighWaterIdempotentBridgeHandler(takeoverFence.FencingToken);
            var tokenVariable = $"TASLIM_TEST_BRIDGE_{Guid.NewGuid():N}";
            var priorTokenValue = Environment.GetEnvironmentVariable(tokenVariable);
            Environment.SetEnvironmentVariable(tokenVariable, "fake-only-test-credential");
            try
            {
                using var http = new HttpClient(fakeBridge);
                var provider = new ManusBridgeWaveLaunchProvider(http, new AutopilotOptions
                {
                    BridgeBaseUrl = "https://bridge.example.test",
                    BridgeTokenEnvironmentVariable = tokenVariable,
                    BridgeCreateTaskPath = "/v1/tasks",
                    BridgeTimeoutSeconds = 5,
                });

                var staleAttempt = await provider.LaunchTaskAsync(CreateRequest(idempotencyKey, staleFence.FencingToken));
                Assert.False(staleAttempt.IsLaunched);
                Assert.Equal(WaveLaunchProviderOutcomes.Rejected, staleAttempt.Outcome);
                Assert.Equal(0, fakeBridge.CreatedTaskCount);

                AutopilotGlobalTaskSlotResult reservation;
                await using (var admissionContext = new TaslimDbContext(dbOptions))
                {
                    var inventory = EmptyAuthoritativeInventory();
                    var allocator = new AutopilotGlobalTaskSlotAllocator(admissionContext, ScopeId);
                    reservation = await allocator.ReserveOrRecoverLaunchAsync(
                        launchTaskId,
                        idempotencyKey,
                        inventory,
                        TimeSpan.FromMinutes(5),
                        providerGuaranteesIdempotency: true);
                }
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Reserved, reservation.Outcome);
                Assert.True(reservation.MayCallProvider);

                var acceptedRequest = CreateRequest(idempotencyKey, takeoverFence.FencingToken);
                var accepted = await provider.LaunchTaskAsync(acceptedRequest);
                Assert.True(accepted.IsLaunched);
                Assert.Equal("fake-bridge-task-1", accepted.ExternalTaskId);

                // Simulate retry after the HTTP response was lost: same fencing
                // epoch and idempotency key must return the existing fake task.
                var retried = await provider.LaunchTaskAsync(acceptedRequest);
                Assert.True(retried.IsLaunched);
                Assert.Equal(accepted.ExternalTaskId, retried.ExternalTaskId);
                Assert.Equal(1, fakeBridge.CreatedTaskCount);

                await using (var acceptedContext = new TaslimDbContext(dbOptions))
                {
                    var persisted = await new AutopilotGlobalTaskSlotAllocator(acceptedContext, ScopeId)
                        .RecordAcceptedAsync(launchTaskId, idempotencyKey, accepted.ExternalTaskId!);
                    Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, persisted.Outcome);
                    Assert.True(persisted.SlotHeld);
                    Assert.False(persisted.MayCallProvider);
                }

                var retryInventory = new AutopilotGlobalTaskInventorySnapshot(
                    ScopeId,
                    true,
                    1,
                    false,
                    null,
                    [new AutopilotGlobalTaskInventoryItem(
                        accepted.ExternalTaskId!, ScopeId, "running", idempotencyKey)]);
                await using (var retryContext = new TaslimDbContext(dbOptions))
                {
                    var retryAdmission = await new AutopilotGlobalTaskSlotAllocator(retryContext, ScopeId)
                        .ReserveOrRecoverLaunchAsync(
                            launchTaskId,
                            idempotencyKey,
                            retryInventory,
                            TimeSpan.FromMinutes(5),
                            providerGuaranteesIdempotency: true);
                    Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, retryAdmission.Outcome);
                    Assert.False(retryAdmission.MayCallProvider);
                }

                Assert.Equal(3, fakeBridge.RequestCount); // stale attempt, create, idempotent retry
                Assert.Equal(1, fakeBridge.CreatedTaskCount);
            }
            finally
            {
                Environment.SetEnvironmentVariable(tokenVariable, priorTokenValue);
            }
        }
        finally
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    // RED contract: the current provider treats any 2xx response containing a
    // task ID as accepted. Until Bridge returns a high-water acknowledgment and
    // the provider verifies it matches the request epoch, this contract cannot
    // pass and must remain unmerged. These responses are synthetic, not live calls.
    [Theory]
    [InlineData(false)] // Bridge omitted its fencing high-water acknowledgment.
    [InlineData(true)]  // Bridge acknowledged a token older than this request.
    public async Task Bridge_task_id_without_matching_high_water_ack_is_not_admission(bool includeStaleAcknowledgment)
    {
        const long requestedFence = 2;
        var responseBody = includeStaleAcknowledgment
            ? JsonSerializer.Serialize(new { task_id = "unverified-fake-task", fencing_high_water_token = requestedFence - 1 })
            : JsonSerializer.Serialize(new { task_id = "unverified-fake-task" });
        var tokenVariable = $"TASLIM_TEST_BRIDGE_{Guid.NewGuid():N}";
        var priorTokenValue = Environment.GetEnvironmentVariable(tokenVariable);
        Environment.SetEnvironmentVariable(tokenVariable, "fake-only-test-credential");
        try
        {
            using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, responseBody));
            var provider = new ManusBridgeWaveLaunchProvider(http, new AutopilotOptions
            {
                BridgeBaseUrl = "https://bridge.example.test",
                BridgeTokenEnvironmentVariable = tokenVariable,
                BridgeCreateTaskPath = "/v1/tasks",
                BridgeTimeoutSeconds = 5,
            });

            var result = await provider.LaunchTaskAsync(CreateRequest("test-idempotency-key", requestedFence));

            Assert.False(result.IsLaunched,
                "RED contract: task identity alone must not authorize admission; Bridge must acknowledge the request's exact fencing high-water epoch.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, priorTokenValue);
        }
    }

    private static WaveLaunchProviderRequest CreateRequest(string idempotencyKey, long fencingToken) =>
        new("replacement-wave", "replacement-task", "Replacement", "Fake-only integration test", new string('a', 40), "feature/replacement", idempotencyKey)
        {
            FencingToken = fencingToken,
        };

    private static AutopilotGlobalTaskInventorySnapshot EmptyAuthoritativeInventory() =>
        new(ScopeId, true, 1, false, null, Array.Empty<AutopilotGlobalTaskInventoryItem>());

    private static async Task<(Guid TaskId, string IdempotencyKey)> SeedPlannedLaunchTaskAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var run = new AutopilotRun
        {
            Id = Guid.NewGuid(),
            WaveKey = "source-wave-fenced-admission",
            RunKey = "run-fenced-admission",
            State = AutopilotRunStates.InProgress,
            BaseSha = new string('a', 40),
            CandidateSha = new string('b', 40),
            Branch = "feature/fenced-admission",
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        };
        var batch = new AutopilotWaveLaunchBatch
        {
            Id = Guid.NewGuid(),
            SourceRunId = run.Id,
            SourceWaveKey = run.WaveKey,
            WaveKey = "replacement-wave",
            Status = AutopilotWaveLaunchStatuses.Planned,
            BaseSha = run.CandidateSha,
            Branch = run.Branch,
            PlanFingerprint = "test-fingerprint",
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        };
        var task = new AutopilotWaveLaunchTask
        {
            Id = Guid.NewGuid(),
            BatchId = batch.Id,
            SourceRunId = run.Id,
            SourceWaveKey = run.WaveKey,
            WaveKey = batch.WaveKey,
            TaskKey = "replacement-task",
            Title = "Replacement task",
            BacklogItemKey = "backlog-fenced-admission",
            Status = AutopilotWaveLaunchStatuses.Planned,
            Branch = batch.Branch,
            BaseSha = batch.BaseSha,
            LaunchAttempt = 0,
            MaxLaunchAttempts = 3,
            ReconcileCount = 0,
            MaxReconciles = 10,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        };

        db.AutopilotRuns.Add(run);
        db.AutopilotWaveLaunchBatches.Add(batch);
        db.AutopilotWaveLaunchTasks.Add(task);
        await db.SaveChangesAsync();
        return (task.Id, AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey));
    }

    private sealed class HighWaterIdempotentBridgeHandler(long initialHighWaterToken) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> tasksByIdempotencyKey = new(StringComparer.Ordinal);
        private long highWaterToken = initialHighWaterToken;
        private int requestCount;
        private int createdTaskCount;

        public int RequestCount => Volatile.Read(ref requestCount);
        public int CreatedTaskCount => Volatile.Read(ref createdTaskCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref requestCount);
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/tasks")
                return Task.FromResult(JsonResponse(HttpStatusCode.NotFound, new { error = "unexpected_fake_route" }));

            var rawFence = request.Headers.TryGetValues("X-Taslim-Fencing-Token", out var fenceValues)
                ? fenceValues.SingleOrDefault()
                : null;
            if (!long.TryParse(rawFence, NumberStyles.None, CultureInfo.InvariantCulture, out var requestedFence))
                return Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, new { error = "fencing_token_missing" }));
            if (requestedFence < highWaterToken)
                return Task.FromResult(JsonResponse(HttpStatusCode.Conflict, new { error = "stale_fencing_token", fencing_high_water_token = highWaterToken }));
            if (requestedFence > highWaterToken)
                highWaterToken = requestedFence;

            var key = request.Headers.TryGetValues("Idempotency-Key", out var keyValues)
                ? keyValues.SingleOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(key))
                return Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, new { error = "idempotency_key_missing" }));

            if (!tasksByIdempotencyKey.TryGetValue(key, out var taskId))
            {
                taskId = "fake-bridge-task-1";
                tasksByIdempotencyKey.Add(key, taskId);
                Interlocked.Increment(ref createdTaskCount);
            }

            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new { task_id = taskId, fencing_high_water_token = highWaterToken }));
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) => new(statusCode)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

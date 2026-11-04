using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// End-to-end acceptance for the unclaimed HTTP intake/watchdog integration.
/// It uses only an in-memory SQLite database and a fake launch provider.
/// </summary>
public sealed class AutopilotEventDispatchIntegrationTests
{
    private const string SigningSecret = "autopilot-event-dispatch-integration-secret";
    private const string WaveKey = "wave-immediate-dispatch-next";
    private const string ParentWaveKey = "wave-immediate-dispatch";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task Signed_completion_wakes_watchdog_and_launches_replacement_without_hourly_wait()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            ConfigureLiveFakePosture(factory);
            await EnsureDatabaseAndApproveOneBacklogItemAsync(factory, "backlog-immediate-1");

            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var payload = CompletionPayload();
                var eventId = $"evt-immediate-{Guid.NewGuid():N}";
                using var first = await PostSignedEventAsync(client, payload, eventId);
                Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

                var launched = await factory.Provider.LaunchReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);
                await WaitForCompletionAndLaunchAsync(factory, eventId, TimeSpan.FromSeconds(8));

                // An identical delivery must wake/re-drive the durable queue, but
                // cannot create a second replacement task. Send it after the
                // first worker pass settles so the SQLite test store is not
                // contended by an HTTP duplicate and the background processor.
                using var duplicate = await PostSignedEventAsync(client, payload, eventId);
                var duplicateContent = await duplicate.Content.ReadAsStringAsync();
                Assert.True(duplicate.StatusCode == HttpStatusCode.OK,
                    $"Expected duplicate acknowledgement; received {(int)duplicate.StatusCode}: {duplicateContent}");
                var duplicateBody = JsonSerializer.Deserialize<JsonElement>(duplicateContent);
                Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicateBody.GetProperty("status").GetString());

                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var intake = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
                Assert.Equal(AutopilotEventStatuses.Processed, intake.Status);
                Assert.Equal(1, await db.AutopilotEvents.CountAsync(item =>
                    item.SourceSystem == "completion-bridge" && item.ExternalEventId == eventId));

                var sourceRun = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == WaveKey);
                Assert.True(sourceRun.IntegrationGatePassed);
                Assert.True(sourceRun.ReleaseGatePassed);
                Assert.Equal(AutopilotRunStates.HandoffPendingHuman, sourceRun.State);

                var replacement = await db.AutopilotWaveLaunchTasks.SingleAsync();
                Assert.Equal("backlog-immediate-1", replacement.BacklogItemKey);
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, replacement.Status);
                Assert.Single(factory.Provider.Launches);
                Assert.NotEmpty(factory.Provider.Launches.Single().IdempotencyKey);
            }
            finally
            {
                await watchdog.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
                Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Signed_terminal_receipt_wakes_orchestrator_releases_exact_slot_and_refills_while_sibling_is_active()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            ConfigureLiveFakePosture(factory);
            await SeedLaunchedTaskAndActiveSiblingAsync(factory);

            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var eventId = $"evt-receipt-refill-{Guid.NewGuid():N}";
                var payload = TerminalReceiptCompletionPayload(eventId);
                using var response = await PostSignedEventAsync(client, payload, eventId);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

                var launched = await WaitForProviderLaunchOrExplainAsync(factory, eventId, TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);
                await WaitForReceiptRefillAsync(factory, eventId, TimeSpan.FromSeconds(8));

                // Duplicate delivery cannot release another slot or create a second refill.
                using var duplicate = await PostSignedEventAsync(client, payload, eventId);
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var intake = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
                Assert.Equal(AutopilotEventStatuses.Processed, intake.Status);

                var completed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "finished-task-1");
                Assert.Equal(AutopilotWaveLaunchStatuses.Completed, completed.Status);
                Assert.Equal("verified_terminal_evidence", completed.ReasonCode);
                Assert.Equal("verified_completion_refill_created", completed.LastStatusDetail);
                Assert.Equal("synthetic-native-task-detail-id", completed.ExternalTaskId);

                var sibling = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "active-sibling-1");
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, sibling.Status);

                var refillWavePrefix = $"{WaveKey}-refill-";
                var refillBatch = await db.AutopilotWaveLaunchBatches.SingleAsync(item => item.WaveKey.StartsWith(refillWavePrefix));
                var replacement = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.BatchId == refillBatch.Id);
                Assert.Equal("backlog-receipt-refill-1", replacement.BacklogItemKey);
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, replacement.Status);
                Assert.Equal(1, await db.AutopilotWaveLaunchBatches.CountAsync(item => item.WaveKey.StartsWith(refillWavePrefix)));
                Assert.Single(factory.Provider.Launches);
            }
            finally
            {
                await watchdog.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Startup_recovery_processes_committed_event_if_process_died_before_wake_signal()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            ConfigureLiveFakePosture(factory);
            await EnsureDatabaseAndApproveOneBacklogItemAsync(factory, "backlog-restart-1");

            var payload = CompletionPayload();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = ComputeSignature(timestamp, payload);
            using (var scope = factory.Services.CreateScope())
            {
                var intake = scope.ServiceProvider.GetRequiredService<IAutopilotEventIntake>();
                var saved = await intake.SubmitAsync(new AutopilotIntakeRequest(
                    signature,
                    timestamp,
                    payload,
                    "completion-bridge",
                    $"evt-committed-before-crash-{Guid.NewGuid():N}",
                    AutopilotEventTypes.WaveTaskCompleted));
                Assert.Equal(AutopilotIntakeOutcomes.Accepted, saved.Outcome);
            }

            // No process-local notification was sent. Starting the watchdog is
            // equivalent to restarting after the durable commit; its first pass
            // recovers the queued row before the next-wave check.
            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var launched = await factory.Provider.LaunchReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);
                Assert.Single(factory.Provider.Launches);
            }
            finally
            {
                await watchdog.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    private static async Task WaitForCompletionAndLaunchAsync(DispatchTestFactory factory, string eventId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var intake = await db.AutopilotEvents.SingleOrDefaultAsync(item => item.ExternalEventId == eventId);
            var replacement = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync();
            if (intake?.Status == AutopilotEventStatuses.Processed
                && replacement?.Status == AutopilotWaveLaunchStatuses.Launched)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("The event was not processed and its approved replacement was not durably launched before the test deadline.");
    }

    private static AutopilotWatchdogService CreateWatchdog(DispatchTestFactory factory) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<AutopilotOptions>>(),
            NullLogger<AutopilotWatchdogService>.Instance);

    private static void ConfigureLiveFakePosture(DispatchTestFactory factory)
    {
        var options = factory.Services.GetRequiredService<AutopilotOptions>();
        options.Enabled = true;
        options.DryRun = false;
        options.AllowNextWaveLaunch = true;
        options.AllowNextWaveLaunchWhenReleasePending = true;
        options.AllowAutomaticRelease = false;
        options.AllowAutomaticIntegrationMerge = false;
        options.ChargingEnabled = false;
        options.PaidProvidersEnabled = false;
        options.MaxTasksPerWave = 1;
        options.WatchdogIntervalMinutes = 60;
        options.Normalize();
    }

    private static async Task EnsureDatabaseAndApproveOneBacklogItemAsync(DispatchTestFactory factory, string itemKey)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.AutopilotBacklogItems.Add(new AutopilotBacklogItem
        {
            Id = Guid.NewGuid(),
            ItemKey = itemKey,
            Title = "Approved replacement task",
            AcceptanceSummary = "Complete the isolated integration acceptance test.",
            Kind = AutopilotBacklogItemKinds.Development,
            Approved = true,
            ApprovedBy = "integration-test-operator",
            ApprovedAt = DateTime.UtcNow,
            ApprovalNote = "Synthetic test approval only.",
            Priority = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLaunchedTaskAndActiveSiblingAsync(DispatchTestFactory factory)
    {
        const string backlogKey = "backlog-receipt-refill-1";
        await EnsureDatabaseAndApproveOneBacklogItemAsync(factory, backlogKey);
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var parentRunId = Guid.NewGuid();
        var eventRunId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        db.AutopilotRuns.AddRange(new AutopilotRun
        {
            Id = parentRunId,
            WaveKey = ParentWaveKey,
            RunKey = $"run-{ParentWaveKey}",
            State = AutopilotRunStates.HandoffPendingHuman,
            BaseSha = BaseSha,
            CandidateSha = CandidateSha,
            Branch = "feature/synthetic-wave",
            ExpectedTaskCount = 1,
            IntegrationGatePassed = true,
            ReleaseGatePassed = true,
            HumanDecisionRequired = AutopilotHumanDecisions.ProductionRelease,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        }, new AutopilotRun
        {
            Id = eventRunId,
            WaveKey = WaveKey,
            RunKey = $"run-{WaveKey}",
            State = AutopilotRunStates.InProgress,
            BaseSha = BaseSha,
            CandidateSha = CandidateSha,
            Branch = "feature/synthetic-wave",
            TaskCount = 2,
            ExpectedTaskCount = 2,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        });
        db.AutopilotWaveTasks.AddRange(
            new AutopilotWaveTask
            {
                Id = Guid.NewGuid(), RunId = eventRunId, WaveKey = WaveKey, TaskId = "finished-task-1",
                State = AutopilotTaskStates.AwaitingResult, Attempt = 1, MaxAttempts = 3,
                Branch = "feature/synthetic-wave", BaseSha = BaseSha, CandidateSha = CandidateSha,
                CreatedAt = now, UpdatedAt = now,
            },
            new AutopilotWaveTask
            {
                Id = Guid.NewGuid(), RunId = eventRunId, WaveKey = WaveKey, TaskId = "sibling-controller-task",
                State = AutopilotTaskStates.Running, Attempt = 1, MaxAttempts = 3,
                Branch = "feature/synthetic-wave", BaseSha = BaseSha, CandidateSha = CandidateSha,
                CreatedAt = now, UpdatedAt = now,
            });
        db.AutopilotWaveLaunchBatches.Add(new AutopilotWaveLaunchBatch
        {
            Id = batchId,
            SourceRunId = parentRunId,
            SourceWaveKey = ParentWaveKey,
            WaveKey = WaveKey,
            Status = AutopilotWaveLaunchStatuses.Launched,
            BaseSha = CandidateSha,
            Branch = "feature/synthetic-wave",
            PlanFingerprint = "synthetic-existing-batch",
            TaskCount = 2,
            LaunchedTaskCount = 2,
            DryRun = false,
            MaxLaunchAttempts = 3,
            MaxReconciles = 24,
            CreatedAt = now,
            UpdatedAt = now,
            LaunchedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        });
        db.AutopilotWaveLaunchTasks.AddRange(
            new AutopilotWaveLaunchTask
            {
                Id = Guid.NewGuid(), BatchId = batchId, SourceRunId = parentRunId,
                SourceWaveKey = ParentWaveKey, WaveKey = WaveKey,
                TaskKey = "finished-task-1", Title = "Finished synthetic task", BacklogItemKey = "prior-item-1",
                ExternalTaskId = "synthetic-native-task-detail-id", Status = AutopilotWaveLaunchStatuses.Launched,
                Branch = "feature/synthetic-wave", BaseSha = CandidateSha, DryRun = false,
                LaunchAttempt = 1, MaxLaunchAttempts = 3, MaxReconciles = 24,
                CreatedAt = now, UpdatedAt = now, LaunchedAt = now, ConcurrencyToken = Guid.NewGuid(),
            },
            new AutopilotWaveLaunchTask
            {
                Id = Guid.NewGuid(), BatchId = batchId, SourceRunId = parentRunId,
                SourceWaveKey = ParentWaveKey, WaveKey = WaveKey,
                TaskKey = "active-sibling-1", Title = "Active synthetic sibling", BacklogItemKey = "prior-item-2",
                ExternalTaskId = "synthetic-native-sibling-id", Status = AutopilotWaveLaunchStatuses.Launched,
                Branch = "feature/synthetic-wave", BaseSha = CandidateSha, DryRun = false,
                LaunchAttempt = 1, MaxLaunchAttempts = 3, MaxReconciles = 24,
                CreatedAt = now, UpdatedAt = now, LaunchedAt = now, ConcurrencyToken = Guid.NewGuid(),
            });
        await db.SaveChangesAsync();
    }

    private static async Task WaitForReceiptRefillAsync(DispatchTestFactory factory, string eventId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var intake = await db.AutopilotEvents.SingleOrDefaultAsync(item => item.ExternalEventId == eventId);
            var completed = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.TaskKey == "finished-task-1");
            var replacement = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.BacklogItemKey == "backlog-receipt-refill-1");
            if (intake?.Status == AutopilotEventStatuses.Processed
                && completed?.Status == AutopilotWaveLaunchStatuses.Completed
                && replacement?.Status == AutopilotWaveLaunchStatuses.Launched)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("A signed Task Detail terminal receipt was not drained into one immediate replacement while a sibling remained active.");
    }

    private static async Task<WaveLaunchProviderRequest> WaitForProviderLaunchOrExplainAsync(
        DispatchTestFactory factory,
        string eventId,
        TimeSpan timeout)
    {
        try
        {
            return await factory.Provider.LaunchReceived.Task.WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var intake = await db.AutopilotEvents.SingleOrDefaultAsync(item => item.ExternalEventId == eventId);
            var tasks = await db.AutopilotWaveLaunchTasks.AsNoTracking()
                .Select(item => $"{item.TaskKey}:{item.Status}:{item.ReasonCode}:{item.LastStatusDetail}:{item.ExternalTaskId}")
                .ToListAsync();
            var batches = await db.AutopilotWaveLaunchBatches.AsNoTracking()
                .Select(item => $"{item.WaveKey}:{item.Status}:{item.ReasonCode}:{item.LastStatusDetail}")
                .ToListAsync();
            throw new InvalidOperationException(
                $"No replacement provider call. event={intake?.Status}/{intake?.Reason}/signature={intake?.SignatureVerified}/replay={intake?.ReplayProtected}/wave={intake?.WaveKey}/task={intake?.TaskId}; tasks=[{string.Join(";", tasks)}]; batches=[{string.Join(";", batches)}]",
                exception);
        }
    }

    private static string CompletionPayload() => JsonSerializer.Serialize(new
    {
        waveKey = WaveKey,
        taskId = "finished-task-1",
        outcome = AutopilotTaskOutcomes.Succeeded,
        branch = "feature/synthetic-wave",
        baseSha = BaseSha,
        candidateSha = CandidateSha,
        attempt = 0,
        evidence = "synthetic deterministic checks passed",
        checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [AutopilotChecks.Build] = true,
            [AutopilotChecks.UnitTests] = true,
            [AutopilotChecks.Typecheck] = true,
            [AutopilotChecks.Lint] = true,
            [AutopilotChecks.BrowserE2E] = true,
            [AutopilotChecks.Migrations] = true,
            [AutopilotChecks.Security] = true,
            [AutopilotChecks.Accounting] = true,
            [AutopilotChecks.IntegrationComplete] = true,
        },
        expectedTaskCount = 1,
        waveComplete = true,
    });

    private static string TerminalReceiptCompletionPayload(string eventId)
    {
        var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            "synthetic-native-task-detail-id",
            "stopped",
            "finish",
            false,
            eventId));
        return JsonSerializer.Serialize(new
        {
            waveKey = WaveKey,
            taskId = "finished-task-1",
            outcome = AutopilotTaskOutcomes.Succeeded,
            branch = "feature/synthetic-wave",
            baseSha = BaseSha,
            candidateSha = CandidateSha,
            attempt = 1,
            evidence = receipt,
            checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            expectedTaskCount = 2,
            waveComplete = false,
        });
    }

    private static async Task<HttpResponseMessage> PostSignedEventAsync(HttpClient client, string payload, string eventId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Autopilot-Source", "completion-bridge");
        content.Headers.Add("X-Autopilot-Event-Id", eventId);
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        content.Headers.Add("X-Autopilot-Timestamp", timestamp);
        content.Headers.Add("X-Autopilot-Signature", $"sha256={ComputeSignature(timestamp, payload)}");
        return await client.PostAsync("/api/autopilot/events", content);
    }

    private static string ComputeSignature(string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
    }

    private sealed class DispatchTestFactory : WebApplicationFactory<Program>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-event-dispatch-{Guid.NewGuid():N}.db");
        public FakeWaveLaunchProvider Provider { get; } = new();

        private string ConnectionString => new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<TaslimDbContext>>();
                services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(ConnectionString));
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWaveLaunchProvider>();
                services.AddSingleton(Provider);
                services.AddScoped<IWaveLaunchProvider>(provider => provider.GetRequiredService<FakeWaveLaunchProvider>());
                services.AddSingleton<IAutopilotGlobalTaskInventoryPageSource, EmptyGlobalTaskInventoryPageSource>();
                services.AddScoped(provider => new AutopilotGlobalTaskSlotAllocator(
                    provider.GetRequiredService<TaslimDbContext>(),
                    "taslim-autopilot-test-scope",
                    provider.GetRequiredService<TimeProvider>()));
                services.AddScoped(provider => new AutopilotGlobalTaskInventoryReader(
                    provider.GetRequiredService<IAutopilotGlobalTaskInventoryPageSource>(),
                    "taslim-autopilot-test-scope"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(databasePath)) File.Delete(databasePath);
            }
        }
    }

    private sealed class EmptyGlobalTaskInventoryPageSource : IAutopilotGlobalTaskInventoryPageSource
    {
        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AutopilotGlobalTaskInventoryPage?>(
                new AutopilotGlobalTaskInventoryPage(scopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), null));
    }

    private sealed class FakeWaveLaunchProvider : IWaveLaunchProvider
    {
        private readonly ConcurrentQueue<WaveLaunchProviderRequest> launches = new();
        private readonly TaskCompletionSource<WaveLaunchProviderRequest> launchReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderKey => "integration-test-fake";
        public bool IsConfigured => true;
        public IReadOnlyList<WaveLaunchProviderRequest> Launches => launches.ToArray();
        public TaskCompletionSource<WaveLaunchProviderRequest> LaunchReceived => launchReceived;

        public Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default)
        {
            launches.Enqueue(request);
            launchReceived.TrySetResult(request);
            return Task.FromResult(WaveLaunchProviderResult.Launched($"fake-external-{request.TaskKey}"));
        }

        public Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default) =>
            Task.FromResult<WaveLaunchTaskState?>(null);
    }
}

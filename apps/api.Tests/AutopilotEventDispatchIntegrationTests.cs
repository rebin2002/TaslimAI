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
            const string nativeTaskId = "ABCDEFGHIJKLMNOPQRSTUV";
            await SeedLaunchedTaskAndActiveSiblingAsync(factory, nativeTaskId);

            using var stop = new CancellationTokenSource();
            var watchdog = CreateWatchdog(factory);
            await watchdog.StartAsync(stop.Token);
            try
            {
                var eventId = $"evt-receipt-refill-{Guid.NewGuid():N}";
                var payload = BridgeV4TerminalReceiptCompletionPayload(eventId, nativeTaskId);
                using (var payloadDocument = JsonDocument.Parse(payload))
                {
                    var root = payloadDocument.RootElement;
                    Assert.Equal(new[] { "evidence", "outcome", "taskId", "waveKey" },
                        root.EnumerateObject().Select(property => property.Name)
                            .OrderBy(name => name, StringComparer.Ordinal).ToArray());
                    Assert.Equal(WaveKey, root.GetProperty("waveKey").GetString());
                    Assert.Equal("finished-task-1", root.GetProperty("taskId").GetString());
                    Assert.Equal(AutopilotTaskOutcomes.Succeeded, root.GetProperty("outcome").GetString());
                    Assert.False(root.TryGetProperty("checks", out _));
                    var receiptJson = root.GetProperty("evidence").GetString();
                    Assert.False(string.IsNullOrWhiteSpace(receiptJson));
                    using var receiptDocument = JsonDocument.Parse(receiptJson!);
                    var receipt = receiptDocument.RootElement;
                    Assert.Equal(nativeTaskId, receipt.GetProperty("task_id").GetString());
                    Assert.Equal(eventId, receipt.GetProperty("event_id").GetString());
                    Assert.Equal("stopped", receipt.GetProperty("agent_status").GetString());
                    Assert.Equal("finish", receipt.GetProperty("stop_reason").GetString());
                    Assert.False(receipt.GetProperty("has_running_background_jobs").GetBoolean());
                }
                using var response = await PostSignedEventAsync(
                    client, payload, eventId, ManusBridgeWaveLaunchProvider.Key);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

                var launched = await WaitForProviderLaunchOrExplainAsync(factory, eventId, TimeSpan.FromSeconds(8));
                Assert.Equal("Approved replacement task", launched.Title);
                await WaitForReceiptRefillAsync(factory, eventId, TimeSpan.FromSeconds(8));

                // Duplicate delivery cannot release another slot or create a second refill.
                using var duplicate = await PostSignedEventWithFenceRetryAsync(
                    client, payload, eventId, ManusBridgeWaveLaunchProvider.Key, TimeSpan.FromSeconds(8));
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                using var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
                Assert.Equal(AutopilotIntakeOutcomes.Duplicate,
                    duplicateBody.RootElement.GetProperty("status").GetString());
                var duplicatePersistedEventId = duplicateBody.RootElement.GetProperty("eventId").GetGuid();

                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                Assert.Equal(1, await db.AutopilotEvents.CountAsync(item =>
                    item.SourceSystem == ManusBridgeWaveLaunchProvider.Key && item.ExternalEventId == eventId));
                var intake = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
                Assert.Equal(AutopilotEventStatuses.Processed, intake.Status);
                Assert.Equal(ManusBridgeWaveLaunchProvider.Key, intake.SourceSystem);
                Assert.Equal(eventId, intake.ExternalEventId);
                Assert.Equal(duplicatePersistedEventId, intake.Id);
                Assert.True(intake.SignatureVerified);
                Assert.True(intake.ReplayProtected);

                var completed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "finished-task-1");
                Assert.Equal(AutopilotWaveLaunchStatuses.Completed, completed.Status);
                Assert.Equal("verified_terminal_evidence", completed.ReasonCode);
                Assert.Equal("verified_completion_refill_created", completed.LastStatusDetail);
                Assert.Equal(nativeTaskId, completed.ExternalTaskId);

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
    public async Task Manus_bridge_receipt_event_id_mismatch_or_absence_is_rejected_before_persistence()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var headerEventId = $"evt-bridge-header-{Guid.NewGuid():N}";
            var mismatchedPayload = TerminalReceiptCompletionPayload($"evt-bridge-body-{Guid.NewGuid():N}");
            using (var mismatch = await PostSignedEventAsync(
                client,
                mismatchedPayload,
                headerEventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            }

            var missingEventId = $"evt-bridge-missing-{Guid.NewGuid():N}";
            var missingPayload = TerminalReceiptCompletionPayload(string.Empty);
            using (var missing = await PostSignedEventAsync(
                client,
                missingPayload,
                missingEventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            }

            var missingPropertyEventId = $"evt-bridge-no-property-{Guid.NewGuid():N}";
            var receiptWithoutEventIdProperty = $$"""{"kind":"{{AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind}}","task_id":"synthetic-native-task-detail-id","agent_status":"stopped","stop_reason":"finish","has_running_background_jobs":false}""";
            var payloadWithoutEventIdProperty = TerminalReceiptCompletionPayloadWithEvidence(receiptWithoutEventIdProperty);
            using (var missingProperty = await PostSignedEventAsync(
                client,
                payloadWithoutEventIdProperty,
                missingPropertyEventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missingProperty.StatusCode);
            }

            var duplicateEventId = $"evt-bridge-duplicate-{Guid.NewGuid():N}";
            var duplicateReceipt = $$"""{"kind":"{{AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind}}","task_id":"synthetic-native-task-detail-id","agent_status":"stopped","stop_reason":"finish","has_running_background_jobs":false,"event_id":"different-id","event_id":"{{duplicateEventId}}"}""";
            var duplicatePayload = TerminalReceiptCompletionPayloadWithEvidence(duplicateReceipt);
            using (var duplicate = await PostSignedEventAsync(
                client,
                duplicatePayload,
                duplicateEventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                Assert.Empty(await db.AutopilotEvents.ToListAsync());
            }

            var matchedEventId = $"evt-bridge-matched-{Guid.NewGuid():N}";
            var matchedPayload = TerminalReceiptCompletionPayload(matchedEventId);
            using (var matched = await PostSignedEventAsync(
                client,
                matchedPayload,
                matchedEventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.Accepted, matched.StatusCode);
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var persisted = await db.AutopilotEvents.SingleAsync();
                Assert.Equal(ManusBridgeWaveLaunchProvider.Key, persisted.SourceSystem);
                Assert.Equal(matchedEventId, persisted.ExternalEventId);
                Assert.Equal(AutopilotEventStatuses.Queued, persisted.Status);
                Assert.True(persisted.SignatureVerified);
                Assert.True(persisted.ReplayProtected);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Manus_bridge_non_success_signal_with_plain_evidence_remains_eligible_for_signed_intake()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            var wakeCalls = 0;
            using var factory = new DispatchTestFactory
            {
                NotifyCommittedEventOverride = () => Interlocked.Increment(ref wakeCalls),
            };
            using var client = factory.CreateClient();
            using (var setupScope = factory.Services.CreateScope())
            {
                var setupDb = setupScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                await setupDb.Database.EnsureCreatedAsync();
            }

            var eventId = $"evt-bridge-failure-{Guid.NewGuid():N}";
            var payload = BridgeFailureCompletionPayload("synthetic native task failure details");

            using (var response = await PostSignedEventAsync(
                client,
                payload,
                eventId,
                ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            }
            Assert.Equal(1, Volatile.Read(ref wakeCalls));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var persisted = await db.AutopilotEvents.SingleAsync();
            Assert.Equal(eventId, persisted.ExternalEventId);
            Assert.Equal(AutopilotEventStatuses.Queued, persisted.Status);
            Assert.True(persisted.SignatureVerified);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Theory]
    [InlineData("unsigned", 401)]
    [InlineData("bad_mac", 401)]
    [InlineData("malformed_timestamp", 401)]
    [InlineData("stale_timestamp", 400)]
    [InlineData("out_of_range_timestamp", 400)]
    public async Task Invalid_signature_or_timestamp_is_rejected_before_fence_acquisition(string mode, int expectedStatus)
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            var locks = new CountingLockService();
            using var factory = new DispatchTestFactory
            {
                LockServiceOverride = _ => locks,
            };
            using var client = factory.CreateClient();
            using (var setup = factory.Services.CreateScope())
                await setup.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreatedAsync();

            var eventId = $"evt-auth-preflight-{mode}-{Guid.NewGuid():N}";
            var payload = CompletionPayload();
            var timestamp = mode switch
            {
                "malformed_timestamp" => "not-a-unix-timestamp",
                "stale_timestamp" => DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString(),
                "out_of_range_timestamp" => "9223372036854775807",
                _ => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            };
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Autopilot-Source", "completion-bridge");
            content.Headers.Add("X-Autopilot-Event-Id", eventId);
            content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
            content.Headers.Add("X-Autopilot-Timestamp", timestamp);
            if (!string.Equals(mode, "unsigned", StringComparison.Ordinal))
            {
                var signature = string.Equals(mode, "bad_mac", StringComparison.Ordinal)
                    ? new string('0', 64)
                    : ComputeSignature(timestamp, payload);
                content.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");
            }

            using (var response = await client.PostAsync("/api/autopilot/events", content))
            {
                Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
            }

            Assert.Equal(0, locks.AcquireCalls);
            using var verify = factory.Services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Empty(await db.AutopilotEvents.ToListAsync());
            Assert.Empty(await db.AutopilotAuditEvents.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Signed_intake_returns_retryable_without_persistence_while_launch_fence_is_held()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory();
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var eventId = $"evt-fence-retry-{Guid.NewGuid():N}";
            var payload = BridgeV4TerminalReceiptCompletionPayload(eventId, "ABCDEFGHIJKLMNOPQRSTUV");
            AutopilotLockHandle heldFence;
            using (var launchScope = factory.Services.CreateScope())
            {
                var locks = launchScope.ServiceProvider.GetRequiredService<IAutopilotLockService>();
                heldFence = await locks.AcquireAsync(
                    AutopilotCompletionLaunchFence.ResourceKey,
                    "synthetic-next-wave-launch");
                Assert.True(heldFence.IsAcquired);

                using var contended = await PostSignedEventAsync(
                    client, payload, eventId, ManusBridgeWaveLaunchProvider.Key);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, contended.StatusCode);
                var error = await contended.Content.ReadAsStringAsync();
                Assert.Contains("AUTOPILOT_EVENT_RETRY", error, StringComparison.Ordinal);

                using var verify = factory.Services.CreateScope();
                var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
                Assert.Empty(await db.AutopilotEvents.ToListAsync());

                Assert.True(await locks.ReleaseAsync(heldFence));
            }

            using (var retried = await PostSignedEventAsync(
                client, payload, eventId, ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
            }

            using (var verify = factory.Services.CreateScope())
            {
                var db = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
                var persisted = await db.AutopilotEvents.SingleAsync();
                Assert.Equal(eventId, persisted.ExternalEventId);
                Assert.Equal(AutopilotEventStatuses.Queued, persisted.Status);
                Assert.True(persisted.SignatureVerified);
                Assert.True(persisted.ReplayProtected);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Fact]
    public async Task Signed_intake_lock_acquire_exception_returns_retryable_without_persistence()
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            using var factory = new DispatchTestFactory
            {
                LockServiceOverride = _ => new AcquireFailureLockService(),
            };
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var eventId = $"evt-fence-acquire-failure-{Guid.NewGuid():N}";
            var payload = BridgeV4TerminalReceiptCompletionPayload(eventId, "ABCDEFGHIJKLMNOPQRSTUV");
            using (var response = await PostSignedEventAsync(
                client, payload, eventId, ManusBridgeWaveLaunchProvider.Key))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("AUTOPILOT_EVENT_RETRY", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using var verify = factory.Services.CreateScope();
            var persistedDb = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.Empty(await persistedDb.AutopilotEvents.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", previousSecret);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_intake_release_uncertainty_returns_retryable_and_same_id_retry_does_not_duplicate(bool throwOnRelease)
    {
        var previousSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", SigningSecret);
        try
        {
            var wakeCalls = 0;
            using var factory = new DispatchTestFactory
            {
                LockServiceOverride = _ => new ReleaseFailureLockService(throwOnRelease),
                NotifyCommittedEventOverride = () => Interlocked.Increment(ref wakeCalls),
            };
            using var client = factory.CreateClient();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var eventId = $"evt-fence-release-uncertain-{Guid.NewGuid():N}";
            var payload = BridgeV4TerminalReceiptCompletionPayload(eventId, "ABCDEFGHIJKLMNOPQRSTUV");
            using (var first = await PostSignedEventAsync(client, payload, eventId, ManusBridgeWaveLaunchProvider.Key))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

            using (var retry = await PostSignedEventAsync(client, payload, eventId, ManusBridgeWaveLaunchProvider.Key))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);

            using var verify = factory.Services.CreateScope();
            var persistedDb = verify.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var saved = await persistedDb.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
            Assert.Equal(AutopilotEventStatuses.Queued, saved.Status);
            Assert.True(saved.SignatureVerified);
            Assert.True(saved.ReplayProtected);
            Assert.Equal(1, await persistedDb.AutopilotEvents.CountAsync(item =>
                item.SourceSystem == ManusBridgeWaveLaunchProvider.Key && item.ExternalEventId == eventId));
            Assert.Equal(0, Volatile.Read(ref wakeCalls));
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

    private static async Task SeedLaunchedTaskAndActiveSiblingAsync(DispatchTestFactory factory, string nativeTaskId)
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
                ExternalTaskId = nativeTaskId, Status = AutopilotWaveLaunchStatuses.Launched,
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
        return TerminalReceiptCompletionPayloadWithEvidence(receipt);
    }

    private static string BridgeV4TerminalReceiptCompletionPayload(string eventId, string nativeTaskId)
    {
        var receipt = JsonSerializer.Serialize(new
        {
            kind = AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            task_id = nativeTaskId,
            agent_status = "stopped",
            stop_reason = "finish",
            has_running_background_jobs = false,
            event_id = eventId,
        });

        // Bridge v4 relays only verified task identity/outcome/evidence. It does
        // not invent CI checks or send the native ID as the logical signal taskId.
        return JsonSerializer.Serialize(new
        {
            waveKey = WaveKey,
            taskId = "finished-task-1",
            outcome = AutopilotTaskOutcomes.Succeeded,
            evidence = receipt,
        });
    }

    private static string TerminalReceiptCompletionPayloadWithEvidence(string receipt) =>
        JsonSerializer.Serialize(new
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

    private static string BridgeFailureCompletionPayload(string evidence) =>
        JsonSerializer.Serialize(new
        {
            waveKey = WaveKey,
            taskId = "finished-task-1",
            outcome = AutopilotTaskOutcomes.Failed,
            failureClass = AutopilotFailureClasses.Infrastructure,
            branch = "feature/synthetic-wave",
            baseSha = BaseSha,
            candidateSha = CandidateSha,
            attempt = 1,
            evidence,
            checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            expectedTaskCount = 2,
            waveComplete = false,
        });

    private static async Task<HttpResponseMessage> PostSignedEventAsync(
        HttpClient client,
        string payload,
        string eventId,
        string source = "completion-bridge")
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.Add("X-Autopilot-Source", source);
        content.Headers.Add("X-Autopilot-Event-Id", eventId);
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
        content.Headers.Add("X-Autopilot-Timestamp", timestamp);
        content.Headers.Add("X-Autopilot-Signature", $"sha256={ComputeSignature(timestamp, payload)}");
        return await client.PostAsync("/api/autopilot/events", content);
    }

    private static async Task<HttpResponseMessage> PostSignedEventWithFenceRetryAsync(
        HttpClient client,
        string payload,
        string eventId,
        string source,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await PostSignedEventAsync(client, payload, eventId, source);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return response;
            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The same signed event ID did not receive an acknowledgement after temporary launch-fence contention.");
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
        public Func<TaslimDbContext, IAutopilotLockService>? LockServiceOverride { get; init; }
        public Action? NotifyCommittedEventOverride { get; init; }

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
                if (LockServiceOverride is not null)
                {
                    services.RemoveAll<IAutopilotLockService>();
                    services.AddScoped(provider => LockServiceOverride(provider.GetRequiredService<TaslimDbContext>()));
                }
                if (NotifyCommittedEventOverride is not null)
                    services.AddSingleton(NotifyCommittedEventOverride);
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

    private sealed class ReleaseFailureLockService(bool throwOnRelease) : IAutopilotLockService
    {
        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey, "synthetic-owner-token", 1, true, heldBy, DateTime.UtcNow.AddMinutes(10)));

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
        {
            if (throwOnRelease) throw new InvalidOperationException("Synthetic release failure.");
            return Task.FromResult(false);
        }
    }

    private sealed class AcquireFailureLockService : IAutopilotLockService
    {
        public Task<AutopilotLockHandle> AcquireAsync(
            string resourceKey,
            string heldBy,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AutopilotLockHandle>(new InvalidOperationException("Synthetic lock-store failure."));

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CountingLockService : IAutopilotLockService
    {
        private int acquireCalls;

        public int AcquireCalls => Volatile.Read(ref acquireCalls);

        public Task<AutopilotLockHandle> AcquireAsync(
            string resourceKey,
            string heldBy,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref acquireCalls);
            return Task.FromResult(new AutopilotLockHandle(
                resourceKey, Guid.NewGuid().ToString("N"), acquireCalls, true, heldBy, DateTime.UtcNow.AddMinutes(10)));
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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

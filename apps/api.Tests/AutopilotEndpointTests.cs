using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// HTTP surface tests: the completion bridge intake is signature-only, and the
/// control console is administrator-only. Nothing here can force a release.
/// </summary>
public sealed class AutopilotEndpointTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AutopilotEndpointTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Intake_endpoint_rejects_unsigned_events()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(Payload("wave-http-unsigned", "task-1"), Encoding.UTF8, "application/json");
        content.Headers.Add("X-Autopilot-Source", "completion-bridge");
        content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);

        var response = await client.PostAsync("/api/autopilot/events", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Intake_endpoint_accepts_a_correctly_signed_event()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            using var client = factory.CreateClient();
            var payload = Payload("wave-http-signed", "task-1");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Autopilot-Source", "completion-bridge");
            content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
            content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
            content.Headers.Add("X-Autopilot-Timestamp", timestamp);
            content.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");

            var response = await client.PostAsync("/api/autopilot/events", content);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, body.GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Intake_endpoint_rejects_oversized_chunked_payload_before_persistence()
    {
        using var client = factory.CreateClient();
        var eventId = $"evt-too-large-{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(new string('x', 20_001));
        using var content = new StreamContent(new ChunkedReadStream(bytes, 64));
        content.Headers.Add("X-Autopilot-Source", "completion-bridge");
        content.Headers.Add("X-Autopilot-Event-Id", eventId);
        content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);

        var response = await client.PostAsync("/api/autopilot/events", content);

        Assert.Equal((HttpStatusCode)StatusCodes.Status413RequestEntityTooLarge, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await db.AutopilotEvents.Where(item => item.ExternalEventId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Signed_webhook_does_not_require_csrf_when_an_auth_cookie_is_present()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            using var client = factory.CreateClient();
            await RegisterAdmin(client);

            var payload = Payload("wave-http-authenticated-signed", "task-1");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            content.Headers.Add("X-Autopilot-Source", "completion-bridge");
            content.Headers.Add("X-Autopilot-Event-Id", $"evt-{Guid.NewGuid():N}");
            content.Headers.Add("X-Autopilot-Event-Type", AutopilotEventTypes.WaveTaskCompleted);
            content.Headers.Add("X-Autopilot-Timestamp", timestamp);
            content.Headers.Add("X-Autopilot-Signature", $"sha256={signature}");

            var response = await client.PostAsync("/api/autopilot/events", content);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Replayed_events_are_rejected_with_a_replay_outcome()
    {
        const string secret = "integration-test-signing-secret";
        Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", secret);
        try
        {
            var payload = Payload("wave-http-replay", "task-1");
            var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds().ToString();
            var signature = Convert.ToHexString(
                new HMACSHA256(Encoding.UTF8.GetBytes(secret))
                    .ComputeHash(Encoding.UTF8.GetBytes($"{staleTimestamp}.{payload}"))).ToLowerInvariant();

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var options = new AutopilotOptions { RequireSignedEvents = true, SignatureToleranceSeconds = 300 };
            options.Normalize();
            var intake = new AutopilotEventIntake(
                db,
                new AutopilotEventAuthenticator(options),
                new EfAutopilotAuditLog(db),
                options,
                TimeProvider.System);

            var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
                signature,
                staleTimestamp,
                payload,
                "completion-bridge",
                $"evt-replay-{Guid.NewGuid():N}",
                AutopilotEventTypes.WaveTaskCompleted));

            Assert.Equal(AutopilotIntakeOutcomes.RejectedReplay, result.Outcome);
            Assert.Empty(await db.AutopilotEvents
                .Where(item => item.SourceSystem == "completion-bridge" && item.ExternalEventId.StartsWith("evt-replay-"))
                .ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", null);
        }
    }

    [Fact]
    public async Task Control_console_requires_administrator_access()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/decisions")).StatusCode);
    }

    [Fact]
    public async Task Control_console_is_idle_and_safe_by_default()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var response = await client.GetAsync("/api/admin/autopilot/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var control = body.GetProperty("control");
        Assert.False(control.GetProperty("featureEnabled").GetBoolean());
        Assert.True(control.GetProperty("dryRun").GetBoolean());
        Assert.False(control.GetProperty("chargingEnabled").GetBoolean());
        Assert.False(control.GetProperty("paidProvidersEnabled").GetBoolean());
        Assert.False(control.GetProperty("killSwitchEngaged").GetBoolean());
        Assert.Equal(20, control.GetProperty("maxConcurrency").GetInt32());
    }

    [Fact]
    public async Task Control_console_exposes_decisions_and_control_semantics()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var decisions = JsonSerializer.Deserialize<JsonElement>(
            await client.GetStringAsync("/api/admin/autopilot/decisions"));
        var humanRequired = decisions.GetProperty("humanRequired").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains(AutopilotHumanDecisions.Pricing, humanRequired);
        Assert.Contains(AutopilotHumanDecisions.EnableCharging, humanRequired);
        Assert.Contains(AutopilotHumanDecisions.ProductionRelease, humanRequired);

        var neverAutomatic = decisions.GetProperty("neverAutomatic").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Contains(AutopilotForbiddenActions.ForcePush, neverAutomatic);
        Assert.Contains(AutopilotForbiddenActions.ResetProductionDatabase, neverAutomatic);

        var reasonRequired = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = true });
        Assert.Equal(HttpStatusCode.BadRequest, reasonRequired.StatusCode);

        var pause = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = true, reason = "integration test pause" });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        var pausedBody = JsonSerializer.Deserialize<JsonElement>(await pause.Content.ReadAsStringAsync());
        Assert.True(pausedBody.GetProperty("paused").GetBoolean());
        Assert.False(pausedBody.GetProperty("killSwitchEngaged").GetBoolean());

        var resume = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = false, reason = "integration test resume" });
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        var reconcile = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/reconcile", new { reason = "integration test reconcile" });
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
    }

    [Fact]
    public async Task Manual_autopilot_mutations_are_attributed_to_the_authenticated_administrator()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var control = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/control", new { paused = true, reason = "attribute control change" });
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        var backlog = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/backlog", new
        {
            itemKey = $"audit-correlation-{Guid.NewGuid():N}",
            title = "Trace the next control-plane mutation",
            kind = AutopilotBacklogItemKinds.Development,
            approved = true,
            reason = "attribute backlog approval",
        });
        Assert.Equal(HttpStatusCode.OK, backlog.StatusCode);
        var reconcile = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/reconcile", new { reason = "attribute manual reconcile" });
        Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var events = await db.AutopilotAuditEvents
            .Where(item => item.ActorUserId == auth.User.Id)
            .ToListAsync();
        Assert.Contains(events, item => item.Action == AutopilotAuditActions.ControlChanged
            && item.Reason == "attribute control change"
            && item.RequestId is not null);
        Assert.Contains(events, item => item.Action == AutopilotAuditActions.BacklogItemChanged
            && item.Reason == "attribute backlog approval"
            && item.RequestId is not null);
        Assert.Contains(events, item => item.Action == AutopilotAuditActions.ManualReconciliationRequested
            && item.Reason == "attribute manual reconcile"
            && item.StatusDetail == "manual_reconcile_requested"
            && item.RequestId is not null);
        Assert.DoesNotContain(events, item =>
            (item.Action == AutopilotAuditActions.ControlChanged || item.Action == AutopilotAuditActions.ManualReconciliationRequested)
            && item.ActorUserId is null);
    }

    [Fact]
    public async Task Pending_completion_events_are_drained_in_bounded_pages_before_next_wave_launch()
    {
        using var host = new EventDrainTestHost();
        await host.SubmitCompletionAsync("wave-order-1");
        await host.SubmitCompletionAsync("wave-order-2");

        var result = await host.ProcessPendingEventsAsync();

        Assert.Equal(2, result.EventsProcessed);
        var eventStatusesAtPoll = Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        var eventStatusesAtLaunch = Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        Assert.Equal(2, eventStatusesAtPoll.Length);
        Assert.Equal(2, eventStatusesAtLaunch.Length);
        Assert.All(eventStatusesAtPoll, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.All(eventStatusesAtLaunch, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
    }

    [Fact]
    public async Task Manual_reconcile_drains_completion_events_before_poll_or_launch()
    {
        using var host = new EventDrainTestHost();
        await host.SubmitCompletionAsync("wave-manual-order-1");
        await host.SubmitCompletionAsync("wave-manual-order-2");

        var result = await host.ReconcileAsync("manual-order-test");

        Assert.Equal(2, result.EventsProcessed);
        var eventStatusesAtPoll = Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        var eventStatusesAtLaunch = Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        Assert.Equal(2, eventStatusesAtPoll.Length);
        Assert.Equal(2, eventStatusesAtLaunch.Length);
        Assert.All(eventStatusesAtPoll, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.All(eventStatusesAtLaunch, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
    }

    [Fact]
    public async Task Backoff_or_other_unprocessed_event_defers_next_wave_launch()
    {
        using var host = new EventDrainTestHost();
        await host.SubmitCompletionAsync("wave-backoff-order");
        await host.SetOnlyEventBackoffAsync(host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        var result = await host.ProcessPendingEventsAsync();

        Assert.Equal("drain_deferred", result.Outcome);
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);
    }

    [Fact]
    public async Task Sustained_multi_producer_arrivals_exhaust_a_whole_cycle_budget_and_backoff_blocks_launch_until_recovery()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-continuous-arrivals";
        await host.AddExistingRunAsync(waveKey, "previous_gate_diagnostic");
        await host.SubmitCompletionAsync(waveKey);
        var barrier = new BlockingFirstAcquireLockService();
        host.Locks = barrier;

        var firstCycleTask = host.ProcessPendingEventsAsync();
        await barrier.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var producerStart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sqliteWriteGate = new SemaphoreSlim(1, 1);
        var dueProducers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await producerStart.Task;
            var eventIds = new List<string>();
            for (var index = 0; index < 2; index++)
            {
                // Producer tasks are concurrent, but serialize writes against
                // this shared-cache SQLite fixture (its per-connection EF
                // functions cannot be registered concurrently while statements
                // are active). Production durability is not simulated here.
                await sqliteWriteGate.WaitAsync();
                try
                {
                    eventIds.Add(await host.SubmitCompletionAsync(waveKey));
                }
                finally
                {
                    sqliteWriteGate.Release();
                }
            }
            return eventIds;
        })).ToArray();
        producerStart.SetResult(true);
        var producedEventIds = await Task.WhenAll(dueProducers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6, producedEventIds.SelectMany(ids => ids).Distinct(StringComparer.Ordinal).Count());

        var delayedEventId = await host.SubmitCompletionAsync(waveKey);
        await host.SetEventBackoffAsync(delayedEventId, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));

        barrier.ReleaseGate.TrySetResult(true);
        var firstCycle = await firstCycleTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("drain_deferred", firstCycle.Outcome);
        Assert.Equal("completion_event_drain_budget_exhausted", firstCycle.Reason);
        Assert.Equal(4, firstCycle.EventsExamined);
        Assert.Equal(4, firstCycle.EventsProcessed);
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            Assert.Equal(4, await db.AutopilotEvents.CountAsync(item => item.Status == AutopilotEventStatuses.Processed));
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.Equal(AutopilotRunStates.AwaitingEvents, run.State);
            Assert.Equal("completion_event_pending_advance:previous_gate_diagnostic", run.LastStatusDetail);
        }

        // A fresh orchestrator instance is deliberate: only the durable marker,
        // not the previous cycle's in-memory run-ID set, can recover advancement.
        host.Locks = new ImmediateTestLockService();
        var secondCycle = await host.ProcessPendingEventsAsync();
        Assert.Equal("drain_deferred", secondCycle.Outcome);
        Assert.Equal("unprocessed_completion_events_remain", secondCycle.Reason);
        Assert.Equal(3, secondCycle.EventsProcessed);
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            Assert.Equal(7, await db.AutopilotEvents.CountAsync(item => item.Status == AutopilotEventStatuses.Processed));
            Assert.Equal(AutopilotEventStatuses.Queued,
                (await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == delayedEventId)).Status);
            Assert.Equal("completion_event_pending_advance:previous_gate_diagnostic",
                (await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey)).LastStatusDetail);
        }

        await host.SetEventBackoffAsync(delayedEventId, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1));
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        var recoveredCycle = await host.ProcessPendingEventsAsync();

        Assert.Equal(1, recoveredCycle.EventsProcessed);
        var pollSnapshot = Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        var launchSnapshot = Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        Assert.Equal(8, pollSnapshot.Length);
        Assert.Equal(8, launchSnapshot.Length);
        Assert.All(pollSnapshot, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.All(launchSnapshot, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        using (var db = host.CreateContext())
        {
            Assert.Equal(8, await db.AutopilotEvents.CountAsync(item => item.Status == AutopilotEventStatuses.Processed));
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.NotEqual(AutopilotRunStates.AwaitingEvents, run.State);
            Assert.False(run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Cancellation_leaves_durable_processing_claim_for_restart_reconciliation_before_launch()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-cancel-restart";
        await host.SubmitCompletionAsync(waveKey);
        await host.SubmitCompletionAsync(waveKey);
        await host.SubmitCompletionAsync(waveKey);

        var barrier = new BlockingFirstAcquireLockService();
        host.Locks = barrier;
        using var cancellation = new CancellationTokenSource();
        var cancelledCycleTask = host.ProcessPendingEventsAsync(cancellation.Token);
        await barrier.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelledCycleTask);

        string claimedEventId;
        using (var db = host.CreateContext())
        {
            var processing = await db.AutopilotEvents.SingleAsync(item => item.Status == AutopilotEventStatuses.Processing);
            claimedEventId = processing.ExternalEventId;
        }
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);

        await host.SetProcessingStartedAtAsync(claimedEventId, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(-10));
        host.Locks = new ImmediateTestLockService();
        var restartCycle = await host.ReconcileAsync("restart_after_cancel");

        Assert.Equal("reconciled", restartCycle.Outcome);
        var pollSnapshot = Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        var launchSnapshot = Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        Assert.Equal(3, pollSnapshot.Length);
        Assert.Equal(3, launchSnapshot.Length);
        Assert.All(pollSnapshot, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.All(launchSnapshot, status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        using (var db = host.CreateContext())
        {
            Assert.Equal(3, await db.AutopilotEvents.CountAsync(item => item.Status == AutopilotEventStatuses.Processed));
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.NotEqual(AutopilotRunStates.AwaitingEvents, run.State);
            Assert.False(run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Cancellation_after_event_commit_recovers_pending_run_advance_after_restart()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-commit-cancel-restart";
        await host.SubmitCompletionAsync(waveKey);
        using var cancellation = new CancellationTokenSource();
        host.Locks = new CancelAfterFirstReleaseLockService(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await host.ProcessPendingEventsAsync(cancellation.Token));

        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotEventStatuses.Processed,
                (await db.AutopilotEvents.SingleAsync()).Status);
            Assert.Equal("completion_event_pending_advance:",
                (await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey)).LastStatusDetail);
        }
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);

        // A new orchestrator instance must discover the persisted marker with an
        // empty event queue and finish advancement before either next-wave pass.
        host.Locks = new ImmediateTestLockService();
        var restartCycle = await host.ProcessPendingEventsAsync();
        Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        Assert.All(host.NextWaves.ReconcileEventStatusSnapshots.Single(),
            status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.All(host.NextWaves.LaunchEventStatusSnapshots.Single(),
            status => Assert.Equal(AutopilotEventStatuses.Processed, status));
        Assert.Equal(0, restartCycle.EventsProcessed);
        using (var db = host.CreateContext())
        {
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.NotEqual(AutopilotRunStates.AwaitingEvents, run.State);
            Assert.False(run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Crash_during_gate_advance_keeps_durable_marker_until_restart_finishes_run()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-gate-crash-restart";
        await host.SubmitCompletionAsync(waveKey);
        var interruption = new InterruptFirstReleaseGateSaveInterceptor(waveKey);
        host.SaveChangesInterceptor = interruption;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await host.ProcessPendingEventsAsync());

        Assert.Equal(1, interruption.Interruptions);
        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotEventStatuses.Processed,
                (await db.AutopilotEvents.SingleAsync()).Status);
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.Equal(AutopilotRunStates.IntegrationGate, run.State);
            Assert.True(run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);

        // Simulate process restart: remove the injected failure, then let a new
        // orchestrator resume the persisted intermediate state and finish gates.
        host.SaveChangesInterceptor = null;
        host.Locks = new ImmediateTestLockService();
        await host.ProcessPendingEventsAsync();

        Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey);
            Assert.False(run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
            Assert.NotEqual(AutopilotRunStates.IntegrationGate, run.State);
        }
    }

    [Fact]
    public async Task Wave_lock_contention_during_advance_keeps_marker_and_blocks_next_wave_until_retry()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-advance-lock-contention";
        await host.SubmitCompletionAsync(waveKey);
        var contentionLock = new DenyRunAdvanceTestLockService();
        host.Locks = contentionLock;

        var deferred = await host.ProcessPendingEventsAsync();

        Assert.Equal("drain_deferred", deferred.Outcome);
        Assert.Equal("completion_run_advancement_deferred", deferred.Reason);
        Assert.Equal(2, contentionLock.AcquireCalls);
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotEventStatuses.Processed,
                (await db.AutopilotEvents.SingleAsync()).Status);
            Assert.True((await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey))
                .LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }

        host.Locks = new ImmediateTestLockService();
        await host.ProcessPendingEventsAsync();
        Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            Assert.False((await db.AutopilotRuns.SingleAsync(item => item.WaveKey == waveKey))
                .LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Direct_event_processing_does_not_bypass_future_backoff()
    {
        using var host = new EventDrainTestHost();
        var waveKey = "wave-direct-backoff";
        var externalEventId = await host.SubmitCompletionAsync(waveKey);
        await host.SetEventBackoffAsync(externalEventId, host.Clock.GetUtcNow().UtcDateTime.AddMinutes(5));
        Guid eventId;
        using (var db = host.CreateContext())
            eventId = (await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == externalEventId)).Id;

        var deferred = await host.ProcessEventAsync(eventId);
        Assert.Equal("drain_deferred", deferred.Outcome);
        Assert.Equal("completion_event_backoff_pending", deferred.Reason);
        Assert.Empty(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Empty(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync(item => item.Id == eventId);
            Assert.Equal(AutopilotEventStatuses.Queued, intakeEvent.Status);
            Assert.Equal(0, intakeEvent.Attempt);
        }

        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await host.ProcessEventAsync(eventId);
        Assert.Single(host.NextWaves.ReconcileEventStatusSnapshots);
        Assert.Single(host.NextWaves.LaunchEventStatusSnapshots);
        using (var db = host.CreateContext())
            Assert.Equal(AutopilotEventStatuses.Processed,
                (await db.AutopilotEvents.SingleAsync(item => item.Id == eventId)).Status);
    }

    [Fact]
    public async Task Watchdog_immediately_redrives_drain_and_run_advance_budget_deferrals()
    {
        var orchestrator = new BudgetDeferredTestOrchestrator();
        var services = new ServiceCollection();
        services.AddSingleton<IAutopilotOrchestrator>(orchestrator);
        using var provider = services.BuildServiceProvider();
        var watchdog = new AutopilotWatchdogService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new AutopilotOptions { Enabled = true, MaxConcurrency = 1 }),
            NullLogger<AutopilotWatchdogService>.Instance);

        await watchdog.RunReconciliationAsync("budget-redrive-test", CancellationToken.None);

        Assert.Equal(3, orchestrator.ProcessPendingEventsCalls);
        Assert.Equal(1, orchestrator.ReconcileCalls);
    }

    private static string Payload(string waveKey, string taskId) => JsonSerializer.Serialize(new
    {
        waveKey,
        taskId,
        outcome = AutopilotTaskOutcomes.Succeeded,
        branch = "feature/taslim-autopilot-controller",
        baseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a",
        candidateSha = "1111111111111111111111111111111111111111",
        checks = new Dictionary<string, bool>
        {
            [AutopilotChecks.Build] = true,
            [AutopilotChecks.UnitTests] = true,
            [AutopilotChecks.Typecheck] = true,
            [AutopilotChecks.BrowserE2E] = true,
            [AutopilotChecks.Migrations] = true,
            [AutopilotChecks.Security] = true,
            [AutopilotChecks.Accounting] = true,
            [AutopilotChecks.IntegrationComplete] = true,
        },
    });

    private async Task<AuthResponse> RegisterAdmin(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Autopilot Administrator",
            email = $"autopilot-admin-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.AddToRoleAsync(user!, AdminPolicies.Role)).Succeeded);
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private sealed class EventDrainTestHost : IDisposable
    {
        private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
        private const string CandidateSha = "1111111111111111111111111111111111111111";
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private readonly AutopilotOptions options = new()
        {
            Enabled = true,
            DryRun = true,
            SimulationMode = true,
            RequireSignedEvents = false,
            MaxConcurrency = 1,
            EventStaleAfterMinutes = 5,
        };

        public EventDrainTestHost()
        {
            connection.Open();
            options.Normalize();
            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public RecordingNextWaveService NextWaves { get; } = new();

        public ManualTestTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public IAutopilotLockService Locks { get; set; } = new ImmediateTestLockService();

        public IInterceptor? SaveChangesInterceptor { get; set; }

        public TaslimDbContext CreateContext() =>
            CreateContextOptions();

        private TaslimDbContext CreateContextOptions()
        {
            var optionsBuilder = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection);
            if (SaveChangesInterceptor is not null)
                optionsBuilder.AddInterceptors(SaveChangesInterceptor);
            return new TaslimDbContext(optionsBuilder.Options);
        }

        public async Task AddExistingRunAsync(string waveKey, string lastStatusDetail)
        {
            using var db = CreateContext();
            var now = Clock.GetUtcNow().UtcDateTime;
            db.AutopilotRuns.Add(new AutopilotRun
            {
                Id = Guid.NewGuid(),
                WaveKey = waveKey,
                RunKey = $"autopilot:{waveKey}",
                State = AutopilotRunStates.AwaitingEvents,
                BaseSha = BaseSha,
                CandidateSha = CandidateSha,
                Branch = "feature/autopilot-event-drain-test",
                LastStatusDetail = lastStatusDetail,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        public async Task<string> SubmitCompletionAsync(string waveKey)
        {
            using var db = CreateContext();
            var signal = new AutopilotCompletionSignal
            {
                WaveKey = waveKey,
                TaskId = $"task-{waveKey}",
                Outcome = AutopilotTaskOutcomes.Succeeded,
                Branch = "feature/autopilot-event-drain-test",
                BaseSha = BaseSha,
                CandidateSha = CandidateSha,
                ExpectedTaskCount = 1,
                Checks = PassingChecks(),
            };
            var intake = new AutopilotEventIntake(
                db,
                new AutopilotEventAuthenticator(options),
                new EfAutopilotAuditLog(db),
                options,
                Clock);
            var externalEventId = $"evt-{Guid.NewGuid():N}";
            var result = await intake.SubmitAsync(new AutopilotIntakeRequest(
                null,
                null,
                JsonSerializer.Serialize(signal),
                "event-drain-test",
                externalEventId,
                AutopilotEventTypes.WaveTaskCompleted));

            Assert.Equal(AutopilotIntakeOutcomes.Accepted, result.Outcome);
            return externalEventId;
        }

        public async Task SetOnlyEventBackoffAsync(DateTime nextAttemptAt)
        {
            using var db = CreateContext();
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            intakeEvent.Status = AutopilotEventStatuses.Queued;
            intakeEvent.NextAttemptAt = nextAttemptAt;
            await db.SaveChangesAsync();
        }

        public async Task SetEventBackoffAsync(string externalEventId, DateTime nextAttemptAt)
        {
            using var db = CreateContext();
            var intakeEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == externalEventId);
            intakeEvent.Status = AutopilotEventStatuses.Queued;
            intakeEvent.NextAttemptAt = nextAttemptAt;
            await db.SaveChangesAsync();
        }

        public async Task SetProcessingStartedAtAsync(string externalEventId, DateTime processingStartedAt)
        {
            using var db = CreateContext();
            var intakeEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == externalEventId);
            intakeEvent.Status = AutopilotEventStatuses.Processing;
            intakeEvent.ProcessingStartedAt = processingStartedAt;
            intakeEvent.NextAttemptAt = null;
            await db.SaveChangesAsync();
        }

        public async Task<AutopilotCycleResult> ProcessPendingEventsAsync(CancellationToken cancellationToken = default)
        {
            using var db = CreateContext();
            NextWaves.Attach(db);
            return await CreateOrchestrator(db).ProcessPendingEventsAsync(cancellationToken);
        }

        public async Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            using var db = CreateContext();
            NextWaves.Attach(db);
            return await CreateOrchestrator(db).ProcessEventAsync(eventId, cancellationToken);
        }

        public async Task<AutopilotCycleResult> ReconcileAsync(string reason, CancellationToken cancellationToken = default)
        {
            using var db = CreateContext();
            NextWaves.Attach(db);
            return await CreateOrchestrator(db).ReconcileAsync(reason, cancellationToken);
        }

        private AutopilotOrchestrator CreateOrchestrator(TaslimDbContext db) =>
            new(
                db,
                Locks,
                NextWaves,
                options,
                Clock,
                NullLogger<AutopilotOrchestrator>.Instance);

        private static Dictionary<string, bool> PassingChecks() => new(StringComparer.OrdinalIgnoreCase)
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
        };

        public void Dispose()
        {
            connection.Close();
            connection.Dispose();
        }
    }

    private sealed class ManualTestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset now = initial;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now = now.Add(amount);
    }

    private sealed class ImmediateTestLockService : IAutopilotLockService
    {
        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey,
                Guid.NewGuid().ToString("N"),
                1,
                true,
                heldBy,
                DateTime.UtcNow.AddMinutes(5)));

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(handle.IsAcquired);
    }

    private sealed class DenyRunAdvanceTestLockService : IAutopilotLockService
    {
        private int acquireCalls;

        public int AcquireCalls => Volatile.Read(ref acquireCalls);

        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref acquireCalls) == 1)
                return Task.FromResult(new AutopilotLockHandle(
                    resourceKey,
                    Guid.NewGuid().ToString("N"),
                    1,
                    true,
                    heldBy,
                    DateTime.UtcNow.AddMinutes(1)));

            return Task.FromResult(AutopilotLockHandle.NotAcquired(
                resourceKey,
                "another-run-advancer",
                DateTime.UtcNow.AddMinutes(1)));
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(handle.IsAcquired);
    }

    private sealed class BlockingFirstAcquireLockService : IAutopilotLockService
    {
        private readonly ImmediateTestLockService inner = new();
        private int acquireCount;

        public TaskCompletionSource<bool> AcquireEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref acquireCount) == 1)
            {
                AcquireEntered.TrySetResult(true);
                await ReleaseGate.Task.WaitAsync(cancellationToken);
            }

            return await inner.AcquireAsync(resourceKey, heldBy, cancellationToken);
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(handle, cancellationToken);
    }

    private sealed class CancelAfterFirstReleaseLockService(CancellationTokenSource cancellation) : IAutopilotLockService
    {
        private readonly ImmediateTestLockService inner = new();
        private int releaseCount;

        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            inner.AcquireAsync(resourceKey, heldBy, cancellationToken);

        public async Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
        {
            var released = await inner.ReleaseAsync(handle, cancellationToken);
            if (Interlocked.Increment(ref releaseCount) == 1)
                cancellation.Cancel();
            return released;
        }
    }

    private sealed class InterruptFirstReleaseGateSaveInterceptor(string waveKey) : SaveChangesInterceptor
    {
        public int Interruptions { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var run = eventData.Context?.ChangeTracker.Entries<AutopilotRun>()
                .Select(entry => entry.Entity)
                .SingleOrDefault(item => item.WaveKey == waveKey);
            if (Interruptions == 0
                && run?.State == AutopilotRunStates.ReleaseGate
                && run.LastStatusDetail?.StartsWith("completion_event_pending_advance:", StringComparison.Ordinal) == true)
            {
                Interruptions++;
                throw new OperationCanceledException("synthetic interruption during release-gate advancement", cancellationToken);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BudgetDeferredTestOrchestrator : IAutopilotOrchestrator
    {
        public int ProcessPendingEventsCalls { get; private set; }

        public int ReconcileCalls { get; private set; }

        public Task<AutopilotCycleResult> ProcessPendingEventsAsync(CancellationToken cancellationToken = default)
        {
            ProcessPendingEventsCalls++;
            var deferredReason = ProcessPendingEventsCalls switch
            {
                1 => "completion_event_drain_budget_exhausted",
                2 => "completion_run_advancement_deferred",
                _ => null,
            };
            return Task.FromResult(new AutopilotCycleResult(
                Enabled: true,
                DryRun: true,
                Outcome: deferredReason is null ? "idle" : "drain_deferred",
                Reason: deferredReason,
                EventsExamined: 0,
                EventsProcessed: 0,
                EventsSkipped: 0,
                PlannedActions: [],
                AuditedEventIds: []));
        }

        public Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AutopilotCycleResult> ReconcileAsync(string reason, CancellationToken cancellationToken = default)
        {
            ReconcileCalls++;
            return Task.FromResult(new AutopilotCycleResult(true, true, "no_reconciliation_needed", reason, 0, 0, 0, [], []));
        }

        public Task<AutopilotControlSnapshot> GetControlAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AutopilotControlSnapshot> SetControlAsync(
            bool? paused,
            bool? killSwitch,
            Guid? actorUserId,
            string reason,
            string? requestId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingNextWaveService : IAutopilotNextWaveService
    {
        private TaslimDbContext? db;

        public List<string[]> LaunchEventStatusSnapshots { get; } = [];

        public List<string[]> ReconcileEventStatusSnapshots { get; } = [];

        public void Attach(TaslimDbContext context) => db = context;

        public async Task<AutopilotNextWaveCycleResult> TryLaunchNextWavesAsync(CancellationToken cancellationToken = default)
        {
            LaunchEventStatusSnapshots.Add(await ReadStatusesAsync(cancellationToken));
            return AutopilotNextWaveCycleResult.Idle("test", "provider_not_configured");
        }

        public async Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync(CancellationToken cancellationToken = default)
        {
            ReconcileEventStatusSnapshots.Add(await ReadStatusesAsync(cancellationToken));
            return AutopilotNextWaveCycleResult.Idle("test", "provider_not_configured");
        }

        private Task<string[]> ReadStatusesAsync(CancellationToken cancellationToken) =>
            db!.AutopilotEvents.AsNoTracking()
                .OrderBy(item => item.ReceivedAt)
                .ThenBy(item => item.Id)
                .Select(item => item.Status)
                .ToArrayAsync(cancellationToken);
    }

    private sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, chunkSize), data.Length - position);
            if (read <= 0) return 0;
            data.AsSpan(position, read).CopyTo(buffer.AsSpan(offset, read));
            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(buffer.Length, chunkSize), data.Length - position);
            if (read <= 0) return ValueTask.FromResult(0);
            data.AsMemory(position, read).CopyTo(buffer);
            position += read;
            return ValueTask.FromResult(read);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

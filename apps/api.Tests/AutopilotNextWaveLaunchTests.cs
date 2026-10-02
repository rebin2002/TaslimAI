using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// End-to-end tests for the Autopilot follow-on ("next wave") execution loop.
/// Every test drives the real planner, launch provider boundary, persistence,
/// state machine, intake, and reconciliation code over a shared SQLite
/// connection, so duplicate, dry-run, unavailable-bridge, restart, backlog,
/// kill-switch, bound, and secret-exposure behaviour is exercised for real.
/// </summary>
public sealed class AutopilotNextWaveLaunchTests
{
    private const string Branch = "feature/taslim-autopilot-next-wave";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task Live_next_wave_launch_creates_the_next_wave_tasks_and_keeps_release_human()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.AllowAutomaticRelease = false;
        });

        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        await host.AddBacklogAsync("bl-2", "Add retry telemetry to the worker");
        await host.GreenRunAsync("wave-live");

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == "wave-live");
        Assert.Equal(AutopilotRunStates.HandoffPendingHuman, run.State);
        Assert.Equal(AutopilotHumanDecisions.ProductionRelease, run.HumanDecisionRequired);

        var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
        Assert.Equal("wave-live-next", batch.WaveKey);
        Assert.Equal("wave-live", batch.SourceWaveKey);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched, batch.Status);
        Assert.Equal(2, batch.TaskCount);
        Assert.Equal(2, batch.LaunchedTaskCount);
        Assert.False(batch.DryRun);
        Assert.Equal(CandidateSha, batch.BaseSha);
        Assert.Equal(Branch, batch.Branch);
        Assert.NotEqual(string.Empty, batch.PlanFingerprint);

        var tasks = await db.AutopilotWaveLaunchTasks.OrderBy(item => item.TaskKey).ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status));
        Assert.All(tasks, task => Assert.False(string.IsNullOrWhiteSpace(task.ExternalTaskId)));
        Assert.All(tasks, task => Assert.Equal(1, task.LaunchAttempt));
        Assert.All(tasks, task => Assert.NotNull(task.NextReconcileAt));

        Assert.Equal(2, host.Provider.LaunchCalls);
        Assert.Equal(2, host.Provider.Requests.Select(request => request.IdempotencyKey).Distinct().Count());
        Assert.All(host.Provider.Requests, request => Assert.Equal(CandidateSha, request.BaseSha));

        // The approved work is consumed exactly once, and the release stays human.
        var backlog = await db.AutopilotBacklogItems.ToListAsync();
        Assert.All(backlog, item => Assert.Equal("wave-live-next", item.ConsumedByWaveKey));
        Assert.False(run.ReleaseGatePassed && run.State == AutopilotRunStates.Completed);

        var audit = await db.AutopilotAuditEvents.ToListAsync();
        Assert.Equal(2, audit.Count(item => item.Action == AutopilotAuditActions.NextWaveLaunched));
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.NextWavePlanSelected);
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.ProductionRelease);
    }

    [Fact]
    public async Task Dry_run_simulation_never_performs_an_external_call()
    {
        using var host = new Host(options => options.AllowNextWaveLaunch = true);
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");

        await host.GreenRunAsync("wave-dry");

        using var db = host.CreateContext();
        var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Simulated, batch.Status);
        Assert.True(batch.DryRun);

        var tasks = await db.AutopilotWaveLaunchTasks.ToListAsync();
        Assert.Single(tasks);
        Assert.Equal(AutopilotWaveLaunchStatuses.Simulated, tasks[0].Status);
        Assert.Null(tasks[0].ExternalTaskId);
        Assert.Equal(0, tasks[0].LaunchAttempt);

        // No external call was made and no work was consumed.
        Assert.Equal(0, host.Provider.LaunchCalls);
        Assert.Equal(0, host.Provider.StateCalls);
        Assert.All(await db.AutopilotBacklogItems.ToListAsync(), item => Assert.Null(item.ConsumedByWaveKey));
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
            item => item.Action == AutopilotAuditActions.NextWaveLaunchSimulated && item.DryRun);
    }

    [Fact]
    public async Task Unavailable_bridge_is_retried_within_bounds_then_requires_a_human()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchAttempts = 3;
        });
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Unavailable("bridge_unreachable"));
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Unavailable("bridge_timeout"));
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Unavailable("bridge_status_503"));

        await host.GreenRunAsync("wave-flaky");
        Assert.Equal(1, host.Provider.LaunchCalls);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal(1, task.LaunchAttempt);
            Assert.NotNull(task.NextReconcileAt);
        }

        host.Time.Advance(TimeSpan.FromHours(1));
        await host.LaunchAsync();
        Assert.Equal(2, host.Provider.LaunchCalls);

        host.Time.Advance(TimeSpan.FromHours(1));
        await host.LaunchAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Failed, task.Status);
            Assert.Equal(3, task.LaunchAttempt);
            Assert.Equal(AutopilotWaveLaunchStatuses.Failed, (await db.AutopilotWaveLaunchBatches.SingleAsync()).Status);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.NextWaveLaunchFailed && item.Reason == "launch_attempts_exhausted");
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.HumanDecisionRequired
                    && item.Reason == AutopilotHumanDecisions.RepairEscalation);
        }

        // The bound is terminal: no further attempts are made.
        host.Time.Advance(TimeSpan.FromHours(5));
        await host.LaunchAsync();
        await host.LaunchAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Restart_recovery_retries_a_planned_launch_and_recovers_a_stale_claim()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Unavailable("bridge_unreachable"));

        await host.GreenRunAsync("wave-restart");

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal(1, task.LaunchAttempt);
        }

        // A restarted process re-reads the durable plan and completes the launch.
        host.Time.Advance(TimeSpan.FromHours(1));
        var recovered = await host.LaunchAsync();
        Assert.Equal(1, recovered.TasksLaunched);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status);
            Assert.Equal(2, task.LaunchAttempt);
            Assert.False(string.IsNullOrWhiteSpace(task.ExternalTaskId));

            // Simulate a worker that crashed while an attempt was in flight.
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.LaunchedAt = null;
            task.ExternalTaskId = null;
            task.UpdatedAt = host.Time.GetUtcNow().UtcDateTime.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var reconcile = await host.ReconcileLaunchesAsync();
        Assert.Equal(1, reconcile.BatchesExamined);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal("stale_launch_claim", task.ReasonCode);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.NextWaveReconciled && item.Reason == "stale_launch_claim");
        }
    }

    [Fact]
    public async Task Duplicate_processing_never_creates_a_duplicate_next_wave_task()
    {
        using var host = new Host(options => options.AllowNextWaveLaunch = true);
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");

        await host.GreenRunAsync("wave-dupe");
        await host.LaunchAsync();
        await host.LaunchAsync();
        await host.ReconcileLaunchesAsync();

        using var db = host.CreateContext();
        Assert.Single(await db.AutopilotWaveLaunchBatches.ToListAsync());
        Assert.Single(await db.AutopilotWaveLaunchTasks.ToListAsync());
        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Missing_approved_backlog_stops_with_a_human_decision_and_resumes_when_supplied()
    {
        using var host = new Host(options => options.AllowNextWaveLaunch = true);

        // An unapproved item and an approved non-development item are never selected.
        await host.AddBacklogAsync("bl-pending", "Unapproved work", approved: false);
        await host.AddBacklogAsync("bl-pricing", "Change the pricing model", approved: true, kind: AutopilotBacklogItemKinds.NonDevelopment);

        await host.GreenRunAsync("wave-nobacklog");

        using (var db = host.CreateContext())
        {
            var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Blocked, batch.Status);
            Assert.Equal("backlog_required", batch.ReasonCode);
            Assert.Equal(AutopilotHumanDecisions.BacklogRequired, batch.HumanDecisionRequired);
            Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.HumanDecisionRequired
                    && item.Reason == AutopilotHumanDecisions.BacklogRequired);
            Assert.Equal(0, host.Provider.LaunchCalls);
        }

        // Once a human approves ordinary development work the loop resumes by itself.
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        await host.LaunchAsync();

        using (var db = host.CreateContext())
        {
            var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Simulated, batch.Status);
            Assert.Null(batch.HumanDecisionRequired);
            Assert.Single(await db.AutopilotWaveLaunchTasks.ToListAsync());
        }
    }

    [Fact]
    public async Task Kill_switch_prevents_future_launches()
    {
        using var host = new Host(options => options.AllowNextWaveLaunch = false);
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        await host.GreenRunAsync("wave-kill");

        // The operator enables next-wave launch, then engages the kill switch.
        host.Options.AllowNextWaveLaunch = true;
        await host.SetKillSwitchAsync(true);

        var result = await host.LaunchAsync();
        Assert.Equal("kill_switch", result.Outcome);

        using var db = host.CreateContext();
        Assert.Empty(await db.AutopilotWaveLaunchBatches.ToListAsync());
        Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
        Assert.Equal(0, host.Provider.LaunchCalls);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
            item => item.Action == AutopilotAuditActions.NextWaveLaunchBlocked
                && item.Reason == "kill_switch_engaged"
                && item.StatusDetail == "kill_switch_prevents_future_launches");
    }

    [Fact]
    public async Task Wave_task_count_is_capped_at_twenty()
    {
        using var host = new Host(options => options.AllowNextWaveLaunch = true);
        for (var index = 0; index < 25; index++)
            await host.AddBacklogAsync($"bl-{index:D2}", $"Backlog item {index:D2}", priority: index);

        await host.GreenRunAsync("wave-cap");

        using var db = host.CreateContext();
        var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
        Assert.Equal(20, batch.TaskCount);
        Assert.Equal(20, await db.AutopilotWaveLaunchTasks.CountAsync());

        var options = new AutopilotOptions { MaxTasksPerWave = 500, MaxLaunchAttempts = 99 };
        options.Normalize();
        Assert.Equal(20, options.MaxTasksPerWave);
        Assert.Equal(3, options.MaxLaunchAttempts);
    }

    [Fact]
    public async Task Product_and_release_decisions_block_the_next_wave_until_explicitly_allowed()
    {
        // A pending product-direction decision always blocks the next wave.
        using (var host = new Host(options => options.AllowNextWaveLaunch = true))
        {
            await host.AddBacklogAsync("bl-1", "Add workspace audit export");
            await host.GreenRunAsync("wave-product");

            using (var db = host.CreateContext())
            {
                var run = await db.AutopilotRuns.SingleAsync();
                run.HumanDecisionRequired = AutopilotHumanDecisions.ProductDirection;
                await db.SaveChangesAsync();
            }

            await host.LaunchAsync();

            using (var db = host.CreateContext())
            {
                var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
                Assert.Equal(AutopilotWaveLaunchStatuses.Blocked, batch.Status);
                Assert.Equal("human_decision_pending:product_direction", batch.ReasonCode);
                Assert.Equal(0, host.Provider.LaunchCalls);
            }
        }

        // A pending production release blocks the next wave unless the operator
        // explicitly allows development to continue while the release waits.
        using (var host = new Host(options =>
               {
                   options.DryRun = false;
                   options.AllowNextWaveLaunch = true;
                   options.AllowNextWaveLaunchWhenReleasePending = false;
               }))
        {
            await host.AddBacklogAsync("bl-1", "Add workspace audit export");
            await host.GreenRunAsync("wave-release");

            using (var db = host.CreateContext())
            {
                var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
                Assert.Equal(AutopilotWaveLaunchStatuses.Blocked, batch.Status);
                Assert.Equal("release_pending_human", batch.ReasonCode);
                Assert.Equal(AutopilotHumanDecisions.ProductionRelease, batch.HumanDecisionRequired);
            }

            Assert.Equal(0, host.Provider.LaunchCalls);

            host.Options.AllowNextWaveLaunchWhenReleasePending = true;
            await host.LaunchAsync();

            using (var db = host.CreateContext())
            {
                var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, batch.Status);
            }

            Assert.Equal(1, host.Provider.LaunchCalls);
        }
    }

    [Fact]
    public async Task Missing_provider_configuration_blocks_live_launch_without_any_call()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        host.Provider.IsConfigured = false;
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");

        await host.GreenRunAsync("wave-unconfigured");

        using var db = host.CreateContext();
        var batch = await db.AutopilotWaveLaunchBatches.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Blocked, batch.Status);
        Assert.Equal("provider_not_configured", batch.ReasonCode);
        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Polling_fallback_detects_terminal_state_and_can_never_satisfy_a_gate()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        await host.GreenRunAsync("wave-poll");

        string externalTaskId;
        using (var db = host.CreateContext())
        {
            externalTaskId = (await db.AutopilotWaveLaunchTasks.SingleAsync()).ExternalTaskId!;
            Assert.False(string.IsNullOrWhiteSpace(externalTaskId));
        }

        // No signed completion event ever arrives; the bounded polling fallback
        // observes a terminal success after the fallback window.
        host.Provider.StateResolver = _ => new WaveLaunchTaskState(
            WaveLaunchTaskStates.Succeeded, IsTerminal: true, IsSuccess: true, ExternalRef: "ref-1", StatusDetail: "bridge_status=completed");
        host.Time.Advance(TimeSpan.FromMinutes(45));

        var reconcile = await host.ReconcileLaunchesAsync();
        Assert.Equal(1, reconcile.TasksReconciled);

        using (var db = host.CreateContext())
        {
            var launchTask = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Completed, launchTask.Status);
            Assert.Equal("polled_succeeded", launchTask.ReasonCode);

            var derived = await db.AutopilotEvents.SingleAsync(item => item.SourceSystem == AutopilotInternalSignalSources.Polling);
            Assert.False(derived.SignatureVerified);
            Assert.False(derived.ReplayProtected);
            Assert.Equal(AutopilotEventStatuses.Queued, derived.Status);
            Assert.Equal("wave-poll-next", derived.WaveKey);
        }

        // The derived completion carries no gate evidence, so the next wave can
        // only ever escalate to a human; polling can never fabricate a green gate.
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var nextRun = await db.AutopilotRuns.SingleAsync(item => item.WaveKey == "wave-poll-next");
            Assert.False(nextRun.IntegrationGatePassed);
            Assert.False(nextRun.ReleaseGatePassed);
            Assert.Equal(AutopilotRunStates.IntegrationFailed, nextRun.State);
            var nextTask = await db.AutopilotWaveTasks.SingleAsync(item => item.RunId == nextRun.Id);
            Assert.Null(nextTask.ChecksJson);
        }
    }

    [Fact]
    public async Task Bridge_provider_is_fail_closed_and_never_exposes_its_credential()
    {
        const string sentinel = "sentinel-bridge-credential-value";
        Environment.SetEnvironmentVariable("TASLIM_BRIDGE_TOKEN", sentinel);
        try
        {
            var handler = new StubBridgeHandler();
            var options = new AutopilotOptions
            {
                Enabled = true,
                DryRun = false,
                AllowNextWaveLaunch = true,
                BridgeBaseUrl = "https://bridge.example.test",
            };
            options.Normalize();
            var provider = new ManusBridgeWaveLaunchProvider(new HttpClient(handler), options);
            Assert.True(provider.IsConfigured);

            var request = new WaveLaunchProviderRequest(
                "wave-x-next", "bl-1", "Add workspace audit export", "bounded instructions", CandidateSha, Branch,
                AutopilotNextWavePolicy.LaunchIdempotencyKey("wave-x-next", "bl-1"));

            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"task\":{\"id\":\"bridge-task-77\",\"status\":\"queued\"}}"),
            };
            var launched = await provider.LaunchTaskAsync(request);
            Assert.True(launched.IsLaunched);
            Assert.Equal("bridge-task-77", launched.ExternalTaskId);

            // The credential is only ever sent as an Authorization header.
            var recorded = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, recorded.Method);
            Assert.Equal("https://bridge.example.test/v1/tasks", recorded.Uri);
            Assert.Equal($"Bearer {sentinel}", recorded.Authorization);
            Assert.Equal(request.IdempotencyKey, recorded.IdempotencyKey);
            Assert.DoesNotContain(sentinel, recorded.Body);

            // A transient failure is reported as unavailable, never as success.
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var transient = await provider.LaunchTaskAsync(request);
            Assert.Equal(WaveLaunchProviderOutcomes.Unavailable, transient.Outcome);
            Assert.False(transient.IsLaunched);

            // A deterministic refusal is never retried automatically.
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
            var rejected = await provider.LaunchTaskAsync(request);
            Assert.Equal(WaveLaunchProviderOutcomes.Rejected, rejected.Outcome);

            // An ambiguous body is never interpreted as a launch.
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            var ambiguous = await provider.LaunchTaskAsync(request);
            Assert.Equal(WaveLaunchProviderOutcomes.Unavailable, ambiguous.Outcome);
            Assert.Equal("bridge_task_identity_missing", ambiguous.ReasonCode);

            // Status polling is conservative: unknown status is not terminal.
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"weird\"}") };
            var unknown = await provider.GetTaskStateAsync("bridge-task-77");
            Assert.NotNull(unknown);
            Assert.False(unknown!.IsTerminal);
            Assert.False(unknown.IsSuccess);

            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"completed\"}") };
            var completed = await provider.GetTaskStateAsync("bridge-task-77");
            Assert.NotNull(completed);
            Assert.True(completed!.IsTerminal);
            Assert.True(completed.IsSuccess);

            Assert.DoesNotContain(sentinel, provider.ProviderKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASLIM_BRIDGE_TOKEN", null);
        }
    }

    [Fact]
    public async Task No_secret_material_is_persisted_in_launch_state_or_audit()
    {
        const string sentinel = "sentinel-bridge-credential-value";
        Environment.SetEnvironmentVariable("TASLIM_BRIDGE_TOKEN", sentinel);
        try
        {
            using var host = new Host(options =>
            {
                options.DryRun = false;
                options.AllowNextWaveLaunch = true;
                options.AllowNextWaveLaunchWhenReleasePending = true;
                options.BridgeBaseUrl = "https://bridge.example.test";
            });
            await host.AddBacklogAsync("bl-1", "Add workspace audit export");
            await host.GreenRunAsync("wave-secret");

            using var db = host.CreateContext();
            var surfaces = new List<string>();
            surfaces.AddRange(await db.AutopilotAuditEvents.Select(item =>
                $"{item.Action}|{item.Outcome}|{item.Reason}|{item.StatusDetail}|{item.TaskId}|{item.RequestId}").ToListAsync());
            surfaces.AddRange(await db.AutopilotWaveLaunchTasks.Select(item =>
                $"{item.TaskKey}|{item.Title}|{item.ReasonCode}|{item.LastStatusDetail}|{item.ExternalTaskId}|{item.ExternalRef}").ToListAsync());
            surfaces.AddRange(await db.AutopilotWaveLaunchBatches.Select(item =>
                $"{item.WaveKey}|{item.ReasonCode}|{item.LastStatusDetail}|{item.PlanFingerprint}").ToListAsync());
            surfaces.AddRange(await db.AutopilotEvents.Select(item => $"{item.SignalJson}|{item.Reason}").ToListAsync());

            Assert.All(surfaces, value => Assert.DoesNotContain(sentinel, value));
            Assert.All(surfaces, value => Assert.DoesNotContain("Bearer", value));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TASLIM_BRIDGE_TOKEN", null);
        }
    }

    [Fact]
    public void Next_wave_policy_is_deterministic_and_never_releases_or_pushes()
    {
        Assert.Equal("wave-1-next", AutopilotNextWavePolicy.NextWaveKey("wave-1"));
        Assert.Equal(AutopilotNextWavePolicy.NextWaveKey("wave-1"), AutopilotNextWavePolicy.NextWaveKey(" wave-1 "));
        Assert.True(AutopilotNextWavePolicy.NextWaveKey(new string('w', 400)).Length <= AutopilotNextWavePolicy.MaxWaveKeyLength);

        var plans = new List<WaveLaunchTaskPlan>
        {
            new("bl-1", "One", "bl-1", Branch, CandidateSha),
            new("bl-2", "Two", "bl-2", Branch, CandidateSha),
        };
        Assert.Equal(
            AutopilotNextWavePolicy.PlanFingerprint("wave-1-next", plans),
            AutopilotNextWavePolicy.PlanFingerprint("wave-1-next", plans));
        Assert.NotEqual(
            AutopilotNextWavePolicy.PlanFingerprint("wave-1-next", plans),
            AutopilotNextWavePolicy.PlanFingerprint("wave-2-next", plans));

        Assert.False(AutopilotNextWavePolicy.CanPerform(AutopilotForbiddenActions.ForcePush));
        Assert.False(AutopilotNextWavePolicy.CanPerform(AutopilotForbiddenActions.ResetProductionDatabase));
        Assert.False(AutopilotNextWavePolicy.CanPerform("production_release"));
        Assert.False(AutopilotNextWavePolicy.CanPerform("integration_merge"));
        Assert.True(AutopilotNextWavePolicy.CanPerform("launch_development_task"));

        var options = new AutopilotOptions();
        options.Normalize();
        Assert.False(options.AllowNextWaveLaunch);
        Assert.False(options.AllowNextWaveLaunchWhenReleasePending);
        Assert.False(options.LiveNextWaveLaunchEnabled);
        Assert.Equal(20, options.MaxTasksPerWave);
        Assert.Equal(3, options.MaxLaunchAttempts);
    }

    // ---------------------------------------------------------------------
    // Test host
    // ---------------------------------------------------------------------

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now = now.Add(delta);
    }

    private sealed record RecordedBridgeCall(HttpMethod Method, string Uri, string? Authorization, string? IdempotencyKey, string Body);

    private sealed class StubBridgeHandler : HttpMessageHandler
    {
        public List<RecordedBridgeCall> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"taskId\":\"bridge-task-1\"}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add(new RecordedBridgeCall(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? string.Join(',', values) : null,
                body));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class FakeWaveLaunchProvider : IWaveLaunchProvider
    {
        public string ProviderKey => "fake-bridge";

        public bool IsConfigured { get; set; } = true;

        public int LaunchCalls { get; private set; }

        public int StateCalls { get; private set; }

        public List<WaveLaunchProviderRequest> Requests { get; } = [];

        public Queue<WaveLaunchProviderResult> Results { get; } = new();

        public Func<string, WaveLaunchTaskState?> StateResolver { get; set; } = _ => null;

        public Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default)
        {
            LaunchCalls++;
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : WaveLaunchProviderResult.Launched($"ext-{request.TaskKey}"));
        }

        public Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default)
        {
            StateCalls++;
            return Task.FromResult(StateResolver(externalTaskId));
        }
    }

    private sealed class Host : IDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public Host(Action<AutopilotOptions>? configure = null)
        {
            connection.Open();
            Options = new AutopilotOptions
            {
                Enabled = true,
                DryRun = true,
                SimulationMode = true,
                RequireSignedEvents = false,
            };
            configure?.Invoke(Options);
            Options.Normalize();
            using var db = CreateContext();
            db.Database.EnsureCreated();
        }

        public AutopilotOptions Options { get; }

        public TestTimeProvider Time { get; } = new();

        public FakeWaveLaunchProvider Provider { get; } = new();

        public TaslimDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

        public async Task<AutopilotCycleResult> RunCycleAsync()
        {
            using var db = CreateContext();
            return await CreateOrchestrator(db).ProcessPendingEventsAsync();
        }

        public async Task<AutopilotNextWaveCycleResult> LaunchAsync()
        {
            using var db = CreateContext();
            return await CreateNextWaveService(db).TryLaunchNextWavesAsync();
        }

        public async Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync()
        {
            using var db = CreateContext();
            return await CreateNextWaveService(db).ReconcileLaunchesAsync();
        }

        public async Task SetKillSwitchAsync(bool engaged)
        {
            using var db = CreateContext();
            await CreateOrchestrator(db).SetControlAsync(null, engaged, null, "test kill switch");
        }

        /// <summary>Submits a fully green wave completion and runs one controller cycle.</summary>
        public async Task GreenRunAsync(string waveKey)
        {
            await SubmitAsync(waveKey, "task-1", CompleteChecks());
            await RunCycleAsync();
        }

        public async Task AddBacklogAsync(
            string itemKey,
            string title,
            bool approved = true,
            string kind = AutopilotBacklogItemKinds.Development,
            int priority = 0)
        {
            using var db = CreateContext();
            var service = new AutopilotBacklogService(db, Time);
            var result = await service.UpsertAsync(new AutopilotBacklogItemRequest
            {
                ItemKey = itemKey,
                Title = title,
                Kind = kind,
                Approved = approved,
                Priority = priority,
                Reason = "test approval",
            }, null);
            Assert.True(result.Succeeded, result.Reason);
        }

        public async Task SubmitAsync(string waveKey, string taskId, Dictionary<string, bool> checks)
        {
            var payload = JsonSerializer.Serialize(new
            {
                waveKey,
                taskId,
                outcome = AutopilotTaskOutcomes.Succeeded,
                branch = Branch,
                baseSha = BaseSha,
                candidateSha = CandidateSha,
                attempt = 1,
                evidence = $"evidence for {taskId}",
                checks,
                expectedTaskCount = 1,
            });

            using var db = CreateContext();
            var result = await CreateIntake(db).SubmitAsync(new AutopilotIntakeRequest(
                null, null, payload, "completion-bridge", $"{waveKey}-{taskId}-{Guid.NewGuid():N}", AutopilotEventTypes.WaveTaskCompleted));
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, result.Outcome);
        }

        private AutopilotEventIntake CreateIntake(TaslimDbContext db) =>
            new(db, new AutopilotEventAuthenticator(Options), new EfAutopilotAuditLog(db), Options, Time);

        private AutopilotNextWaveService CreateNextWaveService(TaslimDbContext db) =>
            new(db, Options, Provider, CreateIntake(db), Time, NullLogger<AutopilotNextWaveService>.Instance);

        private AutopilotOrchestrator CreateOrchestrator(TaslimDbContext db) =>
            new(db, new EfAutopilotLockService(db, Options), CreateNextWaveService(db), Options, Time, NullLogger<AutopilotOrchestrator>.Instance);

        public void Dispose()
        {
            connection.Close();
            connection.Dispose();
        }
    }

    private static Dictionary<string, bool> CompleteChecks() => new(StringComparer.OrdinalIgnoreCase)
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
}

/// <summary>
/// Administrator HTTP surface for the follow-on wave launch: the launch and
/// backlog views are administrator-only, and approving work requires a reason.
/// </summary>
public sealed class AutopilotNextWaveEndpointTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AutopilotNextWaveEndpointTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Next_wave_surfaces_require_administrator_access()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/next-waves")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/autopilot/backlog")).StatusCode);
    }

    [Fact]
    public async Task Backlog_approval_requires_a_reason_and_is_recorded()
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAdmin(client);
        await AddAdminRole(auth.User.Email);

        var missingReason = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/backlog", new
        {
            itemKey = "bl-endpoint-1",
            title = "Add workspace audit export",
            approved = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);

        var approved = await SendWithCsrf(client, HttpMethod.Post, "/api/admin/autopilot/backlog", new
        {
            itemKey = "bl-endpoint-1",
            title = "Add workspace audit export",
            approved = true,
            kind = AutopilotBacklogItemKinds.Development,
            reason = "approved by the product owner",
        });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await approved.Content.ReadAsStringAsync());
        Assert.True(body.GetProperty("approved").GetBoolean());
        Assert.Equal(AutopilotBacklogItemKinds.Development, body.GetProperty("kind").GetString());

        var list = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("/api/admin/autopilot/backlog"));
        Assert.Contains(list.EnumerateArray(), item => item.GetProperty("itemKey").GetString() == "bl-endpoint-1");

        var nextWaves = await client.GetAsync("/api/admin/autopilot/next-waves");
        Assert.Equal(HttpStatusCode.OK, nextWaves.StatusCode);
    }

    private async Task<AuthResponse> RegisterAdmin(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Next Wave Administrator",
            email = $"next-wave-admin-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Taslim.Api.Domain.ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
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
}

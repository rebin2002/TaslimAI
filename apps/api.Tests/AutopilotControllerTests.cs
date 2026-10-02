using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Autopilot;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// End-to-end orchestration tests for the Autopilot Controller foundation.
/// Each test drives the real intake, lock, gate, and state-machine code over a
/// shared SQLite connection so duplicate, missed, partial, retried, blocked,
/// and restarted flows are all exercised for real.
/// </summary>
public sealed class AutopilotControllerTests
{
    private const string Branch = "feature/taslim-autopilot-controller";
    private const string BaseSha = "8bc3999d8a27f047e253a1f8832bd76e5c6dcb9a";
    private const string CandidateSha = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task Duplicate_events_are_stored_once_and_never_reapplied()
    {
        using var host = new AutopilotTestHost();
        const string externalEventId = "evt-duplicate-1";
        var submission = await host.SubmitRawAsync("bridge", externalEventId, "wave-dup", "task-1", AutopilotTaskOutcomes.Succeeded, CompleteChecks());

        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submission.Outcome);

        var duplicate = await host.SubmitRawAsync("bridge", externalEventId, "wave-dup", "task-1", AutopilotTaskOutcomes.Succeeded, CompleteChecks());
        Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicate.Outcome);

        await host.RunCycleAsync();
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        Assert.Equal(1, await db.AutopilotEvents.CountAsync());
        Assert.Equal(1, await db.AutopilotWaveTasks.CountAsync());
        Assert.Equal(1, await db.AutopilotRuns.CountAsync());
        Assert.Equal(2, await db.AutopilotGateEvaluations.CountAsync());
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item => item.Action == AutopilotAuditActions.EventDuplicateIgnored);
    }

    [Fact]
    public async Task Partial_wave_stays_in_progress_and_only_advances_when_complete()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-partial", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks(), expectedTaskCount: 2);
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var run = await db.AutopilotRuns.SingleAsync();
            Assert.Equal(AutopilotRunStates.InProgress, run.State);
            Assert.Equal(1, run.TaskCount);
            Assert.Equal(0, await db.AutopilotGateEvaluations.CountAsync());
        }

        await host.SubmitAsync("wave-partial", "task-2", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks(), expectedTaskCount: 2);
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var run = await db.AutopilotRuns.SingleAsync();
            Assert.Equal(AutopilotRunStates.NextWaveEligible, run.State);
            Assert.Equal(2, run.TaskCount);
            Assert.Equal(2, run.SucceededTaskCount);
            Assert.True(run.IntegrationGatePassed);
            Assert.True(run.ReleaseGatePassed);
        }
    }

    [Fact]
    public async Task Duplicate_processing_of_the_same_event_cannot_launch_a_second_gate()
    {
        using var host = new AutopilotTestHost();
        var submission = await host.SubmitAsync("wave-idem", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        Assert.NotNull(submission.EventId);

        await host.ProcessEventAsync(submission.EventId!.Value);
        await host.ProcessEventAsync(submission.EventId.Value);
        await host.ProcessEventAsync(submission.EventId.Value);

        using var db = host.CreateContext();
        Assert.Equal(2, await db.AutopilotGateEvaluations.CountAsync());
        Assert.Equal(1, await db.AutopilotReleaseHandoffs.CountAsync());
        Assert.Single(await db.AutopilotRuns.ToListAsync());
    }

    [Fact]
    public async Task Ordinary_failure_is_retried_with_bounded_backoff_then_escalates()
    {
        using var host = new AutopilotTestHost(o => o.MaxTaskAttempts = 2);

        await host.SubmitAsync("wave-retry", "task-1", AutopilotTaskOutcomes.Failed, failureClass: AutopilotFailureClasses.Ordinary, attempt: 0);
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveTasks.SingleAsync();
            Assert.Equal(AutopilotTaskStates.RetryScheduled, task.State);
            Assert.NotNull(task.NextAttemptAt);
            Assert.True(task.NextAttemptAt > DateTime.UtcNow.AddSeconds(-5));
        }

        await host.SubmitAsync("wave-retry", "task-1", AutopilotTaskOutcomes.Failed, failureClass: AutopilotFailureClasses.Ordinary, attempt: 1);
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotTaskStates.RetryScheduled, (await db.AutopilotWaveTasks.SingleAsync()).State);
            Assert.Contains(host.LastCycle.PlannedActions, action => action.StartsWith("retry_task:task-1", StringComparison.Ordinal));
        }

        await host.SubmitAsync("wave-retry", "task-1", AutopilotTaskOutcomes.Failed, failureClass: AutopilotFailureClasses.Ordinary, attempt: 2);
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveTasks.SingleAsync();
            Assert.Equal(AutopilotTaskStates.TerminalFailed, task.State);
            Assert.True(task.RequiresHumanDecision);
            Assert.Equal(AutopilotHumanDecisions.RepairEscalation, task.HumanDecisionKind);

            var run = await db.AutopilotRuns.SingleAsync();
            Assert.Equal(AutopilotRunStates.IntegrationFailed, run.State);
            Assert.Equal(AutopilotHumanDecisions.RepairEscalation, run.HumanDecisionRequired);
            Assert.Empty(await db.AutopilotGateEvaluations.ToListAsync());
        }
    }

    [Fact]
    public async Task Blocking_failure_classes_stop_the_wave_without_retry()
    {
        foreach (var failureClass in new[]
                 {
                     AutopilotFailureClasses.Migration,
                     AutopilotFailureClasses.BrowserE2E,
                     AutopilotFailureClasses.Security,
                     AutopilotFailureClasses.Accounting,
                     AutopilotFailureClasses.Integration,
                 })
        {
            using var host = new AutopilotTestHost();
            await host.SubmitAsync($"wave-{failureClass}", "task-1", AutopilotTaskOutcomes.Failed, failureClass: failureClass, attempt: 0);
            await host.RunCycleAsync();

            using var db = host.CreateContext();
            var task = await db.AutopilotWaveTasks.SingleAsync();
            Assert.Equal(AutopilotTaskStates.BlockedHuman, task.State);
            Assert.True(task.RequiresHumanDecision);
            Assert.NotEqual(AutopilotRunStates.ReleaseEligible, (await db.AutopilotRuns.SingleAsync()).State);
            Assert.False(host.LastCycle.PlannedActions.Any(action => action.StartsWith("retry_task", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task Integration_failure_blocks_release_gate_evaluation()
    {
        using var host = new AutopilotTestHost();
        var checks = CompleteChecks();
        checks[AutopilotChecks.Migrations] = false;
        await host.SubmitAsync("wave-int-fail", "task-1", AutopilotTaskOutcomes.Succeeded, checks: checks);
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync();
        Assert.Equal(AutopilotRunStates.IntegrationFailed, run.State);
        Assert.False(run.IntegrationGatePassed);
        Assert.False(run.ReleaseGatePassed);

        var gates = await db.AutopilotGateEvaluations.ToListAsync();
        Assert.Single(gates);
        Assert.Equal(AutopilotGateKinds.Integration, gates[0].GateKind);
        Assert.Equal(AutopilotGateOutcomes.Failed, gates[0].Outcome);
        Assert.Empty(await db.AutopilotReleaseHandoffs.ToListAsync());
    }

    [Fact]
    public async Task Release_gate_failure_blocks_release_eligibility()
    {
        using var host = new AutopilotTestHost();
        var checks = CompleteChecks();
        checks[AutopilotChecks.Security] = false;
        await host.SubmitAsync("wave-rel-fail", "task-1", AutopilotTaskOutcomes.Succeeded, checks: checks);
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync();
        Assert.Equal(AutopilotRunStates.ReleaseBlocked, run.State);
        Assert.True(run.IntegrationGatePassed);
        Assert.False(run.ReleaseGatePassed);
        Assert.Equal(AutopilotHumanDecisions.SecurityAmbiguity, run.HumanDecisionRequired);

        var gates = await db.AutopilotGateEvaluations.OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Equal(2, gates.Count);
        Assert.Equal(AutopilotGateOutcomes.Passed, gates[0].Outcome);
        Assert.Equal(AutopilotGateOutcomes.Failed, gates[1].Outcome);
        Assert.Empty(await db.AutopilotReleaseHandoffs.ToListAsync());
    }

    [Fact]
    public async Task Fully_green_wave_reaches_release_eligibility_and_next_wave_eligibility_in_simulation()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-green", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync();
        Assert.Equal(AutopilotRunStates.NextWaveEligible, run.State);
        Assert.True(run.IntegrationGatePassed);
        Assert.True(run.ReleaseGatePassed);
        Assert.Null(run.HumanDecisionRequired);

        var handoff = await db.AutopilotReleaseHandoffs.SingleAsync();
        Assert.True(handoff.DryRun);
        Assert.Equal(AutopilotHandoffStatuses.Completed, handoff.Status);
        Assert.Equal(AutopilotSmokeStatuses.Passed, handoff.SmokeStatus);

        var audit = await db.AutopilotAuditEvents.ToListAsync();
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.ReleaseHandoffPrepared && item.DryRun);
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.ProductionSmoke && item.DryRun);
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.NextWaveEligibility);
        Assert.Contains(host.LastCycle.PlannedActions, action => action.StartsWith("plan_next_wave_after:", StringComparison.Ordinal));
        Assert.Contains(host.LastCycle.PlannedActions, action => action.StartsWith("release_handoff:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_mode_requires_human_authorization_before_production_release()
    {
        using var host = new AutopilotTestHost(o =>
        {
            o.DryRun = false;
            o.SimulationMode = false;
            o.AllowAutomaticRelease = false;
        });

        await host.SubmitAsync("wave-live", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync();
        Assert.Equal(AutopilotRunStates.HandoffPendingHuman, run.State);
        Assert.Equal(AutopilotHumanDecisions.ProductionRelease, run.HumanDecisionRequired);

        var handoff = await db.AutopilotReleaseHandoffs.SingleAsync();
        Assert.False(handoff.DryRun);
        Assert.Equal(AutopilotHandoffStatuses.AwaitingHuman, handoff.Status);
        Assert.Equal(AutopilotSmokeStatuses.NotRun, handoff.SmokeStatus ?? AutopilotSmokeStatuses.NotRun);

        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
            item => item.Action == AutopilotAuditActions.HumanDecisionRequired && item.Reason == AutopilotHumanDecisions.ProductionRelease);
    }

    [Fact]
    public async Task Disabled_feature_flag_queues_events_without_orchestrating()
    {
        using var host = new AutopilotTestHost(o => o.Enabled = false);
        await host.SubmitAsync("wave-off", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        var cycle = await host.RunCycleAsync();
        Assert.Equal("disabled", cycle.Outcome);
        Assert.Equal(0, cycle.EventsProcessed);

        using var db = host.CreateContext();
        Assert.Empty(await db.AutopilotRuns.ToListAsync());
        Assert.Equal(AutopilotEventStatuses.Queued, (await db.AutopilotEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Kill_switch_and_pause_stop_all_orchestration()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-control", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        await host.SetControlAsync(paused: null, killSwitch: true, "test kill switch");
        var killed = await host.RunCycleAsync();
        Assert.Equal("kill_switch", killed.Outcome);

        await host.SetControlAsync(paused: true, killSwitch: false, "test pause");
        var paused = await host.RunCycleAsync();
        Assert.Equal("paused", paused.Outcome);

        await host.SetControlAsync(paused: false, killSwitch: false, "resume");
        var resumed = await host.RunCycleAsync();
        Assert.True(resumed.EventsProcessed >= 1);

        using var db = host.CreateContext();
        Assert.Equal(AutopilotRunStates.NextWaveEligible, (await db.AutopilotRuns.SingleAsync()).State);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item => item.Action == AutopilotAuditActions.ControlChanged);
    }

    [Fact]
    public async Task Watchdog_reconciles_a_missed_completion_event()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-missed", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        // Simulate a delivery that arrived but was never picked up by the worker.
        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            intakeEvent.Status = AutopilotEventStatuses.Received;
            intakeEvent.QueuedAt = null;
            intakeEvent.ReceivedAt = DateTime.UtcNow.AddHours(-3);
            await db.SaveChangesAsync();
        }

        var reconciliation = await host.ReconcileAsync("test_watchdog");
        Assert.Equal("reconciled", reconciliation.Outcome);

        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            Assert.Equal(AutopilotEventStatuses.Processed, intakeEvent.Status);
            Assert.Equal(1, intakeEvent.ReconciliationCount);
            Assert.Equal(AutopilotRunStates.NextWaveEligible, (await db.AutopilotRuns.SingleAsync()).State);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.WatchdogReconciled);
        }
    }

    [Fact]
    public async Task Restart_recovery_requeues_an_inflight_event_and_reaches_terminal_state()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-restart", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        // Simulate a crash after the event was claimed but before it completed.
        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            intakeEvent.Status = AutopilotEventStatuses.Processing;
            intakeEvent.ProcessingStartedAt = DateTime.UtcNow.AddHours(-2);
            intakeEvent.ReceivedAt = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        // A brand new host simulates a restarted process with a fresh DbContext.
        var recovery = await host.ReconcileAsync("restart_recovery");
        Assert.Equal("reconciled", recovery.Outcome);

        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotEventStatuses.Processed, (await db.AutopilotEvents.SingleAsync()).Status);
            Assert.Equal(AutopilotRunStates.NextWaveEligible, (await db.AutopilotRuns.SingleAsync()).State);
        }
    }

    [Fact]
    public async Task Watchdog_stops_reconciling_after_a_bounded_number_of_attempts()
    {
        using var host = new AutopilotTestHost(o => o.MaxWatchdogReconciliations = 1);
        await host.SubmitAsync("wave-exhaust", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            intakeEvent.Status = AutopilotEventStatuses.Received;
            intakeEvent.ReceivedAt = DateTime.UtcNow.AddHours(-3);
            intakeEvent.ReconciliationCount = 1;
            await db.SaveChangesAsync();
        }

        await host.ReconcileAsync("exhaustion");
        using (var db = host.CreateContext())
        {
            var intakeEvent = await db.AutopilotEvents.SingleAsync();
            Assert.Equal(AutopilotEventStatuses.Failed, intakeEvent.Status);
            Assert.Equal("watchdog_reconciliation_exhausted", intakeEvent.Reason);
        }
    }

    [Fact]
    public async Task Lock_contention_prevents_duplicate_processing_of_the_same_wave()
    {
        using var host = new AutopilotTestHost();
        var submission = await host.SubmitAsync("wave-lock", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        Assert.NotNull(submission.EventId);

        AutopilotLockHandle held;
        using (var db = host.CreateContext())
        {
            var locks = new EfAutopilotLockService(db, host.Options);
            held = await locks.AcquireAsync(EfAutopilotLockService.WaveResource("wave-lock"), "test-holder");
            Assert.True(held.IsAcquired);
        }

        var contended = await host.ProcessEventAsync(submission.EventId!.Value);
        Assert.Equal("contended", contended.Outcome);

        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotEventStatuses.Queued, (await db.AutopilotEvents.SingleAsync()).Status);
            Assert.Empty(await db.AutopilotRuns.ToListAsync());
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item => item.Action == AutopilotAuditActions.LockContended);
            var locks = new EfAutopilotLockService(db, host.Options);
            Assert.True(await locks.ReleaseAsync(held));
        }

        var processed = await host.ProcessEventAsync(submission.EventId.Value);
        Assert.True(processed.EventsProcessed >= 1);

        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotRunStates.NextWaveEligible, (await db.AutopilotRuns.SingleAsync()).State);
        }
    }

    [Fact]
    public async Task Exclusive_lock_rejects_a_second_live_holder()
    {
        using var host = new AutopilotTestHost();
        using var db = host.CreateContext();
        var locks = new EfAutopilotLockService(db, host.Options);

        var first = await locks.AcquireAsync(EfAutopilotLockService.WaveResource("wave-exclusive"), "holder-a");
        Assert.True(first.IsAcquired);

        var second = await locks.AcquireAsync(EfAutopilotLockService.WaveResource("wave-exclusive"), "holder-b");
        Assert.False(second.IsAcquired);
        Assert.Equal("holder-a", second.HeldBy);

        // A stale holder that never released is fenced out by the lease.
        var stale = await db.AutopilotLocks.SingleAsync();
        stale.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        var third = await locks.AcquireAsync(EfAutopilotLockService.WaveResource("wave-exclusive"), "holder-c");
        Assert.True(third.IsAcquired);
        Assert.True(third.FencingToken > first.FencingToken);
    }

    [Fact]
    public async Task Immutable_base_and_candidate_shas_cannot_be_repointed()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-sha", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks(), candidateSha: CandidateSha);
        await host.RunCycleAsync();

        var conflicting = await host.SubmitAsync("wave-sha", "task-2", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks(), candidateSha: "2222222222222222222222222222222222222222");
        Assert.Equal(AutopilotIntakeOutcomes.ShaConflict, conflicting.Outcome);

        using var db = host.CreateContext();
        var run = await db.AutopilotRuns.SingleAsync();
        Assert.Equal(CandidateSha, run.CandidateSha);
        Assert.Equal(BaseSha, run.BaseSha);
        Assert.Equal(AutopilotRunStates.NextWaveEligible, run.State);
        Assert.Contains(await db.AutopilotEvents.ToListAsync(), item => item.Reason == "immutable_candidate_sha_conflict");
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item => item.Action == AutopilotAuditActions.SafetyStop);
    }

    [Fact]
    public async Task Unsigned_events_are_rejected_when_signatures_are_required()
    {
        using var host = new AutopilotTestHost(o => o.RequireSignedEvents = true);
        var result = await host.SubmitAsync("wave-unsigned", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());

        Assert.Equal(AutopilotIntakeOutcomes.RejectedUnsigned, result.Outcome);
        using var db = host.CreateContext();
        Assert.Empty(await db.AutopilotEvents.ToListAsync());
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item => item.Action == AutopilotAuditActions.EventRejected);
    }

    [Fact]
    public async Task Events_with_a_reused_identity_but_different_payload_are_refused()
    {
        using var host = new AutopilotTestHost();
        var first = await host.SubmitRawAsync("bridge", "evt-fixed", "wave-conflict", "task-1", AutopilotTaskOutcomes.Succeeded, CompleteChecks());
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, first.Outcome);

        var mutated = await host.SubmitRawAsync("bridge", "evt-fixed", "wave-conflict", "task-1", AutopilotTaskOutcomes.Failed, CompleteChecks());
        Assert.Equal(AutopilotIntakeOutcomes.PayloadConflict, mutated.Outcome);

        using var db = host.CreateContext();
        Assert.Single(await db.AutopilotEvents.ToListAsync());
    }

    [Fact]
    public async Task Audit_trail_records_reason_status_attempt_task_ids_branch_and_sha()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-audit", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var audit = await db.AutopilotAuditEvents.ToListAsync();
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.EventProcessed
            && item.WaveKey == "wave-audit"
            && item.TaskId == "task-1"
            && item.Branch == Branch
            && item.BaseSha == BaseSha
            && item.CandidateSha == CandidateSha);
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.RunStateChanged && item.RunState == AutopilotRunStates.NextWaveEligible);
        Assert.Contains(audit, item => item.Action == AutopilotAuditActions.GateEvaluated && item.Reason != null);
    }

    [Fact]
    public async Task Consoles_expose_only_product_safe_state()
    {
        using var host = new AutopilotTestHost();
        await host.SubmitAsync("wave-console", "task-1", AutopilotTaskOutcomes.Succeeded, checks: CompleteChecks());
        await host.RunCycleAsync();

        using var db = host.CreateContext();
        var console = new AutopilotConsoleService(db);
        var control = await host.GetControlAsync();
        var overview = await console.GetOverviewAsync(control, "wave-console");

        Assert.True(overview.Control.FeatureEnabled);
        Assert.True(overview.Control.DryRun);
        Assert.False(overview.Control.ChargingEnabled);
        Assert.False(overview.Control.PaidProvidersEnabled);
        Assert.NotNull(overview.Run);
        Assert.Equal(AutopilotRunStates.NextWaveEligible, overview.Run!.State);
        Assert.Equal(2, overview.Gates.Count);
        Assert.NotNull(overview.Handoff);
        Assert.NotEmpty(overview.RecentAudit);
        Assert.Contains(AutopilotHumanDecisions.EnableCharging, console.HumanDecisionCatalogue());
        Assert.Contains(AutopilotForbiddenActions.ForcePush, console.ForbiddenOperations());
    }

    // ---------------------------------------------------------------------
    // Test host
    // ---------------------------------------------------------------------

    private sealed class AutopilotTestHost : IDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");

        public AutopilotTestHost(Action<AutopilotOptions>? configure = null)
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

        public AutopilotCycleResult LastCycle { get; private set; } = new(true, true, "idle", null, 0, 0, 0, [], []);

        public TaslimDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

        public async Task<AutopilotCycleResult> RunCycleAsync()
        {
            using var db = CreateContext();
            LastCycle = await CreateOrchestrator(db).ProcessPendingEventsAsync();
            return LastCycle;
        }

        public async Task<AutopilotCycleResult> ReconcileAsync(string reason)
        {
            using var db = CreateContext();
            LastCycle = await CreateOrchestrator(db).ReconcileAsync(reason);
            return LastCycle;
        }

        public async Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId)
        {
            using var db = CreateContext();
            LastCycle = await CreateOrchestrator(db).ProcessEventAsync(eventId);
            return LastCycle;
        }

        public async Task<AutopilotControlSnapshot> GetControlAsync()
        {
            using var db = CreateContext();
            return await CreateOrchestrator(db).GetControlAsync();
        }

        public async Task<AutopilotControlSnapshot> SetControlAsync(bool? paused, bool? killSwitch, string reason)
        {
            using var db = CreateContext();
            return await CreateOrchestrator(db).SetControlAsync(paused, killSwitch, null, reason);
        }

        public async Task<AutopilotIntakeResult> SubmitAsync(
            string waveKey,
            string taskId,
            string outcome,
            string? failureClass = null,
            int attempt = 0,
            Dictionary<string, bool>? checks = null,
            string candidateSha = CandidateSha,
            int? expectedTaskCount = 1) =>
            await SubmitRawAsync(
                $"bridge-{Guid.NewGuid():N}",
                $"{waveKey}-{taskId}-{outcome}-{failureClass ?? "none"}-{attempt}-{Guid.NewGuid():N}",
                waveKey,
                taskId,
                outcome,
                checks,
                failureClass,
                attempt,
                candidateSha,
                expectedTaskCount);

        public async Task<AutopilotIntakeResult> SubmitRawAsync(
            string sourceSystem,
            string externalEventId,
            string waveKey,
            string taskId,
            string outcome,
            Dictionary<string, bool>? checks = null,
            string? failureClass = null,
            int attempt = 0,
            string candidateSha = CandidateSha,
            int? expectedTaskCount = 1)
        {
            var payload = JsonSerializer.Serialize(new
            {
                waveKey,
                taskId,
                outcome,
                failureClass,
                branch = Branch,
                baseSha = BaseSha,
                candidateSha,
                attempt,
                evidence = $"evidence for {taskId}",
                checks,
                expectedTaskCount,
            });

            using var db = CreateContext();
            var intake = new AutopilotEventIntake(
                db,
                new AutopilotEventAuthenticator(Options),
                new EfAutopilotAuditLog(db),
                Options,
                TimeProvider.System);
            return await intake.SubmitAsync(new AutopilotIntakeRequest(null, null, payload, sourceSystem, externalEventId, AutopilotEventTypes.WaveTaskCompleted));
        }

        private AutopilotOrchestrator CreateOrchestrator(TaslimDbContext db) =>
            new(db, new EfAutopilotLockService(db, Options), Options, TimeProvider.System, NullLogger<AutopilotOrchestrator>.Instance);

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

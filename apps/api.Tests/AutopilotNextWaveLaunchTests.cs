using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    public async Task Processed_exact_task_detail_receipt_immediately_refills_one_slot_while_sibling_is_active()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 2;
        });
        await host.AddBacklogAsync("bl-refill-1", "Implement worker retry summary", priority: 1);
        await host.AddBacklogAsync("bl-refill-2", "Add task reconciliation metric", priority: 2);
        await host.AddBacklogAsync("bl-refill-3", "Add durable queue depth alert", priority: 3);
        await host.GreenRunAsync("wave-refill");
        Assert.Equal(2, host.Provider.LaunchCalls);

        AutopilotWaveLaunchTask completedTask;
        using (var db = host.CreateContext())
        {
            var launched = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .ToListAsync();
            Assert.Equal(2, launched.Count);
            completedTask = launched[0];
        }

        const string eventId = "test-refill-completion-1";
        var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            completedTask.ExternalTaskId,
            "stopped",
            "finish",
            false,
            eventId));
        var submitted = await host.SubmitSignedCompletionAsync(
            completedTask.WaveKey,
            completedTask.TaskKey,
            expectedTaskCount: 2,
            evidence: receipt,
            externalEventId: eventId);
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);

        // The orchestrator drains and commits the signed event; the immediate
        // post-drain launch pass (wired by #82) consumes it without an hourly wait.
        await host.RunCycleAsync();
        using (var db = host.CreateContext())
        {
            var sourceBatch = await db.AutopilotWaveLaunchBatches.SingleAsync(item => item.WaveKey == "wave-refill-next");
            var completed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == completedTask.Id);
            var terminalEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == "test-refill-completion-1");
            var receiptAudit = await db.AutopilotAuditEvents
                .Where(item => item.TargetId == terminalEvent.Id)
                .Select(item => $"{item.Action}:{item.Reason}:{item.StatusDetail}")
                .ToListAsync();
            Assert.True(completed.Status == AutopilotWaveLaunchStatuses.Completed,
                $"launchStatus={completed.Status}; eventStatus={terminalEvent.Status}; verified={terminalEvent.SignatureVerified}; replay={terminalEvent.ReplayProtected}; wave={terminalEvent.WaveKey}; task={terminalEvent.TaskId}; receiptAudit={string.Join('|', receiptAudit)}");
            Assert.Contains(await db.AutopilotWaveLaunchTasks.ToListAsync(), item =>
                item.BatchId == sourceBatch.Id
                && item.Id != completed.Id
                && item.Status == AutopilotWaveLaunchStatuses.Launched);

            var refillBatches = await db.AutopilotWaveLaunchBatches
                .Where(item => item.SourceWaveKey == "wave-refill"
                    && item.WaveKey.StartsWith("wave-refill-next-refill-"))
                .ToListAsync();
            Assert.True(refillBatches.Count == 1,
                $"refillBatchCount={refillBatches.Count}; sourceBatch={sourceBatch.Status}; tasks={string.Join(',', (await db.AutopilotWaveLaunchTasks.ToListAsync()).Select(item => $"{item.TaskKey}:{item.Status}:{item.BacklogItemKey}"))}; backlog={string.Join(',', (await db.AutopilotBacklogItems.ToListAsync()).Select(item => $"{item.ItemKey}:{item.ConsumedByWaveKey}"))}");
            var refillBatch = refillBatches.Single();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, refillBatch.Status);
            Assert.Equal(1, refillBatch.TaskCount);
            Assert.Equal("wave-refill", refillBatch.SourceWaveKey);
            var refillTask = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.BatchId == refillBatch.Id);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, refillTask.Status);
            Assert.Equal("bl-refill-3", refillTask.BacklogItemKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.NextWavePlanSelected
                && item.Reason == "verified_completion_refill"
                && item.TargetId == refillBatch.Id);
        }

        Assert.Equal(3, host.Provider.LaunchCalls);
        await host.LaunchAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);

        var duplicate = await host.SubmitSignedCompletionAsync(
            completedTask.WaveKey,
            completedTask.TaskKey,
            expectedTaskCount: 2,
            evidence: receipt,
            externalEventId: "test-refill-completion-1");
        Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicate.Outcome);
        await host.RunCycleAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Long_source_wave_key_preserves_one_distinct_refill_per_completed_sibling()
    {
        var sourceWaveKey = new string('w', AutopilotNextWavePolicy.MaxWaveKeyLength);
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 3;
            options.MaxLaunchBatchesPerCycle = 32;
        });
        await host.AddBacklogAsync("bl-long-refill-1", "First active sibling", priority: 1);
        await host.AddBacklogAsync("bl-long-refill-2", "Second active sibling", priority: 2);
        await host.AddBacklogAsync("bl-long-refill-3", "Third active sibling", priority: 3);
        await host.AddBacklogAsync("bl-long-refill-4", "First replacement", priority: 4);
        await host.AddBacklogAsync("bl-long-refill-5", "Second replacement", priority: 5);

        await host.GreenRunAsync(sourceWaveKey);
        Assert.Equal(3, host.Provider.LaunchCalls);
        List<AutopilotWaveLaunchTask> initialTasks;
        using (var db = host.CreateContext())
        {
            initialTasks = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .ToListAsync();
            Assert.Equal(3, initialTasks.Count);
            Assert.Equal(AutopilotNextWavePolicy.MaxWaveKeyLength, initialTasks[0].WaveKey.Length);
            Assert.All(initialTasks, item => Assert.Equal(initialTasks[0].WaveKey, item.WaveKey));
        }

        foreach (var (task, index) in initialTasks.Take(2).Select((task, index) => (task, index)))
        {
            var eventId = $"long-wave-refill-{index}";
            var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
                AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
                task.ExternalTaskId,
                "stopped",
                "finish",
                false,
                eventId));
            var submitted = await host.SubmitSignedCompletionAsync(
                task.WaveKey, task.TaskKey, expectedTaskCount: 3, receipt, eventId);
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);
            await host.RunCycleAsync();
        }

        using (var db = host.CreateContext())
        {
            var completedIds = initialTasks.Take(2).Select(item => item.Id).ToArray();
            var completedRows = await db.AutopilotWaveLaunchTasks
                .Where(item => completedIds.Contains(item.Id))
                .ToListAsync();
            Assert.Equal(2, completedRows.Count);
            Assert.All(completedRows, item => Assert.Equal(AutopilotWaveLaunchStatuses.Completed, item.Status));

            var refillBatches = await db.AutopilotWaveLaunchBatches
                .Where(item => item.SourceWaveKey == sourceWaveKey
                    && item.WaveKey.Contains("-refill-"))
                .ToListAsync();
            Assert.Equal(2, refillBatches.Count);
            Assert.Equal(2, refillBatches.Select(item => item.WaveKey).Distinct(StringComparer.Ordinal).Count());
            Assert.All(refillBatches, batch => Assert.True(batch.WaveKey.Length <= AutopilotNextWavePolicy.MaxWaveKeyLength));
            Assert.Equal(2, refillBatches.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));

            var refillBatchIds = refillBatches.Select(item => item.Id).ToArray();
            var refillTasks = await db.AutopilotWaveLaunchTasks
                .Where(item => refillBatchIds.Contains(item.BatchId))
                .ToListAsync();
            Assert.Equal(2, refillTasks.Count);
            foreach (var (completedTask, index) in initialTasks.Take(2).Select((task, index) => (task, index)))
            {
                var replacement = Assert.Single(refillTasks, item =>
                    item.TaskKey == $"refill-{completedTask.Id:N}");
                Assert.Equal($"bl-long-refill-{index + 4}", replacement.BacklogItemKey);
                Assert.Equal(AutopilotWaveLaunchStatuses.Launched, replacement.Status);
            }

            var activeTasks = await db.AutopilotWaveLaunchTasks
                .CountAsync(item => item.Status == AutopilotWaveLaunchStatuses.Launched
                    || item.Status == AutopilotWaveLaunchStatuses.Launching);
            Assert.Equal(3, activeTasks);
        }

        Assert.Equal(5, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Verified_completion_racing_provider_ack_is_never_downgraded_to_launched()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 1;
        });
        host.Provider.BeforeReturnAsync = host.CompleteLaunchBeforeAcknowledgementAsync;
        await host.AddBacklogAsync("bl-ack-race", "Handle provider acknowledgment race");

        await host.GreenRunAsync("wave-ack-race");

        using var db = host.CreateContext();
        var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Completed, task.Status);
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal("verified_completion_no_active_sibling", task.LastStatusDetail);
    }

    [Fact]
    public async Task Restart_after_verified_slot_release_recovers_missing_refill_and_launches_once()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 2;
        });
        await host.AddBacklogAsync("bl-restart-1", "First active task", priority: 1);
        await host.AddBacklogAsync("bl-restart-2", "Sibling remains active", priority: 2);
        await host.AddBacklogAsync("bl-restart-3", "Replacement after restart", priority: 3);
        await host.GreenRunAsync("wave-refill-restart");
        Assert.Equal(2, host.Provider.LaunchCalls);

        AutopilotWaveLaunchTask taskToComplete;
        using (var db = host.CreateContext())
        {
            taskToComplete = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .FirstAsync();
        }
        const string eventId = "test-restart-refill-completion";
        var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            taskToComplete.ExternalTaskId,
            "stopped",
            "finish",
            false,
            eventId));
        var submitted = await host.SubmitSignedCompletionAsync(
            taskToComplete.WaveKey,
            taskToComplete.TaskKey,
            expectedTaskCount: 2,
            evidence: receipt,
            externalEventId: eventId);
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);

        using (var db = host.CreateContext())
        {
            var terminalEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
            terminalEvent.Status = AutopilotEventStatuses.Processed;
            terminalEvent.ProcessedAt = host.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            var released = await new AutopilotGlobalTaskSlotAllocator(db, "taslim-autopilot-test-scope", host.Time)
                .CompleteVerifiedAsync(
                    taskToComplete.Id,
                    AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(taskToComplete.WaveKey, taskToComplete.TaskKey),
                    taskToComplete.ExternalTaskId!,
                    terminalEvent.Id);
            Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, released.Outcome);
            var durable = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == taskToComplete.Id);
            Assert.Equal("slot_released_once", durable.LastStatusDetail);
        }

        // Simulates restart after the durable release commit but before the
        // replacement batch transaction ran.
        await host.LaunchAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);
        using var verify = host.CreateContext();
        var refill = await verify.AutopilotWaveLaunchBatches.SingleAsync(item =>
            item.SourceWaveKey == "wave-refill-restart"
            && item.WaveKey.StartsWith("wave-refill-restart-next-refill-"));
        Assert.Equal(1, refill.TaskCount);
        var refillTask = await verify.AutopilotWaveLaunchTasks.SingleAsync(item => item.BatchId == refill.Id);
        Assert.Equal("bl-restart-3", refillTask.BacklogItemKey);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched, refillTask.Status);
    }

    [Fact]
    public async Task Signed_completion_without_exact_task_detail_receipt_does_not_release_or_refill()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 2;
        });
        await host.AddBacklogAsync("bl-no-receipt-1", "Implement worker retry summary", priority: 1);
        await host.AddBacklogAsync("bl-no-receipt-2", "Add task reconciliation metric", priority: 2);
        await host.AddBacklogAsync("bl-no-receipt-3", "Add durable queue depth alert", priority: 3);
        await host.GreenRunAsync("wave-no-receipt");

        var completedTask = await host.ReadSingleLaunchTaskAsync("wave-no-receipt-next");
        var submitted = await host.SubmitSignedCompletionAsync(
            completedTask.WaveKey,
            completedTask.TaskKey,
            expectedTaskCount: 2,
            evidence: "polling_fallback:completed",
            externalEventId: "test-refill-missing-receipt");
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);
        await host.RunCycleAsync();
        await host.LaunchAsync();

        using var db = host.CreateContext();
        var storedTask = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == completedTask.Id);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched, storedTask.Status);
        Assert.Empty(await db.AutopilotWaveLaunchBatches.Where(item => item.ReasonCode == "verified_completion_refill").ToListAsync());
        Assert.Equal(2, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Poll_state_that_loses_to_verified_completion_does_not_enqueue_orphan_poll_event()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-poll-cas-race", "Receipt wins a concurrent provider poll");
        await host.GreenRunAsync("wave-poll-cas-race");
        var launch = await host.ReadSingleLaunchTaskAsync("wave-poll-cas-race-next");
        var eventId = "test-poll-cas-receipt-wins";

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launch.Id);
            task.NextReconcileAt = host.Time.GetUtcNow().UtcDateTime.AddSeconds(-1);
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        host.Provider.StateResolver = _ => new WaveLaunchTaskState("succeeded", true, true, "poll-ref", "synthetic provider state");
        host.Provider.BeforeStateReturnAsync = async (_, cancellationToken) =>
        {
            using (var db = host.CreateContext())
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launch.Id, cancellationToken);
                task.NextReconcileAt = host.Time.GetUtcNow().UtcDateTime.AddMinutes(5);
                task.ConcurrencyToken = Guid.NewGuid();
                await db.SaveChangesAsync(cancellationToken);
            }

            var evidence = JsonSerializer.Serialize(new
            {
                kind = "taslim.manus.task_detail_terminal_receipt.v1",
                task_id = launch.ExternalTaskId,
                agent_status = "stopped",
                stop_reason = "finish",
                has_running_background_jobs = false,
                event_id = eventId,
            });
            var submitted = await host.SubmitSignedCompletionAsync(
                launch.WaveKey,
                launch.TaskKey,
                expectedTaskCount: 1,
                evidence: evidence,
                externalEventId: eventId);
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);
            await host.RunCycleAsync();
        };

        var reconciliation = await host.ReconcileLaunchesAsync();

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launch.Id);
            Assert.Equal(AutopilotWaveLaunchStatuses.Completed, task.Status);
            var receipt = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
            Assert.Equal(AutopilotEventStatuses.Processed, receipt.Status);
            Assert.True(receipt.SignatureVerified);
            Assert.True(receipt.ReplayProtected);
            Assert.Empty(await db.AutopilotEvents.Where(item =>
                item.SourceSystem == AutopilotInternalSignalSources.Polling).ToListAsync());
        }

        Assert.Contains(reconciliation.PlannedActions, item => item.StartsWith("next_wave_poll_raced_with_completion:", StringComparison.Ordinal));
        Assert.Equal(1, host.Provider.StateCalls);
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_poll_budget_persists_repair_escalation_for_terminal_and_nonterminal_results(bool terminal)
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchReconciles = 1;
        });
        await host.AddBacklogAsync($"bl-poll-exhausted-{terminal}", "Hold slot after final poll");
        await host.GreenRunAsync($"wave-poll-exhausted-{terminal}");

        var launch = await host.ReadSingleLaunchTaskAsync($"wave-poll-exhausted-{terminal}-next");
        host.Provider.StateResolver = _ => terminal
            ? new WaveLaunchTaskState("succeeded", true, true, "poll-ref", "terminal provider state")
            : new WaveLaunchTaskState("running", false, false, "poll-ref", "nonterminal provider state");
        var dueAt = host.Time.GetUtcNow().UtcDateTime.AddSeconds(-1);
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launch.Id);
            task.NextReconcileAt = dueAt;
            task.ReconcileCount = 0;
            task.MaxReconciles = 1;
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        await host.ReconcileLaunchesAsync();
        await host.ReconcileLaunchesAsync();

        var escalationDetail = terminal
            ? "terminal_poll_reconcile_exhausted; verified_terminal_receipt_or_operator_repair_required"
            : "reconcile_exhausted; verified_terminal_receipt_or_operator_repair_required";
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launch.Id);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status);
            Assert.False(string.IsNullOrWhiteSpace(task.ExternalTaskId));
            Assert.Equal(1, task.ReconcileCount);
            Assert.Equal("reconcile_exhausted; global_slot_retained_pending_verified_receipt", task.LastStatusDetail);
            Assert.Equal(dueAt, task.NextReconcileAt);
            Assert.Equal(1, await db.AutopilotAuditEvents.CountAsync(item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.Reason == AutopilotHumanDecisions.RepairEscalation
                && item.StatusDetail == escalationDetail
                && item.TargetId == task.Id));
            Assert.Equal(1, await db.AutopilotAuditEvents.CountAsync(item =>
                item.Action == AutopilotAuditActions.NextWaveReconciled
                && item.Reason == "reconcile_exhausted"
                && item.TargetId == task.Id));
        }
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(1, host.Provider.StateCalls);
    }

    [Fact]
    public async Task Twenty_one_cross_wave_launch_candidates_never_exceed_the_shared_twenty_slot_provider_cap()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchBatchesPerCycle = 32;
            options.MaxTasksPerWave = 1;
        });
        await host.SeedGlobalSlotContendersAsync(21);

        var result = await host.LaunchAsync();
        Assert.Equal(20, result.TasksLaunched);
        Assert.Equal(20, host.Provider.LaunchCalls);
        Assert.All(host.Provider.Requests, request =>
            Assert.StartsWith(AutopilotGlobalTaskSlotIdentity.IdempotencyKeyPrefix, request.IdempotencyKey));

        using (var db = host.CreateContext())
        {
            var tasks = await db.AutopilotWaveLaunchTasks.ToListAsync();
            Assert.Equal(20, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
            var denied = Assert.Single(tasks, item => item.Status == AutopilotWaveLaunchStatuses.Planned);
            Assert.Equal(0, denied.LaunchAttempt);
            var deniedBacklog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == denied.BacklogItemKey);
            Assert.Null(deniedBacklog.ConsumedByWaveKey);
        }

        // A subsequent cycle cannot turn the capacity denial into a provider call.
        await host.LaunchAsync();
        Assert.Equal(20, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Verified_completion_prioritizes_pending_capacity_rows_and_refills_exactly_one_slot()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchBatchesPerCycle = 32;
            options.MaxTasksPerWave = 1;
        });
        await host.SeedGlobalSlotContendersAsync(25);

        var initial = await host.LaunchAsync();
        Assert.Equal(20, initial.TasksLaunched);
        Assert.Equal(20, host.Provider.LaunchCalls);

        AutopilotWaveLaunchTask taskToComplete;
        string[] pendingTaskKeys;
        using (var db = host.CreateContext())
        {
            var tasks = await db.AutopilotWaveLaunchTasks.ToListAsync();
            Assert.Equal(20, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
            Assert.Equal(5, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Planned));
            taskToComplete = tasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .First();
            pendingTaskKeys = tasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Planned)
                .Select(item => item.TaskKey)
                .ToArray();

            var taskByRunId = tasks.ToDictionary(item => item.SourceRunId);
            var runs = await db.AutopilotRuns.OrderBy(item => item.WaveKey).ToListAsync();
            var oldest = host.Time.GetUtcNow().UtcDateTime;
            var oldIndex = 0;
            foreach (var run in runs.Where(item => taskByRunId[item.Id].Status != AutopilotWaveLaunchStatuses.Planned))
                run.UpdatedAt = oldest.AddSeconds(oldIndex++);
            var pendingIndex = 100;
            foreach (var run in runs.Where(item => taskByRunId[item.Id].Status == AutopilotWaveLaunchStatuses.Planned))
                run.UpdatedAt = oldest.AddSeconds(pendingIndex++);
            await db.SaveChangesAsync();
        }

        const string eventId = "test-capacity-refill-verified-completion";
        var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            taskToComplete.ExternalTaskId,
            "stopped",
            "finish",
            false,
            eventId));
        var submitted = await host.SubmitSignedCompletionAsync(
            taskToComplete.WaveKey,
            taskToComplete.TaskKey,
            expectedTaskCount: 1,
            evidence: receipt,
            externalEventId: eventId);
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);
        using (var db = host.CreateContext())
        {
            var terminalEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == eventId);
            Assert.True(terminalEvent.SignatureVerified);
            Assert.True(terminalEvent.ReplayProtected);
            terminalEvent.Status = AutopilotEventStatuses.Processed;
            terminalEvent.ProcessedAt = host.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        var replacementCycle = await host.LaunchAsync();
        Assert.Equal(1, replacementCycle.TasksLaunched);
        Assert.Equal(21, host.Provider.LaunchCalls);

        using (var db = host.CreateContext())
        {
            var tasks = await db.AutopilotWaveLaunchTasks.ToListAsync();
            Assert.Equal(20, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launching
                || item.Status == AutopilotWaveLaunchStatuses.Launched));
            Assert.Equal(1, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Completed));
            Assert.Equal(4, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Planned));
            var replacement = Assert.Single(tasks, item => pendingTaskKeys.Contains(item.TaskKey)
                && item.Status == AutopilotWaveLaunchStatuses.Launched);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == replacement.BacklogItemKey);
            Assert.True(backlog.Approved);
            Assert.Equal(replacement.WaveKey, backlog.ConsumedByWaveKey);
            Assert.All(tasks.Where(item => item.Status == AutopilotWaveLaunchStatuses.Planned), item =>
                Assert.Equal(0, item.LaunchAttempt));
        }
    }

    [Fact]
    public async Task Concurrent_service_workers_never_call_provider_for_more_than_twenty_shared_slots_when_postgres_is_available()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var schemaName = $"next_wave_worker_test_{Guid.NewGuid():N}";
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
            var contextOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            using var host = new Host(options =>
            {
                options.DryRun = false;
                options.SimulationMode = false;
                options.AllowNextWaveLaunch = true;
                options.AllowNextWaveLaunchWhenReleasePending = true;
                options.MaxLaunchBatchesPerCycle = 32;
                options.MaxTasksPerWave = 1;
            }, databaseOptions: contextOptions);
            await host.SeedGlobalSlotContendersAsync(21);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.LaunchAsync()));

            var providerCalls = host.Provider.LaunchCalls;
            Assert.InRange(providerCalls, 1, AutopilotGlobalTaskSlotAllocator.HardMaximum);
            using var verify = host.CreateContext();
            var active = await verify.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launching
                    || item.Status == AutopilotWaveLaunchStatuses.Launched)
                .ToListAsync();
            Assert.True(active.Count <= AutopilotGlobalTaskSlotAllocator.HardMaximum);
            Assert.Equal(providerCalls, active.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
            Assert.Equal(active.Count, active.Select(item => item.BacklogItemKey).Distinct().Count());
            foreach (var task in active)
            {
                var backlogItem = await verify.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == task.BacklogItemKey);
                Assert.Equal(task.WaveKey, backlogItem.ConsumedByWaveKey);
            }
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
    public async Task Live_launch_fails_closed_when_slot_allocator_or_authoritative_inventory_is_missing()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, enableGlobalSlotServices: false);
        await host.AddBacklogAsync("bl-no-slot-service", "Do not call provider without global capacity service");
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GreenRunAsync("wave-no-slot-service"));

        using (var db = host.CreateContext())
        {
            Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
        }
        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Mis_scoped_inventory_never_grants_capacity_or_calls_provider()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        host.InventorySource.PageResolver = (_, _) =>
            new AutopilotGlobalTaskInventoryPage("other-project-scope", Array.Empty<AutopilotGlobalTaskInventoryItem>(), null);
        await host.AddBacklogAsync("bl-wrong-scope", "Do not call provider with mis-scoped inventory");
        await host.GreenRunAsync("wave-wrong-scope");

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal(0, task.LaunchAttempt);
            Assert.Equal("inventory_scope_mismatch", task.ReasonCode);
        }
        Assert.Equal(0, host.Provider.LaunchCalls);
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
    public async Task Ambiguous_bridge_outcome_retains_slot_and_never_retries_without_verified_idempotency()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchAttempts = 3;
        });
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Unavailable("bridge_timeout"));

        await host.GreenRunAsync("wave-flaky");
        Assert.Equal(1, host.Provider.LaunchCalls);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
            Assert.Equal(1, task.LaunchAttempt);
            Assert.Equal("global_slot_reserved", task.ReasonCode);
            Assert.Null(task.ExternalTaskId);
        }

        host.Time.Advance(TimeSpan.FromHours(1));
        await host.LaunchAsync();
        await host.ReconcileLaunchesAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
            Assert.Equal("global_slot_reserved", task.ReasonCode);
            Assert.Equal(1, task.LaunchAttempt);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, (await db.AutopilotWaveLaunchBatches.SingleAsync()).Status);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.NextWaveLaunchBlocked
                    && item.StatusDetail == "global_slot_retained_until_authoritative_reconciliation");
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.HumanDecisionRequired
                    && item.Reason == AutopilotHumanDecisions.RepairEscalation
                    && item.StatusDetail == "ambiguous_provider_outcome_requires_authoritative_reconciliation");
        }

        // Time and repeated cycles cannot free the lease or duplicate the task.
        host.Time.Advance(TimeSpan.FromDays(2));
        await host.LaunchAsync();
        await host.LaunchAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Bridge_http_502_task_creation_ambiguous_holds_slot_and_never_retries_create()
    {
        const string tokenVariable = "TASLIM_EVENT_REFILL_TEST_BRIDGE_TOKEN";
        var previousToken = Environment.GetEnvironmentVariable(tokenVariable);
        Environment.SetEnvironmentVariable(tokenVariable, "synthetic-test-bridge-token");
        try
        {
            const string bridgeBaseUrl = "https://bridge.example.test";
            var providerSettings = new AutopilotOptions
            {
                Enabled = true,
                DryRun = false,
                AllowNextWaveLaunch = true,
                BridgeBaseUrl = bridgeBaseUrl,
                BridgeTokenEnvironmentVariable = tokenVariable,
            };
            providerSettings.Normalize();
            var handler = new StubBridgeHandler
            {
                Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("{\"error\":\"task_creation_ambiguous\"}"),
                },
            };
            using var http = new HttpClient(handler);
            var bridge = new ManusBridgeWaveLaunchProvider(http, providerSettings);
            Assert.True(bridge.IsConfigured);

            using var host = new Host(options =>
            {
                options.DryRun = false;
                options.SimulationMode = false;
                options.AllowNextWaveLaunch = true;
                options.AllowNextWaveLaunchWhenReleasePending = true;
                options.BridgeBaseUrl = bridgeBaseUrl;
                options.BridgeTokenEnvironmentVariable = tokenVariable;
            }, launchProviderOverride: bridge);
            await host.AddBacklogAsync("bl-ambiguous-502", "Do not duplicate an uncertain Manus task");

            await host.GreenRunAsync("wave-ambiguous-502");
            Assert.Equal(1, handler.Requests.Count(item => item.Method == HttpMethod.Post));

            string lateWaveKey;
            string lateTaskKey;
            using (var db = host.CreateContext())
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
                lateWaveKey = task.WaveKey;
                lateTaskKey = task.TaskKey;
                Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
                Assert.Equal(1, task.LaunchAttempt);
                Assert.Equal("global_slot_reserved", task.ReasonCode);
                Assert.Null(task.ExternalTaskId);
                var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-ambiguous-502");
                Assert.Equal(task.WaveKey, backlog.ConsumedByWaveKey);
            }

            // A late signed terminal callback cannot supply the missing durable
            // native-ID mapping after an ambiguous create; keep the reservation.
            const string lateNativeTaskId = "1234567890123456789012";
            const string lateEventId = "late-receipt-after-ambiguous-502";
            var lateReceipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
                AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
                lateNativeTaskId,
                "stopped",
                "finish",
                false,
                lateEventId));
            var lateSubmission = await host.SubmitSignedCompletionAsync(
                lateWaveKey,
                lateTaskKey,
                expectedTaskCount: 1,
                evidence: lateReceipt,
                externalEventId: lateEventId);
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, lateSubmission.Outcome);
            await host.RunCycleAsync();

            using (var db = host.CreateContext())
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
                Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
                Assert.Equal(1, task.LaunchAttempt);
                Assert.Equal("global_slot_reserved", task.ReasonCode);
                Assert.Null(task.ExternalTaskId);
                var lateEvent = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == lateEventId);
                Assert.Equal(AutopilotEventStatuses.Processed, lateEvent.Status);
                Assert.Equal(lateEventId, lateEvent.ExternalEventId);
            }
            Assert.Single(handler.Requests);

            var duplicateLateEvent = await host.SubmitSignedCompletionAsync(
                lateWaveKey,
                lateTaskKey,
                expectedTaskCount: 1,
                evidence: lateReceipt,
                externalEventId: lateEventId);
            Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicateLateEvent.Outcome);
            await host.RunCycleAsync();

            host.Time.Advance(TimeSpan.FromHours(1));
            await host.LaunchAsync();
            await host.ReconcileLaunchesAsync();
            host.Time.Advance(TimeSpan.FromDays(2));
            await host.LaunchAsync();

            Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
            using (var db = host.CreateContext())
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
                Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
                Assert.Equal(1, task.LaunchAttempt);
                Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                    item.Action == AutopilotAuditActions.HumanDecisionRequired
                    && item.Reason == AutopilotHumanDecisions.RepairEscalation);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, previousToken);
        }
    }

    [Fact]
    public async Task Definitive_provider_rejection_releases_only_its_backlog_claim()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-definitive-rejection", "Retry only after a definitive no-create response");
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Rejected("bridge_status_400"));

        await host.GreenRunAsync("wave-definitive-rejection");

        using var db = host.CreateContext();
        var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Failed, task.Status);
        Assert.Equal("bridge_status_400", task.ReasonCode);
        var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-definitive-rejection");
        Assert.Null(backlog.ConsumedByWaveKey);
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
            item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.RepairEscalation);
    }

    [Fact]
    public async Task Restart_recovers_backlog_claim_release_after_durable_provider_rejection_without_provider_call()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.MaxLaunchAttempts = 3;
        });
        await host.SeedGlobalSlotContendersAsync(2);

        Guid rejectedTaskId;
        string rejectedWaveKey;
        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "cap-task-00");
            rejectedTaskId = rejected.Id;
            rejectedWaveKey = rejected.WaveKey;
            rejected.Status = AutopilotWaveLaunchStatuses.Launching;
            rejected.LaunchAttempt = 1;
            rejected.MaxLaunchAttempts = 3;
            rejected.ReasonCode = "global_slot_reserved";
            rejected.LastStatusDetail = null;
            rejected.ExternalTaskId = null;
            rejected.CompletedAt = null;
            rejected.UpdatedAt = host.Time.GetUtcNow().UtcDateTime;
            rejected.ConcurrencyToken = Guid.NewGuid();

            var otherFailed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "cap-task-01");
            otherFailed.Status = AutopilotWaveLaunchStatuses.Failed;
            otherFailed.LaunchAttempt = 1;
            otherFailed.MaxLaunchAttempts = 1;
            otherFailed.LastStatusDetail = "unrelated_failure";

            var rejectedBacklog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00");
            rejectedBacklog.ConsumedByWaveKey = rejectedWaveKey;
            rejectedBacklog.ConsumedAt = host.Time.GetUtcNow().UtcDateTime;
            var otherBacklog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-01");
            otherBacklog.ConsumedByWaveKey = "another-wave-owner";
            otherBacklog.ConsumedAt = host.Time.GetUtcNow().UtcDateTime;

            await db.SaveChangesAsync();
        }

        var rejectedResult = await host.RecordRejectedAsync(rejectedTaskId, "bridge_status_400");
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, rejectedResult.Outcome);
        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rejectedTaskId);
            Assert.Equal(AutopilotWaveLaunchStatuses.Failed, rejected.Status);
            Assert.Equal("provider_rejected_before_task_acceptance", rejected.LastStatusDetail);
            Assert.Equal(rejectedWaveKey,
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00")).ConsumedByWaveKey);
        }

        // The allocator's durable rejection has committed, but the service-side
        // backlog release has not yet run: this is the process-crash boundary.
        await host.LaunchAsync();
        await host.LaunchAsync();

        using (var db = host.CreateContext())
        {
            var recovered = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rejectedTaskId);
            Assert.Equal(AutopilotWaveLaunchStatuses.Failed, recovered.Status);
            Assert.Equal(1, recovered.LaunchAttempt);
            Assert.Equal("provider_rejected_claim_released", recovered.LastStatusDetail);
            Assert.Null((await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00")).ConsumedByWaveKey);
            Assert.Equal("another-wave-owner", (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-01")).ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.Reason == AutopilotHumanDecisions.RepairEscalation
                && item.StatusDetail == "deterministic_rejection_claim_recovered_after_interruption");
        }

        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Rejected_claim_recovery_keeps_marker_and_escalates_if_backlog_owner_changed()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.MaxLaunchAttempts = 3;
        });
        await host.SeedGlobalSlotContendersAsync(2);

        Guid rejectedTaskId;
        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "cap-task-00");
            rejectedTaskId = rejected.Id;
            rejected.Status = AutopilotWaveLaunchStatuses.Launching;
            rejected.LaunchAttempt = 1;
            rejected.MaxLaunchAttempts = 3;
            rejected.ExternalTaskId = null;
            rejected.ConcurrencyToken = Guid.NewGuid();

            var otherFailed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.TaskKey == "cap-task-01");
            otherFailed.Status = AutopilotWaveLaunchStatuses.Failed;
            otherFailed.LaunchAttempt = 1;
            otherFailed.MaxLaunchAttempts = 1;

            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00");
            backlog.ConsumedByWaveKey = "another-wave-owner";
            backlog.ConsumedAt = host.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        var rejectedResult = await host.RecordRejectedAsync(rejectedTaskId, "bridge_status_400");
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, rejectedResult.Outcome);
        await host.LaunchAsync();
        await host.LaunchAsync();

        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rejectedTaskId);
            Assert.Equal(AutopilotWaveLaunchStatuses.Failed, rejected.Status);
            Assert.Equal(1, rejected.LaunchAttempt);
            Assert.Equal("provider_rejected_before_task_acceptance", rejected.LastStatusDetail);
            Assert.Equal("another-wave-owner",
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00")).ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.Reason == AutopilotHumanDecisions.RepairEscalation
                && item.StatusDetail == "deterministic_rejection_backlog_claim_not_owned; recovery_marker_retained"
                && item.TargetId == rejectedTaskId);
        }

        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Restart_recovery_reuses_a_stale_launch_only_when_authoritative_inventory_matches()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-1", "Add workspace audit export");
        await host.GreenRunAsync("wave-restart");
        Assert.Equal(1, host.Provider.LaunchCalls);

        string idempotencyKey;
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status);
            Assert.Equal(1, task.LaunchAttempt);
            idempotencyKey = host.Provider.Requests.Single().IdempotencyKey;
            // Simulate a crash after the provider created its task but before its ID
            // was persisted. The exact provider ID is recovered from scoped inventory.
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.ReasonCode = null;
            task.LaunchedAt = null;
            task.ExternalTaskId = null;
            task.UpdatedAt = host.Time.GetUtcNow().UtcDateTime.AddHours(-1);
            task.NextReconcileAt = null;
            await db.SaveChangesAsync();
        }

        host.InventorySource.PageResolver = (scopeId, _) => new AutopilotGlobalTaskInventoryPage(
            scopeId,
            [new AutopilotGlobalTaskInventoryItem("recovered-test-task-id", scopeId, "running", idempotencyKey)],
            null);
        host.Time.Advance(TimeSpan.FromHours(1));

        var reconcile = await host.ReconcileLaunchesAsync();
        Assert.Equal(1, reconcile.BatchesExamined);
        Assert.Equal(1, host.Provider.LaunchCalls);
        DateTime scheduledPollAt;

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status);
            Assert.Equal("recovered-test-task-id", task.ExternalTaskId);
            Assert.Equal("recovered_from_authoritative_inventory", task.ReasonCode);
            Assert.NotNull(task.NextReconcileAt);
            var now = host.Time.GetUtcNow().UtcDateTime;
            var expectedBackoff = AutopilotNextWavePolicy.ComputeBackoff(1, host.Options);
            scheduledPollAt = task.NextReconcileAt!.Value;
            Assert.Equal(now.Add(expectedBackoff), scheduledPollAt);
            Assert.InRange(scheduledPollAt - now, TimeSpan.Zero, expectedBackoff);
            Assert.Equal("recovered_from_authoritative_inventory;bounded_poll_scheduled", task.LastStatusDetail);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(),
                item => item.Action == AutopilotAuditActions.NextWaveReconciled
                    && item.StatusDetail == "reservation_retained_no_provider_retry");
        }
        Assert.Equal(0, host.Provider.StateCalls);

        host.Time.Advance(scheduledPollAt - host.Time.GetUtcNow().UtcDateTime);
        await host.ReconcileLaunchesAsync();
        Assert.Equal(1, host.Provider.StateCalls);
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(1, task.ReconcileCount);
            Assert.NotNull(task.NextReconcileAt);
            Assert.True(task.NextReconcileAt > scheduledPollAt);
        }
    }

    [Fact]
    public async Task Restart_recovery_exhausted_poll_budget_persists_one_operator_escalation()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-reconcile-exhausted", "Recover a held launch with exhausted polling budget");
        await host.GreenRunAsync("wave-reconcile-exhausted");

        string idempotencyKey;
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            idempotencyKey = host.Provider.Requests.Single().IdempotencyKey;
            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.ReasonCode = null;
            task.LaunchedAt = null;
            task.ExternalTaskId = null;
            task.ReconcileCount = task.MaxReconciles;
            task.UpdatedAt = host.Time.GetUtcNow().UtcDateTime.AddHours(-1);
            task.NextReconcileAt = null;
            await db.SaveChangesAsync();
        }

        host.InventorySource.PageResolver = (scopeId, _) => new AutopilotGlobalTaskInventoryPage(
            scopeId,
            [new AutopilotGlobalTaskInventoryItem("recovered-exhausted-task", scopeId, "running", idempotencyKey)],
            null);
        host.Time.Advance(TimeSpan.FromHours(1));

        await host.ReconcileLaunchesAsync();
        await host.ReconcileLaunchesAsync();

        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, task.Status);
            Assert.Null(task.NextReconcileAt);
            Assert.Equal("recovered_from_authoritative_inventory;polling_exhausted;repair_required", task.LastStatusDetail);
            var escalations = await db.AutopilotAuditEvents.CountAsync(item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.Reason == AutopilotHumanDecisions.RepairEscalation
                && item.StatusDetail == "inventory_recovered_launch_polling_exhausted; verified_receipt_or_operator_repair_required"
                && item.TargetId == task.Id);
            Assert.Equal(1, escalations);
        }
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(0, host.Provider.StateCalls);
    }

    [Fact]
    public async Task Concurrent_repair_escalation_requests_across_contexts_persist_once()
    {
        using var host = new Host();
        var targetId = Guid.NewGuid();

        var inserted = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => host.AddRepairEscalationAsync(
                "concurrent_repair_escalation_test",
                "wave-repair-dedup",
                "task-repair-dedup",
                targetId)));

        Assert.Single(inserted, value => value);
        using var db = host.CreateContext();
        Assert.Equal(1, await db.AutopilotAuditEvents.CountAsync(item =>
            item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.RepairEscalation
            && item.StatusDetail == "concurrent_repair_escalation_test"
            && item.TargetId == targetId));
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
    public async Task Duplicate_plans_cannot_launch_the_same_backlog_item_twice()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchBatchesPerCycle = 2;
        });
        await host.SeedDuplicatePlansAsync();

        await host.LaunchAsync();

        using var db = host.CreateContext();
        var backlog = await db.AutopilotBacklogItems.SingleAsync();
        var tasks = await db.AutopilotWaveLaunchTasks.OrderBy(item => item.TaskKey).ToListAsync();

        // This fixture models two workers that planned the same item before either
        // reached the provider boundary. Only the atomic claim winner may launch.
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(1, tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
        var launchWinner = Assert.Single(tasks, item => item.Status == AutopilotWaveLaunchStatuses.Launched);
        var blocked = Assert.Single(tasks, item => item.Status == AutopilotWaveLaunchStatuses.Blocked);
        Assert.Equal("backlog_item_claimed", blocked.ReasonCode);
        Assert.Equal("backlog_item_already_claimed_by_another_wave", blocked.LastStatusDetail);
        Assert.Equal(launchWinner.WaveKey, backlog.ConsumedByWaveKey);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
            item.Action == AutopilotAuditActions.NextWaveLaunchBlocked
            && item.Reason == "backlog_item_claimed");
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
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, launchTask.Status);
            Assert.Equal("polled_succeeded", launchTask.ReasonCode);
            Assert.Contains("slot_retained_pending_verified_receipt", launchTask.LastStatusDetail);
            Assert.Equal(launchTask.WaveKey,
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == launchTask.BacklogItemKey)).ConsumedByWaveKey);

            var derived = await db.AutopilotEvents.SingleAsync(item =>
                item.SourceSystem == AutopilotInternalSignalSources.Polling
                && item.WaveKey == "wave-poll-next"
                && item.TaskId == launchTask.TaskKey);
            Assert.False(derived.SignatureVerified);
            Assert.False(derived.ReplayProtected);
            Assert.Equal(AutopilotEventStatuses.Queued, derived.Status);
            Assert.Equal("wave-poll-next", derived.WaveKey);
            var signalJson = derived.SignalJson ?? throw new InvalidOperationException("Poll event has no stored signal.");
            var signal = JsonSerializer.Deserialize<AutopilotCompletionSignal>(signalJson);
            Assert.NotNull(signal);
            Assert.Equal(launchTask.TaskKey, signal!.TaskId);
            Assert.Equal(AutopilotTaskOutcomes.Blocked, signal.Outcome);
            Assert.True(signal.RequiresHumanDecision);
            Assert.Equal(AutopilotHumanDecisions.RepairEscalation, signal.HumanDecisionKind);
        }

        // The unsigned poll event is retained for diagnostics only and must not
        // create or mutate a run/task, even when the provider reports success.
        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var launchTask = await db.AutopilotWaveLaunchTasks.SingleAsync();
            var derived = await db.AutopilotEvents.SingleAsync(item =>
                item.SourceSystem == AutopilotInternalSignalSources.Polling
                && item.WaveKey == "wave-poll-next"
                && item.TaskId == launchTask.TaskKey);
            Assert.Equal(AutopilotEventStatuses.Processed, derived.Status);
            Assert.Equal("polling_status_not_completion_evidence", derived.Reason);
            Assert.Empty(await db.AutopilotRuns.Where(item => item.WaveKey == "wave-poll-next").ToListAsync());
            Assert.Empty(await db.AutopilotWaveTasks.Where(item => item.WaveKey == "wave-poll-next").ToListAsync());
            var expectedRefillWaveKey = $"{launchTask.WaveKey}-refill-{launchTask.Id:N}";
            Assert.Empty(await db.AutopilotWaveLaunchBatches.Where(item =>
                item.SourceRunId == launchTask.SourceRunId
                && item.SourceWaveKey == launchTask.SourceWaveKey
                && item.WaveKey == expectedRefillWaveKey).ToListAsync());
        }
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Queued_unsigned_poll_observation_does_not_block_verified_receipt_refill()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxTasksPerWave = 2;
        });
        await host.AddBacklogAsync("bl-poll-receipt-1", "First active task", priority: 1);
        await host.AddBacklogAsync("bl-poll-receipt-2", "Second active sibling", priority: 2);
        await host.AddBacklogAsync("bl-poll-receipt-3", "Approved replacement", priority: 3);
        await host.GreenRunAsync("wave-poll-then-receipt");

        List<AutopilotWaveLaunchTask> launched;
        using (var db = host.CreateContext())
        {
            launched = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .ToListAsync();
        }
        Assert.Equal(2, launched.Count);
        var completedTask = launched[0];
        var siblingTask = launched[1];
        const string eventId = "test-poll-then-receipt-completion";

        host.Provider.StateResolver = _ => new WaveLaunchTaskState(
            WaveLaunchTaskStates.Succeeded, IsTerminal: true, IsSuccess: true, ExternalRef: "poll-ref", StatusDetail: "synthetic poll success");
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == completedTask.Id);
            task.NextReconcileAt = host.Time.GetUtcNow().UtcDateTime.AddSeconds(-1);
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var poll = await host.ReconcileLaunchesAsync();
        Assert.Equal(1, poll.TasksReconciled);
        using (var db = host.CreateContext())
        {
            var queuedPoll = await db.AutopilotEvents.SingleAsync(item =>
                item.SourceSystem == AutopilotInternalSignalSources.Polling
                && item.WaveKey == completedTask.WaveKey
                && item.TaskId == completedTask.TaskKey);
            Assert.Equal(AutopilotEventStatuses.Queued, queuedPoll.Status);
            Assert.False(queuedPoll.SignatureVerified);
            Assert.False(queuedPoll.ReplayProtected);
        }

        var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            completedTask.ExternalTaskId,
            "stopped",
            "finish",
            false,
            eventId));
        var submitted = await host.SubmitSignedCompletionAsync(
            completedTask.WaveKey,
            completedTask.TaskKey,
            expectedTaskCount: 2,
            evidence: receipt,
            externalEventId: eventId);
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);
        var persistedEventId = submitted.EventId ?? throw new InvalidOperationException("Accepted receipt has no persisted event ID.");

        await host.RunCycleAsync();

        using (var db = host.CreateContext())
        {
            var pollEvent = await db.AutopilotEvents.SingleAsync(item =>
                item.SourceSystem == AutopilotInternalSignalSources.Polling
                && item.WaveKey == completedTask.WaveKey
                && item.TaskId == completedTask.TaskKey);
            Assert.Equal(AutopilotEventStatuses.Processed, pollEvent.Status);
            Assert.Equal("polling_status_not_completion_evidence", pollEvent.Reason);
            var terminalEvent = await db.AutopilotEvents.SingleAsync(item => item.Id == persistedEventId);
            Assert.Equal(eventId, terminalEvent.ExternalEventId);
            Assert.Equal("synthetic-completion-test", terminalEvent.SourceSystem);
            Assert.Equal(completedTask.WaveKey, terminalEvent.WaveKey);
            Assert.Equal(completedTask.TaskKey, terminalEvent.TaskId);
            Assert.Equal(AutopilotEventStatuses.Processed, terminalEvent.Status);
            Assert.True(terminalEvent.SignatureVerified);
            Assert.True(terminalEvent.ReplayProtected);
            var signalJson = terminalEvent.SignalJson ?? throw new InvalidOperationException("Terminal event has no stored signal.");
            using var signalDocument = JsonDocument.Parse(signalJson);
            var evidenceJson = signalDocument.RootElement.GetProperty("evidence").GetString()
                ?? throw new InvalidOperationException("Terminal event has no receipt evidence.");
            using var receiptDocument = JsonDocument.Parse(evidenceJson);
            Assert.Equal(eventId, receiptDocument.RootElement.GetProperty("event_id").GetString());

            var completed = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == completedTask.Id);
            Assert.Equal(AutopilotWaveLaunchStatuses.Completed, completed.Status);
            var activeSibling = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == siblingTask.Id);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, activeSibling.Status);
            var expectedRefillWaveKey = $"{completedTask.WaveKey}-refill-{completedTask.Id:N}";
            var refillBatches = await db.AutopilotWaveLaunchBatches
                .Where(item => item.SourceRunId == completedTask.SourceRunId
                    && item.SourceWaveKey == completedTask.SourceWaveKey
                    && item.WaveKey == expectedRefillWaveKey)
                .ToListAsync();
            Assert.Single(refillBatches);
            Assert.Equal(1, refillBatches[0].TaskCount);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, refillBatches[0].Status);
            var replacement = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.BatchId == refillBatches[0].Id);
            Assert.Equal("bl-poll-receipt-3", replacement.BacklogItemKey);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, replacement.Status);
            var replacementBacklog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-poll-receipt-3");
            Assert.True(replacementBacklog.Approved);
            Assert.Equal(refillBatches[0].WaveKey, replacementBacklog.ConsumedByWaveKey);
        }

        Assert.Equal(3, host.Provider.LaunchCalls);
        var duplicate = await host.SubmitSignedCompletionAsync(
            completedTask.WaveKey,
            completedTask.TaskKey,
            expectedTaskCount: 2,
            evidence: receipt,
            externalEventId: eventId);
        Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicate.Outcome);
        Assert.Equal(persistedEventId, duplicate.EventId);
        await host.RunCycleAsync();
        Assert.Equal(3, host.Provider.LaunchCalls);
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

            const string nativeTaskId = "ABCDEFGHIJKLMNOPQRSTUV";
            handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ok = true, task_id = nativeTaskId })),
            };
            var launched = await provider.LaunchTaskAsync(request);
            Assert.True(launched.IsLaunched);
            Assert.Equal(nativeTaskId, launched.ExternalTaskId);

            // The credential is only ever sent as an Authorization header.
            var recorded = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, recorded.Method);
            Assert.Equal("https://bridge.example.test/v1/tasks", recorded.Uri);
            Assert.Equal($"Bearer {sentinel}", recorded.Authorization);
            Assert.Equal(request.IdempotencyKey, recorded.IdempotencyKey);
            using var requestBody = JsonDocument.Parse(recorded.Body);
            Assert.Equal(request.WaveKey, requestBody.RootElement.GetProperty("waveKey").GetString());
            Assert.Equal(request.TaskKey, requestBody.RootElement.GetProperty("taskKey").GetString());
            Assert.Equal(request.IdempotencyKey, requestBody.RootElement.GetProperty("idempotencyKey").GetString());
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
        private readonly object gate = new();
        private readonly List<WaveLaunchProviderRequest> requests = [];
        private int launchCalls;
        private int stateCalls;

        public string ProviderKey => "fake-bridge";

        public bool IsConfigured { get; set; } = true;

        public int LaunchCalls => Volatile.Read(ref launchCalls);

        public int StateCalls => Volatile.Read(ref stateCalls);

        public IReadOnlyList<WaveLaunchProviderRequest> Requests
        {
            get { lock (gate) return requests.ToArray(); }
        }

        public Queue<WaveLaunchProviderResult> Results { get; } = new();

        public Func<string, WaveLaunchTaskState?> StateResolver { get; set; } = _ => null;

        public Func<WaveLaunchProviderRequest, string, CancellationToken, Task>? BeforeReturnAsync { get; set; }

        public Func<string, CancellationToken, Task>? BeforeStateReturnAsync { get; set; }

        public async Task<WaveLaunchProviderResult> LaunchTaskAsync(WaveLaunchProviderRequest request, CancellationToken cancellationToken = default)
        {
            WaveLaunchProviderResult result;
            lock (gate)
            {
                launchCalls++;
                requests.Add(request);
                result = Results.Count > 0
                    ? Results.Dequeue()
                    : WaveLaunchProviderResult.Launched($"ext-{request.TaskKey}");
            }

            if (BeforeReturnAsync is not null && result.IsLaunched && result.ExternalTaskId is not null)
                await BeforeReturnAsync(request, result.ExternalTaskId, cancellationToken);
            return result;
        }

        public async Task<WaveLaunchTaskState?> GetTaskStateAsync(string externalTaskId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref stateCalls);
            if (BeforeStateReturnAsync is not null)
                await BeforeStateReturnAsync(externalTaskId, cancellationToken);
            return StateResolver(externalTaskId);
        }
    }

    private sealed class FakeGlobalTaskInventoryPageSource : IAutopilotGlobalTaskInventoryPageSource
    {
        public Func<string, string?, AutopilotGlobalTaskInventoryPage?> PageResolver { get; set; } =
            (scopeId, _) => new AutopilotGlobalTaskInventoryPage(scopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), null);

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PageResolver(scopeId, cursor));
    }

    private sealed class Host : IDisposable
    {
        private const string TestTaslimScopeId = "taslim-autopilot-test-scope";
        private readonly SqliteConnection? connection;
        private readonly DbContextOptions<TaslimDbContext> contextOptions;
        private readonly bool enableGlobalSlotServices;
        private readonly IWaveLaunchProvider? launchProviderOverride;

        public Host(
            Action<AutopilotOptions>? configure = null,
            bool enableGlobalSlotServices = true,
            DbContextOptions<TaslimDbContext>? databaseOptions = null,
            IWaveLaunchProvider? launchProviderOverride = null)
        {
            this.enableGlobalSlotServices = enableGlobalSlotServices;
            this.launchProviderOverride = launchProviderOverride;
            if (databaseOptions is null)
            {
                connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                contextOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                    .UseSqlite(connection)
                    .Options;
            }
            else
            {
                contextOptions = databaseOptions;
            }
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

        public FakeGlobalTaskInventoryPageSource InventorySource { get; } = new();

        public TaslimDbContext CreateContext() => new(contextOptions);

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

        public async Task<AutopilotGlobalTaskSlotResult> RecordRejectedAsync(Guid launchTaskId, string reasonCode)
        {
            using var db = CreateContext();
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == launchTaskId);
            var idempotencyKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            return await new AutopilotGlobalTaskSlotAllocator(db, TestTaslimScopeId, Time)
                .RecordRejectedAsync(task.Id, idempotencyKey, reasonCode);
        }

        public async Task<bool> AddRepairEscalationAsync(
            string statusDetail,
            string waveKey,
            string taskKey,
            Guid targetId)
        {
            using var db = CreateContext();
            var service = CreateNextWaveService(db);
            var method = typeof(AutopilotNextWaveService).GetMethod(
                "AddRepairEscalationOnceAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(AutopilotNextWaveService), "AddRepairEscalationOnceAsync");
            var result = method.Invoke(service,
            [
                null,
                statusDetail,
                waveKey,
                taskKey,
                1,
                (Guid?)targetId,
                false,
                CancellationToken.None,
            ]);
            return await ((Task<bool>)(result ?? throw new InvalidOperationException("Repair escalation method returned null.")));
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

        public async Task SeedGlobalSlotContendersAsync(int count)
        {
            using var db = CreateContext();
            var now = Time.GetUtcNow().UtcDateTime;
            for (var index = 0; index < count; index++)
            {
                var sourceWaveKey = $"wave-cap-{index:D2}";
                var runId = Guid.NewGuid();
                var batchId = Guid.NewGuid();
                var backlogKey = $"bl-cap-{index:D2}";
                var taskKey = $"cap-task-{index:D2}";
                db.AutopilotBacklogItems.Add(new AutopilotBacklogItem
                {
                    Id = Guid.NewGuid(),
                    ItemKey = backlogKey,
                    Title = $"Approved capacity test work item {index:D2}",
                    Kind = AutopilotBacklogItemKinds.Development,
                    Approved = true,
                    ApprovedBy = "synthetic-test-owner",
                    ApprovedAt = now,
                    ApprovalNote = "synthetic global capacity fixture",
                    Priority = index,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.AutopilotRuns.Add(new AutopilotRun
                {
                    Id = runId,
                    WaveKey = sourceWaveKey,
                    RunKey = $"run-{sourceWaveKey}",
                    State = AutopilotRunStates.HandoffPendingHuman,
                    BaseSha = BaseSha,
                    CandidateSha = CandidateSha,
                    Branch = Branch,
                    TaskCount = 1,
                    ExpectedTaskCount = 1,
                    IntegrationGatePassed = true,
                    ReleaseGatePassed = true,
                    HumanDecisionRequired = AutopilotHumanDecisions.ProductionRelease,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
                db.AutopilotWaveLaunchBatches.Add(new AutopilotWaveLaunchBatch
                {
                    Id = batchId,
                    SourceRunId = runId,
                    SourceWaveKey = sourceWaveKey,
                    WaveKey = $"{sourceWaveKey}-next",
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    BaseSha = CandidateSha,
                    Branch = Branch,
                    PlanFingerprint = $"synthetic-cap-plan-{index}",
                    TaskCount = 1,
                    MaxLaunchAttempts = 3,
                    MaxReconciles = 3,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
                db.AutopilotWaveLaunchTasks.Add(new AutopilotWaveLaunchTask
                {
                    Id = Guid.NewGuid(),
                    BatchId = batchId,
                    SourceRunId = runId,
                    SourceWaveKey = sourceWaveKey,
                    WaveKey = $"{sourceWaveKey}-next",
                    TaskKey = taskKey,
                    Title = $"Approved capacity test work item {index:D2}",
                    BacklogItemKey = backlogKey,
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    Branch = Branch,
                    BaseSha = CandidateSha,
                    MaxLaunchAttempts = 3,
                    MaxReconciles = 3,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task<AutopilotWaveLaunchTask> ReadSingleLaunchTaskAsync(string waveKey)
        {
            using var db = CreateContext();
            return await db.AutopilotWaveLaunchTasks
                .Where(item => item.WaveKey == waveKey)
                .OrderBy(item => item.TaskKey)
                .FirstAsync();
        }

        public async Task<AutopilotIntakeResult> SubmitSignedCompletionAsync(
            string waveKey,
            string taskId,
            int expectedTaskCount,
            string evidence,
            string externalEventId)
        {
            const string secret = "test-only-autopilot-event-signing-key";
            var secretVariable = $"TASLIM_TEST_AUTOPILOT_SECRET_{Guid.NewGuid():N}";
            var previous = Environment.GetEnvironmentVariable(secretVariable);
            var previousSecretVariable = Options.SigningSecretEnvironmentVariable;
            var previousRequireSigned = Options.RequireSignedEvents;
            Options.SigningSecretEnvironmentVariable = secretVariable;
            Options.RequireSignedEvents = true;
            Environment.SetEnvironmentVariable(secretVariable, secret);
            try
            {
                var payload = JsonSerializer.Serialize(new AutopilotCompletionSignal
                {
                    WaveKey = waveKey,
                    TaskId = taskId,
                    Outcome = AutopilotTaskOutcomes.Succeeded,
                    Branch = Branch,
                    BaseSha = CandidateSha,
                    CandidateSha = CandidateSha,
                    Attempt = 1,
                    Evidence = evidence,
                    Checks = CompleteChecks(),
                    ExpectedTaskCount = expectedTaskCount,
                    WaveComplete = false,
                });
                var timestamp = Time.GetUtcNow().ToUnixTimeSeconds().ToString();
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"))).ToLowerInvariant();
                using var db = CreateContext();
                return await CreateIntake(db).SubmitAsync(new AutopilotIntakeRequest(
                    $"sha256={signature}",
                    timestamp,
                    payload,
                    "synthetic-completion-test",
                    externalEventId,
                    AutopilotEventTypes.WaveTaskCompleted));
            }
            finally
            {
                Environment.SetEnvironmentVariable(secretVariable, previous);
                Options.SigningSecretEnvironmentVariable = previousSecretVariable;
                Options.RequireSignedEvents = previousRequireSigned;
            }
        }

        public async Task CompleteLaunchBeforeAcknowledgementAsync(
            WaveLaunchProviderRequest request,
            string externalTaskId,
            CancellationToken cancellationToken)
        {
            Guid launchTaskId;
            string idempotencyKey;
            using (var db = CreateContext())
            {
                var task = await db.AutopilotWaveLaunchTasks.SingleAsync(item =>
                    item.WaveKey == request.WaveKey && item.TaskKey == request.TaskKey, cancellationToken);
                launchTaskId = task.Id;
                idempotencyKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
                var accepted = await new AutopilotGlobalTaskSlotAllocator(db, TestTaslimScopeId, Time)
                    .RecordAcceptedAsync(task.Id, idempotencyKey, externalTaskId, cancellationToken);
                Assert.True(accepted.SlotHeld);
            }

            var eventId = $"ack-race-{request.TaskKey}";
            var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
                AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
                externalTaskId,
                "stopped",
                "finish",
                false,
                eventId));
            var submitted = await SubmitSignedCompletionAsync(
                request.WaveKey,
                request.TaskKey,
                expectedTaskCount: 1,
                evidence: receipt,
                externalEventId: eventId);
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, submitted.Outcome);

            Guid terminalEventId;
            using (var db = CreateContext())
            {
                var terminalEvent = await db.AutopilotEvents.SingleAsync(item =>
                    item.ExternalEventId == eventId, cancellationToken);
                Assert.True(terminalEvent.SignatureVerified);
                Assert.True(terminalEvent.ReplayProtected);
                terminalEvent.Status = AutopilotEventStatuses.Processed;
                terminalEvent.ProcessedAt = Time.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(cancellationToken);
                terminalEventId = terminalEvent.Id;
            }

            using (var db = CreateContext())
            {
                var completed = await new AutopilotGlobalTaskSlotAllocator(db, TestTaslimScopeId, Time)
                    .CompleteVerifiedAsync(
                        launchTaskId,
                        idempotencyKey,
                        externalTaskId,
                        terminalEventId,
                        cancellationToken);
                Assert.Equal(AutopilotGlobalTaskSlotOutcomes.Completed, completed.Outcome);
            }
        }

        public async Task SeedDuplicatePlansAsync()
        {
            using var db = CreateContext();
            var now = Time.GetUtcNow().UtcDateTime;
            var backlog = new AutopilotBacklogItem
            {
                Id = Guid.NewGuid(),
                ItemKey = "bl-duplicate-plan",
                Title = "Add workspace audit export",
                Kind = AutopilotBacklogItemKinds.Development,
                Approved = true,
                ApprovedBy = "synthetic-test-owner",
                ApprovedAt = now,
                ApprovalNote = "synthetic duplicate-plan fixture",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AutopilotBacklogItems.Add(backlog);

            for (var index = 1; index <= 2; index++)
            {
                var sourceWaveKey = $"wave-duplicate-{index}";
                var runId = Guid.NewGuid();
                var batchId = Guid.NewGuid();
                db.AutopilotRuns.Add(new AutopilotRun
                {
                    Id = runId,
                    WaveKey = sourceWaveKey,
                    RunKey = $"run-{sourceWaveKey}",
                    State = AutopilotRunStates.HandoffPendingHuman,
                    BaseSha = BaseSha,
                    CandidateSha = CandidateSha,
                    Branch = Branch,
                    TaskCount = 1,
                    ExpectedTaskCount = 1,
                    IntegrationGatePassed = true,
                    ReleaseGatePassed = true,
                    HumanDecisionRequired = AutopilotHumanDecisions.ProductionRelease,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
                db.AutopilotWaveLaunchBatches.Add(new AutopilotWaveLaunchBatch
                {
                    Id = batchId,
                    SourceRunId = runId,
                    SourceWaveKey = sourceWaveKey,
                    WaveKey = $"{sourceWaveKey}-next",
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    BaseSha = CandidateSha,
                    Branch = Branch,
                    PlanFingerprint = $"synthetic-plan-{index}",
                    TaskCount = 1,
                    MaxLaunchAttempts = 3,
                    MaxReconciles = 3,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
                db.AutopilotWaveLaunchTasks.Add(new AutopilotWaveLaunchTask
                {
                    Id = Guid.NewGuid(),
                    BatchId = batchId,
                    SourceRunId = runId,
                    SourceWaveKey = sourceWaveKey,
                    WaveKey = $"{sourceWaveKey}-next",
                    TaskKey = $"duplicate-task-{index}",
                    Title = backlog.Title,
                    BacklogItemKey = backlog.ItemKey,
                    Status = AutopilotWaveLaunchStatuses.Planned,
                    Branch = Branch,
                    BaseSha = CandidateSha,
                    MaxLaunchAttempts = 3,
                    MaxReconciles = 3,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ConcurrencyToken = Guid.NewGuid(),
                });
            }

            await db.SaveChangesAsync();
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

        private AutopilotNextWaveService CreateNextWaveService(TaslimDbContext db)
        {
            var allocator = enableGlobalSlotServices
                ? new AutopilotGlobalTaskSlotAllocator(db, TestTaslimScopeId, Time)
                : null;
            var inventory = enableGlobalSlotServices
                ? new AutopilotGlobalTaskInventoryReader(InventorySource, TestTaslimScopeId)
                : null;
            return new(db, Options, launchProviderOverride ?? Provider, CreateIntake(db), Time, NullLogger<AutopilotNextWaveService>.Instance, allocator, inventory);
        }

        private AutopilotOrchestrator CreateOrchestrator(TaslimDbContext db) =>
            new(db, new EfAutopilotLockService(db, Options), CreateNextWaveService(db), Options, Time, NullLogger<AutopilotOrchestrator>.Instance);

        public void Dispose()
        {
            connection?.Close();
            connection?.Dispose();
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

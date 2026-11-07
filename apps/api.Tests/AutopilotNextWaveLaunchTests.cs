using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Diagnostics;
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
    private const string PostgresFenceSigningSecret = "synthetic-postgres-fence-intake-test-secret";

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
        var launchEpochs = host.Provider.Requests.Select(request => request.FencingToken).ToArray();
        Assert.All(launchEpochs, token => Assert.True(token > 0));
        Assert.Equal(launchEpochs.Length, launchEpochs.Distinct().Count());
        Assert.Equal(launchEpochs.OrderBy(token => token), launchEpochs);
        var persistedFence = await db.AutopilotLocks.SingleAsync(item =>
            item.ResourceKey == AutopilotCompletionLaunchFence.ResourceKey);
        Assert.Equal(launchEpochs[^1], persistedFence.FencingToken);

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

        using (var intakeDb = host.CreateContext())
        {
            var launched = await intakeDb.AutopilotWaveLaunchTasks.SingleAsync();
            var eventId = $"ack-race-{launched.TaskKey}";
            var receipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
                AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
                launched.ExternalTaskId!,
                "stopped",
                "finish",
                false,
                eventId));
            var acceptedAfterProviderOutcome = await host.SubmitSignedCompletionAsync(
                launched.WaveKey,
                launched.TaskKey,
                expectedTaskCount: 1,
                evidence: receipt,
                externalEventId: eventId);
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, acceptedAfterProviderOutcome.Outcome);
        }
        await host.RunCycleAsync();

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
            var fenceResource = $"autopilot:pg-fence-test:{Guid.NewGuid():N}";
            AutopilotLockHandle firstOwner;
            using (var firstDb = host.CreateContext())
            {
                firstOwner = await new EfAutopilotLockService(firstDb, host.Options)
                    .AcquireAsync(fenceResource, "pg-fence-first-owner");
            }
            Assert.True(firstOwner.IsAcquired);

            using (var expireDb = host.CreateContext())
            {
                await expireDb.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AutopilotLocks"
                    SET "ExpiresAt" = clock_timestamp() - interval '1 second'
                    WHERE "ResourceKey" = {fenceResource}
                    """);
            }

            AutopilotLockHandle secondOwner;
            using (var secondDb = host.CreateContext())
            {
                secondOwner = await new EfAutopilotLockService(secondDb, host.Options)
                    .AcquireAsync(fenceResource, "pg-fence-second-owner");
            }
            Assert.True(secondOwner.IsAcquired);
            Assert.True(secondOwner.FencingToken > firstOwner.FencingToken);

            using (var staleDb = host.CreateContext())
            {
                var staleService = new EfAutopilotLockService(staleDb, host.Options);
                Assert.False((await staleService.RenewAsync(firstOwner)).IsAcquired);
                Assert.False(await staleService.ReleaseAsync(firstOwner));
            }

            using (var currentDb = host.CreateContext())
            {
                var currentService = new EfAutopilotLockService(currentDb, host.Options);
                var renewedOwner = await currentService.RenewAsync(secondOwner);
                Assert.True(renewedOwner.IsAcquired);
                Assert.Equal(secondOwner.FencingToken, renewedOwner.FencingToken);
                Assert.True(await currentService.ReleaseAsync(renewedOwner));
            }

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
    public async Task Completion_launch_fence_is_exclusive_and_expiry_fenced_across_postgres_testhost_processes()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TASLIM_AUTOPILOT_FENCE_PROCESS_WORKER"), "1", StringComparison.Ordinal))
        {
            await RunPostgresFenceProcessWorkerAsync();
            return;
        }

        var baseConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            var runningInCi = IsTruthyEnvironmentVariable("CI") || IsTruthyEnvironmentVariable("GITHUB_ACTIONS");
            Assert.False(runningInCi, "CI must provide disposable PostgreSQL so this inter-process fence test executes rather than silently returning.");
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            var runningInCi = IsTruthyEnvironmentVariable("CI") || IsTruthyEnvironmentVariable("GITHUB_ACTIONS");
            Assert.False(runningInCi, "The PostgreSQL subprocess fence proof requires Linux process ancestry and a private 0700 temp directory.");
            return;
        }

        var schemaName = $"completion_fence_process_{Guid.NewGuid():N}";
        var adminConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        var workerDirectory = Path.Combine(Path.GetTempPath(), $"taslim-fence-process-{Guid.NewGuid():N}");
        Exception? primaryFailure = null;
        try
        {
            Directory.CreateDirectory(workerDirectory);
            File.SetUnixFileMode(workerDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var connection = new Npgsql.NpgsqlConnection(adminConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
                await command.ExecuteNonQueryAsync();
            }

            var scopedConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = schemaName,
                Pooling = false,
            }.ConnectionString;
            var contextOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedConnectionString)
                .Options;
            using var host = new Host(databaseOptions: contextOptions);
            var resourceKey = $"autopilot:completion-launch-process-test:{Guid.NewGuid():N}";

            var initialContenders = await RunPostgresFenceProcessesAsync(
                scopedConnectionString, resourceKey, "initial", 2, workerDirectory);
            Assert.Equal(2, initialContenders.Length);
            Assert.Equal(2, initialContenders.Select(item => item.WorkerProcessId).Distinct().Count());
            var firstOwner = Assert.Single(initialContenders, item => item.Acquired);
            Assert.Equal(1, firstOwner.FencingToken);
            Console.WriteLine($"POSTGRES_FENCE_INITIAL pids={string.Join(',', initialContenders.Select(item => item.WorkerProcessId))} acquired=1 token={firstOwner.FencingToken}");

            using (var expireDb = host.CreateContext())
            {
                var expired = await expireDb.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AutopilotLocks"
                    SET "ExpiresAt" = clock_timestamp() - interval '1 second'
                    WHERE "ResourceKey" = {resourceKey}
                    """);
                Assert.Equal(1, expired);
            }

            var takeoverContender = Assert.Single(await RunPostgresFenceProcessesAsync(
                scopedConnectionString, resourceKey, "takeover", 1, workerDirectory));
            Assert.True(takeoverContender.Acquired);
            Assert.True(takeoverContender.FencingToken > firstOwner.FencingToken);
            Console.WriteLine($"POSTGRES_FENCE_TAKEOVER pid={takeoverContender.WorkerProcessId} token={takeoverContender.FencingToken} staleToken={firstOwner.FencingToken}");

            var staleHandle = firstOwner.ToHandle(resourceKey);
            using (var staleDb = host.CreateContext())
            {
                var staleLocks = new EfAutopilotLockService(staleDb, host.Options);
                Assert.False((await staleLocks.RenewAsync(staleHandle)).IsAcquired);
                Assert.False(await staleLocks.ReleaseAsync(staleHandle));
            }

            var currentHandle = takeoverContender.ToHandle(resourceKey);
            using (var currentDb = host.CreateContext())
            {
                var currentLocks = new EfAutopilotLockService(currentDb, host.Options);
                var renewed = await currentLocks.RenewAsync(currentHandle);
                Assert.True(renewed.IsAcquired);
                Assert.Equal(takeoverContender.FencingToken, renewed.FencingToken);
                Assert.True(await currentLocks.ReleaseAsync(renewed));
            }

            using var liveHost = new Host(options =>
            {
                options.DryRun = false;
                options.SimulationMode = false;
                options.AllowNextWaveLaunch = true;
                options.AllowNextWaveLaunchWhenReleasePending = true;
                options.MaxTasksPerWave = 2;
                options.RequireSignedEvents = false;
            }, databaseOptions: contextOptions);
            await liveHost.AddBacklogAsync("bl-process-boundary-first", "Initial provider-free task", priority: 1);
            await liveHost.AddBacklogAsync("bl-process-boundary-sibling", "Sibling remains active", priority: 2);
            await liveHost.AddBacklogAsync("bl-process-boundary-successor", "One verified replacement", priority: 3);

            PostgresFenceIntakeWorkerInput? completionInput = null;
            liveHost.Provider.BeforeReturnAsync = async (request, externalTaskId, cancellationToken) =>
            {
                if (completionInput is not null) return;
                completionInput = CreatePostgresFenceCompletionInput(request, externalTaskId, expectedTaskCount: 2);
                var blocked = Assert.Single(await RunPostgresFenceProcessesAsync(
                    scopedConnectionString,
                    AutopilotCompletionLaunchFence.ResourceKey,
                    "intake-during-provider-call",
                    1,
                    workerDirectory,
                    workerMode: "intake",
                    intakeInputs: [completionInput!]));
                Assert.Equal(AutopilotIntakeOutcomes.FenceUnavailable, blocked.IntakeOutcome);
                using var verifyDuringCreate = liveHost.CreateContext();
                Assert.Empty(await verifyDuringCreate.AutopilotEvents.ToListAsync(cancellationToken));
            };

            await liveHost.GreenRunAsync("wave-postgres-intake-boundary");
            Assert.Equal(2, liveHost.Provider.LaunchCalls);
            Assert.NotNull(completionInput);

            var acceptedBySeparateProcess = Assert.Single(await RunPostgresFenceProcessesAsync(
                scopedConnectionString,
                AutopilotCompletionLaunchFence.ResourceKey,
                "intake-after-provider-outcome",
                1,
                workerDirectory,
                workerMode: "intake",
                intakeInputs: [completionInput!]));
            Assert.Equal(AutopilotIntakeOutcomes.Accepted, acceptedBySeparateProcess.IntakeOutcome);

            var conflictPayload = completionInput! with { PayloadJson = completionInput.PayloadJson + " " };
            conflictPayload = conflictPayload with
            {
                SignatureHeader = ComputePostgresFenceSignature(conflictPayload.TimestampHeader, conflictPayload.PayloadJson),
            };
            var conflictingProcess = Assert.Single(await RunPostgresFenceProcessesAsync(
                scopedConnectionString,
                AutopilotCompletionLaunchFence.ResourceKey,
                "intake-different-body-same-id",
                1,
                workerDirectory,
                workerMode: "intake",
                intakeInputs: [conflictPayload]));
            Assert.Equal(AutopilotIntakeOutcomes.PayloadConflict, conflictingProcess.IntakeOutcome);

            await liveHost.RunCycleAsync();
            using (var verify = liveHost.CreateContext())
            {
                var savedEvent = await verify.AutopilotEvents.SingleAsync(item =>
                    item.ExternalEventId == completionInput!.ExternalEventId);
                Assert.Equal(AutopilotEventStatuses.Processed, savedEvent.Status);
                Assert.True(savedEvent.SignatureVerified);
                Assert.True(savedEvent.ReplayProtected);
                var launchTasks = await verify.AutopilotWaveLaunchTasks.ToListAsync();
                Assert.Equal(3, launchTasks.Count);
                Assert.Single(launchTasks, item => item.Status == AutopilotWaveLaunchStatuses.Completed);
                Assert.Equal(2, launchTasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched));
                Assert.Equal(2, launchTasks.Count(item =>
                    item.Status is AutopilotWaveLaunchStatuses.Launching or AutopilotWaveLaunchStatuses.Launched));
                Assert.True(launchTasks.Count(item => item.Status is AutopilotWaveLaunchStatuses.Launching or AutopilotWaveLaunchStatuses.Launched)
                    <= AutopilotGlobalTaskSlotAllocator.HardMaximum);
            }
            Assert.Equal(3, liveHost.Provider.LaunchCalls);

            var duplicateBySeparateProcess = Assert.Single(await RunPostgresFenceProcessesAsync(
                scopedConnectionString,
                AutopilotCompletionLaunchFence.ResourceKey,
                "intake-identical-replay",
                1,
                workerDirectory,
                workerMode: "intake",
                intakeInputs: [completionInput!]));
            Assert.Equal(AutopilotIntakeOutcomes.Duplicate, duplicateBySeparateProcess.IntakeOutcome);
            await liveHost.RunCycleAsync();
            Assert.Equal(3, liveHost.Provider.LaunchCalls);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        var cleanupFailures = await CleanupPostgresFenceTestAsync(adminConnectionString, schemaName, workerDirectory);
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
                throw new AggregateException("PostgreSQL fence test and schema/temp cleanup both failed.", [primaryFailure, .. cleanupFailures]);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("PostgreSQL fence schema/temp cleanup failed.", cleanupFailures);
    }

    private static async Task<List<Exception>> CleanupPostgresFenceTestAsync(
        string adminConnectionString,
        string schemaName,
        string workerDirectory)
    {
        var failures = new List<Exception>();
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (Directory.Exists(workerDirectory))
                Directory.Delete(workerDirectory, recursive: true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures;
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
    public async Task Late_signed_completion_committed_during_inventory_blocks_provider_until_event_is_processed()
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
        await host.SeedGlobalSlotContendersAsync(22);
        Assert.Equal(20, (await host.LaunchAsync()).TasksLaunched);
        Assert.Equal(20, host.Provider.LaunchCalls);

        AutopilotWaveLaunchTask firstCompletion;
        using (var db = host.CreateContext())
        {
            firstCompletion = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                .OrderBy(item => item.TaskKey)
                .FirstAsync();
        }
        const string firstEventId = "fence-race-first-completion";
        var firstReceipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            firstCompletion.ExternalTaskId,
            "stopped", "finish", false, firstEventId));
        Assert.Equal(AutopilotIntakeOutcomes.Accepted, (await host.SubmitSignedCompletionAsync(
            firstCompletion.WaveKey, firstCompletion.TaskKey, 1, firstReceipt, firstEventId)).Outcome);
        using (var db = host.CreateContext())
        {
            var saved = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == firstEventId);
            saved.Status = AutopilotEventStatuses.Processed;
            saved.ProcessedAt = host.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, (await host.LaunchAsync()).TasksLaunched);
        Assert.Equal(21, host.Provider.LaunchCalls);

        AutopilotWaveLaunchTask lateCompletion;
        using (var db = host.CreateContext())
        {
            lateCompletion = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched && item.Id != firstCompletion.Id)
                .OrderBy(item => item.TaskKey)
                .FirstAsync();
        }
        const string lateEventId = "fence-race-late-completion";
        var lateReceipt = JsonSerializer.Serialize(new AutopilotGlobalTaskDetailTerminalReceipt(
            AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            lateCompletion.ExternalTaskId,
            "stopped", "finish", false, lateEventId));
        var injected = 0;
        host.InventorySource.AsyncPageResolver = async (scopeId, _, cancellationToken) =>
        {
            if (Interlocked.Exchange(ref injected, 1) == 0)
            {
                var accepted = await host.SubmitSignedCompletionAsync(
                    lateCompletion.WaveKey, lateCompletion.TaskKey, 1, lateReceipt, lateEventId);
                Assert.Equal(AutopilotIntakeOutcomes.Accepted, accepted.Outcome);
            }
            return new AutopilotGlobalTaskInventoryPage(scopeId, Array.Empty<AutopilotGlobalTaskInventoryItem>(), null);
        };

        var deferred = await host.LaunchAsync();
        host.InventorySource.AsyncPageResolver = null;
        Assert.Equal(21, host.Provider.LaunchCalls);
        Assert.Contains(deferred.PlannedActions, item => item.Contains("completion_event_pending", StringComparison.Ordinal));
        using (var db = host.CreateContext())
        {
            var pending = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == lateEventId);
            Assert.Equal(AutopilotEventStatuses.Queued, pending.Status);
            Assert.True(pending.SignatureVerified);
            Assert.True(pending.ReplayProtected);
            var deferredTasks = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Planned)
                .ToListAsync();
            Assert.NotEmpty(deferredTasks);
            Assert.All(deferredTasks, item => Assert.Equal(0, item.LaunchAttempt));
            foreach (var deferredTask in deferredTasks)
            {
                var deferredBacklog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == deferredTask.BacklogItemKey);
                Assert.Null(deferredBacklog.ConsumedByWaveKey);
            }
            Assert.Equal(20, await db.AutopilotWaveLaunchTasks.CountAsync(item =>
                item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
        }

        using (var db = host.CreateContext())
        {
            var pending = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == lateEventId);
            pending.Status = AutopilotEventStatuses.Processed;
            pending.ProcessedAt = host.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }
        var recovered = await host.LaunchAsync();
        Assert.Equal(1, recovered.TasksLaunched);
        Assert.Equal(22, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var processed = await db.AutopilotEvents.SingleAsync(item => item.ExternalEventId == lateEventId);
            Assert.Equal(AutopilotEventStatuses.Processed, processed.Status);
            Assert.True(processed.SignatureVerified);
            Assert.True(processed.ReplayProtected);
            Assert.Equal(20, await db.AutopilotWaveLaunchTasks.CountAsync(item =>
                item.Status == AutopilotWaveLaunchStatuses.Launching || item.Status == AutopilotWaveLaunchStatuses.Launched));
            Assert.Equal(AutopilotWaveLaunchStatuses.Completed,
                (await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == lateCompletion.Id)).Status);
        }
    }

    [Fact]
    public async Task Live_launch_fails_closed_when_fence_lease_is_shorter_than_bridge_timeout_and_margin()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.LockLeaseMinutes = 1;
            options.BridgeTimeoutSeconds = 120;
        });
        await host.AddBacklogAsync("bl-short-launch-fence", "Do not launch under an expiring fence");
        await host.GreenRunAsync("wave-short-launch-fence");

        using (var db = host.CreateContext())
        {
            Assert.Empty(await db.AutopilotWaveLaunchTasks.ToListAsync());
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-short-launch-fence");
            Assert.Null(backlog.ConsumedByWaveKey);
        }
        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Provider_is_not_called_and_claim_resets_when_acquired_fence_cannot_be_renewed()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.BridgeTimeoutSeconds = 120;
        }, lockServiceOverride: new NearExpiryLockService());
        await host.AddBacklogAsync("bl-near-expiry-fence", "Do not create after the launch fence is nearly expired");
        await host.GreenRunAsync("wave-near-expiry-fence");

        Assert.Equal(0, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal(0, task.LaunchAttempt);
            Assert.Equal("completion_launch_fence_renewal_unavailable; no_provider_call", task.LastStatusDetail);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-near-expiry-fence");
            Assert.Null(backlog.ConsumedByWaveKey);
        }
    }

    [Fact]
    public async Task Expired_fence_cannot_be_renewed_or_released_after_takeover_and_token_increases()
    {
        using var host = new Host();
        var resourceKey = AutopilotCompletionLaunchFence.ResourceKey;
        AutopilotLockHandle staleHandle;
        using (var db = host.CreateContext())
        {
            staleHandle = await new EfAutopilotLockService(db, host.Options, host.Time)
                .AcquireAsync(resourceKey, "first-holder");
        }
        Assert.True(staleHandle.IsAcquired);

        using (var db = host.CreateContext())
        {
            var expiredAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var changed = await db.AutopilotLocks
                .Where(item => item.ResourceKey == resourceKey)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ExpiresAt, expiredAt));
            Assert.Equal(1, changed);
        }

        using (var db = host.CreateContext())
        {
            var locks = new EfAutopilotLockService(db, host.Options, host.Time);
            Assert.False((await locks.RenewAsync(staleHandle)).IsAcquired);
        }

        AutopilotLockHandle currentHandle;
        using (var db = host.CreateContext())
        {
            currentHandle = await new EfAutopilotLockService(db, host.Options, host.Time)
                .AcquireAsync(resourceKey, "second-holder");
        }
        Assert.True(currentHandle.IsAcquired);
        Assert.Equal(staleHandle.FencingToken + 1, currentHandle.FencingToken);

        using (var db = host.CreateContext())
        {
            var locks = new EfAutopilotLockService(db, host.Options, host.Time);
            Assert.False(await locks.ReleaseAsync(staleHandle));
            Assert.False((await locks.RenewAsync(staleHandle)).IsAcquired);

            var renewedCurrent = await locks.RenewAsync(currentHandle);
            Assert.True(renewedCurrent.IsAcquired);
            Assert.Equal(currentHandle.FencingToken, renewedCurrent.FencingToken);
            Assert.True(renewedCurrent.ExpiresAt > host.Time.GetUtcNow().UtcDateTime);
            Assert.True(await locks.ReleaseAsync(renewedCurrent));
        }
    }

    [Fact]
    public async Task Renewal_store_operation_canceled_without_stop_request_marks_fence_lost()
    {
        var locks = new RenewalStoreCancellationLockService();
        var handle = await locks.AcquireAsync(AutopilotCompletionLaunchFence.ResourceKey, "heartbeat-test");
        Assert.True(handle.IsAcquired);

        var heartbeat = await AutopilotLockLeaseHeartbeat.StartAsync(
            locks,
            handle,
            TimeSpan.FromMilliseconds(5),
            NullLogger<AutopilotNextWaveService>.Instance,
            CancellationToken.None);
        Assert.NotNull(heartbeat);
        await using var scope = heartbeat!;
        var lost = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = scope.LeaseLostToken.Register(() => lost.TrySetResult(true));

        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(scope.IsLost);
        Assert.Equal(2, locks.RenewCalls);
        Assert.False(await scope.RenewNowAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provider_is_not_called_when_immediate_pre_call_renewal_loses_fence()
    {
        var probe = new FenceScopeProbe { LoseOnScopedRenewCall = 2 };
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: new FenceScopeProbeLockService(probe, scoped: false),
            completionFenceRenewalInterval: TimeSpan.FromSeconds(30));
        await host.SeedGlobalSlotContendersAsync(1);
        using var renewalProvider = new ServiceCollection()
            .AddScoped<IAutopilotLockService>(_ => new FenceScopeProbeLockService(probe, scoped: true))
            .BuildServiceProvider();

        await host.LaunchAsync(fenceRenewalScopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(0, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, launch.Status);
            Assert.Null(launch.ExternalTaskId);
            Assert.Contains("completion_launch_fence_renewal_failed_before_provider_call",
                launch.LastStatusDetail, StringComparison.Ordinal);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == launch.BacklogItemKey);
            Assert.Equal(launch.WaveKey, backlog.ConsumedByWaveKey);
        }

        await host.LaunchAsync(fenceRenewalScopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(0, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Provider_is_not_called_when_successful_renewal_has_insufficient_server_time_remaining()
    {
        var probe = new FenceScopeProbe { RenewalDuration = TimeSpan.FromSeconds(30) };
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: new FenceScopeProbeLockService(probe, scoped: false),
            completionFenceRenewalInterval: TimeSpan.FromSeconds(30));
        await host.SeedGlobalSlotContendersAsync(1);
        using var renewalProvider = new ServiceCollection()
            .AddScoped<IAutopilotLockService>(_ => new FenceScopeProbeLockService(probe, scoped: true))
            .BuildServiceProvider();

        await host.LaunchAsync(fenceRenewalScopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(0, host.Provider.LaunchCalls);
        using var db = host.CreateContext();
        var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, launch.Status);
        Assert.Null(launch.ExternalTaskId);
        Assert.Contains("completion_launch_fence_renewal_failed_before_provider_call",
            launch.LastStatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fence_loss_after_durable_provider_result_stops_later_work_items()
    {
        var probe = new FenceScopeProbe { LoseOnScopedRenewCall = 4 };
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: new FenceScopeProbeLockService(probe, scoped: false),
            completionFenceRenewalInterval: TimeSpan.FromSeconds(30));
        await host.SeedGlobalSlotContendersAsync(2);
        using var renewalProvider = new ServiceCollection()
            .AddScoped<IAutopilotLockService>(_ => new FenceScopeProbeLockService(probe, scoped: true))
            .BuildServiceProvider();

        var result = await host.LaunchAsync(
            fenceRenewalScopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(1, result.TasksLaunched);
        using var db = host.CreateContext();
        var statuses = await db.AutopilotWaveLaunchTasks.Select(item => item.Status).ToListAsync();
        Assert.Single(statuses, status => status == AutopilotWaveLaunchStatuses.Launched);
        Assert.Single(statuses, status => status == AutopilotWaveLaunchStatuses.Planned);
        Assert.Equal(0, probe.ScopedReleaseCalls);
    }

    [Fact]
    public async Task Lost_fence_renewal_during_provider_call_makes_late_success_ambiguous_and_never_retries()
    {
        var expiringLock = new ExpireDuringProviderLockService();
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: expiringLock, completionFenceRenewalInterval: TimeSpan.FromMilliseconds(5));
        await host.AddBacklogAsync("bl-fence-lost-in-provider", "Retain the reservation if the fence expires");
        host.Provider.BeforeReturnAsync = async (_, _, _) =>
        {
            expiringLock.BeginLeaseLoss();
            await expiringLock.LeaseLost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        await host.GreenRunAsync("wave-fence-lost-in-provider");
        Assert.Equal(1, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, launch.Status);
            Assert.Null(launch.ExternalTaskId);
            Assert.Contains("completion_launch_fence_lost_ambiguous", launch.LastStatusDetail, StringComparison.Ordinal);
        }

        await host.LaunchAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, launch.Status);
            Assert.Null(launch.ExternalTaskId);
        }
    }

    [Fact]
    public async Task Cancellation_after_successful_provider_response_persists_launch_and_reconcile_metadata()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.SeedGlobalSlotContendersAsync(1);
        using var cancellation = new CancellationTokenSource();
        host.Provider.AfterProviderResultAsync = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LaunchAsync(cancellation.Token));
        Assert.Equal(1, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched, launch.Status);
            Assert.Equal($"ext-{launch.TaskKey}", launch.ExternalTaskId);
            Assert.Equal("awaiting_signed_completion_event", launch.LastStatusDetail);
            Assert.NotNull(launch.NextReconcileAt);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == launch.BacklogItemKey);
            Assert.Equal(launch.WaveKey, backlog.ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.NextWaveLaunched && item.TargetId == launch.Id);
        }

        await host.LaunchAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Cancellation_after_unverified_rejection_persists_hold_and_retains_claim()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.SeedGlobalSlotContendersAsync(1);
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Rejected("bridge_status_400"));
        using var cancellation = new CancellationTokenSource();
        host.Provider.AfterProviderResultAsync = (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LaunchAsync(cancellation.Token));
        Assert.Equal(1, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var launch = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, launch.Status);
            Assert.Equal("provider_rejection_unverified", launch.ReasonCode);
            Assert.Contains("provider_rejection_unverified_no_create_proof:", launch.LastStatusDetail, StringComparison.Ordinal);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == launch.BacklogItemKey);
            Assert.Equal(launch.WaveKey, backlog.ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.StatusDetail == "provider_rejection_unverified_no_create_proof; slot_and_backlog_claim_retained"
                && item.TargetId == launch.Id);
        }

        await host.LaunchAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Provider_create_holds_the_shared_completion_fence_until_outcome_is_persisted()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-fence-held-through-provider", "Hold the shared fence across create");
        var intakeCouldAcquireFenceDuringCreate = true;
        var fenceDiagnostic = "not reached";
        host.Provider.BeforeReturnAsync = async (_, _, cancellationToken) =>
        {
            using var contenderDb = host.CreateContext();
            var currentFence = await contenderDb.AutopilotLocks.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ResourceKey == AutopilotCompletionLaunchFence.ResourceKey, cancellationToken);
            fenceDiagnostic = currentFence is null
                ? "fence row missing"
                : $"heldBy={currentFence.HeldBy}; expires={currentFence.ExpiresAt:o}; now={DateTime.UtcNow:o}; token={currentFence.FencingToken}";
            var contenderLocks = new EfAutopilotLockService(contenderDb, host.Options);
            var contender = await contenderLocks.AcquireAsync(
                AutopilotCompletionLaunchFence.ResourceKey,
                "synthetic-concurrent-intake",
                cancellationToken);
            intakeCouldAcquireFenceDuringCreate = contender.IsAcquired;
            if (contender.IsAcquired)
                await contenderLocks.ReleaseAsync(contender, cancellationToken);
        };

        await host.GreenRunAsync("wave-fence-held-through-provider");
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.False(intakeCouldAcquireFenceDuringCreate, $"{fenceDiagnostic}; providerCalls={host.Provider.LaunchCalls}");
        using (var db = host.CreateContext())
        {
            Assert.Equal(AutopilotWaveLaunchStatuses.Launched,
                (await db.AutopilotWaveLaunchTasks.SingleAsync()).Status);
            var remainingFence = await db.AutopilotLocks.AsNoTracking()
                .SingleAsync(item => item.ResourceKey == AutopilotCompletionLaunchFence.ResourceKey);
            var locks = new EfAutopilotLockService(db, host.Options);
            var released = await locks.AcquireAsync(
                AutopilotCompletionLaunchFence.ResourceKey,
                "synthetic-post-launch-intake");
            Assert.True(released.IsAcquired,
                $"heldBy={remainingFence.HeldBy}; ownerToken={remainingFence.OwnerToken}; fencingToken={remainingFence.FencingToken}; expiry={remainingFence.ExpiresAt:o}; now={DateTime.UtcNow:o}");
            Assert.True(await locks.ReleaseAsync(released));
        }
    }

    [Fact]
    public async Task Launch_renews_fence_in_isolated_scope_and_releases_after_result_persistence()
    {
        var probe = new FenceScopeProbe();
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: new FenceScopeProbeLockService(probe, scoped: false),
            completionFenceRenewalInterval: TimeSpan.FromMilliseconds(5));
        await host.SeedGlobalSlotContendersAsync(1);
        probe.ReadPersistedLaunchStatus = async () =>
        {
            using var db = host.CreateContext();
            return await db.AutopilotWaveLaunchTasks
                .Select(item => item.Status)
                .SingleAsync();
        };

        using var renewalProvider = new ServiceCollection()
            .AddScoped<IAutopilotLockService>(_ => new FenceScopeProbeLockService(probe, scoped: true))
            .BuildServiceProvider();
        host.Provider.BeforeReturnAsync = async (_, _, cancellationToken) =>
            await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);

        var result = await host.LaunchAsync(
            fenceRenewalScopeFactory: renewalProvider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, result.TasksLaunched);
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(0, probe.OuterRenewCalls);
        Assert.True(probe.ScopedRenewCalls >= 2,
            $"Expected initial plus periodic renewal in child scope; renewals={probe.ScopedRenewCalls}.");
        Assert.Equal(1, probe.ScopedReleaseCalls);
        Assert.Equal(AutopilotWaveLaunchStatuses.Launched, probe.StatusObservedAtRelease);
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
    public async Task Incomplete_paginated_inventory_never_grants_capacity_or_calls_provider()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        var requestedCursors = new List<string?>();
        host.InventorySource.PageResolver = (scopeId, cursor) =>
        {
            requestedCursors.Add(cursor);
            if (cursor is null)
                return new AutopilotGlobalTaskInventoryPage(scopeId,
                    [new AutopilotGlobalTaskInventoryItem("external-running-task", scopeId, "running", null)],
                    "page-2");

            Assert.Equal("page-2", cursor);
            return null;
        };
        await host.AddBacklogAsync("bl-incomplete-inventory", "Do not launch from a partial global snapshot");

        await host.GreenRunAsync("wave-incomplete-inventory");

        Assert.Equal(new string?[] { null, "page-2" }, requestedCursors);
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Planned, task.Status);
            Assert.Equal(0, task.LaunchAttempt);
            Assert.Equal("inventory_page_invalid", task.ReasonCode);
            Assert.Null(task.ExternalTaskId);
            var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-incomplete-inventory");
            Assert.Null(backlog.ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.NextWaveLaunchBlocked
                && item.Reason == "inventory_page_invalid"
                && item.StatusDetail == "provider_call_denied");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_exception_or_timeout_is_persisted_as_ambiguous_and_never_retried(bool timeoutLike)
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-provider-throw", "Retain claim after provider throws");
        host.Provider.LaunchExceptionFactory = _ => timeoutLike
            ? new OperationCanceledException("synthetic provider timeout")
            : new HttpRequestException("synthetic transport loss after possible create");

        await host.GreenRunAsync("wave-provider-throw");

        Assert.Equal(1, host.Provider.LaunchCalls);
        using (var db = host.CreateContext())
        {
            var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
            Assert.Equal("global_slot_reserved", task.ReasonCode);
            Assert.StartsWith("slot_retained_until_authoritative_reconciliation:provider_call_", task.LastStatusDetail);
            Assert.Equal("wave-provider-throw-next",
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-provider-throw")).ConsumedByWaveKey);
            Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                && item.Reason == AutopilotHumanDecisions.RepairEscalation
                && item.StatusDetail == "ambiguous_provider_outcome_requires_authoritative_reconciliation");
        }

        host.Time.Advance(TimeSpan.FromDays(1));
        await host.LaunchAsync();
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Caller_cancellation_during_provider_call_persists_ambiguous_state_before_propagating()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = false;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        });
        await host.AddBacklogAsync("bl-provider-cancel", "Retain claim when caller cancels create");
        await host.GreenRunAsync("wave-provider-cancel");
        host.Options.AllowNextWaveLaunch = true;

        using var cancellation = new CancellationTokenSource();
        host.Provider.LaunchExceptionFactory = _ =>
        {
            cancellation.Cancel();
            return new OperationCanceledException(cancellation.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.LaunchAsync(cancellation.Token));

        Assert.Equal(1, host.Provider.LaunchCalls);
        using var db = host.CreateContext();
        var task = await db.AutopilotWaveLaunchTasks.SingleAsync();
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
        Assert.StartsWith("slot_retained_until_authoritative_reconciliation:provider_call_canceled_ambiguous", task.LastStatusDetail);
        Assert.Equal("wave-provider-cancel-next",
            (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-provider-cancel")).ConsumedByWaveKey);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
            item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.RepairEscalation);
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
    public async Task Unverified_provider_rejection_holds_slot_and_backlog_claim_without_replacement()
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
        Assert.Equal(AutopilotWaveLaunchStatuses.Launching, task.Status);
        Assert.Equal(1, task.LaunchAttempt);
        Assert.Equal("provider_rejection_unverified", task.ReasonCode);
        Assert.Contains("provider_rejection_unverified_no_create_proof:", task.LastStatusDetail, StringComparison.Ordinal);
        var backlog = await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-definitive-rejection");
        Assert.Equal(task.WaveKey, backlog.ConsumedByWaveKey);
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Contains(await db.AutopilotAuditEvents.ToListAsync(), item =>
            item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.RepairEscalation
            && item.StatusDetail == "provider_rejection_unverified_no_create_proof; slot_and_backlog_claim_retained"
            && item.TargetId == task.Id);

        await host.GreenRunAsync("wave-definitive-rejection-replay");
        Assert.Equal(1, host.Provider.LaunchCalls);
        Assert.Equal(1, await db.AutopilotWaveLaunchTasks.CountAsync());
    }

    [Fact]
    public async Task Restart_after_unverified_rejection_keeps_slot_and_backlog_claim_held()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
            options.MaxLaunchAttempts = 3;
        });
        await host.AddBacklogAsync("bl-rejection-restart", "Hold this approved task after an unverified rejection");
        host.Provider.Results.Enqueue(WaveLaunchProviderResult.Rejected("bridge_status_400"));
        await host.GreenRunAsync("wave-rejection-restart");

        Guid rejectedTaskId;
        string rejectedWaveKey;
        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync();
            rejectedTaskId = rejected.Id;
            rejectedWaveKey = rejected.WaveKey;
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, rejected.Status);
            Assert.Equal("provider_rejection_unverified", rejected.ReasonCode);
            Assert.Equal("provider_rejection_unverified_no_create_proof:bridge_status_400", rejected.LastStatusDetail);
            Assert.Equal(rejectedWaveKey,
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-rejection-restart")).ConsumedByWaveKey);
        }
        Assert.Equal(1, host.Provider.LaunchCalls);

        // Simulate process restart after the launch lease expires. Stale
        // reconciliation must retain the original proof marker and never recreate.
        host.Options.LockLeaseMinutes = 1;
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.ReconcileLaunchesAsync();
        await host.ReconcileLaunchesAsync();

        using (var db = host.CreateContext())
        {
            var recovered = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rejectedTaskId);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, recovered.Status);
            Assert.Equal(1, recovered.LaunchAttempt);
            Assert.Equal("provider_rejection_unverified", recovered.ReasonCode);
            Assert.Equal("provider_rejection_unverified_no_create_proof:bridge_status_400", recovered.LastStatusDetail);
            Assert.Equal(rejectedWaveKey,
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-rejection-restart")).ConsumedByWaveKey);
            Assert.Equal(1, await db.AutopilotWaveLaunchTasks.CountAsync());
        }

        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Unverified_rejection_never_releases_backlog_claim_owned_by_another_wave()
    {
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.SimulationMode = false;
            options.AllowNextWaveLaunch = true;
            options.MaxLaunchAttempts = 3;
            options.LockLeaseMinutes = 1;
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
        Assert.Equal(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, rejectedResult.Outcome);
        Assert.True(rejectedResult.SlotHeld);
        Assert.False(rejectedResult.MayCallProvider);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.ReconcileLaunchesAsync();
        await host.ReconcileLaunchesAsync();

        using (var db = host.CreateContext())
        {
            var rejected = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == rejectedTaskId);
            Assert.Equal(AutopilotWaveLaunchStatuses.Launching, rejected.Status);
            Assert.Equal(1, rejected.LaunchAttempt);
            Assert.Equal("provider_rejection_unverified", rejected.ReasonCode);
            Assert.Equal("provider_rejection_unverified_no_create_proof:bridge_status_400", rejected.LastStatusDetail);
            Assert.Equal("another-wave-owner",
                (await db.AutopilotBacklogItems.SingleAsync(item => item.ItemKey == "bl-cap-00")).ConsumedByWaveKey);
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
    public async Task Poll_diagnostic_commit_notifies_event_dispatch_after_the_event_is_durable()
    {
        var wakeCalls = 0;
        var durableAtWake = 0;
        Host? hostReference = null;
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, notifyCommittedEvent: () =>
        {
            Interlocked.Increment(ref wakeCalls);
            using var db = hostReference!.CreateContext();
            if (db.AutopilotEvents.Any(item => item.SourceSystem == AutopilotInternalSignalSources.Polling))
                Interlocked.Increment(ref durableAtWake);
        });
        hostReference = host;
        await host.AddBacklogAsync("bl-poll-wake", "Verify internal poll event wake");
        await host.GreenRunAsync("wave-poll-wake");

        host.Provider.StateResolver = _ => new WaveLaunchTaskState(
            WaveLaunchTaskStates.Succeeded, IsTerminal: true, IsSuccess: true, ExternalRef: "poll-ref", StatusDetail: "synthetic poll success");
        host.Time.Advance(TimeSpan.FromMinutes(45));
        var result = await host.ReconcileLaunchesAsync();

        Assert.Equal(1, result.TasksReconciled);
        Assert.Equal(1, Volatile.Read(ref wakeCalls));
        Assert.Equal(1, Volatile.Read(ref durableAtWake));
        using var verify = host.CreateContext();
        var diagnostic = await verify.AutopilotEvents.SingleAsync(item =>
            item.SourceSystem == AutopilotInternalSignalSources.Polling);
        Assert.Equal(AutopilotEventStatuses.Queued, diagnostic.Status);
        Assert.False(diagnostic.SignatureVerified);
        Assert.False(diagnostic.ReplayProtected);
        Assert.Equal(1, host.Provider.LaunchCalls);
    }

    [Fact]
    public async Task Poll_diagnostic_with_uncertain_fence_release_is_durable_but_not_signaled()
    {
        var wakeCalls = 0;
        using var host = new Host(options =>
        {
            options.DryRun = false;
            options.AllowNextWaveLaunch = true;
            options.AllowNextWaveLaunchWhenReleasePending = true;
        }, lockServiceOverride: new CompletionFenceReleaseFailureLockService(),
            notifyCommittedEvent: () => Interlocked.Increment(ref wakeCalls));
        await host.AddBacklogAsync("bl-poll-wake-release", "Verify wake waits for fence release");
        await host.GreenRunAsync("wave-poll-wake-release");

        host.Provider.StateResolver = _ => new WaveLaunchTaskState(
            WaveLaunchTaskStates.Succeeded, IsTerminal: true, IsSuccess: true, ExternalRef: "poll-ref", StatusDetail: "synthetic poll success");
        host.Time.Advance(TimeSpan.FromMinutes(45));
        var result = await host.ReconcileLaunchesAsync();

        Assert.Equal(1, result.TasksReconciled);
        Assert.Equal(0, Volatile.Read(ref wakeCalls));
        using var verify = host.CreateContext();
        var diagnostic = await verify.AutopilotEvents.SingleAsync(item =>
            item.SourceSystem == AutopilotInternalSignalSources.Polling);
        Assert.Equal(AutopilotEventStatuses.Queued, diagnostic.Status);
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
                AutopilotNextWavePolicy.LaunchIdempotencyKey("wave-x-next", "bl-1"))
            {
                FencingToken = 1_234_567_890_123,
            };

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
            Assert.Equal("1234567890123", recorded.FencingToken);
            using var providerRequestJson = JsonDocument.Parse(JsonSerializer.Serialize(request));
            Assert.False(providerRequestJson.RootElement.TryGetProperty(nameof(WaveLaunchProviderRequest.FencingToken), out _));
            Assert.False(providerRequestJson.RootElement.TryGetProperty("fencingToken", out _));
            using var requestBody = JsonDocument.Parse(recorded.Body);
            Assert.Equal(request.WaveKey, requestBody.RootElement.GetProperty("waveKey").GetString());
            Assert.Equal(request.TaskKey, requestBody.RootElement.GetProperty("taskKey").GetString());
            Assert.Equal(request.IdempotencyKey, requestBody.RootElement.GetProperty("idempotencyKey").GetString());
            Assert.False(requestBody.RootElement.TryGetProperty("fencingToken", out _));
            Assert.False(requestBody.RootElement.TryGetProperty("fencing_token", out _));
            Assert.DoesNotContain(sentinel, recorded.Body);

            var requestsBeforeInvalidEpochs = handler.Requests.Count;
            var missingEpoch = await provider.LaunchTaskAsync(request with { FencingToken = 0 });
            var negativeEpoch = await provider.LaunchTaskAsync(request with { FencingToken = -1 });
            Assert.Equal(WaveLaunchProviderOutcomes.Rejected, missingEpoch.Outcome);
            Assert.Equal("bridge_fencing_token_invalid", missingEpoch.ReasonCode);
            Assert.Equal(WaveLaunchProviderOutcomes.Rejected, negativeEpoch.Outcome);
            Assert.Equal("bridge_fencing_token_invalid", negativeEpoch.ReasonCode);
            Assert.Equal(requestsBeforeInvalidEpochs, handler.Requests.Count);

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

    private sealed record RecordedBridgeCall(HttpMethod Method, string Uri, string? Authorization, string? IdempotencyKey, string? FencingToken, string Body);

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
                request.Headers.TryGetValues("X-Taslim-Fencing-Token", out var fencingValues) ? string.Join(',', fencingValues) : null,
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

        public Func<WaveLaunchProviderRequest, WaveLaunchProviderResult, CancellationToken, Task>? AfterProviderResultAsync { get; set; }

        public Func<string, CancellationToken, Task>? BeforeStateReturnAsync { get; set; }

        public Func<CancellationToken, Exception>? LaunchExceptionFactory { get; set; }

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

            if (LaunchExceptionFactory is not null)
                throw LaunchExceptionFactory(cancellationToken);

            if (BeforeReturnAsync is not null && result.IsLaunched && result.ExternalTaskId is not null)
                await BeforeReturnAsync(request, result.ExternalTaskId, cancellationToken);
            if (AfterProviderResultAsync is not null)
                await AfterProviderResultAsync(request, result, cancellationToken);
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

        public Func<string, string?, CancellationToken, Task<AutopilotGlobalTaskInventoryPage?>>? AsyncPageResolver { get; set; }

        public Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
            string scopeId,
            string? cursor,
            CancellationToken cancellationToken = default) =>
            AsyncPageResolver is null
                ? Task.FromResult(PageResolver(scopeId, cursor))
                : AsyncPageResolver(scopeId, cursor, cancellationToken);
    }

    private sealed class FenceScopeProbe
    {
        public object Gate { get; } = new();
        public bool Held { get; set; }
        public long FencingToken { get; set; }
        public string? OwnerToken { get; set; }
        public int OuterRenewCalls;
        public int ScopedRenewCalls;
        public int ScopedReleaseCalls;
        public int LoseOnScopedRenewCall { get; set; }
        public TimeSpan? RenewalDuration { get; set; }
        public string? StatusObservedAtRelease { get; set; }
        public Func<Task<string>>? ReadPersistedLaunchStatus { get; set; }
    }

    private sealed class FenceScopeProbeLockService(FenceScopeProbe probe, bool scoped)
        : IAutopilotLockService, IAutopilotLockLeaseRenewalService
    {
        public Task<AutopilotLockHandle> AcquireAsync(
            string resourceKey,
            string heldBy,
            CancellationToken cancellationToken = default)
        {
            lock (probe.Gate)
            {
                if (probe.Held)
                    return Task.FromResult(AutopilotLockHandle.NotAcquired(resourceKey, "probe-holder", DateTime.UtcNow.AddMinutes(1)));
                probe.Held = true;
                probe.OwnerToken = Guid.NewGuid().ToString("N");
                probe.FencingToken++;
                return Task.FromResult(new AutopilotLockHandle(
                    resourceKey, probe.OwnerToken, probe.FencingToken, true, heldBy, DateTime.UtcNow.AddMinutes(1)));
            }
        }

        public Task<AutopilotLockHandle> RenewAsync(
            AutopilotLockHandle handle,
            CancellationToken cancellationToken = default)
        {
            var scopedRenewCall = 0;
            if (scoped) scopedRenewCall = Interlocked.Increment(ref probe.ScopedRenewCalls);
            else Interlocked.Increment(ref probe.OuterRenewCalls);

            if (scoped && probe.LoseOnScopedRenewCall == scopedRenewCall)
            {
                lock (probe.Gate) probe.Held = false;
                return Task.FromResult(AutopilotLockHandle.NotAcquired(handle.ResourceKey, "newer-probe-holder", handle.ExpiresAt));
            }

            lock (probe.Gate)
            {
                if (!probe.Held
                    || probe.FencingToken != handle.FencingToken
                    || !string.Equals(probe.OwnerToken, handle.OwnerToken, StringComparison.Ordinal))
                    return Task.FromResult(AutopilotLockHandle.NotAcquired(handle.ResourceKey, "probe-stale", handle.ExpiresAt));
                var renewedAt = DateTime.UtcNow;
                return Task.FromResult(handle with
                {
                    ExpiresAt = renewedAt.Add(probe.RenewalDuration ?? TimeSpan.FromMinutes(10)),
                    LeaseServerTimeUtc = renewedAt,
                });
            }
        }

        public async Task<bool> ReleaseAsync(
            AutopilotLockHandle handle,
            CancellationToken cancellationToken = default)
        {
            lock (probe.Gate)
            {
                if (!probe.Held
                    || probe.FencingToken != handle.FencingToken
                    || !string.Equals(probe.OwnerToken, handle.OwnerToken, StringComparison.Ordinal))
                    return false;
                probe.Held = false;
            }

            if (scoped)
            {
                Interlocked.Increment(ref probe.ScopedReleaseCalls);
                var readStatus = probe.ReadPersistedLaunchStatus;
                if (readStatus is not null)
                    probe.StatusObservedAtRelease = await readStatus();
            }
            return true;
        }
    }

    private sealed class NearExpiryLockService : IAutopilotLockService, IAutopilotLockLeaseRenewalService
    {
        private long fencingToken;

        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey,
                Guid.NewGuid().ToString("N"),
                Interlocked.Increment(ref fencingToken),
                true,
                heldBy,
                DateTime.UtcNow.AddSeconds(-1)));

        public Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotLockHandle.NotAcquired(handle.ResourceKey, null, handle.ExpiresAt));

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ExpireDuringProviderLockService : IAutopilotLockService, IAutopilotLockLeaseRenewalService
    {
        private int renewCalls;
        private int leaseLossEnabled;
        private long fencingToken;

        public TaskCompletionSource<bool> LeaseLost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeginLeaseLoss() => Volatile.Write(ref leaseLossEnabled, 1);

        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey,
                Guid.NewGuid().ToString("N"),
                Interlocked.Increment(ref fencingToken),
                true,
                heldBy,
                DateTime.UtcNow.AddMinutes(5)));

        public Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref renewCalls);
            if (Volatile.Read(ref leaseLossEnabled) == 0)
            {
                var renewedAt = DateTime.UtcNow;
                return Task.FromResult(handle with
                {
                    ExpiresAt = renewedAt.AddMinutes(5),
                    LeaseServerTimeUtc = renewedAt,
                });
            }

            LeaseLost.TrySetResult(true);
            return Task.FromResult(AutopilotLockHandle.NotAcquired(handle.ResourceKey, "newer-owner", handle.ExpiresAt));
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RenewalStoreCancellationLockService : IAutopilotLockService, IAutopilotLockLeaseRenewalService
    {
        private int renewCalls;

        public int RenewCalls => Volatile.Read(ref renewCalls);

        public Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey, "heartbeat-test-token", 1, true, heldBy, DateTime.UtcNow.AddMinutes(5)));

        public Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref renewCalls) == 1)
            {
                var renewedAt = DateTime.UtcNow;
                return Task.FromResult(handle with
                {
                    ExpiresAt = renewedAt.AddMinutes(5),
                    LeaseServerTimeUtc = renewedAt,
                });
            }

            return Task.FromException<AutopilotLockHandle>(new OperationCanceledException("Synthetic renewal-store timeout."));
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CompletionFenceReleaseFailureLockService : IAutopilotLockService, IAutopilotLockLeaseRenewalService
    {
        private long fencingToken;

        public Task<AutopilotLockHandle> AcquireAsync(
            string resourceKey,
            string heldBy,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotLockHandle(
                resourceKey,
                Guid.NewGuid().ToString("N"),
                Interlocked.Increment(ref fencingToken),
                true,
                heldBy,
                DateTime.UtcNow.AddMinutes(10)));

        public Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
        {
            var renewedAt = DateTime.UtcNow;
            return Task.FromResult(handle with
            {
                ExpiresAt = renewedAt.AddMinutes(10),
                LeaseServerTimeUtc = renewedAt,
            });
        }

        public Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(!string.Equals(
                handle.ResourceKey,
                AutopilotCompletionLaunchFence.ResourceKey,
                StringComparison.Ordinal));
    }

    private sealed class Host : IDisposable
    {
        private const string TestTaslimScopeId = "taslim-autopilot-test-scope";
        private readonly SqliteConnection? connection;
        private readonly DbContextOptions<TaslimDbContext> contextOptions;
        private readonly bool enableGlobalSlotServices;
        private readonly IWaveLaunchProvider? launchProviderOverride;
        private readonly IAutopilotLockService? lockServiceOverride;
        private readonly Action? notifyCommittedEvent;
        private readonly TimeSpan? completionFenceRenewalInterval;
        private readonly object renewalServiceProvidersGate = new();
        private readonly List<ServiceProvider> renewalServiceProviders = [];

        public Host(
            Action<AutopilotOptions>? configure = null,
            bool enableGlobalSlotServices = true,
            DbContextOptions<TaslimDbContext>? databaseOptions = null,
            IWaveLaunchProvider? launchProviderOverride = null,
            IAutopilotLockService? lockServiceOverride = null,
            Action? notifyCommittedEvent = null,
            TimeSpan? completionFenceRenewalInterval = null)
        {
            this.enableGlobalSlotServices = enableGlobalSlotServices;
            this.launchProviderOverride = launchProviderOverride;
            this.lockServiceOverride = lockServiceOverride;
            this.notifyCommittedEvent = notifyCommittedEvent;
            this.completionFenceRenewalInterval = completionFenceRenewalInterval;
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

        public async Task<AutopilotNextWaveCycleResult> LaunchAsync(
            CancellationToken cancellationToken = default,
            IServiceScopeFactory? fenceRenewalScopeFactory = null)
        {
            using var db = CreateContext();
            return await CreateNextWaveService(db, fenceRenewalScopeFactory).TryLaunchNextWavesAsync(cancellationToken);
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
            Assert.Equal(AutopilotIntakeOutcomes.FenceUnavailable, submitted.Outcome);
            using (var db = CreateContext())
            {
                Assert.False(await db.AutopilotEvents.AnyAsync(item => item.ExternalEventId == eventId, cancellationToken));
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

        private AutopilotEventIntake CreateIntake(TaslimDbContext db, IAutopilotLockService? intakeLockService = null) =>
            new(db, new AutopilotEventAuthenticator(Options), new EfAutopilotAuditLog(db), Options, Time,
                intakeLockService ?? new EfAutopilotLockService(db, Options, Time));

        private AutopilotNextWaveService CreateNextWaveService(
            TaslimDbContext db,
            IServiceScopeFactory? fenceRenewalScopeFactory = null)
        {
            fenceRenewalScopeFactory ??= CreateRenewalScopeFactory();
            var allocator = enableGlobalSlotServices
                ? new AutopilotGlobalTaskSlotAllocator(db, TestTaslimScopeId, Time)
                : null;
            var inventory = enableGlobalSlotServices
                ? new AutopilotGlobalTaskInventoryReader(InventorySource, TestTaslimScopeId)
                : null;
            return new(db, Options, launchProviderOverride ?? Provider, CreateIntake(db, CreateLockService(db)), Time,
                NullLogger<AutopilotNextWaveService>.Instance, CreateLockService(db), allocator, inventory,
                notifyCommittedEvent, completionFenceRenewalInterval, fenceRenewalScopeFactory);
        }

        private IServiceScopeFactory CreateRenewalScopeFactory()
        {
            var provider = new ServiceCollection()
                .AddScoped<TaslimDbContext>(_ => CreateContext())
                .AddScoped<IAutopilotLockService>(services => lockServiceOverride
                    ?? new EfAutopilotLockService(services.GetRequiredService<TaslimDbContext>(), Options, Time))
                .BuildServiceProvider();
            lock (renewalServiceProvidersGate)
                renewalServiceProviders.Add(provider);
            return provider.GetRequiredService<IServiceScopeFactory>();
        }

        private AutopilotOrchestrator CreateOrchestrator(TaslimDbContext db) =>
            new(db, CreateLockService(db), CreateNextWaveService(db), Options, Time, NullLogger<AutopilotOrchestrator>.Instance);

        private IAutopilotLockService CreateLockService(TaslimDbContext db) =>
            lockServiceOverride ?? new EfAutopilotLockService(db, Options, Time);

        public void Dispose()
        {
            lock (renewalServiceProvidersGate)
            {
                foreach (var provider in renewalServiceProviders)
                    provider.Dispose();
                renewalServiceProviders.Clear();
            }
            connection?.Close();
            connection?.Dispose();
        }
    }

    private static async Task RunPostgresFenceProcessWorkerAsync()
    {
        var connectionString = await Console.In.ReadLineAsync()
            ?? throw new InvalidOperationException("Missing scoped PostgreSQL test connection input.");
        var resourceKey = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_RESOURCE");
        var heldBy = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_HELD_BY");
        var workerMode = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_WORKER_MODE");
        var readyPath = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_READY_PATH");
        var startGatePath = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_START_GATE");
        var resultPath = RequiredFenceWorkerEnvironment("TASLIM_AUTOPILOT_FENCE_RESULT_PATH");
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.OpenConnectionAsync();
        var workerPid = Environment.ProcessId;
        var readyTemporaryPath = $"{readyPath}.{workerPid}.tmp";
        await File.WriteAllTextAsync(readyTemporaryPath, workerPid.ToString());
        File.Move(readyTemporaryPath, readyPath, overwrite: true);
        await WaitForFenceFileAsync(startGatePath, TimeSpan.FromSeconds(45));

        if (string.Equals(workerMode, "intake", StringComparison.Ordinal))
        {
            var inputJson = await Console.In.ReadLineAsync()
                ?? throw new InvalidOperationException("Missing direct-intake worker request input.");
            var input = JsonSerializer.Deserialize<PostgresFenceIntakeWorkerInput>(inputJson)
                ?? throw new InvalidOperationException("Direct-intake worker request input was invalid.");
            var priorSecret = Environment.GetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET");
            Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", PostgresFenceSigningSecret);
            try
            {
                var intakeOptions = new AutopilotOptions { RequireSignedEvents = true };
                intakeOptions.Normalize();
                var intake = new AutopilotEventIntake(
                    db,
                    new AutopilotEventAuthenticator(intakeOptions),
                    new EfAutopilotAuditLog(db),
                    intakeOptions,
                    TimeProvider.System);
                var intakeResult = await intake.SubmitAsync(input.ToRequest());
                var intakeWorkerResult = new PostgresFenceWorkerResult(
                    workerPid,
                    false,
                    string.Empty,
                    0,
                    null,
                    null,
                    null,
                    intakeResult.Outcome,
                    intakeResult.EventId,
                    intakeResult.Reason);
                await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(intakeWorkerResult));
                Console.WriteLine($"POSTGRES_FENCE_INTAKE_CHILD pid={workerPid} outcome={intakeResult.Outcome} eventId={intakeResult.EventId}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("AUTOPILOT_WEBHOOK_SECRET", priorSecret);
            }
            return;
        }

        if (!string.Equals(workerMode, "lock", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported PostgreSQL fence worker mode '{workerMode}'.");

        var autopilotOptions = new AutopilotOptions();
        autopilotOptions.Normalize();
        var handle = await new EfAutopilotLockService(db, autopilotOptions)
            .AcquireAsync(resourceKey, heldBy);
        var result = new PostgresFenceWorkerResult(
            workerPid,
            handle.IsAcquired,
            handle.OwnerToken,
            handle.FencingToken,
            handle.HeldBy,
            handle.ExpiresAt,
            handle.LeaseServerTimeUtc);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result));
        Console.WriteLine($"POSTGRES_FENCE_CHILD pid={workerPid} heldBy={heldBy} acquired={result.Acquired} token={result.FencingToken}");
    }

    private static PostgresFenceIntakeWorkerInput CreatePostgresFenceCompletionInput(
        WaveLaunchProviderRequest request,
        string externalTaskId,
        int expectedTaskCount)
    {
        var externalEventId = $"evt-pg-fence-{Guid.NewGuid():N}";
        var receipt = JsonSerializer.Serialize(new
        {
            kind = AutopilotGlobalTaskDetailTerminalReceipt.RequiredKind,
            task_id = externalTaskId,
            agent_status = "stopped",
            stop_reason = "finish",
            has_running_background_jobs = false,
            event_id = externalEventId,
        });
        var payload = JsonSerializer.Serialize(new
        {
            waveKey = request.WaveKey,
            taskId = request.TaskKey,
            outcome = AutopilotTaskOutcomes.Succeeded,
            branch = Branch,
            baseSha = BaseSha,
            candidateSha = CandidateSha,
            attempt = 1,
            evidence = receipt,
            checks = CompleteChecks(),
            expectedTaskCount,
            waveComplete = false,
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PostgresFenceIntakeWorkerInput(
            ComputePostgresFenceSignature(timestamp, payload),
            timestamp,
            payload,
            ManusBridgeWaveLaunchProvider.Key,
            externalEventId,
            AutopilotEventTypes.WaveTaskCompleted);
    }

    private static string ComputePostgresFenceSignature(string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(PostgresFenceSigningSecret));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static async Task<PostgresFenceWorkerResult[]> RunPostgresFenceProcessesAsync(
        string connectionString,
        string resourceKey,
        string phase,
        int processCount,
        string workerDirectory,
        string workerMode = "lock",
        IReadOnlyList<PostgresFenceIntakeWorkerInput>? intakeInputs = null)
    {
        if (string.Equals(workerMode, "intake", StringComparison.Ordinal) && intakeInputs?.Count != processCount)
            throw new ArgumentException("An intake request must be supplied for every PostgreSQL intake worker.", nameof(intakeInputs));
        if (!string.Equals(workerMode, "intake", StringComparison.Ordinal) && intakeInputs is not null)
            throw new ArgumentException("Intake inputs are only valid for intake worker mode.", nameof(intakeInputs));

        var roundDirectory = Path.Combine(workerDirectory, phase);
        Directory.CreateDirectory(roundDirectory);
        var startGatePath = Path.Combine(roundDirectory, "start");
        var projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Taslim.Api.Tests.csproj"));
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("Could not locate the API test project for the PostgreSQL child-process fence test.", projectPath);
        var workers = new List<PostgresFenceWorkerProcess>();
        var cleanupFailures = new List<Exception>();
        Exception? primaryFailure = null;
        PostgresFenceWorkerResult[]? processResults = null;

        try
        {
            for (var index = 0; index < processCount; index++)
            {
                var readyPath = Path.Combine(roundDirectory, $"worker-{index}.ready");
                var resultPath = Path.Combine(roundDirectory, $"worker-{index}.json");
                var heldBy = $"pg-fence-{phase}-{index}";
                var startInfo = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Name ?? "Debug";
                startInfo.ArgumentList.Add("test");
                startInfo.ArgumentList.Add(projectPath);
                startInfo.ArgumentList.Add("--no-build");
                startInfo.ArgumentList.Add("--no-restore");
                startInfo.ArgumentList.Add("--configuration");
                startInfo.ArgumentList.Add(configuration);
                startInfo.ArgumentList.Add("--filter");
                startInfo.ArgumentList.Add("FullyQualifiedName~Taslim.Api.Tests.AutopilotNextWaveLaunchTests.Completion_launch_fence_is_exclusive_and_expiry_fenced_across_postgres_testhost_processes");
                startInfo.ArgumentList.Add("--logger");
                startInfo.ArgumentList.Add("console;verbosity=quiet");
                foreach (var name in startInfo.Environment.Keys.Where(name =>
                    name.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("DATABASE", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("DB_", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CONNSTR", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CONNECTIONSTRING", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("POSTGRES", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("NPGSQL", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SQL", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase)).ToArray())
                    startInfo.Environment.Remove(name);
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_PROCESS_WORKER"] = "1";
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_RESOURCE"] = resourceKey;
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_HELD_BY"] = heldBy;
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_WORKER_MODE"] = workerMode;
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_READY_PATH"] = readyPath;
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_START_GATE"] = startGatePath;
                startInfo.Environment["TASLIM_AUTOPILOT_FENCE_RESULT_PATH"] = resultPath;

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException($"Could not start PostgreSQL fence worker {index} for phase {phase}.");
                    await process.StandardInput.WriteLineAsync(connectionString);
                    if (intakeInputs is not null)
                        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(intakeInputs[index]));
                    await process.StandardInput.FlushAsync();
                    process.StandardInput.Close();
                    workers.Add(new PostgresFenceWorkerProcess(
                        process,
                        readyPath,
                        resultPath,
                        process.StandardOutput.ReadToEndAsync(),
                        process.StandardError.ReadToEndAsync()));
                }
                catch (Exception startFailure)
                {
                    var startupCleanupFailures = new List<Exception>();
                    await AttemptKillAndWaitAsync(process, "startup-launcher", startupCleanupFailures);
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception disposeException)
                    {
                        startupCleanupFailures.Add(disposeException);
                    }
                    if (startupCleanupFailures.Count > 0)
                        throw new AggregateException("PostgreSQL fence worker startup and cleanup both failed.", [startFailure, .. startupCleanupFailures]);
                    throw;
                }
            }

            for (var index = 0; index < workers.Count; index++)
            {
                var worker = workers[index];
                var testHostPid = await WaitForFenceWorkerReadyAsync(worker, TimeSpan.FromSeconds(45));
                workers[index] = worker with { WorkerTestHostProcessId = testHostPid };
            }
            await File.WriteAllTextAsync(startGatePath, "go");

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                try
                {
                    await Task.WhenAll(workers.Select(worker => worker.Process.WaitForExitAsync(timeout.Token)));
                }
                catch (OperationCanceledException exception)
                {
                    throw new TimeoutException($"PostgreSQL fence workers did not exit in phase {phase}.", exception);
                }
            }

            var results = new List<PostgresFenceWorkerResult>(workers.Count);
            foreach (var worker in workers)
            {
                var standardOutput = await worker.StandardOutput;
                var standardError = await worker.StandardError;
                if (worker.Process.ExitCode != 0)
                    throw new InvalidOperationException($"PostgreSQL fence child PID {worker.Process.Id} exited {worker.Process.ExitCode}. stdout={standardOutput}\nstderr={standardError}");
                if (!File.Exists(worker.ResultPath))
                    throw new InvalidOperationException($"PostgreSQL fence child PID {worker.Process.Id} exited without a result file. stdout={standardOutput}\nstderr={standardError}");

                var result = JsonSerializer.Deserialize<PostgresFenceWorkerResult>(await File.ReadAllTextAsync(worker.ResultPath));
                if (result is null || result.WorkerProcessId != worker.WorkerTestHostProcessId)
                    throw new InvalidOperationException($"PostgreSQL fence child PID {worker.Process.Id} produced an invalid result. stdout={standardOutput}\nstderr={standardError}");
                results.Add(result);
            }

            processResults = results.ToArray();
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            foreach (var worker in workers)
            {
                try
                {
                    await CleanupPostgresFenceWorkerAsync(worker);
                }
                catch (Exception cleanupException)
                {
                    cleanupFailures.Add(cleanupException);
                }
                finally
                {
                    try
                    {
                        worker.Process.Dispose();
                    }
                    catch (Exception disposeException)
                    {
                        cleanupFailures.Add(disposeException);
                    }
                }
            }
        }

        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
                throw new AggregateException("PostgreSQL fence child test and process cleanup failed.", [primaryFailure, .. cleanupFailures]);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("PostgreSQL fence child process cleanup failed.", cleanupFailures);
        return processResults ?? throw new InvalidOperationException("PostgreSQL fence child processes produced no result.");
    }

    private static async Task<int> WaitForFenceWorkerReadyAsync(PostgresFenceWorkerProcess worker, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(worker.ReadyPath))
        {
            if (worker.Process.HasExited)
            {
                var standardOutput = await worker.StandardOutput;
                var standardError = await worker.StandardError;
                throw new InvalidOperationException($"PostgreSQL fence child PID {worker.Process.Id} exited before its barrier. stdout={standardOutput}\nstderr={standardError}");
            }
            if (timer.Elapsed >= timeout)
                throw new TimeoutException($"PostgreSQL fence child PID {worker.Process.Id} did not reach its start barrier.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        var readyText = await File.ReadAllTextAsync(worker.ReadyPath);
        if (!int.TryParse(readyText, out var testHostPid) || testHostPid <= 0)
            throw new InvalidOperationException($"PostgreSQL fence child PID {worker.Process.Id} wrote an invalid testhost PID.");
        if (OperatingSystem.IsLinux() && !IsLinuxProcessDescendantOf(testHostPid, worker.Process.Id))
            throw new InvalidOperationException($"PostgreSQL fence testhost PID {testHostPid} is not a descendant of its launched process PID {worker.Process.Id}.");
        return testHostPid;
    }

    private static bool IsLinuxProcessDescendantOf(int processId, int ancestorProcessId)
    {
        var currentProcessId = processId;
        for (var depth = 0; depth < 32; depth++)
        {
            var statusPath = $"/proc/{currentProcessId}/status";
            if (!File.Exists(statusPath))
                return false;
            var parentLine = File.ReadLines(statusPath)
                .FirstOrDefault(line => line.StartsWith("PPid:", StringComparison.Ordinal));
            if (parentLine is null || !int.TryParse(parentLine.AsSpan("PPid:".Length).Trim(), out var parentProcessId))
                return false;
            if (parentProcessId == ancestorProcessId)
                return true;
            if (parentProcessId <= 1 || parentProcessId == currentProcessId)
                return false;
            currentProcessId = parentProcessId;
        }
        return false;
    }

    private static async Task CleanupPostgresFenceWorkerAsync(PostgresFenceWorkerProcess worker)
    {
        var failures = new List<Exception>();
        var testHostPid = worker.WorkerTestHostProcessId;
        if (testHostPid <= 0)
        {
            try
            {
                if (!File.Exists(worker.ReadyPath))
                    failures.Add(new FileNotFoundException("No ready file was available to identify the PostgreSQL fence testhost for cleanup.", worker.ReadyPath));
                else if (!int.TryParse(await File.ReadAllTextAsync(worker.ReadyPath), out testHostPid) || testHostPid <= 0)
                    failures.Add(new InvalidDataException("The PostgreSQL fence testhost ready file did not contain a valid PID."));
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (testHostPid > 0)
        {
            Process? testHost = null;
            try
            {
                testHost = Process.GetProcessById(testHostPid);
            }
            catch (ArgumentException)
            {
                // The recorded testhost PID no longer exists, so there is no process left to clean.
                Console.Error.WriteLine($"POSTGRES_FENCE_TESTHOST_ALREADY_EXITED pid={testHostPid}");
            }
            catch (Exception exception)
            {
                if (testHost is not null && IsProcessKnownExited(testHost))
                    Console.Error.WriteLine($"POSTGRES_FENCE_TESTHOST_EXITED_DURING_CLEANUP pid={testHostPid}");
                else
                    failures.Add(exception);
            }
            try
            {
                if (testHost is not null)
                    await AttemptKillAndWaitAsync(testHost, "testhost", failures);
            }
            catch (Exception cleanupException)
            {
                failures.Add(cleanupException);
            }
            finally
            {
                if (testHost is not null)
                {
                    try
                    {
                        testHost.Dispose();
                    }
                    catch (Exception disposeException)
                    {
                        failures.Add(disposeException);
                    }
                }
            }
        }

        await AttemptKillAndWaitAsync(worker.Process, "launcher", failures);

        if (failures.Count > 0)
            throw new AggregateException("One or more PostgreSQL fence child processes could not be cleaned up.", failures);
    }

    private static async Task AttemptKillAndWaitAsync(Process process, string role, List<Exception> failures)
    {
        try
        {
            if (process.HasExited)
                Console.Error.WriteLine($"POSTGRES_FENCE_PROCESS_ALREADY_EXITED role={role} pid={process.Id}");
            else
                process.Kill(entireProcessTree: true);
        }
        catch (Exception killException)
        {
            if (IsProcessKnownExited(process))
                Console.Error.WriteLine($"POSTGRES_FENCE_PROCESS_EXITED_DURING_KILL role={role}");
            else
                failures.Add(killException);
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception waitException)
        {
            if (IsProcessKnownExited(process))
                Console.Error.WriteLine($"POSTGRES_FENCE_PROCESS_EXITED_DURING_WAIT role={role}");
            else
                failures.Add(waitException);
        }
    }

    private static bool IsProcessKnownExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTruthyEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal);
    }

    private static async Task WaitForFenceFileAsync(string path, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (timer.Elapsed >= timeout)
                throw new TimeoutException($"Timed out waiting for PostgreSQL fence barrier file '{Path.GetFileName(path)}'.");
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static string RequiredFenceWorkerEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Required PostgreSQL fence worker environment value '{name}' is missing.");

    private sealed record PostgresFenceWorkerProcess(
        Process Process,
        string ReadyPath,
        string ResultPath,
        Task<string> StandardOutput,
        Task<string> StandardError)
    {
        public int WorkerTestHostProcessId { get; init; }
    }

    private sealed record PostgresFenceIntakeWorkerInput(
        string SignatureHeader,
        string TimestampHeader,
        string PayloadJson,
        string SourceSystem,
        string ExternalEventId,
        string EventType)
    {
        public AutopilotIntakeRequest ToRequest() => new(
            SignatureHeader,
            TimestampHeader,
            PayloadJson,
            SourceSystem,
            ExternalEventId,
            EventType);
    }

    private sealed record PostgresFenceWorkerResult(
        int WorkerProcessId,
        bool Acquired,
        string OwnerToken,
        long FencingToken,
        string? HeldBy,
        DateTime? ExpiresAt,
        DateTime? LeaseServerTimeUtc,
        string? IntakeOutcome = null,
        Guid? IntakeEventId = null,
        string? IntakeReason = null)
    {
        public AutopilotLockHandle ToHandle(string resourceKey) =>
            new(resourceKey, OwnerToken, FencingToken, Acquired, HeldBy, ExpiresAt)
            {
                LeaseServerTimeUtc = LeaseServerTimeUtc,
            };
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

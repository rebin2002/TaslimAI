using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

public sealed record AutopilotControlSnapshot(
    bool FeatureEnabled,
    bool DryRun,
    bool Paused,
    bool KillSwitchEngaged,
    bool ChargingEnabled,
    bool PaidProvidersEnabled,
    int MaxConcurrency,
    int WatchdogIntervalMinutes,
    string? LastReason,
    DateTime? UpdatedAt);

internal sealed class AutopilotCycleContext
{
    public List<string> PlannedActions { get; } = [];

    public List<AutopilotAuditEvent> Audit { get; } = [];

    public List<Guid> AuditedEventIds { get; } = [];

    public int EventsExamined { get; set; }

    public int EventsProcessed { get; set; }

    public int EventsSkipped { get; set; }

    public string Outcome { get; set; } = "idle";

    public string? Reason { get; set; }

    public AutopilotCycleResult ToResult(AutopilotOptions options) =>
        new(options.Enabled, options.DryRun, Outcome, Reason, EventsExamined, EventsProcessed, EventsSkipped, PlannedActions, AuditedEventIds);
}

/// <summary>
/// The Autopilot Controller state machine. It consumes durable completion
/// events, inspects wave task results, retries ordinary failures within bounds,
/// and advances a wave through the integration gate, final release gate,
/// controlled release handoff, production smoke, and next-wave eligibility.
/// It never bypasses a failed gate, never performs destructive database work,
/// and never enables charging or paid providers.
/// </summary>
public interface IAutopilotOrchestrator
{
    Task<AutopilotCycleResult> ProcessPendingEventsAsync(CancellationToken cancellationToken = default);

    Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task<AutopilotCycleResult> ReconcileAsync(string reason, CancellationToken cancellationToken = default);

    Task<AutopilotControlSnapshot> GetControlAsync(CancellationToken cancellationToken = default);

    Task<AutopilotControlSnapshot> SetControlAsync(bool? paused, bool? killSwitch, Guid? actorUserId, string reason, CancellationToken cancellationToken = default);
}

public sealed class AutopilotOrchestrator(
    TaslimDbContext db,
    IAutopilotLockService locks,
    IAutopilotNextWaveService nextWaves,
    AutopilotOptions options,
    TimeProvider timeProvider,
    ILogger<AutopilotOrchestrator> logger) : IAutopilotOrchestrator
{
    private readonly AutopilotOptions settings = options;

    public async Task<AutopilotCycleResult> ProcessPendingEventsAsync(CancellationToken cancellationToken = default)
    {
        var context = new AutopilotCycleContext();
        var control = await EnsureControlAsync(cancellationToken);
        if (!await CanRunAsync(control, context, cancellationToken)) return context.ToResult(settings);

        var now = UtcNow();
        var pending = await db.AutopilotEvents.AsNoTracking()
            .Where(item => item.Status == AutopilotEventStatuses.Queued
                || item.Status == AutopilotEventStatuses.Received)
            .Where(item => item.NextAttemptAt == null || item.NextAttemptAt <= now)
            .OrderBy(item => item.ReceivedAt)
            .Take(Math.Max(1, settings.MaxConcurrency))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            context.Outcome = "idle";
            context.Reason = "no_pending_events";
            return context.ToResult(settings);
        }

        var processed = 0;
        foreach (var eventId in pending)
        {
            var result = await ProcessEventInternalAsync(eventId, context, cancellationToken);
            if (result) processed++;
        }

        context.Outcome = processed > 0 ? "cycles_completed" : "idle";
        context.Reason ??= processed > 0 ? null : "no_events_advanced";
        return context.ToResult(settings);
    }

    public async Task<AutopilotCycleResult> ProcessEventAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var context = new AutopilotCycleContext();
        var control = await EnsureControlAsync(cancellationToken);
        if (!await CanRunAsync(control, context, cancellationToken)) return context.ToResult(settings);
        context.EventsExamined = 1;
        await ProcessEventInternalAsync(eventId, context, cancellationToken);
        context.Outcome = context.EventsProcessed > 0 ? "cycle_completed" : context.Outcome;
        return context.ToResult(settings);
    }

    public async Task<AutopilotCycleResult> ReconcileAsync(string reason, CancellationToken cancellationToken = default)
    {
        var context = new AutopilotCycleContext();
        var control = await EnsureControlAsync(cancellationToken);
        if (!await CanRunAsync(control, context, cancellationToken)) return context.ToResult(settings);

        var now = UtcNow();
        var staleBefore = now.AddMinutes(-Math.Max(5, settings.EventStaleAfterMinutes));

        var staleEvents = await db.AutopilotEvents
            .Where(item => (item.Status == AutopilotEventStatuses.Received || item.Status == AutopilotEventStatuses.Queued)
                && item.ReceivedAt < staleBefore)
            .OrderBy(item => item.ReceivedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var expiredClaims = await db.AutopilotEvents
            .Where(item => item.Status == AutopilotEventStatuses.Processing
                && item.ProcessingStartedAt != null
                && item.ProcessingStartedAt < staleBefore)
            .OrderBy(item => item.ProcessingStartedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var recovered = 0;
        foreach (var stale in staleEvents.Concat(expiredClaims))
        {
            if (stale.ReconciliationCount >= Math.Max(1, settings.MaxWatchdogReconciliations))
            {
                stale.Status = AutopilotEventStatuses.Failed;
                stale.Reason = "watchdog_reconciliation_exhausted";
                stale.ProcessedAt = now;
                Record(context, AutopilotAuditFactory.Create(
                    AutopilotAuditActions.WatchdogReconciled,
                    AutopilotAuditOutcomes.Failed,
                    reason: "watchdog_reconciliation_exhausted",
                    waveKey: stale.WaveKey,
                    taskId: stale.TaskId,
                    attempt: stale.Attempt,
                    targetId: stale.Id,
                    dryRun: settings.DryRun));
                continue;
            }

            stale.Status = AutopilotEventStatuses.Queued;
            stale.QueuedAt ??= now;
            stale.NextAttemptAt = now;
            stale.ProcessingStartedAt = null;
            stale.ReconciliationCount++;
            recovered++;
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.WatchdogReconciled,
                AutopilotAuditOutcomes.Recorded,
                reason: reason,
                statusDetail: "missing_or_lost_event_requeued",
                waveKey: stale.WaveKey,
                taskId: stale.TaskId,
                attempt: stale.Attempt,
                targetId: stale.Id,
                dryRun: settings.DryRun));
        }

        if (staleEvents.Count > 0 || expiredClaims.Count > 0)
            await FlushAsync(context, cancellationToken);

        // A wave whose tasks are all terminal but whose run never advanced means the
        // completion event was lost. Re-drive the run state machine from evidence.
        var stalledRuns = await db.AutopilotRuns
            .Where(item => item.State == AutopilotRunStates.AwaitingEvents || item.State == AutopilotRunStates.InProgress)
            .Where(item => item.LastEventAt != null && item.LastEventAt < staleBefore)
            .OrderBy(item => item.UpdatedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var run in stalledRuns)
        {
            var hasPendingEvent = await db.AutopilotEvents.AsNoTracking()
                .AnyAsync(item => item.WaveKey == run.WaveKey
                    && !AutopilotEventStatuses.Terminal.Contains(item.Status), cancellationToken);
            if (hasPendingEvent) continue;

            await AdvanceRunAsync(run, context, recovered: true, cancellationToken);
            await FlushAsync(context, cancellationToken);
        }

        context.EventsExamined = staleEvents.Count + expiredClaims.Count;
        context.EventsProcessed = recovered;
        context.Reason = reason;

        // Bounded follow-on wave pass: launch what a gate-green wave is entitled to
        // launch, then reconcile anything already launched (missed webhooks and
        // restarts). Both steps are bounded and respect the kill switch.
        MergeNextWave(context, await nextWaves.TryLaunchNextWavesAsync(cancellationToken));
        MergeNextWave(context, await nextWaves.ReconcileLaunchesAsync(cancellationToken));

        var processedCycle = await ProcessPendingEventsAsync(cancellationToken);
        context.Outcome = recovered > 0 || processedCycle.EventsProcessed > 0 ? "reconciled" : "no_reconciliation_needed";
        foreach (var action in processedCycle.PlannedActions) context.PlannedActions.Add(action);
        foreach (var id in processedCycle.AuditedEventIds) context.AuditedEventIds.Add(id);
        context.EventsProcessed += processedCycle.EventsProcessed;
        return context.ToResult(settings);
    }

    private static void MergeNextWave(AutopilotCycleContext context, AutopilotNextWaveCycleResult result)
    {
        foreach (var action in result.PlannedActions) context.PlannedActions.Add(action);
    }

    public async Task<AutopilotControlSnapshot> GetControlAsync(CancellationToken cancellationToken = default)
    {
        var control = await EnsureControlAsync(cancellationToken);
        return Snapshot(control);
    }

    public async Task<AutopilotControlSnapshot> SetControlAsync(bool? paused, bool? killSwitch, Guid? actorUserId, string reason, CancellationToken cancellationToken = default)
    {
        var control = await EnsureControlAsync(cancellationToken);
        if (paused.HasValue) control.Paused = paused.Value;
        if (killSwitch.HasValue) control.KillSwitchEngaged = killSwitch.Value;
        control.LastReason = reason;
        control.LastActor = actorUserId?.ToString();
        control.UpdatedAt = UtcNow();
        db.AutopilotAuditEvents.Add(AutopilotAuditFactory.Create(
            AutopilotAuditActions.ControlChanged,
            AutopilotAuditOutcomes.Recorded,
            reason: reason,
            statusDetail: $"paused={control.Paused};killSwitch={control.KillSwitchEngaged}",
            actorUserId: actorUserId,
            dryRun: settings.DryRun));
        await db.SaveChangesAsync(cancellationToken);
        return Snapshot(control);
    }

    private async Task<bool> ProcessEventInternalAsync(Guid eventId, AutopilotCycleContext context, CancellationToken cancellationToken)
    {
        context.EventsExamined++;
        var intakeEvent = await db.AutopilotEvents.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (intakeEvent is null)
        {
            context.EventsSkipped++;
            context.Outcome = "not_found";
            context.Reason = "event_not_found";
            return false;
        }

        if (AutopilotEventStatuses.Terminal.Contains(intakeEvent.Status))
        {
            context.EventsSkipped++;
            context.Outcome = "terminal_skipped";
            context.Reason = "event_already_terminal";
            return false;
        }

        var now = UtcNow();
        var claimed = await db.AutopilotEvents
            .Where(item => item.Id == eventId
                && item.Status != AutopilotEventStatuses.Processing
                && item.ConcurrencyToken == intakeEvent.ConcurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AutopilotEventStatuses.Processing)
                .SetProperty(item => item.ProcessingStartedAt, now)
                .SetProperty(item => item.Attempt, item => item.Attempt + 1), cancellationToken);

        if (claimed == 0)
        {
            context.EventsSkipped++;
            context.Outcome = "contended";
            context.Reason = "event_claimed_by_another_worker";
            return false;
        }

        await db.Entry(intakeEvent).ReloadAsync(cancellationToken);

        AutopilotCompletionSignal? signal;
        try
        {
            signal = string.IsNullOrWhiteSpace(intakeEvent.SignalJson)
                ? null
                : JsonSerializer.Deserialize<AutopilotCompletionSignal>(intakeEvent.SignalJson);
        }
        catch (JsonException)
        {
            signal = null;
        }

        if (signal is null || string.IsNullOrWhiteSpace(signal.WaveKey) || string.IsNullOrWhiteSpace(signal.TaskId))
        {
            intakeEvent.Status = AutopilotEventStatuses.RejectedInvalid;
            intakeEvent.Reason = "signal_unreadable";
            intakeEvent.ProcessedAt = now;
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.EventRejected,
                AutopilotAuditOutcomes.Failed,
                reason: "signal_unreadable",
                targetId: intakeEvent.Id,
                dryRun: settings.DryRun));
            await FlushAsync(context, cancellationToken);
            context.EventsSkipped++;
            return false;
        }

        if (!AutopilotTaskOutcomes.TryNormalize(signal.Outcome, out var normalizedOutcome))
        {
            intakeEvent.Status = AutopilotEventStatuses.RejectedInvalid;
            intakeEvent.Reason = "outcome_invalid";
            intakeEvent.ProcessedAt = now;
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.EventRejected,
                AutopilotAuditOutcomes.Blocked,
                reason: "outcome_invalid",
                waveKey: signal.WaveKey,
                taskId: signal.TaskId,
                targetId: intakeEvent.Id,
                dryRun: settings.DryRun));
            await FlushAsync(context, cancellationToken);
            context.EventsSkipped++;
            context.Outcome = "invalid_event";
            context.Reason = "outcome_invalid";
            return false;
        }

        var lockHandle = await locks.AcquireAsync(EfAutopilotLockService.WaveResource(signal.WaveKey!), "autopilot-controller", cancellationToken);
        if (!lockHandle.IsAcquired)
        {
            intakeEvent.Status = AutopilotEventStatuses.Queued;
            intakeEvent.ProcessingStartedAt = null;
            intakeEvent.NextAttemptAt = now.AddSeconds(Math.Max(5, settings.RetryBaseDelaySeconds));
            intakeEvent.Reason = "wave_lock_contended";
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.LockContended,
                AutopilotAuditOutcomes.Contended,
                reason: "wave_lock_contended",
                waveKey: signal.WaveKey,
                taskId: signal.TaskId,
                targetId: intakeEvent.Id,
                dryRun: settings.DryRun));
            await FlushAsync(context, cancellationToken);
            context.EventsSkipped++;
            context.Outcome = "contended";
            context.Reason = "wave_lock_contended";
            return false;
        }

        try
        {
            await ApplySignalAsync(intakeEvent, signal, normalizedOutcome, context, cancellationToken);
            context.EventsProcessed++;
            context.AuditedEventIds.Add(intakeEvent.Id);
            return true;
        }
        finally
        {
            await locks.ReleaseAsync(lockHandle, CancellationToken.None);
        }
    }

    private async Task ApplySignalAsync(
        AutopilotEvent intakeEvent,
        AutopilotCompletionSignal signal,
        string outcome,
        AutopilotCycleContext context,
        CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var waveKey = signal.WaveKey!.Trim();
        var taskId = signal.TaskId!.Trim();

        var run = await db.AutopilotRuns.FirstOrDefaultAsync(item => item.WaveKey == waveKey, cancellationToken);
        if (run is null)
        {
            run = new AutopilotRun
            {
                Id = Guid.NewGuid(),
                WaveKey = waveKey,
                RunKey = $"autopilot:{waveKey}",
                State = AutopilotRunStates.AwaitingEvents,
                BaseSha = signal.BaseSha ?? string.Empty,
                CandidateSha = signal.CandidateSha ?? string.Empty,
                Branch = signal.Branch ?? string.Empty,
                CorrelationId = intakeEvent.Id.ToString("N"),
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            };
            db.AutopilotRuns.Add(run);
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.RunStateChanged,
                AutopilotAuditOutcomes.Recorded,
                reason: "run_created",
                waveKey: waveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
        }
        else if (!string.Equals(run.CandidateSha, signal.CandidateSha ?? run.CandidateSha, StringComparison.Ordinal))
        {
            // Immutable candidate SHA: refuse to repoint a run at a different commit.
            intakeEvent.Status = AutopilotEventStatuses.Failed;
            intakeEvent.Reason = "immutable_candidate_sha_conflict";
            intakeEvent.ProcessedAt = now;
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.SafetyStop,
                AutopilotAuditOutcomes.Blocked,
                reason: "immutable_candidate_sha_conflict",
                waveKey: waveKey,
                taskId: taskId,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: intakeEvent.Id,
                dryRun: settings.DryRun));
            await FlushAsync(context, cancellationToken);
            return;
        }

        var task = await db.AutopilotWaveTasks
            .FirstOrDefaultAsync(item => item.RunId == run.Id && item.TaskId == taskId, cancellationToken);
        if (task is null)
        {
            task = new AutopilotWaveTask
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                WaveKey = waveKey,
                TaskId = taskId,
                State = AutopilotTaskStates.AwaitingResult,
                Attempt = 0,
                MaxAttempts = Math.Max(0, settings.MaxTaskAttempts),
                Branch = signal.Branch ?? run.Branch,
                BaseSha = signal.BaseSha ?? run.BaseSha,
                CandidateSha = signal.CandidateSha ?? run.CandidateSha,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AutopilotWaveTasks.Add(task);
        }

        var failureClass = AutopilotFailureClasses.Normalize(signal.FailureClass);
        var attempt = Math.Max(0, signal.Attempt ?? task.Attempt);
        var checks = signal.Checks ?? [];

        task.Attempt = attempt;
        task.MaxAttempts = Math.Max(0, settings.MaxTaskAttempts);
        task.EvidenceSummary = Bounded(signal.Evidence, 600);
        task.ChecksJson = checks.Count == 0 ? task.ChecksJson : JsonSerializer.Serialize(checks);
        task.Branch = signal.Branch ?? task.Branch;
        task.BaseSha = signal.BaseSha ?? task.BaseSha;
        task.CandidateSha = signal.CandidateSha ?? task.CandidateSha;
        task.LastEventAt = now;
        task.UpdatedAt = now;

        var previousTaskState = task.State;
        var previousRunState = run.State;

        switch (outcome)
        {
            case AutopilotTaskOutcomes.Succeeded:
                task.State = AutopilotTaskStates.Succeeded;
                task.FailureClass = AutopilotFailureClasses.None;
                task.RequiresHumanDecision = false;
                task.HumanDecisionKind = null;
                task.NextAttemptAt = null;
                task.CompletedAt = now;
                task.LastReason = "task_succeeded";
                break;

            case AutopilotTaskOutcomes.Failed:
                task.FailureClass = failureClass;
                if (AutopilotRetryPolicy.CanRetry(failureClass, attempt, settings.MaxTaskAttempts))
                {
                    task.State = AutopilotTaskStates.RetryScheduled;
                    var backoff = AutopilotRetryPolicy.ComputeBackoff(attempt, settings);
                    task.NextAttemptAt = now.Add(backoff);
                    task.LastReason = $"ordinary_failure_retry_{attempt + 1}";
                    context.PlannedActions.Add($"retry_task:{taskId}:attempt_{attempt + 1}:backoff_seconds_{Math.Round(backoff.TotalSeconds)}");
                    Record(context, AutopilotAuditFactory.Create(
                        AutopilotAuditActions.TaskRetryScheduled,
                        AutopilotAuditOutcomes.Recorded,
                        reason: "ordinary_deterministic_repair",
                        statusDetail: task.LastReason,
                        waveKey: waveKey,
                        taskId: taskId,
                        taskState: task.State,
                        attempt: attempt,
                        branch: task.Branch,
                        baseSha: task.BaseSha,
                        candidateSha: task.CandidateSha,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                }
                else
                {
                    task.LastReason = RationaleForTerminalFailure(failureClass, attempt);
                    if (AutopilotFailureClasses.Blocking.Contains(failureClass))
                    {
                        task.State = AutopilotTaskStates.BlockedHuman;
                        task.HumanDecisionKind = DecisionKindForFailure(failureClass);
                    }
                    else
                    {
                        task.State = AutopilotTaskStates.TerminalFailed;
                        task.CompletedAt = now;
                        task.HumanDecisionKind = AutopilotHumanDecisions.RepairEscalation;
                    }
                    task.RequiresHumanDecision = true;
                    Record(context, AutopilotAuditFactory.Create(
                        AutopilotAuditActions.TaskRetryExhausted,
                        AutopilotAuditOutcomes.Blocked,
                        reason: failureClass,
                        statusDetail: task.LastReason,
                        waveKey: waveKey,
                        taskId: taskId,
                        taskState: task.State,
                        attempt: attempt,
                        branch: task.Branch,
                        baseSha: task.BaseSha,
                        candidateSha: task.CandidateSha,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                }
                break;

            case AutopilotTaskOutcomes.Blocked:
                task.State = AutopilotTaskStates.BlockedHuman;
                task.RequiresHumanDecision = true;
                task.HumanDecisionKind = DecisionKindForFailure(failureClass, signal.HumanDecisionKind);
                task.LastReason = "task_blocked_pending_human";
                break;

            default:
                task.State = AutopilotTaskStates.TerminalFailed;
                task.FailureClass = failureClass == AutopilotFailureClasses.None ? AutopilotFailureClasses.Unknown : failureClass;
                task.LastReason = "task_cancelled";
                task.CompletedAt = now;
                break;
        }

        if (!string.Equals(previousTaskState, task.State, StringComparison.OrdinalIgnoreCase))
        {
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.TaskStateChanged,
                AutopilotAuditOutcomes.Recorded,
                reason: task.LastReason,
                statusDetail: $"{previousTaskState}->{task.State}",
                waveKey: waveKey,
                taskId: taskId,
                taskState: task.State,
                attempt: attempt,
                branch: task.Branch,
                baseSha: task.BaseSha,
                candidateSha: task.CandidateSha,
                targetId: task.Id,
                dryRun: settings.DryRun));
        }

        intakeEvent.Status = AutopilotEventStatuses.Processed;
        intakeEvent.ProcessedAt = now;
        intakeEvent.Reason = outcome;
        intakeEvent.RunId = run.Id;
        intakeEvent.WaveTaskRecordId = task.Id;

        run.LastEventAt = now;
        run.UpdatedAt = now;
        if (run.Branch.Length == 0 && !string.IsNullOrWhiteSpace(signal.Branch)) run.Branch = signal.Branch!;
        if (run.BaseSha.Length == 0 && !string.IsNullOrWhiteSpace(signal.BaseSha)) run.BaseSha = signal.BaseSha!;
        if (run.CandidateSha.Length == 0 && !string.IsNullOrWhiteSpace(signal.CandidateSha)) run.CandidateSha = signal.CandidateSha!;
        if (signal.ExpectedTaskCount is > 0) run.ExpectedTaskCount = Math.Max(run.ExpectedTaskCount, signal.ExpectedTaskCount.Value);
        if (signal.WaveComplete == true || string.Equals(intakeEvent.EventType, AutopilotEventTypes.WaveCompleted, StringComparison.OrdinalIgnoreCase))
        {
            run.WaveClosed = true;
            run.WaveCompletedAt ??= now;
        }

        Record(context, AutopilotAuditFactory.Create(
            AutopilotAuditActions.EventProcessed,
            AutopilotAuditOutcomes.Succeeded,
            reason: outcome,
            statusDetail: $"failureClass={failureClass}",
            waveKey: waveKey,
            taskId: taskId,
            taskState: task.State,
            attempt: attempt,
            branch: task.Branch,
            baseSha: task.BaseSha,
            candidateSha: task.CandidateSha,
            targetId: intakeEvent.Id,
            dryRun: settings.DryRun));

        // Persist the run/task evidence before the state machine reads it back.
        await FlushAsync(context, cancellationToken);
        await AdvanceRunAsync(run, context, recovered: false, cancellationToken);
        await FlushAsync(context, cancellationToken);

        if (!string.Equals(previousRunState, run.State, StringComparison.OrdinalIgnoreCase))
            logger.LogInformation(
                "Autopilot run advanced. WaveKey={WaveKey}; From={From}; To={To}; Branch={Branch}; CandidateSha={CandidateSha}; DryRun={DryRun}",
                run.WaveKey, previousRunState, run.State, run.Branch, run.CandidateSha, settings.DryRun);
    }

    private async Task AdvanceRunAsync(AutopilotRun run, AutopilotCycleContext context, bool recovered, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var tasks = await db.AutopilotWaveTasks.Where(item => item.RunId == run.Id).ToListAsync(cancellationToken);

        run.TaskCount = tasks.Count;
        run.SucceededTaskCount = tasks.Count(item => item.State == AutopilotTaskStates.Succeeded);
        run.FailedTaskCount = tasks.Count(item => item.State == AutopilotTaskStates.TerminalFailed);
        run.RetryScheduledCount = tasks.Count(item => item.State == AutopilotTaskStates.RetryScheduled);
        run.UpdatedAt = now;
        if (recovered) run.ReconciliationCount++;

        // Once evidence exists the run is actively in progress; every later
        // transition is declared from that state.
        if (string.Equals(run.State, AutopilotRunStates.Planned, StringComparison.OrdinalIgnoreCase)
            || string.Equals(run.State, AutopilotRunStates.AwaitingEvents, StringComparison.OrdinalIgnoreCase))
            TransitionRun(run, AutopilotRunStates.InProgress, "evidence_received", context);

        // Any terminal task failure stops the wave: the controller never advances a
        // wave to an integration or release gate while a task is unresolved.
        var blockedTask = tasks.FirstOrDefault(item => item.State == AutopilotTaskStates.BlockedHuman
            || item.State == AutopilotTaskStates.TerminalFailed);

        if (blockedTask is not null)
        {
            var decision = blockedTask.HumanDecisionKind
                ?? AutopilotHumanDecisionPolicy.Evaluate(settings, blockedTask.FailureClass, blockedTask.RequiresHumanDecision, null, integrationGatePassed: false)
                ?? AutopilotHumanDecisions.SecurityAmbiguity;
            run.HumanDecisionRequired = decision;
            run.LastReason = blockedTask.FailureClass ?? AutopilotFailureClasses.Unknown;
            run.LastStatusDetail = $"blocked_by_task:{blockedTask.TaskId}";
            TransitionRun(run, run.IntegrationGatePassed ? AutopilotRunStates.ReleaseBlocked : AutopilotRunStates.IntegrationFailed, "blocking_task_failure", context);
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.SafetyStop,
                AutopilotAuditOutcomes.Blocked,
                reason: run.LastReason,
                statusDetail: run.LastStatusDetail,
                waveKey: run.WaveKey,
                taskId: blockedTask.TaskId,
                runState: run.State,
                taskState: blockedTask.State,
                attempt: blockedTask.Attempt,
                branch: blockedTask.Branch,
                baseSha: blockedTask.BaseSha,
                candidateSha: blockedTask.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.HumanDecisionRequired,
                AutopilotAuditOutcomes.Required,
                reason: decision,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
            return;
        }

        if (tasks.Any(item => item.State == AutopilotTaskStates.RetryScheduled)
            || tasks.Any(item => item.State is AutopilotTaskStates.Pending or AutopilotTaskStates.Running or AutopilotTaskStates.AwaitingResult))
        {
            run.LastStatusDetail = $"in_progress:{run.SucceededTaskCount}/{run.TaskCount}";
            TransitionRun(run, AutopilotRunStates.InProgress, "partial_wave_in_progress", context);
            if (tasks.Count > 0 && tasks.All(item => item.State == AutopilotTaskStates.RetryScheduled))
                context.PlannedActions.Add($"await_retry:{run.WaveKey}");
            return;
        }

        // A wave with no declaration and no explicit closure cannot be judged
        // complete: the controller refuses to run an integration gate on a
        // partial wave that merely looks finished.
        var waveComplete = run.ExpectedTaskCount > 0
            ? run.SucceededTaskCount >= run.ExpectedTaskCount
            : run.WaveClosed && tasks.Count > 0 && run.SucceededTaskCount == tasks.Count;

        if (tasks.Count == 0 || !waveComplete || run.SucceededTaskCount != tasks.Count)
        {
            run.LastStatusDetail = $"waiting:{run.SucceededTaskCount}/{Math.Max(run.ExpectedTaskCount, tasks.Count)}";
            TransitionRun(
                run,
                tasks.Count > 0 ? AutopilotRunStates.InProgress : AutopilotRunStates.AwaitingEvents,
                "awaiting_declared_wave_completion",
                context);
            return;
        }

        run.LastStatusDetail = "all_tasks_succeeded";
        TransitionRun(run, AutopilotRunStates.TasksComplete, "all_tasks_succeeded", context);
        TransitionRun(run, AutopilotRunStates.IntegrationGate, "integration_gate_started", context);

        var integrationInput = BuildGateInput(run, tasks);
        var integrationGate = await EvaluateGateAsync(run, AutopilotGateKinds.Integration, integrationInput, integrationGatePassed: false, context, cancellationToken);
        if (!integrationGate.Passed)
        {
            run.IntegrationGatePassed = false;
            run.LastReason = integrationGate.ReasonCode;
            run.LastStatusDetail = $"failed_checks:{string.Join(',', integrationGate.FailedChecks)}";
            TransitionRun(run, AutopilotRunStates.IntegrationFailed, "integration_gate_failed", context);
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.SafetyStop,
                AutopilotAuditOutcomes.Blocked,
                reason: integrationGate.ReasonCode,
                statusDetail: run.LastStatusDetail,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
            return;
        }

        run.IntegrationGatePassed = true;
        TransitionRun(run, AutopilotRunStates.ReleaseGate, "final_release_gate_started", context);

        var releaseGate = await EvaluateGateAsync(run, AutopilotGateKinds.Release, integrationInput, integrationGatePassed: true, context, cancellationToken);
        if (!releaseGate.Passed)
        {
            run.ReleaseGatePassed = false;
            run.LastReason = releaseGate.ReasonCode;
            run.LastStatusDetail = $"failed_checks:{string.Join(',', releaseGate.FailedChecks)}";
            run.HumanDecisionRequired = AutopilotHumanDecisions.SecurityAmbiguity;
            TransitionRun(run, AutopilotRunStates.ReleaseBlocked, "release_gate_failed", context);
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.SafetyStop,
                AutopilotAuditOutcomes.Blocked,
                reason: releaseGate.ReasonCode,
                statusDetail: run.LastStatusDetail,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.HumanDecisionRequired,
                AutopilotAuditOutcomes.Required,
                reason: run.HumanDecisionRequired,
                waveKey: run.WaveKey,
                runState: run.State,
                targetId: run.Id,
                dryRun: settings.DryRun));
            return;
        }

        run.ReleaseGatePassed = true;
        TransitionRun(run, AutopilotRunStates.ReleaseEligible, "release_eligible", context);
        await PrepareHandoffAsync(run, context, cancellationToken);
        await FlushAsync(context, cancellationToken);
    }

    private async Task PrepareHandoffAsync(AutopilotRun run, AutopilotCycleContext context, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var handoff = await db.AutopilotReleaseHandoffs.FirstOrDefaultAsync(item => item.RunId == run.Id, cancellationToken);
        if (handoff is null)
        {
            handoff = new AutopilotReleaseHandoff
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                WaveKey = run.WaveKey,
                CreatedAt = now,
            };
            db.AutopilotReleaseHandoffs.Add(handoff);
        }

        handoff.CandidateSha = run.CandidateSha;
        handoff.BaseSha = run.BaseSha;
        handoff.Branch = run.Branch;
        handoff.DryRun = settings.DryRun;
        handoff.UpdatedAt = now;

        if (settings.DryRun)
        {
            handoff.Status = AutopilotHandoffStatuses.Completed;
            handoff.SmokeStatus = AutopilotSmokeStatuses.Passed;
            handoff.SmokeReason = "simulated_production_smoke";
            handoff.LastStatusDetail = "simulation_only_no_external_action";
            handoff.CompletedAt = now;
            context.PlannedActions.Add($"release_handoff:{run.WaveKey}:{run.CandidateSha}");
            TransitionRun(run, AutopilotRunStates.HandoffReady, "handoff_simulated", context);
            TransitionRun(run, AutopilotRunStates.SmokeRunning, "smoke_simulated_start", context);
            TransitionRun(run, AutopilotRunStates.SmokePassed, "smoke_simulated_pass", context);
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.ReleaseHandoffPrepared,
                AutopilotAuditOutcomes.Simulated,
                reason: "dry_run_simulation",
                statusDetail: handoff.LastStatusDetail,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: true));
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.ProductionSmoke,
                AutopilotAuditOutcomes.Simulated,
                reason: handoff.SmokeReason,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: true));
            await EvaluateNextWaveEligibilityAsync(run, context, cancellationToken);
            await FlushAsync(context, cancellationToken);
            MergeNextWave(context, await nextWaves.TryLaunchNextWavesAsync(cancellationToken));
            return;
        }

        if (settings.AllowAutomaticRelease)
        {
            handoff.Status = AutopilotHandoffStatuses.Authorized;
            handoff.LastStatusDetail = "handoff_authorized_awaiting_external_executor";
            TransitionRun(run, AutopilotRunStates.HandoffReady, "handoff_authorized", context);
            context.PlannedActions.Add($"release_handoff:{run.WaveKey}:{run.CandidateSha}");
        }
        else
        {
            handoff.Status = AutopilotHandoffStatuses.AwaitingHuman;
            handoff.LastStatusDetail = "production_release_requires_human_authorization";
            run.HumanDecisionRequired = AutopilotHumanDecisions.ProductionRelease;
            TransitionRun(run, AutopilotRunStates.HandoffPendingHuman, "handoff_pending_human", context);
        }

        Record(context, AutopilotAuditFactory.Create(
            AutopilotAuditActions.ReleaseHandoffPrepared,
            handoff.Status == AutopilotHandoffStatuses.AwaitingHuman ? AutopilotAuditOutcomes.Required : AutopilotAuditOutcomes.Recorded,
            reason: handoff.Status,
            statusDetail: handoff.LastStatusDetail,
            waveKey: run.WaveKey,
            runState: run.State,
            branch: run.Branch,
            baseSha: run.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: run.Id,
            dryRun: settings.DryRun));

        if (handoff.Status == AutopilotHandoffStatuses.AwaitingHuman)
        {
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.HumanDecisionRequired,
                AutopilotAuditOutcomes.Required,
                reason: AutopilotHumanDecisions.ProductionRelease,
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: false));
        }

        // The production release stays a human decision; the next *development*
        // batch may still start when the operator has explicitly allowed it.
        await FlushAsync(context, cancellationToken);
        MergeNextWave(context, await nextWaves.TryLaunchNextWavesAsync(cancellationToken));
    }

    private async Task EvaluateNextWaveEligibilityAsync(AutopilotRun run, AutopilotCycleContext context, CancellationToken cancellationToken)
    {
        if (!run.ReleaseGatePassed)
        {
            TransitionRun(run, AutopilotRunStates.SmokeFailed, "next_wave_blocked_by_release_gate", context);
            return;
        }

        if (!string.IsNullOrWhiteSpace(run.HumanDecisionRequired))
        {
            TransitionRun(run, AutopilotRunStates.Completed, "next_wave_blocked_pending_human", context);
            return;
        }

        TransitionRun(run, AutopilotRunStates.NextWaveEligible, "next_wave_eligible", context);
        context.PlannedActions.Add($"plan_next_wave_after:{run.WaveKey}");
        Record(context, AutopilotAuditFactory.Create(
            AutopilotAuditActions.NextWaveEligibility,
            AutopilotAuditOutcomes.Recorded,
            reason: "release_gate_passed_smoke_passed",
            statusDetail: "launch_deferred_to_operator",
            waveKey: run.WaveKey,
            runState: run.State,
            branch: run.Branch,
            baseSha: run.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: run.Id,
            dryRun: settings.DryRun));
        await Task.CompletedTask;
    }

    private AutopilotGateInput BuildGateInput(AutopilotRun run, IReadOnlyList<AutopilotWaveTask> tasks)
    {
        var merged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var perTask = tasks
            .Select(task => ParseChecks(task.ChecksJson))
            .ToList();

        foreach (var check in AutopilotChecks.IntegrationRequired.Concat(AutopilotChecks.ReleaseRequired)
                     .Concat([AutopilotChecks.Lint, AutopilotChecks.BrowserE2E, AutopilotChecks.Migrations]))
        {
            var values = perTask.Where(map => map.ContainsKey(check)).Select(map => map[check]).ToList();
            merged[check] = values.Count > 0 && values.All(value => value);
        }

        var candidateMatches = run.CandidateSha.Length > 0
            && tasks.All(task => string.IsNullOrWhiteSpace(task.CandidateSha) || string.Equals(task.CandidateSha, run.CandidateSha, StringComparison.Ordinal));

        return new AutopilotGateInput(
            AllTasksSucceeded: tasks.Count > 0 && tasks.All(task => task.State == AutopilotTaskStates.Succeeded),
            HasBlockingFailure: tasks.Any(task => task.State == AutopilotTaskStates.TerminalFailed || task.State == AutopilotTaskStates.BlockedHuman),
            CandidateShaMatches: candidateMatches,
            Checks: merged);
    }

    private async Task<GateEvaluation> EvaluateGateAsync(
        AutopilotRun run,
        string gateKind,
        AutopilotGateInput input,
        bool integrationGatePassed,
        AutopilotCycleContext context,
        CancellationToken cancellationToken)
    {
        var existing = await db.AutopilotGateEvaluations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.RunId == run.Id
                && item.GateKind == gateKind
                && item.CandidateSha == run.CandidateSha, cancellationToken);

        if (existing is not null)
        {
            return new GateEvaluation(
                string.Equals(existing.Outcome, AutopilotGateOutcomes.Passed, StringComparison.OrdinalIgnoreCase),
                existing.ReasonCode ?? "already_evaluated",
                existing.Reason ?? "Gate already evaluated for this candidate.",
                ParseFailedChecks(existing.FailedChecksJson));
        }

        var evaluation = string.Equals(gateKind, AutopilotGateKinds.Integration, StringComparison.OrdinalIgnoreCase)
            ? AutopilotSafetyPolicy.EvaluateIntegrationGate(input, settings)
            : AutopilotSafetyPolicy.EvaluateReleaseGate(input, integrationGatePassed);

        var record = new AutopilotGateEvaluation
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            WaveKey = run.WaveKey,
            GateKind = gateKind,
            Outcome = evaluation.Passed ? AutopilotGateOutcomes.Passed : AutopilotGateOutcomes.Failed,
            CandidateSha = run.CandidateSha,
            BaseSha = run.BaseSha,
            ReasonCode = evaluation.ReasonCode,
            Reason = evaluation.Reason,
            FailedChecksJson = evaluation.FailedChecks.Count == 0 ? null : JsonSerializer.Serialize(evaluation.FailedChecks),
            CreatedAt = UtcNow(),
        };
        db.AutopilotGateEvaluations.Add(record);
        try
        {
            await FlushAsync(context, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent duplicate evaluation: reuse the winner so no second
            // integration/release can be launched from the same candidate.
            db.Entry(record).State = EntityState.Detached;
            var winner = await db.AutopilotGateEvaluations.AsNoTracking()
                .FirstOrDefaultAsync(item => item.RunId == run.Id && item.GateKind == gateKind && item.CandidateSha == run.CandidateSha, cancellationToken);
            if (winner is null) throw;
            return new GateEvaluation(
                string.Equals(winner.Outcome, AutopilotGateOutcomes.Passed, StringComparison.OrdinalIgnoreCase),
                winner.ReasonCode ?? "already_evaluated",
                winner.Reason ?? "Gate already evaluated for this candidate.",
                ParseFailedChecks(winner.FailedChecksJson));
        }

        Record(context, AutopilotAuditFactory.Create(
            AutopilotAuditActions.GateEvaluated,
            evaluation.Passed ? AutopilotAuditOutcomes.Succeeded : AutopilotAuditOutcomes.Failed,
            reason: evaluation.ReasonCode,
            statusDetail: evaluation.FailedChecks.Count == 0 ? gateKind : $"failed_checks:{string.Join(',', evaluation.FailedChecks)}",
            waveKey: run.WaveKey,
            runState: run.State,
            branch: run.Branch,
            baseSha: run.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: run.Id,
            dryRun: settings.DryRun));

        return evaluation;
    }

    private void TransitionRun(AutopilotRun run, string next, string reason, AutopilotCycleContext context)
    {
        if (string.Equals(run.State, next, StringComparison.OrdinalIgnoreCase)) return;
        if (!AutopilotStateMachine.CanTransitionRun(run.State, next))
        {
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.RunStateChanged,
                AutopilotAuditOutcomes.Blocked,
                reason: "invalid_transition",
                statusDetail: $"{run.State}->{next}",
                waveKey: run.WaveKey,
                runState: run.State,
                branch: run.Branch,
                baseSha: run.BaseSha,
                candidateSha: run.CandidateSha,
                targetId: run.Id,
                dryRun: settings.DryRun));
            return;
        }

        var previous = run.State;
        run.State = next;
        run.UpdatedAt = UtcNow();
        Record(context, AutopilotAuditFactory.Create(
            AutopilotAuditActions.RunStateChanged,
            AutopilotAuditOutcomes.Recorded,
            reason: reason,
            statusDetail: $"{previous}->{next}",
            waveKey: run.WaveKey,
            runState: next,
            branch: run.Branch,
            baseSha: run.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: run.Id,
            dryRun: settings.DryRun));
    }

    private async Task<bool> CanRunAsync(AutopilotControlState control, AutopilotCycleContext context, CancellationToken cancellationToken)
    {
        if (control.KillSwitchEngaged)
        {
            context.Outcome = "kill_switch";
            context.Reason = "kill_switch_engaged";
            await Task.CompletedTask;
            return false;
        }

        if (!settings.Enabled)
        {
            context.Outcome = "disabled";
            context.Reason = "feature_flag_disabled";
            Record(context, AutopilotAuditFactory.Create(
                AutopilotAuditActions.ControllerDisabled,
                AutopilotAuditOutcomes.Skipped,
                reason: "feature_flag_disabled",
                statusDetail: "Autopilot.Enabled=false",
                dryRun: settings.DryRun));
            await FlushAsync(context, cancellationToken);
            return false;
        }

        if (control.Paused)
        {
            context.Outcome = "paused";
            context.Reason = "controller_paused";
            return false;
        }

        return true;
    }

    private async Task<AutopilotControlState> EnsureControlAsync(CancellationToken cancellationToken)
    {
        var control = await db.AutopilotControlStates
            .FirstOrDefaultAsync(item => item.ControlKey == AutopilotControlState.SingletonKey, cancellationToken);
        if (control is not null) return control;

        control = new AutopilotControlState
        {
            Id = Guid.NewGuid(),
            ControlKey = AutopilotControlState.SingletonKey,
            Paused = false,
            KillSwitchEngaged = false,
            UpdatedAt = UtcNow(),
        };
        db.AutopilotControlStates.Add(control);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(control).State = EntityState.Detached;
            control = await db.AutopilotControlStates
                .FirstAsync(item => item.ControlKey == AutopilotControlState.SingletonKey, cancellationToken);
        }
        return control;
    }

    private AutopilotControlSnapshot Snapshot(AutopilotControlState control) =>
        new(settings.Enabled, settings.DryRun, control.Paused, control.KillSwitchEngaged,
            settings.ChargingEnabled, settings.PaidProvidersEnabled, settings.MaxConcurrency,
            settings.WatchdogIntervalMinutes, control.LastReason, control.UpdatedAt);

    private static void Record(AutopilotCycleContext context, AutopilotAuditEvent entry)
    {
        context.Audit.Add(entry);
    }

    private async Task FlushAsync(AutopilotCycleContext context, CancellationToken cancellationToken)
    {
        if (context.Audit.Count > 0)
        {
            db.AutopilotAuditEvents.AddRange(context.Audit);
            context.Audit.Clear();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, bool> ParseChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            return parsed is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<string> ParseFailedChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string DecisionKindForFailure(string failureClass, string? explicitKind = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitKind) && AutopilotHumanDecisionPolicy.RequiresHumanDecision(explicitKind))
            return explicitKind;
        return failureClass switch
        {
            AutopilotFailureClasses.Pricing => AutopilotHumanDecisions.Pricing,
            AutopilotFailureClasses.SchemaIrreversible => AutopilotHumanDecisions.DestructiveSchema,
            AutopilotFailureClasses.ProductDirection => AutopilotHumanDecisions.ProductDirection,
            AutopilotFailureClasses.Security => AutopilotHumanDecisions.SecurityAmbiguity,
            _ => AutopilotHumanDecisions.RepairEscalation,
        };
    }

    private static string RationaleForTerminalFailure(string failureClass, int attempt)
    {
        if (AutopilotFailureClasses.Blocking.Contains(failureClass)) return $"blocking_failure:{failureClass}";
        return $"retry_exhausted:attempt_{attempt}";
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}

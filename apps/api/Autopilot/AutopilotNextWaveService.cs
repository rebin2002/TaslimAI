using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>Bounded, provider-neutral summary of one next-wave launch or reconciliation pass.</summary>
public sealed record AutopilotNextWaveCycleResult(
    string Outcome,
    string? Reason,
    int BatchesExamined,
    int TasksPlanned,
    int TasksLaunched,
    int TasksSimulated,
    int BatchesBlocked,
    int TasksFailed,
    int TasksReconciled,
    IReadOnlyList<string> PlannedActions)
{
    public static AutopilotNextWaveCycleResult Idle(string outcome, string? reason) =>
        new(outcome, reason, 0, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// The Autopilot execution loop that the foundation deliberately left out: after
/// a wave is gate-green the controller plans the smallest safe follow-on
/// development wave from the pre-approved backlog and launches it through a
/// provider-neutral interface, then reconciles the launched work in bounded
/// steps for missed webhooks and restarts.
///
/// Hard boundaries (never bypassed at any setting):
/// <list type="bullet">
/// <item>only pre-approved ordinary development backlog items are ever selected;</item>
/// <item>no backlog means a human decision, never invented work;</item>
/// <item>at most <c>Autopilot:MaxTasksPerWave</c> (hard cap 20) tasks per wave and
/// at most <c>Autopilot:MaxLaunchAttempts</c> (hard cap 3) launch attempts per task;</item>
/// <item>the kill switch stops all future launches immediately;</item>
/// <item>dry-run never performs an external call;</item>
/// <item>a poll-derived completion never carries gate evidence, so polling can
/// only ever escalate to a human — it can never fabricate a green gate.</item>
/// </list>
/// </summary>
public interface IAutopilotNextWaveService
{
    Task<AutopilotNextWaveCycleResult> TryLaunchNextWavesAsync(CancellationToken cancellationToken = default);

    Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync(CancellationToken cancellationToken = default);
}

public sealed class AutopilotNextWaveService(
    TaslimDbContext db,
    AutopilotOptions options,
    IWaveLaunchProvider provider,
    IAutopilotEventIntake intake,
    TimeProvider timeProvider,
    ILogger<AutopilotNextWaveService> logger) : IAutopilotNextWaveService
{
    private readonly AutopilotOptions settings = options;

    public async Task<AutopilotNextWaveCycleResult> TryLaunchNextWavesAsync(CancellationToken cancellationToken = default)
    {
        var audit = new List<AutopilotAuditEvent>();
        var actions = new List<string>();
        var now = UtcNow();

        var control = await ReadControlAsync(cancellationToken);
        if (!settings.Enabled)
            return AutopilotNextWaveCycleResult.Idle("disabled", "feature_flag_disabled");

        if (control.KillSwitchEngaged)
        {
            audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                reason: "kill_switch_engaged",
                statusDetail: "kill_switch_prevents_future_launches",
                dryRun: settings.DryRun));
            await FlushAsync(audit, cancellationToken);
            return AutopilotNextWaveCycleResult.Idle("kill_switch", "kill_switch_engaged");
        }

        if (control.Paused)
            return AutopilotNextWaveCycleResult.Idle("paused", "controller_paused");

        var candidates = await db.AutopilotRuns
            .Where(run => run.IntegrationGatePassed && run.ReleaseGatePassed)
            .Where(run => run.State == AutopilotRunStates.NextWaveEligible
                || run.State == AutopilotRunStates.SmokePassed
                || run.State == AutopilotRunStates.HandoffReady
                || run.State == AutopilotRunStates.HandoffPendingHuman)
            .OrderBy(run => run.UpdatedAt)
            .Take(Math.Max(1, settings.MaxLaunchBatchesPerCycle))
            .ToListAsync(cancellationToken);

        var examined = 0;
        var planned = 0;
        var launched = 0;
        var simulated = 0;
        var blocked = 0;
        var failed = 0;

        foreach (var run in candidates)
        {
            examined++;
            var waveKey = AutopilotNextWavePolicy.NextWaveKey(run.WaveKey);
            var batch = await db.AutopilotWaveLaunchBatches
                .FirstOrDefaultAsync(item => item.SourceRunId == run.Id && item.WaveKey == waveKey, cancellationToken);

            var eligibility = AutopilotNextWavePolicy.Evaluate(run, settings, control.KillSwitchEngaged, control.Paused, batch is not null);
            if (!eligibility.Eligible)
            {
                // While the feature is disabled the controller stays silent: it records
                // nothing and creates no launch row.
                if (batch is null && IsFeatureDisabledReason(eligibility.ReasonCode))
                {
                    examined--;
                    continue;
                }

                blocked++;
                actions.Add($"next_wave_blocked:{waveKey}:{eligibility.ReasonCode}");
                await RecordBlockAsync(run, batch, waveKey, eligibility, audit, now, cancellationToken);
                continue;
            }

            if (batch is null)
            {
                batch = NewBatch(run, waveKey, now);
                db.AutopilotWaveLaunchBatches.Add(batch);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // A concurrent worker already created the batch for this source wave.
                    db.Entry(batch).State = EntityState.Detached;
                    blocked++;
                    actions.Add($"next_wave_blocked:{waveKey}:batch_exists");
                    continue;
                }
            }
            else if (string.Equals(batch.Status, AutopilotWaveLaunchStatuses.Blocked, StringComparison.OrdinalIgnoreCase))
            {
                // The blocking condition has been resolved (for example the human supplied the
                // backlog, or the operator enabled next-wave launch): resume deliberately.
                batch.Status = AutopilotWaveLaunchStatuses.Planned;
                batch.ReasonCode = null;
                batch.HumanDecisionRequired = null;
                batch.LastStatusDetail = "resumed_after_unblock";
                batch.UpdatedAt = now;
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchAttempted, AutopilotAuditOutcomes.Recorded,
                    reason: "next_wave_launch_resumed",
                    statusDetail: batch.LastStatusDetail,
                    waveKey: waveKey,
                    runState: run.State,
                    branch: run.Branch,
                    baseSha: run.BaseSha,
                    candidateSha: run.CandidateSha,
                    targetId: batch.Id,
                    dryRun: settings.DryRun));
            }

            if (string.Equals(batch.Status, AutopilotWaveLaunchStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(batch.Status, AutopilotWaveLaunchStatuses.Failed, StringComparison.OrdinalIgnoreCase))
            {
                // Terminal batches are never relaunched; a human resolves them explicitly.
                continue;
            }

            if (batch.BaseSha.Length > 0
                && !string.Equals(batch.BaseSha, run.CandidateSha, StringComparison.Ordinal))
            {
                // The immutable base ref of a planned wave is never repointed.
                batch.Status = AutopilotWaveLaunchStatuses.Blocked;
                batch.ReasonCode = "immutable_base_sha_conflict";
                batch.HumanDecisionRequired = AutopilotHumanDecisions.RepairEscalation;
                batch.UpdatedAt = now;
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                    reason: "immutable_base_sha_conflict",
                    statusDetail: $"recorded={batch.BaseSha};observed={run.CandidateSha}",
                    waveKey: waveKey,
                    baseSha: batch.BaseSha,
                    candidateSha: run.CandidateSha,
                    targetId: batch.Id,
                    dryRun: settings.DryRun));
                blocked++;
                actions.Add($"next_wave_blocked:{waveKey}:immutable_base_sha_conflict");
                continue;
            }

            var tasks = await db.AutopilotWaveLaunchTasks
                .Where(item => item.BatchId == batch.Id)
                .OrderBy(item => item.TaskKey)
                .ToListAsync(cancellationToken);

            if (tasks.Count == 0)
            {
                var plannedTasks = await BuildPlanAsync(run, batch, waveKey, now, audit, cancellationToken);
                if (plannedTasks is null)
                {
                    blocked++;
                    actions.Add($"next_wave_blocked:{waveKey}:backlog_required");
                    continue;
                }

                tasks = plannedTasks;
            }

            if (!settings.DryRun && !provider.IsConfigured)
            {
                // Never enable a live capability while its configuration or credential is missing.
                batch.Status = AutopilotWaveLaunchStatuses.Blocked;
                batch.ReasonCode = "provider_not_configured";
                batch.HumanDecisionRequired = AutopilotHumanDecisions.RepairEscalation;
                batch.LastStatusDetail = "wave_launch_provider_is_not_configured";
                batch.UpdatedAt = now;
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                    reason: "provider_not_configured",
                    statusDetail: batch.LastStatusDetail,
                    waveKey: waveKey,
                    targetId: batch.Id,
                    dryRun: false));
                blocked++;
                actions.Add($"next_wave_blocked:{waveKey}:provider_not_configured");
                continue;
            }

            batch.TaskCount = tasks.Count;
            batch.DryRun = settings.DryRun;
            batch.MaxLaunchAttempts = Math.Max(0, settings.MaxLaunchAttempts);
            batch.MaxReconciles = Math.Max(1, settings.MaxLaunchReconciles);

            if (!settings.DryRun)
            {
                // A previously simulated plan is upgraded in place when the operator turns dry-run off.
                await db.AutopilotWaveLaunchTasks
                    .Where(item => item.BatchId == batch.Id && item.Status == AutopilotWaveLaunchStatuses.Simulated)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Status, AutopilotWaveLaunchStatuses.Planned)
                        .SetProperty(item => item.ReasonCode, "upgraded_from_simulation")
                        .SetProperty(item => item.UpdatedAt, now), cancellationToken);

                foreach (var task in tasks.Where(item => item.Status == AutopilotWaveLaunchStatuses.Simulated))
                {
                    task.Status = AutopilotWaveLaunchStatuses.Planned;
                    task.ReasonCode = "upgraded_from_simulation";
                    task.UpdatedAt = now;
                }
            }

            foreach (var candidate in tasks)
            {
                var task = candidate;
                var retryable = task.Status == AutopilotWaveLaunchStatuses.Failed
                    && task.LaunchAttempt < Math.Max(0, settings.MaxLaunchAttempts);
                var launchable = task.Status == AutopilotWaveLaunchStatuses.Planned || retryable;
                if (!launchable) continue;

                if (settings.DryRun)
                {
                    task.Status = AutopilotWaveLaunchStatuses.Simulated;
                    task.DryRun = true;
                    task.LaunchedAt ??= now;
                    task.ReasonCode = "dry_run_simulation";
                    task.LastStatusDetail = "no_external_call_performed";
                    task.UpdatedAt = now;
                    simulated++;
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchSimulated, AutopilotAuditOutcomes.Simulated,
                        reason: "dry_run_simulation",
                        statusDetail: "no_external_call_performed",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        branch: task.Branch,
                        baseSha: task.BaseSha,
                        targetId: task.Id,
                        dryRun: true));
                    continue;
                }

                var claimed = await ClaimLaunchAttemptAsync(task, cancellationToken);
                if (claimed is null)
                {
                    // Another worker holds the attempt, or the bound is exhausted.
                    if (task.Status == AutopilotWaveLaunchStatuses.Failed) failed++;
                    continue;
                }

                task = claimed;
                batch.LaunchAttempt = Math.Max(batch.LaunchAttempt, task.LaunchAttempt);
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchAttempted, AutopilotAuditOutcomes.Recorded,
                    reason: provider.ProviderKey,
                    statusDetail: "launch_attempt_started",
                    waveKey: waveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    branch: task.Branch,
                    baseSha: task.BaseSha,
                    targetId: task.Id,
                    dryRun: false));

                var request = new WaveLaunchProviderRequest(
                    waveKey,
                    task.TaskKey,
                    task.Title,
                    BuildInstructions(task),
                    batch.BaseSha,
                    batch.Branch,
                    AutopilotNextWavePolicy.LaunchIdempotencyKey(waveKey, task.TaskKey));

                var result = await provider.LaunchTaskAsync(request, cancellationToken);
                if (result.IsLaunched)
                {
                    task.Status = AutopilotWaveLaunchStatuses.Launched;
                    task.ExternalTaskId = result.ExternalTaskId;
                    task.ExternalRef = null;
                    task.LaunchedAt = now;
                    task.ReasonCode = "launched";
                    task.LastStatusDetail = "awaiting_signed_completion_event";
                    task.NextReconcileAt = now.AddMinutes(Math.Max(5, settings.PollingFallbackAfterMinutes));
                    task.UpdatedAt = now;
                    launched++;
                    actions.Add($"next_wave_launched:{waveKey}:{task.TaskKey}");
                    await MarkBacklogConsumedAsync(task.BacklogItemKey, waveKey, now, cancellationToken);
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunched, AutopilotAuditOutcomes.Succeeded,
                        reason: provider.ProviderKey,
                        statusDetail: "launched",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        branch: task.Branch,
                        baseSha: task.BaseSha,
                        targetId: task.Id,
                        dryRun: false));
                    continue;
                }

                if (string.Equals(result.Outcome, WaveLaunchProviderOutcomes.Rejected, StringComparison.OrdinalIgnoreCase))
                {
                    task.Status = AutopilotWaveLaunchStatuses.Failed;
                    task.ReasonCode = result.ReasonCode ?? "launch_rejected";
                    task.LastStatusDetail = result.StatusDetail ?? "launch_rejected";
                    task.CompletedAt = now;
                    task.UpdatedAt = now;
                    failed++;
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchFailed, AutopilotAuditOutcomes.Blocked,
                        reason: task.ReasonCode,
                        statusDetail: task.LastStatusDetail,
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                        reason: AutopilotHumanDecisions.RepairEscalation,
                        statusDetail: task.ReasonCode,
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    continue;
                }

                // Transient provider failure: bounded retry, then escalate to a human.
                task.ReasonCode = result.ReasonCode ?? "launch_unavailable";
                task.LastStatusDetail = result.StatusDetail ?? "launch_unavailable";
                task.UpdatedAt = now;
                if (task.LaunchAttempt >= Math.Max(0, settings.MaxLaunchAttempts))
                {
                    task.Status = AutopilotWaveLaunchStatuses.Failed;
                    task.CompletedAt = now;
                    failed++;
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchFailed, AutopilotAuditOutcomes.Blocked,
                        reason: "launch_attempts_exhausted",
                        statusDetail: task.ReasonCode,
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                        reason: AutopilotHumanDecisions.RepairEscalation,
                        statusDetail: "launch_attempts_exhausted",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                }
                else
                {
                    task.Status = AutopilotWaveLaunchStatuses.Planned;
                    task.NextReconcileAt = now.Add(AutopilotNextWavePolicy.ComputeBackoff(task.LaunchAttempt, settings));
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchAttempted, AutopilotAuditOutcomes.Recorded,
                        reason: task.ReasonCode,
                        statusDetail: "launch_retry_scheduled",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                }
            }

            RefreshBatchFromTasks(batch, tasks, now);
            planned += tasks.Count;
            await FlushAsync(audit, cancellationToken);
        }

        await FlushAsync(audit, cancellationToken);

        var outcome = launched > 0 || simulated > 0 ? "next_wave_processed" : blocked > 0 ? "next_wave_blocked" : "next_wave_idle";
        logger.LogInformation(
            "Autopilot next-wave pass completed. Outcome={Outcome}; Examined={Examined}; Launched={Launched}; Simulated={Simulated}; Blocked={Blocked}; Failed={Failed}; DryRun={DryRun}",
            outcome, examined, launched, simulated, blocked, failed, settings.DryRun);

        return new AutopilotNextWaveCycleResult(outcome, null, examined, planned, launched, simulated, blocked, failed, 0, actions);
    }

    public async Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync(CancellationToken cancellationToken = default)
    {
        var audit = new List<AutopilotAuditEvent>();
        var actions = new List<string>();
        var now = UtcNow();

        var control = await ReadControlAsync(cancellationToken);
        if (!settings.Enabled)
            return AutopilotNextWaveCycleResult.Idle("disabled", "feature_flag_disabled");
        if (control.KillSwitchEngaged)
            return AutopilotNextWaveCycleResult.Idle("kill_switch", "kill_switch_engaged");
        if (control.Paused)
            return AutopilotNextWaveCycleResult.Idle("paused", "controller_paused");

        // Recover launches whose claim outlived its lease (a crashed worker), within bounds.
        var staleBefore = now.AddMinutes(-Math.Max(1, settings.LockLeaseMinutes));
        var staleClaims = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launching && item.UpdatedAt < staleBefore)
            .OrderBy(item => item.UpdatedAt)
            .Take(Math.Max(1, settings.MaxLaunchReconciliations))
            .ToListAsync(cancellationToken);

        foreach (var stale in staleClaims)
        {
            var retryable = stale.LaunchAttempt < Math.Max(0, settings.MaxLaunchAttempts);
            stale.Status = retryable ? AutopilotWaveLaunchStatuses.Planned : AutopilotWaveLaunchStatuses.Failed;
            stale.ReasonCode = retryable ? "stale_launch_claim" : "launch_attempts_exhausted";
            stale.LastStatusDetail = "stale_launch_claim_recovered";
            stale.UpdatedAt = now;
            actions.Add($"next_wave_recovered:{stale.WaveKey}:{stale.TaskKey}");
            audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                reason: "stale_launch_claim",
                statusDetail: stale.Status,
                waveKey: stale.WaveKey,
                taskId: stale.TaskKey,
                attempt: stale.LaunchAttempt,
                targetId: stale.Id,
                dryRun: settings.DryRun));
        }

        var due = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
            .Where(item => item.NextReconcileAt != null && item.NextReconcileAt <= now)
            .Where(item => item.ReconcileCount < item.MaxReconciles)
            .OrderBy(item => item.NextReconcileAt)
            .Take(Math.Max(1, settings.MaxLaunchReconciliations))
            .ToListAsync(cancellationToken);

        var reconciled = 0;
        foreach (var task in due)
        {
            reconciled++;
            var batch = await db.AutopilotWaveLaunchBatches.FirstOrDefaultAsync(item => item.Id == task.BatchId, cancellationToken);

            // Signed event completion remains the preferred path: if the wave task
            // is already terminal, the launch is simply reconciled.
            if (await HasTerminalSignedCompletionAsync(task, cancellationToken))
            {
                task.Status = AutopilotWaveLaunchStatuses.Completed;
                task.ReasonCode = "signed_event_completed";
                task.LastStatusDetail = "signed_completion_event_received";
                task.CompletedAt = now;
                task.UpdatedAt = now;
                actions.Add($"next_wave_reconciled:{task.WaveKey}:{task.TaskKey}:signed_event");
                audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Succeeded,
                    reason: "signed_event_completed",
                    statusDetail: "signed_completion_event_received",
                    waveKey: task.WaveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    targetId: task.Id,
                    dryRun: settings.DryRun));
                if (batch is not null) await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
                continue;
            }

            var state = string.IsNullOrWhiteSpace(task.ExternalTaskId)
                ? null
                : await provider.GetTaskStateAsync(task.ExternalTaskId!, cancellationToken);

            if (state is null || !state.IsTerminal)
            {
                task.ReconcileCount++;
                task.UpdatedAt = now;
                task.ReasonCode = state is null ? "reconcile_state_unknown" : $"polled_{state.Status}";
                if (task.ReconcileCount >= task.MaxReconciles)
                {
                    task.Status = AutopilotWaveLaunchStatuses.Blocked;
                    task.LastStatusDetail = "reconcile_exhausted";
                    task.CompletedAt = now;
                    actions.Add($"next_wave_reconcile_exhausted:{task.WaveKey}:{task.TaskKey}");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Blocked,
                        reason: "reconcile_exhausted",
                        statusDetail: task.ReasonCode,
                        waveKey: task.WaveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                    audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                        reason: AutopilotHumanDecisions.RepairEscalation,
                        statusDetail: "reconcile_exhausted",
                        waveKey: task.WaveKey,
                        taskId: task.TaskKey,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                }
                else
                {
                    task.NextReconcileAt = now.Add(AutopilotNextWavePolicy.ComputeBackoff(task.ReconcileCount, settings));
                    if (task.ReconcileCount == 1)
                    {
                        audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                            reason: task.ReasonCode,
                            statusDetail: "polling_fallback_in_progress",
                            waveKey: task.WaveKey,
                            taskId: task.TaskKey,
                            attempt: task.LaunchAttempt,
                            targetId: task.Id,
                            dryRun: settings.DryRun));
                    }
                }

                if (batch is not null) await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
                continue;
            }

            // Terminal state observed by polling. The derived completion event is
            // deliberately evidence-free and unsigned: it can only ever escalate to
            // a human, never satisfy a gate.
            var signal = BuildPollDerivedSignal(task, state, batch);
            var submission = await intake.SubmitInternalAsync(
                new AutopilotInternalSignalRequest(
                    AutopilotInternalSignalSources.Polling,
                    PollEventId(task.ExternalTaskId, state.Status),
                    AutopilotEventTypes.WaveTaskCompleted,
                    signal,
                    "polling_fallback"),
                cancellationToken);

            task.ReconcileCount++;
            task.Status = AutopilotWaveLaunchStatuses.Completed;
            task.ExternalRef = state.ExternalRef;
            task.ReasonCode = $"polled_{state.Status}";
            task.LastStatusDetail = $"polling_fallback:{submission.Outcome}";
            task.CompletedAt = now;
            task.UpdatedAt = now;
            actions.Add($"next_wave_reconciled:{task.WaveKey}:{task.TaskKey}:{state.Status}");
            audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                reason: task.ReasonCode,
                statusDetail: task.LastStatusDetail,
                waveKey: task.WaveKey,
                taskId: task.TaskKey,
                attempt: task.LaunchAttempt,
                targetId: task.Id,
                dryRun: settings.DryRun));
            if (batch is not null) await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
        }

        await FlushAsync(audit, cancellationToken);

        var outcome = reconciled > 0 ? "next_wave_reconciled" : "next_wave_reconcile_idle";
        return new AutopilotNextWaveCycleResult(outcome, null, staleClaims.Count, 0, 0, 0, 0, 0, reconciled, actions);
    }

    // -----------------------------------------------------------------------
    // Planning
    // -----------------------------------------------------------------------

    private async Task<List<AutopilotWaveLaunchTask>?> BuildPlanAsync(
        AutopilotRun run,
        AutopilotWaveLaunchBatch batch,
        string waveKey,
        DateTime now,
        List<AutopilotAuditEvent> audit,
        CancellationToken cancellationToken)
    {
        var items = await db.AutopilotBacklogItems
            .Where(item => item.Approved
                && item.ConsumedByWaveKey == null
                && item.Kind == AutopilotBacklogItemKinds.Development)
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Take(Math.Max(1, settings.MaxTasksPerWave))
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            // No approved work exists: the controller stops and asks for a human decision.
            batch.Status = AutopilotWaveLaunchStatuses.Blocked;
            batch.ReasonCode = "backlog_required";
            batch.HumanDecisionRequired = AutopilotHumanDecisions.BacklogRequired;
            batch.LastStatusDetail = "no_approved_development_backlog_items";
            batch.TaskCount = 0;
            batch.UpdatedAt = now;
            audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                reason: "backlog_required",
                statusDetail: batch.LastStatusDetail,
                waveKey: waveKey,
                runState: run.State,
                branch: run.Branch,
                candidateSha: run.CandidateSha,
                targetId: batch.Id,
                dryRun: settings.DryRun));
            audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                reason: AutopilotHumanDecisions.BacklogRequired,
                statusDetail: "approved_development_backlog_required",
                waveKey: waveKey,
                targetId: batch.Id,
                dryRun: settings.DryRun));
            await FlushAsync(audit, cancellationToken);
            return null;
        }

        var plans = items
            .Select(item => new WaveLaunchTaskPlan(
                Bounded(item.ItemKey, 200),
                Bounded(item.Title, 300),
                Bounded(item.ItemKey, 120),
                Bounded(run.Branch, 300),
                Bounded(run.CandidateSha, 64)))
            .ToList();

        batch.SourceRunId = run.Id;
        batch.SourceWaveKey = run.WaveKey;
        batch.WaveKey = waveKey;
        batch.BaseSha = Bounded(run.CandidateSha, 64);
        batch.Branch = Bounded(run.Branch, 300);
        batch.PlanFingerprint = AutopilotNextWavePolicy.PlanFingerprint(waveKey, plans);
        batch.TaskCount = plans.Count;
        batch.DryRun = settings.DryRun;
        batch.MaxLaunchAttempts = Math.Max(0, settings.MaxLaunchAttempts);
        batch.MaxReconciles = Math.Max(1, settings.MaxLaunchReconciles);
        batch.Status = AutopilotWaveLaunchStatuses.Planned;
        batch.ReasonCode = null;
        batch.HumanDecisionRequired = null;
        batch.LastStatusDetail = "planned_from_approved_backlog";
        batch.UpdatedAt = now;

        var created = new List<AutopilotWaveLaunchTask>();
        foreach (var plan in plans)
        {
            var task = new AutopilotWaveLaunchTask
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                SourceRunId = run.Id,
                SourceWaveKey = run.WaveKey,
                WaveKey = waveKey,
                TaskKey = plan.TaskKey,
                Title = plan.Title,
                BacklogItemKey = plan.BacklogItemKey,
                Status = AutopilotWaveLaunchStatuses.Planned,
                Branch = plan.Branch,
                BaseSha = plan.BaseSha,
                DryRun = settings.DryRun,
                LaunchAttempt = 0,
                MaxLaunchAttempts = Math.Max(0, settings.MaxLaunchAttempts),
                ReconcileCount = 0,
                MaxReconciles = Math.Max(1, settings.MaxLaunchReconciles),
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            };
            db.AutopilotWaveLaunchTasks.Add(task);
            created.Add(task);
        }

        audit.Add(Audit(AutopilotAuditActions.NextWavePlanSelected, AutopilotAuditOutcomes.Recorded,
            reason: "approved_backlog_selected",
            statusDetail: $"tasks={created.Count};fingerprint={batch.PlanFingerprint[..12]}",
            waveKey: waveKey,
            runState: run.State,
            branch: run.Branch,
            baseSha: batch.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: batch.Id,
            dryRun: settings.DryRun));

        try
        {
            await FlushAsync(audit, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent worker planned the same wave; reuse its rows.
            db.ChangeTracker.Clear();
            var existing = await db.AutopilotWaveLaunchTasks
                .Where(item => item.BatchId == batch.Id)
                .OrderBy(item => item.TaskKey)
                .ToListAsync(cancellationToken);
            return existing.Count > 0 ? existing : null;
        }

        return created;
    }

    private async Task MarkBacklogConsumedAsync(string backlogItemKey, string waveKey, DateTime now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backlogItemKey)) return;
        await db.AutopilotBacklogItems
            .Where(item => item.ItemKey == backlogItemKey && item.ConsumedByWaveKey == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConsumedByWaveKey, waveKey)
                .SetProperty(item => item.ConsumedAt, now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private async Task<AutopilotWaveLaunchTask?> ClaimLaunchAttemptAsync(AutopilotWaveLaunchTask task, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var maxAttempts = Math.Max(0, settings.MaxLaunchAttempts);
        var claimed = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Id == task.Id
                && item.ConcurrencyToken == task.ConcurrencyToken
                && (item.Status == AutopilotWaveLaunchStatuses.Planned || item.Status == AutopilotWaveLaunchStatuses.Failed)
                && item.LaunchAttempt < maxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AutopilotWaveLaunchStatuses.Launching)
                .SetProperty(item => item.LaunchAttempt, item => item.LaunchAttempt + 1)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);

        if (claimed == 0) return null;

        await db.Entry(task).ReloadAsync(cancellationToken);
        return task;
    }

    private async Task<bool> HasTerminalSignedCompletionAsync(AutopilotWaveLaunchTask task, CancellationToken cancellationToken)
    {
        var run = await db.AutopilotRuns.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WaveKey == task.WaveKey, cancellationToken);
        if (run is null) return false;

        var externalId = task.ExternalTaskId;
        var waveTask = await db.AutopilotWaveTasks.AsNoTracking()
            .Where(item => item.RunId == run.Id)
            .Where(item => item.TaskId == task.TaskKey || (externalId != null && item.TaskId == externalId))
            .FirstOrDefaultAsync(cancellationToken);

        return waveTask is not null && AutopilotTaskStates.Terminal.Contains(waveTask.State);
    }

    private static AutopilotCompletionSignal BuildPollDerivedSignal(
        AutopilotWaveLaunchTask task,
        WaveLaunchTaskState state,
        AutopilotWaveLaunchBatch? batch) =>
        new()
        {
            WaveKey = task.WaveKey,
            TaskId = string.IsNullOrWhiteSpace(task.ExternalTaskId) ? task.TaskKey : task.ExternalTaskId,
            Outcome = state.IsSuccess
                ? AutopilotTaskOutcomes.Succeeded
                : state.Status == WaveLaunchTaskStates.Blocked || state.Status == WaveLaunchTaskStates.Cancelled
                    ? AutopilotTaskOutcomes.Blocked
                    : AutopilotTaskOutcomes.Failed,
            FailureClass = state.IsSuccess ? AutopilotFailureClasses.None : AutopilotFailureClasses.Unknown,
            Attempt = task.LaunchAttempt,
            Evidence = $"polling_fallback:{Bounded(state.Status, 40)}",
            Checks = null,
            RequiresHumanDecision = !state.IsSuccess,
            HumanDecisionKind = state.IsSuccess ? null : AutopilotHumanDecisions.RepairEscalation,
            ExpectedTaskCount = batch is { TaskCount: > 0 } ? batch.TaskCount : null,
            WaveComplete = false,
        };

    private static string PollEventId(string? externalTaskId, string status)
    {
        var raw = (externalTaskId ?? string.Empty).Trim();
        var identity = raw.Length <= 120
            ? raw
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..32];
        return Bounded($"poll:{identity}:{status.ToLowerInvariant()}", 200);
    }

    private static string BuildInstructions(AutopilotWaveLaunchTask task) =>
        Bounded($"Deliver the approved backlog item '{task.Title}'. Wave: {task.WaveKey}. Task: {task.TaskKey}. " +
                "Open a pull request; do not merge, do not release, and do not enable charging or paid providers.", 4000);

    // -----------------------------------------------------------------------
    // Batch helpers
    // -----------------------------------------------------------------------

    private async Task RecordBlockAsync(
        AutopilotRun run,
        AutopilotWaveLaunchBatch? batch,
        string waveKey,
        NextWaveEligibility eligibility,
        List<AutopilotAuditEvent> audit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (batch is null)
        {
            batch = NewBatch(run, waveKey, now);
            batch.Status = AutopilotWaveLaunchStatuses.Blocked;
            batch.ReasonCode = eligibility.ReasonCode;
            batch.HumanDecisionRequired = eligibility.HumanDecisionRequired;
            batch.LastStatusDetail = "next_wave_launch_blocked";
            db.AutopilotWaveLaunchBatches.Add(batch);
        }
        else
        {
            if (string.Equals(batch.ReasonCode, eligibility.ReasonCode, StringComparison.Ordinal)
                && string.Equals(batch.Status, AutopilotWaveLaunchStatuses.Blocked, StringComparison.OrdinalIgnoreCase))
                return;

            batch.Status = AutopilotWaveLaunchStatuses.Blocked;
            batch.ReasonCode = eligibility.ReasonCode;
            batch.HumanDecisionRequired = eligibility.HumanDecisionRequired;
            batch.LastStatusDetail = "next_wave_launch_blocked";
            batch.UpdatedAt = now;
        }

        audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
            reason: eligibility.ReasonCode,
            statusDetail: "next_wave_launch_blocked",
            waveKey: waveKey,
            runState: run.State,
            branch: run.Branch,
            baseSha: run.BaseSha,
            candidateSha: run.CandidateSha,
            targetId: batch.Id,
            dryRun: settings.DryRun));

        if (!string.IsNullOrWhiteSpace(eligibility.HumanDecisionRequired))
        {
            audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                reason: eligibility.HumanDecisionRequired,
                statusDetail: eligibility.ReasonCode,
                waveKey: waveKey,
                targetId: batch.Id,
                dryRun: settings.DryRun));
        }

        await FlushAsync(audit, cancellationToken);
    }

    /// <summary>Reasons that mean "the feature is simply switched off" — recorded silently.</summary>
    private static bool IsFeatureDisabledReason(string? reasonCode) =>
        string.Equals(reasonCode, "next_wave_launch_disabled", StringComparison.Ordinal)
        || string.Equals(reasonCode, "feature_flag_disabled", StringComparison.Ordinal);

    private AutopilotWaveLaunchBatch NewBatch(AutopilotRun run, string waveKey, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        SourceRunId = run.Id,
        SourceWaveKey = run.WaveKey,
        WaveKey = waveKey,
        Status = AutopilotWaveLaunchStatuses.Planned,
        BaseSha = Bounded(run.CandidateSha, 64),
        Branch = Bounded(run.Branch, 300),
        DryRun = settings.DryRun,
        TaskCount = 0,
        LaunchedTaskCount = 0,
        MaxLaunchAttempts = Math.Max(0, settings.MaxLaunchAttempts),
        MaxReconciles = Math.Max(1, settings.MaxLaunchReconciles),
        CreatedAt = now,
        UpdatedAt = now,
        ConcurrencyToken = Guid.NewGuid(),
    };

    private void RefreshBatchFromTasks(AutopilotWaveLaunchBatch batch, IReadOnlyList<AutopilotWaveLaunchTask> tasks, DateTime now)
    {
        batch.TaskCount = tasks.Count;
        batch.LaunchedTaskCount = tasks.Count(item => item.Status == AutopilotWaveLaunchStatuses.Launched
            || item.Status == AutopilotWaveLaunchStatuses.Completed);
        batch.UpdatedAt = now;

        if (tasks.Count > 0 && tasks.All(item => item.Status == AutopilotWaveLaunchStatuses.Completed))
        {
            batch.Status = AutopilotWaveLaunchStatuses.Completed;
            batch.ReasonCode = "all_tasks_reconciled";
            batch.CompletedAt ??= now;
            return;
        }

        if (tasks.Count > 0 && tasks.All(item => item.Status == AutopilotWaveLaunchStatuses.Simulated))
        {
            batch.Status = AutopilotWaveLaunchStatuses.Simulated;
            batch.ReasonCode = "dry_run_simulation";
            batch.CompletedAt ??= now;
            return;
        }

        if (tasks.Any(item => item.Status == AutopilotWaveLaunchStatuses.Launched))
        {
            batch.Status = AutopilotWaveLaunchStatuses.Launched;
            batch.ReasonCode = "launched";
            batch.LaunchedAt ??= now;
            return;
        }

        if (tasks.Count > 0 && tasks.All(item => item.Status == AutopilotWaveLaunchStatuses.Failed
            || item.Status == AutopilotWaveLaunchStatuses.Blocked))
        {
            batch.Status = AutopilotWaveLaunchStatuses.Failed;
            batch.ReasonCode = "launch_failed";
            batch.HumanDecisionRequired ??= AutopilotHumanDecisions.RepairEscalation;
            batch.CompletedAt ??= now;
            return;
        }

        batch.Status = AutopilotWaveLaunchStatuses.Planned;
    }

    private async Task RefreshBatchFromDatabaseAsync(AutopilotWaveLaunchBatch batch, DateTime now, CancellationToken cancellationToken)
    {
        var tasks = await db.AutopilotWaveLaunchTasks
            .Where(item => item.BatchId == batch.Id)
            .ToListAsync(cancellationToken);
        RefreshBatchFromTasks(batch, tasks, now);
    }

    private async Task<AutopilotControlState> ReadControlAsync(CancellationToken cancellationToken) =>
        await db.AutopilotControlStates
            .FirstOrDefaultAsync(item => item.ControlKey == AutopilotControlState.SingletonKey, cancellationToken)
        ?? new AutopilotControlState { Id = Guid.NewGuid(), ControlKey = AutopilotControlState.SingletonKey, UpdatedAt = UtcNow() };

    private static AutopilotAuditEvent Audit(
        string action,
        string outcome,
        string? reason = null,
        string? statusDetail = null,
        string? waveKey = null,
        string? taskId = null,
        string? runState = null,
        string? taskState = null,
        int attempt = 0,
        string? branch = null,
        string? baseSha = null,
        string? candidateSha = null,
        Guid? targetId = null,
        bool dryRun = false) =>
        AutopilotAuditFactory.Create(action, outcome, reason, statusDetail, waveKey, taskId, runState, taskState,
            attempt, branch, baseSha, candidateSha, targetId, dryRun: dryRun);

    private async Task FlushAsync(List<AutopilotAuditEvent> audit, CancellationToken cancellationToken)
    {
        if (audit.Count > 0)
        {
            db.AutopilotAuditEvents.AddRange(audit);
            audit.Clear();
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static string Bounded(string? value, int max)
    {
        var candidate = value ?? string.Empty;
        return candidate.Length <= max ? candidate : candidate[..max];
    }
}

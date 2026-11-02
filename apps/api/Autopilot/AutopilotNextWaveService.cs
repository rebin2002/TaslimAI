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
    ILogger<AutopilotNextWaveService> logger,
    AutopilotGlobalTaskSlotAllocator? globalTaskSlots = null,
    AutopilotGlobalTaskInventoryReader? globalTaskInventory = null) : IAutopilotNextWaveService
{
    private readonly AutopilotOptions settings = options;
    private const string VerifiedCompletionRefillReason = "verified_completion_refill";
    private const int MaxCompletionEventsPerCycle = 1000;

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

        if (settings.AllowNextWaveLaunch
            && !settings.DryRun
            && (globalTaskSlots is null || globalTaskInventory is null))
            throw new InvalidOperationException(
                "Live Autopilot next-wave launch requires a durable global slot allocator and authoritative inventory source.");

        // Recovery is idempotent and runs before eligibility planning: only a
        // durable, processed event with the allocator's exact Task Detail receipt
        // can release a slot or create a one-for-one refill batch.
        await ProcessVerifiedCompletionsAsync(now, cancellationToken);

        // Capacity-denied tasks stay Planned. Select their source runs before
        // older runs whose launch batches have no runnable work, or a bounded
        // scan can repeatedly miss the rows that should consume a released slot.
        var runnableSourceRunIds = (
            from task in db.AutopilotWaveLaunchTasks.AsNoTracking()
            join batch in db.AutopilotWaveLaunchBatches.AsNoTracking() on task.BatchId equals batch.Id
            where task.SourceRunId == batch.SourceRunId
                && task.Status == AutopilotWaveLaunchStatuses.Planned
                && (batch.Status == AutopilotWaveLaunchStatuses.Planned
                    || batch.Status == AutopilotWaveLaunchStatuses.Launched)
            select batch.SourceRunId)
            .Distinct();

        var candidates = await db.AutopilotRuns
            .Where(run => run.IntegrationGatePassed && run.ReleaseGatePassed)
            .Where(run => run.State == AutopilotRunStates.NextWaveEligible
                || run.State == AutopilotRunStates.SmokePassed
                || run.State == AutopilotRunStates.HandoffReady
                || run.State == AutopilotRunStates.HandoffPendingHuman)
            .OrderByDescending(run => runnableSourceRunIds.Contains(run.Id))
            .ThenBy(run => run.UpdatedAt)
            .ThenBy(run => run.Id)
            .Take(Math.Max(1, settings.MaxLaunchBatchesPerCycle))
            .ToListAsync(cancellationToken);

        var workItems = new List<(AutopilotRun Run, string WaveKey, AutopilotWaveLaunchBatch? Batch)>();
        var refillBatches = await db.AutopilotWaveLaunchBatches
            .Where(item => item.ReasonCode == VerifiedCompletionRefillReason)
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Planned
                || item.Status == AutopilotWaveLaunchStatuses.Launched)
            .OrderBy(item => item.UpdatedAt)
            .Take(Math.Max(1, settings.MaxLaunchBatchesPerCycle))
            .ToListAsync(cancellationToken);
        foreach (var refillBatch in refillBatches)
        {
            var sourceRun = await db.AutopilotRuns.FirstOrDefaultAsync(item =>
                item.Id == refillBatch.SourceRunId
                && item.IntegrationGatePassed
                && item.ReleaseGatePassed,
                cancellationToken);
            if (sourceRun is not null)
                workItems.Add((sourceRun, refillBatch.WaveKey, refillBatch));
        }

        var maxWorkItems = Math.Max(1, settings.MaxLaunchBatchesPerCycle);
        foreach (var run in candidates)
        {
            if (workItems.Count >= maxWorkItems) break;
            workItems.Add((run, AutopilotNextWavePolicy.NextWaveKey(run.WaveKey), null));
        }

        var examined = 0;
        var planned = 0;
        var launched = 0;
        var simulated = 0;
        var blocked = 0;
        var failed = 0;

        foreach (var workItem in workItems)
        {
            var run = workItem.Run;
            examined++;
            var waveKey = workItem.WaveKey;
            var batch = workItem.Batch ?? await db.AutopilotWaveLaunchBatches
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
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var claimed = await ClaimLaunchAttemptAsync(task, cancellationToken);
                if (claimed is null)
                {
                    // Another worker holds the task, the attempt bound is exhausted,
                    // or another wave atomically claimed this backlog item first.
                    if (task.Status == AutopilotWaveLaunchStatuses.Failed) failed++;
                    if (task.Status == AutopilotWaveLaunchStatuses.Blocked
                        && task.ReasonCode == "backlog_item_claimed")
                    {
                        failed++;
                        audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                            reason: task.ReasonCode,
                            statusDetail: task.LastStatusDetail,
                            waveKey: waveKey,
                            taskId: task.TaskKey,
                            attempt: task.LaunchAttempt,
                            targetId: task.Id,
                            dryRun: false));
                    }
                    continue;
                }

                task = claimed;
                batch.LaunchAttempt = Math.Max(batch.LaunchAttempt, task.LaunchAttempt);

                var idempotencyKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(waveKey, task.TaskKey);
                var inventory = globalTaskInventory is null
                    ? AutopilotGlobalTaskInventorySnapshot.Unknown("", "inventory_source_not_configured")
                    : await globalTaskInventory.ReadAuthoritativeSnapshotAsync(cancellationToken);
                var reservation = globalTaskSlots is null
                    ? new AutopilotGlobalTaskSlotResult(
                        AutopilotGlobalTaskSlotOutcomes.InventoryUnknown,
                        false,
                        false,
                        0,
                        ReasonCode: "global_slot_allocator_not_configured")
                    : await globalTaskSlots.ReserveOrRecoverLaunchAsync(
                        task.Id,
                        idempotencyKey,
                        inventory,
                        TimeSpan.FromMinutes(Math.Max(1, settings.LockLeaseMinutes)),
                        providerGuaranteesIdempotency: false,
                        cancellationToken);
                if (!reservation.MayCallProvider)
                {
                    var capacityResetSucceeded = true;
                    var safeNoCallDenial = (reservation.Outcome is AutopilotGlobalTaskSlotOutcomes.CapacityReached
                        or AutopilotGlobalTaskSlotOutcomes.InventoryUnknown
                        or AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable)
                        || reservation.ReasonCode == "global_slot_allocator_not_configured";
                    if (!reservation.SlotHeld && safeNoCallDenial)
                    {
                        // The allocator may clear EF tracking after relational retry
                        // exhaustion. Reset the durable task and release its backlog
                        // claim in one transaction, and only if both writes succeed.
                        var resetToken = Guid.NewGuid();
                        var reset = await ResetDeniedReservationAsync(
                            task.Id,
                            task.BacklogItemKey,
                            waveKey,
                            reservation.ReasonCode ?? reservation.Outcome,
                            resetToken,
                            now,
                            cancellationToken);
                        capacityResetSucceeded = reset;
                        if (reset)
                        {
                            task.Status = AutopilotWaveLaunchStatuses.Planned;
                            task.LaunchAttempt = Math.Max(0, task.LaunchAttempt - 1);
                            task.ReasonCode = reservation.ReasonCode ?? reservation.Outcome;
                            task.LastStatusDetail = "global_slot_not_authorized; no_provider_call";
                            task.UpdatedAt = now;
                            task.ConcurrencyToken = resetToken;
                        }
                        else
                        {
                            task.Status = AutopilotWaveLaunchStatuses.Launching;
                            task.LastStatusDetail = "global_slot_reset_uncertain; backlog_claim_retained";
                        }
                    }
                    else if (!reservation.SlotHeld)
                    {
                        capacityResetSucceeded = false;
                    }
                    // A held-but-ambiguous reservation remains Launching and keeps
                    // its backlog claim until authoritative reconciliation resolves it.
                    blocked++;
                    actions.Add($"next_wave_blocked:{waveKey}:global_slot_{reservation.Outcome}");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: reservation.ReasonCode ?? reservation.Outcome,
                        statusDetail: reservation.SlotHeld
                            ? "global_slot_retained_no_provider_retry"
                            : capacityResetSucceeded ? "provider_call_denied" : "slot_reset_uncertain_backlog_retained",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    continue;
                }

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
                    idempotencyKey);

                var result = await provider.LaunchTaskAsync(request, cancellationToken);
                if (result.IsLaunched)
                {
                    if (string.IsNullOrWhiteSpace(result.ExternalTaskId))
                    {
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:accepted_identity_missing_slot_retained");
                        audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                            reason: "external_task_identity_missing",
                            statusDetail: "provider_claimed_success_without_identity; launch_reservation_retained",
                            waveKey: waveKey,
                            taskId: task.TaskKey,
                            attempt: task.LaunchAttempt,
                            targetId: task.Id,
                            dryRun: false));
                        continue;
                    }

                    task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared before acceptance persistence.");
                    var accepted = await globalTaskSlots!.RecordAcceptedAsync(
                        task.Id,
                        idempotencyKey,
                        result.ExternalTaskId,
                        cancellationToken);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider acceptance.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        // A valid receipt may race with this response handler. The
                        // terminal transition wins; never write Launched over it.
                        await EnsureCompletionRefillBatchAsync(task, now, cancellationToken);
                        launched++;
                        actions.Add($"next_wave_launched:{waveKey}:{task.TaskKey}:completed_before_ack");
                        continue;
                    }

                    var metadataToken = Guid.NewGuid();
                    var metadataUpdated = accepted.SlotHeld
                        && task.Status == AutopilotWaveLaunchStatuses.Launched
                        && string.Equals(task.ExternalTaskId, result.ExternalTaskId, StringComparison.Ordinal)
                        ? await db.AutopilotWaveLaunchTasks
                            .Where(item => item.Id == task.Id
                                && item.Status == AutopilotWaveLaunchStatuses.Launched
                                && item.ExternalTaskId == result.ExternalTaskId
                                && item.ConcurrencyToken == task.ConcurrencyToken)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(item => item.LastStatusDetail, "awaiting_signed_completion_event")
                                .SetProperty(item => item.NextReconcileAt, now.AddMinutes(Math.Max(5, settings.PollingFallbackAfterMinutes)))
                                .SetProperty(item => item.UpdatedAt, now)
                                .SetProperty(item => item.ConcurrencyToken, metadataToken), cancellationToken)
                        : 0;
                    task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider acceptance.");
                    if (metadataUpdated != 1
                        || task.Status != AutopilotWaveLaunchStatuses.Launched
                        || !string.Equals(task.ExternalTaskId, result.ExternalTaskId, StringComparison.Ordinal))
                    {
                        if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                            await EnsureCompletionRefillBatchAsync(task, now, cancellationToken);
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:accepted_identity_state_changed_slot_retained");
                        continue;
                    }

                    launched++;
                    actions.Add($"next_wave_launched:{waveKey}:{task.TaskKey}");
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
                    var rejected = await globalTaskSlots!.RecordRejectedAsync(
                        task.Id,
                        idempotencyKey,
                        result.ReasonCode ?? "provider_rejected",
                        cancellationToken);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider rejection.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        await EnsureCompletionRefillBatchAsync(task, now, cancellationToken);
                        blocked++;
                        actions.Add($"next_wave_rejected_after_verified_completion:{waveKey}:{task.TaskKey}");
                        continue;
                    }
                    if (rejected.SlotHeld)
                    {
                        // Persistence uncertainty must not free capacity or make the
                        // backlog item available to a second provider call.
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejection_persistence_ambiguous");
                        continue;
                    }
                    if (rejected.Outcome != AutopilotGlobalTaskSlotOutcomes.LaunchRejected
                        || task.Status != AutopilotWaveLaunchStatuses.Failed)
                    {
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejection_state_uncertain");
                        continue;
                    }

                    var rejectionToken = Guid.NewGuid();
                    var rejectionUpdated = await db.AutopilotWaveLaunchTasks
                        .Where(item => item.Id == task.Id
                            && item.Status == AutopilotWaveLaunchStatuses.Failed
                            && item.ConcurrencyToken == task.ConcurrencyToken)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.ReasonCode, result.ReasonCode ?? "launch_rejected")
                            .SetProperty(item => item.LastStatusDetail, result.StatusDetail ?? "launch_rejected")
                            .SetProperty(item => item.CompletedAt, now)
                            .SetProperty(item => item.UpdatedAt, now)
                            .SetProperty(item => item.ConcurrencyToken, rejectionToken), cancellationToken);
                    if (rejectionUpdated != 1)
                    {
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejection_raced_with_completion");
                        continue;
                    }
                    task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after rejection persistence.");
                    await ReleaseBacklogItemClaimAsync(task.BacklogItemKey, waveKey, cancellationToken);
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

                // A timeout or transport failure is ambiguous: the Bridge may have
                // created a task before the response was lost. Never reset to Planned
                // or retry until authoritative inventory proves the outcome.
                var ambiguous = await globalTaskSlots!.RecordAmbiguousAsync(
                    task.Id,
                    idempotencyKey,
                    result.ReasonCode ?? "launch_outcome_unknown",
                    cancellationToken);
                task = await ReloadLaunchTaskFreshAsync(task.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The durable launch row disappeared after an ambiguous provider result.");
                if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                {
                    await EnsureCompletionRefillBatchAsync(task, now, cancellationToken);
                    actions.Add($"next_wave_ambiguous_response_after_verified_completion:{waveKey}:{task.TaskKey}");
                    continue;
                }
                blocked++;
                actions.Add($"next_wave_blocked:{waveKey}:ambiguous_provider_outcome");
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                    reason: ambiguous.ReasonCode ?? result.ReasonCode ?? "launch_outcome_unknown",
                    statusDetail: ambiguous.SlotHeld
                        ? "global_slot_retained_until_authoritative_reconciliation"
                        : "launch_state_uncertain; backlog_claim_retained_no_retry",
                    waveKey: waveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    targetId: task.Id,
                    dryRun: false));
            }

            await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
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

        await ProcessVerifiedCompletionsAsync(now, cancellationToken);

        // A stale launch claim may represent a Bridge task created before a worker
        // crash. Only complete authoritative inventory may reconcile it; absent that
        // source, retain the durable Launching reservation and never retry the call.
        var staleBefore = now.AddMinutes(-Math.Max(1, settings.LockLeaseMinutes));
        var staleClaims = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launching && item.UpdatedAt < staleBefore)
            .OrderBy(item => item.UpdatedAt)
            .Take(Math.Max(1, settings.MaxLaunchReconciliations))
            .ToListAsync(cancellationToken);

        foreach (var stale in staleClaims)
        {
            if (globalTaskSlots is null || globalTaskInventory is null)
            {
                stale.LastStatusDetail = "stale_launch_claim_held; authoritative_inventory_not_configured";
                continue;
            }

            var inventory = await globalTaskInventory.ReadAuthoritativeSnapshotAsync(cancellationToken);
            var reservation = await globalTaskSlots.ReserveOrRecoverLaunchAsync(
                stale.Id,
                AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(stale.WaveKey, stale.TaskKey),
                inventory,
                TimeSpan.FromMinutes(Math.Max(1, settings.LockLeaseMinutes)),
                providerGuaranteesIdempotency: false,
                cancellationToken);
            if (reservation.Outcome == AutopilotGlobalTaskSlotOutcomes.AlreadyActive)
            {
                actions.Add($"next_wave_recovered:{stale.WaveKey}:{stale.TaskKey}:inventory");
            }
            else
            {
                stale.LastStatusDetail = reservation.SlotHeld
                    ? "stale_launch_claim_held_until_authoritative_reconciliation"
                    : $"stale_launch_claim_not_retried:{reservation.ReasonCode ?? reservation.Outcome}";
            }
            audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                reason: reservation.ReasonCode ?? reservation.Outcome,
                statusDetail: reservation.SlotHeld ? "reservation_retained_no_provider_retry" : "no_provider_call",
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

            var state = string.IsNullOrWhiteSpace(task.ExternalTaskId)
                ? null
                : await provider.GetTaskStateAsync(task.ExternalTaskId!, cancellationToken);

            if (state is null || !state.IsTerminal)
            {
                var nextReconcileCount = task.ReconcileCount + 1;
                var reasonCode = state is null ? "reconcile_state_unknown" : $"polled_{state.Status}";
                var exhausted = nextReconcileCount >= task.MaxReconciles;
                var nextReconcileAt = exhausted
                    ? task.NextReconcileAt
                    : now.Add(AutopilotNextWavePolicy.ComputeBackoff(nextReconcileCount, settings));
                var lastStatusDetail = exhausted
                    ? "reconcile_exhausted; global_slot_retained_pending_verified_receipt"
                    : task.LastStatusDetail;
                var updatedToken = Guid.NewGuid();
                var updated = await db.AutopilotWaveLaunchTasks
                    .Where(item => item.Id == task.Id
                        && item.Status == AutopilotWaveLaunchStatuses.Launched
                        && item.ConcurrencyToken == task.ConcurrencyToken)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.ReconcileCount, nextReconcileCount)
                        .SetProperty(item => item.UpdatedAt, now)
                        .SetProperty(item => item.ReasonCode, reasonCode)
                        .SetProperty(item => item.NextReconcileAt, nextReconcileAt)
                        .SetProperty(item => item.LastStatusDetail, lastStatusDetail)
                        .SetProperty(item => item.ConcurrencyToken, updatedToken), cancellationToken);
                if (updated != 1)
                {
                    actions.Add($"next_wave_reconcile_raced_with_completion:{task.WaveKey}:{task.TaskKey}");
                }
                else if (exhausted)
                {
                    actions.Add($"next_wave_reconcile_exhausted:{task.WaveKey}:{task.TaskKey}");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Blocked,
                        reason: "reconcile_exhausted",
                        statusDetail: reasonCode,
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
                else if (nextReconcileCount == 1)
                {
                    audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                        reason: reasonCode,
                        statusDetail: "polling_fallback_in_progress",
                        waveKey: task.WaveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
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

            // Provider status is not terminal proof for slot release. The compare-
            // and-swap also prevents this poll result overwriting a concurrent
            // receipt-driven Completed transition.
            var pollToken = Guid.NewGuid();
            var pollUpdated = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Id == task.Id
                    && item.Status == AutopilotWaveLaunchStatuses.Launched
                    && item.ConcurrencyToken == task.ConcurrencyToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ReconcileCount, task.ReconcileCount + 1)
                    .SetProperty(item => item.ExternalRef, state.ExternalRef)
                    .SetProperty(item => item.ReasonCode, $"polled_{state.Status}")
                    .SetProperty(item => item.LastStatusDetail, $"polling_fallback:{submission.Outcome}; slot_retained_pending_verified_receipt")
                    .SetProperty(item => item.NextReconcileAt, now.Add(AutopilotNextWavePolicy.ComputeBackoff(task.ReconcileCount + 1, settings)))
                    .SetProperty(item => item.UpdatedAt, now)
                    .SetProperty(item => item.ConcurrencyToken, pollToken), cancellationToken);
            if (pollUpdated != 1)
            {
                actions.Add($"next_wave_poll_raced_with_completion:{task.WaveKey}:{task.TaskKey}");
                if (batch is not null) await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
                continue;
            }
            actions.Add($"next_wave_reconciled:{task.WaveKey}:{task.TaskKey}:{state.Status}");
            audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                reason: $"polled_{state.Status}",
                statusDetail: $"polling_fallback:{submission.Outcome}; slot_retained_pending_verified_receipt",
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

    private async Task<int> ProcessVerifiedCompletionsAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (globalTaskSlots is null) return 0;

        // Re-scan durable processed events so a crash after event commit but before
        // release/refill is recovered on the next immediate or watchdog cycle.
        var events = await (
                from intakeEvent in db.AutopilotEvents.AsNoTracking()
                join launchTask in db.AutopilotWaveLaunchTasks.AsNoTracking()
                    on new { WaveKey = intakeEvent.WaveKey, TaskKey = intakeEvent.TaskId }
                    equals new { launchTask.WaveKey, TaskKey = launchTask.TaskKey }
                where intakeEvent.Status == AutopilotEventStatuses.Processed
                    && intakeEvent.EventType == AutopilotEventTypes.WaveTaskCompleted
                    && intakeEvent.SignatureVerified
                    && intakeEvent.ReplayProtected
                    && (launchTask.Status == AutopilotWaveLaunchStatuses.Launching
                        || launchTask.Status == AutopilotWaveLaunchStatuses.Launched
                        || (launchTask.Status == AutopilotWaveLaunchStatuses.Completed
                            && launchTask.LastStatusDetail == "slot_released_once"))
                orderby intakeEvent.ProcessedAt descending
                select intakeEvent)
            .Take(MaxCompletionEventsPerCycle)
            .ToListAsync(cancellationToken);

        var released = 0;
        foreach (var intakeEvent in events)
        {
            if (string.IsNullOrWhiteSpace(intakeEvent.WaveKey)
                || string.IsNullOrWhiteSpace(intakeEvent.TaskId))
                continue;

            var task = await db.AutopilotWaveLaunchTasks.FirstOrDefaultAsync(item =>
                item.WaveKey == intakeEvent.WaveKey && item.TaskKey == intakeEvent.TaskId,
                cancellationToken);
            if (task is null
                || task.Status is not (AutopilotWaveLaunchStatuses.Launching
                    or AutopilotWaveLaunchStatuses.Launched
                    or AutopilotWaveLaunchStatuses.Completed)
                || string.IsNullOrWhiteSpace(task.ExternalTaskId))
                continue;

            var idempotencyKey = AutopilotGlobalTaskSlotIdentity.CreateIdempotencyKey(task.WaveKey, task.TaskKey);
            var completion = await globalTaskSlots.CompleteVerifiedAsync(
                task.Id,
                idempotencyKey,
                task.ExternalTaskId,
                intakeEvent.Id,
                cancellationToken);
            if (completion.Outcome is not (AutopilotGlobalTaskSlotOutcomes.Completed
                or AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion))
            {
                db.AutopilotAuditEvents.Add(Audit(
                    AutopilotAuditActions.NextWaveReconciled,
                    AutopilotAuditOutcomes.Blocked,
                    reason: completion.ReasonCode ?? completion.Outcome,
                    statusDetail: "processed_event_did_not_release_slot; terminal_receipt_required",
                    waveKey: task.WaveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    targetId: intakeEvent.Id,
                    dryRun: settings.DryRun));
                continue;
            }

            released++;
            task = await db.AutopilotWaveLaunchTasks.SingleAsync(item => item.Id == task.Id, cancellationToken);
            await EnsureCompletionRefillBatchAsync(task, now, cancellationToken);
        }

        return released;
    }

    private async Task EnsureCompletionRefillBatchAsync(
        AutopilotWaveLaunchTask completedTask,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sourceBatch = await db.AutopilotWaveLaunchBatches.AsNoTracking().FirstOrDefaultAsync(
            item => item.Id == completedTask.BatchId, cancellationToken);
        if (sourceBatch is null
            || (sourceBatch.Status != AutopilotWaveLaunchStatuses.Launched
                && sourceBatch.Status != AutopilotWaveLaunchStatuses.Planned))
        {
            await transaction.RollbackAsync(cancellationToken);
            await MarkCompletionRefillResolvedAsync(completedTask.Id, "verified_completion_no_active_source_batch", now, cancellationToken);
            return;
        }

        var activeSiblingExists = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .AnyAsync(item => item.BatchId == sourceBatch.Id
                && item.Id != completedTask.Id
                && (item.Status == AutopilotWaveLaunchStatuses.Launched
                    || item.Status == AutopilotWaveLaunchStatuses.Launching),
                cancellationToken);
        if (!activeSiblingExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            await MarkCompletionRefillResolvedAsync(completedTask.Id, "verified_completion_no_active_sibling", now, cancellationToken);
            return;
        }

        var refillWaveKey = Bounded($"{completedTask.WaveKey}-refill-{completedTask.Id:N}", 120);
        if (await db.AutopilotWaveLaunchBatches.AsNoTracking().AnyAsync(item =>
                item.SourceRunId == sourceBatch.SourceRunId && item.WaveKey == refillWaveKey,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            await MarkCompletionRefillResolvedAsync(completedTask.Id, "verified_completion_refill_created", now, cancellationToken);
            return;
        }

        var representedBacklogItems = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .Select(item => item.BacklogItemKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        var backlogCandidates = await db.AutopilotBacklogItems.AsNoTracking()
            .Where(item => item.Approved
                && item.ConsumedByWaveKey == null
                && item.Kind == AutopilotBacklogItemKinds.Development
                && !representedBacklogItems.Contains(item.ItemKey))
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.CreatedAt)
            .Take(AutopilotGlobalTaskSlotAllocator.HardMaximum)
            .ToListAsync(cancellationToken);

        AutopilotBacklogItem? backlogItem = null;
        foreach (var candidate in backlogCandidates)
        {
            var claimed = await db.AutopilotBacklogItems
                .Where(item => item.Id == candidate.Id
                    && item.Approved
                    && item.Kind == AutopilotBacklogItemKinds.Development
                    && item.ConsumedByWaveKey == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ConsumedByWaveKey, refillWaveKey)
                    .SetProperty(item => item.ConsumedAt, now)
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (claimed == 1)
            {
                backlogItem = candidate;
                break;
            }
        }
        if (backlogItem is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            await MarkCompletionRefillResolvedAsync(completedTask.Id, "verified_completion_no_approved_backlog", now, cancellationToken);
            return;
        }

        var refillTaskKey = Bounded($"refill-{completedTask.Id:N}", 200);
        var plan = new WaveLaunchTaskPlan(
            refillTaskKey,
            Bounded(backlogItem.Title, 300),
            Bounded(backlogItem.ItemKey, 120),
            Bounded(sourceBatch.Branch, 300),
            Bounded(sourceBatch.BaseSha, 64));
        var refillBatch = new AutopilotWaveLaunchBatch
        {
            Id = Guid.NewGuid(),
            SourceRunId = sourceBatch.SourceRunId,
            SourceWaveKey = sourceBatch.SourceWaveKey,
            WaveKey = refillWaveKey,
            Status = AutopilotWaveLaunchStatuses.Planned,
            ReasonCode = VerifiedCompletionRefillReason,
            LastStatusDetail = "one_approved_backlog_item_after_verified_task_completion",
            BaseSha = plan.BaseSha,
            Branch = plan.Branch,
            PlanFingerprint = AutopilotNextWavePolicy.PlanFingerprint(refillWaveKey, [plan]),
            TaskCount = 1,
            DryRun = settings.DryRun,
            MaxLaunchAttempts = Math.Max(0, settings.MaxLaunchAttempts),
            MaxReconciles = Math.Max(1, settings.MaxLaunchReconciles),
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        };
        var refillTask = new AutopilotWaveLaunchTask
        {
            Id = Guid.NewGuid(),
            BatchId = refillBatch.Id,
            SourceRunId = sourceBatch.SourceRunId,
            SourceWaveKey = sourceBatch.SourceWaveKey,
            WaveKey = refillWaveKey,
            TaskKey = plan.TaskKey,
            Title = plan.Title,
            BacklogItemKey = plan.BacklogItemKey,
            Status = AutopilotWaveLaunchStatuses.Planned,
            Branch = plan.Branch,
            BaseSha = plan.BaseSha,
            DryRun = settings.DryRun,
            MaxLaunchAttempts = refillBatch.MaxLaunchAttempts,
            MaxReconciles = refillBatch.MaxReconciles,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid(),
        };
        var refillAudit = Audit(
            AutopilotAuditActions.NextWavePlanSelected,
            AutopilotAuditOutcomes.Recorded,
            reason: "verified_completion_refill",
            statusDetail: "one_approved_backlog_item_selected",
            waveKey: refillWaveKey,
            taskId: refillTaskKey,
            branch: refillBatch.Branch,
            baseSha: refillBatch.BaseSha,
            targetId: refillBatch.Id,
            dryRun: settings.DryRun);
        db.AutopilotWaveLaunchBatches.Add(refillBatch);
        db.AutopilotWaveLaunchTasks.Add(refillTask);
        db.AutopilotAuditEvents.Add(refillAudit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var refillResolvedToken = Guid.NewGuid();
            var completionMarked = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Id == completedTask.Id
                    && item.Status == AutopilotWaveLaunchStatuses.Completed
                    && item.LastStatusDetail == "slot_released_once")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastStatusDetail, "verified_completion_refill_created")
                    .SetProperty(item => item.UpdatedAt, now)
                    .SetProperty(item => item.ConcurrencyToken, refillResolvedToken), cancellationToken);
            if (completionMarked != 1)
                throw new DbUpdateException("The released completion row changed before its refill was durably marked.");
            await transaction.CommitAsync(cancellationToken);
            await db.Entry(completedTask).ReloadAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.Entry(refillTask).State = EntityState.Detached;
            db.Entry(refillBatch).State = EntityState.Detached;
            db.Entry(refillAudit).State = EntityState.Detached;
            var winnerExists = await db.AutopilotWaveLaunchBatches.AsNoTracking().AnyAsync(item =>
                item.SourceRunId == sourceBatch.SourceRunId && item.WaveKey == refillWaveKey,
                cancellationToken);
            if (!winnerExists) throw;
            await MarkCompletionRefillResolvedAsync(completedTask.Id, "verified_completion_refill_created", now, cancellationToken);
        }
    }

    private async Task MarkCompletionRefillResolvedAsync(
        Guid completedTaskId,
        string statusDetail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var pending = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == completedTaskId
                && item.Status == AutopilotWaveLaunchStatuses.Completed
                && item.LastStatusDetail == "slot_released_once", cancellationToken);
        if (pending is null) return;

        var token = Guid.NewGuid();
        await db.AutopilotWaveLaunchTasks
            .Where(item => item.Id == completedTaskId
                && item.Status == AutopilotWaveLaunchStatuses.Completed
                && item.ConcurrencyToken == pending.ConcurrencyToken
                && item.LastStatusDetail == "slot_released_once")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.LastStatusDetail, statusDetail)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.ConcurrencyToken, token), cancellationToken);
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
            .AsNoTracking()
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

    private async Task ReleaseBacklogItemClaimAsync(
        string backlogItemKey,
        string waveKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backlogItemKey)) return;

        await db.AutopilotBacklogItems
            .Where(item => item.ItemKey == backlogItemKey && item.ConsumedByWaveKey == waveKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConsumedByWaveKey, (string?)null)
                .SetProperty(item => item.ConsumedAt, (DateTime?)null), cancellationToken);
    }

    private async Task<AutopilotWaveLaunchTask?> ClaimLaunchAttemptAsync(AutopilotWaveLaunchTask task, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var maxAttempts = Math.Max(0, settings.MaxLaunchAttempts);
        var claimedToken = Guid.NewGuid();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Id == task.Id
                && item.ConcurrencyToken == task.ConcurrencyToken
                && (item.Status == AutopilotWaveLaunchStatuses.Planned || item.Status == AutopilotWaveLaunchStatuses.Failed)
                && item.LaunchAttempt < maxAttempts)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AutopilotWaveLaunchStatuses.Launching)
                .SetProperty(item => item.LaunchAttempt, item => item.LaunchAttempt + 1)
                .SetProperty(item => item.ReasonCode, "launch_attempt_claimed")
                .SetProperty(item => item.LastStatusDetail, "task_and_approved_backlog_claim_in_progress")
                .SetProperty(item => item.CompletedAt, (DateTime?)null)
                .SetProperty(item => item.ConcurrencyToken, claimedToken)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            await db.Entry(task).ReloadAsync(cancellationToken);
            return null;
        }

        var backlogClaimed = string.IsNullOrWhiteSpace(task.BacklogItemKey)
            ? 0
            : await db.AutopilotBacklogItems
                .Where(item => item.ItemKey == task.BacklogItemKey
                    && item.Approved
                    && item.Kind == AutopilotBacklogItemKinds.Development
                    && (item.ConsumedByWaveKey == null || item.ConsumedByWaveKey == task.WaveKey))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ConsumedByWaveKey, task.WaveKey)
                    .SetProperty(item => item.ConsumedAt, now)
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);

        if (backlogClaimed != 1)
        {
            var deniedToken = Guid.NewGuid();
            await db.AutopilotWaveLaunchTasks
                .Where(item => item.Id == task.Id
                    && item.Status == AutopilotWaveLaunchStatuses.Launching
                    && item.ConcurrencyToken == claimedToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, AutopilotWaveLaunchStatuses.Blocked)
                    .SetProperty(item => item.ReasonCode, "backlog_item_claimed")
                    .SetProperty(item => item.LastStatusDetail, "backlog_item_already_claimed_by_another_wave")
                    .SetProperty(item => item.CompletedAt, now)
                    .SetProperty(item => item.UpdatedAt, now)
                    .SetProperty(item => item.ConcurrencyToken, deniedToken), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await db.Entry(task).ReloadAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        await db.Entry(task).ReloadAsync(cancellationToken);
        return task;
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

    private async Task<bool> ResetDeniedReservationAsync(
        Guid launchTaskId,
        string backlogItemKey,
        string waveKey,
        string reasonCode,
        Guid newConcurrencyToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == launchTaskId, cancellationToken);
        if (current is null
            || current.Status != AutopilotWaveLaunchStatuses.Launching
            || current.ReasonCode != "launch_attempt_claimed"
            || !string.Equals(current.BacklogItemKey, backlogItemKey, StringComparison.Ordinal)
            || !string.Equals(current.WaveKey, waveKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var reset = await db.AutopilotWaveLaunchTasks
            .Where(item => item.Id == launchTaskId
                && item.Status == AutopilotWaveLaunchStatuses.Launching
                && item.ConcurrencyToken == current.ConcurrencyToken
                && item.ReasonCode == "launch_attempt_claimed")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, AutopilotWaveLaunchStatuses.Planned)
                .SetProperty(item => item.LaunchAttempt, Math.Max(0, current.LaunchAttempt - 1))
                .SetProperty(item => item.ReasonCode, reasonCode)
                .SetProperty(item => item.LastStatusDetail, "global_slot_not_authorized; no_provider_call")
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.ConcurrencyToken, newConcurrencyToken), cancellationToken);
        if (reset != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var released = await db.AutopilotBacklogItems
            .Where(item => item.ItemKey == backlogItemKey && item.ConsumedByWaveKey == waveKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConsumedByWaveKey, (string?)null)
                .SetProperty(item => item.ConsumedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (released != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<AutopilotWaveLaunchTask?> ReloadLaunchTaskFreshAsync(Guid launchTaskId, CancellationToken cancellationToken)
    {
        var entries = db.ChangeTracker.Entries<AutopilotWaveLaunchTask>()
            .Where(entry => entry.Entity.Id == launchTaskId)
            .ToList();
        foreach (var entry in entries) entry.State = EntityState.Detached;
        return await db.AutopilotWaveLaunchTasks
            .SingleOrDefaultAsync(item => item.Id == launchTaskId, cancellationToken);
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
            || item.Status == AutopilotWaveLaunchStatuses.Launching
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

        if (tasks.Any(item => item.Status == AutopilotWaveLaunchStatuses.Launched
            || item.Status == AutopilotWaveLaunchStatuses.Launching))
        {
            batch.Status = AutopilotWaveLaunchStatuses.Launched;
            batch.ReasonCode = tasks.Any(item => item.Status == AutopilotWaveLaunchStatuses.Launched)
                ? "launched"
                : "launch_in_progress";
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
        db.Entry(batch).State = EntityState.Detached;
        var persistedBatch = await db.AutopilotWaveLaunchBatches
            .SingleOrDefaultAsync(item => item.Id == batch.Id, cancellationToken);
        if (persistedBatch is null) return;
        var tasks = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .Where(item => item.BatchId == persistedBatch.Id)
            .ToListAsync(cancellationToken);
        RefreshBatchFromTasks(persistedBatch, tasks, now);
        await db.SaveChangesAsync(cancellationToken);
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

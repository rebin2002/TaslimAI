using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    IAutopilotLockService? locks = null,
    AutopilotGlobalTaskSlotAllocator? globalTaskSlots = null,
    AutopilotGlobalTaskInventoryReader? globalTaskInventory = null,
    Action? notifyCommittedEvent = null,
    TimeSpan? completionFenceRenewalInterval = null,
    IServiceScopeFactory? fenceRenewalScopeFactory = null) : IAutopilotNextWaveService
{
    private readonly AutopilotOptions settings = options;
    private readonly Action eventNotifier = notifyCommittedEvent ?? AutopilotEventDispatchSignal.NotifyCommittedEvent;
    private readonly TimeSpan fenceRenewalInterval = completionFenceRenewalInterval
        ?? AutopilotCompletionLaunchFence.RenewalInterval(options.LockLeaseMinutes);
    private const string VerifiedCompletionRefillReason = "verified_completion_refill";
    private const int MaxCompletionEventsPerCycle = 1000;
    private const string RejectedClaimRecoveryMarker = "provider_rejected_before_task_acceptance";
    private const string RejectedClaimReleasedMarker = "provider_rejected_claim_released";
    private const long RepairEscalationAdvisoryLockKey = 0x4155544F52455041L;
    private static readonly SemaphoreSlim LocalRepairEscalationGate = new(1, 1);

    public async Task<AutopilotNextWaveCycleResult> TryLaunchNextWavesAsync(CancellationToken cancellationToken = default)
    {
        var lockService = locks ?? new EfAutopilotLockService(db, settings, timeProvider);
        var audit = new List<AutopilotAuditEvent>();
        var actions = new List<string>();
        var now = UtcNow();

        var control = await ReadControlAsync(cancellationToken);
        if (!settings.Enabled)
            return AutopilotNextWaveCycleResult.Idle("disabled", "feature_flag_disabled");

        if (settings.LiveNextWaveLaunchEnabled
            && settings.LockLeaseMinutes < AutopilotCompletionLaunchFence.RequiredLeaseMinutes(settings.BridgeTimeoutSeconds))
            return AutopilotNextWaveCycleResult.Idle(
                "launch_fence_lease_unsafe",
                "lock_lease_shorter_than_bridge_timeout_and_persistence_margin");

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
        await RepairRejectedBacklogClaimsAsync(now, audit, actions, cancellationToken);

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
        var completionFenceLostDuringLaunchCycle = false;

        foreach (var workItem in workItems)
        {
            if (completionFenceLostDuringLaunchCycle)
            {
                blocked++;
                actions.Add($"next_wave_deferred:{workItem.WaveKey}:completion_launch_fence_lost_after_durable_outcome");
                audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                    reason: "completion_launch_fence_lost_after_durable_outcome",
                    statusDetail: "current_provider_outcome_persisted; no_further_provider_calls_this_cycle",
                    waveKey: workItem.WaveKey,
                    dryRun: false));
                break;
            }

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
                if (cancellationToken.IsCancellationRequested)
                {
                    await FlushAsync(audit, CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var task = candidate;
                var deterministicRejection = IsDeterministicProviderRejection(task);
                var retryable = task.Status == AutopilotWaveLaunchStatuses.Failed
                    && !deterministicRejection
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

                // Inventory may take several pages. Acquire the cross-process
                // intake/launch fence after that read, then re-check the durable
                // queue before reserving capacity or authorizing an external call.
                var completionFence = await lockService.AcquireAsync(
                    AutopilotCompletionLaunchFence.ResourceKey,
                    "autopilot-next-wave-launch",
                    cancellationToken);
                if (!completionFence.IsAcquired)
                {
                    var reset = await ResetClaimedLaunchForFenceAsync(
                        task, waveKey, "completion_launch_fence_contended",
                        "completion_launch_fence_contended; no_provider_call", now, cancellationToken);
                    blocked++;
                    actions.Add($"next_wave_deferred:{waveKey}:completion_launch_fence_contended");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: "completion_launch_fence_contended",
                        statusDetail: reset ? "claim_released_no_provider_call" : "claim_reset_uncertain_backlog_claim_retained",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    if (!reset)
                        await AddRepairEscalationOnceAsync(audit,
                            "completion_launch_fence_contended_and_claim_reset_uncertain",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, cancellationToken);
                    continue;
                }

                var fenceHeartbeatLease = await StartFenceHeartbeatAsync(
                    lockService, completionFence, "next-wave-launch");
                if (fenceHeartbeatLease.Heartbeat is null)
                {
                    try { _ = await lockService.ReleaseAsync(completionFence, CancellationToken.None); }
                    catch (Exception releaseException)
                    {
                        logger.LogError(releaseException,
                            "Autopilot launch fence release failed after initial heartbeat renewal was unavailable. Resource={ResourceKey}; FencingToken={FencingToken}",
                            completionFence.ResourceKey, completionFence.FencingToken);
                    }
                    var reset = await ResetClaimedLaunchForFenceAsync(
                        task, waveKey, "completion_launch_fence_renewal_unavailable",
                        "completion_launch_fence_renewal_unavailable; no_provider_call", now, cancellationToken);
                    blocked++;
                    actions.Add($"next_wave_deferred:{waveKey}:completion_launch_fence_renewal_unavailable");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: "completion_launch_fence_renewal_unavailable",
                        statusDetail: reset ? "claim_released_no_provider_call" : "claim_reset_uncertain_backlog_claim_retained",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    if (!reset)
                        await AddRepairEscalationOnceAsync(audit,
                            "completion_launch_fence_renewal_unavailable_and_claim_reset_uncertain",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, cancellationToken);
                    continue;
                }

                var completionFenceHeartbeatScope = fenceHeartbeatLease.Heartbeat;
                await using var completionFenceRelease = new CompletionLaunchFenceRelease(
                    fenceHeartbeatLease.LockService,
                    completionFenceHeartbeatScope,
                    fenceHeartbeatLease.RenewalScope,
                    logger,
                    () => completionFenceLostDuringLaunchCycle = true);
                if (await HasNonterminalCompletionEventsAsync(cancellationToken))
                {
                    var reset = await ResetClaimedLaunchForFenceAsync(
                        task, waveKey, "completion_event_pending_before_launch",
                        "completion_event_pending; no_provider_call", now, cancellationToken);
                    blocked++;
                    actions.Add($"next_wave_deferred:{waveKey}:completion_event_pending");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: "completion_event_pending_before_launch",
                        statusDetail: reset ? "claim_released_no_provider_call" : "claim_reset_uncertain_backlog_claim_retained",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    if (!reset)
                        await AddRepairEscalationOnceAsync(audit,
                            "completion_event_pending_and_claim_reset_uncertain",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, cancellationToken);
                    continue;
                }

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
                    if (reservation.Outcome != AutopilotGlobalTaskSlotOutcomes.CapacityReached)
                    {
                        await AddRepairEscalationOnceAsync(audit,
                            $"global_slot_reservation_requires_operator_review:{reservation.ReasonCode ?? reservation.Outcome}",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, cancellationToken);
                    }
                    continue;
                }

                var requiredFenceRemainder = TimeSpan.FromSeconds(
                    Math.Clamp(settings.BridgeTimeoutSeconds, 5, 120)
                    + AutopilotCompletionLaunchFence.PersistenceSafetyMarginSeconds);
                var configuredFenceRemainder = TimeSpan.FromMinutes(Math.Max(1, settings.LockLeaseMinutes));
                var fenceRenewedBeforeProvider = !completionFenceHeartbeatScope.IsLost
                    && await completionFenceHeartbeatScope.RenewNowAsync(CancellationToken.None)
                    && !completionFenceHeartbeatScope.IsLost;
                var renewedFenceHandle = completionFenceHeartbeatScope.CurrentHandle;
                var actualFenceRemainder = renewedFenceHandle.ExpiresAt is DateTime leaseExpiry
                    && renewedFenceHandle.LeaseServerTimeUtc is DateTime leaseServerTime
                    ? leaseExpiry - leaseServerTime
                    : TimeSpan.Zero;
                if (!fenceRenewedBeforeProvider
                    || actualFenceRemainder < requiredFenceRemainder
                    || configuredFenceRemainder < requiredFenceRemainder)
                {
                    var held = await globalTaskSlots!.RecordAmbiguousAsync(
                        task.Id,
                        idempotencyKey,
                        "completion_launch_fence_renewal_failed_before_provider_call",
                        CancellationToken.None);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, CancellationToken.None)
                        ?? throw new InvalidOperationException("The durable launch row disappeared while retaining an expiring-fence reservation.");
                    blocked++;
                    actions.Add($"next_wave_blocked:{waveKey}:completion_launch_fence_renewal_failed");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: held.ReasonCode ?? "completion_launch_fence_renewal_failed_before_provider_call",
                        statusDetail: held.SlotHeld
                            ? "slot_retained_no_provider_call_fence_renewal_failed"
                            : "launch_state_uncertain_no_provider_call",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    await AddRepairEscalationOnceAsync(audit,
                        "completion_launch_fence_renewal_failed_before_provider_call; operator_reconciliation_required",
                        waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, CancellationToken.None);
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

                WaveLaunchProviderResult result;
                using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    completionFenceHeartbeatScope.LeaseLostToken);
                try
                {
                    if (completionFenceHeartbeatScope.IsLost)
                        throw new InvalidOperationException("completion_launch_fence_lost_before_provider_call");
                    providerCancellation.Token.ThrowIfCancellationRequested();
                    result = await provider.LaunchTaskAsync(request, providerCancellation.Token);
                    if (!await completionFenceHeartbeatScope.RenewNowAsync(CancellationToken.None))
                        throw new InvalidOperationException("completion_launch_fence_lost_after_provider_response");
                }
                catch (Exception exception)
                {
                    if (!completionFenceHeartbeatScope.IsLost)
                        _ = await completionFenceHeartbeatScope.RenewNowAsync(CancellationToken.None);
                    // A thrown transport/cancellation error does not prove that
                    // the remote create failed. Persist the held reservation
                    // without the caller token so cancellation cannot erase the
                    // ambiguity record or make the backlog eligible for retry.
                    var callerCanceled = exception is OperationCanceledException
                        && cancellationToken.IsCancellationRequested;
                    var exceptionReason = completionFenceHeartbeatScope.IsLost
                        ? "completion_launch_fence_lost_ambiguous"
                        : exception is OperationCanceledException
                            ? "provider_call_canceled_ambiguous"
                            : "provider_call_exception_ambiguous";
                    logger.LogError(
                        "Autopilot provider call threw after a durable claim; retaining the global slot. WaveKey={WaveKey}; TaskKey={TaskKey}; ExceptionType={ExceptionType}",
                        waveKey, task.TaskKey, exception.GetType().Name);

                    var ambiguousException = await globalTaskSlots!.RecordAmbiguousAsync(
                        task.Id,
                        idempotencyKey,
                        exceptionReason,
                        CancellationToken.None);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, CancellationToken.None)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after a thrown provider call.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        await EnsureCompletionRefillBatchAsync(task, now, CancellationToken.None);
                        actions.Add($"next_wave_provider_exception_after_verified_completion:{waveKey}:{task.TaskKey}");
                        if (callerCanceled)
                        {
                            await FlushAsync(audit, CancellationToken.None);
                            throw;
                        }

                        continue;
                    }

                    blocked++;
                    actions.Add($"next_wave_blocked:{waveKey}:ambiguous_provider_exception");
                    audit.Add(Audit(AutopilotAuditActions.NextWaveLaunchBlocked, AutopilotAuditOutcomes.Blocked,
                        reason: ambiguousException.ReasonCode ?? exceptionReason,
                        statusDetail: ambiguousException.SlotHeld
                            ? "global_slot_retained_until_authoritative_reconciliation"
                            : "launch_state_uncertain; backlog_claim_retained_no_retry",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                        reason: AutopilotHumanDecisions.RepairEscalation,
                        statusDetail: "ambiguous_provider_outcome_requires_authoritative_reconciliation",
                        waveKey: waveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: false));
                    if (callerCanceled)
                    {
                        await FlushAsync(audit, CancellationToken.None);
                        throw;
                    }

                    continue;
                }
                var outcomePersistenceToken = CancellationToken.None;
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
                        await AddRepairEscalationOnceAsync(audit,
                            "provider_claimed_success_without_external_identity; slot_and_backlog_claim_retained",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                        continue;
                    }

                    task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared before acceptance persistence.");
                    var accepted = await globalTaskSlots!.RecordAcceptedAsync(
                        task.Id,
                        idempotencyKey,
                        result.ExternalTaskId,
                        outcomePersistenceToken);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider acceptance.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        // A valid receipt may race with this response handler. The
                        // terminal transition wins; never write Launched over it.
                        await EnsureCompletionRefillBatchAsync(task, now, outcomePersistenceToken);
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
                                .SetProperty(item => item.ConcurrencyToken, metadataToken), outcomePersistenceToken)
                        : 0;
                    task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider acceptance.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        await EnsureCompletionRefillBatchAsync(task, now, outcomePersistenceToken);
                        launched++;
                        actions.Add($"next_wave_launched:{waveKey}:{task.TaskKey}:completed_before_ack");
                        continue;
                    }

                    if (metadataUpdated != 1
                        || task.Status != AutopilotWaveLaunchStatuses.Launched
                        || !string.Equals(task.ExternalTaskId, result.ExternalTaskId, StringComparison.Ordinal))
                    {
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:accepted_identity_state_changed_slot_retained");
                        await AddRepairEscalationOnceAsync(audit,
                            "accepted_provider_identity_or_state_changed; slot_and_backlog_claim_retained",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
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
                        outcomePersistenceToken);
                    task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after provider rejection.");
                    if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                    {
                        await EnsureCompletionRefillBatchAsync(task, now, outcomePersistenceToken);
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
                        await AddRepairEscalationOnceAsync(audit,
                            "provider_rejection_persistence_ambiguous; slot_and_backlog_claim_retained",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                        continue;
                    }
                    if (rejected.Outcome != AutopilotGlobalTaskSlotOutcomes.LaunchRejected
                        || task.Status != AutopilotWaveLaunchStatuses.Failed)
                    {
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejection_state_uncertain");
                        await AddRepairEscalationOnceAsync(audit,
                            "provider_rejection_state_uncertain; no_backlog_claim_release",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                        continue;
                    }

                    var rejectionToken = Guid.NewGuid();
                    var rejectionRaced = false;
                    var claimReleaseMismatch = false;
                    await using (var rejectionTransaction = await db.Database.BeginTransactionAsync(outcomePersistenceToken))
                    {
                        var rejectionUpdated = await db.AutopilotWaveLaunchTasks
                            .Where(item => item.Id == task.Id
                                && item.Status == AutopilotWaveLaunchStatuses.Failed
                                && item.ConcurrencyToken == task.ConcurrencyToken)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(item => item.ReasonCode, result.ReasonCode ?? "launch_rejected")
                                .SetProperty(item => item.LastStatusDetail, RejectedClaimReleasedMarker)
                                .SetProperty(item => item.CompletedAt, now)
                            .SetProperty(item => item.UpdatedAt, now)
                            .SetProperty(item => item.ConcurrencyToken, rejectionToken), outcomePersistenceToken);
                        if (rejectionUpdated != 1)
                        {
                            await rejectionTransaction.RollbackAsync(outcomePersistenceToken);
                            rejectionRaced = true;
                        }
                        else
                        {
                            // RecordRejectedAsync and this service transaction are separate.
                            // The allocator's durable marker is also scanned on restart if
                            // this exact-owner release cannot be completed.
                            var releasedClaims = await ReleaseBacklogItemClaimAsync(task.BacklogItemKey, waveKey, outcomePersistenceToken);
                            if (releasedClaims != 1)
                            {
                                await rejectionTransaction.RollbackAsync(outcomePersistenceToken);
                                claimReleaseMismatch = true;
                            }
                            else
                            {
                                db.AutopilotAuditEvents.Add(Audit(AutopilotAuditActions.NextWaveLaunchFailed, AutopilotAuditOutcomes.Blocked,
                                    reason: result.ReasonCode ?? "provider_rejected",
                                    statusDetail: RejectedClaimReleasedMarker,
                                    waveKey: waveKey,
                                    taskId: task.TaskKey,
                                    attempt: task.LaunchAttempt,
                                    targetId: task.Id,
                                    dryRun: false));
                                await AddRepairEscalationOnceAsync(audit,
                                    result.ReasonCode ?? "provider_rejected",
                                    waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                                await rejectionTransaction.CommitAsync(outcomePersistenceToken);
                            }
                        }
                    }

                    if (rejectionRaced)
                    {
                        task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                            ?? throw new InvalidOperationException("The durable launch row disappeared after rejection race.");
                        if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                        {
                            await EnsureCompletionRefillBatchAsync(task, now, outcomePersistenceToken);
                            launched++;
                            actions.Add($"next_wave_rejected_after_verified_completion:{waveKey}:{task.TaskKey}");
                            continue;
                        }

                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejection_raced_with_completion");
                        await AddRepairEscalationOnceAsync(audit,
                            "deterministic_rejection_transition_raced; backlog_claim_not_released",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                        continue;
                    }

                    if (claimReleaseMismatch)
                    {
                        task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                            ?? throw new InvalidOperationException("The durable launch row disappeared after claim-release mismatch.");
                        blocked++;
                        actions.Add($"next_wave_blocked:{waveKey}:rejected_backlog_claim_owner_mismatch");
                        await AddRepairEscalationOnceAsync(audit,
                            "deterministic_rejection_backlog_claim_not_owned; recovery_marker_retained",
                            waveKey, task.TaskKey, task.LaunchAttempt, task.Id, false, outcomePersistenceToken);
                        continue;
                    }

                    task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                        ?? throw new InvalidOperationException("The durable launch row disappeared after rejection persistence.");
                    failed++;
                    continue;
                }

                // A timeout or transport failure is ambiguous: the Bridge may have
                // created a task before the response was lost. Never reset to Planned
                // or retry until authoritative inventory proves the outcome.
                var ambiguous = await globalTaskSlots!.RecordAmbiguousAsync(
                    task.Id,
                    idempotencyKey,
                    result.ReasonCode ?? "launch_outcome_unknown",
                    outcomePersistenceToken);
                task = await ReloadLaunchTaskFreshAsync(task.Id, outcomePersistenceToken)
                    ?? throw new InvalidOperationException("The durable launch row disappeared after an ambiguous provider result.");
                if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                {
                    await EnsureCompletionRefillBatchAsync(task, now, outcomePersistenceToken);
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
                audit.Add(Audit(AutopilotAuditActions.HumanDecisionRequired, AutopilotAuditOutcomes.Required,
                    reason: AutopilotHumanDecisions.RepairEscalation,
                    statusDetail: "ambiguous_provider_outcome_requires_authoritative_reconciliation",
                    waveKey: waveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    targetId: task.Id,
                    dryRun: false));
            }

            await RefreshBatchFromDatabaseAsync(batch, now, CancellationToken.None);
            planned += tasks.Count;
            await FlushAsync(audit, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
        }

        await FlushAsync(audit, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = launched > 0 || simulated > 0 ? "next_wave_processed" : blocked > 0 ? "next_wave_blocked" : "next_wave_idle";
        logger.LogInformation(
            "Autopilot next-wave pass completed. Outcome={Outcome}; Examined={Examined}; Launched={Launched}; Simulated={Simulated}; Blocked={Blocked}; Failed={Failed}; DryRun={DryRun}",
            outcome, examined, launched, simulated, blocked, failed, settings.DryRun);

        return new AutopilotNextWaveCycleResult(outcome, null, examined, planned, launched, simulated, blocked, failed, 0, actions);
    }

    public async Task<AutopilotNextWaveCycleResult> ReconcileLaunchesAsync(CancellationToken cancellationToken = default)
    {
        var lockService = locks ?? new EfAutopilotLockService(db, settings, timeProvider);
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
        await RepairRejectedBacklogClaimsAsync(now, audit, actions, cancellationToken);

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
                await AddRepairEscalationOnceAsync(audit,
                    "stale_launch_claim_held; authoritative_inventory_not_configured",
                    stale.WaveKey, stale.TaskKey, stale.LaunchAttempt, stale.Id, settings.DryRun, cancellationToken);
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
                var pollScheduled = await ScheduleRecoveredLaunchPollAsync(stale, now, audit, cancellationToken);
                actions.Add(pollScheduled
                    ? $"next_wave_recovered:{stale.WaveKey}:{stale.TaskKey}:inventory_poll_scheduled"
                    : $"next_wave_recovered:{stale.WaveKey}:{stale.TaskKey}:inventory_repair_required");
            }
            else
            {
                stale.LastStatusDetail = reservation.SlotHeld
                    ? "stale_launch_claim_held_until_authoritative_reconciliation"
                    : $"stale_launch_claim_not_retried:{reservation.ReasonCode ?? reservation.Outcome}";
                await AddRepairEscalationOnceAsync(audit,
                    stale.LastStatusDetail,
                    stale.WaveKey, stale.TaskKey, stale.LaunchAttempt, stale.Id, settings.DryRun, cancellationToken);
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
                int updated;
                await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
                {
                    updated = await db.AutopilotWaveLaunchTasks
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
                    if (updated == 1 && exhausted)
                    {
                        db.AutopilotAuditEvents.Add(Audit(AutopilotAuditActions.NextWaveReconciled,
                            AutopilotAuditOutcomes.Blocked,
                            reason: "reconcile_exhausted",
                            statusDetail: reasonCode,
                            waveKey: task.WaveKey,
                            taskId: task.TaskKey,
                            attempt: task.LaunchAttempt,
                            targetId: task.Id,
                            dryRun: settings.DryRun));
                        await AddRepairEscalationOnceAsync(null,
                            "reconcile_exhausted; verified_terminal_receipt_or_operator_repair_required",
                            task.WaveKey, task.TaskKey, task.LaunchAttempt, task.Id, settings.DryRun, cancellationToken);
                    }

                    if (updated == 1) await transaction.CommitAsync(cancellationToken);
                    else await transaction.RollbackAsync(cancellationToken);
                }
                if (updated != 1)
                {
                    actions.Add($"next_wave_reconcile_raced_with_completion:{task.WaveKey}:{task.TaskKey}");
                }
                else if (exhausted)
                {
                    actions.Add($"next_wave_reconcile_exhausted:{task.WaveKey}:{task.TaskKey}");
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

            // Provider status is not terminal proof for slot release. Persist the
            // poll count/state first; do not enqueue an internal event unless this
            // task's compare-and-swap wins over any concurrent receipt transition.
            var terminalPollCount = task.ReconcileCount + 1;
            var terminalPollExhausted = terminalPollCount >= task.MaxReconciles;
            var terminalPollDetail = terminalPollExhausted
                ? "reconcile_exhausted; global_slot_retained_pending_verified_receipt"
                : $"polling_fallback:{Bounded(state.Status, 40)}; slot_retained_pending_verified_receipt";
            var terminalPollAt = terminalPollExhausted
                ? task.NextReconcileAt
                : now.Add(AutopilotNextWavePolicy.ComputeBackoff(terminalPollCount, settings));
            var pollToken = Guid.NewGuid();
            int pollUpdated;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                pollUpdated = await db.AutopilotWaveLaunchTasks
                    .Where(item => item.Id == task.Id
                        && item.Status == AutopilotWaveLaunchStatuses.Launched
                        && item.ConcurrencyToken == task.ConcurrencyToken)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.ReconcileCount, terminalPollCount)
                        .SetProperty(item => item.ExternalRef, state.ExternalRef)
                        .SetProperty(item => item.ReasonCode, $"polled_{state.Status}")
                        .SetProperty(item => item.LastStatusDetail, terminalPollDetail)
                        .SetProperty(item => item.NextReconcileAt, terminalPollAt)
                        .SetProperty(item => item.UpdatedAt, now)
                        .SetProperty(item => item.ConcurrencyToken, pollToken), cancellationToken);
                if (pollUpdated == 1 && terminalPollExhausted)
                {
                    db.AutopilotAuditEvents.Add(Audit(AutopilotAuditActions.NextWaveReconciled,
                        AutopilotAuditOutcomes.Blocked,
                        reason: "reconcile_exhausted",
                        statusDetail: $"terminal_poll:{state.Status};verified_receipt_required",
                        waveKey: task.WaveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                    await AddRepairEscalationOnceAsync(null,
                        "terminal_poll_reconcile_exhausted; verified_terminal_receipt_or_operator_repair_required",
                        task.WaveKey, task.TaskKey, task.LaunchAttempt, task.Id, settings.DryRun, cancellationToken);
                }

                if (pollUpdated == 1) await transaction.CommitAsync(cancellationToken);
                else await transaction.RollbackAsync(cancellationToken);
            }
            if (pollUpdated != 1)
            {
                actions.Add($"next_wave_poll_raced_with_completion:{task.WaveKey}:{task.TaskKey}");
                if (batch is not null) await RefreshBatchFromDatabaseAsync(batch, now, cancellationToken);
                continue;
            }

            // The queued internal record is a diagnostic only (the orchestrator
            // must never apply an unsigned polling observation as task completion).
            // Inserting only after the CAS also prevents an orphan poll event when
            // an authoritative receipt has already completed the launch row.
            var signal = BuildPollDerivedSignal(task, state, batch);
            var pollFence = await lockService.AcquireAsync(
                AutopilotCompletionLaunchFence.ResourceKey,
                "autopilot-poll-observation",
                cancellationToken);
            if (!pollFence.IsAcquired)
            {
                actions.Add($"next_wave_poll_event_deferred:{task.WaveKey}:{task.TaskKey}:completion_launch_fence_contended");
            }
            else
            {
                var pollHeartbeatLease = await StartFenceHeartbeatAsync(lockService, pollFence, "polling-intake");
                if (pollHeartbeatLease.Heartbeat is null)
                {
                    try { _ = await lockService.ReleaseAsync(pollFence, CancellationToken.None); }
                    catch (Exception releaseException)
                    {
                        logger.LogError(releaseException,
                            "Autopilot polling fence release failed after heartbeat initialization failed. Resource={ResourceKey}; FencingToken={FencingToken}",
                            pollFence.ResourceKey, pollFence.FencingToken);
                    }
                    actions.Add($"next_wave_poll_event_deferred:{task.WaveKey}:{task.TaskKey}:fence_renewal_unavailable");
                }
                else
                {
                    var pollHeartbeat = pollHeartbeatLease.Heartbeat;
                    await using var pollFenceRelease = new CompletionLaunchFenceRelease(
                        pollHeartbeatLease.LockService,
                        pollHeartbeat,
                        pollHeartbeatLease.RenewalScope,
                        logger);
                    AutopilotIntakeResult? submission = null;
                    await pollHeartbeat.StopRenewalLoopAsync();
                    if (!pollHeartbeat.IsLost
                        && lockService is IAutopilotLockLeaseRenewalService requestScopeRenewal)
                    {
                        await using var pollTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
                        var transactionFence = await requestScopeRenewal.RenewAsync(pollFence, cancellationToken);
                        if (transactionFence.IsAcquired
                            && transactionFence.FencingToken == pollFence.FencingToken
                            && string.Equals(transactionFence.OwnerToken, pollFence.OwnerToken, StringComparison.Ordinal))
                        {
                            submission = await intake.SubmitInternalAsync(
                                new AutopilotInternalSignalRequest(
                                    AutopilotInternalSignalSources.Polling,
                                    PollEventId(task.ExternalTaskId, state.Status),
                                    AutopilotEventTypes.WaveTaskCompleted,
                                    signal,
                                    "polling_fallback"),
                                cancellationToken);
                            await pollTransaction.CommitAsync(cancellationToken);
                        }
                        else
                        {
                            await pollTransaction.RollbackAsync(CancellationToken.None);
                            actions.Add($"next_wave_poll_event_deferred:{task.WaveKey}:{task.TaskKey}:fence_lost_before_persistence");
                        }
                    }
                    else
                    {
                        actions.Add($"next_wave_poll_event_deferred:{task.WaveKey}:{task.TaskKey}:fence_renewal_unavailable");
                    }

                    var pollFenceReleased = await pollFenceRelease.ReleaseAsync();

                    if (submission?.Outcome is AutopilotIntakeOutcomes.Accepted or AutopilotIntakeOutcomes.Duplicate)
                    {
                        if (pollFenceReleased)
                            eventNotifier();
                        else
                            actions.Add($"next_wave_poll_event_wake_deferred:{task.WaveKey}:{task.TaskKey}:fence_release_uncertain");
                    }
                    else if (submission is not null)
                        actions.Add($"next_wave_poll_event_not_queued:{task.WaveKey}:{task.TaskKey}:{submission.Outcome}");
                }
            }

            actions.Add($"next_wave_reconciled:{task.WaveKey}:{task.TaskKey}:{state.Status}");
            if (terminalPollExhausted)
            {
                actions.Add($"next_wave_reconcile_exhausted:{task.WaveKey}:{task.TaskKey}");
            }
            else
            {
                audit.Add(Audit(AutopilotAuditActions.NextWaveReconciled, AutopilotAuditOutcomes.Recorded,
                    reason: $"polled_{state.Status}",
                    statusDetail: terminalPollDetail,
                    waveKey: task.WaveKey,
                    taskId: task.TaskKey,
                    attempt: task.LaunchAttempt,
                    targetId: task.Id,
                    dryRun: settings.DryRun));
            }
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
                var failureDetail = $"processed_completion_requires_repair:{completion.ReasonCode ?? completion.Outcome}";
                if (await AddRepairEscalationOnceAsync(null, failureDetail,
                        task.WaveKey, task.TaskKey, task.LaunchAttempt, intakeEvent.Id, settings.DryRun, cancellationToken))
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
                }
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

        var refillWaveKey = BuildCompletionRefillWaveKey(completedTask.WaveKey, completedTask.Id);
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

    private async Task<bool> ScheduleRecoveredLaunchPollAsync(
        AutopilotWaveLaunchTask stale,
        DateTime now,
        List<AutopilotAuditEvent> audit,
        CancellationToken cancellationToken)
    {
        var recovered = await ReloadLaunchTaskFreshAsync(stale.Id, cancellationToken);
        if (recovered is null
            || recovered.Status != AutopilotWaveLaunchStatuses.Launched
            || string.IsNullOrWhiteSpace(recovered.ExternalTaskId))
        {
            await AddRepairEscalationOnceAsync(audit,
                "inventory_recovery_did_not_persist_launch_identity; slot_remains_held",
                stale.WaveKey, stale.TaskKey, stale.LaunchAttempt, stale.Id, settings.DryRun, cancellationToken);
            return false;
        }

        var canPoll = recovered.ReconcileCount < recovered.MaxReconciles;
        var nextPollAt = canPoll
            ? now.Add(AutopilotNextWavePolicy.ComputeBackoff(recovered.ReconcileCount + 1, settings))
            : (DateTime?)null;
        var statusDetail = canPoll
            ? "recovered_from_authoritative_inventory;bounded_poll_scheduled"
            : "recovered_from_authoritative_inventory;polling_exhausted;repair_required";
        var token = Guid.NewGuid();
        await using (var scheduleTransaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var updated = await db.AutopilotWaveLaunchTasks
                .Where(item => item.Id == recovered.Id
                    && item.Status == AutopilotWaveLaunchStatuses.Launched
                    && item.ConcurrencyToken == recovered.ConcurrencyToken)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.NextReconcileAt, nextPollAt)
                    .SetProperty(item => item.LastStatusDetail, statusDetail)
                    .SetProperty(item => item.UpdatedAt, now)
                    .SetProperty(item => item.ConcurrencyToken, token), cancellationToken);
            if (updated == 1)
            {
                if (!canPoll)
                {
                    await AddRepairEscalationOnceAsync(audit,
                        "inventory_recovered_launch_polling_exhausted; verified_receipt_or_operator_repair_required",
                        stale.WaveKey, stale.TaskKey, stale.LaunchAttempt, stale.Id, settings.DryRun, cancellationToken);
                }
                await scheduleTransaction.CommitAsync(cancellationToken);
                return canPoll;
            }

            await scheduleTransaction.RollbackAsync(cancellationToken);
        }

        var latest = await ReloadLaunchTaskFreshAsync(stale.Id, cancellationToken);
        if (latest?.Status == AutopilotWaveLaunchStatuses.Completed)
            return false;

        await AddRepairEscalationOnceAsync(audit,
            "inventory_recovered_launch_poll_schedule_raced; slot_remains_held",
            stale.WaveKey, stale.TaskKey, stale.LaunchAttempt, stale.Id, settings.DryRun, cancellationToken);
        return false;
    }

    private async Task<int> RepairRejectedBacklogClaimsAsync(
        DateTime now,
        List<AutopilotAuditEvent> audit,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        // RecordRejectedAsync commits the definitive no-create result before the
        // service can release the backlog claim. If the process stops in that gap,
        // its exact durable marker is sufficient to retry only the claim release.
        var pending = await db.AutopilotWaveLaunchTasks
            .AsNoTracking()
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Failed
                && item.ExternalTaskId == null
                && item.LastStatusDetail == RejectedClaimRecoveryMarker)
            .OrderBy(item => item.CompletedAt)
            .Take(Math.Max(1, settings.MaxLaunchReconciliations))
            .ToListAsync(cancellationToken);

        var repaired = 0;
        foreach (var task in pending)
        {
            if (string.IsNullOrWhiteSpace(task.WaveKey) || string.IsNullOrWhiteSpace(task.BacklogItemKey))
            {
                await AddRepairEscalationOnceAsync(audit,
                    "deterministic_rejection_claim_recovery_missing_backlog_identity",
                    task.WaveKey, task.TaskKey, task.LaunchAttempt, task.Id, settings.DryRun, cancellationToken);
                continue;
            }

            var claimReleaseMismatch = false;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var token = Guid.NewGuid();
                var claimed = await db.AutopilotWaveLaunchTasks
                    .Where(item => item.Id == task.Id
                        && item.Status == AutopilotWaveLaunchStatuses.Failed
                        && item.ExternalTaskId == null
                        && item.ConcurrencyToken == task.ConcurrencyToken
                        && item.LastStatusDetail == RejectedClaimRecoveryMarker)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.LastStatusDetail, RejectedClaimReleasedMarker)
                        .SetProperty(item => item.UpdatedAt, now)
                        .SetProperty(item => item.ConcurrencyToken, token), cancellationToken);
                if (claimed != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                var releasedClaims = await ReleaseBacklogItemClaimAsync(task.BacklogItemKey, task.WaveKey, cancellationToken);
                if (releasedClaims != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    claimReleaseMismatch = true;
                }
                else
                {
                    db.AutopilotAuditEvents.Add(Audit(AutopilotAuditActions.NextWaveLaunchFailed, AutopilotAuditOutcomes.Blocked,
                        reason: task.ReasonCode ?? "provider_rejected",
                        statusDetail: "deterministic_rejection_claim_recovered_after_interruption",
                        waveKey: task.WaveKey,
                        taskId: task.TaskKey,
                        attempt: task.LaunchAttempt,
                        targetId: task.Id,
                        dryRun: settings.DryRun));
                    await AddRepairEscalationOnceAsync(audit,
                        "deterministic_rejection_claim_recovered_after_interruption",
                        task.WaveKey, task.TaskKey, task.LaunchAttempt, task.Id, settings.DryRun, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    repaired++;
                    actions.Add($"next_wave_recovered_rejected_backlog_claim:{task.WaveKey}:{task.TaskKey}");
                }
            }

            if (claimReleaseMismatch)
            {
                await AddRepairEscalationOnceAsync(audit,
                    "deterministic_rejection_backlog_claim_not_owned; recovery_marker_retained",
                    task.WaveKey, task.TaskKey, task.LaunchAttempt, task.Id, settings.DryRun, cancellationToken);
            }
        }

        return repaired;
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

    private async Task<int> ReleaseBacklogItemClaimAsync(
        string backlogItemKey,
        string waveKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(backlogItemKey)) return 0;

        return await db.AutopilotBacklogItems
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
                && item.LastStatusDetail != RejectedClaimRecoveryMarker
                && item.LastStatusDetail != RejectedClaimReleasedMarker
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
            TaskId = task.TaskKey,
            // task.list/task-state polling has no authoritative stop_reason or
            // background-job flag. Even a provider-reported terminal success is
            // not completion evidence and must not advance a gate.
            Outcome = AutopilotTaskOutcomes.Blocked,
            FailureClass = AutopilotFailureClasses.Unknown,
            Attempt = task.LaunchAttempt,
            Evidence = $"polling_fallback:{Bounded(state.Status, 40)}",
            Checks = null,
            RequiresHumanDecision = true,
            HumanDecisionKind = AutopilotHumanDecisions.RepairEscalation,
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
        CancellationToken cancellationToken,
        string statusDetail = "global_slot_not_authorized; no_provider_call")
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
                .SetProperty(item => item.LastStatusDetail, statusDetail)
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

    private Task<bool> HasNonterminalCompletionEventsAsync(CancellationToken cancellationToken) =>
        db.AutopilotEvents.AsNoTracking()
            .AnyAsync(item => item.Status != AutopilotEventStatuses.Processed
                && item.Status != AutopilotEventStatuses.DuplicateIgnored
                && item.Status != AutopilotEventStatuses.RejectedUnsigned
                && item.Status != AutopilotEventStatuses.RejectedReplay
                && item.Status != AutopilotEventStatuses.RejectedInvalid
                && item.Status != AutopilotEventStatuses.Failed, cancellationToken);

    private async Task<bool> ResetClaimedLaunchForFenceAsync(
        AutopilotWaveLaunchTask task,
        string waveKey,
        string reasonCode,
        string statusDetail,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        var reset = await ResetDeniedReservationAsync(
            task.Id, task.BacklogItemKey, waveKey, reasonCode, token, now, cancellationToken, statusDetail);
        if (!reset)
        {
            task.LastStatusDetail = "completion_fence_reset_uncertain; backlog_claim_retained";
            return false;
        }

        task.Status = AutopilotWaveLaunchStatuses.Planned;
        task.LaunchAttempt = Math.Max(0, task.LaunchAttempt - 1);
        task.ReasonCode = reasonCode;
        task.LastStatusDetail = statusDetail;
        task.UpdatedAt = now;
        task.ConcurrencyToken = token;
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

    private static bool IsDeterministicProviderRejection(AutopilotWaveLaunchTask task) =>
        string.Equals(task.LastStatusDetail, RejectedClaimRecoveryMarker, StringComparison.Ordinal)
        || string.Equals(task.LastStatusDetail, RejectedClaimReleasedMarker, StringComparison.Ordinal);

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

    private async Task<bool> AddRepairEscalationOnceAsync(
        List<AutopilotAuditEvent>? audit,
        string statusDetail,
        string? waveKey,
        string? taskId,
        int attempt,
        Guid? targetId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        await LocalRepairEscalationGate.WaitAsync(cancellationToken);
        try
        {
            if (db.Database.CurrentTransaction is null)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(
                    db.Database.IsNpgsql()
                        ? System.Data.IsolationLevel.ReadCommitted
                        : System.Data.IsolationLevel.Serializable,
                    cancellationToken);
                await AcquireRepairEscalationFenceAsync(cancellationToken);
                var inserted = await PersistRepairEscalationIfMissingAsync(
                    audit, statusDetail, waveKey, taskId, attempt, targetId, dryRun, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return inserted;
            }

            await AcquireRepairEscalationFenceAsync(cancellationToken);
            return await PersistRepairEscalationIfMissingAsync(
                audit, statusDetail, waveKey, taskId, attempt, targetId, dryRun, cancellationToken);
        }
        finally
        {
            LocalRepairEscalationGate.Release();
        }
    }

    private async Task AcquireRepairEscalationFenceAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({RepairEscalationAdvisoryLockKey})",
                cancellationToken);
        }
    }

    private async Task<bool> PersistRepairEscalationIfMissingAsync(
        List<AutopilotAuditEvent>? audit,
        string statusDetail,
        string? waveKey,
        string? taskId,
        int attempt,
        Guid? targetId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        bool Matches(AutopilotAuditEvent item) =>
            item.Action == AutopilotAuditActions.HumanDecisionRequired
            && item.Reason == AutopilotHumanDecisions.RepairEscalation
            && item.StatusDetail == statusDetail
            && item.TargetId == targetId;

        // Persist escalation immediately, in the same transaction as a terminal
        // hold/recovery transition when the caller already owns one. This closes
        // the crash window between committing the held state and flushing audit.
        audit?.RemoveAll(item => Matches(item));

        if (db.AutopilotAuditEvents.Local.Any(Matches))
        {
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (targetId.HasValue && await db.AutopilotAuditEvents.AsNoTracking().AnyAsync(item =>
                item.Action == AutopilotAuditActions.HumanDecisionRequired
                    && item.Reason == AutopilotHumanDecisions.RepairEscalation
                    && item.StatusDetail == statusDetail
                    && item.TargetId == targetId,
                cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            return false;
        }

        var escalation = Audit(
            AutopilotAuditActions.HumanDecisionRequired,
            AutopilotAuditOutcomes.Required,
            reason: AutopilotHumanDecisions.RepairEscalation,
            statusDetail: statusDetail,
            waveKey: waveKey,
            taskId: taskId,
            attempt: attempt,
            targetId: targetId,
            dryRun: dryRun);
        db.AutopilotAuditEvents.Add(escalation);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

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

    private static string BuildCompletionRefillWaveKey(string sourceWaveKey, Guid completedTaskId)
    {
        // Keep the globally unique source task ID at the end. Truncating the
        // whole concatenated key could erase it for 120-character wave keys and
        // collapse multiple sibling completions into the same refill batch.
        var suffix = $"-refill-{completedTaskId:N}";
        var prefixLength = Math.Max(0, AutopilotNextWavePolicy.MaxWaveKeyLength - suffix.Length);
        var prefix = sourceWaveKey.Length <= prefixLength ? sourceWaveKey : sourceWaveKey[..prefixLength];
        return prefix + suffix;
    }

    private static string Bounded(string? value, int max)
    {
        var candidate = value ?? string.Empty;
        return candidate.Length <= max ? candidate : candidate[..max];
    }

    private async Task<FenceHeartbeatLease> StartFenceHeartbeatAsync(
        IAutopilotLockService fallbackLocks,
        AutopilotLockHandle handle,
        string operation)
    {
        if (fenceRenewalScopeFactory is null)
        {
            logger.LogError(
                "Autopilot cannot renew a completion/launch fence without an isolated dependency-injection scope. Operation={Operation}; Resource={ResourceKey}; FencingToken={FencingToken}",
                operation, handle.ResourceKey, handle.FencingToken);
            return new FenceHeartbeatLease(null, fallbackLocks, null);
        }

        IServiceScope? renewalScope = null;
        var renewalLocks = fallbackLocks;
        try
        {
            renewalScope = fenceRenewalScopeFactory?.CreateScope();
            if (renewalScope is not null)
                renewalLocks = renewalScope.ServiceProvider.GetRequiredService<IAutopilotLockService>();

            var heartbeat = await AutopilotLockLeaseHeartbeat.StartAsync(
                renewalLocks,
                handle,
                fenceRenewalInterval,
                logger,
                CancellationToken.None);
            if (heartbeat is null)
            {
                renewalScope?.Dispose();
                renewalScope = null;
            }

            return new FenceHeartbeatLease(renewalScope, renewalLocks, heartbeat);
        }
        catch (Exception exception)
        {
            try { renewalScope?.Dispose(); }
            catch (Exception disposeException)
            {
                logger.LogError(disposeException,
                    "Autopilot isolated fence-renewal scope disposal failed. Operation={Operation}", operation);
            }
            logger.LogError(exception,
                "Autopilot could not initialize an isolated fence-renewal heartbeat. Operation={Operation}; Resource={ResourceKey}; FencingToken={FencingToken}",
                operation, handle.ResourceKey, handle.FencingToken);
            return new FenceHeartbeatLease(null, fallbackLocks, null);
        }
    }

    private sealed record FenceHeartbeatLease(
        IServiceScope? RenewalScope,
        IAutopilotLockService LockService,
        AutopilotLockLeaseHeartbeat? Heartbeat);

    private sealed class CompletionLaunchFenceRelease(
        IAutopilotLockService locks,
        AutopilotLockLeaseHeartbeat heartbeat,
        IServiceScope? renewalScope,
        ILogger logger,
        Action? onLeaseLost = null) : IAsyncDisposable
    {
        private bool? releaseResult;

        public async ValueTask<bool> ReleaseAsync()
        {
            if (releaseResult.HasValue) return releaseResult.Value;
            try
            {
                await heartbeat.StopRenewalLoopAsync();
                releaseResult = !heartbeat.IsLost
                    && await heartbeat.RenewNowAsync(CancellationToken.None)
                    && await locks.ReleaseAsync(heartbeat.CurrentHandle, CancellationToken.None);
                if (!releaseResult.Value)
                {
                    onLeaseLost?.Invoke();
                    logger.LogError("Autopilot completion/launch fence release lost ownership. Resource={ResourceKey}; FencingToken={FencingToken}",
                        heartbeat.CurrentHandle.ResourceKey, heartbeat.CurrentHandle.FencingToken);
                }
            }
            catch (Exception exception)
            {
                onLeaseLost?.Invoke();
                logger.LogError(exception, "Autopilot completion/launch fence release failed. Resource={ResourceKey}; FencingToken={FencingToken}",
                    heartbeat.CurrentHandle.ResourceKey, heartbeat.CurrentHandle.FencingToken);
                releaseResult = false;
            }

            return releaseResult.Value;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _ = await ReleaseAsync();
            }
            finally
            {
                try { await heartbeat.DisposeAsync(); }
                finally { renewalScope?.Dispose(); }
            }
        }
    }
}

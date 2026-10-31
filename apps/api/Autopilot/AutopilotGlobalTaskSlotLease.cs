using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>
/// One entry from the authoritative, project-scoped Manus task listing. ScopeId
/// is a stable Taslim ownership marker, not a user/API-key identity.
/// </summary>
public sealed record AutopilotGlobalTaskInventoryItem(
    string TaskId,
    string ScopeId,
    string Status,
    string? IdempotencyKey);

/// <summary>A single page from the external inventory endpoint.</summary>
public sealed record AutopilotGlobalTaskInventoryPage(
    string QueryScopeId,
    IReadOnlyList<AutopilotGlobalTaskInventoryItem>? Items,
    string? NextCursor);

/// <summary>Page-source contract; implementations must request the exact supplied scope.</summary>
public interface IAutopilotGlobalTaskInventoryPageSource
{
    Task<AutopilotGlobalTaskInventoryPage?> ReadPageAsync(
        string scopeId,
        string? cursor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A complete active-task snapshot. Partial pages are never represented as
/// authoritative and therefore cannot grant capacity.
/// </summary>
public sealed record AutopilotGlobalTaskInventorySnapshot(
    string ScopeId,
    bool IsAuthoritative,
    int PagesRead,
    bool HasMorePages,
    string? FailureCode,
    IReadOnlyList<AutopilotGlobalTaskInventoryItem> ActiveTasks)
{
    public static AutopilotGlobalTaskInventorySnapshot Unknown(string scopeId, string failureCode, int pagesRead = 0) =>
        new(scopeId, false, pagesRead, true, failureCode, Array.Empty<AutopilotGlobalTaskInventoryItem>());
}

/// <summary>
/// Reads every page of the configured Taslim scope. It rejects a failed,
/// repeated, truncated, mis-scoped, malformed, or status-ambiguous listing rather
/// than interpreting an unknown external occupancy as zero.
/// </summary>
public sealed class AutopilotGlobalTaskInventoryReader(
    IAutopilotGlobalTaskInventoryPageSource source,
    string taslimScopeId,
    int maxPages = 256)
{
    public const int MaximumPages = 256;

    public async Task<AutopilotGlobalTaskInventorySnapshot> ReadAuthoritativeSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taslimScopeId))
            return AutopilotGlobalTaskInventorySnapshot.Unknown(string.Empty, "inventory_scope_missing");

        var pageLimit = Math.Clamp(maxPages, 1, MaximumPages);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var byId = new Dictionary<string, AutopilotGlobalTaskInventoryItem>(StringComparer.Ordinal);
        string? cursor = null;
        var pagesRead = 0;

        for (var pageNumber = 0; pageNumber < pageLimit; pageNumber++)
        {
            AutopilotGlobalTaskInventoryPage? page;
            try
            {
                page = await source.ReadPageAsync(taslimScopeId, cursor, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_source_unavailable", pagesRead);
            }

            pagesRead++;
            if (page is null || page.Items is null)
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_page_invalid", pagesRead);
            if (!string.Equals(page.QueryScopeId, taslimScopeId, StringComparison.Ordinal))
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_scope_mismatch", pagesRead);

            foreach (var item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.TaskId) || string.IsNullOrWhiteSpace(item.ScopeId))
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_task_identity_or_scope_missing", pagesRead);

                // Other projects in a tenant-wide listing are deliberately ignored.
                if (!string.Equals(item.ScopeId, taslimScopeId, StringComparison.Ordinal))
                    continue;

                var normalizedStatus = NormalizeStatus(item.Status);
                if (normalizedStatus == InventoryStatus.Unknown)
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_task_status_unknown", pagesRead);
                if (normalizedStatus == InventoryStatus.Terminal)
                    continue;

                var taskId = item.TaskId.Trim();
                var idempotencyKey = string.IsNullOrWhiteSpace(item.IdempotencyKey) ? null : item.IdempotencyKey.Trim();
                if (idempotencyKey is { Length: > 200 })
                    return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_idempotency_key_invalid", pagesRead);

                var normalized = item with { TaskId = taskId, ScopeId = taslimScopeId, Status = item.Status.Trim().ToLowerInvariant(), IdempotencyKey = idempotencyKey };
                if (byId.TryGetValue(taskId, out var previous))
                {
                    if (!string.Equals(previous.ScopeId, normalized.ScopeId, StringComparison.Ordinal)
                        || !string.Equals(previous.Status, normalized.Status, StringComparison.Ordinal)
                        || !string.Equals(previous.IdempotencyKey, normalized.IdempotencyKey, StringComparison.Ordinal))
                        return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_duplicate_identity_conflict", pagesRead);
                    continue;
                }

                byId.Add(taskId, normalized);
            }

            if (page.NextCursor is null)
            {
                return new AutopilotGlobalTaskInventorySnapshot(
                    taslimScopeId,
                    true,
                    pagesRead,
                    false,
                    null,
                    byId.Values.OrderBy(item => item.TaskId, StringComparer.Ordinal).ToArray());
            }

            var nextCursor = page.NextCursor.Trim();
            if (nextCursor.Length == 0 || !seenCursors.Add(nextCursor) || string.Equals(cursor, nextCursor, StringComparison.Ordinal))
                return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_cursor_repeated_or_invalid", pagesRead);
            cursor = nextCursor;
        }

        return AutopilotGlobalTaskInventorySnapshot.Unknown(taslimScopeId, "inventory_page_limit_exceeded", pagesRead);
    }

    private enum InventoryStatus { Active, Terminal, Unknown }

    private static InventoryStatus NormalizeStatus(string? value)
    {
        var status = (value ?? string.Empty).Trim().ToLowerInvariant();
        return status switch
        {
            "queued" or "pending" or "created" or "accepted" or "scheduled"
                or "running" or "in_progress" or "in-progress" or "processing" or "started" or "active"
                or "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human"
                => InventoryStatus.Active,
            "completed" or "complete" or "succeeded" or "success" or "done" or "finished"
                or "failed" or "failure" or "error" or "errored" or "timed_out" or "timeout"
                or "cancelled" or "canceled" or "stopped" or "aborted" or "killed" or "terminated"
                => InventoryStatus.Terminal,
            _ => InventoryStatus.Unknown,
        };
    }
}

public static class AutopilotGlobalTaskSlotOutcomes
{
    public const string Reserved = "reserved";
    public const string CapacityReached = "capacity_reached";
    public const string InventoryUnknown = "inventory_unknown";
    public const string AlreadyActive = "already_active";
    public const string Completed = "completed";
    public const string DuplicateCompletion = "duplicate_completion";
    public const string LaunchInProgress = "launch_in_progress";
    public const string AmbiguousLaunch = "ambiguous_launch";
    public const string LaunchRejected = "launch_rejected";
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string InvalidTransition = "invalid_transition";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string ReservationUnavailable = "reservation_unavailable";
}

/// <summary>Result of an atomic slot or lifecycle transition.</summary>
public sealed record AutopilotGlobalTaskSlotResult(
    string Outcome,
    bool SlotHeld,
    bool MayCallProvider,
    int ActiveSlotsObserved,
    string? ExternalTaskId = null,
    string? ReasonCode = null);

/// <summary>
/// Atomically claims and maintains the single global Taslim task cap across all
/// waves and API processes. It uses the already-durable AutopilotWaveLaunchTask
/// row as the reservation: Launching and Launched hold one slot; Completed and a
/// deterministic provider rejection release it. PostgreSQL transaction advisory
/// locking serializes independent processes; serializable transactions are also
/// used for relational providers and SQLite tests.
///
/// This component must be placed around every Taslim Manus launch path to enforce
/// the cap. Until the launch service and completion/reconciliation call sites are
/// wired through it, it is a tested component, not an effective production limit.
/// </summary>
public sealed class AutopilotGlobalTaskSlotAllocator
{
    public const int HardMaximum = 20;
    private const string SlotReservedReason = "global_slot_reserved";
    private const int AdvisoryLockNamespace = 1_413_560_652; // ASCII "TASL"
    private const int AdvisoryLockResource = 20;
    private const int MaxRowsExamined = 10_000;

    // Protects a single scoped allocator/DbContext. Independent instances are
    // serialized by the relational transaction and PostgreSQL advisory lock.
    private readonly SemaphoreSlim LocalReservationGate = new(1, 1);

    private readonly TaslimDbContext db;
    private readonly TimeProvider clock;
    private readonly string scopeId;
    private readonly int capacity;

    public AutopilotGlobalTaskSlotAllocator(
        TaslimDbContext db,
        string taslimScopeId,
        TimeProvider? timeProvider = null,
        int maxConcurrentTasks = HardMaximum)
    {
        this.db = db;
        scopeId = string.IsNullOrWhiteSpace(taslimScopeId)
            ? throw new ArgumentException("A stable Taslim inventory scope is required.", nameof(taslimScopeId))
            : taslimScopeId.Trim();
        if (maxConcurrentTasks < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentTasks));
        capacity = Math.Min(maxConcurrentTasks, HardMaximum);
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Reserves a slot before the external provider call. Stable idempotency keys
    /// map to the existing row's wave/task identity. An expired ambiguous launch
    /// is retried only when the caller confirms the bridge enforces that key.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> ReserveOrRecoverLaunchAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        AutopilotGlobalTaskInventorySnapshot inventory,
        TimeSpan launchLease,
        bool providerGuaranteesIdempotency,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthoritative(inventory))
            return new(AutopilotGlobalTaskSlotOutcomes.InventoryUnknown, false, false, 0, ReasonCode: inventory?.FailureCode ?? "inventory_unknown");

        if (launchLease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(launchLease));
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            if (!db.Database.IsRelational())
                return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "relational_store_required");

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireCrossProcessFenceAsync(cancellationToken);

            var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
            if (task is null)
                return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "launch_task_missing");

            var expectedKey = AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey);
            if (!string.Equals(expectedKey, idempotencyKey, StringComparison.Ordinal))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

            var duplicateDatabaseIdentity = await db.AutopilotWaveLaunchTasks.AsNoTracking()
                .AnyAsync(item => item.Id != task.Id && item.WaveKey == task.WaveKey && item.TaskKey == task.TaskKey, cancellationToken);
            if (duplicateDatabaseIdentity)
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "duplicate_wave_task_identity");

            var inventoryMatches = inventory.ActiveTasks
                .Where(item => string.Equals(item.IdempotencyKey, expectedKey, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(task.ExternalTaskId)
                        && string.Equals(item.TaskId, task.ExternalTaskId, StringComparison.Ordinal)))
                .ToList();
            if (inventoryMatches.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).Count() > 1)
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, true, false, inventory.ActiveTasks.Count, ReasonCode: "multiple_external_tasks_for_idempotency_key");

            if (string.Equals(task.Status, AutopilotWaveLaunchStatuses.Completed, StringComparison.OrdinalIgnoreCase))
                return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);

            if (inventoryMatches.Count == 1)
            {
                if (!string.IsNullOrWhiteSpace(task.ExternalTaskId)
                    && !string.Equals(task.ExternalTaskId, inventoryMatches[0].TaskId, StringComparison.Ordinal))
                    return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, true, false, inventory.ActiveTasks.Count,
                        task.ExternalTaskId, "provider_idempotency_key_returned_different_task_id");

                task.ExternalTaskId = inventoryMatches[0].TaskId;
                task.Status = AutopilotWaveLaunchStatuses.Launched;
                task.ReasonCode = "recovered_from_authoritative_inventory";
                task.LastStatusDetail = "existing_taslim_task_reused; no_duplicate_launch";
                task.LaunchedAt ??= UtcNow();
                task.UpdatedAt = UtcNow();
                task.ConcurrencyToken = Guid.NewGuid();
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);
            }

            if (string.Equals(task.Status, AutopilotWaveLaunchStatuses.Launched, StringComparison.OrdinalIgnoreCase))
                return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);

            // The current controller claims a durable row immediately before it
            // calls IWaveLaunchProvider. Treat that fresh, unmarked claim as a
            // candidate, then atomically convert it into the global slot lease.
            // Only this marker (or Launched) counts as a local reservation.
            if (task.Status == AutopilotWaveLaunchStatuses.Launching && !IsGlobalSlotReservation(task))
            {
                var staleUnreservedClaim = UtcNow() - task.UpdatedAt >= launchLease;
                var occupiedBeforeClaim = await CountOccupiedSlotsAsync(inventory, cancellationToken);
                if (occupiedBeforeClaim >= capacity)
                    return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, false, false, occupiedBeforeClaim, ReasonCode: "global_taslim_slot_cap_reached");

                if (staleUnreservedClaim && (!providerGuaranteesIdempotency || task.LaunchAttempt >= task.MaxLaunchAttempts))
                {
                    // The prior provider call may have been accepted but the
                    // inventory may lag. Reserve conservatively and do not call
                    // the provider until idempotency is verified by the caller.
                    task.ReasonCode = SlotReservedReason;
                    task.LastStatusDetail = "stale_preallocator_claim_held_for_reconciliation";
                    task.ConcurrencyToken = Guid.NewGuid();
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(task.LaunchAttempt >= task.MaxLaunchAttempts
                            ? AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted
                            : AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch,
                        true, false, occupiedBeforeClaim + 1, task.ExternalTaskId, "stale_claim_requires_idempotent_reconciliation");
                }

                task.ReasonCode = SlotReservedReason;
                task.LastStatusDetail = staleUnreservedClaim
                    ? "stale_claim_retried_with_same_idempotency_key"
                    : $"global_slot_claimed:{occupiedBeforeClaim + 1}/{capacity}";
                if (staleUnreservedClaim)
                {
                    task.LaunchAttempt++;
                    task.UpdatedAt = UtcNow();
                }
                task.ConcurrencyToken = Guid.NewGuid();
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, occupiedBeforeClaim + 1,
                    ReasonCode: staleUnreservedClaim ? "stale_launch_retried_idempotently" : null);
            }

            if (task.Status == AutopilotWaveLaunchStatuses.Launching && IsGlobalSlotReservation(task))
            {
                if (UtcNow() - task.UpdatedAt < launchLease)
                    return new(AutopilotGlobalTaskSlotOutcomes.LaunchInProgress, true, false, inventory.ActiveTasks.Count, task.ExternalTaskId);

                if (!providerGuaranteesIdempotency)
                    return new(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, true, false, inventory.ActiveTasks.Count, ReasonCode: "expired_claim_but_provider_idempotency_unverified");

                if (task.LaunchAttempt >= task.MaxLaunchAttempts)
                    return new(AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted, true, false, inventory.ActiveTasks.Count, ReasonCode: "ambiguous_retry_bound_exhausted");

                var observed = await CountOccupiedSlotsAsync(inventory, cancellationToken);
                if (observed > capacity)
                    return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, true, false, observed, task.ExternalTaskId, "global_taslim_slot_cap_already_exceeded");
                task.LaunchAttempt++;
                task.UpdatedAt = UtcNow();
                task.ReasonCode = SlotReservedReason;
                task.LastStatusDetail = "slot_retained; provider_retry_requires_idempotency";
                task.ConcurrencyToken = Guid.NewGuid();
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, observed, ReasonCode: "stale_launch_retried_idempotently");
            }

            if (task.Status != AutopilotWaveLaunchStatuses.Planned && task.Status != AutopilotWaveLaunchStatuses.Failed)
                return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, inventory.ActiveTasks.Count, task.ExternalTaskId, "task_not_launchable");

            if (task.LaunchAttempt >= task.MaxLaunchAttempts)
                return new(AutopilotGlobalTaskSlotOutcomes.AttemptsExhausted, false, false, inventory.ActiveTasks.Count, ReasonCode: "launch_attempt_bound_exhausted");

            var occupied = await CountOccupiedSlotsAsync(inventory, cancellationToken);
            if (occupied >= capacity)
                return new(AutopilotGlobalTaskSlotOutcomes.CapacityReached, false, false, occupied, ReasonCode: "global_taslim_slot_cap_reached");

            task.Status = AutopilotWaveLaunchStatuses.Launching;
            task.LaunchAttempt++;
            task.UpdatedAt = UtcNow();
            task.CompletedAt = null;
            task.ReasonCode = SlotReservedReason;
            task.LastStatusDetail = $"global_slot_claimed:{occupied + 1}/{capacity}";
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AutopilotGlobalTaskSlotOutcomes.Reserved, true, true, occupied + 1);
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>Persists provider acceptance without creating a second reservation.</summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordAcceptedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string externalTaskId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalTaskId) || externalTaskId.Trim().Length > 200)
            return new(AutopilotGlobalTaskSlotOutcomes.ReservationUnavailable, false, false, 0, ReasonCode: "external_task_id_invalid");

        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireCrossProcessFenceAsync(cancellationToken);
            var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
            if (task is null || !MatchesKey(task, idempotencyKey))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

            if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);
            if (task.Status == AutopilotWaveLaunchStatuses.Launched)
            {
                var sameTask = string.Equals(task.ExternalTaskId, externalTaskId.Trim(), StringComparison.Ordinal);
                return new(sameTask ? AutopilotGlobalTaskSlotOutcomes.AlreadyActive : AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict,
                    true, false, 0, task.ExternalTaskId, sameTask ? null : "provider_returned_conflicting_task_id");
            }
            if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "launch_claim_not_held");

            task.ExternalTaskId = externalTaskId.Trim();
            task.Status = AutopilotWaveLaunchStatuses.Launched;
            task.LaunchedAt ??= UtcNow();
            task.UpdatedAt = UtcNow();
            task.ReasonCode = "launched";
            task.LastStatusDetail = "external_task_identity_persisted";
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, 0, task.ExternalTaskId);
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>
    /// A deterministic provider rejection proves no external task was accepted,
    /// so the durable reservation can be released. Network timeouts are not
    /// deterministic and must use RecordAmbiguousAsync instead.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordRejectedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireCrossProcessFenceAsync(cancellationToken);
            var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
            if (task is null || !MatchesKey(task, idempotencyKey))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");
            if (task.Status == AutopilotWaveLaunchStatuses.Failed)
                return new(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, false, false, 0, task.ExternalTaskId, task.ReasonCode);
            if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, task.Status == AutopilotWaveLaunchStatuses.Launched, false, 0, task.ExternalTaskId, "launch_claim_not_held");

            task.Status = AutopilotWaveLaunchStatuses.Failed;
            task.ReasonCode = SafeCode(reasonCode, "provider_rejected");
            task.LastStatusDetail = "provider_rejected_before_task_acceptance";
            task.CompletedAt = UtcNow();
            task.UpdatedAt = UtcNow();
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AutopilotGlobalTaskSlotOutcomes.LaunchRejected, false, false, 0, task.ExternalTaskId, task.ReasonCode);
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>Retains and refreshes the slot after an ambiguous provider timeout.</summary>
    public async Task<AutopilotGlobalTaskSlotResult> RecordAmbiguousAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string reasonCode = "provider_outcome_unknown",
        CancellationToken cancellationToken = default)
    {
        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireCrossProcessFenceAsync(cancellationToken);
            var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
            if (task is null || !MatchesKey(task, idempotencyKey))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");
            if (task.Status == AutopilotWaveLaunchStatuses.Launched)
                return new(AutopilotGlobalTaskSlotOutcomes.AlreadyActive, true, false, 0, task.ExternalTaskId);
            if (task.Status != AutopilotWaveLaunchStatuses.Launching)
                return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "launch_claim_not_held");

            task.ReasonCode = SlotReservedReason;
            task.LastStatusDetail = $"slot_retained_until_authoritative_reconciliation:{SafeCode(reasonCode, "provider_outcome_unknown")}";
            task.UpdatedAt = UtcNow();
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AutopilotGlobalTaskSlotOutcomes.AmbiguousLaunch, true, false, 0, task.ExternalTaskId, task.ReasonCode);
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    /// <summary>
    /// Releases exactly one durable slot only after the caller has verified
    /// terminal evidence (for example, an authenticated signed completion event).
    /// Replays with the same task identity are harmless.
    /// </summary>
    public async Task<AutopilotGlobalTaskSlotResult> CompleteVerifiedAsync(
        Guid launchTaskRowId,
        string idempotencyKey,
        string externalTaskId,
        bool terminalEvidenceVerified,
        CancellationToken cancellationToken = default)
    {
        if (!terminalEvidenceVerified)
            return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, ReasonCode: "terminal_evidence_not_verified");
        if (string.IsNullOrWhiteSpace(externalTaskId) || externalTaskId.Trim().Length > 200)
            return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, ReasonCode: "external_task_id_invalid");

        await LocalReservationGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await AcquireCrossProcessFenceAsync(cancellationToken);
            var task = await db.AutopilotWaveLaunchTasks.SingleOrDefaultAsync(item => item.Id == launchTaskRowId, cancellationToken);
            if (task is null || !MatchesKey(task, idempotencyKey))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, false, false, 0, ReasonCode: "launch_identity_mismatch");

            var externalId = externalTaskId.Trim();
            if (task.ExternalTaskId is not null && !string.Equals(task.ExternalTaskId, externalId, StringComparison.Ordinal))
                return new(AutopilotGlobalTaskSlotOutcomes.IdempotencyConflict, task.Status is AutopilotWaveLaunchStatuses.Launching or AutopilotWaveLaunchStatuses.Launched,
                    false, 0, task.ExternalTaskId, "terminal_event_task_identity_mismatch");
            if (task.Status == AutopilotWaveLaunchStatuses.Completed)
                return new(AutopilotGlobalTaskSlotOutcomes.DuplicateCompletion, false, false, 0, task.ExternalTaskId);
            if (task.Status != AutopilotWaveLaunchStatuses.Launching && task.Status != AutopilotWaveLaunchStatuses.Launched)
                return new(AutopilotGlobalTaskSlotOutcomes.InvalidTransition, false, false, 0, task.ExternalTaskId, "task_has_no_reserved_slot");

            task.ExternalTaskId = externalId;
            task.Status = AutopilotWaveLaunchStatuses.Completed;
            task.CompletedAt = UtcNow();
            task.UpdatedAt = UtcNow();
            task.ReasonCode = "verified_terminal_evidence";
            task.LastStatusDetail = "slot_released_once";
            task.ConcurrencyToken = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AutopilotGlobalTaskSlotOutcomes.Completed, false, false, 0, task.ExternalTaskId);
        }
        finally
        {
            LocalReservationGate.Release();
        }
    }

    private async Task<int> CountOccupiedSlotsAsync(
        AutopilotGlobalTaskInventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        var externalIds = inventory.ActiveTasks.Select(item => item.TaskId).Distinct(StringComparer.Ordinal).ToArray();
        var recentlyCompletedIds = externalIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.AutopilotWaveLaunchTasks.AsNoTracking()
                .Where(item => item.Status == AutopilotWaveLaunchStatuses.Completed
                    && item.ExternalTaskId != null
                    && externalIds.Contains(item.ExternalTaskId))
                .Select(item => item.ExternalTaskId!)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var externalActive = inventory.ActiveTasks
            .Where(item => !recentlyCompletedIds.Contains(item.TaskId))
            .ToList();
        var externalActiveIds = externalActive.Select(item => item.TaskId).ToHashSet(StringComparer.Ordinal);
        var externalActiveKeys = externalActive
            .Where(item => !string.IsNullOrWhiteSpace(item.IdempotencyKey))
            .Select(item => item.IdempotencyKey!)
            .ToHashSet(StringComparer.Ordinal);

        var localActive = await db.AutopilotWaveLaunchTasks.AsNoTracking()
            .Where(item => item.Status == AutopilotWaveLaunchStatuses.Launched
                || (item.Status == AutopilotWaveLaunchStatuses.Launching && item.ReasonCode == SlotReservedReason))
            .Take(MaxRowsExamined + 1)
            .ToListAsync(cancellationToken);
        if (localActive.Count > MaxRowsExamined)
            return HardMaximum;

        var occupied = new HashSet<string>(externalActiveIds.Select(id => $"external:{id}"), StringComparer.Ordinal);
        foreach (var reservation in localActive)
        {
            if (!string.IsNullOrWhiteSpace(reservation.ExternalTaskId)
                && externalActiveIds.Contains(reservation.ExternalTaskId))
                continue;

            var key = AutopilotNextWavePolicy.LaunchIdempotencyKey(reservation.WaveKey, reservation.TaskKey);
            if (string.IsNullOrWhiteSpace(reservation.ExternalTaskId) && externalActiveKeys.Contains(key))
                continue;

            occupied.Add(string.IsNullOrWhiteSpace(reservation.ExternalTaskId)
                ? $"reservation:{reservation.Id:D}"
                : $"database-task:{reservation.ExternalTaskId}");
        }

        return occupied.Count;
    }

    private async Task AcquireCrossProcessFenceAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            return;

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, {AdvisoryLockResource})";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private bool IsAuthoritative(AutopilotGlobalTaskInventorySnapshot? inventory)
    {
        if (inventory is null
            || !inventory.IsAuthoritative
            || inventory.PagesRead < 1
            || inventory.HasMorePages
            || !string.IsNullOrWhiteSpace(inventory.FailureCode)
            || !string.Equals(inventory.ScopeId, scopeId, StringComparison.Ordinal)
            || inventory.ActiveTasks is null)
            return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in inventory.ActiveTasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskId)
                || !string.Equals(task.ScopeId, scopeId, StringComparison.Ordinal)
                || !IsKnownActiveStatus(task.Status)
                || !ids.Add(task.TaskId))
                return false;
            if (task.IdempotencyKey is { Length: > 200 }) return false;
        }
        return true;
    }

    private bool MatchesKey(AutopilotWaveLaunchTask task, string idempotencyKey) =>
        string.Equals(AutopilotNextWavePolicy.LaunchIdempotencyKey(task.WaveKey, task.TaskKey), idempotencyKey, StringComparison.Ordinal);

    private static bool IsGlobalSlotReservation(AutopilotWaveLaunchTask task) =>
        task.Status == AutopilotWaveLaunchStatuses.Launched
        || (task.Status == AutopilotWaveLaunchStatuses.Launching && task.ReasonCode == SlotReservedReason);

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    private static bool IsKnownActiveStatus(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "queued" or "pending" or "created" or "accepted" or "scheduled" or "running"
            or "in_progress" or "in-progress" or "processing" or "started" or "active"
            or "blocked" or "awaiting_input" or "awaiting_approval" or "needs_input" or "requires_human" => true,
        _ => false,
    };

    private static string SafeCode(string? value, string fallback)
    {
        var normalized = new string((value ?? string.Empty).Trim().ToLowerInvariant()
            .Take(120).Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
        return normalized.Length == 0 ? fallback : normalized;
    }
}

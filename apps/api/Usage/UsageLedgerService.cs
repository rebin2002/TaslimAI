using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public interface IUsageChargingService
{
    decimal CalculateCustomerCharge(UsageTransaction transaction);
}

public sealed class SafeUsageChargingService : IUsageChargingService
{
    public decimal CalculateCustomerCharge(UsageTransaction transaction) => 0m;
}

public interface IUsageCostControl
{
    Task<UsagePreflightResult> CheckPreflightAsync(
        Guid workspaceId,
        UsageFeature feature,
        decimal? estimatedProviderCostUsd,
        CancellationToken cancellationToken = default);

    Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default);
}

public sealed class UsageCostControl(
    TaslimDbContext db,
    Microsoft.Extensions.Options.IOptions<UsageControlOptions> options,
    ILogger<UsageCostControl> logger) : IUsageCostControl
{
    private readonly UsageControlOptions settings = options.Value;

    public async Task<UsagePreflightResult> CheckPreflightAsync(
        Guid workspaceId,
        UsageFeature feature,
        decimal? estimatedProviderCostUsd,
        CancellationToken cancellationToken = default)
    {
        var estimate = estimatedProviderCostUsd.HasValue
            ? decimal.Round(Math.Max(0m, estimatedProviderCostUsd.Value), 8, MidpointRounding.AwayFromZero)
            : (decimal?)null;
        if (!settings.GuardrailsEnabled) return new UsagePreflightResult(true, estimate);
        if (!estimate.HasValue && settings.RejectUnknownEstimates)
            return new UsagePreflightResult(false, null, "COST_ESTIMATE_UNKNOWN", "The provider cost could not be estimated safely.");

        if (settings.MaxEstimatedProviderCostPerGenerationUsd is { } single && estimate > single)
            return new UsagePreflightResult(false, estimate, "COST_ESTIMATE_EXCEEDS_LIMIT", "This operation exceeds the configured safety limit.");

        var now = DateTime.UtcNow;
        if (settings.DailyWorkspaceProviderCostCeilingUsd is { } daily)
        {
            var start = now.Date;
            var (spent, hasUnknown) = await ProviderCostForWindowAsync(workspaceId, start, now, cancellationToken);
            if (hasUnknown)
                return new UsagePreflightResult(false, estimate, "WORKSPACE_COST_UNKNOWN", "This workspace has provider exposure that cannot be estimated safely.");
            if (spent + estimate.GetValueOrDefault() > daily)
                return new UsagePreflightResult(false, estimate, "DAILY_COST_CEILING_EXCEEDED", "This workspace has reached its configured daily safety limit.");
        }

        if (settings.MonthlyWorkspaceProviderCostCeilingUsd is { } monthly)
        {
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var (spent, hasUnknown) = await ProviderCostForWindowAsync(workspaceId, start, now, cancellationToken);
            if (hasUnknown)
                return new UsagePreflightResult(false, estimate, "WORKSPACE_COST_UNKNOWN", "This workspace has provider exposure that cannot be estimated safely.");
            if (spent + estimate.GetValueOrDefault() > monthly)
                return new UsagePreflightResult(false, estimate, "MONTHLY_COST_CEILING_EXCEEDED", "This workspace has reached its configured monthly safety limit.");
        }

        return new UsagePreflightResult(true, estimate);
    }

    public async Task MarkAnomalyAsync(UsageTransaction transaction, CancellationToken cancellationToken = default)
    {
        var code = await FindAnomalyCodeAsync(transaction, cancellationToken);
        if (code is null) return;
        transaction.IsAnomalous = true;
        transaction.AnomalyCode = code;
        transaction.AnomalyDetectedAt ??= DateTime.UtcNow;
        logger.LogWarning(
            "Usage anomaly flagged. TransactionId={TransactionId}; WorkspaceId={WorkspaceId}; Feature={Feature}; AnomalyCode={AnomalyCode}; ProviderCostUsd={ProviderCostUsd}",
            transaction.Id, transaction.WorkspaceId, transaction.Feature, code, transaction.ProviderCostUsd);
    }

    private async Task<(decimal Cost, bool HasUnknown)> ProviderCostForWindowAsync(Guid workspaceId, DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var rows = await db.UsageTransactions
            .Where(item => item.WorkspaceId == workspaceId && item.CreatedAt >= start && item.CreatedAt < end)
            .Select(item => new { item.Status, item.ProviderCostUsd, item.ProviderCostKnown, item.EstimatedProviderCostUsd })
            .ToListAsync(cancellationToken);
        var hasUnknown = rows.Any(item => item.Status != UsageTransactionStatus.Cancelled
            && (item.Status == UsageTransactionStatus.Pending ? !item.EstimatedProviderCostUsd.HasValue : !item.ProviderCostKnown));
        var cost = rows.Sum(item => item.Status == UsageTransactionStatus.Completed || item.Status == UsageTransactionStatus.Failed
            ? item.ProviderCostKnown ? item.ProviderCostUsd : 0m
            : item.Status == UsageTransactionStatus.Pending
                ? item.EstimatedProviderCostUsd ?? 0m
                : 0m);
        return (cost, hasUnknown);
    }

    private async Task<string?> FindAnomalyCodeAsync(UsageTransaction transaction, CancellationToken cancellationToken)
    {
        if (settings.SingleTransactionAnomalyThresholdUsd is { } single && transaction.ProviderCostUsd > single)
            return "SINGLE_TRANSACTION_COST_THRESHOLD";

        if (settings.DailyWorkspaceAnomalyThresholdUsd is { } daily)
        {
            var start = transaction.CreatedAt.Date;
            var (spent, hasUnknown) = await ProviderCostForWindowAsync(transaction.WorkspaceId, start, DateTime.UtcNow, cancellationToken);
            if (hasUnknown) return "PROVIDER_COST_UNKNOWN";
            if (spent > daily) return "DAILY_WORKSPACE_COST_THRESHOLD";
        }

        if (settings.RepeatedFailedOperationsThreshold is { } failures && failures > 0)
        {
            var start = DateTime.UtcNow.AddMinutes(-Math.Abs(settings.RepeatedFailedOperationsWindowMinutes));
            var count = await db.UsageTransactions.CountAsync(item =>
                item.WorkspaceId == transaction.WorkspaceId &&
                item.Status == UsageTransactionStatus.Failed &&
                item.CreatedAt >= start, cancellationToken);
            if (count >= failures) return "REPEATED_FAILED_OPERATIONS";
        }

        return null;
    }
}

public sealed record UsageAdjustmentResult(UsageTransactionAdjustment Adjustment, bool Created);

public interface IUsageLedgerService
{
    Task<UsageTransaction> GetOrCreatePendingAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        Guid? conversationId,
        string requestId,
        UsageFeature feature,
        CancellationToken cancellationToken = default,
        Guid? generationJobId = null,
        decimal? estimatedProviderCostUsd = null,
        string? costEstimateJson = null);

    Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default);
    Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default);
    Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default);
    Task<UsageAdjustmentResult?> RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default);
    Task<UsageAdjustmentResult?> ReverseAsync(UsageTransaction transaction, string idempotencyKey, string reason, CancellationToken cancellationToken = default);
}

public sealed class UsageLedgerService(
    TaslimDbContext db,
    IAiCostCalculator costCalculator,
    IUsageChargingService chargingService,
    IUsageCostControl costControl,
    ILogger<UsageLedgerService> logger) : IUsageLedgerService
{
    public async Task<UsageTransaction> GetOrCreatePendingAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        Guid? conversationId,
        string requestId,
        UsageFeature feature,
        CancellationToken cancellationToken = default,
        Guid? generationJobId = null,
        decimal? estimatedProviderCostUsd = null,
        string? costEstimateJson = null)
    {
        var existing = await db.UsageTransactions.SingleOrDefaultAsync(transaction =>
            transaction.WorkspaceId == workspaceId && transaction.RequestId == requestId && transaction.Feature == feature,
            cancellationToken);
        if (existing is not null)
        {
            // A generation transaction is permanently tied to its job idempotency key.
            // Never reopen a terminal generation after a retry or duplicate worker delivery.
            if (existing.GenerationJobId.HasValue && existing.Status is (UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled or UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded))
                return existing;
            if (existing.Status is UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled)
            {
                existing.Status = UsageTransactionStatus.Pending;
                existing.ProviderCostUsd = 0m;
                existing.ProviderCostKnown = false;
                existing.ChargedAmount = 0m;
                existing.ReversedAmount = 0m;
                existing.IsBillable = false;
                existing.EstimatedProviderCostUsd = estimatedProviderCostUsd;
                existing.CostEstimateJson = costEstimateJson;
                existing.ReservedAt = DateTime.UtcNow;
                existing.ActualRecordedAt = null;
                existing.BillableAt = null;
                existing.CompletedAt = null;
                existing.RefundedAt = null;
                existing.FailureCode = null;
                existing.CostBasis = null;
                existing.PricingVersion = null;
                existing.PricingSnapshotJson = null;
                existing.IsAnomalous = false;
                existing.AnomalyCode = null;
                existing.AnomalyDetectedAt = null;
                existing.Provider = "pending";
                existing.Model = "pending";
                await db.SaveChangesAsync(cancellationToken);
            }
            return existing;
        }

        var transaction = new UsageTransaction
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            ProjectId = projectId,
            ConversationId = conversationId,
            GenerationJobId = generationJobId,
            RequestId = requestId,
            Feature = feature,
            Provider = "pending",
            Model = "pending",
            Status = UsageTransactionStatus.Pending,
            EstimatedProviderCostUsd = estimatedProviderCostUsd,
            CostEstimateJson = costEstimateJson,
            ProviderCostUsd = 0m,
            ProviderCostKnown = false,
            ChargedAmount = 0m,
            ReversedAmount = 0m,
            IsBillable = false,
            ChargedUnit = UsageChargeUnit.Usd,
            Currency = UsageCurrencies.Usd,
            CreatedAt = DateTime.UtcNow,
            ReservedAt = DateTime.UtcNow,
        };
        db.UsageTransactions.Add(transaction);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return transaction;
        }
        catch (DbUpdateException)
        {
            db.Entry(transaction).State = EntityState.Detached;
            var concurrent = await db.UsageTransactions.SingleAsync(item =>
                item.WorkspaceId == workspaceId && item.RequestId == requestId && item.Feature == feature,
                cancellationToken);
            logger.LogInformation("Usage transaction already exists for idempotent request. WorkspaceId={WorkspaceId}; Feature={Feature}", workspaceId, feature);
            return concurrent;
        }
    }

    public async Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default)
    {
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded or UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled) return;
        var now = DateTime.UtcNow;
        var providerCost = costCalculator.Calculate(usage);
        var snapshot = string.IsNullOrWhiteSpace(usage.PricingSnapshotJson) ? costCalculator.GetPricingSnapshot(usage)?.ToJson() : usage.PricingSnapshotJson;
        transaction.Provider = usage.ProviderKey;
        transaction.Model = usage.ModelKey;
        transaction.Status = UsageTransactionStatus.Completed;
        transaction.InputTokens = usage.InputTokens;
        transaction.CachedInputTokens = usage.CachedInputTokens;
        transaction.OutputTokens = usage.OutputTokens;
        transaction.ImageInputTokens = usage.ImageInputTokens;
        transaction.ImageOutputTokens = usage.ImageOutputTokens;
        transaction.LatencyMs = usage.LatencyMs;
        transaction.ProviderCostUsd = providerCost ?? 0m;
        transaction.ProviderCostKnown = providerCost.HasValue;
        transaction.ChargedAmount = chargingService.CalculateCustomerCharge(transaction);
        transaction.ReversedAmount = 0m;
        transaction.IsBillable = true;
        transaction.Currency = string.IsNullOrWhiteSpace(usage.Currency) ? transaction.Currency : usage.Currency.Trim().ToUpperInvariant();
        transaction.CostBasis = string.IsNullOrWhiteSpace(usage.CostBasis)
            ? usage.ActualCost.HasValue ? UsageCostBasis.Actual : providerCost.HasValue ? UsageCostBasis.Estimated : UsageCostBasis.Unknown
            : usage.CostBasis;
        transaction.PricingVersion = string.IsNullOrWhiteSpace(usage.PricingVersion) ? costCalculator.GetPricingSnapshot(usage)?.Version : usage.PricingVersion;
        transaction.PricingSnapshotJson = snapshot;
        transaction.CostEstimateJson = usage.EstimatedCost.HasValue && string.IsNullOrWhiteSpace(transaction.CostEstimateJson)
            ? JsonSerializer.Serialize(new { amountUsd = usage.EstimatedCost, pricingVersion = usage.PricingVersion })
            : transaction.CostEstimateJson;
        transaction.SafeMetadataJson = usage.SafeMetadataJson;
        transaction.ActualRecordedAt = now;
        transaction.BillableAt = now;
        transaction.CompletedAt = now;
        transaction.FailureCode = null;
        await costControl.MarkAnomalyAsync(transaction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction completed. TransactionId={TransactionId}; Feature={Feature}; ProviderCostUsd={ProviderCostUsd}; PricingVersion={PricingVersion}", transaction.Id, transaction.Feature, transaction.ProviderCostUsd, transaction.PricingVersion ?? "none");
    }

    public async Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default)
    {
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded or UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled) return;
        var now = DateTime.UtcNow;
        transaction.Status = UsageTransactionStatus.Failed;
        var providerCost = usage is null ? null : costCalculator.Calculate(usage);
        transaction.ProviderCostUsd = providerCost ?? 0m;
        transaction.ProviderCostKnown = providerCost.HasValue;
        transaction.ChargedAmount = 0m;
        transaction.ReversedAmount = 0m;
        transaction.IsBillable = false;
        transaction.BillableAt = null;
        transaction.InputTokens = usage?.InputTokens;
        transaction.CachedInputTokens = usage?.CachedInputTokens;
        transaction.OutputTokens = usage?.OutputTokens;
        transaction.ImageInputTokens = usage?.ImageInputTokens;
        transaction.ImageOutputTokens = usage?.ImageOutputTokens;
        transaction.LatencyMs = usage?.LatencyMs;
        transaction.Provider = usage?.ProviderKey ?? transaction.Provider;
        transaction.Model = usage?.ModelKey ?? transaction.Model;
        transaction.Currency = string.IsNullOrWhiteSpace(usage?.Currency) ? transaction.Currency : usage.Currency.Trim().ToUpperInvariant();
        transaction.CostBasis = string.IsNullOrWhiteSpace(usage?.CostBasis)
            ? usage?.ActualCost.HasValue == true ? UsageCostBasis.Actual : providerCost.HasValue ? UsageCostBasis.Estimated : UsageCostBasis.Unknown
            : usage.CostBasis;
        transaction.PricingVersion = string.IsNullOrWhiteSpace(usage?.PricingVersion) ? transaction.PricingVersion : usage.PricingVersion;
        transaction.PricingSnapshotJson = string.IsNullOrWhiteSpace(usage?.PricingSnapshotJson) ? transaction.PricingSnapshotJson : usage.PricingSnapshotJson;
        transaction.SafeMetadataJson = usage?.SafeMetadataJson ?? transaction.SafeMetadataJson;
        transaction.FailureCode = failureCode;
        transaction.ActualRecordedAt = usage is null ? null : now;
        transaction.CompletedAt = null;
        transaction.RefundedAt = null;
        await costControl.MarkAnomalyAsync(transaction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction failed. TransactionId={TransactionId}; Feature={Feature}; FailureCode={FailureCode}; ProviderCostUsd={ProviderCostUsd}", transaction.Id, transaction.Feature, failureCode, transaction.ProviderCostUsd);
    }

    public async Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default)
    {
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded or UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled) return;
        // The guard above reads this context's copy, which a worker may have loaded at
        // reservation time and held across the whole provider window. While that copy says
        // Pending, the charge leg and the refund leg can both have committed elsewhere, and
        // assigning the cancellation onto the stale copy lets Entity Framework write back
        // exactly the columns that differ from it - Status and FailureCode - restating a
        // committed Refunded row as Cancelled while leaving the durable ChargedAmount,
        // ReversedAmount, RefundedAt and the paid-out Refund adjustment untouched. That is the
        // asymmetry PR #1077 closed for GenerationProviderAttempt with an atomic
        // UPDATE ... WHERE Status = Started: the attempt row got a compare-and-set, the
        // charge/refund leg it fences did not.
        // Cancel is now the same shape: one durable compare-and-set that lands only while the
        // row in the database still carries the status this context read. On PostgreSQL the
        // losing finalizer blocks on the row lock, re-evaluates the predicate against the
        // committed row and matches zero rows; on SQLite it simply matches zero rows.
        // Zero rows is fail-closed: the cancellation is not applied, the durable terminal
        // outcome is adopted instead, so a Cancelled row can never disagree with its ledger.
        var expectedStatus = transaction.Status;
        if (db.Entry(transaction).State == EntityState.Added)
        {
            // A reservation this context has not saved yet has no durable row to race, so there
            // is nothing to guard: stage the cancellation on the pending insert as before.
            ApplyCancellation(transaction, cancellationCode);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Usage transaction cancelled before its reservation was saved. TransactionId={TransactionId}; Feature={Feature}; FailureCode={FailureCode}", transaction.Id, transaction.Feature, cancellationCode);
            return;
        }

        var settled = await db.UsageTransactions
            .Where(item => item.Id == transaction.Id && item.Status == expectedStatus)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, UsageTransactionStatus.Cancelled)
                .SetProperty(item => item.ProviderCostUsd, 0m)
                .SetProperty(item => item.ProviderCostKnown, false)
                .SetProperty(item => item.ChargedAmount, 0m)
                .SetProperty(item => item.ReversedAmount, 0m)
                .SetProperty(item => item.IsBillable, false)
                .SetProperty(item => item.BillableAt, (DateTime?)null)
                .SetProperty(item => item.ActualRecordedAt, (DateTime?)null)
                .SetProperty(item => item.CompletedAt, (DateTime?)null)
                .SetProperty(item => item.FailureCode, cancellationCode),
                cancellationToken);
        if (settled == 1)
        {
            ApplyCancellation(transaction, cancellationCode);
            AcceptDurableCancellation(transaction);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Usage transaction cancelled. TransactionId={TransactionId}; Feature={Feature}; FailureCode={FailureCode}", transaction.Id, transaction.Feature, cancellationCode);
            return;
        }

        // Zero rows: this copy is behind the durable row, or the row is gone. Neither case may
        // write a cancellation, so the committed outcome is adopted onto the caller's object.
        var committed = await db.UsageTransactions
            .AsNoTracking()
            .Where(item => item.Id == transaction.Id)
            .Select(item => new
            {
                item.Status,
                item.ProviderCostUsd,
                item.ProviderCostKnown,
                item.ChargedAmount,
                item.ReversedAmount,
                item.IsBillable,
                item.BillableAt,
                item.ActualRecordedAt,
                item.CompletedAt,
                item.RefundedAt,
                item.FailureCode,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (committed is null)
        {
            logger.LogWarning("Usage transaction cancellation matched no durable row. TransactionId={TransactionId}; Feature={Feature}; ObservedStatus={ObservedStatus}; CancellationCode={CancellationCode}", transaction.Id, transaction.Feature, expectedStatus, cancellationCode);
            return;
        }

        logger.LogWarning("Usage transaction cancellation lost the terminal-status race; adopting the committed outcome. TransactionId={TransactionId}; Feature={Feature}; CommittedStatus={CommittedStatus}; SupersededCancellationCode={CancellationCode}; CommittedChargedAmount={ChargedAmount}; CommittedReversedAmount={ReversedAmount}", transaction.Id, transaction.Feature, committed.Status, cancellationCode, committed.ChargedAmount, committed.ReversedAmount);
        transaction.Status = committed.Status;
        transaction.ProviderCostUsd = committed.ProviderCostUsd;
        transaction.ProviderCostKnown = committed.ProviderCostKnown;
        transaction.ChargedAmount = committed.ChargedAmount;
        transaction.ReversedAmount = committed.ReversedAmount;
        transaction.IsBillable = committed.IsBillable;
        transaction.BillableAt = committed.BillableAt;
        transaction.ActualRecordedAt = committed.ActualRecordedAt;
        transaction.CompletedAt = committed.CompletedAt;
        transaction.RefundedAt = committed.RefundedAt;
        transaction.FailureCode = committed.FailureCode;
        AcceptDurableCancellation(transaction);
    }

    private static void ApplyCancellation(UsageTransaction transaction, string cancellationCode)
    {
        transaction.Status = UsageTransactionStatus.Cancelled;
        transaction.ProviderCostUsd = 0m;
        transaction.ProviderCostKnown = false;
        transaction.ChargedAmount = 0m;
        transaction.ReversedAmount = 0m;
        transaction.IsBillable = false;
        transaction.BillableAt = null;
        transaction.ActualRecordedAt = null;
        transaction.CompletedAt = null;
        transaction.FailureCode = cancellationCode;
    }

    /// <summary>
    /// Keeps the cancellation to exactly one durable write. The compare-and-set above already
    /// persisted the row, and the local copy was just brought in line with it, so the tracking
    /// snapshot is advanced over the same values: a later SaveChangesAsync of this context must
    /// not re-send them, because another finalizer may have moved the row again since the
    /// compare-and-set landed. Marking the individual properties unmodified is not enough here -
    /// that reverts the current value to the old snapshot - so the whole snapshot is advanced.
    /// </summary>
    private void AcceptDurableCancellation(UsageTransaction transaction)
    {
        var entry = db.Entry(transaction);
        if (entry.State is not (EntityState.Modified or EntityState.Unchanged)) return;
        entry.OriginalValues.SetValues(entry.CurrentValues);
    }

    public Task<UsageAdjustmentResult?> RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default) =>
        AddAdjustmentAsync(transaction, UsageTransactionAdjustmentType.Refund, $"usage:{transaction.Id:N}:refund", "Usage transaction refund", cancellationToken);

    public Task<UsageAdjustmentResult?> ReverseAsync(UsageTransaction transaction, string idempotencyKey, string reason, CancellationToken cancellationToken = default) =>
        AddAdjustmentAsync(transaction, UsageTransactionAdjustmentType.Reversal, idempotencyKey, reason, cancellationToken);

    private async Task<UsageAdjustmentResult?> AddAdjustmentAsync(
        UsageTransaction transaction,
        UsageTransactionAdjustmentType type,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        var normalizedKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("An adjustment idempotency key is required.", nameof(idempotencyKey)) : idempotencyKey.Trim();
        if (normalizedKey.Length > 180) throw new ArgumentException("The adjustment idempotency key is too long.", nameof(idempotencyKey));
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Usage transaction adjustment" : reason.Trim();
        if (normalizedReason.Length > 500) throw new ArgumentException("The adjustment reason is too long.", nameof(reason));
        var existing = await db.UsageTransactionAdjustments.SingleOrDefaultAsync(item => item.WorkspaceId == transaction.WorkspaceId && item.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.UsageTransactionId != transaction.Id) throw new InvalidOperationException("The adjustment idempotency key belongs to a different usage transaction.");
            return new UsageAdjustmentResult(existing, false);
        }
        if (transaction.Status != UsageTransactionStatus.Completed || !transaction.IsBillable) return null;
        var remaining = Math.Round(transaction.ChargedAmount - transaction.ReversedAmount, 8, MidpointRounding.AwayFromZero);
        if (remaining <= 0m) return null;
        var now = DateTime.UtcNow;
        var adjustment = new UsageTransactionAdjustment
        {
            Id = Guid.NewGuid(),
            WorkspaceId = transaction.WorkspaceId,
            UsageTransactionId = transaction.Id,
            Type = type,
            AmountUsd = remaining,
            Currency = transaction.Currency,
            IdempotencyKey = normalizedKey,
            Reason = normalizedReason,
            CreatedAt = now,
        };
        transaction.ReversedAmount += remaining;
        transaction.Status = UsageTransactionStatus.Refunded;
        transaction.IsBillable = false;
        transaction.RefundedAt = now;
        db.UsageTransactionAdjustments.Add(adjustment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Usage transaction adjustment recorded. TransactionId={TransactionId}; Type={Type}; AmountUsd={AmountUsd}", transaction.Id, type, remaining);
            return new UsageAdjustmentResult(adjustment, true);
        }
        catch (DbUpdateException)
        {
            db.Entry(adjustment).State = EntityState.Detached;
            db.Entry(transaction).State = EntityState.Unchanged;
            var concurrent = await db.UsageTransactionAdjustments.SingleOrDefaultAsync(item => item.WorkspaceId == transaction.WorkspaceId && item.IdempotencyKey == normalizedKey, cancellationToken);
            if (concurrent is not null)
            {
                if (concurrent.UsageTransactionId != transaction.Id) throw new InvalidOperationException("The adjustment idempotency key belongs to a different usage transaction.");
                return new UsageAdjustmentResult(concurrent, false);
            }

            // The transaction-level unique index is the concurrency guard for
            // different retry keys. Replay the winning full adjustment rather
            // than leaking a second audit row or surfacing a transient conflict.
            concurrent = await db.UsageTransactionAdjustments.SingleOrDefaultAsync(item => item.WorkspaceId == transaction.WorkspaceId && item.UsageTransactionId == transaction.Id, cancellationToken);
            if (concurrent is null) throw;
            return new UsageAdjustmentResult(concurrent, false);
        }
    }
}

public static class UsageFailureCodes
{
    public static string FromException(Exception exception) => exception switch
    {
        AiProviderTimeoutException => "PROVIDER_TIMEOUT",
        AiProviderUnavailableException => "PROVIDER_UNAVAILABLE",
        AiProviderException => "PROVIDER_FAILED",
        MockAiProviderException => "MOCK_PROVIDER_FAILED",
        AiGenerationException => "GENERATION_INCOMPLETE",
        OperationCanceledException => "GENERATION_CANCELLED",
        _ => "AI_GENERATION_FAILED",
    };
}

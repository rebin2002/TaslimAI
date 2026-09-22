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
        decimal estimatedProviderCostUsd,
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
        decimal estimatedProviderCostUsd,
        CancellationToken cancellationToken = default)
    {
        var estimate = decimal.Round(Math.Max(0m, estimatedProviderCostUsd), 8, MidpointRounding.AwayFromZero);
        if (!settings.GuardrailsEnabled) return new UsagePreflightResult(true, estimate);

        if (settings.MaxEstimatedProviderCostPerGenerationUsd is { } single && estimate > single)
            return new UsagePreflightResult(false, estimate, "COST_ESTIMATE_EXCEEDS_LIMIT", "This operation exceeds the configured safety limit.");

        var now = DateTime.UtcNow;
        if (settings.DailyWorkspaceProviderCostCeilingUsd is { } daily)
        {
            var start = now.Date;
            var spent = await ProviderCostForWindowAsync(workspaceId, start, now, cancellationToken);
            if (spent + estimate > daily)
                return new UsagePreflightResult(false, estimate, "DAILY_COST_CEILING_EXCEEDED", "This workspace has reached its configured daily safety limit.");
        }

        if (settings.MonthlyWorkspaceProviderCostCeilingUsd is { } monthly)
        {
            var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var spent = await ProviderCostForWindowAsync(workspaceId, start, now, cancellationToken);
            if (spent + estimate > monthly)
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

    private async Task<decimal> ProviderCostForWindowAsync(Guid workspaceId, DateTime start, DateTime end, CancellationToken cancellationToken) =>
        await db.UsageTransactions
            .Where(item => item.WorkspaceId == workspaceId && item.CreatedAt >= start && item.CreatedAt < end)
            .SumAsync(item => item.Status == UsageTransactionStatus.Completed
                ? item.ProviderCostUsd
                : item.Status == UsageTransactionStatus.Pending
                    ? (item.EstimatedProviderCostUsd ?? 0m)
                    : 0m, cancellationToken);

    private async Task<string?> FindAnomalyCodeAsync(UsageTransaction transaction, CancellationToken cancellationToken)
    {
        if (settings.SingleTransactionAnomalyThresholdUsd is { } single && transaction.ProviderCostUsd > single)
            return "SINGLE_TRANSACTION_COST_THRESHOLD";

        if (settings.DailyWorkspaceAnomalyThresholdUsd is { } daily)
        {
            var start = transaction.CreatedAt.Date;
            var spent = await ProviderCostForWindowAsync(transaction.WorkspaceId, start, DateTime.UtcNow, cancellationToken);
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
        decimal? estimatedProviderCostUsd = null);

    Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default);
    Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default);
    Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default);
    Task RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default);
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
        decimal? estimatedProviderCostUsd = null)
    {
        var existing = await db.UsageTransactions.SingleOrDefaultAsync(transaction =>
            transaction.WorkspaceId == workspaceId && transaction.RequestId == requestId && transaction.Feature == feature,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Status is UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled)
            {
                existing.Status = UsageTransactionStatus.Pending;
                existing.ProviderCostUsd = 0m;
                existing.ChargedAmount = 0m;
                existing.EstimatedProviderCostUsd = estimatedProviderCostUsd;
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
            ProviderCostUsd = 0m,
            ChargedAmount = 0m,
            ChargedUnit = UsageChargeUnit.Usd,
            Currency = UsageCurrencies.Usd,
            CreatedAt = DateTime.UtcNow,
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
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded) return;
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
        transaction.ProviderCostUsd = providerCost;
        transaction.ChargedAmount = chargingService.CalculateCustomerCharge(transaction);
        transaction.Currency = string.IsNullOrWhiteSpace(usage.Currency) ? transaction.Currency : usage.Currency.Trim().ToUpperInvariant();
        transaction.CostBasis = string.IsNullOrWhiteSpace(usage.CostBasis)
            ? usage.ActualCost.HasValue ? UsageCostBasis.Actual : UsageCostBasis.Estimated
            : usage.CostBasis;
        transaction.PricingVersion = string.IsNullOrWhiteSpace(usage.PricingVersion) ? costCalculator.GetPricingSnapshot(usage)?.Version : usage.PricingVersion;
        transaction.PricingSnapshotJson = snapshot;
        transaction.CompletedAt = DateTime.UtcNow;
        transaction.FailureCode = null;
        await costControl.MarkAnomalyAsync(transaction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction completed. TransactionId={TransactionId}; Feature={Feature}; ProviderCostUsd={ProviderCostUsd}; PricingVersion={PricingVersion}", transaction.Id, transaction.Feature, transaction.ProviderCostUsd, transaction.PricingVersion ?? "none");
    }

    public async Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default)
    {
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded) return;
        transaction.Status = UsageTransactionStatus.Failed;
        transaction.ProviderCostUsd = usage is null ? 0m : costCalculator.Calculate(usage);
        transaction.ChargedAmount = 0m;
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
            ? usage?.ActualCost.HasValue == true ? UsageCostBasis.Actual : transaction.CostBasis
            : usage.CostBasis;
        transaction.PricingVersion = string.IsNullOrWhiteSpace(usage?.PricingVersion) ? transaction.PricingVersion : usage.PricingVersion;
        transaction.PricingSnapshotJson = string.IsNullOrWhiteSpace(usage?.PricingSnapshotJson) ? transaction.PricingSnapshotJson : usage.PricingSnapshotJson;
        transaction.FailureCode = failureCode;
        transaction.CompletedAt = null;
        await costControl.MarkAnomalyAsync(transaction, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction failed. TransactionId={TransactionId}; Feature={Feature}; FailureCode={FailureCode}; ProviderCostUsd={ProviderCostUsd}", transaction.Id, transaction.Feature, failureCode, transaction.ProviderCostUsd);
    }

    public async Task CancelAsync(UsageTransaction transaction, string cancellationCode, CancellationToken cancellationToken = default)
    {
        if (transaction.Status is UsageTransactionStatus.Completed or UsageTransactionStatus.Refunded) return;
        transaction.Status = UsageTransactionStatus.Cancelled;
        transaction.ProviderCostUsd = 0m;
        transaction.ChargedAmount = 0m;
        transaction.FailureCode = cancellationCode;
        transaction.CompletedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction cancelled. TransactionId={TransactionId}; Feature={Feature}; FailureCode={FailureCode}", transaction.Id, transaction.Feature, cancellationCode);
    }

    public async Task RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (transaction.Status != UsageTransactionStatus.Completed) return;
        transaction.Status = UsageTransactionStatus.Refunded;
        transaction.ChargedAmount = 0m;
        transaction.RefundedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Usage transaction refunded. TransactionId={TransactionId}; Feature={Feature}", transaction.Id, transaction.Feature);
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

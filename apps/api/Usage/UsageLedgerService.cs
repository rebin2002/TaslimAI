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

public interface IUsageLedgerService
{
    Task<UsageTransaction> GetOrCreatePendingAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        Guid? conversationId,
        string requestId,
        UsageFeature feature,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(UsageTransaction transaction, AiUsageMetadata usage, CancellationToken cancellationToken = default);
    Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default);
    Task RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default);
}

public sealed class UsageLedgerService(
    TaslimDbContext db,
    IAiCostCalculator costCalculator,
    IUsageChargingService chargingService,
    ILogger<UsageLedgerService> logger) : IUsageLedgerService
{
    public async Task<UsageTransaction> GetOrCreatePendingAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        Guid? conversationId,
        string requestId,
        UsageFeature feature,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.UsageTransactions.SingleOrDefaultAsync(transaction =>
            transaction.WorkspaceId == workspaceId && transaction.RequestId == requestId && transaction.Feature == feature,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == UsageTransactionStatus.Failed)
            {
                existing.Status = UsageTransactionStatus.Pending;
                existing.ProviderCostUsd = 0m;
                existing.ChargedAmount = 0m;
                existing.CompletedAt = null;
                existing.FailureCode = null;
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
            RequestId = requestId,
            Feature = feature,
            Provider = "pending",
            Model = "pending",
            Status = UsageTransactionStatus.Pending,
            ProviderCostUsd = 0m,
            ChargedAmount = 0m,
            ChargedUnit = UsageChargeUnit.Usd,
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
        if (transaction.Status == UsageTransactionStatus.Completed) return;
        var providerCost = costCalculator.Calculate(usage);
        transaction.Provider = usage.ProviderKey;
        transaction.Model = usage.ModelKey;
        transaction.Status = UsageTransactionStatus.Completed;
        transaction.InputTokens = usage.InputTokens;
        transaction.CachedInputTokens = usage.CachedInputTokens;
        transaction.OutputTokens = usage.OutputTokens;
        transaction.ProviderCostUsd = providerCost;
        transaction.ChargedAmount = chargingService.CalculateCustomerCharge(transaction);
        transaction.CompletedAt = DateTime.UtcNow;
        transaction.FailureCode = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(UsageTransaction transaction, string failureCode, AiUsageMetadata? usage = null, CancellationToken cancellationToken = default)
    {
        if (transaction.Status == UsageTransactionStatus.Completed || transaction.Status == UsageTransactionStatus.Refunded) return;
        transaction.Status = UsageTransactionStatus.Failed;
        transaction.ProviderCostUsd = usage is null ? 0m : costCalculator.Calculate(usage);
        transaction.ChargedAmount = 0m;
        transaction.InputTokens = usage?.InputTokens;
        transaction.CachedInputTokens = usage?.CachedInputTokens;
        transaction.OutputTokens = usage?.OutputTokens;
        transaction.Provider = usage?.ProviderKey ?? transaction.Provider;
        transaction.Model = usage?.ModelKey ?? transaction.Model;
        transaction.FailureCode = failureCode;
        transaction.CompletedAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RefundAsync(UsageTransaction transaction, CancellationToken cancellationToken = default)
    {
        if (transaction.Status != UsageTransactionStatus.Completed) return;
        transaction.Status = UsageTransactionStatus.Refunded;
        transaction.ChargedAmount = 0m;
        await db.SaveChangesAsync(cancellationToken);
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

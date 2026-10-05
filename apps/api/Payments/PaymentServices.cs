using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Persistence;

namespace Taslim.Api.Payments;

public sealed class CheckoutSessionService(
    TaslimDbContext db,
    IOptions<BillingOptions> options,
    IEnumerable<IPaymentProvider> providers,
    ILogger<CheckoutSessionService> logger) : ICheckoutSessionService
{
    private readonly BillingOptions settings = options.Value;

    public async Task<CheckoutSessionResult> CreateAsync(
        Guid workspaceId,
        string planCode,
        string idempotencyKey,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default)
    {
        if (!settings.CustomerChargingEnabled)
        {
            logger.LogInformation("Checkout request rejected because customer charging is disabled. WorkspaceId={WorkspaceId}; PlanCode={PlanCode}; ChargingEnabled={ChargingEnabled}", workspaceId, planCode, false);
            return new(false, null, null, "CHECKOUT_DISABLED", "Customer charging is intentionally disabled.");
        }

        var plan = await db.Plans.SingleOrDefaultAsync(item => item.Code == planCode && item.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("The requested billing plan is not available.");
        var existing = await db.CheckoutSessions.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return new(existing.Status is CheckoutSessionStatus.Created or CheckoutSessionStatus.Open,
                existing.Id, existing.ProviderSessionReference is null ? null : null, "IDEMPOTENT_REPLAY", existing.FailureReason);
        }

        var provider = providers.SingleOrDefault(item => item.Key.Equals(settings.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogWarning("Checkout request could not find a configured payment provider. WorkspaceId={WorkspaceId}; ProviderKey={ProviderKey}", workspaceId, settings.Provider);
            return new(false, null, null, "PAYMENT_PROVIDER_UNCONFIGURED", "A reviewed payment provider has not been configured.");
        }

        var workspace = await db.Workspaces.AsNoTracking().SingleOrDefaultAsync(item => item.Id == workspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The workspace was not found.");
        var now = DateTime.UtcNow;
        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = plan.Id, Provider = provider.Key,
            Status = CheckoutSessionStatus.Created, IdempotencyKey = idempotencyKey.Trim(), Currency = plan.Currency,
            Amount = plan.MonthlyPriceUsd, CreatedAt = now, ExpiresAt = now.AddMinutes(30),
        };
        db.CheckoutSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var customerReference = await db.ProviderCustomerReferences.AsNoTracking()
                .Where(item => item.WorkspaceId == workspaceId && item.Provider == provider.Key && item.IsDefault)
                .Select(item => item.ProviderCustomerReferenceValue)
                .FirstOrDefaultAsync(cancellationToken);
            var result = await provider.CreateCheckoutSessionAsync(
                new(idempotencyKey, plan.Code, plan.MonthlyPriceUsd, plan.Currency, customerReference, successUrl, cancelUrl),
                cancellationToken);
            session.ProviderSessionReference = result.ProviderSessionReference;
            session.Status = CheckoutSessionStatus.Open;
            session.ExpiresAt = result.ExpiresAt ?? session.ExpiresAt;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Checkout session created. CheckoutSessionId={CheckoutSessionId}; WorkspaceId={WorkspaceId}; ProviderKey={ProviderKey}; Status={Status}", session.Id, workspaceId, provider.Key, session.Status);
            return new(true, session.Id, result.CheckoutUrl, "CHECKOUT_CREATED", null);
        }
        catch (Exception exception)
        {
            session.Status = CheckoutSessionStatus.Failed;
            session.FailureReason = "Checkout provider request failed; no customer charge was confirmed.";
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Checkout provider request failed. CheckoutSessionId={CheckoutSessionId}; WorkspaceId={WorkspaceId}; ProviderKey={ProviderKey}; ChargingEnabled={ChargingEnabled}", session.Id, workspaceId, provider.Key, settings.CustomerChargingEnabled);
            throw new InvalidOperationException(session.FailureReason, exception);
        }
    }
}

public sealed class PaymentLifecycleService(TaslimDbContext db, ICreditLedgerService creditLedger, INotificationEventWriter notifications, ILogger<PaymentLifecycleService> logger) : IPaymentLifecycleService
{
    public async Task<PaymentAttempt> RecordPaymentAttemptAsync(
        Guid workspaceId, string provider, string idempotencyKey, decimal amount, string currency,
        PaymentAttemptStatus status = PaymentAttemptStatus.Created, Guid? subscriptionId = null,
        Guid? checkoutSessionId = null, string? providerPaymentReference = null, string? failureCode = null,
        string? failureReason = null, CancellationToken cancellationToken = default)
    {
        ValidateReason(provider, nameof(provider));
        ValidateReason(idempotencyKey, nameof(idempotencyKey));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var normalizedProvider = provider.Trim();
        var normalizedKey = idempotencyKey.Trim();
        var normalizedCurrency = NormalizeCurrency(currency);
        var normalizedProviderReference = string.IsNullOrWhiteSpace(providerPaymentReference) ? null : providerPaymentReference.Trim();
        var existing = await db.PaymentAttempts.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.IdempotencyKey == normalizedKey,
            cancellationToken);
        if (existing is not null)
        {
            EnsurePaymentAttemptReplayMatches(existing, normalizedProvider, amount, normalizedCurrency, subscriptionId, checkoutSessionId, normalizedProviderReference);
            return existing;
        }
        if (subscriptionId.HasValue && !await db.Subscriptions.AnyAsync(item => item.Id == subscriptionId.Value && item.WorkspaceId == workspaceId, cancellationToken))
            throw new InvalidOperationException("The subscription belongs to a different workspace or was not found.");
        if (checkoutSessionId.HasValue && !await db.CheckoutSessions.AnyAsync(item => item.Id == checkoutSessionId.Value && item.WorkspaceId == workspaceId, cancellationToken))
            throw new InvalidOperationException("The checkout session belongs to a different workspace or was not found.");
        if (!string.IsNullOrWhiteSpace(providerPaymentReference))
        {
            var byProviderReference = await db.PaymentAttempts.SingleOrDefaultAsync(
                item => item.Provider == normalizedProvider && item.ProviderPaymentReference == normalizedProviderReference,
                cancellationToken);
            if (byProviderReference is not null)
            {
                if (byProviderReference.WorkspaceId != workspaceId)
                    throw new InvalidOperationException("The provider payment reference is already associated with another workspace.");
                EnsurePaymentAttemptReplayMatches(byProviderReference, normalizedProvider, amount, normalizedCurrency, subscriptionId, checkoutSessionId, normalizedProviderReference);
                return byProviderReference;
            }
        }

        var now = DateTime.UtcNow;
        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, SubscriptionId = subscriptionId,
            CheckoutSessionId = checkoutSessionId, Provider = normalizedProvider,
            ProviderPaymentReference = normalizedProviderReference, Status = status,
            Amount = amount, Currency = normalizedCurrency, IdempotencyKey = normalizedKey,
            FailureCode = failureCode?.Trim(), FailureReason = failureReason?.Trim(), CreatedAt = now, UpdatedAt = now,
            SucceededAt = status == PaymentAttemptStatus.Succeeded ? now : null,
        };
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment attempt recorded. PaymentAttemptId={PaymentAttemptId}; WorkspaceId={WorkspaceId}; ProviderKey={Provider}; Status={Status}; Amount={Amount}; Currency={Currency}", attempt.Id, workspaceId, attempt.Provider, attempt.Status, attempt.Amount, attempt.Currency);
        return attempt;
    }

    public async Task<PaymentAttempt> MarkPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, string failureCode, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(failureCode, nameof(failureCode));
        ValidateReason(reason, nameof(reason));
        var attempt = await GetAttemptAsync(workspaceId, paymentAttemptId, cancellationToken);
        if (attempt.Status == PaymentAttemptStatus.Failed)
        {
            if (string.Equals(attempt.FailureCode, failureCode.Trim(), StringComparison.Ordinal) && string.Equals(attempt.FailureReason, reason.Trim(), StringComparison.Ordinal))
                return attempt;
            throw new InvalidOperationException("The payment attempt has already been recorded as failed with different details.");
        }
        if (attempt.Status is PaymentAttemptStatus.Refunded or PaymentAttemptStatus.PartiallyRefunded)
            throw new InvalidOperationException("A refunded payment attempt cannot be marked failed.");
        if (attempt.Status == PaymentAttemptStatus.Succeeded)
            throw new InvalidOperationException("A succeeded payment attempt cannot be marked failed.");
        attempt.Status = PaymentAttemptStatus.Failed;
        attempt.FailureCode = failureCode.Trim();
        attempt.FailureReason = reason.Trim();
        attempt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Payment attempt failed. PaymentAttemptId={PaymentAttemptId}; WorkspaceId={WorkspaceId}; FailureCode={FailureCode}", paymentAttemptId, workspaceId, failureCode);
        if (attempt.SubscriptionId.HasValue)
        {
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(item => item.Id == attempt.SubscriptionId && item.WorkspaceId == workspaceId, cancellationToken);
            if (subscription is not null && subscription.Status == SubscriptionStatus.Active)
            {
                subscription.Status = SubscriptionStatus.PastDue;
                subscription.UpdatedAt = DateTime.UtcNow;
                AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.PaymentFailed, "payment", reason);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        try
        {
            await notifications.CreateBillingPaymentFailedAsync(workspaceId, paymentAttemptId, cancellationToken);
        }
        catch (Exception)
        {
            // Billing state remains authoritative even if an in-app attention record cannot be written.
        }
        return attempt;
    }

    public async Task<PaymentAttempt> MarkPaymentSucceededAsync(Guid workspaceId, Guid paymentAttemptId, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(reason, nameof(reason));
        var attempt = await GetAttemptAsync(workspaceId, paymentAttemptId, cancellationToken);
        if (attempt.Status == PaymentAttemptStatus.Succeeded) return attempt;
        if (attempt.Status is PaymentAttemptStatus.Failed or PaymentAttemptStatus.Cancelled or PaymentAttemptStatus.Refunded or PaymentAttemptStatus.PartiallyRefunded)
            throw new InvalidOperationException("A terminal payment attempt cannot be marked succeeded again.");
        attempt.Status = PaymentAttemptStatus.Succeeded;
        attempt.FailureCode = null;
        attempt.FailureReason = null;
        attempt.SucceededAt = DateTime.UtcNow;
        attempt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment attempt succeeded. PaymentAttemptId={PaymentAttemptId}; WorkspaceId={WorkspaceId}; Provider={Provider}", paymentAttemptId, workspaceId, attempt.Provider);
        return attempt;
    }

    public async Task<Subscription> ActivateSubscriptionAsync(Guid workspaceId, Guid subscriptionId, Guid planId, string provider, string providerSubscriptionReference, DateTime periodStart, DateTime periodEnd, string reason, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(periodStart, periodEnd);
        ValidateReason(reason, nameof(reason));
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var subscription = await GetSubscriptionAsync(workspaceId, subscriptionId, cancellationToken);
        var plan = await db.Plans.SingleOrDefaultAsync(item => item.Id == planId && item.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("The subscription plan is not available.");
        subscription.PlanId = plan.Id;
        subscription.BillingProvider = provider.Trim();
        subscription.ProviderSubscriptionReference = providerSubscriptionReference.Trim();
        subscription.Status = SubscriptionStatus.Active;
        subscription.CancelAtPeriodEnd = false;
        subscription.CurrentPeriodStart = periodStart;
        subscription.CurrentPeriodEnd = periodEnd;
        subscription.NextRenewalAt = periodEnd;
        subscription.UpdatedAt = DateTime.UtcNow;
        AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.Activated, provider, reason);
        await db.SaveChangesAsync(cancellationToken);
        await EnsurePeriodAndGrantAsync(subscription, plan, periodStart, periodEnd, $"subscription activation: {reason}", cancellationToken);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
        return subscription;
    }

    public async Task<Subscription> RenewSubscriptionAsync(Guid workspaceId, Guid subscriptionId, DateTime periodStart, DateTime periodEnd, string idempotencyKey, string reason, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(periodStart, periodEnd);
        ValidateReason(idempotencyKey, nameof(idempotencyKey));
        ValidateReason(reason, nameof(reason));
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var subscription = await GetSubscriptionAsync(workspaceId, subscriptionId, cancellationToken);
        if (subscription.CancelAtPeriodEnd)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.UpdatedAt = DateTime.UtcNow;
            AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.Cancelled, "payment", reason);
            await db.SaveChangesAsync(cancellationToken);
            if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
            return subscription;
        }
        var plan = await db.Plans.SingleAsync(item => item.Id == subscription.PlanId, cancellationToken);
        subscription.Status = SubscriptionStatus.Active;
        subscription.CurrentPeriodStart = periodStart;
        subscription.CurrentPeriodEnd = periodEnd;
        subscription.NextRenewalAt = periodEnd;
        subscription.UpdatedAt = DateTime.UtcNow;
        AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.Renewed, "payment", reason);
        await db.SaveChangesAsync(cancellationToken);
        await EnsurePeriodAndGrantAsync(subscription, plan, periodStart, periodEnd, $"subscription renewal: {reason}", cancellationToken);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
        return subscription;
    }

    public async Task<PaymentRefund> RecordRefundAsync(Guid workspaceId, Guid paymentAttemptId, string provider, decimal amount, string currency, string idempotencyKey, string reason, string? providerRefundReference = null, CancellationToken cancellationToken = default)
    {
        ValidateReason(provider, nameof(provider));
        ValidateReason(idempotencyKey, nameof(idempotencyKey));
        ValidateReason(reason, nameof(reason));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var normalizedProvider = provider.Trim();
        var normalizedCurrency = NormalizeCurrency(currency);
        var normalizedKey = idempotencyKey.Trim();
        var normalizedReason = reason.Trim();
        var normalizedProviderReference = string.IsNullOrWhiteSpace(providerRefundReference) ? null : providerRefundReference.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.PaymentRefunds.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            EnsureRefundReplayMatches(existing, paymentAttemptId, normalizedProvider, amount, normalizedCurrency, normalizedReason, normalizedProviderReference);
            return existing;
        }
        if (normalizedProviderReference is not null)
        {
            var duplicateReference = await db.PaymentRefunds.SingleOrDefaultAsync(item => item.Provider == normalizedProvider && item.ProviderRefundReference == normalizedProviderReference, cancellationToken);
            if (duplicateReference is not null)
            {
                if (duplicateReference.WorkspaceId != workspaceId)
                    throw new InvalidOperationException("The provider refund reference is already associated with another workspace.");
                EnsureRefundReplayMatches(duplicateReference, paymentAttemptId, normalizedProvider, amount, normalizedCurrency, normalizedReason, normalizedProviderReference);
                return duplicateReference;
            }
        }
        var attempt = await GetAttemptAsync(workspaceId, paymentAttemptId, cancellationToken);
        if (attempt.Status is not (PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.PartiallyRefunded))
            throw new InvalidOperationException("Only a succeeded payment attempt can be refunded.");
        if (!string.Equals(attempt.Provider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The refund provider does not match the payment attempt.");
        if (!string.Equals(attempt.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The refund currency does not match the payment attempt.");
        var refundedAmounts = await db.PaymentRefunds
            .Where(item => item.PaymentAttemptId == attempt.Id && item.Status == PaymentRefundStatus.Succeeded)
            .Select(item => item.Amount)
            .ToListAsync(cancellationToken);
        var refundedAmount = refundedAmounts.Sum();
        if (amount > attempt.Amount - refundedAmount)
            throw new InvalidOperationException("The refund exceeds the remaining refundable payment amount.");
        var totalRefundedAmount = refundedAmount + amount;
        var now = DateTime.UtcNow;
        var refund = new PaymentRefund
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, PaymentAttemptId = attempt.Id, Provider = normalizedProvider,
            ProviderRefundReference = normalizedProviderReference, Status = PaymentRefundStatus.Succeeded,
            Amount = amount, Currency = normalizedCurrency, IdempotencyKey = normalizedKey,
            Reason = normalizedReason, CreatedAt = now, UpdatedAt = now, CompletedAt = now,
        };
        db.PaymentRefunds.Add(refund);
        attempt.Status = totalRefundedAmount >= attempt.Amount ? PaymentAttemptStatus.Refunded : PaymentAttemptStatus.PartiallyRefunded;
        attempt.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        // A partial monetary refund has no reviewed credit-conversion policy. Do
        // not reverse the entire purchased grant until the payment is fully
        // refunded; otherwise a $1 refund could restore all $9-plan credits.
        if (attempt.CreditLedgerEntryId.HasValue && totalRefundedAmount >= attempt.Amount)
        {
            await creditLedger.RefundAsync(workspaceId, attempt.CreditLedgerEntryId.Value, $"refund:{refund.Id}", $"Payment refund {refund.Id}: {reason}", cancellationToken: cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Payment refund recorded. PaymentRefundId={PaymentRefundId}; PaymentAttemptId={PaymentAttemptId}; WorkspaceId={WorkspaceId}; Status={Status}", refund.Id, paymentAttemptId, workspaceId, refund.Status);
        return refund;
    }

    public async Task SetCancelAtPeriodEndAsync(Guid workspaceId, Guid subscriptionId, bool cancelAtPeriodEnd, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(reason, nameof(reason));
        var subscription = await GetSubscriptionAsync(workspaceId, subscriptionId, cancellationToken);
        subscription.CancelAtPeriodEnd = cancelAtPeriodEnd;
        subscription.UpdatedAt = DateTime.UtcNow;
        AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.CancelRequested, "account", reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelSubscriptionAsync(Guid workspaceId, Guid subscriptionId, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(reason, nameof(reason));
        var subscription = await GetSubscriptionAsync(workspaceId, subscriptionId, cancellationToken);
        subscription.Status = SubscriptionStatus.Cancelled;
        subscription.CancelAtPeriodEnd = false;
        subscription.UpdatedAt = DateTime.UtcNow;
        AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.Cancelled, "payment", reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddLifecycleEvent(Subscription subscription, SubscriptionLifecycleEventType type, string source, string reason)
    {
        db.SubscriptionLifecycleEvents.Add(new SubscriptionLifecycleEvent
        {
            Id = Guid.NewGuid(), WorkspaceId = subscription.WorkspaceId, SubscriptionId = subscription.Id,
            Type = type, Source = source, Reason = reason.Trim(), CreatedAt = DateTime.UtcNow,
        });
    }

    private async Task EnsurePeriodAndGrantAsync(Subscription subscription, Plan plan, DateTime periodStart, DateTime periodEnd, string reason, CancellationToken cancellationToken)
    {
        var period = await db.BillingPeriods.SingleOrDefaultAsync(item => item.SubscriptionId == subscription.Id && item.StartsAt == periodStart, cancellationToken);
        if (period is null)
        {
            period = new BillingPeriod
            {
                Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Open,
                StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = plan.MonthlyCreditAllowance, CreatedAt = DateTime.UtcNow,
            };
            db.BillingPeriods.Add(period);
            await db.SaveChangesAsync(cancellationToken);
        }
        var idempotencyKey = $"included:{subscription.Id}:{period.Id}";
        await creditLedger.GrantAsync(subscription.WorkspaceId, CreditEntitlementType.IncludedMonthly, plan.MonthlyCreditAllowance,
            idempotencyKey, reason, periodEnd, period.Id, sourceReference: subscription.ProviderSubscriptionReference, cancellationToken: cancellationToken);
    }

    private async Task<PaymentAttempt> GetAttemptAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken) =>
        await db.PaymentAttempts.SingleOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId, cancellationToken)
        ?? throw new InvalidOperationException("The payment attempt was not found.");

    private async Task<Subscription> GetSubscriptionAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken) =>
        await db.Subscriptions.SingleOrDefaultAsync(item => item.Id == id && item.WorkspaceId == workspaceId, cancellationToken)
        ?? throw new InvalidOperationException("The subscription was not found.");

    private static void EnsurePaymentAttemptReplayMatches(
        PaymentAttempt existing, string provider, decimal amount, string currency,
        Guid? subscriptionId, Guid? checkoutSessionId, string? providerPaymentReference)
    {
        if (!string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase) ||
            existing.Amount != amount ||
            !string.Equals(existing.Currency, currency, StringComparison.OrdinalIgnoreCase) ||
            existing.SubscriptionId != subscriptionId ||
            existing.CheckoutSessionId != checkoutSessionId ||
            !string.Equals(existing.ProviderPaymentReference, providerPaymentReference, StringComparison.Ordinal))
            throw new InvalidOperationException("The payment attempt idempotency key belongs to a different payment.");
    }

    private static void EnsureRefundReplayMatches(
        PaymentRefund existing, Guid paymentAttemptId, string provider, decimal amount, string currency, string reason, string? providerRefundReference)
    {
        if (existing.PaymentAttemptId != paymentAttemptId ||
            !string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase) ||
            existing.Amount != amount ||
            !string.Equals(existing.Currency, currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(existing.Reason, reason, StringComparison.Ordinal) ||
            !string.Equals(existing.ProviderRefundReference, providerRefundReference, StringComparison.Ordinal))
            throw new InvalidOperationException("The refund idempotency key belongs to a different refund.");
    }

    private static string NormalizeCurrency(string value)
    {
        ValidateReason(value, nameof(value));
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 3) throw new ArgumentException("Currency must be a three-letter code.", nameof(value));
        return normalized;
    }

    private static void ValidateReason(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An auditable value is required.", name);
    }

    private static void ValidatePeriod(DateTime start, DateTime end)
    {
        if (end <= start) throw new ArgumentException("The billing period must end after it starts.");
    }
}

public sealed class PaymentWebhookService(
    TaslimDbContext db,
    IEnumerable<IPaymentProvider> providers,
    IPaymentLifecycleService lifecycle,
    ILogger<PaymentWebhookService> logger) : IPaymentWebhookService
{
    public async Task<WebhookProcessingResult> ProcessAsync(string providerKey, string rawPayload, string signature, CancellationToken cancellationToken = default)
    {
        var provider = providers.SingleOrDefault(item => item.Key.Equals(providerKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogWarning("Payment webhook rejected because provider is unconfigured. ProviderKey={ProviderKey}", providerKey);
            return new(false, false, "PAYMENT_PROVIDER_UNCONFIGURED", null, "The payment provider is not configured.");
        }
        if (string.IsNullOrWhiteSpace(rawPayload) || !provider.WebhookSignatureVerifier.Verify(rawPayload, signature))
        {
            logger.LogWarning("Payment webhook signature verification failed. ProviderKey={ProviderKey}; PayloadPresent={PayloadPresent}", provider.Key, !string.IsNullOrWhiteSpace(rawPayload));
            return new(false, false, "WEBHOOK_SIGNATURE_INVALID", null, "The webhook signature could not be verified.");
        }

        ProviderPaymentEvent parsed;
        try
        {
            parsed = provider.ParseWebhook(rawPayload);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Payment webhook payload could not be parsed. Provider={Provider}", provider.Key);
            return new(false, false, "WEBHOOK_PAYLOAD_INVALID", null, "The payment event payload is invalid.");
        }

        var duplicate = await db.PaymentEvents.SingleOrDefaultAsync(item => item.Provider == provider.Key && item.ProviderEventReference == parsed.ProviderEventReference, cancellationToken);
        if (duplicate is not null)
        {
            logger.LogInformation("Duplicate payment webhook ignored. ProviderKey={ProviderKey}; PaymentEventId={PaymentEventId}; Status={Status}", provider.Key, duplicate.Id, duplicate.Status);
            return new(true, true, "WEBHOOK_DUPLICATE", duplicate.Id, "The already-recorded payment event was not applied twice.");
        }

        var paymentEvent = new PaymentEvent
        {
            Id = Guid.NewGuid(), Provider = provider.Key, ProviderEventReference = parsed.ProviderEventReference,
            Type = parsed.Type, Status = PaymentEventStatus.Received, WorkspaceId = parsed.WorkspaceId,
            SubscriptionId = parsed.SubscriptionId, CheckoutSessionId = parsed.CheckoutSessionId, PaymentAttemptId = parsed.PaymentAttemptId,
            PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload))),
            PayloadJson = rawPayload.Length > 100_000 ? rawPayload[..100_000] : rawPayload,
            SignatureVerified = true, Reason = parsed.Reason, OccurredAt = parsed.OccurredAt, ReceivedAt = DateTime.UtcNow,
        };
        db.PaymentEvents.Add(paymentEvent);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment webhook recorded. ProviderKey={ProviderKey}; PaymentEventId={PaymentEventId}; EventType={EventType}; SignatureVerified={SignatureVerified}", provider.Key, paymentEvent.Id, paymentEvent.Type, paymentEvent.SignatureVerified);

        var ownsTransition = db.Database.CurrentTransaction is null;
        await using var transition = ownsTransition
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var savepointName = $"payment_webhook_{Guid.NewGuid():N}";
        if (!ownsTransition)
            await db.Database.CurrentTransaction!.CreateSavepointAsync(savepointName, cancellationToken);

        try
        {
            ValidateProviderEventShape(parsed);
            PaymentAttempt? linkedAttempt = null;
            if (parsed.PaymentAttemptId.HasValue)
            {
                linkedAttempt = await db.PaymentAttempts.SingleOrDefaultAsync(item =>
                    item.Id == parsed.PaymentAttemptId.Value && item.WorkspaceId == parsed.WorkspaceId!.Value, cancellationToken)
                    ?? throw new InvalidOperationException("The payment attempt referenced by the event was not found.");
                if (!string.Equals(linkedAttempt.Provider, provider.Key, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The payment attempt provider does not match the webhook provider.");
                if (!string.IsNullOrWhiteSpace(parsed.ProviderPaymentReference))
                {
                    if (linkedAttempt.ProviderPaymentReference is not null &&
                        !string.Equals(linkedAttempt.ProviderPaymentReference, parsed.ProviderPaymentReference, StringComparison.Ordinal))
                        throw new InvalidOperationException("The provider payment reference does not match the recorded payment attempt.");
                    linkedAttempt.ProviderPaymentReference ??= parsed.ProviderPaymentReference.Trim();
                }
            }
            switch (parsed.Type)
            {
                case PaymentEventType.PaymentSucceeded:
                case PaymentEventType.RenewalSucceeded when parsed.PaymentAttemptId.HasValue:
                    if (linkedAttempt is not null)
                    {
                        var workspaceId = parsed.WorkspaceId ?? throw new InvalidOperationException("The payment event did not include a valid workspace reference.");
                        var paymentAttemptId = parsed.PaymentAttemptId ?? throw new InvalidOperationException("The payment event did not include a valid payment attempt reference.");
                        if (parsed.Amount.HasValue && parsed.Amount.Value != linkedAttempt.Amount || !string.Equals(parsed.Currency, linkedAttempt.Currency, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The provider payment amount or currency does not match the recorded payment attempt.");
                        await lifecycle.MarkPaymentSucceededAsync(workspaceId, paymentAttemptId, parsed.Reason ?? "Provider payment succeeded.", cancellationToken);
                        if (parsed.Type == PaymentEventType.RenewalSucceeded && parsed.SubscriptionId is Guid subscriptionId && parsed.PeriodStart is DateTime periodStart && parsed.PeriodEnd is DateTime periodEnd)
                            await lifecycle.RenewSubscriptionAsync(workspaceId, subscriptionId, periodStart, periodEnd, $"webhook:{paymentEvent.Id}", parsed.Reason ?? "Provider renewal succeeded.", cancellationToken);
                    }
                    break;
                case PaymentEventType.PaymentFailed:
                case PaymentEventType.RenewalFailed:
                    if (parsed.WorkspaceId.HasValue && parsed.PaymentAttemptId.HasValue)
                        await lifecycle.MarkPaymentFailedAsync(parsed.WorkspaceId.Value, parsed.PaymentAttemptId.Value, "PROVIDER_PAYMENT_FAILED", parsed.Reason ?? "Provider payment failed.", cancellationToken);
                    break;
                case PaymentEventType.SubscriptionCancelled:
                    if (parsed.WorkspaceId.HasValue && parsed.SubscriptionId.HasValue)
                        await lifecycle.CancelSubscriptionAsync(parsed.WorkspaceId.Value, parsed.SubscriptionId.Value, parsed.Reason ?? "Provider cancelled the subscription.", cancellationToken);
                    break;
                case PaymentEventType.RenewalSucceeded when parsed.SubscriptionId.HasValue && parsed.WorkspaceId.HasValue && parsed.PeriodStart.HasValue && parsed.PeriodEnd.HasValue:
                    await lifecycle.RenewSubscriptionAsync(parsed.WorkspaceId.Value, parsed.SubscriptionId.Value, parsed.PeriodStart.Value, parsed.PeriodEnd.Value, $"webhook:{paymentEvent.Id}", parsed.Reason ?? "Provider renewal succeeded.", cancellationToken);
                    break;
            }
            paymentEvent.Status = PaymentEventStatus.Processed;
            paymentEvent.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (ownsTransition) await transition!.CommitAsync(cancellationToken);
            logger.LogInformation("Payment webhook processed. ProviderKey={ProviderKey}; PaymentEventId={PaymentEventId}; EventType={EventType}; Status={Status}", provider.Key, paymentEvent.Id, paymentEvent.Type, paymentEvent.Status);
            return new(true, false, "WEBHOOK_PROCESSED", paymentEvent.Id, null);
        }
        catch (Exception exception)
        {
            if (ownsTransition)
            {
                await transition!.RollbackAsync(CancellationToken.None);
                await transition.DisposeAsync();
            }
            else
                await db.Database.CurrentTransaction!.RollbackToSavepointAsync(savepointName, CancellationToken.None);

            db.ChangeTracker.Clear();
            var rejectedEvent = await db.PaymentEvents.SingleAsync(item => item.Id == paymentEvent.Id, CancellationToken.None);
            rejectedEvent.Status = PaymentEventStatus.Rejected;
            rejectedEvent.FailureReason = "The verified event was recorded but its domain transition was not applied.";
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogError(exception, "Verified payment webhook could not be applied. EventId={EventId}", paymentEvent.Id);
            return new(false, false, "WEBHOOK_PROCESSING_FAILED", rejectedEvent.Id, rejectedEvent.FailureReason);
        }
    }

    private static void ValidateProviderEventShape(ProviderPaymentEvent parsed)
    {
        switch (parsed.Type)
        {
            case PaymentEventType.PaymentSucceeded:
                RequireText(parsed.ProviderPaymentReference, "provider payment");
                Require(parsed.WorkspaceId, "workspace");
                Require(parsed.PaymentAttemptId, "payment attempt");
                break;
            case PaymentEventType.PaymentFailed:
            case PaymentEventType.RenewalFailed:
                Require(parsed.WorkspaceId, "workspace");
                Require(parsed.PaymentAttemptId, "payment attempt");
                break;
            case PaymentEventType.RenewalSucceeded:
                Require(parsed.WorkspaceId, "workspace");
                Require(parsed.SubscriptionId, "subscription");
                if (!parsed.PeriodStart.HasValue || !parsed.PeriodEnd.HasValue)
                    throw new InvalidOperationException("The renewal event did not include a complete billing period.");
                if (parsed.PaymentAttemptId.HasValue)
                    RequireText(parsed.ProviderPaymentReference, "provider payment");
                break;
            case PaymentEventType.SubscriptionCancelled:
                Require(parsed.WorkspaceId, "workspace");
                Require(parsed.SubscriptionId, "subscription");
                break;
            default:
                throw new InvalidOperationException("The verified payment event type is not supported by the billing foundation.");
        }
    }

    private static void Require(Guid? value, string name)
    {
        if (!value.HasValue || value.Value == Guid.Empty)
            throw new InvalidOperationException($"The payment event did not include a valid {name} reference.");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"The payment event did not include a valid {name} reference.");
    }
}

public sealed class PaymentReconciliationService(TaslimDbContext db, ILogger<PaymentReconciliationService> logger) : IPaymentReconciliationService
{
    public async Task<PaymentReconciliationRecord> RecordAsync(
        string provider, string providerObjectType, string providerObjectReference, ReconciliationStatus status,
        string reason, Guid? workspaceId = null, string? localEntityType = null, Guid? localEntityId = null,
        CancellationToken cancellationToken = default)
    {
        Validate(provider, nameof(provider));
        Validate(providerObjectType, nameof(providerObjectType));
        Validate(providerObjectReference, nameof(providerObjectReference));
        Validate(reason, nameof(reason));
        var existing = await db.PaymentReconciliationRecords.SingleOrDefaultAsync(item =>
            item.Provider == provider && item.ProviderObjectType == providerObjectType && item.ProviderObjectReference == providerObjectReference,
            cancellationToken);
        if (existing is not null) return existing;
        var now = DateTime.UtcNow;
        var record = new PaymentReconciliationRecord
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Provider = provider.Trim(),
            ProviderObjectType = providerObjectType.Trim(), ProviderObjectReference = providerObjectReference.Trim(),
            LocalEntityType = localEntityType?.Trim(), LocalEntityId = localEntityId, Status = status,
            Reason = reason.Trim(), CreatedAt = now, UpdatedAt = now,
        };
        db.PaymentReconciliationRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment reconciliation record created. ReconciliationId={ReconciliationId}; Provider={Provider}; Status={Status}; WorkspaceId={WorkspaceId}", record.Id, record.Provider, record.Status, record.WorkspaceId);
        return record;
    }

    public async Task ResolveAsync(Guid reconciliationId, string reason, CancellationToken cancellationToken = default)
    {
        Validate(reason, nameof(reason));
        var record = await db.PaymentReconciliationRecords.SingleOrDefaultAsync(item => item.Id == reconciliationId, cancellationToken)
            ?? throw new InvalidOperationException("The reconciliation record was not found.");
        record.Status = ReconciliationStatus.Resolved;
        record.Reason = reason.Trim();
        record.ResolvedAt = DateTime.UtcNow;
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Payment reconciliation record resolved. ReconciliationId={ReconciliationId}; Status={Status}", reconciliationId, record.Status);
    }

    private static void Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("An auditable value is required.", name);
    }
}

public sealed class UnconfiguredPaymentProvider : IPaymentProvider
{
    public string Key => "unconfigured";
    public IWebhookSignatureVerifier WebhookSignatureVerifier { get; } = new RejectingWebhookSignatureVerifier();
    public Task<ProviderCheckoutSessionResult> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No payment provider is configured.");
    public ProviderPaymentEvent ParseWebhook(string rawPayload) => throw new InvalidOperationException("No payment provider is configured.");
}

public sealed class RejectingWebhookSignatureVerifier : IWebhookSignatureVerifier
{
    public bool Verify(string rawPayload, string signature) => false;
}

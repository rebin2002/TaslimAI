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
    IEnumerable<IPaymentProvider> providers) : ICheckoutSessionService
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
            return new(true, session.Id, result.CheckoutUrl, "CHECKOUT_CREATED", null);
        }
        catch (Exception exception)
        {
            session.Status = CheckoutSessionStatus.Failed;
            session.FailureReason = "Checkout provider request failed; no customer charge was confirmed.";
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(session.FailureReason, exception);
        }
    }
}

public sealed class PaymentLifecycleService(TaslimDbContext db, ICreditLedgerService creditLedger, INotificationEventWriter notifications) : IPaymentLifecycleService
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
        var existing = await db.PaymentAttempts.SingleOrDefaultAsync(
            item => item.WorkspaceId == workspaceId && item.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null) return existing;
        if (!string.IsNullOrWhiteSpace(providerPaymentReference))
        {
            var byProviderReference = await db.PaymentAttempts.SingleOrDefaultAsync(
                item => item.Provider == provider && item.ProviderPaymentReference == providerPaymentReference,
                cancellationToken);
            if (byProviderReference is not null)
            {
                if (byProviderReference.WorkspaceId != workspaceId)
                    throw new InvalidOperationException("The provider payment reference is already associated with another workspace.");
                return byProviderReference;
            }
        }

        var now = DateTime.UtcNow;
        var attempt = new PaymentAttempt
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, SubscriptionId = subscriptionId,
            CheckoutSessionId = checkoutSessionId, Provider = provider.Trim(),
            ProviderPaymentReference = providerPaymentReference?.Trim(), Status = status,
            Amount = amount, Currency = currency.Trim().ToUpperInvariant(), IdempotencyKey = idempotencyKey.Trim(),
            FailureCode = failureCode?.Trim(), FailureReason = failureReason?.Trim(), CreatedAt = now, UpdatedAt = now,
            SucceededAt = status == PaymentAttemptStatus.Succeeded ? now : null,
        };
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task<PaymentAttempt> MarkPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, string failureCode, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(failureCode, nameof(failureCode));
        ValidateReason(reason, nameof(reason));
        var attempt = await GetAttemptAsync(workspaceId, paymentAttemptId, cancellationToken);
        if (attempt.Status is PaymentAttemptStatus.Refunded or PaymentAttemptStatus.PartiallyRefunded)
            throw new InvalidOperationException("A refunded payment attempt cannot be marked failed.");
        attempt.Status = PaymentAttemptStatus.Failed;
        attempt.FailureCode = failureCode.Trim();
        attempt.FailureReason = reason.Trim();
        attempt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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
        if (attempt.Status is PaymentAttemptStatus.Refunded or PaymentAttemptStatus.PartiallyRefunded)
            throw new InvalidOperationException("A refunded payment attempt cannot be marked succeeded again.");
        attempt.Status = PaymentAttemptStatus.Succeeded;
        attempt.FailureCode = null;
        attempt.FailureReason = null;
        attempt.SucceededAt = DateTime.UtcNow;
        attempt.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task<Subscription> ActivateSubscriptionAsync(Guid workspaceId, Guid subscriptionId, Guid planId, string provider, string providerSubscriptionReference, DateTime periodStart, DateTime periodEnd, string reason, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(periodStart, periodEnd);
        ValidateReason(reason, nameof(reason));
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
        return subscription;
    }

    public async Task<Subscription> RenewSubscriptionAsync(Guid workspaceId, Guid subscriptionId, DateTime periodStart, DateTime periodEnd, string idempotencyKey, string reason, CancellationToken cancellationToken = default)
    {
        ValidatePeriod(periodStart, periodEnd);
        ValidateReason(idempotencyKey, nameof(idempotencyKey));
        ValidateReason(reason, nameof(reason));
        var subscription = await GetSubscriptionAsync(workspaceId, subscriptionId, cancellationToken);
        if (subscription.CancelAtPeriodEnd)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.UpdatedAt = DateTime.UtcNow;
            AddLifecycleEvent(subscription, SubscriptionLifecycleEventType.Cancelled, "payment", reason);
            await db.SaveChangesAsync(cancellationToken);
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
        return subscription;
    }

    public async Task<PaymentRefund> RecordRefundAsync(Guid workspaceId, Guid paymentAttemptId, string provider, decimal amount, string currency, string idempotencyKey, string reason, string? providerRefundReference = null, CancellationToken cancellationToken = default)
    {
        ValidateReason(provider, nameof(provider));
        ValidateReason(idempotencyKey, nameof(idempotencyKey));
        ValidateReason(reason, nameof(reason));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var existing = await db.PaymentRefunds.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return existing;
        if (!string.IsNullOrWhiteSpace(providerRefundReference))
        {
            var duplicateReference = await db.PaymentRefunds.SingleOrDefaultAsync(item => item.Provider == provider && item.ProviderRefundReference == providerRefundReference, cancellationToken);
            if (duplicateReference is not null) return duplicateReference;
        }
        var attempt = await GetAttemptAsync(workspaceId, paymentAttemptId, cancellationToken);
        var refundedAmounts = await db.PaymentRefunds
            .Where(item => item.PaymentAttemptId == attempt.Id && item.Status == PaymentRefundStatus.Succeeded)
            .Select(item => item.Amount)
            .ToListAsync(cancellationToken);
        var refundedAmount = refundedAmounts.Sum();
        if (amount > attempt.Amount - refundedAmount)
            throw new InvalidOperationException("The refund exceeds the remaining refundable payment amount.");
        var now = DateTime.UtcNow;
        var refund = new PaymentRefund
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, PaymentAttemptId = attempt.Id, Provider = provider.Trim(),
            ProviderRefundReference = providerRefundReference?.Trim(), Status = PaymentRefundStatus.Succeeded,
            Amount = amount, Currency = currency.Trim().ToUpperInvariant(), IdempotencyKey = idempotencyKey.Trim(),
            Reason = reason.Trim(), CreatedAt = now, UpdatedAt = now, CompletedAt = now,
        };
        db.PaymentRefunds.Add(refund);
        attempt.Status = amount >= attempt.Amount ? PaymentAttemptStatus.Refunded : PaymentAttemptStatus.PartiallyRefunded;
        attempt.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (attempt.CreditLedgerEntryId.HasValue)
        {
            await creditLedger.RefundAsync(workspaceId, attempt.CreditLedgerEntryId.Value, $"refund:{refund.Id}", $"Payment refund {refund.Id}: {reason}", cancellationToken: cancellationToken);
        }
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
        if (provider is null) return new(false, false, "PAYMENT_PROVIDER_UNCONFIGURED", null, "The payment provider is not configured.");
        if (string.IsNullOrWhiteSpace(rawPayload) || !provider.WebhookSignatureVerifier.Verify(rawPayload, signature))
            return new(false, false, "WEBHOOK_SIGNATURE_INVALID", null, "The webhook signature could not be verified.");

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
            return new(true, true, "WEBHOOK_DUPLICATE", duplicate.Id, "The already-recorded payment event was not applied twice.");

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

        try
        {
            switch (parsed.Type)
            {
                case PaymentEventType.PaymentSucceeded:
                case PaymentEventType.RenewalSucceeded when parsed.PaymentAttemptId.HasValue:
                    if (parsed.WorkspaceId.HasValue && parsed.PaymentAttemptId.HasValue)
                    {
                        var attempt = await db.PaymentAttempts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == parsed.PaymentAttemptId.Value && item.WorkspaceId == parsed.WorkspaceId.Value, cancellationToken)
                            ?? throw new InvalidOperationException("The payment attempt referenced by the event was not found.");
                        if (parsed.Amount.HasValue && parsed.Amount.Value != attempt.Amount || !string.Equals(parsed.Currency, attempt.Currency, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("The provider payment amount or currency does not match the recorded payment attempt.");
                        await lifecycle.MarkPaymentSucceededAsync(parsed.WorkspaceId.Value, parsed.PaymentAttemptId.Value, parsed.Reason ?? "Provider payment succeeded.", cancellationToken);
                        if (parsed.Type == PaymentEventType.RenewalSucceeded && parsed.SubscriptionId.HasValue && parsed.PeriodStart.HasValue && parsed.PeriodEnd.HasValue)
                            await lifecycle.RenewSubscriptionAsync(parsed.WorkspaceId.Value, parsed.SubscriptionId.Value, parsed.PeriodStart.Value, parsed.PeriodEnd.Value, $"webhook:{paymentEvent.Id}", parsed.Reason ?? "Provider renewal succeeded.", cancellationToken);
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
            return new(true, false, "WEBHOOK_PROCESSED", paymentEvent.Id, null);
        }
        catch (Exception exception)
        {
            paymentEvent.Status = PaymentEventStatus.Rejected;
            paymentEvent.FailureReason = "The verified event was recorded but its domain transition was not applied.";
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError(exception, "Verified payment webhook could not be applied. EventId={EventId}", paymentEvent.Id);
            return new(false, false, "WEBHOOK_PROCESSING_FAILED", paymentEvent.Id, paymentEvent.FailureReason);
        }
    }
}

public sealed class PaymentReconciliationService(TaslimDbContext db) : IPaymentReconciliationService
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

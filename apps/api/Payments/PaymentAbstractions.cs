using Taslim.Api.Domain;

namespace Taslim.Api.Payments;

public sealed record ProviderCheckoutRequest(
    string IdempotencyKey,
    string PlanCode,
    decimal Amount,
    string Currency,
    string? ProviderCustomerReference,
    string SuccessUrl,
    string CancelUrl);

public sealed record ProviderCheckoutSessionResult(
    string ProviderSessionReference,
    string CheckoutUrl,
    DateTime? ExpiresAt);

public sealed record ProviderPaymentEvent(
    string ProviderEventReference,
    PaymentEventType Type,
    Guid? WorkspaceId,
    Guid? SubscriptionId,
    Guid? CheckoutSessionId,
    Guid? PaymentAttemptId,
    string? ProviderPaymentReference,
    decimal? Amount,
    string Currency,
    DateTime OccurredAt,
    string? Reason,
    string? ProviderSubscriptionReference,
    DateTime? PeriodStart,
    DateTime? PeriodEnd,
    bool CancelAtPeriodEnd);

public interface IWebhookSignatureVerifier
{
    bool Verify(string rawPayload, string signature);
}

public interface IPaymentProvider
{
    string Key { get; }
    IWebhookSignatureVerifier WebhookSignatureVerifier { get; }
    Task<ProviderCheckoutSessionResult> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken cancellationToken = default);
    ProviderPaymentEvent ParseWebhook(string rawPayload);
}

public sealed record CheckoutSessionResult(
    bool Available,
    Guid? CheckoutSessionId,
    string? CheckoutUrl,
    string Code,
    string? Reason);

public interface ICheckoutSessionService
{
    Task<CheckoutSessionResult> CreateAsync(
        Guid workspaceId,
        string planCode,
        string idempotencyKey,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);
}

public sealed record WebhookProcessingResult(
    bool Accepted,
    bool Duplicate,
    string Code,
    Guid? PaymentEventId,
    string? Reason);

public interface IPaymentLifecycleService
{
    Task<PaymentAttempt> RecordPaymentAttemptAsync(
        Guid workspaceId,
        string provider,
        string idempotencyKey,
        decimal amount,
        string currency,
        PaymentAttemptStatus status = PaymentAttemptStatus.Created,
        Guid? subscriptionId = null,
        Guid? checkoutSessionId = null,
        string? providerPaymentReference = null,
        string? failureCode = null,
        string? failureReason = null,
        CancellationToken cancellationToken = default);

    Task<PaymentAttempt> MarkPaymentSucceededAsync(
        Guid workspaceId,
        Guid paymentAttemptId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<PaymentAttempt> MarkPaymentFailedAsync(
        Guid workspaceId,
        Guid paymentAttemptId,
        string failureCode,
        string reason,
        CancellationToken cancellationToken = default);

    Task<Subscription> ActivateSubscriptionAsync(
        Guid workspaceId,
        Guid subscriptionId,
        Guid planId,
        string provider,
        string providerSubscriptionReference,
        DateTime periodStart,
        DateTime periodEnd,
        string reason,
        CancellationToken cancellationToken = default);

    Task<Subscription> RenewSubscriptionAsync(
        Guid workspaceId,
        Guid subscriptionId,
        DateTime periodStart,
        DateTime periodEnd,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken = default);

    Task<PaymentRefund> RecordRefundAsync(
        Guid workspaceId,
        Guid paymentAttemptId,
        string provider,
        decimal amount,
        string currency,
        string idempotencyKey,
        string reason,
        string? providerRefundReference = null,
        CancellationToken cancellationToken = default);

    Task SetCancelAtPeriodEndAsync(Guid workspaceId, Guid subscriptionId, bool cancelAtPeriodEnd, string reason, CancellationToken cancellationToken = default);
    Task CancelSubscriptionAsync(Guid workspaceId, Guid subscriptionId, string reason, CancellationToken cancellationToken = default);
}

public interface IPaymentWebhookService
{
    Task<WebhookProcessingResult> ProcessAsync(
        string providerKey,
        string rawPayload,
        string signature,
        CancellationToken cancellationToken = default);
}

public interface IPaymentReconciliationService
{
    Task<PaymentReconciliationRecord> RecordAsync(
        string provider,
        string providerObjectType,
        string providerObjectReference,
        ReconciliationStatus status,
        string reason,
        Guid? workspaceId = null,
        string? localEntityType = null,
        Guid? localEntityId = null,
        CancellationToken cancellationToken = default);

    Task ResolveAsync(Guid reconciliationId, string reason, CancellationToken cancellationToken = default);
}

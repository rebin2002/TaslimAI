namespace Taslim.Api.Domain;

public enum CheckoutSessionStatus
{
    Created,
    Open,
    Completed,
    Expired,
    Cancelled,
    Failed,
}

public enum PaymentAttemptStatus
{
    Created,
    RequiresAction,
    Authorized,
    Succeeded,
    Failed,
    Cancelled,
    PartiallyRefunded,
    Refunded,
}

public enum PaymentEventType
{
    CheckoutCompleted,
    PaymentSucceeded,
    PaymentFailed,
    SubscriptionActivated,
    SubscriptionUpdated,
    SubscriptionCancelled,
    RenewalSucceeded,
    RenewalFailed,
    RefundIssued,
}

public enum PaymentEventStatus
{
    Received,
    Processed,
    Ignored,
    Rejected,
}

public enum PaymentRefundStatus
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
}

public enum SubscriptionLifecycleEventType
{
    Activated,
    Updated,
    Renewed,
    CancelRequested,
    Cancelled,
    PaymentFailed,
}

public enum ReconciliationStatus
{
    Pending,
    Matched,
    Mismatch,
    Resolved,
}

public sealed class ProviderCustomerReference
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderCustomerReferenceValue { get; set; } = string.Empty;
    public bool IsDefault { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
}

public sealed class CheckoutSession
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PlanId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderSessionReference { get; set; }
    public CheckoutSessionStatus Status { get; set; } = CheckoutSessionStatus.Created;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public Plan Plan { get; set; } = null!;
    public Subscription? Subscription { get; set; }
    public ICollection<PaymentAttempt> PaymentAttempts { get; set; } = [];
}

public sealed class PaymentAttempt
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? CheckoutSessionId { get; set; }
    public Guid? CreditLedgerEntryId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderPaymentReference { get; set; }
    public PaymentAttemptStatus Status { get; set; } = PaymentAttemptStatus.Created;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SucceededAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public Subscription? Subscription { get; set; }
    public CheckoutSession? CheckoutSession { get; set; }
    public CreditLedgerEntry? CreditLedgerEntry { get; set; }
    public ICollection<PaymentRefund> Refunds { get; set; } = [];
}

public sealed class PaymentEvent
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderEventReference { get; set; } = string.Empty;
    public PaymentEventType Type { get; set; }
    public PaymentEventStatus Status { get; set; } = PaymentEventStatus.Received;
    public Guid? WorkspaceId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? CheckoutSessionId { get; set; }
    public Guid? PaymentAttemptId { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public bool SignatureVerified { get; set; }
    public string? Reason { get; set; }
    public string? FailureReason { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public Workspace? Workspace { get; set; }
    public Subscription? Subscription { get; set; }
    public CheckoutSession? CheckoutSession { get; set; }
    public PaymentAttempt? PaymentAttempt { get; set; }
}

public sealed class PaymentRefund
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PaymentAttemptId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ProviderRefundReference { get; set; }
    public PaymentRefundStatus Status { get; set; } = PaymentRefundStatus.Pending;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public PaymentAttempt PaymentAttempt { get; set; } = null!;
}

public sealed class SubscriptionLifecycleEvent
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid SubscriptionId { get; set; }
    public SubscriptionLifecycleEventType Type { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? ProviderEventReference { get; set; }
    public DateTime CreatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public Subscription Subscription { get; set; } = null!;
}

public sealed class PaymentReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderObjectType { get; set; } = string.Empty;
    public string ProviderObjectReference { get; set; } = string.Empty;
    public string? LocalEntityType { get; set; }
    public Guid? LocalEntityId { get; set; }
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Pending;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Workspace? Workspace { get; set; }
}

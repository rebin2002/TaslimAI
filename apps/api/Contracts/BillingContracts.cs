namespace Taslim.Api.Contracts;

public sealed record BillingPlanDto(string Code, string Name, decimal MonthlyPriceUsd, long MonthlyCreditAllowance, string Currency);
public sealed record BillingPlanOptionDto(string Code, string Name, decimal MonthlyPriceUsd, long MonthlyCreditAllowance, string Currency, bool IsCurrent);
public sealed record BillingSubscriptionDto(string Status, DateTime CurrentPeriodStart, DateTime CurrentPeriodEnd, DateTime NextRenewalAt, bool CancelAtPeriodEnd);
public sealed record BillingPeriodDto(Guid Id, string Status, DateTime StartsAt, DateTime EndsAt, long IncludedCredits);
public sealed record BillingCreditBalanceDto(long IncludedGranted, long IncludedRemaining, long PurchasedRemaining, long AdjustmentBalance, long TotalRemaining);
public sealed record BillingPaymentStatusDto(string Status, string? Provider, string? LastFailureReason, DateTime? LastPaymentAt);
public sealed record BillingActionsDto(bool CheckoutAvailable, bool UpgradeAvailable, bool DowngradeAvailable, bool CancelAvailable, string DisabledReason);
public sealed record CreditLedgerEntryDto(Guid Id, string Type, long Amount, string Reason, DateTime CreatedAt);
public sealed record BillingAccountDto(
    BillingPlanDto CurrentPlan,
    BillingSubscriptionDto Subscription,
    BillingPeriodDto BillingPeriod,
    BillingCreditBalanceDto Credits,
    IReadOnlyList<CreditLedgerEntryDto> Transactions,
    IReadOnlyList<BillingPlanOptionDto> AvailablePlans,
    BillingPaymentStatusDto PaymentStatus,
    BillingActionsDto Actions);

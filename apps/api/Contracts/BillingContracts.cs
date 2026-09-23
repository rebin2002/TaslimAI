namespace Taslim.Api.Contracts;

public sealed record BillingPlanDto(string Code, string Name, decimal MonthlyPriceUsd, long MonthlyCreditAllowance, string Currency);
public sealed record BillingSubscriptionDto(string Status, DateTime CurrentPeriodStart, DateTime CurrentPeriodEnd, DateTime NextRenewalAt, bool CancelAtPeriodEnd);
public sealed record BillingPeriodDto(Guid Id, string Status, DateTime StartsAt, DateTime EndsAt, long IncludedCredits);
public sealed record BillingCreditBalanceDto(long IncludedGranted, long IncludedRemaining, long PurchasedRemaining, long AdjustmentBalance, long TotalRemaining);
public sealed record CreditLedgerEntryDto(Guid Id, string Type, long Amount, string Reason, DateTime CreatedAt);
public sealed record BillingAccountDto(
    BillingPlanDto CurrentPlan,
    BillingSubscriptionDto Subscription,
    BillingPeriodDto BillingPeriod,
    BillingCreditBalanceDto Credits,
    IReadOnlyList<CreditLedgerEntryDto> Transactions,
    bool UpgradeAvailable);

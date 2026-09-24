namespace Taslim.Api.Domain;

public enum SubscriptionStatus
{
    Trialing,
    Active,
    PastDue,
    Paused,
    Cancelled,
}

public enum BillingPeriodStatus
{
    Open,
    Closed,
    Expired,
}

public enum CreditEntitlementType
{
    IncludedMonthly,
    Purchased,
    AdministrativeCorrection,
}

public enum CreditLedgerEntryType
{
    Grant,
    Debit,
    Refund,
    Reversal,
    AdministrativeCorrection,
    Expiry,
}

public sealed class Plan
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal MonthlyPriceUsd { get; set; }
    public long MonthlyCreditAllowance { get; set; }
    public string Currency { get; set; } = "USD";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<Subscription> Subscriptions { get; set; } = [];
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid PlanId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public string? BillingProvider { get; set; }
    public string? ProviderSubscriptionReference { get; set; }
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public DateTime NextRenewalAt { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public Plan Plan { get; set; } = null!;
    public ICollection<BillingPeriod> BillingPeriods { get; set; } = [];
}

public sealed class BillingPeriod
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public BillingPeriodStatus Status { get; set; } = BillingPeriodStatus.Open;
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public long IncludedCredits { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Subscription Subscription { get; set; } = null!;
    public ICollection<CreditEntitlement> CreditEntitlements { get; set; } = [];
}

public sealed class CreditEntitlement
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? BillingPeriodId { get; set; }
    public CreditEntitlementType Type { get; set; }
    public long GrantedCredits { get; set; }
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public DateTime CreatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public BillingPeriod? BillingPeriod { get; set; }
    public ICollection<CreditLedgerEntry> LedgerEntries { get; set; } = [];
}

public sealed class CreditLedgerEntry
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid? CreditEntitlementId { get; set; }
    public Guid? UsageTransactionId { get; set; }
    public Guid? ReversesEntryId { get; set; }
    public CreditLedgerEntryType Type { get; set; }
    public long Amount { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public Guid? ActorUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Workspace Workspace { get; set; } = null!;
    public CreditEntitlement? CreditEntitlement { get; set; }
    public UsageTransaction? UsageTransaction { get; set; }
}

public static class DefaultPlanCatalog
{
    public static readonly Guid FreeId = Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100");
    public static readonly Guid ProId = Guid.Parse("1f1e1d1c-1b1a-1918-1716-151413121110");
    public static readonly Guid UltraId = Guid.Parse("2f2e2d2c-2b2a-2928-2726-252423222120");
    public static readonly Guid MegaId = Guid.Parse("3f3e3d3c-3b3a-3938-3736-353433323130");
    public static readonly Guid BusinessId = Guid.Parse("4f4e4d4c-4b4a-4948-4746-454443424140");

    public static readonly IReadOnlyList<Plan> All =
    [
        new() { Id = FreeId, Code = "free", Name = "Free", Description = "A no-cost Taslim foundation plan.", MonthlyPriceUsd = 0m, MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 1 },
        new() { Id = ProId, Code = "pro", Name = "Pro", Description = "For regular individual work.", MonthlyPriceUsd = 9m, MonthlyCreditAllowance = 10_000, Currency = "USD", IsActive = true, SortOrder = 2 },
        new() { Id = UltraId, Code = "ultra", Name = "Ultra", Description = "For heavier individual usage.", MonthlyPriceUsd = 19m, MonthlyCreditAllowance = 30_000, Currency = "USD", IsActive = true, SortOrder = 3 },
        new() { Id = MegaId, Code = "mega", Name = "Mega", Description = "For high-volume creative work.", MonthlyPriceUsd = 29m, MonthlyCreditAllowance = 60_000, Currency = "USD", IsActive = true, SortOrder = 4 },
        new() { Id = BusinessId, Code = "business", Name = "Business", Description = "For teams and business workspaces.", MonthlyPriceUsd = 59m, MonthlyCreditAllowance = 150_000, Currency = "USD", IsActive = true, SortOrder = 5 },
    ];
}

public sealed class BillingOptions
{
    // Keep false until a reviewed payment/charging launch explicitly enables it.
    public bool CustomerChargingEnabled { get; set; }
    // Deliberately unconfigured until credentials and a launch decision exist.
    public string Provider { get; set; } = "unconfigured";
}

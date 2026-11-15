using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;
namespace Taslim.Api.Tests;
/// <summary>
/// Contract tests for the credit ledger history returned by
/// <see cref="BillingAccountService"/>. The Account &gt; Billing screen renders
/// `transactions` as the workspace's credit history and shows "No transactions
/// yet" when the list is empty, so the list is a display claim, not a balance
/// claim. Spendable balances must keep honouring entitlement expiry; visible
/// history must not silently drop movements that were never deleted.
/// </summary>
public sealed class BillingCreditHistoryTests
{
    [Fact]
    public async Task Expired_included_allowance_still_reports_its_grant_in_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var periodStart = now.Date.AddMonths(-1);
        var periodEnd = now.Date;
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Expired Allowance Workspace", Slug = $"expired-allowance-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"expired-allowance-{Guid.NewGuid():N}", Name = "Expired Allowance Test", MonthlyPriceUsd = 0m, MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 98, CreatedAt = now, UpdatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodStart = periodStart, CurrentPeriodEnd = periodEnd, NextRenewalAt = periodEnd, CreatedAt = now, UpdatedAt = now };
        var period = new BillingPeriod { Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Expired, StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = 1_000, CreatedAt = periodStart, ClosedAt = periodEnd };
        var entitlement = new CreditEntitlement { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, BillingPeriodId = period.Id, Type = CreditEntitlementType.IncludedMonthly, GrantedCredits = 1_000, GrantedAt = periodStart, ExpiresAt = periodEnd, IdempotencyKey = "included:expired-allowance", CreatedAt = periodStart };
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 1_000, IdempotencyKey = "expired-allowance:grant",
            Reason = "Monthly allowance", CreatedAt = periodStart,
        });
        await db.SaveChangesAsync();
        var service = new BillingAccountService(
            db,
            new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));
        var account = await service.GetAccountAsync(workspace.Id);
        // Expiry is a balance fact: the aged-out allowance is no longer spendable.
        Assert.Equal(0, account.Credits.IncludedRemaining);
        Assert.Equal(0, account.Credits.TotalRemaining);
        // ...but the grant still exists in the ledger, so the history must show it.
        Assert.Single(account.Transactions);
        var entry = Assert.Single(account.Transactions);
        Assert.Equal("Grant", entry.Type);
        Assert.Equal(1_000, entry.Amount);
        Assert.Equal("Monthly allowance", entry.Reason);
    }
    [Fact]
    public async Task History_is_complete_when_only_some_entitlements_have_expired()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var periodStart = now.Date.AddMonths(-1);
        var periodEnd = now.Date;
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Mixed Allowance Workspace", Slug = $"mixed-allowance-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"mixed-allowance-{Guid.NewGuid():N}", Name = "Mixed Allowance Test", MonthlyPriceUsd = 0m, MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 97, CreatedAt = now, UpdatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodStart = periodStart, CurrentPeriodEnd = periodEnd, NextRenewalAt = periodEnd, CreatedAt = now, UpdatedAt = now };
        var period = new BillingPeriod { Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Expired, StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = 1_000, CreatedAt = periodStart, ClosedAt = periodEnd };
        var expired = new CreditEntitlement { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, BillingPeriodId = period.Id, Type = CreditEntitlementType.IncludedMonthly, GrantedCredits = 1_000, GrantedAt = periodStart, ExpiresAt = periodEnd, IdempotencyKey = "included:mixed-expired", CreatedAt = periodStart };
        var active = new CreditEntitlement { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, BillingPeriodId = period.Id, Type = CreditEntitlementType.Purchased, GrantedCredits = 500, GrantedAt = periodEnd, ExpiresAt = null, IdempotencyKey = "purchased:mixed-active", CreatedAt = now };
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        db.CreditEntitlements.Add(expired);
        db.CreditEntitlements.Add(active);
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = expired.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 1_000, IdempotencyKey = "mixed:grant",
            Reason = "Monthly allowance", CreatedAt = periodStart,
        });
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = active.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 500, IdempotencyKey = "mixed:purchase",
            Reason = "Credit purchase", CreatedAt = now,
        });
        await db.SaveChangesAsync();
        var service = new BillingAccountService(
            db,
            new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));
        var account = await service.GetAccountAsync(workspace.Id);
        // Only the live entitlement counts as spendable.
        Assert.Equal(500, account.Credits.PurchasedRemaining);
        Assert.Equal(500, account.Credits.TotalRemaining);
        // Both movements are durable ledger records and both must be displayed.
        Assert.Equal(2, account.Transactions.Count);
        Assert.Equal(new[] { "Credit purchase", "Monthly allowance" }, account.Transactions.Select(item => item.Reason).ToArray());
    }
    [Fact]
    public async Task History_cap_and_newest_first_order_still_apply_across_expired_entitlements()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var periodStart = now.Date.AddMonths(-1);
        var periodEnd = now.Date;
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Capped History Workspace", Slug = $"capped-history-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"capped-history-{Guid.NewGuid():N}", Name = "Capped History Test", MonthlyPriceUsd = 0m, MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 96, CreatedAt = now, UpdatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodStart = periodStart, CurrentPeriodEnd = periodEnd, NextRenewalAt = periodEnd, CreatedAt = now, UpdatedAt = now };
        var period = new BillingPeriod { Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Expired, StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = 1_000, CreatedAt = periodStart, ClosedAt = periodEnd };
        var entitlement = new CreditEntitlement { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, BillingPeriodId = period.Id, Type = CreditEntitlementType.IncludedMonthly, GrantedCredits = 1_000, GrantedAt = periodStart, ExpiresAt = periodEnd, IdempotencyKey = "included:capped", CreatedAt = periodStart };
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 1_000, IdempotencyKey = "capped:grant",
            Reason = "Monthly allowance", CreatedAt = periodStart,
        });
        for (var index = 0; index < 120; index++)
        {
            db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
                Type = CreditLedgerEntryType.Refund, Amount = 1, IdempotencyKey = $"capped:refund:{index}",
                Reason = $"Goodwill refund {index}", CreatedAt = periodStart.AddMinutes(index),
            });
        }
        await db.SaveChangesAsync();
        var service = new BillingAccountService(
            db,
            new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));
        var account = await service.GetAccountAsync(workspace.Id);
        Assert.Equal(0, account.Credits.TotalRemaining);
        // The intentional 100-row display cap and newest-first ordering are unchanged.
        Assert.Equal(100, account.Transactions.Count);
        Assert.Equal("Goodwill refund 119", account.Transactions[0].Reason);
        Assert.True(account.Transactions[0].CreatedAt >= account.Transactions[^1].CreatedAt);
    }
}

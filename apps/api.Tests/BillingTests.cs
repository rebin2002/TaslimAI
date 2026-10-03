using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class BillingTests
{
    [Fact]
    public async Task Grant_is_idempotent_and_reverse_creates_an_auditable_opposite_entry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Billing Workspace", Slug = $"billing-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));

        var first = await service.GrantAsync(workspace.Id, CreditEntitlementType.AdministrativeCorrection, 25, "correction:one", "Goodwill correction");
        var duplicate = await service.GrantAsync(workspace.Id, CreditEntitlementType.AdministrativeCorrection, 25, "correction:one", "Goodwill correction");
        var reversal = await service.ReverseAsync(workspace.Id, first.Entry.Id, "reversal:one", "Correction withdrawn");

        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Entry.Id, duplicate.Entry.Id);
        Assert.True(reversal.Created);
        Assert.Equal(-25, reversal.Entry.Amount);
        Assert.Equal(first.Entry.Id, reversal.Entry.ReversesEntryId);
        Assert.Equal(2, await db.CreditLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Usage_debit_is_not_written_while_customer_charging_is_disabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Disabled Billing Workspace", Slug = $"disabled-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));

        var result = await service.RecordUsageDebitAsync(workspace.Id, Guid.NewGuid(), 10, "usage:disabled", "AI usage");

        Assert.Null(result);
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task Billing_balance_uses_the_full_ledger_not_only_the_visible_history_page()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Balance Workspace", Slug = $"balance-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"balance-{Guid.NewGuid():N}", Name = "Balance Test", MonthlyCreditAllowance = 1_000, Currency = "USD", IsActive = true, SortOrder = 99, CreatedAt = now, UpdatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodStart = now.Date, CurrentPeriodEnd = now.Date.AddMonths(1), NextRenewalAt = now.Date.AddMonths(1), CreatedAt = now, UpdatedAt = now };
        var period = new BillingPeriod { Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Open, StartsAt = now.Date, EndsAt = now.Date.AddMonths(1), IncludedCredits = 1_000, CreatedAt = now };
        var entitlement = new CreditEntitlement { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, BillingPeriodId = period.Id, Type = CreditEntitlementType.IncludedMonthly, GrantedCredits = 1_000, GrantedAt = now, ExpiresAt = now.AddMonths(1), IdempotencyKey = "included:balance", CreatedAt = now };
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 1_000, IdempotencyKey = "balance:grant",
            Reason = "Monthly allowance", CreatedAt = now.AddMinutes(-200),
        });
        for (var index = 0; index < 100; index++)
        {
            db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
                Type = CreditLedgerEntryType.Debit, Amount = -1, IdempotencyKey = $"balance:debit:{index}",
                Reason = "Usage", CreatedAt = now.AddMinutes(-100 + index),
            });
        }
        await db.SaveChangesAsync();

        var service = new BillingAccountService(
            db,
            new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));
        var account = await service.GetAccountAsync(workspace.Id);

        Assert.Equal(900, account.Credits.IncludedRemaining);
        Assert.Equal(900, account.Credits.TotalRemaining);
        Assert.Equal(100, account.Transactions.Count);
    }
}

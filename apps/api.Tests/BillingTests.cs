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
    public async Task Grant_replay_rejects_changes_to_entitlement_metadata_or_audit_attribution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Grant Replay Workspace", Slug = $"grant-replay-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));
        var actorUserId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(30);
        await service.GrantAsync(
            workspace.Id,
            CreditEntitlementType.Purchased,
            25,
            "grant:metadata",
            "Purchased credits",
            expiresAt,
            billingPeriodId: null,
            actorUserId: actorUserId,
            sourceReference: "provider:purchase-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(workspace.Id, CreditEntitlementType.Purchased, 25, "grant:metadata", "Purchased credits", expiresAt.AddDays(1), actorUserId: actorUserId, sourceReference: "provider:purchase-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(workspace.Id, CreditEntitlementType.Purchased, 25, "grant:metadata", "Purchased credits", expiresAt, billingPeriodId: Guid.NewGuid(), actorUserId: actorUserId, sourceReference: "provider:purchase-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(workspace.Id, CreditEntitlementType.Purchased, 25, "grant:metadata", "Purchased credits", expiresAt, actorUserId: actorUserId, sourceReference: "provider:purchase-2"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GrantAsync(workspace.Id, CreditEntitlementType.Purchased, 25, "grant:metadata", "Purchased credits", expiresAt, actorUserId: Guid.NewGuid(), sourceReference: "provider:purchase-1"));

        Assert.Single(await db.CreditEntitlements.ToListAsync());
        Assert.Single(await db.CreditLedgerEntries.ToListAsync());
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

    [Fact]
    public async Task Usage_debit_requires_a_completed_billable_transaction_in_the_same_workspace()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = NewWorkspace("usage");
        var otherWorkspace = NewWorkspace("other");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = "billing-usage@example.com", NormalizedUserName = "BILLING-USAGE@EXAMPLE.COM",
            Email = "billing-usage@example.com", NormalizedEmail = "BILLING-USAGE@EXAMPLE.COM", DisplayName = "Billing Usage",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workspaces.AddRange(workspace, otherWorkspace);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var completed = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = user.Id, RequestId = "billable-usage",
            Feature = UsageFeature.Generation, Provider = "test", Model = "test", Status = UsageTransactionStatus.Completed,
            IsBillable = true, CreatedAt = DateTime.UtcNow,
        };
        var otherWorkspaceUsage = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = otherWorkspace.Id, UserId = user.Id, RequestId = "other-usage",
            Feature = UsageFeature.Generation, Provider = "test", Model = "test", Status = UsageTransactionStatus.Completed,
            IsBillable = true, CreatedAt = DateTime.UtcNow,
        };
        db.UsageTransactions.AddRange(completed, otherWorkspaceUsage);
        var entitlement = new CreditEntitlement
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Type = CreditEntitlementType.Purchased, GrantedCredits = 10,
            GrantedAt = DateTime.UtcNow, IdempotencyKey = "purchased:usage", CreatedAt = DateTime.UtcNow,
        };
        db.CreditEntitlements.Add(entitlement);
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, CreditEntitlementId = entitlement.Id,
            Type = CreditLedgerEntryType.Grant, Amount = 10, IdempotencyKey = "grant:usage", Reason = "Test credits", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true }));

        var first = await service.RecordUsageDebitAsync(workspace.Id, completed.Id, 4, "usage:one", "Billable usage");
        var duplicate = await service.RecordUsageDebitAsync(workspace.Id, completed.Id, 4, "usage:one", "Billable usage");

        Assert.NotNull(first);
        Assert.NotNull(duplicate);
        Assert.False(duplicate!.Created);
        Assert.Equal(first!.Entry.Id, duplicate.Entry.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordUsageDebitAsync(workspace.Id, otherWorkspaceUsage.Id, 1, "usage:cross-workspace", "Cross-workspace usage"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordUsageDebitAsync(workspace.Id, completed.Id, 1, "usage:second-debit", "Duplicate usage debit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordUsageDebitAsync(workspace.Id, completed.Id, 7, "usage:overdraw", "Overdraw attempt"));
        Assert.Equal(1, await db.CreditLedgerEntries.CountAsync(item => item.Type == CreditLedgerEntryType.Debit));
    }

    [Fact]
    public async Task Reversal_is_append_only_but_cannot_be_applied_twice()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var workspace = NewWorkspace("reverse-once");
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));

        var grant = await service.GrantAsync(workspace.Id, CreditEntitlementType.AdministrativeCorrection, 25, "correction:once", "Goodwill correction");
        await service.ReverseAsync(workspace.Id, grant.Entry.Id, "reversal:once", "Correction withdrawn");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReverseAsync(workspace.Id, grant.Entry.Id, "reversal:twice", "Duplicate correction withdrawal"));
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Type = CreditLedgerEntryType.Refund, Amount = 25,
            ReversesEntryId = grant.Entry.Id, IdempotencyKey = "refund:duplicate-link", Reason = "Duplicate reversal link", CreatedAt = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(2, await db.CreditLedgerEntries.CountAsync());
    }

    private static Workspace NewWorkspace(string prefix) => new()
    {
        Id = Guid.NewGuid(), Name = $"{prefix} workspace", Slug = $"{prefix}-{Guid.NewGuid():N}", Type = WorkspaceType.Personal,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };
}

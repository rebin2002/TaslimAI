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
/// Issue #330 QA lane (test-only): a grant reversed through the production
/// <see cref="CreditLedgerService.ReverseAsync"/> path carries the granted
/// entitlement's id on the compensating row. That attribution must survive the
/// entitlement later expiring: the aged-out grant/reversal pair must stay
/// paired on the same dead lot (so it can never silently subtract from, or
/// double-count into, a newer active lot), remain fully visible in the
/// Account &gt; Billing history read model (#761 semantics), and keep the
/// displayed balance identical to the debit-admission sum.
/// Distinct from every existing lane: #690 pins expiry isolation for
/// unallocated usage debits and post-expiry debit refunds, #777 pins the
/// reversal ceiling for a fully consumed expired lot, #853 pins usage-debit
/// lot attribution, and #761 pins visibility of expired grant rows only.
/// No open lane pins the attribution of a reversed GRANT across expiry.
/// All fixtures are synthetic and workspace-scoped; nothing here enables
/// production charging (the default <c>CustomerChargingEnabled=false</c> is
/// what runs the grants and the reversal), no provider, payment, migration,
/// or shared database is touched. The serializable admission sum is replayed
/// here only to document the expected parity; the assertions go through the
/// real service, so a projection/admission split would fail this test.
/// </summary>
public sealed class BillingGrantReversalExpiryAttributionTests
{
    [Fact]
    public async Task Sqlite_reversed_grant_attribution_survives_expiry_for_read_model_and_admission()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var periodStart = now.Date.AddMonths(-1);
        var periodEnd = now.Date;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(), Name = "Grant Reversal Expiry Workspace",
            Slug = $"grant-reversal-expiry-{Guid.NewGuid():N}", Type = WorkspaceType.Personal,
            CreatedAt = now, UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = "grant-reversal-expiry@example.com",
            NormalizedUserName = "GRANT-REVERSAL-EXPIRY@EXAMPLE.COM",
            Email = "grant-reversal-expiry@example.com", NormalizedEmail = "GRANT-REVERSAL-EXPIRY@EXAMPLE.COM",
            DisplayName = "Grant Reversal Expiry", CreatedAt = now, UpdatedAt = now,
        };
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"grant-reversal-expiry-{Guid.NewGuid():N}", Name = "Grant Reversal Expiry Test",
            MonthlyPriceUsd = 0m, MonthlyCreditAllowance = 1_000, Currency = "USD",
            IsActive = true, SortOrder = 95, CreatedAt = now, UpdatedAt = now,
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = periodStart, CurrentPeriodEnd = periodEnd, NextRenewalAt = periodEnd,
            CreatedAt = now, UpdatedAt = now,
        };
        var period = new BillingPeriod
        {
            Id = Guid.NewGuid(), SubscriptionId = subscription.Id, Status = BillingPeriodStatus.Expired,
            StartsAt = periodStart, EndsAt = periodEnd, IncludedCredits = 1_000, CreatedAt = periodStart, ClosedAt = periodEnd,
        };
        db.Workspaces.Add(workspace);
        db.Users.Add(user);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        db.BillingPeriods.Add(period);
        await db.SaveChangesAsync();

        // Grants and reversals never require charging; the production default
        // (CustomerChargingEnabled=false) is the instance that writes them.
        var service = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false }));

        var lotA = await service.GrantAsync(
            workspace.Id, CreditEntitlementType.Purchased, 40,
            "expiryattribution:lotA:grant", "Synthetic first-period purchase",
            expiresAt: now.AddHours(1));
        var lotAId = lotA.Entry.CreditEntitlementId!.Value;
        var reversal = await service.ReverseAsync(
            workspace.Id, lotA.Entry.Id, "expiryattribution:lotA:reversal", "Admin reversal of synthetic purchase");
        Assert.True(lotA.Created);
        Assert.True(reversal.Created);
        Assert.Equal(-40, reversal.Entry.Amount);
        Assert.Equal(lotAId, reversal.Entry.CreditEntitlementId);

        var reversalBeforeExpiry = await CaptureAttributionSnapshotAsync(db, reversal.Entry.Id);

        // Simulate the calendar crossing the lot's expiry, exactly as the
        // #690 contract fixture does: move the entitlement clock, mutate
        // nothing else.
        var agingEntitlement = await db.CreditEntitlements.FirstAsync(item => item.Id == lotAId);
        agingEntitlement.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();

        // (1) The compensating row's attribution survives expiry untouched.
        var reversalAfterExpiry = await CaptureAttributionSnapshotAsync(db, reversal.Entry.Id);
        Assert.Equal(reversalBeforeExpiry, reversalAfterExpiry);

        // A later active purchase of the same category must see the full
        // amount spendable: the expired +40/-40 pair sits on the dead lot.
        var lotB = await service.GrantAsync(
            workspace.Id, CreditEntitlementType.Purchased, 40,
            "expiryattribution:lotB:grant", "Synthetic regrant",
            expiresAt: DateTime.UtcNow.AddDays(30));
        Assert.True(lotB.Created);

        // (2) Read model: history keeps every movement (including the expired
        // lot's reversal row) newest-first, while the balance counts only the
        // live lot.
        var accountService = new BillingAccountService(
            db,
            new BillingProvisioningService(db, NullLogger<BillingProvisioningService>.Instance),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }));
        var account = await accountService.GetAccountAsync(workspace.Id);

        Assert.Equal(
            new[]
            {
                (Type: "Grant", Amount: 40L, Reason: "Synthetic regrant"),
                (Type: "Reversal", Amount: -40L, Reason: "Admin reversal of synthetic purchase"),
                (Type: "Grant", Amount: 40L, Reason: "Synthetic first-period purchase"),
            },
            account.Transactions.Select(item => (Type: item.Type, Amount: item.Amount, Reason: item.Reason)).ToArray());
        Assert.True(account.Transactions[0].CreatedAt >= account.Transactions[1].CreatedAt);
        Assert.True(account.Transactions[1].CreatedAt >= account.Transactions[2].CreatedAt);
        Assert.Equal(0, account.Credits.IncludedRemaining);
        Assert.Equal(40, account.Credits.PurchasedRemaining);
        Assert.Equal(0, account.Credits.AdjustmentBalance);
        Assert.Equal(40, account.Credits.TotalRemaining);

        // (3) Admission parity: the serializable admission sum agrees with the
        // displayed 40. Only this synthetic debit leg needs charging enabled;
        // no production setting, provider, or payment path is involved.
        var firstUsage = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = user.Id, RequestId = "expiryattribution-usage-1",
            Feature = UsageFeature.Generation, Provider = "test", Model = "test",
            Status = UsageTransactionStatus.Completed, IsBillable = true, CreatedAt = DateTime.UtcNow,
        };
        var secondUsage = new UsageTransaction
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = user.Id, RequestId = "expiryattribution-usage-2",
            Feature = UsageFeature.Generation, Provider = "test", Model = "test",
            Status = UsageTransactionStatus.Completed, IsBillable = true, CreatedAt = DateTime.UtcNow,
        };
        db.UsageTransactions.AddRange(firstUsage, secondUsage);
        await db.SaveChangesAsync();

        var admissionLedger = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true }));
        var debit = await admissionLedger.RecordUsageDebitAsync(
            workspace.Id, firstUsage.Id, 40, "expiryattribution:usage:debit", "Synthetic billable usage");
        Assert.NotNull(debit);
        Assert.True(debit!.Created);

        // The expired lot cannot mint a second spendable 40: exactly lotB is left.
        await Assert.ThrowsAsync<InvalidOperationException>(() => admissionLedger.RecordUsageDebitAsync(
            workspace.Id, secondUsage.Id, 1, "expiryattribution:usage:overdraw", "Overdraw attempt after lotB spent"));

        // (4) After spending lotB, display and admission both land on zero:
        // still consistent, and the attribution row is still untouched.
        var spentAccount = await accountService.GetAccountAsync(workspace.Id);
        Assert.Equal(0, spentAccount.Credits.TotalRemaining);
        Assert.Equal(reversalBeforeExpiry, await CaptureAttributionSnapshotAsync(db, reversal.Entry.Id));

        // Append-only, mutation-free invariants of the whole scenario.
        Assert.Equal(2, await db.CreditEntitlements.CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(4, await db.CreditLedgerEntries.CountAsync(item => item.WorkspaceId == workspace.Id));
        Assert.Equal(0, await db.PaymentAttempts.CountAsync(item => item.WorkspaceId == workspace.Id));
    }

    private static async Task<(Guid Id, Guid? EntitlementId, string Type, long Amount, string IdempotencyKey, string Reason, Guid? ReversesEntryId, Guid? UsageTransactionId, Guid? ActorUserId, DateTime CreatedAt)> CaptureAttributionSnapshotAsync(
        TaslimDbContext db, Guid entryId)
    {
        var entry = await db.CreditLedgerEntries.AsNoTracking().FirstAsync(item => item.Id == entryId);
        return (entry.Id, entry.CreditEntitlementId, entry.Type.ToString(), entry.Amount, entry.IdempotencyKey, entry.Reason, entry.ReversesEntryId, entry.UsageTransactionId, entry.ActorUserId, entry.CreatedAt);
    }
}

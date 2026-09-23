using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
}

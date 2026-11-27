using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Covers filtered partial refunds at the smallest ledger unit, cancellation costs,
/// and the deterministic top-20 boundary when users tie on both cost and name.
/// All transactions are synthetic, and these read-side tests exercise no billing actions.
/// </summary>
public sealed class AdminUsageReportExactnessBoundaryTests
{
    // Same stored-scale constant as the #1145 class so the arithmetic provenance matches.
    private const decimal LedgerRowCostUsd = 0.12345679m;
    private const decimal ExactLedgerTotalUsd = 7.90123456m;

    private static readonly DateTime WindowStart = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Filtered_partial_refunds_preserve_the_smallest_positive_residual()
    {
        await using var fixture = await BoundaryFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        var otherWorkspace = fixture.AddWorkspace("Other Workspace");
        var user = fixture.Users[0];

        // Both reversals are valid. The matching rows leave one and two units at the
        // ledger's eight-decimal scale. Each other row differs in exactly one filter.
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Refunded, LedgerRowCostUsd, 1m, 0.99999999m);
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Refunded, LedgerRowCostUsd, 1m, 0.99999998m);
        fixture.AddTransaction(otherWorkspace, user, UsageTransactionStatus.Refunded, 5m, 5m, 1m);
        fixture.AddTransaction(workspace, fixture.Users[1], UsageTransactionStatus.Refunded, 5m, 5m, 1m);
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Completed, 5m, 5m, 0m);
        await fixture.SaveChangesAsync();

        var filter = fixture.FullRange with { WorkspaceId = workspace.Id, UserId = user.Id, Status = UsageTransactionStatus.Refunded };
        var report = await fixture.Service.GetReportAsync(filter);

        const decimal expectedNet = 0.00000003m;
        Assert.Equal(2, report.Summary.TransactionCount);
        Assert.Equal(expectedNet, report.Summary.TotalCustomerChargesUsd);
        Assert.Equal(2 * LedgerRowCostUsd, report.Summary.TotalProviderCostUsd);
        var status = Assert.Single(report.Breakdowns.ByStatus);
        Assert.Equal(nameof(UsageTransactionStatus.Refunded), status.Status);
        Assert.Equal(expectedNet, status.CustomerChargesUsd);
        Assert.Equal(2 * LedgerRowCostUsd, status.ProviderCostUsd);
        Assert.Equal(expectedNet, Assert.Single(report.Breakdowns.ByDay).CustomerChargesUsd);
        Assert.Equal(expectedNet, Assert.Single(report.Breakdowns.ByUser).CustomerChargesUsd);
        Assert.Equal(expectedNet, Assert.Single(report.Breakdowns.ByWorkspace).CustomerChargesUsd);
        Assert.Equal(expectedNet, Assert.Single(report.Breakdowns.ByFeature).CustomerChargesUsd);
        Assert.Equal(new[] { 0.00000001m, 0.00000002m }, report.Transactions.Items.Select(item => item.ChargedAmount).OrderBy(amount => amount));

        var json = JsonSerializer.Serialize(report.Summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"totalCustomerChargesUsd\":0.00000003", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fix orders the status breakdown by <c>item.Status, Ordinal</c> on the
    /// displayed name, and no existing test asserts that leg. It also pins that the
    /// cancellation leg reports zero net charge against a positive provider cost -
    /// the one status combination (cost without revenue) that the #1145 class never
    /// seeds - so a future "improvement" that floors or drops it is caught here.
    /// </summary>
    [Fact]
    public async Task Status_rank_orders_ordinally_and_keeps_cancelled_zero_net_with_positive_cost()
    {
        await using var fixture = await BoundaryFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        var user = fixture.Users[0];

        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Completed, 2.00m, 2.00m, 0m);
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Cancelled, ExactLedgerTotalUsd, 0m, 0m);
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Failed, 1.00m, 0m, 0m);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        // StringComparer.Ordinal on the enum names: Cancelled < Completed < Failed.
        Assert.Equal(
            new[] { nameof(UsageTransactionStatus.Cancelled), nameof(UsageTransactionStatus.Completed), nameof(UsageTransactionStatus.Failed) },
            report.Breakdowns.ByStatus.Select(item => item.Status));

        var cancelled = Assert.Single(report.Breakdowns.ByStatus, item => item.Status == nameof(UsageTransactionStatus.Cancelled));
        Assert.Equal(0m, cancelled.CustomerChargesUsd);
        Assert.Equal(ExactLedgerTotalUsd, cancelled.ProviderCostUsd);
        Assert.Equal(1, cancelled.CancelledCount);
    }

    /// <summary>
    /// <c>ReportRankLimit</c> guards two truncation sites. The #1145 class proves
    /// deterministic membership for the workspace rank only; this pins the second,
    /// per-user rank: 21 identical-cost users, one cut off, and the surviving set and
    /// order must be fixed (ordinal display name, then email, then id), not storage
    /// order, across repeated identical requests.
    /// </summary>
    [Fact]
    public async Task Truncated_user_rank_keeps_a_deterministic_membership_when_tied()
    {
        await using var fixture = await BoundaryFixture.CreateAsync(userCount: 21);
        var workspace = fixture.Workspaces[0];

        // At the cutoff three users share a display name. The user with the lowest
        // identifier has an email; the two users without email must rank first, by id.
        for (int index = 18; index < 21; index++)
        {
            fixture.Users[index].DisplayName = "Tied User 18";
            if (index > 18)
            {
                fixture.Users[index].Email = null;
                fixture.Users[index].NormalizedEmail = null;
            }
        }

        foreach (var user in fixture.Users.AsEnumerable().Reverse())
        {
            fixture.AddTransaction(workspace, user, UsageTransactionStatus.Completed, 1m, 1m, 0m);
        }
        await fixture.SaveChangesAsync();

        var first = await fixture.Service.GetReportAsync(fixture.FullRange);
        var second = await fixture.Service.GetReportAsync(fixture.FullRange);
        var expectedIds = fixture.Users.Take(18).Concat(fixture.Users.Skip(19)).Select(user => user.Id);
        Assert.Equal(20, first.Breakdowns.ByUser.Count);
        Assert.Equal(expectedIds, first.Breakdowns.ByUser.Select(item => item.UserId));

        // Identical request, identical members and order: no dependence on group-by emission.
        Assert.Equal(
            first.Breakdowns.ByUser.Select(item => item.UserId),
            second.Breakdowns.ByUser.Select(item => item.UserId));
        Assert.Equal(
            first.Breakdowns.ByUser.Select(item => item.DisplayName),
            second.Breakdowns.ByUser.Select(item => item.DisplayName));
    }

    /// <summary>Minimal direct-service harness with reversal and status control.</summary>
    private sealed class BoundaryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private TaslimDbContext db = null!;
        private int sequence;

        private BoundaryFixture()
        {
        }

        public AdminUsageService Service { get; private set; } = null!;
        public List<Workspace> Workspaces { get; } = [];
        public List<ApplicationUser> Users { get; } = [];

        public AdminUsageFilter FullRange => new(WindowStart.AddDays(-1), WindowStart.AddDays(1), null, null, null, null, null, 1, 100);

        public static async Task<BoundaryFixture> CreateAsync(int userCount = 2)
        {
            var fixture = new BoundaryFixture();
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(fixture.connection).Options;
            fixture.db = new TaslimDbContext(options);
            await fixture.db.Database.EnsureCreatedAsync();
            fixture.Service = new AdminUsageService(fixture.db);

            for (int index = 0; index < userCount; index++)
            {
                fixture.AddUser($"boundary-{index:D2}@example.com", $"Tied User {index:D2}");
            }
            fixture.AddWorkspace("Primary Workspace");
            await fixture.db.SaveChangesAsync();
            return fixture;
        }

        public Workspace AddWorkspace(string name)
        {
            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = name,
                Slug = $"exact-{Interlocked.Increment(ref sequence)}-{Guid.NewGuid():N}",
                Type = WorkspaceType.Business,
                CreatedAt = WindowStart,
                UpdatedAt = WindowStart,
            };
            db.Workspaces.Add(workspace);
            Workspaces.Add(workspace);
            foreach (var user in Users)
            {
                db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    UserId = user.Id,
                    Role = WorkspaceRole.Owner,
                    JoinedAt = WindowStart,
                });
            }
            return workspace;
        }

        public void AddTransaction(Workspace workspace, ApplicationUser user, UsageTransactionStatus status, decimal providerCostUsd, decimal chargedAmountUsd, decimal reversedAmountUsd)
        {
            db.UsageTransactions.Add(new UsageTransaction
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                RequestId = Guid.NewGuid().ToString("N"),
                Feature = UsageFeature.Chat,
                Provider = "mock",
                Model = "mock-model",
                Status = status,
                InputTokens = 10,
                OutputTokens = 10,
                ProviderCostUsd = providerCostUsd,
                ChargedAmount = chargedAmountUsd,
                ReversedAmount = reversedAmountUsd,
                ProviderCostKnown = true,
                CostBasis = "Actual",
                Currency = UsageCurrencies.Usd,
                CreatedAt = WindowStart.AddSeconds(Interlocked.Increment(ref sequence)),
                CompletedAt = WindowStart.AddSeconds(sequence),
            });
        }

        private ApplicationUser AddUser(string email, string displayName)
        {
            var user = new ApplicationUser
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{Users.Count + 1:D12}"),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = displayName,
                CreatedAt = WindowStart,
                UpdatedAt = WindowStart,
            };
            db.Users.Add(user);
            Users.Add(user);
            return user;
        }

        public Task SaveChangesAsync() => db.SaveChangesAsync();

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}

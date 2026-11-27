using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Boundary coverage for the customer-charge leg of the admin usage report on refunded rows.
///
/// PR #902 pins every currency aggregate back to the ledger scale, but the aggregate it pins on
/// the customer side is the double-accumulated <c>ChargedAmount - ReversedAmount</c>, and no test
/// in the repo seeds <c>ReversedAmount</c> at all against this read model: every case in
/// <see cref="AdminUsageReportExactnessTests"/> leaves it at its default zero, and
/// <c>AdminUsageTests</c> covers cancellation (both amounts zero) rather than a reversal. So the
/// one projection in the report that subtracts before the lossy double round trip - the projection
/// a refund actually exercises - is untested.
///
/// A refund is also the only routine way that projection reaches an exact zero net while the row
/// keeps real provider cost. <c>UsageLedgerService.AddAdjustmentAsync</c> reverses precisely the
/// remaining balance, so an over-reversal is not a reachable state and is deliberately not
/// asserted here; a full reversal netting to zero is.
/// </summary>
public sealed class AdminUsageReportRefundBoundaryTests
{
    // Both operands are stored at the declared decimal(18, 8) scale, so the per-row net of
    // 0.12345678 and its exact sum over 64 rows are both representable at eight decimal places.
    // Accumulating the difference through the report's double projection does not keep it so.
    private const decimal ChargedPerRowUsd = 0.12345679m;
    private const decimal ReversedPerRowUsd = 0.00000001m;
    private const int RefundRowCount = 64;
    private const decimal ExactNetTotalUsd = 7.90123392m;      // 64 x 0.12345678
    private const decimal ExactProviderCostTotalUsd = 7.90123456m; // 64 x 0.12345679

    // A fully reversed row: the customer charge nets to exactly zero, the provider cost does not.
    private const decimal FullyRefundedChargedUsd = 1.00000000m;
    private const decimal FullyRefundedProviderCostUsd = 0.50000000m;

    private static readonly DateTime WindowStart = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The report has to present the refund residual rather than the charged amount, at the
    /// stored scale, on every rank that carries a customer-charge column.
    /// </summary>
    [Fact]
    public async Task Partially_refunded_rows_report_the_exact_net_charge_not_the_charged_amount()
    {
        await using var fixture = await RefundFixture.CreateAsync();
        fixture.AddReversals(fixture.Workspaces[0], fixture.Users[0], RefundRowCount, ChargedPerRowUsd, ReversedPerRowUsd, ChargedPerRowUsd, UsageFeature.Chat);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(ExactNetTotalUsd, report.Summary.TotalCustomerChargesUsd);
        Assert.Equal(ExactProviderCostTotalUsd, report.Summary.TotalProviderCostUsd);

        var feature = Assert.Single(report.Breakdowns.ByFeature);
        Assert.Equal(ExactNetTotalUsd, feature.CustomerChargesUsd);

        var day = Assert.Single(report.Breakdowns.ByDay);
        Assert.Equal(ExactNetTotalUsd, day.CustomerChargesUsd);

        var workspace = Assert.Single(report.Breakdowns.ByWorkspace);
        Assert.Equal(ExactNetTotalUsd, workspace.CustomerChargesUsd);

        var user = Assert.Single(report.Breakdowns.ByUser);
        Assert.Equal(ExactNetTotalUsd, user.CustomerChargesUsd);
    }

    /// <summary>
    /// A full reversal nets that row's customer charge to exactly zero without dragging its
    /// provider spend out of the report, so the two columns stay independent at the boundary and
    /// the refunded feature cannot be mistaken for free traffic in a rank.
    /// </summary>
    [Fact]
    public async Task Fully_reversed_row_nets_customer_charge_to_zero_but_keeps_provider_cost()
    {
        await using var fixture = await RefundFixture.CreateAsync();
        fixture.AddReversals(fixture.Workspaces[0], fixture.Users[0], RefundRowCount, ChargedPerRowUsd, ReversedPerRowUsd, ChargedPerRowUsd, UsageFeature.Chat);
        fixture.AddReversals(fixture.Workspaces[0], fixture.Users[0], 1, FullyRefundedChargedUsd, FullyRefundedChargedUsd, FullyRefundedProviderCostUsd, UsageFeature.Image, UsageTransactionStatus.Refunded);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        // The refund adds nothing to charges and nothing to provider cost stays visible.
        Assert.Equal(ExactNetTotalUsd, report.Summary.TotalCustomerChargesUsd);
        Assert.Equal(ExactProviderCostTotalUsd + FullyRefundedProviderCostUsd, report.Summary.TotalProviderCostUsd);
        Assert.Equal(1, report.Summary.RefundedCount);
        Assert.Equal(RefundRowCount + 1, report.Summary.TransactionCount);

        var image = Assert.Single(report.Breakdowns.ByFeature, item => item.Feature == nameof(UsageFeature.Image));
        Assert.Equal(0m, image.CustomerChargesUsd);
        Assert.Equal(FullyRefundedProviderCostUsd, image.ProviderCostUsd);

        var refunded = Assert.Single(report.Breakdowns.ByStatus, item => item.Status == nameof(UsageTransactionStatus.Refunded));
        Assert.Equal(0m, refunded.CustomerChargesUsd);
        Assert.Equal(FullyRefundedProviderCostUsd, refunded.ProviderCostUsd);

        // Ranks are cut on provider cost, so the fully refunded feature still ranks below the
        // billed one on a non-tied margin: the zero net charge must not reorder the rank.
        Assert.Equal(
            new[] { nameof(UsageFeature.Chat), nameof(UsageFeature.Image) },
            report.Breakdowns.ByFeature.Select(item => item.Feature));
    }

    /// <summary>Minimal direct-service harness: one in-memory SQLite ledger per test.</summary>
    private sealed class RefundFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private TaslimDbContext db = null!;
        private int sequence;

        public AdminUsageService Service { get; private set; } = null!;
        public List<Workspace> Workspaces { get; } = [];
        public List<ApplicationUser> Users { get; } = [];

        public AdminUsageFilter FullRange => new(WindowStart.AddDays(-1), WindowStart.AddDays(1), null, null, null, null, null, 1, 100);

        public static async Task<RefundFixture> CreateAsync()
        {
            var fixture = new RefundFixture();
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(fixture.connection).Options;
            fixture.db = new TaslimDbContext(options);
            await fixture.db.Database.EnsureCreatedAsync();
            fixture.Service = new AdminUsageService(fixture.db);

            fixture.AddUser("refund-0@example.com");
            fixture.AddUser("refund-1@example.com");
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
                Slug = $"refund-{Interlocked.Increment(ref sequence)}-{Guid.NewGuid():N}",
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

        public void AddReversals(
            Workspace workspace,
            ApplicationUser user,
            int count,
            decimal chargedAmountUsd,
            decimal reversedAmountUsd,
            decimal providerCostUsd,
            UsageFeature feature,
            UsageTransactionStatus status = UsageTransactionStatus.Completed)
        {
            for (int index = 0; index < count; index++)
            {
                db.UsageTransactions.Add(new UsageTransaction
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    UserId = user.Id,
                    RequestId = Guid.NewGuid().ToString("N"),
                    Feature = feature,
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
                    RefundedAt = status == UsageTransactionStatus.Refunded ? WindowStart.AddSeconds(sequence) : null,
                });
            }
        }

        private ApplicationUser AddUser(string email)
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                DisplayName = email,
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

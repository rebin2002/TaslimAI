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
/// Boundary legs of the #1145 exactness fix that the six facts in
/// <see cref="AdminUsageReportExactnessTests"/> do not reach. That class seeds only
/// <see cref="UsageTransactionStatus.Completed"/> rows with
/// <c>ReversedAmount = 0</c>, and asserts rank order only on the workspace and feature
/// lists. The admin report still projects three uncovered properties:
/// 1. a negative net customer charge - the admin projection deliberately does not
///    floor like the entity's <c>NetChargedAmount</c>, so over-reversal must survive
///    the double accumulation sign-safely and pin at the ledger scale, not flip to
///    zero or leak binary noise with a minus sign;
/// 2. the status breakdown, which is now ordinal-ordered by its displayed name while
///    its Cancelled/Refunded legs carry zero net charges alongside positive provider
///    costs;
/// 3. the truncated user rank - the workspace rank has a membership test, but the
///    second <c>Take(ReportRankLimit)</c> site (per-user rows joined by email) has
///    none, so a tied cut there is unguarded.
/// Pure read-side projection tests: synthetic in-memory SQLite rows, Provider = "mock",
/// no stored amount is recalculated and no billing write path is exercised.
/// </summary>
public sealed class AdminUsageReportExactnessBoundaryTests
{
    // Same stored-scale constant as the #1145 class so the arithmetic provenance matches.
    private const decimal LedgerRowCostUsd = 0.12345679m;
    private const int LedgerRowCount = 64;
    private const decimal ExactLedgerTotalUsd = 7.90123456m;

    private static readonly DateTime WindowStart = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Every test in the repo seeds ReversedAmount <= ChargedAmount, so the negative
    /// branch of (double)(ChargedAmount - ReversedAmount) never crosses LedgerAmount.
    /// Over-reversal is exactly representable at the stored scale, so the reported net
    /// must be exact and negative, not floored to zero (the entity clamps; the admin
    /// projection intentionally does not) and not a noisy -2.000000020000001.
    /// </summary>
    [Fact]
    public async Task Negative_net_customer_charges_survive_the_ledger_pin_without_sign_or_scale_drift()
    {
        await using var fixture = await BoundaryFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        var user = fixture.Users[0];

        // Two rows charged 1.00 each, reversed 3.00000001 in total: net = -1.00000002.
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Refunded, LedgerRowCostUsd, 1.00m, 2.00000001m);
        fixture.AddTransaction(workspace, user, UsageTransactionStatus.Refunded, LedgerRowCostUsd, 1.00m, 1.00000001m);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        // Sign preserved, pinned at eight places - the artifact double sum would show as
        // -1.0000000200000019 before the pin.
        Assert.Equal(-1.00000002m, report.Summary.TotalCustomerChargesUsd);
        Assert.True(report.Summary.TotalCustomerChargesUsd < 0m);

        // Provider cost is unaffected by the reversal legs and still pins exactly.
        Assert.Equal(2 * LedgerRowCostUsd, report.Summary.TotalProviderCostUsd);

        var status = Assert.Single(report.Breakdowns.ByStatus);
        Assert.Equal(nameof(UsageTransactionStatus.Refunded), status.Status);
        Assert.Equal(-1.00000002m, status.CustomerChargesUsd);
        Assert.Equal(2 * LedgerRowCostUsd, status.ProviderCostUsd);

        var day = Assert.Single(report.Breakdowns.ByDay);
        Assert.Equal(-1.00000002m, day.CustomerChargesUsd);

        // Wire form: negative, invariant, non-exponential, at the ledger scale.
        var json = JsonSerializer.Serialize(report.Summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"totalCustomerChargesUsd\":-1.00000002", json, StringComparison.Ordinal);
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

        for (int index = 0; index < 21; index++)
        {
            fixture.AddTransaction(workspace, fixture.Users[index], UsageTransactionStatus.Completed, 1.00m, 1.00m, 0m);
        }
        await fixture.SaveChangesAsync();

        string ExpectedName(int index) => $"Tied User {index:D2}";

        var first = await fixture.Service.GetReportAsync(fixture.FullRange);
        var second = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(20, first.Breakdowns.ByUser.Count);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(ExpectedName),
            first.Breakdowns.ByUser.Select(item => item.DisplayName));

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
                Id = Guid.NewGuid(),
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

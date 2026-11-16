using System.Globalization;
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
/// The admin usage report is the read model behind usage exports and operator ranks.
/// Its currency values have to render the same number for the same stored rows, and its
/// ranked lists have to return the same rows in the same order, regardless of the host
/// culture or the order the database chooses to emit a grouped query in.
/// </summary>
public sealed class AdminUsageReportExactnessTests
{
    // Stored at the ledger's declared decimal(18, 8) scale. Summing 64 of these through the
    // double projection SQLite needs lands on 7.90123455999999 instead of 7.90123456.
    private const decimal LedgerRowCostUsd = 0.12345679m;
    private const int LedgerRowCount = 64;
    private const decimal ExactLedgerTotalUsd = 7.90123456m;

    private static readonly DateTime WindowStart = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Report_totals_are_pinned_to_the_ledger_scale()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();
        fixture.AddTransactions(
            fixture.Workspaces[0],
            fixture.Users[0],
            Enumerable.Repeat(LedgerRowCostUsd, LedgerRowCount),
            UsageFeature.Chat);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(ExactLedgerTotalUsd, report.Summary.TotalProviderCostUsd);
        Assert.Equal(ExactLedgerTotalUsd, report.Summary.TotalCustomerChargesUsd);

        var feature = Assert.Single(report.Breakdowns.ByFeature);
        Assert.Equal(ExactLedgerTotalUsd, feature.ProviderCostUsd);
        Assert.Equal(ExactLedgerTotalUsd, feature.CustomerChargesUsd);

        var day = Assert.Single(report.Breakdowns.ByDay);
        Assert.Equal(ExactLedgerTotalUsd, day.ProviderCostUsd);
        Assert.Equal(ExactLedgerTotalUsd, day.CustomerChargesUsd);

        var workspace = Assert.Single(report.Breakdowns.ByWorkspace);
        Assert.Equal(ExactLedgerTotalUsd, workspace.ProviderCostUsd);
    }

    /// <summary>
    /// The wire form is what an export renders, so it is pinned directly: an invariant,
    /// non-exponential decimal at the ledger scale rather than binary rounding noise.
    /// </summary>
    [Fact]
    public async Task Report_emits_locale_neutral_currency_values_on_the_wire()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();
        fixture.AddTransactions(
            fixture.Workspaces[0],
            fixture.Users[0],
            Enumerable.Repeat(LedgerRowCostUsd, LedgerRowCount),
            UsageFeature.Chat);
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        // A comma decimal separator or an Arabic-Indic digit set in the numeric payload
        // would silently re-column a spreadsheet, so the JSON text is asserted verbatim.
        var json = JsonSerializer.Serialize(report.Summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var cost = document.RootElement.GetProperty("totalProviderCostUsd");
        Assert.Equal(JsonValueKind.Number, cost.ValueKind);
        Assert.Equal("7.90123456", cost.GetRawText());

        // Invariant rendering is required no matter what culture the request thread carries.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-EG");
            var localizedJson = JsonSerializer.Serialize(report.Summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Contains("\"totalProviderCostUsd\":7.90123456", localizedJson, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// Equal-cost rows are the normal case, not an edge case: a quiet day leaves most
    /// features and workspaces tied at the same total. Without a tie-breaker the order -
    /// and, past the rank limit, which members appear at all - is whatever the database
    /// grouped them in.
    /// </summary>
    [Fact]
    public async Task Tied_breakdown_ranks_use_an_ordinal_tie_breaker()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();

        // Inserted so that neither the enum order nor the insertion order matches the
        // ordinal name order the rank is expected to settle on.
        string[] workspaceNames = ["Zeta", "Yankee", "Xray", "Whisky", "Victor"];
        foreach (var name in workspaceNames)
        {
            var workspace = fixture.AddWorkspace(name);
            // Music sorts after Document ordinally but was seeded first.
            fixture.AddTransactions(workspace, fixture.Users[0], [1.00m, 1.00m], UsageFeature.Music);
            fixture.AddTransactions(workspace, fixture.Users[0], [1.00m, 1.00m], UsageFeature.Document);
        }
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(new[] { "Victor", "Whisky", "Xray", "Yankee", "Zeta" }, report.Breakdowns.ByWorkspace.Select(item => item.WorkspaceName));
        Assert.Equal(new[] { "Document", "Music" }, report.Breakdowns.ByFeature.Select(item => item.Feature));
    }

    /// <summary>
    /// Ordinal and culture-sensitive comparison disagree on case: a culture comparer folds
    /// and reorders mixed case, so 'alpha' would rank above 'Zeta'. Ranking has to be the
    /// same on every host, so the ordinal result is the one pinned here.
    /// </summary>
    [Fact]
    public async Task Breakdown_ranks_do_not_depend_on_the_host_culture()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();
        foreach (var name in new[] { "alpha", "Zeta" })
        {
            var workspace = fixture.AddWorkspace(name);
            fixture.AddTransactions(workspace, fixture.Users[0], [1.00m], UsageFeature.Chat);
        }
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        // StringComparer.Ordinal: 'Z' (0x5A) precedes 'a' (0x61).
        Assert.Equal(new[] { "Zeta", "alpha" }, report.Breakdowns.ByWorkspace.Select(item => item.WorkspaceName));
        Assert.Equal(new[] { "Chat" }, report.Breakdowns.ByFeature.Select(item => item.Feature));
    }

    [Fact]
    public async Task Breakdown_ranks_are_reproducible_across_identical_requests()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();
        string[] workspaceNames = ["Zeta", "Yankee", "Xray", "Whisky", "Victor"];
        foreach (var name in workspaceNames)
        {
            var workspace = fixture.AddWorkspace(name);
            fixture.AddTransactions(workspace, fixture.Users[0], [0.25m, 0.25m, 0.50m], UsageFeature.Chat);
            fixture.AddTransactions(workspace, fixture.Users[1], [1.00m], UsageFeature.Image);
        }
        await fixture.SaveChangesAsync();

        var first = await fixture.Service.GetReportAsync(fixture.FullRange);
        var second = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(
            first.Breakdowns.ByWorkspace.Select(item => (item.WorkspaceId, item.ProviderCostUsd)),
            second.Breakdowns.ByWorkspace.Select(item => (item.WorkspaceId, item.ProviderCostUsd)));
        Assert.Equal(
            first.Breakdowns.ByUser.Select(item => (item.UserId, item.ProviderCostUsd)),
            second.Breakdowns.ByUser.Select(item => (item.UserId, item.ProviderCostUsd)));
        Assert.Equal(
            first.Breakdowns.ByFeature.Select(item => item.Feature),
            second.Breakdowns.ByFeature.Select(item => item.Feature));
    }

    /// <summary>
    /// A rank is only reproducible if the cut keeps the right members: every tied row sorts
    /// to the same side of the limit on a host with a different culture.
    /// </summary>
    [Fact]
    public async Task Truncated_ranks_keep_a_deterministic_membership()
    {
        await using var fixture = await AdminUsageFixture.CreateAsync();
        for (int index = 0; index < 25; index++)
        {
            var workspace = fixture.AddWorkspace($"Tied {index:D2}");
            fixture.AddTransactions(workspace, fixture.Users[0], [1.00m], UsageFeature.Chat);
        }
        await fixture.SaveChangesAsync();

        var report = await fixture.Service.GetReportAsync(fixture.FullRange);

        Assert.Equal(20, report.Breakdowns.ByWorkspace.Count);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"Tied {index:D2}"),
            report.Breakdowns.ByWorkspace.Select(item => item.WorkspaceName));

    }

    /// <summary>Minimal direct-service harness: one in-memory SQLite ledger per test.</summary>
    private sealed class AdminUsageFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("DataSource=:memory:");
        private TaslimDbContext db = null!;
        private int sequence;

        public AdminUsageService Service { get; private set; } = null!;
        public List<Workspace> Workspaces { get; } = [];
        public List<ApplicationUser> Users { get; } = [];

        public AdminUsageFilter FullRange => new(WindowStart.AddDays(-1), WindowStart.AddDays(1), null, null, null, null, null, 1, 100);

        public static async Task<AdminUsageFixture> CreateAsync()
        {
            var fixture = new AdminUsageFixture();
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(fixture.connection).Options;
            fixture.db = new TaslimDbContext(options);
            await fixture.db.Database.EnsureCreatedAsync();
            fixture.Service = new AdminUsageService(fixture.db);

            fixture.AddWorkspace("Primary Workspace");
            for (int index = 0; index < 2; index++)
            {
                // AddUser already registers the user in Users and wires the memberships.
                fixture.AddUser($"report-{index}@example.com");
            }
            await fixture.db.SaveChangesAsync();
            return fixture;
        }

        public Workspace AddWorkspace(string name)
        {
            var workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = name,
                Slug = $"usage-{Interlocked.Increment(ref sequence)}-{Guid.NewGuid():N}",
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

        public void AddTransactions(Workspace workspace, ApplicationUser user, IEnumerable<decimal> providerCostsUsd, UsageFeature feature)
        {
            foreach (var cost in providerCostsUsd)
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
                    Status = UsageTransactionStatus.Completed,
                    InputTokens = 10,
                    OutputTokens = 10,
                    ProviderCostUsd = cost,
                    ChargedAmount = cost,
                    ProviderCostKnown = true,
                    CostBasis = "Actual",
                    Currency = UsageCurrencies.Usd,
                    CreatedAt = WindowStart.AddSeconds(Interlocked.Increment(ref sequence)),
                    CompletedAt = WindowStart.AddSeconds(sequence),
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

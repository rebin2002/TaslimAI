using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Payments;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Release finding F-27, addressed on the shipping vehicle rather than described.
//
// RenewalPeriodIdentity named a renewal period over DateTime.Ticks - a 100-nanosecond resolution the
// billing row cannot hold. The column is timestamp with time zone, which stores microseconds, and the
// provider TRUNCATES the finer digits on the way in; measured against this stack, not assumed: a start
// at base+7 ticks arrives in the row as base, while base+15 arrives as base+10. So the id minted from
// a boundary still carrying sub-microsecond digits was an id no stored row could ever reproduce, and
// the identity-derived collision that PR #1094 made load-bearing silently stopped existing for exactly
// the deliveries that hit it. Independently, the derivation folded the raw value, so the same instant
// spelled as the offset-bearing form a webhook payload parses into and the UTC form a row reads back as
// named two different renewals of one period, while ValidatePeriod ordered on raw DateTime digits and
// therefore ignored Kind.
//
// Both halves are closed by hashing the instant at the resolution the store keeps it at (see
// PaymentLifecycleService.StoreResolutionTicks). What this lane pins:
//
//   A. The digits the store discards cannot change the identity, and the digits it keeps still can.
//      Over-collapsing would fuse real renewals together, so distinctness is asserted in the same
//      breath as invariance.
//   B. One instant is one identity whatever representation it arrives in.
//   C. Compatibility: whole-second boundaries - what activation and renewal compute today, and what
//      every existing row holds - hash over the same bytes as before this fix. Proven by re-deriving
//      the historical name in the test, not by trusting the implementation to agree with itself.
//   D. The claim as it exists on the database that enforces it: a period persisted through Npgsql at
//      sub-microsecond precision has a stored StartsAt from which the shipped derivation recomputes
//      the id that is actually in the primary key. This is the assertion that fails on the pre-fix
//      code, and it is exercised on a real PostgreSQL, not on an in-memory stand-in.
//
// Nothing here charges anyone: CustomerChargingEnabled stays false, every identifier is synthetic, and
// the money statement is one included allowance per renewal period.
public sealed class BillingRenewalPeriodStorePrecisionTests : IAsyncLifetime
{
    // A whole-second boundary: ticks 639079200000000000, already a multiple of ten, which is why the
    // fix cannot move any existing identity.
    private static readonly DateTime PeriodStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PeriodEnd = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SeededAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Subscription = Guid.Parse("11111111-1111-1111-1111-111111111111");
    // Pinned in BillingRenewalPeriodIdentityTests; restated here so a derivation change that quietly
    // moves the historical value cannot pass this lane either.
    private static readonly Guid GoldenIdentity = Guid.Parse("1ecaf78b-e243-5d6d-92c0-b222f64c8e83");
    private const long MonthlyAllowance = 500;
    private const long TicksPerMicrosecond = 10;
    private readonly string schema = $"bill_prec_{Guid.NewGuid():N}";
    private string connectionString = null!;
    private DbContextOptions<TaslimDbContext> options = null!;

    public async Task InitializeAsync()
    {
        connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? string.Empty;
        // The pure tests below need no database and must run in every CI lane. Without a PostgreSQL
        // service the schema is not created and the round-trip tests skip through their attribute.
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }
        options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
            .Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public void Digits_the_store_cannot_hold_do_not_move_the_identity_and_digits_it_keeps_still_do()
    {
        // A. Invariance across the truncation the provider performs. 1..9 ticks past a microsecond
        // boundary are discarded on write, so they must not be able to name a different renewal.
        for (long delta = 1; delta <= 9; delta++)
        {
            Assert.Equal(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(delta)));
        }
        // And the boundary behaves the same way one microsecond below it, because flooring - not
        // rounding - is what the store does: base-1 tick is stored as base-10, and is named as such.
        Assert.Equal(
            PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(-TicksPerMicrosecond)),
            PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(-1)));
        // Distinctness, so this is resolution-matching rather than blurring: one more microsecond the
        // store can hold is a different renewal period, and a whole second is many microseconds.
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(TicksPerMicrosecond)));
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(TicksPerMicrosecond * 5)));
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddSeconds(1)));
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddMonths(1)));
        Assert.NotEqual(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Guid.Parse("22222222-2222-2222-2222-222222222222"), PeriodStart));
    }

    [Fact]
    public void One_instant_is_one_identity_whatever_representation_it_arrives_in()
    {
        // B. The payload path and the row path: an offset-bearing webhook timestamp parses into a local
        // representation, and the same period reads back from the column as UTC. One renewal, so one
        // identity, on any host offset - which is why the local form is derived from the instant rather
        // than written out as fixed digits.
        var asUtc = PeriodStart;
        var asLocal = PeriodStart.ToLocalTime();
        var asUnspecified = DateTime.SpecifyKind(PeriodStart, DateTimeKind.Unspecified);
        Assert.Equal(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, asLocal));
        Assert.Equal(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, asUnspecified));
        Assert.Equal(PaymentLifecycleService.RenewalPeriodIdentity(Subscription, asUtc), PaymentLifecycleService.RenewalPeriodIdentity(Subscription, asLocal));
        // Two spellings of one instant, straight off the wire.
        var zulu = DateTimeOffset.Parse("2026-03-01T00:00:00+00:00", CultureInfo.InvariantCulture);
        var offset = DateTimeOffset.Parse("2026-02-28T21:00:00-03:00", CultureInfo.InvariantCulture);
        Assert.Equal(offset.ToUniversalTime(), zulu.ToUniversalTime());
        Assert.Equal(
            PaymentLifecycleService.RenewalPeriodIdentity(Subscription, zulu.UtcDateTime),
            PaymentLifecycleService.RenewalPeriodIdentity(Subscription, offset.LocalDateTime));
        Assert.Equal(GoldenIdentity, PaymentLifecycleService.RenewalPeriodIdentity(Subscription, offset.LocalDateTime));
        // The normalising helper agrees with the store on both halves of the decision.
        Assert.Equal(PeriodStart.Ticks, PaymentLifecycleService.StoreResolutionTicks(asLocal));
        Assert.Equal(PeriodStart.Ticks, PaymentLifecycleService.StoreResolutionTicks(asUnspecified));
        Assert.Equal(PeriodStart.Ticks + 10, PaymentLifecycleService.StoreResolutionTicks(PeriodStart.AddTicks(15)));
        Assert.Equal(PeriodStart.Ticks, PaymentLifecycleService.StoreResolutionTicks(PeriodStart.AddTicks(9)));
        // A genuine instant difference survives the normalising, so this is not a way to lose a real
        // period: one second is 10,000,000 microseconds the store can hold.
        Assert.NotEqual(PaymentLifecycleService.StoreResolutionTicks(asUtc), PaymentLifecycleService.StoreResolutionTicks(asUtc.AddSeconds(-1)));
        Assert.Equal(PeriodStart.Ticks, PaymentLifecycleService.StoreResolutionTicks(PeriodStart.AddTicks(1)));
    }

    [Fact]
    public void Whole_second_boundaries_hash_over_the_bytes_they_hashed_over_before_the_fix()
    {
        // C. Wire compatibility with every id already in the table. Rather than trusting the new
        // derivation to agree with itself, re-derive the historical name - the one that hashed raw
        // Ticks - and require byte-for-byte equality for whole-second input. Existing BillingPeriod
        // primary keys, and the grant keys built from them, therefore do not move.
        static Guid Historical(Guid subscriptionId, DateTime periodStart)
        {
            var name = Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"taslim:billing-period:v1:{subscriptionId:N}:{periodStart.Ticks}"));
            var digest = SHA1.HashData(name);
            digest[6] = (byte)((digest[6] & 0x0F) | 0x50);
            digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
            return Guid.Parse(Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant());
        }
        foreach (var boundary in new[]
                 {
                     PeriodStart, PeriodEnd, SeededAt, PeriodStart.AddMonths(1), PeriodStart.AddSeconds(-1),
                     new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                 })
        {
            Assert.Equal(Historical(Subscription, boundary), PaymentLifecycleService.RenewalPeriodIdentity(Subscription, boundary));
        }
        Assert.Equal(Historical(Subscription, PeriodStart), GoldenIdentity);
        // And the one case the old code got wrong is the one the store cannot represent: below a
        // microsecond the historical derivation diverges, which is the bug, not a regression.
        Assert.NotEqual(Historical(Subscription, PeriodStart.AddTicks(7)), PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(7)));
        Assert.Equal(Historical(Subscription, PeriodStart), PaymentLifecycleService.RenewalPeriodIdentity(Subscription, PeriodStart.AddTicks(7)));
    }

    [DisposablePostgresFact]
    public async Task A_start_persisted_below_store_resolution_replays_on_the_instant_the_row_actually_holds()
    {
        // D. The finding as it exists on the engine that enforces it. The renewal is delivered with a
        // boundary carrying ticks the column discards, so the id is minted from a value that never
        // reaches the row.
        var suppliedStart = PeriodStart.AddTicks(7);
        var suppliedEnd = PeriodEnd.AddTicks(7);
        // The premise, stated in the input itself: a whole-second boundary is already at store
        // resolution, and this one carries digits below it.
        Assert.Equal(0, PeriodStart.Ticks % TicksPerMicrosecond);
        Assert.Equal(7, suppliedStart.Ticks % TicksPerMicrosecond);
        var workspace = NewWorkspace("store-precision");
        var plan = NewPlan("store-precision");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        await SeedAsync(workspace, plan, subscription);
        await using (var db = new TaslimDbContext(options))
            await NewLifecycle(db).RenewSubscriptionAsync(workspace.Id, subscription.Id, suppliedStart, suppliedEnd, "webhook:precision-1", "Provider renewal succeeded.");
        await using var probe = new TaslimDbContext(options);
        var period = await probe.BillingPeriods.AsNoTracking().SingleAsync(item => item.SubscriptionId == subscription.Id);
        // The premise, asserted rather than assumed: PostgreSQL really did discard the digits, so the
        // rest of this test cannot pass vacuously.
        Assert.Equal(PeriodStart, period.StartsAt);
        Assert.NotEqual(suppliedStart, period.StartsAt);
        // The property that failed before the fix: the id in the primary key is reproducible from the
        // stored instant. On the pre-quantising derivation this is the mismatch that left a duplicate
        // delivery with a grant key no row could recompute.
        Assert.Equal(PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, period.StartsAt), period.Id);
        Assert.Equal(PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, PeriodStart), period.Id);
        Assert.Equal(DateTimeKind.Utc, period.StartsAt.Kind);
        // One allowance, and one grant key built from it.
        Assert.Equal(MonthlyAllowance, period.IncludedCredits);
        Assert.Equal($"included:{subscription.Id}:{period.Id}", await SingleGrantKeyAsync(workspace.Id));
        // A redelivery of the same event, expressed at the resolution the row holds - which is what any
        // later reader of this period hands back - is a replay, not a second renewal. The reason text
        // matches deliberately: the ledger replays an identical movement and refuses a key that turns
        // out to describe a different one, and the boundary below is the only thing that differs.
        await using (var db = new TaslimDbContext(options))
            await NewLifecycle(db).RenewSubscriptionAsync(workspace.Id, subscription.Id, period.StartsAt, period.EndsAt, "webhook:precision-2", "Provider renewal succeeded.");
        Assert.Equal(1, await probe.BillingPeriods.AsNoTracking().CountAsync(item => item.SubscriptionId == subscription.Id));
        Assert.Equal(MonthlyAllowance, await probe.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspace.Id && item.Type == CreditLedgerEntryType.Grant)
            .SumAsync(item => (long?)item.Amount) ?? 0);
    }

    [DisposablePostgresFact]
    public async Task Microseconds_the_store_keeps_stay_distinct_renewals_across_the_round_trip()
    {
        // The other half of the bound: matching the store's resolution must not merge renewals that are
        // genuinely a microsecond apart. Each persisted start keeps its own identity through the wire.
        var workspace = NewWorkspace("store-distinct");
        var plan = NewPlan("store-distinct");
        // One subscription carries both periods: a workspace holds a single subscription, and two
        // renewal boundaries on one subscription is the shape the unique index actually guards.
        var subscription = NewSubscription(workspace.Id, plan.Id);
        await SeedAsync(workspace, plan, subscription);
        var firstStart = PeriodStart.AddTicks(10);
        var secondStart = PeriodStart.AddTicks(20);
        await using (var db = new TaslimDbContext(options))
        {
            await db.BillingPeriods.AddAsync(new BillingPeriod
            {
                Id = PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, firstStart), SubscriptionId = subscription.Id,
                Status = BillingPeriodStatus.Open, StartsAt = firstStart, EndsAt = PeriodEnd,
                IncludedCredits = MonthlyAllowance, CreatedAt = SeededAt,
            });
            await db.BillingPeriods.AddAsync(new BillingPeriod
            {
                Id = PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, secondStart), SubscriptionId = subscription.Id,
                Status = BillingPeriodStatus.Open, StartsAt = secondStart, EndsAt = PeriodEnd,
                IncludedCredits = MonthlyAllowance, CreatedAt = SeededAt,
            });
            await db.SaveChangesAsync();
        }
        await using var probe = new TaslimDbContext(options);
        var rows = await probe.BillingPeriods.AsNoTracking().OrderBy(item => item.StartsAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(firstStart, rows[0].StartsAt);
        Assert.Equal(secondStart, rows[1].StartsAt);
        Assert.NotEqual(rows[0].Id, rows[1].Id);
        // Stored value reproduces its own key, one row at a time.
        foreach (var row in rows)
            Assert.Equal(PaymentLifecycleService.RenewalPeriodIdentity(row.SubscriptionId, row.StartsAt), row.Id);
        // And a start the store truncates onto one of them is the same renewal as that row, which is
        // the collision the guard is supposed to see.
        Assert.Equal(rows[0].Id, PaymentLifecycleService.RenewalPeriodIdentity(subscription.Id, firstStart.AddTicks(4)));
    }

    private async Task<string?> SingleGrantKeyAsync(Guid workspaceId)
    {
        await using var db = new TaslimDbContext(options);
        var keys = await db.CreditLedgerEntries.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Type == CreditLedgerEntryType.Grant)
            .Select(item => item.IdempotencyKey).ToListAsync();
        Assert.Equal(1, keys.Count);
        return keys[0];
    }

    private async Task SeedAsync(Workspace workspace, Plan plan, params Subscription[] subscriptions)
    {
        await using var db = new TaslimDbContext(options);
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.AddRange(subscriptions);
        await db.SaveChangesAsync();
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(
        db,
        new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false })),
        new NullNotificationEventWriter(),
        NullLogger<PaymentLifecycleService>.Instance);

    private static Workspace NewWorkspace(string label) => new()
    {
        Id = Guid.NewGuid(), Name = $"Precision {label}", Slug = $"bill-prec-{label}-{Guid.NewGuid():N}",
        Type = WorkspaceType.Personal, CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    // Plan.Code is varchar(40) with a unique index, so the suffix is a short random token.
    private static Plan NewPlan(string code) => new()
    {
        Id = Guid.NewGuid(), Code = $"{code}-{Guid.NewGuid():N}"[..40], Name = $"Precision plan {code}",
        MonthlyCreditAllowance = MonthlyAllowance, MonthlyPriceUsd = 0m, Currency = "USD", IsActive = true,
        SortOrder = 501, CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    private static Subscription NewSubscription(Guid workspaceId, Guid planId) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = planId, Status = SubscriptionStatus.Active,
        BillingProvider = "synthetic", ProviderSubscriptionReference = $"sub_{Guid.NewGuid():N}",
        CurrentPeriodStart = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        CurrentPeriodEnd = PeriodStart, NextRenewalAt = PeriodStart,
        CreatedAt = SeededAt, UpdatedAt = SeededAt,
    };

    private sealed class NullNotificationEventWriter : INotificationEventWriter
    {
        public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

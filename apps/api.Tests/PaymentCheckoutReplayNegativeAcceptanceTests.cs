using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Payments;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Provider-free negative acceptance for PR #255. These assertions intentionally stay red
// against the current replay implementation until the checkout owner fixes the contract.
// Exact replay may return the original safe synthetic URL or explicitly fail closed; this
// test does not require persisting a raw checkout URL.
public sealed class PaymentCheckoutReplayNegativeAcceptanceTests
{
    private const string IdempotencyKey = "checkout-replay-contract-key";
    private const string SuccessUrl = "https://example.test/success";
    private const string CancelUrl = "https://example.test/cancel";

    [Fact]
    public async Task First_checkout_returns_a_usable_synthetic_destination()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var provider = new FakeCheckoutProvider("fake");

        var result = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);

        Assert.True(result.Available, "A first synthetic checkout should be available.");
        Assert.NotNull(result.CheckoutSessionId);
        Assert.True(IsSafeSyntheticDestination(result.CheckoutUrl), "The fake provider must return only its synthetic HTTPS destination.");
        Assert.Equal(FakeCheckoutProvider.UrlForCall(1), result.CheckoutUrl);
        Assert.Equal(1, provider.CheckoutCreateCallCount);
    }

    [Fact]
    public async Task Exact_replay_returns_the_same_safe_destination_or_explicitly_fails_closed()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var provider = new FakeCheckoutProvider("fake");

        var first = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);
        AssertUsableFirstCheckout(first, provider);

        // Each invocation uses a fresh DbContext, so the replay is read from durable SQLite state.
        var replay = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);
        var reusableDestination = replay.Available
            && replay.CheckoutSessionId == first.CheckoutSessionId
            && IsSafeSyntheticDestination(replay.CheckoutUrl)
            && string.Equals(replay.CheckoutUrl, first.CheckoutUrl, StringComparison.Ordinal);
        var explicitFailClosed = !replay.Available && replay.CheckoutUrl is null;

        var failures = new List<string>();
        if (!reusableDestination && !explicitFailClosed)
            failures.Add("Exact replay must return the same safe reusable destination or be explicitly unavailable; Available=true with a null, unsafe, or different URL is forbidden.");
        if (provider.CheckoutCreateCallCount != 1)
            failures.Add($"Exact replay must not call the provider again; observed {provider.CheckoutCreateCallCount} calls.");

        Assert.True(failures.Count == 0, string.Join(" ", failures));
    }

    [Fact]
    public async Task Exact_replay_invokes_the_fake_provider_at_most_once()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var provider = new FakeCheckoutProvider("fake");

        _ = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);
        _ = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);

        Assert.Equal(1, provider.CheckoutCreateCallCount);
    }

    [Fact]
    public async Task Same_key_with_a_different_plan_is_rejected_without_reusing_the_original_session()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var provider = new FakeCheckoutProvider("fake");

        var first = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);
        AssertUsableFirstCheckout(first, provider);

        var changedRequest = await CreateCheckoutAsync(store, workspace.Id, "mega", IdempotencyKey, "fake", provider);

        AssertMismatchFailsClosed(first, changedRequest, (provider, 1));
    }

    [Fact]
    public async Task Same_key_with_a_changed_plan_amount_is_rejected_without_reusing_the_original_session()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var provider = new FakeCheckoutProvider("fake");

        var first = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);
        AssertUsableFirstCheckout(first, provider);

        await using (var db = store.CreateDbContext())
        {
            var plan = await db.Plans.SingleAsync(item => item.Code == "pro");
            plan.MonthlyPriceUsd += 1.25m;
            plan.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var changedRequest = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", provider);

        AssertMismatchFailsClosed(first, changedRequest, (provider, 1));
    }

    [Fact]
    public async Task Same_key_with_a_different_configured_provider_is_rejected_without_reusing_the_original_session()
    {
        var workspace = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspace);
        var originalProvider = new FakeCheckoutProvider("fake");
        var changedProvider = new FakeCheckoutProvider("alternate-fake");

        var first = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "fake", originalProvider, changedProvider);
        AssertUsableFirstCheckout(first, originalProvider);

        var changedRequest = await CreateCheckoutAsync(store, workspace.Id, "pro", IdempotencyKey, "alternate-fake", originalProvider, changedProvider);

        AssertMismatchFailsClosed(first, changedRequest, (originalProvider, 1), (changedProvider, 0));
    }

    [Fact]
    public async Task Same_idempotency_key_in_another_workspace_is_isolated()
    {
        var workspaceA = NewWorkspace();
        var workspaceB = NewWorkspace();
        await using var store = await SqliteCheckoutTestStore.CreateAsync(workspaceA, workspaceB);
        var provider = new FakeCheckoutProvider("fake");

        var checkoutA = await CreateCheckoutAsync(store, workspaceA.Id, "pro", IdempotencyKey, "fake", provider);
        var checkoutB = await CreateCheckoutAsync(store, workspaceB.Id, "pro", IdempotencyKey, "fake", provider);

        var failures = new List<string>();
        if (!IsUsableDestination(checkoutA)) failures.Add("Workspace A should receive its own usable synthetic checkout.");
        if (!IsUsableDestination(checkoutB)) failures.Add("Workspace B should receive its own usable synthetic checkout.");
        if (checkoutA.CheckoutSessionId == checkoutB.CheckoutSessionId)
            failures.Add("A workspace-B replay must not reuse workspace A's checkout session.");
        if (provider.CheckoutCreateCallCount != 2)
            failures.Add($"Separate workspaces should create separate fake sessions; observed {provider.CheckoutCreateCallCount} provider calls.");

        await using var db = store.CreateDbContext();
        var persistedSessions = await db.CheckoutSessions
            .Where(item => item.IdempotencyKey == IdempotencyKey)
            .ToListAsync();
        if (persistedSessions.Count != 2 || persistedSessions.Select(item => item.WorkspaceId).Distinct().Count() != 2)
            failures.Add("SQLite must contain one isolated checkout session per workspace for the shared key.");

        Assert.True(failures.Count == 0, string.Join(" ", failures));
    }

    private static async Task<CheckoutSessionResult> CreateCheckoutAsync(
        SqliteCheckoutTestStore store,
        Guid workspaceId,
        string planCode,
        string idempotencyKey,
        string selectedProvider,
        params IPaymentProvider[] providers)
    {
        await using var db = store.CreateDbContext();
        var service = new CheckoutSessionService(
            db,
            Options.Create(new BillingOptions
            {
                // Enabled only in this synthetic, local test fixture so the service path can be exercised.
                // No application setting, default, or customer charging state is changed.
                CustomerChargingEnabled = true,
                Provider = selectedProvider,
            }),
            providers,
            NullLogger<CheckoutSessionService>.Instance);

        return await service.CreateAsync(workspaceId, planCode, idempotencyKey, SuccessUrl, CancelUrl);
    }

    private static void AssertUsableFirstCheckout(CheckoutSessionResult result, FakeCheckoutProvider provider)
    {
        Assert.True(result.Available, "The initial provider-free checkout must be available.");
        Assert.NotNull(result.CheckoutSessionId);
        Assert.True(IsSafeSyntheticDestination(result.CheckoutUrl), "The first checkout must expose the usable synthetic HTTPS URL returned by the fake provider.");
        Assert.Equal(FakeCheckoutProvider.UrlForCall(1), result.CheckoutUrl);
        Assert.Equal(1, provider.CheckoutCreateCallCount);
    }

    private static void AssertMismatchFailsClosed(
        CheckoutSessionResult original,
        CheckoutSessionResult changedRequest,
        params (FakeCheckoutProvider Provider, int ExpectedCalls)[] expectedCalls)
    {
        var failures = new List<string>();
        if (changedRequest.Available)
            failures.Add("A changed plan, amount, or provider under an existing idempotency key must fail closed.");
        if (changedRequest.CheckoutSessionId == original.CheckoutSessionId)
            failures.Add("A changed request must not reuse the original checkout-session identifier.");
        if (changedRequest.CheckoutUrl is not null)
            failures.Add("A changed request rejected by the idempotency binding must not return a checkout URL.");

        foreach (var (provider, expected) in expectedCalls)
        {
            if (provider.CheckoutCreateCallCount != expected)
                failures.Add($"Provider '{provider.Key}' expected {expected} invocation(s), observed {provider.CheckoutCreateCallCount}.");
        }

        Assert.True(failures.Count == 0, string.Join(" ", failures));
    }

    private static bool IsUsableDestination(CheckoutSessionResult result) =>
        result.Available
        && result.CheckoutSessionId.HasValue
        && IsSafeSyntheticDestination(result.CheckoutUrl);

    private static bool IsSafeSyntheticDestination(string? checkoutUrl) =>
        Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, "checkout.example.test", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/synthetic/session/", StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static Workspace NewWorkspace() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Checkout Replay Test Workspace",
        Slug = $"checkout-{Guid.NewGuid():N}",
        Type = WorkspaceType.Personal,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private sealed class SqliteCheckoutTestStore(SqliteConnection connection, DbContextOptions<TaslimDbContext> options) : IAsyncDisposable
    {
        public static async Task<SqliteCheckoutTestStore> CreateAsync(params Workspace[] workspaces)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
            var store = new SqliteCheckoutTestStore(connection, options);

            try
            {
                await using var db = store.CreateDbContext();
                await db.Database.EnsureCreatedAsync();
                db.Workspaces.AddRange(workspaces);
                await db.SaveChangesAsync();
                return store;
            }
            catch
            {
                await store.DisposeAsync();
                throw;
            }
        }

        public TaslimDbContext CreateDbContext() => new(options);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class FakeCheckoutProvider(string key) : IPaymentProvider
    {
        private int checkoutCreateCallCount;

        public string Key { get; } = key;
        public int CheckoutCreateCallCount => Volatile.Read(ref checkoutCreateCallCount);
        public IWebhookSignatureVerifier WebhookSignatureVerifier { get; } = new RejectingSignatureVerifier();

        public static string UrlForCall(int callNumber) => $"https://checkout.example.test/synthetic/session/{callNumber}";

        public Task<ProviderCheckoutSessionResult> CreateCheckoutSessionAsync(
            ProviderCheckoutRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var callNumber = Interlocked.Increment(ref checkoutCreateCallCount);
            return Task.FromResult(new ProviderCheckoutSessionResult(
                $"synthetic-session-{callNumber}",
                UrlForCall(callNumber),
                DateTime.UtcNow.AddMinutes(30)));
        }

        public ProviderPaymentEvent ParseWebhook(string rawPayload) =>
            throw new InvalidOperationException("Checkout replay acceptance must not invoke webhook parsing.");
    }

    private sealed class RejectingSignatureVerifier : IWebhookSignatureVerifier
    {
        public bool Verify(string rawPayload, string signature) => false;
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Billing;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Payments;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PaymentFoundationTests
{
    [Fact]
    public async Task Checkout_is_unavailable_when_customer_charging_is_disabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var service = new CheckoutSessionService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }), []);

        var result = await service.CreateAsync(Guid.NewGuid(), "pro", "checkout:disabled", "https://example.test/success", "https://example.test/cancel");

        Assert.False(result.Available);
        Assert.Equal("CHECKOUT_DISABLED", result.Code);
        Assert.Empty(await db.CheckoutSessions.ToListAsync());
    }

    [Fact]
    public async Task Payment_attempts_are_idempotent_and_failed_subscription_becomes_past_due()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        var plan = DefaultPlanCatalog.All.Single(item => item.Code == "pro");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);

        var first = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:one", 9, "usd", subscriptionId: subscription.Id);
        var duplicate = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:one", 9, "usd", subscriptionId: subscription.Id);
        await service.MarkPaymentFailedAsync(workspace.Id, first.Id, "card_declined", "The payment provider declined the attempt.");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(PaymentAttemptStatus.Failed, duplicate.Status);
        Assert.Equal(SubscriptionStatus.PastDue, (await db.Subscriptions.SingleAsync(item => item.Id == subscription.Id)).Status);
        Assert.Single(await db.SubscriptionLifecycleEvents.ToListAsync());
    }

    [Fact]
    public async Task Refunds_are_idempotent_and_keep_the_original_attempt_auditable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);
        var attempt = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:refund", 9, "USD", PaymentAttemptStatus.Succeeded, providerPaymentReference: "payment-1");

        var first = await service.RecordRefundAsync(workspace.Id, attempt.Id, "test", 9, "USD", "refund:one", "Customer requested a full refund.", "refund-1");
        var duplicate = await service.RecordRefundAsync(workspace.Id, attempt.Id, "test", 9, "USD", "refund:one", "Customer requested a full refund.", "refund-1");

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(PaymentRefundStatus.Succeeded, duplicate.Status);
        Assert.Equal(PaymentAttemptStatus.Refunded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Single(await db.PaymentRefunds.ToListAsync());
    }

    [Fact]
    public async Task Signed_webhook_is_recorded_once_and_replayed_without_duplicate_state_change()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:webhook", 9, "USD");
        var providerEvent = new ProviderPaymentEvent("evt-1", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-1", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var provider = new FakePaymentProvider(providerEvent);
        var service = new PaymentWebhookService(db, [provider], lifecycle, NullLogger<PaymentWebhookService>.Instance);

        var first = await service.ProcessAsync("fake", "{\"event\":\"evt-1\"}", "valid");
        var duplicate = await service.ProcessAsync("fake", "{\"event\":\"evt-1\"}", "valid");

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Single(await db.PaymentEvents.ToListAsync());
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(db, new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true })), new NullNotificationEventWriter());

    private static Workspace NewWorkspace() => new()
    {
        Id = Guid.NewGuid(), Name = "Payment Workspace", Slug = $"payment-{Guid.NewGuid():N}", Type = WorkspaceType.Personal,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static Subscription NewSubscription(Guid workspaceId, Guid planId)
    {
        var start = DateTime.UtcNow.Date;
        return new Subscription
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, PlanId = planId, Status = SubscriptionStatus.Active,
            CurrentPeriodStart = start, CurrentPeriodEnd = start.AddMonths(1), NextRenewalAt = start.AddMonths(1),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
    }

    private sealed class FakePaymentProvider(ProviderPaymentEvent paymentEvent) : IPaymentProvider
    {
        public string Key => "fake";
        public IWebhookSignatureVerifier WebhookSignatureVerifier { get; } = new AcceptingSignatureVerifier();
        public Task<ProviderCheckoutSessionResult> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderCheckoutSessionResult("session-1", "https://example.test/checkout/session-1", DateTime.UtcNow.AddMinutes(30)));
        public ProviderPaymentEvent ParseWebhook(string rawPayload) => paymentEvent;
    }

    private sealed class AcceptingSignatureVerifier : IWebhookSignatureVerifier
    {
        public bool Verify(string rawPayload, string signature) => signature == "valid";
    }

    private sealed class NullNotificationEventWriter : INotificationEventWriter
    {
        public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

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
        var service = new CheckoutSessionService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "unconfigured" }), [], NullLogger<CheckoutSessionService>.Instance);

        var result = await service.CreateAsync(Guid.NewGuid(), "pro", "checkout:disabled", "https://example.test/success", "https://example.test/cancel");

        Assert.False(result.Available);
        Assert.Equal("CHECKOUT_DISABLED", result.Code);
        Assert.Empty(await db.CheckoutSessions.ToListAsync());
    }

    [Fact]
    public async Task Webhook_is_rejected_without_persisting_when_customer_charging_is_disabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var provider = new FakePaymentProvider(new ProviderPaymentEvent(
            "evt-disabled", PaymentEventType.PaymentSucceeded, null, null, null, null, "pay-disabled", 9, "USD",
            DateTime.UtcNow, "Payment succeeded.", null, null, null, false));
        var service = new PaymentWebhookService(
            db,
            [provider],
            NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-disabled\"}", "invalid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_WEBHOOK_DISABLED", result.Code);
        Assert.Empty(await db.PaymentEvents.ToListAsync());
    }

    [Fact]
    public async Task Webhook_rejects_a_provider_that_is_not_the_selected_launch_provider()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var provider = new FakePaymentProvider(new ProviderPaymentEvent(
            "evt-unselected", PaymentEventType.PaymentSucceeded, null, null, null, null, "pay-unselected", 9, "USD",
            DateTime.UtcNow, "Payment succeeded.", null, null, null, false));
        var service = new PaymentWebhookService(
            db,
            [provider],
            NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "other-provider" }),
            NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-unselected\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_PROVIDER_UNCONFIGURED", result.Code);
        Assert.Empty(await db.PaymentEvents.ToListAsync());
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
    public async Task Partial_refunds_do_not_reverse_credits_until_the_payment_is_fully_refunded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var creditLedger = new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true }));
        var grant = await creditLedger.GrantAsync(workspace.Id, CreditEntitlementType.Purchased, 1_000, "purchase:credits", "Purchased credits");
        var service = NewLifecycle(db);
        var attempt = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:partial-refund", 9, "USD", PaymentAttemptStatus.Succeeded);
        attempt.CreditLedgerEntryId = grant.Entry.Id;
        await db.SaveChangesAsync();

        var partial = await service.RecordRefundAsync(workspace.Id, attempt.Id, "test", 1, "USD", "refund:partial", "Partial refund.", "refund-partial");
        Assert.Equal(PaymentAttemptStatus.PartiallyRefunded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(1, await db.PaymentRefunds.CountAsync());
        Assert.Empty(await db.CreditLedgerEntries.Where(item => item.Type == CreditLedgerEntryType.Refund).ToListAsync());

        await service.RecordRefundAsync(workspace.Id, attempt.Id, "test", 8, "USD", "refund:remainder", "Refund remainder.", "refund-remainder");

        Assert.Equal(PaymentAttemptStatus.Refunded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Single(await db.CreditLedgerEntries.Where(item => item.Type == CreditLedgerEntryType.Refund).ToListAsync());
        Assert.Equal(grant.Entry.Id, (await db.CreditLedgerEntries.SingleAsync(item => item.Type == CreditLedgerEntryType.Refund)).ReversesEntryId);
        Assert.Equal(partial.PaymentAttemptId, attempt.Id);
    }

    [Fact]
    public async Task Reusing_a_payment_idempotency_key_with_different_amount_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);

        await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:conflict", 9, "USD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:conflict", 19, "USD"));
    }

    [Fact]
    public async Task Refunds_require_a_succeeded_payment_and_succeeded_attempts_cannot_be_marked_failed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);
        var created = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:created-refund", 9, "USD");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordRefundAsync(workspace.Id, created.Id, "test", 9, "USD", "refund:invalid", "Refund before payment success."));

        var succeeded = await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:succeeded-failed", 9, "USD", PaymentAttemptStatus.Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkPaymentFailedAsync(workspace.Id, succeeded.Id, "late_failure", "A late failure must not overwrite success."));
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == succeeded.Id)).Status);
    }

    [Fact]
    public async Task Subscription_activation_rolls_back_before_granting_if_credit_provisioning_fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"invalid-{Guid.NewGuid():N}", Name = "Invalid zero-credit plan",
            MonthlyCreditAllowance = 0, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.Status = SubscriptionStatus.PastDue;
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);
        var periodStart = DateTime.UtcNow.Date.AddMonths(1);
        var periodEnd = periodStart.AddMonths(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ActivateSubscriptionAsync(
            workspace.Id, subscription.Id, plan.Id, "test", "subscription-1", periodStart, periodEnd, "Activate subscription"));

        var persistedSubscription = await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.PastDue, persistedSubscription.Status);
        Assert.Equal(0, await db.SubscriptionLifecycleEvents.CountAsync());
        Assert.Equal(0, await db.BillingPeriods.CountAsync());
        Assert.Equal(0, await db.CreditEntitlements.CountAsync());
        Assert.Equal(0, await db.CreditLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Payment_attempt_replay_cannot_change_provider_reference()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var service = NewLifecycle(db);
        await service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:provider-reference", 9, "USD", providerPaymentReference: "payment-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordPaymentAttemptAsync(workspace.Id, "test", "attempt:provider-reference", 9, "USD", providerPaymentReference: "payment-2"));
        Assert.Single(await db.PaymentAttempts.ToListAsync());
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
        var service = new PaymentWebhookService(db, [provider], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var first = await service.ProcessAsync("fake", "{\"event\":\"evt-1\"}", "valid");
        var duplicate = await service.ProcessAsync("fake", "{\"event\":\"evt-1\"}", "valid");

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal("pay-1", (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).ProviderPaymentReference);
        Assert.Single(await db.PaymentEvents.ToListAsync());
    }

    [Fact]
    public async Task Webhook_rejects_a_conflicting_provider_payment_reference()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:reference-conflict", 9, "USD", providerPaymentReference: "pay-1");
        var providerEvent = new ProviderPaymentEvent(
            "evt-reference-conflict", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-2", 9, "USD",
            DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-reference-conflict\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        var persistedAttempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Created, persistedAttempt.Status);
        Assert.Equal("pay-1", persistedAttempt.ProviderPaymentReference);
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Webhook_rejects_a_payment_attempt_owned_by_another_provider()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "other-provider", "attempt:provider-conflict", 9, "USD");
        var providerEvent = new ProviderPaymentEvent(
            "evt-provider-conflict", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-1", 9, "USD",
            DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-provider-conflict\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Webhook_rejects_incomplete_payment_events_instead_of_marking_them_processed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var providerEvent = new ProviderPaymentEvent(
            "evt-incomplete-payment", PaymentEventType.PaymentSucceeded, null, null, null, null, "pay-1", 9, "USD",
            DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], NewLifecycle(db), Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-incomplete-payment\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        var saved = await db.PaymentEvents.SingleAsync();
        Assert.Equal(PaymentEventStatus.Rejected, saved.Status);
        Assert.Equal("The verified event was recorded but its domain transition was not applied.", saved.FailureReason);
    }

    [Fact]
    public async Task Webhook_rolls_back_payment_success_when_renewal_credit_provisioning_fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"invalid-renewal-{Guid.NewGuid():N}", Name = "Invalid zero-credit plan",
            MonthlyCreditAllowance = 0, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:renewal-atomicity", 9, "USD");
        var periodStart = DateTime.UtcNow.Date.AddMonths(1);
        var periodEnd = periodStart.AddMonths(1);
        var providerEvent = new ProviderPaymentEvent(
            "evt-renewal-atomicity", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id, "pay-atomicity", 9, "USD",
            DateTime.UtcNow, "Renewal succeeded.", null, periodStart, periodEnd, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-renewal-atomicity\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).Status);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.SingleAsync()).Status);
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        var db = new TaslimDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static PaymentLifecycleService NewLifecycle(TaslimDbContext db) => new(db, new CreditLedgerService(db, Options.Create(new BillingOptions { CustomerChargingEnabled = true })), new NullNotificationEventWriter(), NullLogger<PaymentLifecycleService>.Instance);

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

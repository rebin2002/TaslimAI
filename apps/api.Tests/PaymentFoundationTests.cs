using Microsoft.Data.Sqlite;
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
        var settlementVerifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(
            db,
            [provider],
            NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = false, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance,
            [settlementVerifier]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-disabled\"}", "invalid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_WEBHOOK_DISABLED", result.Code);
        Assert.Empty(await db.PaymentEvents.ToListAsync());
        Assert.Empty(await db.PaymentAttempts.ToListAsync());
        Assert.Empty(await db.CheckoutSessions.ToListAsync());
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
        Assert.Equal(0, provider.SignatureVerifier.CallCount);
        Assert.Equal(0, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
        Assert.Equal(0, settlementVerifier.CallCount);
    }

    [Fact]
    public async Task Webhook_rejects_invalid_signature_before_parsing_or_payment_side_effects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:invalid-signature", 9, "USD");
        var provider = new FakePaymentProvider(new ProviderPaymentEvent(
            "evt-invalid-signature", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id,
            "pay-invalid-signature", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false));
        var settlementVerifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(
            db,
            [provider],
            lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance,
            [settlementVerifier]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-invalid-signature\"}", "invalid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_SIGNATURE_INVALID", result.Code);
        Assert.False(result.Duplicate);
        Assert.Equal(1, provider.SignatureVerifier.CallCount);
        Assert.Equal(0, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
        Assert.Equal(0, settlementVerifier.CallCount);
        var unchangedAttempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Created, unchangedAttempt.Status);
        Assert.Null(unchangedAttempt.ProviderPaymentReference);
        Assert.Single(await db.PaymentAttempts.ToListAsync());
        Assert.Empty(await db.PaymentEvents.ToListAsync());
        Assert.Empty(await db.CheckoutSessions.ToListAsync());
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
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
        Assert.Empty(await db.PaymentAttempts.ToListAsync());
        Assert.Empty(await db.CheckoutSessions.ToListAsync());
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
        Assert.Equal(0, provider.SignatureVerifier.CallCount);
        Assert.Equal(0, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
    }

    [Theory]
    [InlineData("other-provider", "sub-real")]
    [InlineData("fake", "sub-unrelated")]
    [InlineData("fake", null)]
    public async Task Cancellation_webhook_rejects_unbound_provider_subscription_identity(
        string storedBillingProvider,
        string? eventProviderSubscriptionReference)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = DefaultPlanCatalog.All.Single(item => item.Code == "pro");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = storedBillingProvider;
        subscription.ProviderSubscriptionReference = "sub-real";
        db.Workspaces.Add(workspace);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var originalPeriodStart = subscription.CurrentPeriodStart;
        var originalPeriodEnd = subscription.CurrentPeriodEnd;
        var providerEvent = new ProviderPaymentEvent(
            $"evt-cancel-unbound-{Guid.NewGuid():N}", PaymentEventType.SubscriptionCancelled,
            workspace.Id, subscription.Id, null, null, null, null, "USD", DateTime.UtcNow,
            "Provider cancellation callback.", eventProviderSubscriptionReference, null, null, false);
        var provider = new FakePaymentProvider(providerEvent);
        var service = new PaymentWebhookService(
            db, [provider], NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync(
            "fake", $"{{\"event\":\"{providerEvent.ProviderEventReference}\"}}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        var unchanged = await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.Active, unchanged.Status);
        Assert.Equal(originalPeriodStart, unchanged.CurrentPeriodStart);
        Assert.Equal(originalPeriodEnd, unchanged.CurrentPeriodEnd);
        Assert.False(unchanged.CancelAtPeriodEnd);
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
        var savedEvent = await db.PaymentEvents.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentEventStatus.Rejected, savedEvent.Status);
        Assert.Equal("The verified event was recorded but its domain transition was not applied.", savedEvent.FailureReason);
        Assert.Empty(await db.PaymentAttempts.ToListAsync());
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Equal(1, provider.SignatureVerifier.CallCount);
        Assert.Equal(1, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
    }

    [Fact]
    public async Task Cancellation_webhook_with_matching_provider_binding_is_replay_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = DefaultPlanCatalog.All.Single(item => item.Code == "pro");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-real";
        db.Workspaces.Add(workspace);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var providerEvent = new ProviderPaymentEvent(
            "evt-cancel-bound", PaymentEventType.SubscriptionCancelled,
            workspace.Id, subscription.Id, null, null, null, null, "USD", DateTime.UtcNow,
            "Provider cancellation callback.", "sub-real", null, null, false);
        var provider = new FakePaymentProvider(providerEvent);
        var service = new PaymentWebhookService(
            db, [provider], NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);
        const string rawPayload = "{\"event\":\"evt-cancel-bound\"}";

        var first = await service.ProcessAsync("fake", rawPayload, "valid");
        var duplicate = await service.ProcessAsync("fake", rawPayload, "valid");

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(SubscriptionStatus.Cancelled,
            (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).Status);
        var lifecycleEvent = Assert.Single(await db.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync());
        Assert.Equal(SubscriptionLifecycleEventType.Cancelled, lifecycleEvent.Type);
        Assert.Equal(PaymentEventStatus.Processed,
            (await db.PaymentEvents.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, provider.SignatureVerifier.CallCount);
        Assert.Equal(2, provider.ParseCallCount);
        Assert.Empty(await db.PaymentAttempts.ToListAsync());
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
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

    [Theory]
    [InlineData(PaymentEventType.PaymentFailed, true)]
    [InlineData(PaymentEventType.PaymentFailed, false)]
    [InlineData(PaymentEventType.RenewalFailed, true)]
    [InlineData(PaymentEventType.RenewalFailed, false)]
    public async Task Stale_subscription_payment_failure_after_newer_funding_fails_closed(
        PaymentEventType failureType,
        bool includeStaleCycle)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"stale-failure-{Guid.NewGuid():N}", Name = "Stale failure test plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-failure-cycle";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attemptedPeriodStart = subscription.CurrentPeriodEnd;
        var attemptedPeriodEnd = attemptedPeriodStart.AddMonths(1);
        var olderAttempt = await lifecycle.RecordPaymentAttemptAsync(
            workspace.Id, "fake", "attempt:old-cycle", 9, "USD", subscriptionId: subscription.Id,
            providerPaymentReference: "pay-old-cycle");

        await lifecycle.RenewSubscriptionAsync(workspace.Id, subscription.Id,
            attemptedPeriodStart, attemptedPeriodEnd, "renewal:funded-current-cycle", "A newer payment funded this cycle.");
        var newerSucceededAttempt = await lifecycle.RecordPaymentAttemptAsync(
            workspace.Id, "fake", "attempt:new-cycle", 9, "USD", PaymentAttemptStatus.Succeeded,
            subscriptionId: subscription.Id, providerPaymentReference: "pay-new-cycle");
        var ledgerCountBefore = await db.CreditLedgerEntries.CountAsync();
        var ledgerBalanceBefore = await db.CreditLedgerEntries.SumAsync(item => item.Amount);
        var providerEvent = new ProviderPaymentEvent(
            $"evt-stale-failure-{Guid.NewGuid():N}", failureType,
            workspace.Id, subscription.Id, null, olderAttempt.Id, "pay-old-cycle", 9, "USD", DateTime.UtcNow,
            "Older payment attempt failed late.", "sub-failure-cycle",
            includeStaleCycle ? attemptedPeriodStart : null,
            includeStaleCycle ? attemptedPeriodEnd : null,
            false);
        var provider = new FakePaymentProvider(providerEvent);
        var service = new PaymentWebhookService(
            db, [provider], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);
        var rawPayload = $"{{\"event\":\"{providerEvent.ProviderEventReference}\"}}";

        var result = await service.ProcessAsync("fake", rawPayload, "valid");
        var replay = await service.ProcessAsync("fake", rawPayload, "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.True(replay.Duplicate);
        Assert.Equal(PaymentAttemptStatus.Created,
            (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == olderAttempt.Id)).Status);
        Assert.Equal(PaymentAttemptStatus.Succeeded,
            (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == newerSucceededAttempt.Id)).Status);
        var currentSubscription = await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        Assert.Equal(SubscriptionStatus.Active, currentSubscription.Status);
        Assert.Equal(attemptedPeriodStart, currentSubscription.CurrentPeriodStart);
        Assert.Equal(attemptedPeriodEnd, currentSubscription.CurrentPeriodEnd);
        Assert.Equal(1, await db.PaymentEvents.CountAsync());
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.AsNoTracking().SingleAsync()).Status);
        var lifecycleEvents = await db.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync();
        Assert.Single(lifecycleEvents);
        Assert.Equal(SubscriptionLifecycleEventType.Renewed, lifecycleEvents[0].Type);
        Assert.Single(await db.BillingPeriods.ToListAsync());
        Assert.Single(await db.CreditEntitlements.ToListAsync());
        Assert.Equal(ledgerCountBefore, await db.CreditLedgerEntries.CountAsync());
        Assert.Equal(ledgerBalanceBefore, await db.CreditLedgerEntries.SumAsync(item => item.Amount));
        Assert.Empty(await db.PaymentRefunds.ToListAsync());
        Assert.Equal(2, provider.SignatureVerifier.CallCount);
        Assert.Equal(2, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
    }

    [Fact]
    public async Task Current_due_renewal_failure_keeps_past_due_behavior_and_replay_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"current-failure-{Guid.NewGuid():N}", Name = "Current failure test plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-current-due";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(
            workspace.Id, "fake", "attempt:current-due", 9, "USD", subscriptionId: subscription.Id,
            providerPaymentReference: "pay-current-due");
        var providerEvent = new ProviderPaymentEvent(
            "evt-current-due-failure", PaymentEventType.RenewalFailed,
            workspace.Id, subscription.Id, null, attempt.Id, "pay-current-due", 9, "USD", DateTime.UtcNow,
            "Currently due renewal failed.", "sub-current-due", periodStart, periodEnd, false);
        var provider = new FakePaymentProvider(providerEvent);
        var service = new PaymentWebhookService(
            db, [provider], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);
        const string rawPayload = "{\"event\":\"evt-current-due-failure\"}";

        var first = await service.ProcessAsync("fake", rawPayload, "valid");
        var duplicate = await service.ProcessAsync("fake", rawPayload, "valid");

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Accepted);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(PaymentAttemptStatus.Failed,
            (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(SubscriptionStatus.PastDue,
            (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).Status);
        var lifecycleEvent = Assert.Single(await db.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync());
        Assert.Equal(SubscriptionLifecycleEventType.PaymentFailed, lifecycleEvent.Type);
        Assert.Equal(PaymentEventStatus.Processed,
            (await db.PaymentEvents.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Equal(2, provider.SignatureVerifier.CallCount);
        Assert.Equal(2, provider.ParseCallCount);
        Assert.Equal(0, provider.CheckoutCreateCallCount);
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
        var verifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(db, [provider], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance, [verifier]);

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
    public async Task Concurrent_same_event_webhook_deliveries_apply_only_one_renewal_and_credit_grant()
    {
        var databaseName = $"payment-webhook-concurrency-{Guid.NewGuid():N}";
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseName,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15,
        }.ToString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();

        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"concurrent-{Guid.NewGuid():N}", Name = "Concurrent replay test plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-concurrent-replay";
        PaymentAttempt attempt;
        DateTime periodStart;
        DateTime periodEnd;
        await using (var setupDb = CreateDb(anchor))
        {
            setupDb.Workspaces.Add(workspace);
            setupDb.Plans.Add(plan);
            setupDb.Subscriptions.Add(subscription);
            await setupDb.SaveChangesAsync();
            attempt = await NewLifecycle(setupDb).RecordPaymentAttemptAsync(
                workspace.Id, "fake", "attempt:concurrent-replay", 9, "USD", subscriptionId: subscription.Id);
            periodStart = subscription.CurrentPeriodEnd;
            periodEnd = periodStart.AddMonths(1);
        }

        await using var connectionA = new SqliteConnection(connectionString);
        await using var connectionB = new SqliteConnection(connectionString);
        await connectionA.OpenAsync();
        await connectionB.OpenAsync();
        await using var dbA = CreateDb(connectionA);
        await using var dbB = CreateDb(connectionB);
        var providerEvent = new ProviderPaymentEvent(
            "evt-concurrent-replay", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
            "pay-concurrent-replay", 9, "USD", DateTime.UtcNow, "Renewal succeeded.",
            "sub-concurrent-replay", periodStart, periodEnd, false);
        var providerA = new FakePaymentProvider(providerEvent);
        var providerB = new FakePaymentProvider(providerEvent);
        var verifier = new FakePaymentSettlementVerifier();
        var settings = Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" });
        var serviceA = new PaymentWebhookService(dbA, [providerA], NewLifecycle(dbA), settings,
            NullLogger<PaymentWebhookService>.Instance, [verifier]);
        var serviceB = new PaymentWebhookService(dbB, [providerB], NewLifecycle(dbB), settings,
            NullLogger<PaymentWebhookService>.Instance, [verifier]);
        const string rawPayload = "{\"event\":\"evt-concurrent-replay\"}";

        var results = await Task.WhenAll(
            serviceA.ProcessAsync("fake", rawPayload, "valid"),
            serviceB.ProcessAsync("fake", rawPayload, "valid"));

        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Single(results, result => !result.Duplicate);
        Assert.Single(results, result => result.Duplicate);
        Assert.Equal(1, verifier.CallCount);
        Assert.Equal(1, providerA.SignatureVerifier.CallCount);
        Assert.Equal(1, providerB.SignatureVerifier.CallCount);
        Assert.Equal(1, providerA.ParseCallCount);
        Assert.Equal(1, providerB.ParseCallCount);
        Assert.Equal(0, providerA.CheckoutCreateCallCount + providerB.CheckoutCreateCallCount);

        var savedAttempt = await dbA.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, savedAttempt.Status);
        Assert.Equal("pay-concurrent-replay", savedAttempt.ProviderPaymentReference);
        var savedSubscription = await dbA.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        Assert.Equal(periodEnd, savedSubscription.CurrentPeriodEnd);
        var paymentEvent = await dbA.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == "evt-concurrent-replay");
        Assert.Equal(PaymentEventStatus.Processed, paymentEvent.Status);
        Assert.Single(await dbA.BillingPeriods.AsNoTracking().ToListAsync());
        Assert.Single(await dbA.CreditEntitlements.AsNoTracking().ToListAsync());
        var grant = Assert.Single(await dbA.CreditLedgerEntries.AsNoTracking().ToListAsync());
        Assert.Equal(CreditLedgerEntryType.Grant, grant.Type);
        Assert.Equal(100, grant.Amount);
        Assert.Empty(await dbA.PaymentRefunds.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Concurrent_same_event_replays_call_settlement_verifier_once_while_claim_is_held()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"payment-webhook-fanout-{Guid.NewGuid():N}.db");
        var verifier = new BlockingCountingPaymentSettlementVerifier();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WebhookProcessingResult>[] tasks = [];
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                DefaultTimeout = 30,
            }.ToString();
            var workspace = NewWorkspace();
            var plan = new Plan
            {
                Id = Guid.NewGuid(), Code = $"fanout-{Guid.NewGuid():N}", Name = "Verifier fanout test plan",
                MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var subscription = NewSubscription(workspace.Id, plan.Id);
            subscription.BillingProvider = "fake";
            subscription.ProviderSubscriptionReference = "sub-verifier-fanout";
            PaymentAttempt attempt;
            DateTime periodStart;
            DateTime periodEnd;
            await using (var setupConnection = new SqliteConnection(connectionString))
            {
                await setupConnection.OpenAsync();
                await using var journalModeCommand = setupConnection.CreateCommand();
                journalModeCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await journalModeCommand.ExecuteNonQueryAsync();
                await using var setupDb = CreateDb(setupConnection);
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Workspaces.Add(workspace);
                setupDb.Plans.Add(plan);
                setupDb.Subscriptions.Add(subscription);
                await setupDb.SaveChangesAsync();
                attempt = await NewLifecycle(setupDb).RecordPaymentAttemptAsync(
                    workspace.Id, "fake", "attempt:verifier-fanout", 9, "USD", subscriptionId: subscription.Id);
                periodStart = subscription.CurrentPeriodEnd;
                periodEnd = periodStart.AddMonths(1);
            }

            const int deliveryCount = 20;
            const string rawPayload = "{\"event\":\"evt-verifier-fanout\"}";
            var occurredAt = DateTime.UtcNow;
            var settings = Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" });
            using var ready = new CountdownEvent(deliveryCount);
            tasks = Enumerable.Range(0, deliveryCount).Select(async _ =>
            {
                ready.Signal();
                await start.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var db = CreateDb(connection);
                var providerEvent = new ProviderPaymentEvent(
                    "evt-verifier-fanout", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
                    "pay-verifier-fanout", 9, "USD", occurredAt, "Synthetic renewal succeeded.",
                    "sub-verifier-fanout", periodStart, periodEnd, false);
                var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], NewLifecycle(db), settings,
                    NullLogger<PaymentWebhookService>.Instance, [verifier]);
                return await service.ProcessAsync("fake", rawPayload, "valid");
            }).ToArray();

            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "All replay workers should reach the common start gate.");
            start.TrySetResult(true);
            await verifier.FirstCall.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            var callsWhileFirstVerificationBlocked = verifier.CallCount;
            verifier.Release();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, result => Assert.True(result.Accepted));
            Assert.Single(results, result => !result.Duplicate);
            Assert.Equal(deliveryCount - 1, results.Count(result => result.Duplicate));
            Assert.Equal(1, callsWhileFirstVerificationBlocked);
            Assert.Equal(1, verifier.CallCount);

            await using (var finalConnection = new SqliteConnection(connectionString))
            {
                await finalConnection.OpenAsync();
                await using var finalDb = CreateDb(finalConnection);
                Assert.Equal(PaymentAttemptStatus.Succeeded,
                    (await finalDb.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
                Assert.Equal(PaymentEventStatus.Processed,
                    (await finalDb.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == "evt-verifier-fanout")).Status);
                Assert.Single(await finalDb.BillingPeriods.AsNoTracking().ToListAsync());
                Assert.Single(await finalDb.CreditEntitlements.AsNoTracking().ToListAsync());
                var grant = Assert.Single(await finalDb.CreditLedgerEntries.AsNoTracking().ToListAsync());
                Assert.Equal(CreditLedgerEntryType.Grant, grant.Type);
                Assert.Equal(100, grant.Amount);
            }

        }
        finally
        {
            start.TrySetResult(true);
            verifier.Release();
            try { await Task.WhenAll(tasks); } catch { }
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Signed_cross_workspace_renewals_fail_closed_and_are_audited_without_account_mutation()
    {
        await using var testDatabase = await DisposablePaymentTestDatabase.CreateAsync();
        var workspaceA = NewWorkspace();
        var workspaceB = NewWorkspace();
        var plan = NewTestPlan("cross");
        var subscriptionA = NewSubscription(workspaceA.Id, plan.Id);
        subscriptionA.BillingProvider = "fake";
        subscriptionA.ProviderSubscriptionReference = "sub-cross-a";
        var subscriptionB = NewSubscription(workspaceB.Id, plan.Id);
        subscriptionB.BillingProvider = "fake";
        subscriptionB.ProviderSubscriptionReference = "sub-cross-b";
        var subscriptionAPeriodStart = subscriptionA.CurrentPeriodEnd;
        var subscriptionAPeriodEnd = subscriptionAPeriodStart.AddMonths(1);
        PaymentAttempt attemptA;
        PaymentAttempt attemptB;

        await using (var setupDb = new TaslimDbContext(testDatabase.Options))
        {
            setupDb.Workspaces.AddRange(workspaceA, workspaceB);
            setupDb.Plans.Add(plan);
            setupDb.Subscriptions.AddRange(subscriptionA, subscriptionB);
            await setupDb.SaveChangesAsync();
            var lifecycle = NewLifecycle(setupDb);
            attemptA = await lifecycle.RecordPaymentAttemptAsync(
                workspaceA.Id, "fake", "attempt:cross-workspace-a", 9, "USD", subscriptionId: subscriptionA.Id);
            attemptB = await lifecycle.RecordPaymentAttemptAsync(
                workspaceB.Id, "fake", "attempt:cross-workspace-b", 9, "USD", subscriptionId: subscriptionB.Id);
        }

        var scenarios = new[]
        {
            new CrossWorkspaceRenewalScenario("evt-cross-attempt", attemptA, subscriptionA),
            new CrossWorkspaceRenewalScenario("evt-cross-subscription", attemptB, subscriptionA),
        };
        var verifier = new FakePaymentSettlementVerifier();
        var providers = new List<FakePaymentProvider>();
        foreach (var scenario in scenarios)
        {
            var rawPayload = $"{{\"event\":\"{scenario.EventReference}\",\"workspaceId\":\"{workspaceB.Id:D}\",\"subscriptionId\":\"{scenario.Subscription.Id:D}\",\"paymentAttemptId\":\"{scenario.Attempt.Id:D}\"}}";
            var providerEvent = new ProviderPaymentEvent(
                scenario.EventReference, PaymentEventType.RenewalSucceeded, workspaceB.Id, scenario.Subscription.Id,
                null, scenario.Attempt.Id, $"pay-{scenario.EventReference}", scenario.Attempt.Amount, scenario.Attempt.Currency,
                DateTime.UtcNow, "Synthetic signed cross-workspace renewal.", scenario.Subscription.ProviderSubscriptionReference,
                subscriptionAPeriodStart, subscriptionAPeriodEnd, false);
            var provider = new FakePaymentProvider(providerEvent);
            providers.Add(provider);
            await using var db = new TaslimDbContext(testDatabase.Options);
            var service = new PaymentWebhookService(
                db, [provider], NewLifecycle(db),
                Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
                NullLogger<PaymentWebhookService>.Instance, [verifier]);

            var result = await service.ProcessAsync("fake", rawPayload, "valid");

            Assert.False(result.Accepted);
            Assert.False(result.Duplicate);
            Assert.Equal("PAYMENT_REFERENCE_PENDING", result.Code);
            Assert.Equal(1, provider.SignatureVerifier.CallCount);
            Assert.Equal(1, provider.ParseCallCount);
            Assert.Equal(0, verifier.CallCount);
        }

        await using var auditDb = new TaslimDbContext(testDatabase.Options);
        var auditEvents = await auditDb.PaymentEvents.AsNoTracking().OrderBy(item => item.ProviderEventReference).ToListAsync();
        Assert.Equal(2, auditEvents.Count);
        foreach (var scenario in scenarios)
        {
            var rawPayload = $"{{\"event\":\"{scenario.EventReference}\",\"workspaceId\":\"{workspaceB.Id:D}\",\"subscriptionId\":\"{scenario.Subscription.Id:D}\",\"paymentAttemptId\":\"{scenario.Attempt.Id:D}\"}}";
            var saved = Assert.Single(auditEvents, item => item.ProviderEventReference == scenario.EventReference);
            Assert.Equal(PaymentEventStatus.Received, saved.Status);
            Assert.True(saved.SignatureVerified);
            Assert.Null(saved.WorkspaceId);
            Assert.Null(saved.SubscriptionId);
            Assert.Null(saved.PaymentAttemptId);
            Assert.Null(saved.CheckoutSessionId);
            Assert.Equal(rawPayload, saved.PayloadJson);
            Assert.False(string.IsNullOrWhiteSpace(saved.PayloadHash));
            Assert.Null(saved.ProcessedAt);
        }

        var savedAttempts = await auditDb.PaymentAttempts.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, savedAttempts.Count);
        Assert.All(savedAttempts, item =>
        {
            Assert.Equal(PaymentAttemptStatus.Created, item.Status);
            Assert.Null(item.ProviderPaymentReference);
            Assert.Null(item.CreditLedgerEntryId);
            Assert.Null(item.SucceededAt);
        });
        var savedSubscriptions = await auditDb.Subscriptions.AsNoTracking().OrderBy(item => item.Id).ToListAsync();
        Assert.Equal(2, savedSubscriptions.Count);
        Assert.All(savedSubscriptions, item => Assert.Equal(SubscriptionStatus.Active, item.Status));
        Assert.Equal(subscriptionAPeriodStart, savedSubscriptions.Single(item => item.Id == subscriptionA.Id).CurrentPeriodEnd);
        Assert.Empty(await auditDb.BillingPeriods.AsNoTracking().ToListAsync());
        Assert.Empty(await auditDb.CreditEntitlements.AsNoTracking().ToListAsync());
        Assert.Empty(await auditDb.CreditLedgerEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await auditDb.PaymentRefunds.AsNoTracking().ToListAsync());
        Assert.Empty(await auditDb.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync());
        Assert.Equal(2, providers.Count);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Verifier_timeout_releases_claim_and_same_event_retry_grants_exactly_once()
    {
        await using var testDatabase = await DisposablePaymentTestDatabase.CreateAsync();
        var workspace = NewWorkspace();
        var plan = NewTestPlan("timeout");
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-timeout-retry";
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var occurredAt = DateTime.UtcNow;
        const string eventReference = "evt-verifier-timeout-retry";
        const string signature = "valid";
        PaymentAttempt attempt;
        await using (var setupDb = new TaslimDbContext(testDatabase.Options))
        {
            setupDb.Workspaces.Add(workspace);
            setupDb.Plans.Add(plan);
            setupDb.Subscriptions.Add(subscription);
            await setupDb.SaveChangesAsync();
            attempt = await NewLifecycle(setupDb).RecordPaymentAttemptAsync(
                workspace.Id, "fake", "attempt:verifier-timeout-retry", 9, "USD", subscriptionId: subscription.Id);
        }

        var rawPayload = $"{{\"event\":\"{eventReference}\",\"workspaceId\":\"{workspace.Id:D}\",\"subscriptionId\":\"{subscription.Id:D}\",\"paymentAttemptId\":\"{attempt.Id:D}\"}}";
        var providerEvent = new ProviderPaymentEvent(
            eventReference, PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
            "pay-verifier-timeout-retry", 9, "USD", occurredAt, "Synthetic renewal succeeded.",
            "sub-timeout-retry", periodStart, periodEnd, false);
        var verifier = new CancelFirstThenSuccessfulPaymentSettlementVerifier();
        var settings = Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" });
        var firstProvider = new FakePaymentProvider(providerEvent);
        FakePaymentProvider? concurrentProvider = null;
        WebhookProcessingResult? concurrentResult = null;
        Task<WebhookProcessingResult>? firstTask = null;
        Task<WebhookProcessingResult>? concurrentTask = null;
        using (var cancellation = new CancellationTokenSource())
        {
            using var concurrentCancellation = new CancellationTokenSource();
            await using var firstDb = new TaslimDbContext(testDatabase.Options);
            await using var concurrentDb = new TaslimDbContext(testDatabase.Options);
            var firstService = new PaymentWebhookService(
                firstDb, [firstProvider], NewLifecycle(firstDb), settings,
                NullLogger<PaymentWebhookService>.Instance, [verifier]);
            try
            {
                var firstRequest = firstService.ProcessAsync("fake", rawPayload, signature, cancellation.Token);
                firstTask = firstRequest;
                await verifier.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, verifier.CallCount);

                await using (var heldDb = new TaslimDbContext(testDatabase.Options))
                {
                    var visibleEvent = await heldDb.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == eventReference);
                    Assert.Equal(PaymentEventStatus.Received, visibleEvent.Status);
                    Assert.Null(visibleEvent.WorkspaceId);
                    Assert.Empty(await heldDb.BillingPeriods.AsNoTracking().ToListAsync());
                    Assert.Empty(await heldDb.CreditEntitlements.AsNoTracking().ToListAsync());
                    Assert.Empty(await heldDb.CreditLedgerEntries.AsNoTracking().ToListAsync());
                }

                if (testDatabase.UsesPostgres)
                {
                    var concurrentParsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    concurrentProvider = new FakePaymentProvider(providerEvent, concurrentParsed);
                    var concurrentService = new PaymentWebhookService(
                        concurrentDb, [concurrentProvider], NewLifecycle(concurrentDb), settings,
                        NullLogger<PaymentWebhookService>.Instance, [verifier]);
                    var concurrentRequest = concurrentService.ProcessAsync("fake", rawPayload, signature, concurrentCancellation.Token);
                    concurrentTask = concurrentRequest;
                    await concurrentParsed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await testDatabase.WaitForPostgresLockWaitAsync(TimeSpan.FromSeconds(10));
                    Assert.False(concurrentRequest.IsCompleted,
                        "A same-event delivery should remain blocked on the verifier-held claim.");
                    Assert.Equal(1, verifier.CallCount);
                }

                cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await firstRequest.WaitAsync(TimeSpan.FromSeconds(10)));
                if (concurrentTask is not null)
                    concurrentResult = await concurrentTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                cancellation.Cancel();
                concurrentCancellation.Cancel();
                await ObserveTaskCompletionAsync(firstTask);
                await ObserveTaskCompletionAsync(concurrentTask);
            }
        }
        Assert.Equal(1, firstProvider.SignatureVerifier.CallCount);
        Assert.Equal(1, firstProvider.ParseCallCount);
        if (concurrentProvider is not null)
        {
            Assert.Equal(1, concurrentProvider.SignatureVerifier.CallCount);
            Assert.Equal(1, concurrentProvider.ParseCallCount);
        }
        if (concurrentResult is { Accepted: true })
        {
            Assert.False(concurrentResult.Duplicate);
            Assert.Equal("WEBHOOK_PROCESSED", concurrentResult.Code);
        }
        else if (concurrentResult is not null)
        {
            Assert.Equal("WEBHOOK_EVENT_RETRY_REQUIRED", concurrentResult.Code);
        }

        if (concurrentResult is null || !concurrentResult.Accepted)
        {
            await using var afterCancelDb = new TaslimDbContext(testDatabase.Options);
            var received = await afterCancelDb.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == eventReference);
            Assert.Equal(PaymentEventStatus.Received, received.Status);
            Assert.True(received.SignatureVerified);
            Assert.Null(received.WorkspaceId);
            Assert.Null(received.SubscriptionId);
            Assert.Null(received.PaymentAttemptId);
            Assert.Null(received.CheckoutSessionId);
            Assert.Equal(rawPayload, received.PayloadJson);
            Assert.Null(received.ProcessedAt);
            var unchangedAttempt = await afterCancelDb.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
            Assert.Equal(PaymentAttemptStatus.Created, unchangedAttempt.Status);
            Assert.Null(unchangedAttempt.ProviderPaymentReference);
            var unchangedSubscription = await afterCancelDb.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
            Assert.Equal(SubscriptionStatus.Active, unchangedSubscription.Status);
            Assert.Equal(periodStart, unchangedSubscription.CurrentPeriodEnd);
            Assert.Empty(await afterCancelDb.BillingPeriods.AsNoTracking().ToListAsync());
            Assert.Empty(await afterCancelDb.CreditEntitlements.AsNoTracking().ToListAsync());
            Assert.Empty(await afterCancelDb.CreditLedgerEntries.AsNoTracking().ToListAsync());
            Assert.Empty(await afterCancelDb.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync());
        }

        var retryProvider = new FakePaymentProvider(providerEvent);
        WebhookProcessingResult retry;
        await using (var retryDb = new TaslimDbContext(testDatabase.Options))
        {
            var retryService = new PaymentWebhookService(
                retryDb, [retryProvider], NewLifecycle(retryDb), settings,
                NullLogger<PaymentWebhookService>.Instance, [verifier]);
            retry = await retryService.ProcessAsync("fake", rawPayload, signature)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(retry.Accepted);
        var concurrentReplayProcessed = concurrentResult?.Accepted == true;
        Assert.Equal(concurrentReplayProcessed, retry.Duplicate);
        Assert.Equal(concurrentReplayProcessed ? "WEBHOOK_DUPLICATE" : "WEBHOOK_PROCESSED", retry.Code);
        Assert.Equal(2, verifier.CallCount);
        Assert.Equal(1, retryProvider.SignatureVerifier.CallCount);
        Assert.Equal(1, retryProvider.ParseCallCount);

        await using (var duplicateDb = new TaslimDbContext(testDatabase.Options))
        {
            var duplicateProvider = new FakePaymentProvider(providerEvent);
            var duplicateService = new PaymentWebhookService(
                duplicateDb, [duplicateProvider], NewLifecycle(duplicateDb), settings,
                NullLogger<PaymentWebhookService>.Instance, [verifier]);
            var duplicate = await duplicateService.ProcessAsync("fake", rawPayload, signature)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(duplicate.Accepted);
            Assert.True(duplicate.Duplicate);
            Assert.Equal("WEBHOOK_DUPLICATE", duplicate.Code);
            Assert.Equal(2, verifier.CallCount);
        }

        await using var finalDb = new TaslimDbContext(testDatabase.Options);
        var processedEvent = await finalDb.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == eventReference);
        Assert.Equal(PaymentEventStatus.Processed, processedEvent.Status);
        Assert.Equal(workspace.Id, processedEvent.WorkspaceId);
        Assert.Equal(subscription.Id, processedEvent.SubscriptionId);
        Assert.Equal(attempt.Id, processedEvent.PaymentAttemptId);
        Assert.Null(processedEvent.FailureReason);
        var succeededAttempt = await finalDb.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
        Assert.Equal(PaymentAttemptStatus.Succeeded, succeededAttempt.Status);
        Assert.Equal("pay-verifier-timeout-retry", succeededAttempt.ProviderPaymentReference);
        Assert.Equal(periodEnd, (await finalDb.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).CurrentPeriodEnd);
        var billingPeriod = Assert.Single(await finalDb.BillingPeriods.AsNoTracking().ToListAsync());
        Assert.Equal(subscription.Id, billingPeriod.SubscriptionId);
        Assert.Equal(periodStart, billingPeriod.StartsAt);
        Assert.Equal(periodEnd, billingPeriod.EndsAt);
        Assert.Equal(100L, billingPeriod.IncludedCredits);
        var entitlement = Assert.Single(await finalDb.CreditEntitlements.AsNoTracking().ToListAsync());
        Assert.Equal(workspace.Id, entitlement.WorkspaceId);
        Assert.Equal(billingPeriod.Id, entitlement.BillingPeriodId);
        Assert.Equal(CreditEntitlementType.IncludedMonthly, entitlement.Type);
        Assert.Equal(100L, entitlement.GrantedCredits);
        var grant = Assert.Single(await finalDb.CreditLedgerEntries.AsNoTracking().ToListAsync());
        Assert.Equal(workspace.Id, grant.WorkspaceId);
        Assert.Equal(entitlement.Id, grant.CreditEntitlementId);
        Assert.Equal(CreditLedgerEntryType.Grant, grant.Type);
        Assert.Equal(100L, grant.Amount);
        Assert.Single(await finalDb.SubscriptionLifecycleEvents.AsNoTracking().ToListAsync());
        Assert.Empty(await finalDb.PaymentRefunds.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Pending_webhook_is_durable_and_retries_after_provider_API_reports_completion()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:unverified", 9, "USD");
        var providerEvent = new ProviderPaymentEvent("evt-unverified", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-unverified", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var provider = new FakePaymentProvider(providerEvent);
        var verificationCalls = 0;
        var verifier = new FakePaymentSettlementVerifier((attemptSnapshot, _, paymentEvent) =>
            Interlocked.Increment(ref verificationCalls) == 1
                ? new(false, paymentEvent.ProviderPaymentReference, paymentEvent.Amount, paymentEvent.Currency, null, "Provider still reports pending.")
                : new PaymentSettlementVerification(
                    ProviderReportsCompleted: true,
                    ProviderPaymentReference: paymentEvent.ProviderPaymentReference,
                    Amount: attemptSnapshot.Amount,
                    Currency: attemptSnapshot.Currency,
                    CompletedAt: DateTime.UtcNow,
                    ProviderConfirmsFundsSettledOrAvailable: true));
        var service = new PaymentWebhookService(db, [provider], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var pending = await service.ProcessAsync("fake", "{\"event\":\"evt-unverified\"}", "valid");

        Assert.False(pending.Accepted);
        Assert.Equal("PAYMENT_SETTLEMENT_UNVERIFIED", pending.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Received, (await db.PaymentEvents.SingleAsync()).Status);

        var completed = await service.ProcessAsync("fake", "{\"event\":\"evt-unverified\"}", "valid");

        Assert.True(completed.Accepted);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Processed, (await db.PaymentEvents.SingleAsync()).Status);
        Assert.Equal(2, verifier.CallCount);
    }

    [Fact]
    public async Task Settlement_verifier_exception_keeps_event_retryable_without_ledger_mutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"verifier-error-{Guid.NewGuid():N}", Name = "Verifier exception recovery plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-verifier-error";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();

        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(
            workspace.Id, "fake", "attempt:verifier-error", 9, "USD", subscriptionId: subscription.Id);
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var occurredAt = DateTime.UtcNow;
        var providerEvent = new ProviderPaymentEvent(
            "evt-verifier-error", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
            "pay-verifier-error", 9, "USD", occurredAt, "Synthetic renewal succeeded.",
            "sub-verifier-error", periodStart, periodEnd, false);
        const string rawPayload = "{\"event\":\"evt-verifier-error\"}";
        var options = Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" });
        var failedVerifier = new FakePaymentSettlementVerifier((_, _, _) =>
            throw new InvalidOperationException("synthetic verifier failure"));
        var failedService = new PaymentWebhookService(
            db, [new FakePaymentProvider(providerEvent)], lifecycle, options,
            NullLogger<PaymentWebhookService>.Instance, [failedVerifier]);

        var failed = await failedService.ProcessAsync("fake", rawPayload, "valid");

        Assert.False(failed.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", failed.Code);
        Assert.Equal(PaymentEventStatus.Received, (await db.PaymentEvents.SingleAsync()).Status);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(periodStart, (await db.Subscriptions.SingleAsync(item => item.Id == subscription.Id)).CurrentPeriodEnd);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());

        var retryVerifier = new FakePaymentSettlementVerifier();
        var retryService = new PaymentWebhookService(
            db, [new FakePaymentProvider(providerEvent)], lifecycle, options,
            NullLogger<PaymentWebhookService>.Instance, [retryVerifier]);
        var retried = await retryService.ProcessAsync("fake", rawPayload, "valid");

        Assert.True(retried.Accepted);
        Assert.Equal(PaymentEventStatus.Processed, (await db.PaymentEvents.SingleAsync()).Status);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(periodEnd, (await db.Subscriptions.SingleAsync(item => item.Id == subscription.Id)).CurrentPeriodEnd);
        Assert.Single(await db.BillingPeriods.ToListAsync());
        Assert.Single(await db.CreditEntitlements.ToListAsync());
        Assert.Single(await db.CreditLedgerEntries.ToListAsync());
        Assert.Equal(1, failedVerifier.CallCount);
        Assert.Equal(1, retryVerifier.CallCount);
    }

    [Fact]
    public async Task Completed_renewal_without_final_funds_confirmation_does_not_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"unsettled-{Guid.NewGuid():N}", Name = "Pending Settlement Test Plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-final-funds-pending";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:final-funds-pending", 9, "USD", subscriptionId: subscription.Id);
        var originalPeriodEnd = subscription.CurrentPeriodEnd;
        var providerEvent = new ProviderPaymentEvent(
            "evt-final-funds-pending", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
            "pay-final-funds-pending", 9, "USD", DateTime.UtcNow, "Checkout complete.", "sub-final-funds-pending",
            originalPeriodEnd, originalPeriodEnd.AddMonths(1), false);
        var verifier = new FakePaymentSettlementVerifier((attemptSnapshot, _, paymentEvent) => new PaymentSettlementVerification(
            ProviderReportsCompleted: true,
            ProviderPaymentReference: paymentEvent.ProviderPaymentReference,
            Amount: attemptSnapshot.Amount,
            Currency: attemptSnapshot.Currency,
            CompletedAt: DateTime.UtcNow,
            Reason: "The order is Complete, but the provider has not confirmed final funds availability.",
            ProviderConfirmsFundsSettledOrAvailable: false));
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-final-funds-pending\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_SETTLEMENT_UNVERIFIED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Received, (await db.PaymentEvents.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(originalPeriodEnd, (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).CurrentPeriodEnd);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Successful_webhook_fails_closed_without_registered_settlement_verifier()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:no-verifier", 9, "USD");
        var providerEvent = new ProviderPaymentEvent("evt-no-verifier", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-no-verifier", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-no-verifier\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_SETTLEMENT_VERIFIER_UNCONFIGURED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Received, (await db.PaymentEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Same_provider_event_reference_with_different_signed_payload_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:event-payload-replay", 9, "USD");
        var providerEvent = new ProviderPaymentEvent("evt-payload-replay", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, attempt.Id, "pay-payload-replay", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var provider = new FakePaymentProvider(providerEvent);
        var verifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(db, [provider], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var first = await service.ProcessAsync("fake", "{\"event\":\"evt-payload-replay\"}", "valid");
        var conflicting = await service.ProcessAsync("fake", "{\"event\":\"evt-payload-replay\",\"changed\":true}", "valid");

        Assert.True(first.Accepted);
        Assert.False(conflicting.Accepted);
        Assert.Equal("WEBHOOK_EVENT_REFERENCE_CONFLICT", conflicting.Code);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Single(await db.PaymentEvents.ToListAsync());
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Replayed_verified_renewal_event_creates_only_one_period_credit_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"replay-{Guid.NewGuid():N}", Name = "Replay Test Plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-renewal-replay";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:renewal-replay", 9, "USD", subscriptionId: subscription.Id);
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var providerEvent = new ProviderPaymentEvent("evt-renewal-replay", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id, "pay-renewal-replay", 9, "USD", DateTime.UtcNow, "Renewal succeeded.", "sub-renewal-replay", periodStart, periodEnd, false);
        var verifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var first = await service.ProcessAsync("fake", "{\"event\":\"evt-renewal-replay\"}", "valid");
        var duplicate = await service.ProcessAsync("fake", "{\"event\":\"evt-renewal-replay\"}", "valid");

        Assert.True(first.Accepted);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, verifier.CallCount);
        Assert.Single(await db.BillingPeriods.ToListAsync());
        Assert.Single(await db.CreditEntitlements.ToListAsync());
        var grant = await db.CreditLedgerEntries.SingleAsync();
        Assert.Equal(CreditLedgerEntryType.Grant, grant.Type);
        Assert.Equal(100, grant.Amount);

        var updatedSubscription = await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
        var secondUseEvent = providerEvent with
        {
            ProviderEventReference = "evt-renewal-replay-second-period",
            PeriodStart = updatedSubscription.CurrentPeriodEnd,
            PeriodEnd = updatedSubscription.CurrentPeriodEnd.AddMonths(1),
        };
        var secondUseService = new PaymentWebhookService(db, [new FakePaymentProvider(secondUseEvent)], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance, [verifier]);
        var secondUse = await secondUseService.ProcessAsync("fake", "{\"event\":\"evt-renewal-replay-second-period\"}", "valid");

        Assert.False(secondUse.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", secondUse.Code);
        Assert.Equal(PaymentAttemptStatus.Succeeded, (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.AsNoTracking().SingleAsync(item => item.ProviderEventReference == "evt-renewal-replay-second-period")).Status);
        Assert.Equal(2, await db.PaymentEvents.CountAsync());
        Assert.Single(await db.BillingPeriods.ToListAsync());
        Assert.Single(await db.CreditEntitlements.ToListAsync());
        Assert.Single(await db.CreditLedgerEntries.ToListAsync());
        Assert.Equal(2, verifier.CallCount);
    }

    [Fact]
    public async Task Renewal_webhook_with_mismatched_provider_subscription_reference_is_rejected_without_grant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"mismatch-{Guid.NewGuid():N}", Name = "Renewal identity test plan",
            MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 98,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-expected";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:renewal-mismatch", 9, "USD", subscriptionId: subscription.Id);
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var providerEvent = new ProviderPaymentEvent(
            "evt-renewal-mismatch", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id,
            "pay-renewal-mismatch", 9, "USD", DateTime.UtcNow, "Renewal succeeded.", "sub-unrelated", periodStart, periodEnd, false);
        var verifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-renewal-mismatch\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.SingleAsync()).Status);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
        Assert.Empty(await db.CreditEntitlements.ToListAsync());
    }

    [Fact]
    public async Task Provider_payment_reference_already_bound_across_provider_casing_is_rejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var workspace = NewWorkspace();
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var referenceOwner = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "FAKE", "attempt:reference-owner", 9, "USD", providerPaymentReference: "pay-already-bound");
        var candidate = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:reference-candidate", 9, "USD");
        var providerEvent = new ProviderPaymentEvent(
            "evt-reference-already-bound", PaymentEventType.PaymentSucceeded, workspace.Id, null, null, candidate.Id,
            "pay-already-bound", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var verifier = new FakePaymentSettlementVerifier();
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle,
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance, [verifier]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-reference-already-bound\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.SingleAsync(item => item.Id == referenceOwner.Id)).Status);
        var rejectedAttempt = await db.PaymentAttempts.SingleAsync(item => item.Id == candidate.Id);
        Assert.Equal(PaymentAttemptStatus.Created, rejectedAttempt.Status);
        Assert.Null(rejectedAttempt.ProviderPaymentReference);
        Assert.Equal(PaymentEventStatus.Rejected, (await db.PaymentEvents.SingleAsync()).Status);
        Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
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
    public async Task Signed_event_with_unknown_local_references_is_durably_pending_without_foreign_key_failure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var providerEvent = new ProviderPaymentEvent(
            "evt-unknown-references", PaymentEventType.PaymentSucceeded, Guid.NewGuid(), null, null, Guid.NewGuid(),
            "pay-unknown", 9, "USD", DateTime.UtcNow, "Payment succeeded.", null, null, null, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], NewLifecycle(db),
            Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
            NullLogger<PaymentWebhookService>.Instance);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-unknown-references\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("PAYMENT_REFERENCE_PENDING", result.Code);
        var saved = await db.PaymentEvents.SingleAsync();
        Assert.Equal(PaymentEventStatus.Received, saved.Status);
        Assert.Null(saved.WorkspaceId);
        Assert.Null(saved.PaymentAttemptId);
        Assert.True(saved.SignatureVerified);
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
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-atomicity";
        db.Workspaces.Add(workspace);
        db.Plans.Add(plan);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var lifecycle = NewLifecycle(db);
        var attempt = await lifecycle.RecordPaymentAttemptAsync(workspace.Id, "fake", "attempt:renewal-atomicity", 9, "USD", subscriptionId: subscription.Id);
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var providerEvent = new ProviderPaymentEvent(
            "evt-renewal-atomicity", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id, null, attempt.Id, "pay-atomicity", 9, "USD",
            DateTime.UtcNow, "Renewal succeeded.", "sub-atomicity", periodStart, periodEnd, false);
        var service = new PaymentWebhookService(db, [new FakePaymentProvider(providerEvent)], lifecycle, Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }), NullLogger<PaymentWebhookService>.Instance, [new FakePaymentSettlementVerifier()]);

        var result = await service.ProcessAsync("fake", "{\"event\":\"evt-renewal-atomicity\"}", "valid");

        Assert.False(result.Accepted);
        Assert.Equal("WEBHOOK_PROCESSING_FAILED", result.Code);
        Assert.Equal(PaymentAttemptStatus.Created, (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id)).Status);
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).Status);
        Assert.Empty(await db.BillingPeriods.ToListAsync());
        Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
        Assert.Equal(PaymentEventStatus.Received, (await db.PaymentEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task Verified_renewal_retries_same_event_after_credit_repair_and_grants_once()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"payment-renewal-retry-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connectionString).Options;
        var workspace = NewWorkspace();
        var plan = new Plan
        {
            Id = Guid.NewGuid(), Code = $"repairable-renewal-{Guid.NewGuid():N}", Name = "Repairable renewal fixture",
            MonthlyCreditAllowance = 0, Currency = "USD", IsActive = true, SortOrder = 99,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var subscription = NewSubscription(workspace.Id, plan.Id);
        subscription.BillingProvider = "fake";
        subscription.ProviderSubscriptionReference = "sub-renewal-retry";
        var periodStart = subscription.CurrentPeriodEnd;
        var periodEnd = periodStart.AddMonths(1);
        var occurredAt = DateTime.UtcNow;
        const string rawPayload = "{\"event\":\"evt-renewal-retry-repair\"}";
        const string signature = "valid";
        var paymentAttemptId = Guid.Empty;
        PaymentEventStatus? eventStatusAfterFailure = null;

        try
        {
            await using (var db = new TaslimDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Workspaces.Add(workspace);
                db.Plans.Add(plan);
                db.Subscriptions.Add(subscription);
                await db.SaveChangesAsync();
                var lifecycle = NewLifecycle(db);
                var attempt = await lifecycle.RecordPaymentAttemptAsync(
                    workspace.Id, "fake", "attempt:renewal-retry-repair", 9, "USD", subscriptionId: subscription.Id);
                paymentAttemptId = attempt.Id;
                var providerEvent = new ProviderPaymentEvent(
                    "evt-renewal-retry-repair", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id,
                    null, attempt.Id, "pay-renewal-retry-repair", 9, "USD", occurredAt,
                    "Synthetic renewal succeeded.", "sub-renewal-retry", periodStart, periodEnd, false);
                var firstVerifier = new FakePaymentSettlementVerifier();
                // Local-only fake provider/verifier configuration; this test performs no checkout or network request.
                var service = new PaymentWebhookService(
                    db, [new FakePaymentProvider(providerEvent)], lifecycle,
                    Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
                    NullLogger<PaymentWebhookService>.Instance, [firstVerifier]);

                var first = await service.ProcessAsync("fake", rawPayload, signature);
                Assert.False(first.Accepted);
                Assert.False(first.Duplicate);
                Assert.Equal("WEBHOOK_PROCESSING_FAILED", first.Code);
                Assert.Equal(1, firstVerifier.CallCount);
                var unchangedAttempt = await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == attempt.Id);
                Assert.Equal(PaymentAttemptStatus.Created, unchangedAttempt.Status);
                Assert.Null(unchangedAttempt.ProviderPaymentReference);
                var unchangedSubscription = await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id);
                Assert.Equal(SubscriptionStatus.Active, unchangedSubscription.Status);
                Assert.Equal(periodStart, unchangedSubscription.CurrentPeriodEnd);
                Assert.Empty(await db.BillingPeriods.ToListAsync());
                Assert.Empty(await db.CreditLedgerEntries.ToListAsync());
                Assert.Empty(await db.CreditEntitlements.ToListAsync());
                Assert.Empty(await db.SubscriptionLifecycleEvents.ToListAsync());
                eventStatusAfterFailure = (await db.PaymentEvents.AsNoTracking().SingleAsync()).Status;
            }

            await using (var db = new TaslimDbContext(options))
            {
                var repairedPlan = await db.Plans.SingleAsync(item => item.Id == plan.Id);
                repairedPlan.MonthlyCreditAllowance = 100;
                await db.SaveChangesAsync();

                var providerEvent = new ProviderPaymentEvent(
                    "evt-renewal-retry-repair", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id,
                    null, paymentAttemptId, "pay-renewal-retry-repair", 9, "USD", occurredAt,
                    "Synthetic renewal succeeded.", "sub-renewal-retry", periodStart, periodEnd, false);
                var retryVerifier = new FakePaymentSettlementVerifier();
                var retryService = new PaymentWebhookService(
                    db, [new FakePaymentProvider(providerEvent)], NewLifecycle(db),
                    Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
                    NullLogger<PaymentWebhookService>.Instance, [retryVerifier]);

                var retry = await retryService.ProcessAsync("fake", rawPayload, signature);
                Assert.True(retry.Accepted);
                Assert.False(retry.Duplicate);
                Assert.Equal("WEBHOOK_PROCESSED", retry.Code);
                Assert.Equal(1, retryVerifier.CallCount);
                Assert.Equal(PaymentEventStatus.Received, eventStatusAfterFailure);
                var processedEvent = await db.PaymentEvents.AsNoTracking().SingleAsync();
                Assert.Equal(PaymentEventStatus.Processed, processedEvent.Status);
                Assert.Null(processedEvent.FailureReason);
                Assert.Equal(PaymentAttemptStatus.Succeeded,
                    (await db.PaymentAttempts.AsNoTracking().SingleAsync(item => item.Id == paymentAttemptId)).Status);
                Assert.Equal(periodEnd,
                    (await db.Subscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).CurrentPeriodEnd);
                Assert.Single(await db.BillingPeriods.ToListAsync());
                Assert.Single(await db.CreditEntitlements.ToListAsync());
                var grant = Assert.Single(await db.CreditLedgerEntries.ToListAsync());
                Assert.Equal(100L, grant.Amount);
                Assert.Equal(CreditLedgerEntryType.Grant, grant.Type);
                Assert.Single(await db.SubscriptionLifecycleEvents.ToListAsync());
            }

            await using (var db = new TaslimDbContext(options))
            {
                var providerEvent = new ProviderPaymentEvent(
                    "evt-renewal-retry-repair", PaymentEventType.RenewalSucceeded, workspace.Id, subscription.Id,
                    null, paymentAttemptId, "pay-renewal-retry-repair", 9, "USD", occurredAt,
                    "Synthetic renewal succeeded.", "sub-renewal-retry", periodStart, periodEnd, false);
                var replayVerifier = new FakePaymentSettlementVerifier();
                var replayService = new PaymentWebhookService(
                    db, [new FakePaymentProvider(providerEvent)], NewLifecycle(db),
                    Options.Create(new BillingOptions { CustomerChargingEnabled = true, Provider = "fake" }),
                    NullLogger<PaymentWebhookService>.Instance, [replayVerifier]);

                var duplicate = await replayService.ProcessAsync("fake", rawPayload, signature);
                Assert.True(duplicate.Accepted);
                Assert.True(duplicate.Duplicate);
                Assert.Equal("WEBHOOK_DUPLICATE", duplicate.Code);
                Assert.Equal(0, replayVerifier.CallCount);
                Assert.Single(await db.BillingPeriods.ToListAsync());
                Assert.Single(await db.CreditEntitlements.ToListAsync());
                var grant = Assert.Single(await db.CreditLedgerEntries.ToListAsync());
                Assert.Equal(100L, grant.Amount);
                Assert.Single(await db.SubscriptionLifecycleEvents.ToListAsync());
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
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

    private static Plan NewTestPlan(string prefix) => new()
    {
        Id = Guid.NewGuid(), Code = $"{prefix}-{Guid.NewGuid():N}", Name = "Synthetic payment test plan",
        MonthlyCreditAllowance = 100, Currency = "USD", IsActive = true, SortOrder = 99,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static async Task ObserveTaskCompletionAsync(Task? task)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // The test's primary assertion owns the failure; drain/cancel tasks here to avoid leakage.
        }
    }

    private sealed record CrossWorkspaceRenewalScenario(
        string EventReference,
        PaymentAttempt Attempt,
        Subscription Subscription);

    private sealed class DisposablePaymentTestDatabase : IAsyncDisposable
    {
        private readonly string? sqlitePath;
        private readonly string? postgresBaseConnectionString;
        private readonly string? postgresSchema;
        private readonly string? postgresApplicationName;

        private DisposablePaymentTestDatabase(
            DbContextOptions<TaslimDbContext> options,
            string? sqlitePath,
            string? postgresBaseConnectionString,
            string? postgresSchema,
            bool usesPostgres,
            string? postgresApplicationName)
        {
            Options = options;
            this.sqlitePath = sqlitePath;
            this.postgresBaseConnectionString = postgresBaseConnectionString;
            this.postgresSchema = postgresSchema;
            this.postgresApplicationName = postgresApplicationName;
            UsesPostgres = usesPostgres;
        }

        public DbContextOptions<TaslimDbContext> Options { get; }
        public bool UsesPostgres { get; }

        public static async Task<DisposablePaymentTestDatabase> CreateAsync()
        {
            var postgresTestConnection = Environment.GetEnvironmentVariable("PAYMENT_FANOUT_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(postgresTestConnection))
            {
                var databasePath = Path.Combine(Path.GetTempPath(), $"payment-webhook-followup-{Guid.NewGuid():N}.db");
                var sqliteConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false,
                    DefaultTimeout = 30,
                }.ToString();
                var sqliteOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                    .UseSqlite(sqliteConnectionString)
                    .Options;
                try
                {
                    await using var db = new TaslimDbContext(sqliteOptions);
                    await db.Database.EnsureCreatedAsync();
                    return new DisposablePaymentTestDatabase(sqliteOptions, databasePath, null, null, false, null);
                }
                catch
                {
                    DeleteSqliteFiles(databasePath);
                    throw;
                }
            }

            var baseBuilder = new NpgsqlConnectionStringBuilder(postgresTestConnection);
            if (!string.Equals(baseBuilder.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || baseBuilder.Port != 55433
                || !string.Equals(baseBuilder.Database, "taslim_tests", StringComparison.Ordinal)
                || !string.Equals(baseBuilder.Username, "taslim", StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(baseBuilder.Password))
                throw new InvalidOperationException(
                    "PAYMENT_FANOUT_TEST_POSTGRES must target the passwordless disposable 127.0.0.1:55433/taslim_tests test cluster; the value is not logged.");

            baseBuilder.Pooling = false;
            baseBuilder.Timeout = 5;
            baseBuilder.CommandTimeout = 5;
            var adminConnectionString = baseBuilder.ConnectionString;
            var schema = $"payment_followup_{Guid.NewGuid():N}";
            var applicationName = $"p255_{Guid.NewGuid():N}";
            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var createSchema = adminConnection.CreateCommand();
                createSchema.CommandText = $"CREATE SCHEMA \"{schema}\"";
                await createSchema.ExecuteNonQueryAsync();
            }

            var scopedBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                SearchPath = schema,
                ApplicationName = applicationName,
                Pooling = false,
                Timeout = 15,
                CommandTimeout = 30,
            };
            var postgresOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(scopedBuilder.ConnectionString)
                .Options;
            try
            {
                await using var db = new TaslimDbContext(postgresOptions);
                await db.Database.EnsureCreatedAsync();
                return new DisposablePaymentTestDatabase(postgresOptions, null, adminConnectionString, schema, true, applicationName);
            }
            catch
            {
                await DropPostgresSchemaAsync(adminConnectionString, schema);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (postgresBaseConnectionString is not null && postgresSchema is not null)
                await DropPostgresSchemaAsync(postgresBaseConnectionString, postgresSchema);
            if (sqlitePath is not null)
                DeleteSqliteFiles(sqlitePath);
        }

        public async Task WaitForPostgresLockWaitAsync(TimeSpan timeout)
        {
            if (!UsesPostgres || postgresBaseConnectionString is null || postgresApplicationName is null)
                throw new InvalidOperationException("The PostgreSQL lock-wait observer requires the dedicated disposable test fixture.");

            var deadline = DateTime.UtcNow + timeout;
            await using var connection = new NpgsqlConnection(postgresBaseConnectionString);
            await connection.OpenAsync();
            while (DateTime.UtcNow < deadline)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND application_name = @applicationName AND state = 'active' AND wait_event_type = 'Lock' AND query ILIKE '%PaymentEvents%')";
                command.Parameters.AddWithValue("applicationName", postgresApplicationName);
                if (Equals(await command.ExecuteScalarAsync(), true)) return;
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }

            throw new TimeoutException("The same-event request did not enter a PostgreSQL PaymentEvents lock wait within the bounded interval.");
        }

        private static async Task DropPostgresSchemaAsync(string connectionString, string schema)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }

        private static void DeleteSqliteFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = databasePath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private sealed class FakePaymentProvider(
        ProviderPaymentEvent paymentEvent,
        TaskCompletionSource<bool>? parseSignal = null) : IPaymentProvider
    {
        public string Key => "fake";
        public AcceptingSignatureVerifier SignatureVerifier { get; } = new();
        public IWebhookSignatureVerifier WebhookSignatureVerifier => SignatureVerifier;
        public int CheckoutCreateCallCount { get; private set; }
        public int ParseCallCount { get; private set; }

        public Task<ProviderCheckoutSessionResult> CreateCheckoutSessionAsync(ProviderCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            CheckoutCreateCallCount++;
            return Task.FromResult(new ProviderCheckoutSessionResult("session-1", "https://example.test/checkout/session-1", DateTime.UtcNow.AddMinutes(30)));
        }

        public ProviderPaymentEvent ParseWebhook(string rawPayload)
        {
            ParseCallCount++;
            parseSignal?.TrySetResult(true);
            return paymentEvent;
        }
    }

    private sealed class FakePaymentSettlementVerifier(
        Func<PaymentAttempt, CheckoutSession?, ProviderPaymentEvent, PaymentSettlementVerification>? responseFactory = null)
        : IPaymentSettlementVerifier
    {
        public string ProviderKey => "fake";
        public int CallCount { get; private set; }

        public Task<PaymentSettlementVerification> VerifyAsync(
            PaymentAttempt attempt,
            CheckoutSession? checkoutSession,
            ProviderPaymentEvent providerEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var result = responseFactory?.Invoke(attempt, checkoutSession, providerEvent)
            ?? new PaymentSettlementVerification(
                    ProviderReportsCompleted: true,
                    ProviderPaymentReference: providerEvent.ProviderPaymentReference,
                    Amount: attempt.Amount,
                    Currency: attempt.Currency,
                    CompletedAt: DateTime.UtcNow,
                    ProviderConfirmsFundsSettledOrAvailable: true);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingCountingPaymentSettlementVerifier : IPaymentSettlementVerifier
    {
        private readonly TaskCompletionSource<bool> firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public string ProviderKey => "fake";
        public int CallCount => Volatile.Read(ref callCount);
        public Task FirstCall => firstCall.Task;

        public void Release() => released.TrySetResult(true);

        public async Task<PaymentSettlementVerification> VerifyAsync(
            PaymentAttempt attempt,
            CheckoutSession? checkoutSession,
            ProviderPaymentEvent providerEvent,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref callCount);
            firstCall.TrySetResult(true);
            await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new PaymentSettlementVerification(
                ProviderReportsCompleted: true,
                ProviderPaymentReference: providerEvent.ProviderPaymentReference,
                Amount: attempt.Amount,
                Currency: attempt.Currency,
                CompletedAt: DateTime.UtcNow,
                ProviderConfirmsFundsSettledOrAvailable: true);
        }
    }

    private sealed class CancelFirstThenSuccessfulPaymentSettlementVerifier : IPaymentSettlementVerifier
    {
        private readonly TaskCompletionSource<bool> firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public string ProviderKey => "fake";
        public int CallCount => Volatile.Read(ref callCount);
        public Task FirstCallStarted => firstCallStarted.Task;

        public async Task<PaymentSettlementVerification> VerifyAsync(
            PaymentAttempt attempt,
            CheckoutSession? checkoutSession,
            ProviderPaymentEvent providerEvent,
            CancellationToken cancellationToken = default)
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                firstCallStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new PaymentSettlementVerification(
                ProviderReportsCompleted: true,
                ProviderPaymentReference: providerEvent.ProviderPaymentReference,
                Amount: attempt.Amount,
                Currency: attempt.Currency,
                CompletedAt: DateTime.UtcNow,
                ProviderConfirmsFundsSettledOrAvailable: true);
        }
    }

    private sealed class AcceptingSignatureVerifier : IWebhookSignatureVerifier
    {
        public int CallCount { get; private set; }

        public bool Verify(string rawPayload, string signature)
        {
            CallCount++;
            return signature == "valid";
        }
    }

    private sealed class NullNotificationEventWriter : INotificationEventWriter
    {
        public Task CreateGenerationCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationFailedAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateGenerationAttentionAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateBillingPaymentFailedAsync(Guid workspaceId, Guid paymentAttemptId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

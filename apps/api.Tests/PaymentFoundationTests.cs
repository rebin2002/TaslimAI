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
        var verifier = new TwoCallPaymentSettlementVerifier();
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
        Assert.Equal(2, verifier.CallCount);
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

    private sealed class TwoCallPaymentSettlementVerifier : IPaymentSettlementVerifier
    {
        private readonly TaskCompletionSource<bool> bothCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public string ProviderKey => "fake";
        public int CallCount => Volatile.Read(ref callCount);

        public async Task<PaymentSettlementVerification> VerifyAsync(
            PaymentAttempt attempt,
            CheckoutSession? checkoutSession,
            ProviderPaymentEvent providerEvent,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref callCount) == 2)
                bothCalls.TrySetResult(true);
            await bothCalls.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
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

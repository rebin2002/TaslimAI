using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Documents;
using Taslim.Api.Generation;
using Taslim.Api.Images;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Taslim.Api.Presentations;
using Taslim.Api.Resilience;
using Taslim.Api.Tests.ProviderTesting;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class PublicFreeTierProviderSpendPolicyTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public void Defaults_are_conservative_and_firewall_is_off_by_default()
    {
        var options = new PublicFreeTierPolicyOptions();

        Assert.False(options.Enabled);
        Assert.Equal(3, options.MaxChatMessagesPerUserPerUtcDay);
        Assert.Equal(10, options.MaxChatMessagesPerUserPerUtcWeek);
        Assert.Equal(2_000, options.MaxInputTokens);
        Assert.Equal(512, options.MaxOutputTokens);
        Assert.Equal(1.00m, options.MaxGlobalProviderSpendPerUtcDayUsd);
        Assert.Equal(5.00m, options.MaxGlobalProviderSpendPerUtcMonthUsd);
        Assert.Equal(0.10m, options.MaxProviderSpendPerUserPerUtcDayUsd);
        Assert.Equal(0.50m, options.MaxProviderSpendPerUserPerUtcMonthUsd);
        Assert.Equal(0.25m, options.MaxProviderSpendPerWorkspacePerUtcDayUsd);
        Assert.Equal(1.00m, options.MaxProviderSpendPerWorkspacePerUtcMonthUsd);
        Assert.Equal(new[] { PublicFreeTierCapability.TextChat }, options.AllowedCapabilities);
    }

    [Fact]
    public void Disabled_firewall_fails_closed_instead_of_allowing_a_paid_call()
    {
        var result = Evaluate(options: new PublicFreeTierPolicyOptions());

        AssertDenied(result, 503, "PUBLIC_FREE_TIER_FIREWALL_DISABLED");
    }

    [Fact]
    public void Authenticated_free_chat_within_all_limits_is_admitted()
    {
        var result = Evaluate();

        Assert.True(result.Allowed);
        Assert.Equal("PUBLIC_FREE_TIER_ADMITTED", result.Code);
    }

    [Fact]
    public void Zero_customer_credits_do_not_enter_or_change_provider_spend_admission()
    {
        const long customerCreditBalance = 0;
        var request = ValidRequest();

        Assert.Equal(0L, customerCreditBalance);
        Assert.Equal(PublicFreeTierPlan.Free, request.Plan);
        Assert.DoesNotContain(
            typeof(PublicFreeTierAdmissionRequest).GetProperties(),
            property => property.Name.Contains("Credit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(IPublicFreeTierSpendLedger).GetMethods(),
            method => method.Name.Contains("Credit", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Refund", StringComparison.OrdinalIgnoreCase));
        Assert.True(Evaluate(request: request).Allowed);
    }

    [Fact]
    public void Anonymous_visitor_is_rejected_before_any_provider_reservation()
    {
        var result = Evaluate(request: ValidRequest() with { IsAuthenticated = false });

        AssertDenied(result, 401, "PUBLIC_FREE_TIER_AUTHENTICATION_REQUIRED");
    }

    [Theory]
    [InlineData(false, true, true, true, "PUBLIC_FREE_TIER_ACCOUNT_NOT_ELIGIBLE")]
    [InlineData(true, false, true, true, "PUBLIC_FREE_TIER_EMAIL_CONFIRMATION_REQUIRED")]
    [InlineData(true, true, false, true, "PUBLIC_FREE_TIER_WORKSPACE_ACCESS_DENIED")]
    [InlineData(true, true, true, false, "PUBLIC_FREE_TIER_WORKSPACE_ACCESS_DENIED")]
    public void Inactive_unconfirmed_or_unowned_context_is_rejected(
        bool accountActive,
        bool emailConfirmed,
        bool workspaceActive,
        bool isWorkspaceMember,
        string expectedCode)
    {
        var request = ValidRequest() with
        {
            AccountIsActive = accountActive,
            EmailIsConfirmed = emailConfirmed,
            WorkspaceIsActive = workspaceActive,
            IsWorkspaceMember = isWorkspaceMember,
        };

        AssertDenied(Evaluate(request: request), 403, expectedCode);
    }

    [Theory]
    [InlineData(PublicFreeTierPlan.Unknown)]
    [InlineData(PublicFreeTierPlan.Paid)]
    public void Only_server_classified_free_plan_is_eligible(PublicFreeTierPlan plan)
    {
        AssertDenied(Evaluate(request: ValidRequest() with { Plan = plan }), 403, "PUBLIC_FREE_TIER_PLAN_REQUIRED");
    }

    [Theory]
    [InlineData(PublicFreeTierCapability.ImageGeneration)]
    [InlineData(PublicFreeTierCapability.VideoGeneration)]
    [InlineData(PublicFreeTierCapability.VoiceGeneration)]
    [InlineData(PublicFreeTierCapability.MusicGeneration)]
    [InlineData(PublicFreeTierCapability.ShortFormClipGeneration)]
    [InlineData(PublicFreeTierCapability.BackgroundProviderJob)]
    [InlineData(PublicFreeTierCapability.OtherProviderRoute)]
    [InlineData(PublicFreeTierCapability.Unspecified)]
    public void Expensive_and_unclassified_routes_are_denied_by_default(PublicFreeTierCapability capability)
    {
        var result = Evaluate(request: ValidRequest() with { Capability = capability });

        AssertDenied(result, 403, "PUBLIC_FREE_TIER_CAPABILITY_NOT_ENABLED");
    }

    [Fact]
    public void Media_and_job_routes_remain_denied_even_if_added_to_the_configurable_allowlist()
    {
        var options = EnabledOptions();
        options.AllowedCapabilities.UnionWith(new[]
        {
            PublicFreeTierCapability.ImageGeneration,
            PublicFreeTierCapability.VideoGeneration,
            PublicFreeTierCapability.VoiceGeneration,
            PublicFreeTierCapability.MusicGeneration,
            PublicFreeTierCapability.ShortFormClipGeneration,
            PublicFreeTierCapability.BackgroundProviderJob,
            PublicFreeTierCapability.OtherProviderRoute,
        });

        foreach (var capability in options.AllowedCapabilities.Where(capability => capability != PublicFreeTierCapability.TextChat))
            AssertDenied(Evaluate(request: ValidRequest() with { Capability = capability }, options: options), 403, "PUBLIC_FREE_TIER_CAPABILITY_NOT_ENABLED");
    }

    [Fact]
    public void Low_cost_tool_requires_an_explicit_operator_allowlist_entry()
    {
        var request = ValidRequest() with { Capability = PublicFreeTierCapability.LowCostResearchLookup };
        AssertDenied(Evaluate(request: request), 403, "PUBLIC_FREE_TIER_CAPABILITY_NOT_ENABLED");

        var options = EnabledOptions();
        options.AllowedCapabilities.Add(PublicFreeTierCapability.LowCostResearchLookup);
        Assert.True(Evaluate(request: request, options: options).Allowed);
    }

    [Fact]
    public void Daily_and_weekly_message_quotas_return_429_with_utc_retry_time()
    {
        var daily = Evaluate(snapshot: ValidSnapshot() with { UserMessagesForUtcDay = 3 });
        AssertDenied(daily, 429, "PUBLIC_FREE_TIER_DAILY_MESSAGE_LIMIT");
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero), daily.RetryAfterUtc);

        var weekly = Evaluate(snapshot: ValidSnapshot() with { UserMessagesForUtcWeek = 10 });
        AssertDenied(weekly, 429, "PUBLIC_FREE_TIER_WEEKLY_MESSAGE_LIMIT");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), weekly.RetryAfterUtc);
    }

    [Fact]
    public void Token_caps_are_inclusive_and_oversized_requests_are_rejected()
    {
        Assert.True(Evaluate(request: ValidRequest() with { InputTokens = 2_000, MaxOutputTokens = 512 }).Allowed);
        AssertDenied(Evaluate(request: ValidRequest() with { InputTokens = 2_001 }), 413, "PUBLIC_FREE_TIER_TOKEN_LIMIT_EXCEEDED");
        AssertDenied(Evaluate(request: ValidRequest() with { MaxOutputTokens = 513 }), 413, "PUBLIC_FREE_TIER_TOKEN_LIMIT_EXCEEDED");
        AssertDenied(Evaluate(request: ValidRequest() with { InputTokens = -1 }), 400, "PUBLIC_FREE_TIER_INVALID_TOKEN_BUDGET");
    }

    [Theory]
    [InlineData(null, "USD", "price-v1")]
    [InlineData("0", "USD", "price-v1")]
    [InlineData("0.001", "EUR", "price-v1")]
    [InlineData("0.001", "USD", "")]
    public void Unknown_zero_or_unversioned_price_fails_closed(
        string? amountUsdText,
        string currency,
        string pricingVersion)
    {
        decimal? amountUsd = amountUsdText is null
            ? null
            : decimal.Parse(amountUsdText, System.Globalization.CultureInfo.InvariantCulture);
        var request = ValidRequest() with
        {
            EstimatedProviderCostUsd = amountUsd,
            Currency = currency,
            PricingVersion = pricingVersion,
        };

        AssertDenied(Evaluate(request: request), 503, "PUBLIC_FREE_TIER_PRICE_UNAVAILABLE");
    }

    [Fact]
    public void User_and_workspace_scope_must_match_the_server_snapshot()
    {
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { WorkspaceId = Guid.NewGuid() }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_SCOPE_MISMATCH");
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { UserId = Guid.NewGuid() }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_SCOPE_MISMATCH");
    }

    [Fact]
    public void Stale_future_or_wrong_window_snapshot_fails_closed()
    {
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { CapturedAtUtc = NowUtc.AddSeconds(-16) }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_SNAPSHOT_STALE");
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { CapturedAtUtc = NowUtc.AddSeconds(1) }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_SNAPSHOT_STALE");
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { UtcDay = DateOnly.FromDateTime(NowUtc.UtcDateTime).AddDays(-1) }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_WINDOW_MISMATCH");
    }

    [Fact]
    public void Unknown_global_provider_exposure_pauses_all_free_ai_until_reconciled()
    {
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { UnknownProviderCostEntries = 1 }),
            503,
            "PUBLIC_FREE_TIER_UNKNOWN_PROVIDER_EXPOSURE");
    }

    [Fact]
    public void Negative_accounting_values_fail_closed()
    {
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { UserMessagesForUtcDay = -1 }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_DATA_INVALID");
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { GlobalProviderExposureForUtcDayUsd = -0.01m }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_DATA_INVALID");
        AssertDenied(
            Evaluate(snapshot: ValidSnapshot() with { UnknownProviderCostEntries = -1 }),
            503,
            "PUBLIC_FREE_TIER_ACCOUNTING_DATA_INVALID");
    }

    [Fact]
    public void Per_user_workspace_and_global_provider_spend_caps_apply_independently()
    {
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { UserProviderExposureForUtcDayUsd = 0.10m }), 429, "PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { UserProviderExposureForUtcMonthUsd = 0.50m }), 429, "PUBLIC_FREE_TIER_USER_MONTHLY_SPEND_LIMIT");
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { WorkspaceProviderExposureForUtcDayUsd = 0.25m }), 429, "PUBLIC_FREE_TIER_WORKSPACE_DAILY_SPEND_LIMIT");
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { WorkspaceProviderExposureForUtcMonthUsd = 1.00m }), 429, "PUBLIC_FREE_TIER_WORKSPACE_MONTHLY_SPEND_LIMIT");
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { GlobalProviderExposureForUtcDayUsd = 1.00m }), 429, "PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        AssertDenied(Evaluate(snapshot: ValidSnapshot() with { GlobalProviderExposureForUtcMonthUsd = 5.00m }), 429, "PUBLIC_FREE_TIER_GLOBAL_MONTHLY_SPEND_LIMIT");
    }

    [Fact]
    public void Exact_spend_ceiling_is_allowed_but_costs_cannot_overflow_past_it()
    {
        Assert.True(Evaluate(
            request: ValidRequest() with { EstimatedProviderCostUsd = 0.001m },
            snapshot: ValidSnapshot() with { GlobalProviderExposureForUtcDayUsd = 0.999m }).Allowed);
        AssertDenied(
            Evaluate(
                request: ValidRequest() with { EstimatedProviderCostUsd = 0.001m },
                snapshot: ValidSnapshot() with { GlobalProviderExposureForUtcDayUsd = decimal.MaxValue }),
            429,
            "PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
    }

    [Fact]
    public void Invalid_policy_configuration_fails_closed()
    {
        var options = EnabledOptions();
        options.MaxGlobalProviderSpendPerUtcDayUsd = 0m;

        AssertDenied(Evaluate(options: options), 503, "PUBLIC_FREE_TIER_POLICY_MISCONFIGURED");
    }

    [Fact]
    public async Task Atomic_reservations_prevent_parallel_global_oversubscription()
    {
        var options = EnabledOptions();
        options.MaxGlobalProviderSpendPerUtcDayUsd = 0.01m;
        options.MaxGlobalProviderSpendPerUtcMonthUsd = 0.05m;
        var ledger = new AtomicTestLedger(options, NowUtc);
        var requests = Enumerable.Range(0, 12).Select(index => ValidRequest(
            userId: Guid.NewGuid(),
            workspaceId: Guid.NewGuid(),
            idempotencyKey: $"parallel-attempt-{index}") with { EstimatedProviderCostUsd = 0.01m });

        var results = await Task.WhenAll(requests.Select(request => ledger.TryReserveBeforeProviderCallAsync(request)));

        Assert.Single(results, result => result.Reserved);
        Assert.Equal(1, ledger.ReservationCount);
        Assert.Equal(0.01m, ledger.GlobalExposureUsd);
    }

    [Fact]
    public async Task Idempotent_replay_does_not_double_reserve_and_key_conflict_is_rejected()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var request = ValidRequest(idempotencyKey: "server-generated-attempt-1");

        var first = await ledger.TryReserveBeforeProviderCallAsync(request);
        var replay = await ledger.TryReserveBeforeProviderCallAsync(request);
        var conflictingReplay = await ledger.TryReserveBeforeProviderCallAsync(request with { EstimatedProviderCostUsd = 0.02m });

        Assert.True(first.Reserved);
        Assert.True(replay.Reserved);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(PublicFreeTierReservationState.Reserved, replay.State);
        Assert.False(replay.Decision.Allowed);
        Assert.Equal("PUBLIC_FREE_TIER_RESERVATION_REPLAY", replay.Decision.Code);
        Assert.Equal(first.ReservationId, replay.ReservationId);
        Assert.Equal(1, ledger.ReservationCount);

        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(first.ReservationId!.Value, "provider-attempt-1");
        var dispatchReplay = await ledger.TryAuthorizeProviderDispatchAsync(first.ReservationId.Value, "provider-attempt-1");
        Assert.True(dispatch.DispatchAuthorized);
        Assert.False(dispatch.IsIdempotentReplay);
        Assert.False(dispatchReplay.DispatchAuthorized);
        Assert.True(dispatchReplay.IsIdempotentReplay);
        Assert.Equal(PublicFreeTierReservationState.Dispatched, dispatchReplay.State);

        Assert.False(conflictingReplay.Reserved);
        Assert.Equal(409, conflictingReplay.Decision.StatusCode);
        Assert.Equal("PUBLIC_FREE_TIER_IDEMPOTENCY_KEY_CONFLICT", conflictingReplay.Decision.Code);
    }

    [Fact]
    public async Task Dispatched_reservation_cannot_be_reauthorized_with_a_different_dispatch_key()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(
            ValidRequest(idempotencyKey: "dispatch-key-rebinding") with { EstimatedProviderCostUsd = 0.008m });

        Assert.True(reservation.Reserved);
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "physical-attempt-1");
        var rekeyedDispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId.Value, "physical-attempt-2");

        Assert.True(dispatch.DispatchAuthorized);
        Assert.False(dispatch.IsIdempotentReplay);
        Assert.False(rekeyedDispatch.DispatchAuthorized);
        Assert.False(rekeyedDispatch.IsIdempotentReplay);
        Assert.Equal(PublicFreeTierReservationState.Dispatched, rekeyedDispatch.State);
        Assert.Equal("PROVIDER_DISPATCH_ALREADY_AUTHORIZED", rekeyedDispatch.Code);
        Assert.Equal(1, ledger.ReservationCount);
        Assert.Equal(0.008m, ledger.GlobalExposureUsd);
    }

    [Fact]
    public async Task Concurrent_duplicate_reservations_and_dispatch_authorization_are_one_shot()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var request = ValidRequest(idempotencyKey: "concurrent-same-attempt");

        var admissions = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => ledger.TryReserveBeforeProviderCallAsync(request))));

        var original = Assert.Single(admissions, result => !result.IsIdempotentReplay);
        Assert.Equal(31, admissions.Count(result => result.IsIdempotentReplay));
        Assert.Single(admissions, result => result.Decision.Allowed);
        Assert.All(admissions, result => Assert.Equal(original.ReservationId, result.ReservationId));
        Assert.Equal(1, ledger.ReservationCount);

        var dispatches = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => ledger.TryAuthorizeProviderDispatchAsync(original.ReservationId!.Value, "concurrent-dispatch"))));

        Assert.Single(dispatches, result => result.DispatchAuthorized);
        Assert.Equal(31, dispatches.Count(result => result.IsIdempotentReplay));
        Assert.All(dispatches.Where(result => !result.DispatchAuthorized), result =>
        {
            Assert.Equal(PublicFreeTierReservationState.Dispatched, result.State);
            Assert.False(result.DispatchAuthorized);
        });
    }

    [Fact]
    public async Task Retry_uses_a_new_attempt_key_and_still_consumes_the_user_quota()
    {
        var options = EnabledOptions();
        options.MaxChatMessagesPerUserPerUtcDay = 1;
        options.MaxChatMessagesPerUserPerUtcWeek = 1;
        var ledger = new AtomicTestLedger(options, NowUtc);
        var firstRequest = ValidRequest(idempotencyKey: "generation-1-attempt-1");

        Assert.True((await ledger.TryReserveBeforeProviderCallAsync(firstRequest)).Reserved);
        Assert.True((await ledger.TryReserveBeforeProviderCallAsync(firstRequest)).IsIdempotentReplay);
        var retry = await ledger.TryReserveBeforeProviderCallAsync(firstRequest with { IdempotencyKey = "generation-1-attempt-2" });

        Assert.False(retry.Reserved);
        Assert.Equal("PUBLIC_FREE_TIER_DAILY_MESSAGE_LIMIT", retry.Decision.Code);
        Assert.Equal(1, ledger.ReservationCount);
    }

    [Fact]
    public async Task User_message_limit_cannot_be_bypassed_by_switching_workspaces()
    {
        var options = EnabledOptions();
        options.MaxChatMessagesPerUserPerUtcDay = 1;
        options.MaxChatMessagesPerUserPerUtcWeek = 1;
        var ledger = new AtomicTestLedger(options, NowUtc);

        Assert.True((await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(
            userId: UserId,
            workspaceId: WorkspaceId,
            idempotencyKey: "workspace-one-attempt"))).Reserved);
        var secondWorkspace = await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(
            userId: UserId,
            workspaceId: Guid.NewGuid(),
            idempotencyKey: "workspace-two-attempt"));

        Assert.False(secondWorkspace.Reserved);
        Assert.Equal("PUBLIC_FREE_TIER_DAILY_MESSAGE_LIMIT", secondWorkspace.Decision.Code);
    }

    [Fact]
    public async Task Workspace_spend_caps_are_isolated_for_the_same_user()
    {
        var options = EnabledOptions();
        options.MaxChatMessagesPerUserPerUtcDay = 10;
        options.MaxChatMessagesPerUserPerUtcWeek = 10;
        options.MaxProviderSpendPerWorkspacePerUtcDayUsd = 0.001m;
        options.MaxProviderSpendPerWorkspacePerUtcMonthUsd = 0.001m;
        var ledger = new AtomicTestLedger(options, NowUtc);

        var firstWorkspace = await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(
            userId: UserId,
            workspaceId: WorkspaceId,
            idempotencyKey: "same-user-workspace-one") with { EstimatedProviderCostUsd = 0.001m });
        var secondWorkspaceId = Guid.NewGuid();
        var secondWorkspace = await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(
            userId: UserId,
            workspaceId: secondWorkspaceId,
            idempotencyKey: "same-user-workspace-two") with { EstimatedProviderCostUsd = 0.001m });
        var overFirstWorkspaceCap = await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(
            userId: UserId,
            workspaceId: WorkspaceId,
            idempotencyKey: "same-user-workspace-one-over-cap") with { EstimatedProviderCostUsd = 0.001m });

        Assert.True(firstWorkspace.Reserved);
        Assert.True(secondWorkspace.Reserved);
        Assert.False(overFirstWorkspaceCap.Reserved);
        Assert.Equal("PUBLIC_FREE_TIER_WORKSPACE_DAILY_SPEND_LIMIT", overFirstWorkspaceCap.Decision.Code);
        Assert.Equal(2, ledger.ReservationCount);
        Assert.Equal(0.002m, ledger.GlobalExposureUsd);
    }

    [Fact]
    public async Task Failed_provider_call_keeps_unknown_estimate_until_reconciled()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var request = ValidRequest(idempotencyKey: "failed-attempt") with { EstimatedProviderCostUsd = 0.008m };
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(request);
        Assert.True(reservation.Reserved);
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "failed-attempt-dispatch");
        Assert.True(dispatch.DispatchAuthorized);

        var failed = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "provider-result-1",
            PublicFreeTierProviderOutcome.Failed,
            actualCostKnown: false,
            actualProviderCostUsd: null);

        Assert.True(failed.Applied);
        Assert.True(failed.ReconciliationRequired);
        Assert.Equal(0.008m, ledger.GlobalExposureUsd);
        Assert.Equal(1, ledger.UnknownExposureCount);
        var paused = await ledger.TryReserveBeforeProviderCallAsync(ValidRequest(idempotencyKey: "another-attempt"));
        Assert.False(paused.Reserved);
        Assert.Equal("PUBLIC_FREE_TIER_UNKNOWN_PROVIDER_EXPOSURE", paused.Decision.Code);

        var reconciled = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "provider-reconcile-1",
            PublicFreeTierProviderOutcome.Failed,
            actualCostKnown: true,
            actualProviderCostUsd: 0.002m);

        Assert.True(reconciled.Applied);
        Assert.False(reconciled.ReconciliationRequired);
        Assert.Equal(0.002m, ledger.GlobalExposureUsd);
        Assert.Equal(0, ledger.UnknownExposureCount);
    }

    [Fact]
    public async Task Provider_failure_with_known_cost_is_not_refunded()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(
            ValidRequest(idempotencyKey: "provider-failed-known-cost") with { EstimatedProviderCostUsd = 0.009m });
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "known-cost-dispatch");
        Assert.True(dispatch.DispatchAuthorized);

        var result = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "provider-result-known-cost",
            PublicFreeTierProviderOutcome.Failed,
            actualCostKnown: true,
            actualProviderCostUsd: 0.004m);

        Assert.True(result.Applied);
        Assert.False(result.ReconciliationRequired);
        Assert.Equal(0.004m, ledger.GlobalExposureUsd);
        Assert.Equal(0, ledger.UnknownExposureCount);
    }

    [Fact]
    public async Task Settlement_replay_or_late_completion_cannot_refund_provider_exposure()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(
            ValidRequest(idempotencyKey: "settlement-no-refund") with { EstimatedProviderCostUsd = 0.009m });
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "settlement-no-refund-dispatch");
        Assert.True(dispatch.DispatchAuthorized);

        var settled = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "authoritative-provider-actual",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0.006m);
        var replay = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "authoritative-provider-actual",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0.006m);
        var conflictingReplay = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "authoritative-provider-actual",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0m);
        var lateAdjustment = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "late-adjustment-must-not-refund-provider-cost",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0m);

        Assert.True(settled.Applied);
        Assert.True(replay.IsIdempotentReplay);
        Assert.False(conflictingReplay.Applied);
        Assert.Equal("COMPLETION_KEY_CONFLICT", conflictingReplay.Code);
        Assert.False(lateAdjustment.Applied);
        Assert.Equal("RESERVATION_ALREADY_FINALIZED", lateAdjustment.Code);
        Assert.Equal(0.006m, ledger.GlobalExposureUsd);
        Assert.Equal(0, ledger.UnknownExposureCount);
    }

    [Fact]
    public async Task Concurrent_conflicting_settlements_apply_one_actual_cost_without_exposure_erosion()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(
            ValidRequest(idempotencyKey: "settlement-race") with { EstimatedProviderCostUsd = 0.009m });
        Assert.True(reservation.Reserved);
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "settlement-race-dispatch");
        Assert.True(dispatch.DispatchAuthorized);

        var reports = Enumerable.Range(1, 24)
            .Select(index => (Key: $"settlement-report-{index}", ActualCost: index * 0.001m))
            .ToArray();
        var completions = await Task.WhenAll(reports.Select(report => Task.Run(async () =>
            (report.ActualCost, Result: await ledger.RecordProviderOutcomeAsync(
                reservation.ReservationId.Value,
                report.Key,
                PublicFreeTierProviderOutcome.Succeeded,
                actualCostKnown: true,
                actualProviderCostUsd: report.ActualCost)))));

        var winner = Assert.Single(completions, completion => completion.Result.Applied);
        Assert.Equal(23, completions.Count(completion => completion.Result.Code == "RESERVATION_ALREADY_FINALIZED"));
        Assert.Equal(winner.ActualCost, ledger.GlobalExposureUsd);
        Assert.Equal(0, ledger.UnknownExposureCount);
    }

    [Fact]
    public async Task Proven_undispatched_request_releases_cost_reservation_idempotently()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(
            ValidRequest(idempotencyKey: "never-dispatched") with { EstimatedProviderCostUsd = 0.009m });

        var released = await ledger.ReleaseUndispatchedAsync(reservation.ReservationId!.Value, "release-before-dispatch");
        var replay = await ledger.ReleaseUndispatchedAsync(reservation.ReservationId.Value, "release-before-dispatch");

        Assert.True(released.Applied);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(0m, ledger.GlobalExposureUsd);
        Assert.Equal(1, ledger.UserMessagesForUtcDay);
    }

    [Fact]
    public async Task Released_and_settled_replays_never_claim_dispatch_permission_or_live_reservation()
    {
        var releasedLedger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var releasedRequest = ValidRequest(idempotencyKey: "terminal-release-replay");
        var releasedReservation = await releasedLedger.TryReserveBeforeProviderCallAsync(releasedRequest);
        var release = await releasedLedger.ReleaseUndispatchedAsync(releasedReservation.ReservationId!.Value, "release-terminal");
        Assert.True(release.Applied);

        var releasedReplay = await releasedLedger.TryReserveBeforeProviderCallAsync(releasedRequest);
        var releasedDispatch = await releasedLedger.TryAuthorizeProviderDispatchAsync(releasedReservation.ReservationId.Value, "dispatch-after-release");
        Assert.True(releasedReplay.IsIdempotentReplay);
        Assert.False(releasedReplay.Reserved);
        Assert.Equal(PublicFreeTierReservationState.Released, releasedReplay.State);
        Assert.False(releasedReplay.Decision.Allowed);
        Assert.Equal("PUBLIC_FREE_TIER_RESERVATION_RELEASED", releasedReplay.Decision.Code);
        Assert.False(releasedDispatch.DispatchAuthorized);
        Assert.Equal(PublicFreeTierReservationState.Released, releasedDispatch.State);
        Assert.Equal(0m, releasedLedger.GlobalExposureUsd);

        var settledLedger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var settledRequest = ValidRequest(idempotencyKey: "terminal-settlement-replay");
        var settledReservation = await settledLedger.TryReserveBeforeProviderCallAsync(settledRequest);
        var dispatch = await settledLedger.TryAuthorizeProviderDispatchAsync(settledReservation.ReservationId!.Value, "settlement-dispatch");
        Assert.True(dispatch.DispatchAuthorized);
        var settlement = await settledLedger.RecordProviderOutcomeAsync(
            settledReservation.ReservationId.Value,
            "settlement-completion",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0.0005m);
        var settlementReplay = await settledLedger.RecordProviderOutcomeAsync(
            settledReservation.ReservationId.Value,
            "settlement-completion",
            PublicFreeTierProviderOutcome.Succeeded,
            actualCostKnown: true,
            actualProviderCostUsd: 0.0005m);

        var settledReplay = await settledLedger.TryReserveBeforeProviderCallAsync(settledRequest);
        var settledDispatch = await settledLedger.TryAuthorizeProviderDispatchAsync(settledReservation.ReservationId.Value, "settlement-dispatch");
        Assert.True(settlement.Applied);
        Assert.True(settlementReplay.IsIdempotentReplay);
        Assert.True(settledReplay.IsIdempotentReplay);
        Assert.False(settledReplay.Reserved);
        Assert.Equal(PublicFreeTierReservationState.Settled, settledReplay.State);
        Assert.False(settledReplay.Decision.Allowed);
        Assert.Equal("PUBLIC_FREE_TIER_RESERVATION_SETTLED", settledReplay.Decision.Code);
        Assert.False(settledDispatch.DispatchAuthorized);
        Assert.Equal(PublicFreeTierReservationState.Settled, settledDispatch.State);
    }

    [Fact]
    public async Task Dispatched_and_unknown_replays_keep_the_hold_but_never_authorize_a_second_call()
    {
        var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
        var request = ValidRequest(idempotencyKey: "dispatched-unknown-replay") with { EstimatedProviderCostUsd = 0.008m };
        var reservation = await ledger.TryReserveBeforeProviderCallAsync(request);
        var dispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId!.Value, "single-provider-dispatch");
        Assert.True(dispatch.DispatchAuthorized);

        var dispatchedReplay = await ledger.TryReserveBeforeProviderCallAsync(request);
        var duplicateDispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId.Value, "single-provider-dispatch");
        Assert.True(dispatchedReplay.Reserved);
        Assert.Equal(PublicFreeTierReservationState.Dispatched, dispatchedReplay.State);
        Assert.False(dispatchedReplay.Decision.Allowed);
        Assert.False(duplicateDispatch.DispatchAuthorized);
        Assert.True(duplicateDispatch.IsIdempotentReplay);

        var unknown = await ledger.RecordProviderOutcomeAsync(
            reservation.ReservationId.Value,
            "unknown-provider-result",
            PublicFreeTierProviderOutcome.Failed,
            actualCostKnown: false,
            actualProviderCostUsd: null);
        var unknownReplay = await ledger.TryReserveBeforeProviderCallAsync(request);
        var unknownDispatch = await ledger.TryAuthorizeProviderDispatchAsync(reservation.ReservationId.Value, "late-provider-dispatch");

        Assert.True(unknown.ReconciliationRequired);
        Assert.True(unknownReplay.Reserved);
        Assert.Equal(PublicFreeTierReservationState.ReconciliationRequired, unknownReplay.State);
        Assert.False(unknownReplay.Decision.Allowed);
        Assert.False(unknownDispatch.DispatchAuthorized);
        Assert.Equal(0.008m, ledger.GlobalExposureUsd);
    }

    [Fact]
    public async Task Undispatched_release_racing_dispatch_authorization_has_exactly_one_winner()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var ledger = new AtomicTestLedger(EnabledOptions(), NowUtc);
            var request = ValidRequest(idempotencyKey: $"release-dispatch-race-{attempt}") with { EstimatedProviderCostUsd = 0.009m };
            var reservation = await ledger.TryReserveBeforeProviderCallAsync(request);
            var reservationId = reservation.ReservationId!.Value;
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var releaseTask = Task.Run(async () =>
            {
                await start.Task;
                return await ledger.ReleaseUndispatchedAsync(reservationId, $"race-release-{attempt}");
            });
            var dispatchTask = Task.Run(async () =>
            {
                await start.Task;
                return await ledger.TryAuthorizeProviderDispatchAsync(reservationId, $"race-dispatch-{attempt}");
            });
            start.SetResult(true);

            await Task.WhenAll(releaseTask, dispatchTask);
            var release = await releaseTask;
            var dispatch = await dispatchTask;
            Assert.NotEqual(release.Applied, dispatch.DispatchAuthorized);

            var replay = await ledger.TryReserveBeforeProviderCallAsync(request);
            Assert.False(replay.Decision.Allowed);
            if (release.Applied)
            {
                Assert.Equal(PublicFreeTierReservationState.Released, replay.State);
                Assert.False(replay.Reserved);
                Assert.Equal(0m, ledger.GlobalExposureUsd);
            }
            else
            {
                Assert.True(dispatch.DispatchAuthorized);
                Assert.Equal(PublicFreeTierReservationState.Dispatched, replay.State);
                Assert.True(replay.Reserved);
                Assert.Equal(0.009m, ledger.GlobalExposureUsd);
            }
        }
    }

    private static PublicFreeTierPolicyOptions EnabledOptions() => new() { Enabled = true };

    private static PublicFreeTierAdmissionDecision Evaluate(
        PublicFreeTierAdmissionRequest? request = null,
        PublicFreeTierExposureSnapshot? snapshot = null,
        PublicFreeTierPolicyOptions? options = null) =>
        PublicFreeTierPolicy.Evaluate(
            request ?? ValidRequest(),
            snapshot ?? ValidSnapshot(),
            options ?? EnabledOptions(),
            NowUtc);

    private static PublicFreeTierAdmissionRequest ValidRequest(
        Guid? userId = null,
        Guid? workspaceId = null,
        string idempotencyKey = "server-generated-job-attempt-1") => new(
            IsAuthenticated: true,
            UserId: userId ?? UserId,
            WorkspaceId: workspaceId ?? WorkspaceId,
            AccountIsActive: true,
            EmailIsConfirmed: true,
            WorkspaceIsActive: true,
            IsWorkspaceMember: true,
            Plan: PublicFreeTierPlan.Free,
            Capability: PublicFreeTierCapability.TextChat,
            InputTokens: 500,
            MaxOutputTokens: 256,
            EstimatedProviderCostUsd: 0.001m,
            Currency: "USD",
            PricingVersion: "test-price-v1",
            IdempotencyKey: idempotencyKey);

    private static PublicFreeTierExposureSnapshot ValidSnapshot() => new(
        UserId: UserId,
        WorkspaceId: WorkspaceId,
        CapturedAtUtc: NowUtc,
        UtcDay: DateOnly.FromDateTime(NowUtc.UtcDateTime),
        UtcIsoWeekStart: UtcIsoWeekStart(DateOnly.FromDateTime(NowUtc.UtcDateTime)),
        UtcMonthStart: new DateOnly(NowUtc.Year, NowUtc.Month, 1),
        UserMessagesForUtcDay: 0,
        UserMessagesForUtcWeek: 0,
        UserProviderExposureForUtcDayUsd: 0m,
        UserProviderExposureForUtcMonthUsd: 0m,
        WorkspaceProviderExposureForUtcDayUsd: 0m,
        WorkspaceProviderExposureForUtcMonthUsd: 0m,
        GlobalProviderExposureForUtcDayUsd: 0m,
        GlobalProviderExposureForUtcMonthUsd: 0m,
        UnknownProviderCostEntries: 0);

    private static DateOnly UtcIsoWeekStart(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static void AssertDenied(PublicFreeTierAdmissionDecision decision, int statusCode, string code)
    {
        Assert.False(decision.Allowed);
        Assert.Equal(statusCode, decision.StatusCode);
        Assert.Equal(code, decision.Code);
    }

    /// <summary>
    /// Test-only atomic fake for exercising the ledger contract. It is not a production store,
    /// does not call providers, and does not prove PostgreSQL or multi-replica behavior.
    /// </summary>
    private sealed class AtomicTestLedger(PublicFreeTierPolicyOptions options, DateTimeOffset nowUtc) : IPublicFreeTierSpendLedger
    {
        private readonly object gate = new();
        private readonly Dictionary<(Guid UserId, Guid WorkspaceId, string Key), StoredReservation> byKey = new();
        private readonly Dictionary<Guid, StoredReservation> byId = new();

        public int ReservationCount
        {
            get { lock (gate) return byId.Count; }
        }

        public decimal GlobalExposureUsd
        {
            get { lock (gate) return byId.Values.Sum(item => item.ExposureUsd); }
        }

        public int UnknownExposureCount
        {
            get { lock (gate) return byId.Values.Count(item => item.UnknownExposure); }
        }

        public int UserMessagesForUtcDay
        {
            get { lock (gate) return byId.Values.Count(item => item.Request.UserId == UserId); }
        }

        public Task<PublicFreeTierReservationResult> TryReserveBeforeProviderCallAsync(
            PublicFreeTierAdmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                var key = (request.UserId, request.WorkspaceId, request.IdempotencyKey);
                if (byKey.TryGetValue(key, out var existing))
                {
                    if (existing.Request != request)
                        return Task.FromResult(Rejected(409, "PUBLIC_FREE_TIER_IDEMPOTENCY_KEY_CONFLICT", existing.State));
                    return Task.FromResult(ToReplay(existing));
                }

                var snapshot = CreateSnapshot(request);
                var decision = PublicFreeTierPolicy.Evaluate(request, snapshot, options, nowUtc);
                if (!decision.Allowed)
                    return Task.FromResult(new PublicFreeTierReservationResult(false, null, false, decision));

                var stored = new StoredReservation(Guid.NewGuid(), request, request.EstimatedProviderCostUsd!.Value);
                byKey.Add(key, stored);
                byId.Add(stored.Id, stored);
                return Task.FromResult(new PublicFreeTierReservationResult(
                    true,
                    stored.Id,
                    false,
                    PublicFreeTierAdmissionDecision.Allow(),
                    PublicFreeTierReservationState.Reserved));
            }
        }

        public Task<PublicFreeTierDispatchAuthorizationResult> TryAuthorizeProviderDispatchAsync(
            Guid reservationId,
            string dispatchIdempotencyKey,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!byId.TryGetValue(reservationId, out var stored))
                    return Task.FromResult(new PublicFreeTierDispatchAuthorizationResult(
                        false, false, PublicFreeTierReservationState.None, "RESERVATION_NOT_FOUND"));
                if (string.IsNullOrWhiteSpace(dispatchIdempotencyKey) || dispatchIdempotencyKey.Length > 200)
                    return Task.FromResult(new PublicFreeTierDispatchAuthorizationResult(
                        false, false, stored.State, "DISPATCH_IDEMPOTENCY_KEY_INVALID"));
                if (stored.State == PublicFreeTierReservationState.Dispatched)
                    return Task.FromResult(string.Equals(stored.DispatchKey, dispatchIdempotencyKey, StringComparison.Ordinal)
                        ? new PublicFreeTierDispatchAuthorizationResult(false, true, stored.State, "IDEMPOTENT_DISPATCH_REPLAY")
                        : new PublicFreeTierDispatchAuthorizationResult(false, false, stored.State, "PROVIDER_DISPATCH_ALREADY_AUTHORIZED"));
                if (stored.State != PublicFreeTierReservationState.Reserved)
                    return Task.FromResult(new PublicFreeTierDispatchAuthorizationResult(
                        false,
                        false,
                        stored.State,
                        stored.State switch
                        {
                            PublicFreeTierReservationState.ReconciliationRequired => "RECONCILIATION_REQUIRED",
                            PublicFreeTierReservationState.Settled => "RESERVATION_ALREADY_SETTLED",
                            PublicFreeTierReservationState.Released => "RESERVATION_ALREADY_RELEASED",
                            _ => "RESERVATION_NOT_DISPATCHABLE",
                        }));

                stored.State = PublicFreeTierReservationState.Dispatched;
                stored.DispatchKey = dispatchIdempotencyKey;
                return Task.FromResult(new PublicFreeTierDispatchAuthorizationResult(
                    true, false, stored.State, "DISPATCH_AUTHORIZED"));
            }
        }

        public Task<PublicFreeTierSettlementResult> RecordProviderOutcomeAsync(
            Guid reservationId,
            string completionIdempotencyKey,
            PublicFreeTierProviderOutcome outcome,
            bool actualCostKnown,
            decimal? actualProviderCostUsd,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!byId.TryGetValue(reservationId, out var stored))
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, false, "RESERVATION_NOT_FOUND"));
                if (stored.CompletionKeys.TryGetValue(completionIdempotencyKey, out var prior))
                    return Task.FromResult(prior == $"{outcome}:{actualCostKnown}:{actualProviderCostUsd}"
                        ? new PublicFreeTierSettlementResult(true, true, stored.UnknownExposure, "IDEMPOTENT_REPLAY")
                        : new PublicFreeTierSettlementResult(false, false, stored.UnknownExposure, "COMPLETION_KEY_CONFLICT"));
                if (actualCostKnown && (!actualProviderCostUsd.HasValue || actualProviderCostUsd.Value < 0m))
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, stored.UnknownExposure, "ACTUAL_COST_INVALID"));
                if (!actualCostKnown && actualProviderCostUsd.HasValue)
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, stored.UnknownExposure, "ACTUAL_COST_INVALID"));
                if (stored.State == PublicFreeTierReservationState.ReconciliationRequired && !actualCostKnown)
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, true, "RECONCILIATION_REQUIRED"));
                if (stored.State is PublicFreeTierReservationState.Settled or PublicFreeTierReservationState.Released)
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, false, "RESERVATION_ALREADY_FINALIZED"));
                if (stored.State is not (PublicFreeTierReservationState.Dispatched or PublicFreeTierReservationState.ReconciliationRequired))
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, stored.UnknownExposure, "PROVIDER_DISPATCH_NOT_AUTHORIZED"));

                stored.CompletionKeys.Add(completionIdempotencyKey, $"{outcome}:{actualCostKnown}:{actualProviderCostUsd}");
                if (actualCostKnown)
                {
                    stored.ExposureUsd = actualProviderCostUsd!.Value;
                    stored.UnknownExposure = false;
                    stored.State = PublicFreeTierReservationState.Settled;
                }
                else
                {
                    // Provider failure/timeout can still incur charges. Keep the full estimate
                    // reserved and block admission until a reconciliation supplies actual cost.
                    stored.UnknownExposure = true;
                    stored.State = PublicFreeTierReservationState.ReconciliationRequired;
                }
                return Task.FromResult(new PublicFreeTierSettlementResult(
                    true,
                    false,
                    stored.UnknownExposure,
                    stored.UnknownExposure ? "RECONCILIATION_REQUIRED" : "SETTLED"));
            }
        }

        public Task<PublicFreeTierSettlementResult> ReleaseUndispatchedAsync(
            Guid reservationId,
            string completionIdempotencyKey,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (!byId.TryGetValue(reservationId, out var stored))
                    return Task.FromResult(new PublicFreeTierSettlementResult(false, false, false, "RESERVATION_NOT_FOUND"));
                if (stored.CompletionKeys.TryGetValue(completionIdempotencyKey, out var prior))
                    return Task.FromResult(prior == "RELEASE_UNDISPATCHED"
                        ? new PublicFreeTierSettlementResult(true, true, false, "IDEMPOTENT_REPLAY")
                        : new PublicFreeTierSettlementResult(false, false, stored.UnknownExposure, "COMPLETION_KEY_CONFLICT"));
                if (stored.State != PublicFreeTierReservationState.Reserved)
                    return Task.FromResult(new PublicFreeTierSettlementResult(
                        false,
                        false,
                        stored.UnknownExposure,
                        stored.State switch
                        {
                            PublicFreeTierReservationState.Dispatched => "PROVIDER_ALREADY_DISPATCHED",
                            PublicFreeTierReservationState.ReconciliationRequired => "RECONCILIATION_REQUIRED",
                            PublicFreeTierReservationState.Settled => "RESERVATION_ALREADY_FINALIZED",
                            PublicFreeTierReservationState.Released => "RESERVATION_ALREADY_RELEASED",
                            _ => "RESERVATION_NOT_DISPATCHABLE",
                        }));

                stored.CompletionKeys.Add(completionIdempotencyKey, "RELEASE_UNDISPATCHED");
                stored.ExposureUsd = 0m;
                stored.UnknownExposure = false;
                stored.State = PublicFreeTierReservationState.Released;
                return Task.FromResult(new PublicFreeTierSettlementResult(true, false, false, "RELEASED"));
            }
        }

        private PublicFreeTierExposureSnapshot CreateSnapshot(PublicFreeTierAdmissionRequest request)
        {
            var all = byId.Values.ToArray();
            var user = all.Where(item => item.Request.UserId == request.UserId).ToArray();
            var workspace = all.Where(item => item.Request.WorkspaceId == request.WorkspaceId).ToArray();
            var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            return new PublicFreeTierExposureSnapshot(
                request.UserId,
                request.WorkspaceId,
                nowUtc,
                today,
                UtcIsoWeekStart(today),
                new DateOnly(today.Year, today.Month, 1),
                user.Length,
                user.Length,
                user.Sum(item => item.ExposureUsd),
                user.Sum(item => item.ExposureUsd),
                workspace.Sum(item => item.ExposureUsd),
                workspace.Sum(item => item.ExposureUsd),
                all.Sum(item => item.ExposureUsd),
                all.Sum(item => item.ExposureUsd),
                all.Count(item => item.UnknownExposure));
        }

        private static PublicFreeTierReservationResult ToReplay(StoredReservation stored)
        {
            var decision = stored.State switch
            {
                PublicFreeTierReservationState.Reserved => PublicFreeTierAdmissionDecision.Deny(
                    409, "PUBLIC_FREE_TIER_RESERVATION_REPLAY", "This provider attempt is already reserved; do not dispatch from a reservation replay."),
                PublicFreeTierReservationState.Dispatched => PublicFreeTierAdmissionDecision.Deny(
                    409, "PUBLIC_FREE_TIER_DISPATCH_ALREADY_STARTED", "This provider attempt has already been authorized for dispatch."),
                PublicFreeTierReservationState.ReconciliationRequired => PublicFreeTierAdmissionDecision.Deny(
                    503, "PUBLIC_FREE_TIER_RECONCILIATION_REQUIRED", "Free AI is paused while provider usage is reconciled."),
                PublicFreeTierReservationState.Settled => PublicFreeTierAdmissionDecision.Deny(
                    409, "PUBLIC_FREE_TIER_RESERVATION_SETTLED", "This provider attempt is already finalized."),
                PublicFreeTierReservationState.Released => PublicFreeTierAdmissionDecision.Deny(
                    409, "PUBLIC_FREE_TIER_RESERVATION_RELEASED", "This provider attempt was released and cannot be dispatched."),
                _ => PublicFreeTierAdmissionDecision.Deny(
                    503, "PUBLIC_FREE_TIER_RESERVATION_STATE_INVALID", "Free AI usage could not be verified safely."),
            };
            var exposureHeld = stored.State is PublicFreeTierReservationState.Reserved
                or PublicFreeTierReservationState.Dispatched
                or PublicFreeTierReservationState.ReconciliationRequired;
            return new PublicFreeTierReservationResult(exposureHeld, stored.Id, true, decision, stored.State);
        }

        private static PublicFreeTierReservationResult Rejected(
            int statusCode,
            string code,
            PublicFreeTierReservationState state = PublicFreeTierReservationState.None) => new(
            false,
            null,
            false,
            PublicFreeTierAdmissionDecision.Deny(statusCode, code, "The request could not be admitted safely."),
            state);

        private sealed class StoredReservation(Guid id, PublicFreeTierAdmissionRequest request, decimal reservedUsd)
        {
            public Guid Id { get; } = id;
            public PublicFreeTierAdmissionRequest Request { get; } = request;
            public decimal ExposureUsd { get; set; } = reservedUsd;
            public bool UnknownExposure { get; set; }
            public PublicFreeTierReservationState State { get; set; } = PublicFreeTierReservationState.Reserved;
            public string? DispatchKey { get; set; }
            public Dictionary<string, string> CompletionKeys { get; } = new(StringComparer.Ordinal);
        }
    }
}


/// <summary>
/// Provider-free route-level regression for exhausted Free-tier budgets. All provider seams are
/// replaced with deterministic fakes; this verifies runtime wiring rather than the pure policy.
/// </summary>
public sealed class PublicFreeTierRuntimeNegativeAcceptanceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Synthetic_exhausted_free_budget_must_reject_before_every_fake_provider_dispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"taslim-free-tier-negative-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var observations = new List<DispatchObservation>();
        var policyControls = EvaluatePolicyControls();
        Assert.Equal("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT", policyControls.UserCapCode);
        Assert.Equal("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT", policyControls.GlobalCapCode);

        using var factory = new NegativeRuntimeApiFactory(root);
        using var startupScope = factory.Services.CreateScope();
        await startupScope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreatedAsync();
        var activeGuard = factory.Services.GetRequiredService<IProviderCostGuard>().GetType().Name;

        // Route-auth negative control: one fresh test host per module avoids interpreting an
        // anonymous-IP rate-limit response as authorization enforcement.
        foreach (var module in Modules)
            observations.Add(await RunUnauthenticatedAsync(root, module));

        observations.Add(await RunUnconfirmedChatAsync(factory));

        // Each newly registered workspace is provisioned on the canonical Free plan; the test
        // marks its address confirmed so the only relevant missing gate is spend admission.
        observations.AddRange(await RunChatAsync(factory));
        observations.AddRange(await RunImageAsync(factory));
        observations.AddRange(await RunDocumentAsync(factory));
        observations.AddRange(await RunPresentationAsync(factory));
        observations.AddRange(await RunMovieAsync(factory));

        var userBudgetFailures = observations.Where(item => item.Scenario == "user_daily_cap_exhausted"
            && (item.LedgerReserveDelta < 1 || item.ProviderCallDelta != 0 || item.DispatchAuthorizationDelta != 0 || item.CreditLedgerEntryDelta != 0
                || item.StatusCodes.Any(status => status < 400))).ToArray();
        var globalConcurrentFailures = observations.Where(item => item.Scenario == "global_daily_cap_exhausted_concurrent_retry"
            && (item.LedgerReserveDelta < 1 || item.ProviderCallDelta != 0 || item.DispatchAuthorizationDelta != 0 || item.CreditLedgerEntryDelta != 0
                || item.StatusCodes.Any(status => status < 400))).ToArray();
        var anonymousFailures = observations.Where(item => item.Scenario == "unauthenticated"
            && (item.StatusCodes.Any(status => status is not (401 or 403)) || item.ProviderCallDelta != 0 || item.LedgerReserveDelta != 0)).ToArray();
        var unconfirmedRegistrationFailures = observations.Where(item => item.Scenario == "unconfirmed_registration"
            && (item.EmailConfirmed || item.StatusCodes.Length == 0 || item.StatusCodes.Any(status => status is not (401 or 403))
                || item.ProviderCallDelta != 0 || item.DispatchAuthorizationDelta != 0
                || item.CreditLedgerEntryDelta != 0 || item.ChargedAmountUsd != 0m)).ToArray();

        var chargedAmount = await SumCustomerChargesAsync(factory, null);
        var egressAttempts = factory.EgressAttempts.Count;
        var report = new NegativeAcceptanceReport(
            Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            activeGuard,
            "Synthetic ledger denies exhausted per-user/global budgets; all provider adapters are fakes; IHttpClientFactory blocks outbound requests.",
            policyControls,
            observations,
            userBudgetFailures.Length,
            globalConcurrentFailures.Length,
            anonymousFailures.Length,
            unconfirmedRegistrationFailures.Length,
            chargedAmount,
            egressAttempts);
        await File.WriteAllTextAsync(Path.Combine(root, "negative-acceptance-report.json"), JsonSerializer.Serialize(report, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(root, "negative-acceptance-summary.md"), RenderSummary(report));

        var failureSummary = JsonSerializer.Serialize(new
        {
            userBudgetFailures,
            globalConcurrentFailures,
            anonymousFailures,
            unconfirmedRegistrationFailures,
            report.RuntimeProviderCostGuard,
            report.ChargedAmountUsd,
            report.BlockedOutboundHttpAttempts,
        }, JsonOptions);
        Assert.Equal(0m, chargedAmount);
        Assert.Equal(0, egressAttempts);
        Assert.True(userBudgetFailures.Length == 0 && globalConcurrentFailures.Length == 0 && anonymousFailures.Length == 0
            && unconfirmedRegistrationFailures.Length == 0,
            "NEGATIVE ACCEPTANCE RED: real module dispatch did not fail closed. Full report: " + Path.Combine(root, "negative-acceptance-report.json") + " Details=" + failureSummary);
    }

    private static string[] Modules => ["Chat", "Image", "Document", "Presentation", "Movie"];

    private static async Task<IReadOnlyList<DispatchObservation>> RunChatAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Chat");
        await ConfirmFreeAccountAsync(factory, auth);

        var result = new List<DispatchObservation>();
        var firstConversation = await CreateConversationAsync(client, auth.PersonalWorkspace.Id);
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Chat", "user_daily_cap_exhausted", auth.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, $"/api/conversations/{firstConversation.Id}/messages", new
            {
                content = "Synthetic spend-cap rejection probe.", requestId = "chat-user-cap-probe-01",
            }), () => factory.Calls.Get("Chat")));

        var retryConversation = await CreateConversationAsync(client, auth.PersonalWorkspace.Id);
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Chat", "global_daily_cap_exhausted_concurrent_retry", auth.PersonalWorkspace.Id,
            () => SendPairAsync(client, HttpMethod.Post, $"/api/conversations/{retryConversation.Id}/messages", new
            {
                content = "Synthetic concurrent retry probe.", requestId = "chat-global-race-02",
            }, null), () => factory.Calls.Get("Chat")));
        return result;
    }

    private static async Task<DispatchObservation> RunUnconfirmedChatAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "UnconfirmedChat");
        Assert.False(await IsEmailConfirmedAsync(factory, auth.User.Id));
        var conversation = await CreateConversationAsync(client, auth.PersonalWorkspace.Id);
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_EMAIL_CONFIRMATION_REQUIRED");

        return await ObserveAsync(factory, client, "Chat", "unconfirmed_registration", auth.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
            {
                content = "Unconfirmed registration must not reach a provider.", requestId = $"unconfirmed-{Guid.NewGuid():N}",
            }), () => factory.Calls.Get("Chat"));
    }

    private static async Task<IReadOnlyList<DispatchObservation>> RunImageAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Image");
        await ConfirmFreeAccountAsync(factory, auth);
        var result = new List<DispatchObservation>();
        object payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Synthetic firewall image",
            description = "A small deterministic test image on a pale blue background.",
            style = "product",
            aspectRatio = "square",
            quality = "standard",
            mood = "calm",
            background = "plain",
        };
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Image", "user_daily_cap_exhausted", auth.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, "/api/image-generation/jobs", payload, "image-user-cap-01"),
            () => factory.Calls.Get("Image")));
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Image", "global_daily_cap_exhausted_concurrent_retry", auth.PersonalWorkspace.Id,
            () => SendPairAsync(client, HttpMethod.Post, "/api/image-generation/jobs", payload, "image-global-race-02"),
            () => factory.Calls.Get("Image")));
        return result;
    }

    private static async Task<IReadOnlyList<DispatchObservation>> RunDocumentAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Document");
        await ConfirmFreeAccountAsync(factory, auth);
        var result = new List<DispatchObservation>();
        object payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Synthetic launch brief",
            prompt = "Create a concise launch brief for a local provider-free safety test.",
            description = "A bounded synthetic document request.",
            documentType = "report",
            length = "short",
            audience = "test reviewers",
            additionalInstructions = "Use concise, safe language.",
            attachmentIds = Array.Empty<Guid>(),
            language = "en",
            outputFormat = "both",
            tone = "professional",
            includeTableOfContents = false,
        };
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Document", "user_daily_cap_exhausted", auth.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, "/api/document-generation/jobs", payload, "document-user-cap-01"),
            () => factory.Calls.Get("Document")));
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Document", "global_daily_cap_exhausted_concurrent_retry", auth.PersonalWorkspace.Id,
            () => SendPairAsync(client, HttpMethod.Post, "/api/document-generation/jobs", payload, "document-global-race-02"),
            () => factory.Calls.Get("Document")));
        return result;
    }

    private static async Task<IReadOnlyList<DispatchObservation>> RunPresentationAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var auth = await RegisterAsync(client, "Presentation");
        await ConfirmFreeAccountAsync(factory, auth);
        var result = new List<DispatchObservation>();
        object payload = new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            title = "Synthetic safety briefing",
            description = "A provider-free synthetic acceptance request.",
            presentationType = "business",
            length = "standard",
            tone = "professional",
            language = "ku",
            includeAgenda = true,
            includeClosingNextSteps = true,
            attachmentIds = Array.Empty<Guid>(),
        };
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Presentation", "user_daily_cap_exhausted", auth.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, "/api/presentation-generation/jobs", payload, "presentation-user-cap-01"),
            () => factory.Calls.Get("Presentation")));
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Presentation", "global_daily_cap_exhausted_concurrent_retry", auth.PersonalWorkspace.Id,
            () => SendPairAsync(client, HttpMethod.Post, "/api/presentation-generation/jobs", payload, "presentation-global-race-02"),
            () => factory.Calls.Get("Presentation")));
        return result;
    }

    private static async Task<IReadOnlyList<DispatchObservation>> RunMovieAsync(NegativeRuntimeApiFactory factory)
    {
        using var client = factory.CreateClient();
        var result = new List<DispatchObservation>();
        var firstFixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Free-tier negative Movie probe");
        await ConfirmFreeAccountAsync(factory, firstFixture.Owner);
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Movie", "user_daily_cap_exhausted", firstFixture.Owner.PersonalWorkspace.Id,
            () => SendOneAsync(client, HttpMethod.Post, $"/api/movie-studio/shots/{firstFixture.Shot.Id}/generate", new { title = "Synthetic Movie provider call" }, "movie-user-cap-01"),
            () => factory.Calls.Get("Movie")));

        // A fresh Free account/project makes this a new provider attempt rather than a replay of
        // the already-completed first request; two same-key calls race through the real route.
        var retryFixture = await MovieOperationalFixtures.CreateFullMovieAsync(client, title: "Free-tier concurrent Movie retry probe");
        await ConfirmFreeAccountAsync(factory, retryFixture.Owner);
        factory.Ledger.SetDenialCode("PUBLIC_FREE_TIER_GLOBAL_DAILY_SPEND_LIMIT");
        result.Add(await ObserveAsync(factory, client, "Movie", "global_daily_cap_exhausted_concurrent_retry", retryFixture.Owner.PersonalWorkspace.Id,
            () => SendPairAsync(client, HttpMethod.Post, $"/api/movie-studio/shots/{retryFixture.Shot.Id}/generate", new { title = "Synthetic concurrent Movie retry" }, "movie-global-race-02"),
            () => factory.Calls.Get("Movie")));
        return result;
    }

    private static async Task<DispatchObservation> RunUnauthenticatedAsync(string root, string module)
    {
        using var factory = new NegativeRuntimeApiFactory(root);
        using var client = factory.CreateClient();
        var randomId = Guid.NewGuid();
        var (method, path, payload, key) = module switch
        {
            "Chat" => (HttpMethod.Post, $"/api/conversations/{randomId}/messages", (object)new { content = "anonymous synthetic probe", requestId = "anon-chat-01" }, (string?)null),
            "Image" => (HttpMethod.Post, "/api/image-generation/jobs", (object)new { workspaceId = randomId, title = "anon", description = "anonymous synthetic probe", style = "product", aspectRatio = "square", quality = "standard", mood = "calm", background = "plain" }, (string?)"anon-image-01"),
            "Document" => (HttpMethod.Post, "/api/document-generation/jobs", (object)new { workspaceId = randomId, title = "anon", prompt = "anonymous synthetic probe", description = "anonymous synthetic probe", attachmentIds = Array.Empty<Guid>(), language = "en", outputFormat = "both", tone = "professional" }, (string?)"anon-document-01"),
            "Presentation" => (HttpMethod.Post, "/api/presentation-generation/jobs", (object)new { workspaceId = randomId, title = "anon", description = "anonymous synthetic probe", presentationType = "business", length = "standard", tone = "professional", language = "ku", attachmentIds = Array.Empty<Guid>() }, (string?)"anon-presentation-01"),
            _ => (HttpMethod.Post, $"/api/movie-studio/shots/{randomId}/generate", (object)new { title = "anonymous synthetic probe" }, (string?)"anon-movie-01"),
        };
        var before = factory.Calls.Get(module);
        var reserveBefore = factory.Ledger.ReserveCalls;
        var response = await SendOneAsync(client, method, path, payload, key);
        var status = response[0].StatusCode;
        var error = response[0].ErrorCode;
        var providerDelta = factory.Calls.Get(module) - before;
        return new DispatchObservation(module, "unauthenticated", [status], error is null ? Array.Empty<string>() : [error], providerDelta,
            factory.Ledger.ReserveCalls - reserveBefore, 0,
            0, 0m, false, factory.DatabasePath);
    }

    private static async Task<DispatchObservation> ObserveAsync(
        NegativeRuntimeApiFactory factory,
        HttpClient client,
        string module,
        string scenario,
        Guid workspaceId,
        Func<Task<ResponseInfo[]>> send,
        Func<int> providerCalls)
    {
        var providerBefore = providerCalls();
        var reserveBefore = factory.Ledger.ReserveCalls;
        var dispatchBefore = factory.Ledger.DispatchAuthorizationCalls;
        var creditBefore = await CountCreditEntriesAsync(factory, workspaceId);
        var responses = await send();
        var jobIds = responses.Where(item => item.JobId.HasValue).Select(item => item.JobId!.Value).Distinct().ToArray();
        foreach (var jobId in jobIds) await WaitForTerminalAsync(client, jobId);
        var providerDelta = providerCalls() - providerBefore;
        var reserveDelta = factory.Ledger.ReserveCalls - reserveBefore;
        var dispatchDelta = factory.Ledger.DispatchAuthorizationCalls - dispatchBefore;
        var creditDelta = await CountCreditEntriesAsync(factory, workspaceId) - creditBefore;
        var charged = await SumCustomerChargesAsync(factory, workspaceId);
        var isFree = await IsActiveFreeSubscriptionAsync(factory, workspaceId);
        var emailConfirmed = await IsWorkspaceEmailConfirmedAsync(factory, workspaceId);
        return new DispatchObservation(module, scenario,
            responses.Select(item => item.StatusCode).ToArray(),
            responses.Select(item => item.ErrorCode).Where(item => item is not null).Cast<string>().ToArray(),
            providerDelta, reserveDelta, dispatchDelta, creditDelta, charged, isFree, factory.DatabasePath, emailConfirmed);
    }

    private static async Task<ResponseInfo[]> SendOneAsync(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey = null)
    {
        using var request = await CreateRequestAsync(client, method, path, payload, idempotencyKey);
        using var response = await client.SendAsync(request);
        return [await ReadResponseAsync(response, client)];
    }

    private static async Task<ResponseInfo[]> SendPairAsync(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey)
    {
        var token = await GetCsrfTokenAsync(client);
        using var first = CreateRequest(method, path, payload, token, idempotencyKey);
        using var second = CreateRequest(method, path, payload, token, idempotencyKey);
        using var responses = new ResponsePair(await Task.WhenAll(client.SendAsync(first), client.SendAsync(second)));
        return [await ReadResponseAsync(responses.First, client), await ReadResponseAsync(responses.Second, client)];
    }

    private static async Task<HttpRequestMessage> CreateRequestAsync(HttpClient client, HttpMethod method, string path, object? payload, string? idempotencyKey)
    {
        var token = await GetCsrfTokenAsync(client);
        return CreateRequest(method, path, payload, token, idempotencyKey);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload, string token, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    private static async Task<ResponseInfo> ReadResponseAsync(HttpResponseMessage response, HttpClient client)
    {
        var body = await response.Content.ReadAsStringAsync();
        string? errorCode = null;
        Guid? jobId = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)) errorCode = code.GetString();
            if (root.TryGetProperty("job", out var job) && job.ValueKind == JsonValueKind.Object && job.TryGetProperty("id", out var id))
                jobId = id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
        }
        catch (JsonException) { }
        return new ResponseInfo((int)response.StatusCode, errorCode, jobId, body, null);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return csrf.GetProperty("token").GetString()!;
    }

    private static async Task<AuthResponse> RegisterAsync(HttpClient client, string module)
    {
        var response = (await SendOneAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = $"Negative {module} Tester",
            email = $"negative-{module.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        }))[0];
        Assert.Equal(200, response.StatusCode);
        return JsonSerializer.Deserialize<AuthResponse>(response.Body, JsonOptions)!;
    }

    private static async Task<ConversationDto> CreateConversationAsync(HttpClient client, Guid workspaceId)
    {
        var response = (await SendOneAsync(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { }))[0];
        Assert.InRange(response.StatusCode, 200, 299);
        return JsonSerializer.Deserialize<ConversationDto>(response.Body, JsonOptions)!;
    }

    private static async Task ConfirmFreeAccountAsync(NegativeRuntimeApiFactory factory, AuthResponse auth)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == auth.User.Id);
        user.EmailConfirmed = true;
        await db.SaveChangesAsync();
        var subscription = await db.Subscriptions.Include(item => item.Plan).SingleAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id);
        Assert.Equal("free", subscription.Plan.Code);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    private static async Task<bool> IsActiveFreeSubscriptionAsync(NegativeRuntimeApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.Subscriptions.Include(item => item.Plan).AnyAsync(item => item.WorkspaceId == workspaceId
            && item.Status == SubscriptionStatus.Active && item.Plan.Code == "free");
    }

    private static async Task<bool> IsWorkspaceEmailConfirmedAsync(NegativeRuntimeApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var userId = await db.WorkspaceMembers.Where(item => item.WorkspaceId == workspaceId)
            .Select(item => item.UserId).FirstAsync();
        return await db.Users.Where(item => item.Id == userId).Select(item => item.EmailConfirmed).SingleAsync();
    }

    private static async Task<bool> IsEmailConfirmedAsync(NegativeRuntimeApiFactory factory, Guid userId)
    {
        if (userId == Guid.Empty) return false;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        return await db.Users.Where(item => item.Id == userId).Select(item => item.EmailConfirmed).SingleAsync();
    }

    private static async Task<int> CountCreditEntriesAsync(NegativeRuntimeApiFactory factory, Guid workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TaslimDbContext>().CreditLedgerEntries.CountAsync(item => item.WorkspaceId == workspaceId);
    }

    private static async Task<decimal> SumCustomerChargesAsync(NegativeRuntimeApiFactory factory, Guid? workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        var usage = scope.ServiceProvider.GetRequiredService<TaslimDbContext>().UsageTransactions.AsNoTracking();
        if (workspaceId.HasValue) usage = usage.Where(item => item.WorkspaceId == workspaceId.Value);
        return (await usage.Select(item => item.ChargedAmount).ToListAsync()).Sum();
    }

    private static async Task WaitForTerminalAsync(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var statusResponse = await client.GetAsync($"/api/generation/jobs/{jobId}");
            if (statusResponse.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"Generation job {jobId} was not visible to its owner.");
            var statusBody = await statusResponse.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(statusBody);
            var status = json.RootElement.GetProperty("status").GetString();
            if (status is "Succeeded" or "Failed" or "Cancelled") return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Generation job {jobId} did not reach terminal state.");
    }

    private static async Task<int> GetCreditCountAsync(NegativeRuntimeApiFactory factory, Guid workspaceId) => await CountCreditEntriesAsync(factory, workspaceId);

    private static PolicyControlObservation EvaluatePolicyControls()
    {
        var options = new PublicFreeTierPolicyOptions { Enabled = true };
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var week = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        var month = new DateOnly(day.Year, day.Month, 1);
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var request = new PublicFreeTierAdmissionRequest(true, userId, workspaceId, true, true, true, true,
            PublicFreeTierPlan.Free, PublicFreeTierCapability.TextChat, 10, 64, 0.01m, "USD", "synthetic-v1", "policy-control");
        var userCapped = Snapshot(userId, workspaceId, now, day, week, month,
            options.MaxProviderSpendPerUserPerUtcDayUsd, 0m);
        var globalCapped = Snapshot(userId, workspaceId, now, day, week, month,
            0m, options.MaxGlobalProviderSpendPerUtcDayUsd);
        var userDecision = PublicFreeTierPolicy.Evaluate(request, userCapped, options, now);
        var globalDecision = PublicFreeTierPolicy.Evaluate(request, globalCapped, options, now);
        return new PolicyControlObservation(userDecision.Allowed, userDecision.Code, globalDecision.Allowed, globalDecision.Code);
    }

    private static PublicFreeTierExposureSnapshot Snapshot(Guid userId, Guid workspaceId, DateTimeOffset now,
        DateOnly day, DateOnly week, DateOnly month, decimal userDaily, decimal globalDaily) =>
        new(userId, workspaceId, now, day, week, month, 0, 0, userDaily, 0m, 0m, 0m, globalDaily, 0m, 0);

    private static string RenderSummary(NegativeAcceptanceReport report)
    {
        var lines = new List<string>
        {
            "# Free-tier provider-spend negative acceptance",
            "",
            $"- Tested source head: `{report.Head}`",
            $"- Runtime IProviderCostGuard registration: `{report.RuntimeProviderCostGuard}`",
            $"- Synthetic policy controls: user cap `{report.PolicyControls.UserCapCode}`; global cap `{report.PolicyControls.GlobalCapCode}` (both denied by the pure policy).",
            $"- Provider-free execution: {report.EgressNote}",
            $"- Runtime customer charge total: ${report.ChargedAmountUsd:0.000000} USD; blocked outbound HTTP attempts: {report.BlockedOutboundHttpAttempts}.",
            $"- Failing user-cap module cases: {report.UserBudgetFailureCount}; failing global/concurrent cases: {report.GlobalConcurrentFailureCount}; failing unauth cases: {report.UnauthenticatedFailureCount}; failing unconfirmed-registration cases: {report.UnconfirmedRegistrationFailureCount}.",
            "",
            "| Module | Scenario | HTTP status(es) | Error code(s) | Fake provider delta | ledger reserve delta | customer credit-entry delta | active Free plan | email confirmed |",
            "|---|---|---:|---|---:|---:|---:|---|---|",
        };
        foreach (var item in report.Observations)
            lines.Add($"| {item.Module} | {item.Scenario} | {string.Join(",", item.StatusCodes)} | {string.Join(",", item.ErrorCodes)} | {item.ProviderCallDelta} | {item.LedgerReserveDelta} | {item.CreditLedgerEntryDelta} | {item.IsActiveFreePlan} | {item.EmailConfirmed} |");
        lines.Add("");
        lines.Add("An authenticated exhausted-budget case is a positive safety result only if the synthetic ledger was called, every request was rejected before provider invocation, and the customer credit ledger was unchanged. Unauthenticated and unconfirmed registrations should receive 401/403 before provider invocation. This report is provider-free and does not enable or alter production code/configuration.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ResponseInfo(int StatusCode, string? ErrorCode, Guid? JobId, string Body, Guid? UserId);
    private sealed record DispatchObservation(string Module, string Scenario, int[] StatusCodes, string[] ErrorCodes,
        int ProviderCallDelta, int LedgerReserveDelta, int DispatchAuthorizationDelta, int CreditLedgerEntryDelta,
        decimal ChargedAmountUsd, bool IsActiveFreePlan, string DatabasePath, bool EmailConfirmed = false);
    private sealed record PolicyControlObservation(bool UserCapAllowed, string UserCapCode, bool GlobalCapAllowed, string GlobalCapCode);
    private sealed record NegativeAcceptanceReport(string Head, string RuntimeProviderCostGuard, string EgressNote,
        PolicyControlObservation PolicyControls, IReadOnlyList<DispatchObservation> Observations,
        int UserBudgetFailureCount, int GlobalConcurrentFailureCount, int UnauthenticatedFailureCount,
        int UnconfirmedRegistrationFailureCount, decimal ChargedAmountUsd, int BlockedOutboundHttpAttempts);

    private sealed class ResponsePair : IDisposable
    {
        public ResponsePair(HttpResponseMessage[] responses) { First = responses[0]; Second = responses[1]; }
        public HttpResponseMessage First { get; }
        public HttpResponseMessage Second { get; }
        public void Dispose() { First.Dispose(); Second.Dispose(); }
    }
}

internal sealed class NegativeRuntimeApiFactory : GenerationJobsApiFactory
{
    private readonly string root;
    public ProviderDispatchCounters Calls { get; } = new();
    public ExhaustedLedgerProbe Ledger { get; } = new();
    public ConcurrentBag<string> EgressAttempts { get; } = new();
    public string DatabasePath => databasePath;

    public NegativeRuntimeApiFactory(string root) => this.root = Path.GetFullPath(root);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("GenerationJobs:WorkerEnabled", "true");
        builder.UseSetting("GenerationJobs:PollIntervalMilliseconds", "100");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Warning");
        builder.UseSetting("UsageControls:GuardrailsEnabled", "false");
        builder.UseSetting("GenerationBudget:Enabled", "false");
        builder.UseSetting("Billing:CustomerChargingEnabled", "false");
        builder.UseSetting("Ai:OpenAI:Enabled", "false");
        builder.UseSetting("Ai:AllowMockProvider", "false");
        builder.UseSetting("ImageGeneration:Enabled", "true");
        builder.UseSetting("ImageGeneration:ProviderKey", "synthetic-image");
        builder.UseSetting("DocumentGeneration:Enabled", "true");
        builder.UseSetting("PresentationGeneration:Enabled", "true");
        builder.UseSetting("MovieVideo:Enabled", "true");
        builder.UseSetting("MovieVideo:ProviderKey", "fake-movie");
        builder.UseSetting("MovieVideo:StatusPollIntervalSeconds", "0");
        builder.UseSetting("MovieVideo:MaxStatusPolls", "3");
        builder.UseSetting("Files:StorageProvider", "Local");
        builder.UseSetting("Files:LocalRootPath", Path.Combine(root, "files"));

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Calls);
            services.AddSingleton(Ledger);
            services.AddSingleton<IPublicFreeTierSpendLedger>(sp => sp.GetRequiredService<ExhaustedLedgerProbe>());

            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(sp => new CountingChatCompletionService(sp.GetRequiredService<ProviderDispatchCounters>()));

            services.RemoveAll<IImageGenerationProvider>();
            services.AddSingleton<IImageGenerationProvider>(sp => new CountingImageProvider(sp.GetRequiredService<ProviderDispatchCounters>()));

            services.RemoveAll<IDocumentGenerationProvider>();
            services.AddSingleton<IDocumentGenerationProvider>(sp => new CountingDocumentProvider(sp.GetRequiredService<ProviderDispatchCounters>()));

            services.RemoveAll<IPresentationGenerationProvider>();
            services.AddSingleton<IPresentationGenerationProvider>(sp => new CountingPresentationProvider(sp.GetRequiredService<ProviderDispatchCounters>()));

            services.RemoveAll<IMovieVideoProvider>();
            services.AddSingleton(new FakeProviderScenarioCatalog());
            services.AddSingleton(new FakeProviderCallLog());
            services.AddSingleton<IMovieVideoProvider>(sp => new CountingMovieVideoProvider(
                sp.GetRequiredService<ProviderDispatchCounters>(),
                sp.GetRequiredService<FakeProviderScenarioCatalog>(),
                sp.GetRequiredService<FakeProviderCallLog>()));

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(sp => new NoEgressHttpClientFactory(sp.GetRequiredService<ConcurrentBag<string>>()));
            services.AddSingleton(EgressAttempts);
        });
    }
}

internal sealed class ProviderDispatchCounters
{
    private readonly ConcurrentDictionary<string, int> values = new(StringComparer.Ordinal);
    public void Record(string module) => values.AddOrUpdate(module, 1, (_, value) => value + 1);
    public int Get(string module) => values.TryGetValue(module, out var value) ? value : 0;
}

internal sealed class ExhaustedLedgerProbe : IPublicFreeTierSpendLedger
{
    private int reserveCalls;
    private int dispatchAuthorizationCalls;
    private string denialCode = "PUBLIC_FREE_TIER_USER_DAILY_SPEND_LIMIT";
    public int ReserveCalls => Volatile.Read(ref reserveCalls);
    public int DispatchAuthorizationCalls => Volatile.Read(ref dispatchAuthorizationCalls);
    public void SetDenialCode(string code) => Volatile.Write(ref denialCode, code);

    public Task<PublicFreeTierReservationResult> TryReserveBeforeProviderCallAsync(PublicFreeTierAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref reserveCalls);
        var code = Volatile.Read(ref denialCode);
        var decision = request.EmailIsConfirmed
            ? PublicFreeTierAdmissionDecision.Deny(429, code, "Synthetic exhausted-budget sentinel: deny before provider I/O.")
            : PublicFreeTierAdmissionDecision.Deny(403, "PUBLIC_FREE_TIER_EMAIL_CONFIRMATION_REQUIRED", "Confirm your email before using free AI.");
        return Task.FromResult(new PublicFreeTierReservationResult(false, null, false, decision, PublicFreeTierReservationState.None));
    }

    public Task<PublicFreeTierDispatchAuthorizationResult> TryAuthorizeProviderDispatchAsync(Guid reservationId, string dispatchIdempotencyKey, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref dispatchAuthorizationCalls);
        return Task.FromResult(new PublicFreeTierDispatchAuthorizationResult(false, false, PublicFreeTierReservationState.None, "SYNTHETIC_DENY"));
    }

    public Task<PublicFreeTierSettlementResult> RecordProviderOutcomeAsync(Guid reservationId, string completionIdempotencyKey, PublicFreeTierProviderOutcome outcome, bool actualCostKnown, decimal? actualProviderCostUsd, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublicFreeTierSettlementResult(false, false, true, "SYNTHETIC_DENY"));

    public Task<PublicFreeTierSettlementResult> ReleaseUndispatchedAsync(Guid reservationId, string completionIdempotencyKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublicFreeTierSettlementResult(false, false, true, "SYNTHETIC_DENY"));
}

internal sealed class NoEgressHttpClientFactory(ConcurrentBag<string> attempts) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new BlockAllOutboundHandler(attempts), disposeHandler: true);
}

internal sealed class BlockAllOutboundHandler(ConcurrentBag<string> attempts) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        attempts.Add($"{request.Method} {request.RequestUri}");
        throw new InvalidOperationException("Outbound HTTP is forbidden in the synthetic acceptance harness.");
    }
}

internal sealed class CountingChatCompletionService(ProviderDispatchCounters calls) : IChatCompletionService
{
    private static readonly AiUsageMetadata Usage = new("synthetic-chat", "synthetic-chat-v1", 12, null, 8, 0.001m, 0.001m, 1, "completed", false, PricingVersion: "synthetic-v1", Currency: "USD", CostBasis: UsageCostBasis.Estimated);

    public async Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        calls.Record("Chat");
        await Task.Delay(35, cancellationToken);
        return new AiGenerationResult("Synthetic provider-free response.", Usage);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        calls.Record("Chat");
        await Task.Delay(35, cancellationToken);
        yield return new AiMessageDelta("Synthetic provider-free response.");
        yield return new AiMessageCompleted(Usage);
    }
}

internal sealed class CountingImageProvider(ProviderDispatchCounters calls) : IImageGenerationProvider, IImageGenerationProviderReadiness
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    public string Key => "synthetic-image";
    public bool IsAvailable => true;
    public Task<ImageProviderResult> GenerateAsync(ImageGenerationInput request, ImagePromptBuildResult prompt, CancellationToken cancellationToken = default)
    {
        calls.Record("Image");
        return Task.FromResult(new ImageProviderResult(Png, "image/png", "png", 1, 1,
            new ImageProviderUsage(12, 12, null, 8, 0.001m, CostBasis: UsageCostBasis.Estimated)));
    }
}

internal sealed class CountingDocumentProvider(ProviderDispatchCounters calls) : IDocumentGenerationProvider
{
    private readonly DeterministicDocumentProvider inner = new();
    public Task<DocumentProviderResult> GenerateAsync(DocumentGenerationPrompt prompt, DocumentGenerationOptions options, CancellationToken cancellationToken = default)
    {
        calls.Record("Document");
        return inner.GenerateAsync(prompt, options, cancellationToken);
    }
}

internal sealed class CountingPresentationProvider(ProviderDispatchCounters calls) : IPresentationGenerationProvider
{
    private readonly DeterministicPresentationProvider inner = new();
    public Task<PresentationProviderResult> GenerateAsync(PresentationGenerationPrompt prompt, PresentationGenerationOptions options, CancellationToken cancellationToken = default)
    {
        calls.Record("Presentation");
        return inner.GenerateAsync(prompt, options, cancellationToken);
    }
}

internal sealed class CountingMovieVideoProvider : IMovieVideoProvider
{
    private readonly ProviderDispatchCounters calls;
    private readonly FakeMovieVideoProvider inner;
    public CountingMovieVideoProvider(ProviderDispatchCounters calls, FakeProviderScenarioCatalog scenarios, FakeProviderCallLog log)
    {
        this.calls = calls;
        inner = new FakeMovieVideoProvider(scenarios, log);
    }
    public string Key => inner.Key;
    public bool IsAvailable => inner.IsAvailable;
    public IReadOnlyCollection<string> SupportedOperations => inner.SupportedOperations;
    public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
    {
        calls.Record("Movie");
        return await inner.SubmitAsync(request, cancellationToken);
    }
    public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) => inner.GetStatusAsync(providerJobId, cancellationToken);
    public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) => inner.RetrieveAsync(providerJobId, status, cancellationToken);
    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => inner.CancelAsync(providerJobId, cancellationToken);
}

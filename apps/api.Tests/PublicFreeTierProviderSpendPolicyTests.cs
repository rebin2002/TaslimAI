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

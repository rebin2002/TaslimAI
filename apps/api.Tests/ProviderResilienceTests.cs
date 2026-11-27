using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Resilience;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ProviderResilienceTests
{
    private static readonly Guid JobId = Guid.NewGuid();
    private static readonly Guid Token = Guid.NewGuid();

    [Fact]
    public async Task Success_records_one_attempt_and_finalizes_once()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("primary", _ => Task.FromResult<string>("ok"));
        var result = await Create(store).ExecuteAsync(JobId, Token, "image.generate", "request-1", "input", [provider]);

        Assert.True(result.Succeeded);
        Assert.Equal(1, provider.Calls);
        Assert.Single(store.Attempts);
        Assert.Equal(ProviderAttemptResultCategory.Success, store.Attempts[0].Category);
        Assert.Equal("completed", store.FinalizationState);
    }

    [Fact]
    public async Task Transient_failure_is_retried_with_bounded_attempts()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("primary", call => call < 3
            ? throw new ProviderResilienceException(ProviderAttemptResultCategory.TransientFailure, "TEMPORARY")
            : Task.FromResult<string>("ok"));
        var result = await Create(store, retries: 2).ExecuteAsync(JobId, Token, "image.generate", "request-2", "input", [provider]);

        Assert.True(result.Succeeded);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, store.Attempts.Count);
        Assert.Equal(2, store.Attempts.Count(item => item.IsRetry));
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("primary", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.PermanentFailure, "PERMANENT"));
        var result = await Create(store, retries: 4).ExecuteAsync(JobId, Token, "music.generate", "request-3", "input", [provider]);

        Assert.Equal(ProviderAttemptResultCategory.PermanentFailure, result.Category);
        Assert.Equal(1, provider.Calls);
        Assert.Single(store.Attempts);
    }

    [Fact]
    public async Task Rate_limit_and_timeout_are_retryable_but_invalid_input_and_cancellation_are_not()
    {
        var store = new FakeResilienceStore();
        var rateLimited = new TestProvider("rate", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.RateLimited, "RATE_LIMITED"));
        var rateResult = await Create(store, retries: 2).ExecuteAsync(JobId, Token, "voice.generate", "request-4", "input", [rateLimited]);
        Assert.Equal(3, rateLimited.Calls);
        Assert.Equal(ProviderAttemptResultCategory.RateLimited, rateResult.Category);

        var timeout = new TestProvider("timeout", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.TimedOut, "TIMEOUT"));
        var timeoutResult = await Create(store, retries: 2).ExecuteAsync(JobId, Token, "voice.generate", "request-5", "input", [timeout]);
        Assert.Equal(3, timeout.Calls);
        Assert.Equal(ProviderAttemptResultCategory.TimedOut, timeoutResult.Category);

        var invalid = new TestProvider("invalid", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.InvalidUserInput, "INVALID"));
        var invalidResult = await Create(store, retries: 2).ExecuteAsync(JobId, Token, "voice.generate", "request-6", "input", [invalid]);
        Assert.Equal(1, invalid.Calls);
        Assert.Equal(ProviderAttemptResultCategory.InvalidUserInput, invalidResult.Category);
    }

    [Fact]
    public async Task Network_exceptions_are_classified_as_transient_and_retried_with_the_shared_policy()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("network", call => call < 2
            ? throw new HttpRequestException("private network detail")
            : Task.FromResult("ok"));

        var result = await Create(store, retries: 1).ExecuteAsync(JobId, Token, "image.generate", "request-network", "input", [provider]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(ProviderAttemptResultCategory.TransientFailure, store.Attempts[0].Category);
        Assert.Equal("PROVIDER_NETWORK_FAILURE", store.Attempts[0].ErrorCode);
    }

    [Fact]
    public async Task Validation_failure_is_not_fallback_eligible()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider("primary", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.ValidationRejected, "VALIDATION"));
        var fallback = new TestProvider("fallback", _ => Task.FromResult("must-not-run"));

        var result = await Create(store).ExecuteAsync(JobId, Token, "movie.generate", "request-validation", "input", [primary, fallback]);

        Assert.Equal(ProviderAttemptResultCategory.ValidationRejected, result.Category);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Routing_skips_providers_that_miss_quality_cost_or_idempotency_requirements()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider(
            "primary",
            _ => Task.FromResult("must-not-run"),
            new ProviderRoutingProfile(QualityRank: 5, EstimatedCostUsd: 10m, SupportsIdempotency: false));
        var fallback = new TestProvider(
            "fallback",
            _ => Task.FromResult("safe-result"),
            new ProviderRoutingProfile(QualityRank: 2, EstimatedCostUsd: 1m, SupportsIdempotency: true));

        var result = await Create(store).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("movie.generate", MinimumQualityRank: 1, MaxEstimatedCostUsd: 2m),
            "request-route",
            "input",
            [primary, fallback]);

        Assert.True(result.Succeeded);
        Assert.Equal("fallback", result.ProviderKey);
        Assert.Equal(0, primary.Calls);
        Assert.Equal(1, fallback.Calls);
        Assert.False(store.Attempts.Single(item => item.ProviderKey == "fallback").IsFallback);
    }

    [Fact]
    public async Task Routing_does_not_use_a_fallback_when_the_route_disallows_it_after_policy_rejection()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider(
            "primary",
            _ => Task.FromResult("must-not-run"),
            new ProviderRoutingProfile(QualityRank: 1, EstimatedCostUsd: 1m, SupportsIdempotency: false));
        var fallback = new TestProvider("fallback", _ => Task.FromResult("must-not-run"));

        var result = await Create(store).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("image.generate", RequireIdempotency: true, AllowFallback: false),
            "request-no-fallback-policy",
            "input",
            [primary, fallback]);

        Assert.Equal(ProviderAttemptResultCategory.UnsupportedCapability, result.Category);
        Assert.Equal("IDEMPOTENCY_REQUIRED", result.ErrorCode);
        Assert.Equal(0, primary.Calls);
        Assert.Equal(0, fallback.Calls);
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task Routing_does_not_use_a_fallback_when_the_route_disallows_it_after_capability_rejection()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider("primary", _ => Task.FromResult("must-not-run"), canHandle: false);
        var fallback = new TestProvider("fallback", _ => Task.FromResult("must-not-run"));

        var result = await Create(store).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("image.generate", AllowFallback: false),
            "request-no-fallback-capability",
            "input",
            [primary, fallback]);

        Assert.Equal(ProviderAttemptResultCategory.UnsupportedCapability, result.Category);
        Assert.Equal("CAPABILITY_UNSUPPORTED", result.ErrorCode);
        Assert.Equal(0, primary.Calls);
        Assert.Equal(0, fallback.Calls);
        Assert.Empty(store.Attempts);
    }

    [Fact]
    public async Task Cost_limit_rejection_does_not_call_an_expensive_provider()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider(
            "expensive",
            _ => Task.FromResult("must-not-run"),
            new ProviderRoutingProfile(QualityRank: 2, EstimatedCostUsd: 6m));

        var result = await Create(store).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("image.generate", MaxEstimatedCostUsd: 2m),
            "request-cost",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("COST_LIMIT_EXCEEDED", result.ErrorCode);
    }

    [Fact]
    public void Negative_configured_cost_ceiling_rejects_estimated_provider_costs()
    {
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m);
        var requirements = new ProviderRouteRequirements("image.generate");

        Assert.False(ProviderRoutingPolicy.MeetsRequirements(profile, requirements, -1m));
    }

    [Fact]
    public async Task Negative_configured_cost_ceiling_fails_closed_before_an_unknown_cost_provider_runs()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("unknown-cost", _ => Task.FromResult("must-not-run"));

        var result = await Create(store, maxCostUsd: -1m).ExecuteAsync(
            JobId,
            Token,
            "image.generate",
            "request-invalid-cost-limit",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("INVALID_COST_LIMIT", result.ErrorCode);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("completed", store.FinalizationState);
    }

    [Fact]
    public async Task Cumulative_estimate_overflow_rejects_before_any_provider_call()
    {
        var store = new FakeResilienceStore { CumulativeEstimatedCostUsd = decimal.MaxValue };
        var provider = new TestProvider("overflow-estimate",
            _ => Task.FromResult("must-not-run"),
            new ProviderRoutingProfile(EstimatedCostUsd: 1m));

        var result = await Create(store, maxCostUsd: decimal.MaxValue).ExecuteAsync(
            JobId, Token, "image.generate", "request-overflow-preflight", "input", [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("COST_LIMIT_EXCEEDED", result.ErrorCode);
        Assert.Equal(0, provider.Calls);
        Assert.Empty(store.Attempts);
        Assert.Equal("completed", store.FinalizationState);
    }

    [Fact]
    public async Task Cumulative_reported_cost_overflow_rejects_completed_provider_output()
    {
        var store = new FakeResilienceStore { CumulativeEstimatedCostUsd = decimal.MaxValue };
        var provider = new TestProvider("overflow-reported",
            _ => Task.FromResult("must-not-accept"), reportedCostUsd: 1m);

        var result = await Create(store, maxCostUsd: decimal.MaxValue).ExecuteAsync(
            JobId, Token, "image.generate", "request-overflow-postflight", "input", [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("COST_LIMIT_EXCEEDED", result.ErrorCode);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, Assert.Single(store.Attempts).Category);
        Assert.Equal("completed", store.FinalizationState);
    }

    [Fact]
    public async Task Provider_reported_cost_is_checked_before_accepting_the_result()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("reported-cost", _ => Task.FromResult("must-not-accept"), reportedCostUsd: 6m);

        var result = await Create(store).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("image.generate", MaxEstimatedCostUsd: 2m),
            "request-reported-cost",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, Assert.Single(store.Attempts).Category);
    }

    [Fact]
    public async Task Circuit_opens_after_threshold_and_recovers_with_a_single_probe()
    {
        var store = new FakeResilienceStore { FailureThreshold = 2 };
        var provider = new TestProvider("primary", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.TransientFailure));
        var first = await Create(store, retries: 0).ExecuteAsync(Guid.NewGuid(), Token, "image.generate", "circuit-1", "input", [provider]);
        var second = await Create(store, retries: 0).ExecuteAsync(Guid.NewGuid(), Token, "image.generate", "circuit-2", "input", [provider]);
        var third = await Create(store, retries: 0).ExecuteAsync(Guid.NewGuid(), Token, "image.generate", "circuit-3", "input", [provider]);

        Assert.Equal(ProviderAttemptResultCategory.TransientFailure, first.Category);
        Assert.Equal(ProviderAttemptResultCategory.TransientFailure, second.Category);
        Assert.Equal(ProviderAttemptResultCategory.CircuitOpen, third.Category);
        Assert.Equal(2, provider.Calls);
        Assert.Contains(store.Attempts, item => item.Category == ProviderAttemptResultCategory.CircuitOpen);

        store.AdvanceCircuitWindow();
        var recovering = new TestProvider("primary", _ => Task.FromResult<string>("recovered"));
        var recovery = await Create(store, retries: 0).ExecuteAsync(Guid.NewGuid(), Token, "image.generate", "circuit-4", "input", [recovering]);
        Assert.True(recovery.Succeeded);
        Assert.Equal(1, recovering.Calls);
        Assert.Equal(ProviderCircuitState.Closed, (await store.GetCircuitAsync("primary", "image.generate"))!.State);
    }

    [Fact]
    public async Task Capability_aware_fallback_succeeds_without_exposing_provider_details()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider("primary", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.Unavailable, "DOWN"));
        var fallback = new TestProvider("fallback", _ => Task.FromResult<string>("fallback-result"));
        var result = await Create(store, retries: 0).ExecuteAsync(JobId, Token, "movie.generate", "fallback-1", "input", [primary, fallback]);

        Assert.True(result.Succeeded);
        Assert.Equal("fallback", result.ProviderKey);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
        Assert.True(store.Attempts.Single(item => item.ProviderKey == "fallback").IsFallback);
    }

    [Fact]
    public async Task All_providers_unavailable_returns_a_generic_internal_result()
    {
        var store = new FakeResilienceStore();
        var providers = new[]
        {
            new TestProvider("one", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.Unavailable)),
            new TestProvider("two", _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.CircuitOpen)),
        };
        var result = await Create(store, retries: 0).ExecuteAsync(JobId, Token, "movie.generate", "fallback-2", "input", providers);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("one", result.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("two", result.ErrorCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_during_fallback_stops_without_calling_the_next_provider()
    {
        var store = new FakeResilienceStore();
        var primary = new TestProvider("primary", _ =>
        {
            store.CancellationRequested = true;
            return Task.FromException<string>(new ProviderResilienceException(ProviderAttemptResultCategory.Unavailable));
        });
        var fallback = new TestProvider("fallback", _ => Task.FromResult<string>("must-not-run"));
        var result = await Create(store, retries: 0).ExecuteAsync(JobId, Token, "image.generate", "cancel-1", "input", [primary, fallback]);

        Assert.Equal(ProviderAttemptResultCategory.Cancelled, result.Category);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Stale_worker_is_fenced_before_provider_attempt()
    {
        var store = new FakeResilienceStore { StaleWorker = true };
        var provider = new TestProvider("primary", _ => Task.FromResult<string>("must-not-run"));

        await Assert.ThrowsAsync<StaleProviderWorkerException>(() => Create(store).ExecuteAsync(JobId, Token, "image.generate", "stale-1", "input", [provider]));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Completed_replay_is_idempotent_and_does_not_call_provider_again()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider("primary", _ => Task.FromResult<string>("ok"));
        var first = await Create(store).ExecuteAsync(JobId, Token, "image.generate", "replay-1", "input", [provider]);
        var replay = await Create(store).ExecuteAsync(JobId, Token, "image.generate", "replay-1", "input", [provider]);

        Assert.True(first.Succeeded);
        Assert.True(replay.IsReplay);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, store.FinalizationCompletions);
    }

    [Fact]
    public async Task Concurrent_duplicate_executions_allow_only_one_provider_attempt_and_finalization()
    {
        var store = new FakeResilienceStore();
        store.EnableConcurrentDuplicateClaimBarrier();
        var provider = new TestProvider("primary", _ => Task.FromResult<string>("ok"));

        var executions = new[]
        {
            Create(store).ExecuteAsync(JobId, Token, "image.generate", "concurrent-duplicate", "input", [provider]),
            Create(store).ExecuteAsync(JobId, Token, "image.generate", "concurrent-duplicate", "input", [provider]),
        };
        var results = await Task.WhenAll(executions);

        Assert.Equal(1, provider.Calls);
        Assert.Single(store.Attempts);
        Assert.Equal(1, store.FinalizationCompletions);
        Assert.Contains(results, result => result.Succeeded);
        Assert.Contains(results, result => result.IsReplay || result.ErrorCode == "EXECUTION_ALREADY_CLAIMED");
    }

    // The routing gate only refuses a provider that cannot de-duplicate when the caller
    // opts into RequireIdempotency. When a route deliberately admits such a provider, the
    // retry wrapper is the last place able to stop the same paid submission being sent
    // twice, so it must fail closed on any outcome that does not prove the provider
    // rejected the request before accepting it.
    [Fact]
    public async Task Ambiguous_timeout_is_never_resubmitted_to_a_provider_without_idempotency_support()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider(
            "non-idempotent",
            _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.TimedOut, "PROVIDER_TIMEOUT"),
            new ProviderRoutingProfile(SupportsIdempotency: false));

        var result = await Create(store, retries: 2).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("movie.generate", RequireIdempotency: false),
            "resubmit-timeout",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.TimedOut, result.Category);
        Assert.Equal("PROVIDER_TIMEOUT", result.ErrorCode);
        Assert.Equal(1, provider.Calls);
        Assert.Single(store.Attempts);
        Assert.Equal(1, result.AttemptCount);
    }

    [Fact]
    public async Task Ambiguous_network_failure_is_never_resubmitted_to_a_provider_without_idempotency_support()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider(
            "non-idempotent",
            _ => throw new HttpRequestException("connection reset after the request was written"),
            new ProviderRoutingProfile(SupportsIdempotency: false));

        var result = await Create(store, retries: 2).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("movie.generate", RequireIdempotency: false),
            "resubmit-network",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.TransientFailure, result.Category);
        Assert.Equal("PROVIDER_NETWORK_FAILURE", result.ErrorCode);
        Assert.Equal(1, provider.Calls);
        Assert.Single(store.Attempts);
    }

    [Fact]
    public async Task Definitive_rate_limit_rejection_is_still_retried_for_a_provider_without_idempotency_support()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider(
            "non-idempotent",
            _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.RateLimited, "RATE_LIMITED"),
            new ProviderRoutingProfile(SupportsIdempotency: false));

        var result = await Create(store, retries: 2).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("movie.generate", RequireIdempotency: false),
            "resubmit-rate-limit",
            "input",
            [provider]);

        // A rate limit proves the provider refused the request before accepting it,
        // so re-sending cannot duplicate work the provider has already taken on.
        Assert.Equal(ProviderAttemptResultCategory.RateLimited, result.Category);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, store.Attempts.Count);
    }

    [Fact]
    public async Task Ambiguous_timeout_keeps_being_retried_when_the_provider_can_deduplicate_the_idempotency_key()
    {
        var store = new FakeResilienceStore();
        var provider = new TestProvider(
            "idempotent",
            _ => throw new ProviderResilienceException(ProviderAttemptResultCategory.TimedOut, "PROVIDER_TIMEOUT"),
            new ProviderRoutingProfile(SupportsIdempotency: true));

        var result = await Create(store, retries: 2).ExecuteAsync(
            JobId,
            Token,
            new ProviderRouteRequirements("movie.generate", RequireIdempotency: false),
            "resubmit-idempotent",
            "input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.TimedOut, result.Category);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(3, store.Attempts.Count);
    }

    [Theory]
    [InlineData(false, 2, true, true)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 2, true, true)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 2, false, false)]
    public async Task Uncertain_primary_submission_uses_a_distinct_fallback_only_within_the_job_budget(
        bool primaryIsIdempotent, int ceiling, bool allowFallback, bool expectFallback)
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var charging = new CountingTestChargingService();
        var ledger = ActivatorUtilities.CreateInstance<UsageLedgerService>(job.Services, charging);
        var transaction = await ledger.GetOrCreatePendingAsync(job.WorkspaceId, job.UserId, null, null, job.Key, UsageFeature.Generation, generationJobId: job.Id);
        var primary = new TestProvider("primary", _ => throw new TimeoutException(),
            new ProviderRoutingProfile(EstimatedCostUsd: 1m, SupportsIdempotency: primaryIsIdempotent));
        var alias = new TestProvider("PRIMARY", _ => Task.FromResult("duplicate"),
            new ProviderRoutingProfile(EstimatedCostUsd: 1m, SupportsIdempotency: primaryIsIdempotent));
        var secondary = new TestProvider("secondary", _ => Task.FromResult("output"), new ProviderRoutingProfile(EstimatedCostUsd: 1m));
        IResilientProvider<string, string>[] providers = [primary, alias, secondary];
        var result = await job.ExecuteAsync(providers, ceiling, allowFallback);

        var expectedPrimaryCalls = primaryIsIdempotent ? 3 : 1;
        Assert.Equal(expectedPrimaryCalls, primary.Calls);
        Assert.Equal(0, alias.Calls);
        Assert.Equal(expectFallback ? 1 : 0, secondary.Calls);
        Assert.Equal(expectFallback, result.Succeeded);
        var attempts = await job.AttemptsAsync();
        Assert.Equal(expectedPrimaryCalls + (expectFallback ? 1 : 0), attempts.Count);
        var primaryAttempts = attempts.Where(attempt => attempt.Provider == "primary").ToArray();
        Assert.All(primaryAttempts, attempt => Assert.Equal(ProviderAttemptResultCategory.TimedOut.ToString(), attempt.ResultClassification));
        Assert.Equal(1m, Assert.Single(primaryAttempts, attempt => attempt.EstimatedProviderCostUsd > 0m).EstimatedProviderCostUsd);
        Assert.Equal(expectedPrimaryCalls - 1, primaryAttempts.Count(attempt => attempt.IsRetry));
        if (expectFallback)
        {
            Assert.Equal("secondary", result.ProviderKey);
            Assert.True(attempts[^1].IsFallback);
            Assert.Equal(1m, attempts[^1].EstimatedProviderCostUsd);
        }
        else
        {
            Assert.Equal(allowFallback ? ProviderAttemptResultCategory.CostGuardRejected : ProviderAttemptResultCategory.TimedOut, result.Category);
        }

        var usage = new AiUsageMetadata("secondary", "fixture", null, null, null, 1m, null, 1, "completed", true);
        if (result.Succeeded) await ledger.CompleteAsync(transaction, usage);
        else await ledger.FailAsync(transaction, result.ErrorCode!);

        var replay = await job.ExecuteAsync(providers, ceiling, allowFallback);
        Assert.True(replay.IsReplay);
        Assert.Equal(result.Category, replay.Category);
        Assert.Equal(expectedPrimaryCalls, primary.Calls);
        Assert.Equal(expectFallback ? 1 : 0, secondary.Calls);
        var replayedTransaction = await ledger.GetOrCreatePendingAsync(job.WorkspaceId, job.UserId, null, null, job.Key, UsageFeature.Generation, generationJobId: job.Id);
        Assert.Equal(transaction.Id, replayedTransaction.Id);
        if (replay.Succeeded) await ledger.CompleteAsync(replayedTransaction, usage);
        Assert.Equal(expectFallback ? 1 : 0, charging.Calculations);
        Assert.Single(await job.Db.ProviderExecutionFinalizations.AsNoTracking().Where(f => f.GenerationJobId == job.Id).ToListAsync());
        var finalTransaction = Assert.Single(await job.Db.UsageTransactions.AsNoTracking().Where(t => t.GenerationJobId == job.Id).ToListAsync());
        Assert.Equal(expectFallback ? 0.25m : 0m, finalTransaction.ChargedAmount);
    }

    [Theory]
    [InlineData(null, 1, 2, 1)]
    [InlineData(1, null, 2, 1)]
    [InlineData(null, null, 0, 0)]
    public async Task Unpriced_exposure_cannot_authorize_an_additional_vendor(
        int? primaryEstimate, int? secondaryEstimate, int ceiling, int primaryCalls)
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var primary = new TestProvider("primary", _ => throw new TimeoutException(),
            new ProviderRoutingProfile(EstimatedCostUsd: primaryEstimate, SupportsIdempotency: false));
        var secondary = new TestProvider("secondary", _ => Task.FromResult("unexpected"),
            new ProviderRoutingProfile(EstimatedCostUsd: secondaryEstimate));
        var result = await job.ExecuteAsync([primary, secondary], ceiling);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("COST_ESTIMATE_REQUIRED", result.ErrorCode);
        Assert.Equal(primaryCalls, primary.Calls);
        Assert.Equal(0, secondary.Calls);
    }

    [Fact]
    public async Task An_uncertain_primary_and_one_contacted_fallback_cannot_trigger_a_third_dispatch()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m, SupportsIdempotency: false);
        var primary = new TestProvider("primary", _ => throw new TimeoutException(), profile);
        var secondary = new TestProvider("secondary", _ => throw new TimeoutException(), profile);
        var third = new TestProvider("third", _ => Task.FromResult("unexpected"), profile);
        var result = await job.ExecuteAsync([primary, secondary, third], 3);

        Assert.Equal(ProviderAttemptResultCategory.TimedOut, result.Category);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
        Assert.Equal(0, third.Calls);
        Assert.Equal(2m, (await job.AttemptsAsync()).Sum(attempt => attempt.EstimatedProviderCostUsd));
    }

    [Fact]
    public async Task A_rejected_candidate_does_not_consume_the_one_remaining_dispatch()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m, SupportsIdempotency: false);
        var primary = new TestProvider("primary", _ => throw new TimeoutException(), profile);
        var overpriced = new TestProvider("overpriced", _ => Task.FromResult("unexpected"),
            new ProviderRoutingProfile(EstimatedCostUsd: 2m));
        var fallback = new TestProvider("fallback", _ => Task.FromResult("accepted"), profile);

        Assert.True((await job.ExecuteAsync([primary, overpriced, fallback], 2)).Succeeded);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, overpriced.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task A_later_rejection_does_not_remove_an_earlier_uncertain_submission()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m);
        var primary = new TestProvider("primary", call => call == 1 ? throw new TimeoutException()
            : throw new ProviderResilienceException(ProviderAttemptResultCategory.Unavailable), profile);
        var fallback = new TestProvider("fallback", _ => Task.FromResult("unexpected"), profile);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, (await job.ExecuteAsync([primary, fallback], 1)).Category);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(1m, (await job.AttemptsAsync()).Sum(attempt => attempt.EstimatedProviderCostUsd));
    }

    [Fact]
    public async Task An_idempotent_same_vendor_success_replaces_the_uncertain_estimate_with_its_reported_cost()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m);
        var provider = new TestProvider("primary", call => call == 1 ? throw new TimeoutException() : Task.FromResult("accepted"),
            profile, reportedCostUsd: 0.75m);
        var result = await job.ExecuteAsync([provider], 1);

        Assert.True(result.Succeeded);
        Assert.Equal(2, provider.Calls);
        var attempts = await job.AttemptsAsync();
        Assert.Equal(0m, attempts[0].EstimatedProviderCostUsd);
        Assert.Equal(0.75m, attempts[1].EstimatedProviderCostUsd);
        var store = job.Services.GetRequiredService<IProviderResilienceStore>();
        Assert.Equal(0.75m, await store.GetCumulativeEstimatedCostAsync(job.Id));
    }

    [Fact]
    public async Task A_confirmed_success_above_the_estimate_never_dispatches_another_provider()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var profile = new ProviderRoutingProfile(EstimatedCostUsd: 1m);
        var primary = new TestProvider("primary", _ => Task.FromResult("accepted"), profile, reportedCostUsd: 3m);
        var fallback = new TestProvider("fallback", _ => Task.FromResult("unexpected"), profile);
        var result = await job.ExecuteAsync([primary, fallback], 2);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(3m, Assert.Single(await job.AttemptsAsync()).EstimatedProviderCostUsd);
    }

    [Fact]
    public async Task The_cumulative_ceiling_preserves_decimal_precision_and_ignores_legacy_circuit_estimates()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var store = job.Services.GetRequiredService<IProviderResilienceStore>();
        var now = DateTime.UtcNow;
        var skipped = await store.StartAttemptAsync(job.Id, job.Token, "old", "movie.generate", "offline", 1, false, false, now);
        await store.CompleteAttemptAsync(skipped, ProviderAttemptResultCategory.CircuitOpen, "CIRCUIT_OPEN", 0, 100m, now);
        const decimal priorCost = 1000000000.00000001m;
        var prior = await store.StartAttemptAsync(job.Id, job.Token, "old", "movie.generate", "prior", 2, false, true, now);
        await store.CompleteAttemptAsync(prior, ProviderAttemptResultCategory.Success, null, 0, priorCost, now);
        Assert.Equal(priorCost, await store.GetCumulativeEstimatedCostAsync(job.Id));

        const decimal ceiling = 1000000000.01m;
        var router = new ProviderResilienceOrchestrator(store, new AllowAllProviderCostGuard(),
            Options.Create(new ProviderResilienceOptions { MaxEstimatedCostUsd = ceiling }), TimeProvider.System, NullLogger<ProviderResilienceOrchestrator>.Instance);
        var candidate = new TestProvider("next", _ => Task.FromResult("unexpected"), new ProviderRoutingProfile(EstimatedCostUsd: 0.01m));
        var result = await router.ExecuteAsync(job.Id, job.Token,
            new ProviderRouteRequirements("movie.generate", MaxEstimatedCostUsd: ceiling), job.Key, "synthetic", [candidate]);
        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal(0, candidate.Calls);
    }

    [Fact]
    public async Task An_unpriced_started_attempt_on_the_same_job_blocks_a_new_execution_key()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var store = job.Services.GetRequiredService<IProviderResilienceStore>();
        await store.StartAttemptAsync(job.Id, job.Token, "prior-stage", "movie.generate", "previous", 1, false, false, DateTime.UtcNow);
        var candidate = new TestProvider("next", _ => Task.FromResult("unexpected"), new ProviderRoutingProfile(EstimatedCostUsd: 1m));

        var result = await job.ExecuteAsync([candidate], 2);
        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal(0, candidate.Calls);
    }

    [Theory]
    [InlineData(ProviderAttemptResultCategory.CircuitOpen)]
    [InlineData(ProviderAttemptResultCategory.RateLimited)]
    public async Task Recovery_continues_after_a_persisted_rejection_that_proves_no_vendor_accepted(ProviderAttemptResultCategory classified)
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var store = job.Services.GetRequiredService<IProviderResilienceStore>();
        var past = DateTime.UtcNow.AddHours(-1);
        await store.TryClaimFinalizationAsync(job.Id, job.Token, job.Key, past, TimeSpan.FromMinutes(5));
        var rejected = await store.StartAttemptAsync(job.Id, job.Token, job.Key, "movie.generate", "primary", 1, false, false, past);
        await store.CompleteAttemptAsync(rejected, classified, classified.ToString(), 0, null, past);
        var primary = new TestProvider("Primary", _ => Task.FromResult("unexpected"));
        var next = new TestProvider("next", _ => Task.FromResult("accepted"), new ProviderRoutingProfile(EstimatedCostUsd: 1m));

        using var newWorker = job.Services.CreateScope();
        var orchestrator = newWorker.ServiceProvider.GetRequiredService<IProviderResilienceOrchestrator>();
        var result = await orchestrator.ExecuteAsync(job.Id, job.Token, "movie.generate", job.Key, "synthetic", [primary, next]);

        Assert.True(result.Succeeded);
        Assert.Equal(0, primary.Calls);
        Assert.Equal(1, next.Calls);
        var attempts = await job.AttemptsAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(2, attempts[1].AttemptNumber);
        Assert.True(attempts[1].IsFallback);
    }

    [Fact]
    public async Task An_expired_worker_with_a_started_submission_requires_reconciliation_before_redispatch()
    {
        using var job = new ResilienceJob();
        await job.SeedAsync();
        var store = job.Services.GetRequiredService<IProviderResilienceStore>();
        var past = DateTime.UtcNow.AddHours(-1);
        await store.TryClaimFinalizationAsync(job.Id, job.Token, job.Key, past, TimeSpan.FromMinutes(5));
        await store.StartAttemptAsync(job.Id, job.Token, job.Key, "movie.generate", "primary", 1, false, false, past, reservedCostUsd: 1m);
        var provider = new TestProvider("primary", _ => Task.FromResult("duplicate"), new ProviderRoutingProfile(EstimatedCostUsd: 1m, SupportsIdempotency: false));

        using var nextWorker = job.Services.CreateScope();
        var orchestrator = nextWorker.ServiceProvider.GetRequiredService<IProviderResilienceOrchestrator>();
        var result = await orchestrator.ExecuteAsync(job.Id, job.Token,
            new ProviderRouteRequirements("movie.generate", MaxEstimatedCostUsd: 2m, RequireIdempotency: false),
            job.Key, "synthetic", [provider]);
        Assert.Equal("PROVIDER_RECONCILIATION_REQUIRED", result.ErrorCode);
        Assert.False(result.Succeeded);
        Assert.Equal(0, provider.Calls);
        var attempt = Assert.Single(await job.AttemptsAsync());
        Assert.Equal(GenerationProviderAttemptStatus.Started, attempt.Status);
        Assert.Equal(1m, attempt.EstimatedProviderCostUsd);
    }

    private sealed class CountingTestChargingService : IUsageChargingService
    {
        public int Calculations { get; private set; }

        public decimal CalculateCustomerCharge(UsageTransaction transaction)
        {
            Calculations++;
            return 0.25m;
        }
    }

    private sealed class ResilienceJob : IDisposable
    {
        private readonly TaslimApiFactory factory = new();
        private readonly IServiceScope scope;

        public ResilienceJob() => scope = factory.Services.CreateScope();
        public IServiceProvider Services => scope.ServiceProvider;
        public TaslimDbContext Db => Services.GetRequiredService<TaslimDbContext>();
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Token { get; } = Guid.NewGuid();
        public string Key => $"generation:{Id}";

        public async Task SeedAsync()
        {
            await Db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow;
            Db.Users.Add(new ApplicationUser { Id = UserId, UserName = "resilience@example.test", CreatedAt = now, UpdatedAt = now });
            Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Fallback fixture", Slug = $"fixture-{Id:N}", CreatedAt = now, UpdatedAt = now });
            Db.GenerationJobs.Add(new GenerationJob
            {
                Id = Id, WorkspaceId = WorkspaceId, CreatedByUserId = UserId,
                JobType = GenerationJobTypes.SystemTest, Status = GenerationJobStatus.Running,
                ConcurrencyToken = Token, InputJson = "{}", CreatedAt = now,
            });
            await Db.SaveChangesAsync();
        }

        public Task<ProviderResilienceExecutionResult<string>> ExecuteAsync(
            IResilientProvider<string, string>[] providers, int ceiling, bool allowFallback = true)
        {
            var orchestrator = Services.GetRequiredService<IProviderResilienceOrchestrator>();
            var requirements = new ProviderRouteRequirements("movie.generate", MaxEstimatedCostUsd: ceiling, RequireIdempotency: false, AllowFallback: allowFallback);
            return orchestrator.ExecuteAsync(Id, Token, requirements, Key, "synthetic", providers);
        }

        public Task<List<GenerationProviderAttempt>> AttemptsAsync() =>
            Db.GenerationProviderAttempts.AsNoTracking().Where(a => a.GenerationJobId == Id).OrderBy(a => a.AttemptNumber).ToListAsync();

        public void Dispose()
        {
            scope.Dispose();
            factory.Dispose();
        }
    }

    private static ProviderResilienceOrchestrator Create(FakeResilienceStore store, int retries = 0, decimal maxCostUsd = 5m) =>
        new(store, new AllowAllProviderCostGuard(), Options.Create(new ProviderResilienceOptions
        {
            MaxProviders = 3,
            MaxRetriesPerProvider = retries,
            InitialBackoffMilliseconds = 0,
            MaxBackoffMilliseconds = 0,
            CircuitFailureThreshold = store.FailureThreshold,
            CircuitOpenSeconds = 30,
            CircuitProbeLeaseSeconds = 60,
            ProviderTimeoutSeconds = 1,
            MaxEstimatedCostUsd = maxCostUsd,
        }), store.Time, NullLogger<ProviderResilienceOrchestrator>.Instance);

    private sealed class TestProvider(string key, Func<int, Task<string>> behavior, ProviderRoutingProfile? routingProfile = null, decimal? reportedCostUsd = null, bool canHandle = true) : IResilientProvider<string, string>, IProviderRoutingProfile<string>
    {
        public string Key { get; } = key;
        public int Calls { get; private set; }
        private ProviderRoutingProfile RoutingProfile { get; } = routingProfile ?? ProviderRoutingProfile.Default;
        private decimal? ReportedCostUsd { get; } = reportedCostUsd;
        private bool CanHandleRequest { get; } = canHandle;
        public bool CanHandle(string capability, string input) => CanHandleRequest;
        public ProviderRoutingProfile GetRoutingProfile(string capability, string input) => RoutingProfile;
        public async Task<ProviderExecutionSuccess<string>> ExecuteAsync(ProviderExecutionContext context, string input, CancellationToken cancellationToken)
        {
            Calls++;
            return new ProviderExecutionSuccess<string>(await behavior(Calls), ReportedCostUsd);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public void Advance(TimeSpan amount) => now = now.Add(amount);
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeResilienceStore : IProviderResilienceStore
    {
        private readonly Dictionary<string, (ProviderCircuitState State, int Failures, DateTimeOffset OpenUntil)> circuits = new();
        private readonly Dictionary<string, ProviderFinalizationClaimResult> finalizations = new();
        private readonly Dictionary<string, ProviderFinalizationOutcome> completions = new();
        private readonly object finalizationLock = new();
        private TaskCompletionSource<bool>? duplicateClaimBarrier;
        private int duplicateClaimParticipants;
        public List<(string ProviderKey, bool IsRetry, bool IsFallback, ProviderAttemptResultCategory Category, string? ErrorCode)> Attempts { get; } = [];
        public ManualTimeProvider Time { get; } = new();
        public bool StaleWorker { get; set; }
        public bool CancellationRequested { get; set; }
        public int FailureThreshold { get; set; } = 3;
        public decimal CumulativeEstimatedCostUsd { get; set; }
        public int FinalizationCompletions { get; private set; }
        public string? FinalizationState { get; private set; }

        public void EnableConcurrentDuplicateClaimBarrier() => duplicateClaimBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProviderFinalizationClaimResult> TryClaimFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, DateTime now, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            var barrier = duplicateClaimBarrier;
            if (barrier is not null)
            {
                if (Interlocked.Increment(ref duplicateClaimParticipants) == 2) barrier.TrySetResult(true);
                await barrier.Task;
            }

            lock (finalizationLock)
            {
                if (StaleWorker) return ProviderFinalizationClaimResult.StaleWorker;
                if (finalizations.TryGetValue(idempotencyKey, out var existing)) return existing == ProviderFinalizationClaimResult.AlreadyCompleted ? existing : ProviderFinalizationClaimResult.AlreadyClaimed;
                finalizations[idempotencyKey] = ProviderFinalizationClaimResult.AlreadyClaimed;
                return ProviderFinalizationClaimResult.Claimed;
            }
        }
        public Task<ProviderFinalizationOutcome?> ReadCompletedOutcomeAsync(Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            lock (finalizationLock)
                return Task.FromResult(completions.GetValueOrDefault(idempotencyKey));
        }

        public Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default, string? errorCode = null)
        {
            if (StaleWorker) throw new StaleProviderWorkerException(generationJobId);
            lock (finalizationLock)
            {
                finalizations[idempotencyKey] = ProviderFinalizationClaimResult.AlreadyCompleted;
                completions.TryAdd(idempotencyKey, new(result, errorCode));
                FinalizationCompletions++;
                FinalizationState = "completed";
            }
            return Task.CompletedTask;
        }
        public Task<ProviderAttemptHistory> ReadAttemptHistoryAsync(Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default) => Task.FromResult(new ProviderAttemptHistory(0, new HashSet<string>(), null, false));
        public Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default, decimal? reservedCostUsd = null)
        {
            if (StaleWorker) throw new StaleProviderWorkerException(generationJobId);
            var attempt = new ProviderAttemptRecord(Guid.NewGuid(), generationJobId, jobConcurrencyToken, idempotencyKey, capability, providerKey, attemptNumber, isRetry, isFallback, startedAt);
            return Task.FromResult(attempt);
        }
        public Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default, Guid? replacedReservationId = null)
        {
            Attempts.Add((attempt.ProviderKey, attempt.IsRetry, attempt.IsFallback, result, errorCode));
            return Task.CompletedTask;
        }
        public Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default)
        {
            var key = providerKey + ":" + capability;
            if (!circuits.TryGetValue(key, out var circuit))
            {
                circuits[key] = (ProviderCircuitState.Closed, 0, DateTimeOffset.MinValue);
                return Task.FromResult(ProviderCircuitAdmission.Allowed);
            }
            if (circuit.State == ProviderCircuitState.Open && circuit.OpenUntil > Time.GetUtcNow()) return Task.FromResult(ProviderCircuitAdmission.Open);
            if (circuit.State == ProviderCircuitState.Open) { circuits[key] = (ProviderCircuitState.HalfOpen, circuit.Failures, DateTimeOffset.MinValue); return Task.FromResult(ProviderCircuitAdmission.Allowed); }
            return Task.FromResult(ProviderCircuitAdmission.Allowed);
        }
        public Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default)
        {
            circuits[providerKey + ":" + capability] = (ProviderCircuitState.Closed, 0, DateTimeOffset.MinValue);
            return Task.CompletedTask;
        }
        public Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default)
        {
            var key = providerKey + ":" + capability;
            circuits.TryGetValue(key, out var circuit);
            var failures = circuit.Failures + 1;
            circuits[key] = (failures >= failureThreshold ? ProviderCircuitState.Open : ProviderCircuitState.Closed, failures, Time.GetUtcNow().Add(openDuration));
            return Task.CompletedTask;
        }
        public Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default)
        {
            if (!circuits.TryGetValue(providerKey + ":" + capability, out var circuit)) return Task.FromResult<ProviderCircuitSnapshot?>(null);
            return Task.FromResult<ProviderCircuitSnapshot?>(new ProviderCircuitSnapshot(providerKey, capability, circuit.State, circuit.Failures, circuit.OpenUntil.UtcDateTime, null));
        }
        public Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default) => Task.FromResult(CancellationRequested);
        public Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default, Guid? excludeReservationId = null) => Task.FromResult(CumulativeEstimatedCostUsd);
        public void AdvanceCircuitWindow() => Time.Advance(TimeSpan.FromMinutes(1));
    }
}

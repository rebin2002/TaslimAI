using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Resilience;
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

    private static ProviderResilienceOrchestrator Create(FakeResilienceStore store, int retries = 0) =>
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
        private readonly object finalizationLock = new();
        private TaskCompletionSource<bool>? duplicateClaimBarrier;
        private int duplicateClaimParticipants;
        public List<(string ProviderKey, bool IsRetry, bool IsFallback, ProviderAttemptResultCategory Category, string? ErrorCode)> Attempts { get; } = [];
        public ManualTimeProvider Time { get; } = new();
        public bool StaleWorker { get; set; }
        public bool CancellationRequested { get; set; }
        public int FailureThreshold { get; set; } = 3;
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
        public Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default)
        {
            if (StaleWorker) throw new StaleProviderWorkerException(generationJobId);
            finalizations[idempotencyKey] = ProviderFinalizationClaimResult.AlreadyCompleted;
            FinalizationCompletions++;
            FinalizationState = "completed";
            return Task.CompletedTask;
        }
        public Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default)
        {
            if (StaleWorker) throw new StaleProviderWorkerException(generationJobId);
            var attempt = new ProviderAttemptRecord(Guid.NewGuid(), generationJobId, jobConcurrencyToken, idempotencyKey, capability, providerKey, attemptNumber, isRetry, isFallback, startedAt);
            return Task.FromResult(attempt);
        }
        public Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default)
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
        public Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default) => Task.FromResult(0m);
        public void AdvanceCircuitWindow() => Time.Advance(TimeSpan.FromMinutes(1));
    }
}

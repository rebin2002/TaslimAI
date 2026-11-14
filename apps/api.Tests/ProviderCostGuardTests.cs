using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Resilience;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free regressions for the cost-gate seam. The runtime-default test is
/// intentionally red until production DI replaces AllowAllProviderCostGuard with
/// an approved fail-closed implementation. The other tests prove a denying or
/// unavailable gate is evaluated before an attempt can invoke a provider.
/// </summary>
public sealed class ProviderCostGuardTests
{
    [Fact]
    public void Runtime_DI_must_not_register_the_allow_all_guard_for_paid_provider_dispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"taslim-cost-guard-di-{Guid.NewGuid():N}");
        using var factory = new NegativeRuntimeApiFactory(root);

        var guard = factory.Services.GetRequiredService<IProviderCostGuard>();

        Assert.False(guard is AllowAllProviderCostGuard,
            "Runtime DI still registers AllowAllProviderCostGuard; this does not provide a zero-paid-call default.");
    }

    [Fact]
    public async Task Unknown_estimate_denial_stops_before_provider_invocation_and_attempt_recording()
    {
        var store = new RecordingStore();
        var guard = new DelegateCostGuard(context => context.EstimatedCostUsd is null
            ? new ProviderCostGuardDecision(false, "UNKNOWN_COST_DENIED")
            : new ProviderCostGuardDecision(true));
        var provider = new CountingProvider(new ProviderRoutingProfile(EstimatedCostUsd: null));
        var result = await Create(store, guard).ExecuteAsync(
            Guid.NewGuid(), Guid.NewGuid(),
            new ProviderRouteRequirements("synthetic.generate", AllowFallback: false),
            "unknown-cost-attempt",
            "synthetic input",
            [provider]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("UNKNOWN_COST_DENIED", result.ErrorCode);
        Assert.Equal(1, guard.Calls);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, store.StartAttemptCalls);
    }

    [Fact]
    public async Task Cost_guard_denial_is_checked_for_every_fallback_before_any_provider_attempt()
    {
        var store = new RecordingStore();
        var guard = new DelegateCostGuard(_ => new ProviderCostGuardDecision(false, "PUBLIC_FREE_TIER_BUDGET_EXHAUSTED"));
        var unpricedProvider = new CountingProvider(new ProviderRoutingProfile(EstimatedCostUsd: null));
        var pricedFallback = new CountingProvider(new ProviderRoutingProfile(EstimatedCostUsd: 0.01m));

        var result = await Create(store, guard, maxProviders: 2).ExecuteAsync(
            Guid.NewGuid(), Guid.NewGuid(),
            new ProviderRouteRequirements("synthetic.generate", AllowFallback: true),
            "denied-fallback-attempt",
            "synthetic input",
            [unpricedProvider, pricedFallback]);

        Assert.Equal(ProviderAttemptResultCategory.CostGuardRejected, result.Category);
        Assert.Equal("PUBLIC_FREE_TIER_BUDGET_EXHAUSTED", result.ErrorCode);
        Assert.Equal(2, guard.Calls);
        Assert.Equal(0, result.AttemptCount);
        Assert.Equal(0, unpricedProvider.Calls);
        Assert.Equal(0, pricedFallback.Calls);
        Assert.Equal(0, store.StartAttemptCalls);
    }

    [Fact]
    public async Task Cost_guard_storage_failure_stops_before_provider_invocation_and_attempt_recording()
    {
        var store = new RecordingStore();
        var provider = new CountingProvider(new ProviderRoutingProfile(EstimatedCostUsd: 0.01m));
        var orchestrator = Create(store, new DelegateCostGuard(_ => throw new InvalidOperationException("synthetic guard unavailable")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ExecuteAsync(
            Guid.NewGuid(), Guid.NewGuid(),
            new ProviderRouteRequirements("synthetic.generate", AllowFallback: false),
            "guard-error-attempt",
            "synthetic input",
            [provider]));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, store.StartAttemptCalls);
    }

    private static ProviderResilienceOrchestrator Create(RecordingStore store, IProviderCostGuard guard, int maxProviders = 1) =>
        new(store, guard, Options.Create(new ProviderResilienceOptions
        {
            MaxProviders = maxProviders,
            MaxRetriesPerProvider = 0,
            InitialBackoffMilliseconds = 0,
            MaxBackoffMilliseconds = 0,
            ProviderTimeoutSeconds = 1,
        }), TimeProvider.System, NullLogger<ProviderResilienceOrchestrator>.Instance);

    private sealed class DelegateCostGuard(Func<ProviderCostGuardContext, ProviderCostGuardDecision> decide) : IProviderCostGuard
    {
        public int Calls { get; private set; }

        public Task<ProviderCostGuardDecision> CanAttemptAsync(ProviderCostGuardContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(decide(context));
        }
    }

    private sealed class CountingProvider(ProviderRoutingProfile profile) : IResilientProvider<string, string>, IProviderRoutingProfile<string>
    {
        public string Key => "synthetic-provider";
        public int Calls { get; private set; }
        public bool CanHandle(string capability, string input) => true;
        public ProviderRoutingProfile GetRoutingProfile(string capability, string input) => profile;

        public Task<ProviderExecutionSuccess<string>> ExecuteAsync(ProviderExecutionContext context, string input, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ProviderExecutionSuccess<string>("synthetic result"));
        }
    }

    private sealed class RecordingStore : IProviderResilienceStore
    {
        public int StartAttemptCalls { get; private set; }

        public Task<ProviderFinalizationClaimResult> TryClaimFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, DateTime now, TimeSpan lease, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderFinalizationClaimResult.Claimed);

        public Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default)
        {
            StartAttemptCalls++;
            return Task.FromResult(new ProviderAttemptRecord(Guid.NewGuid(), generationJobId, jobConcurrencyToken, idempotencyKey, capability, providerKey, attemptNumber, isRetry, isFallback, startedAt));
        }

        public Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderCircuitAdmission.Allowed);

        public Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default) => Task.FromResult<ProviderCircuitSnapshot?>(null);
        public Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default) => Task.FromResult(0m);
    }
}

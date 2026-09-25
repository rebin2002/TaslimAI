using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Resilience;

public enum ProviderAttemptResultCategory
{
    Success,
    Unavailable,
    RateLimited,
    TransientFailure,
    PermanentFailure,
    UnsupportedCapability,
    ValidationRejected,
    InvalidUserInput,
    Cancelled,
    TimedOut,
    CircuitOpen,
    CostGuardRejected,
}

public static class ProviderResilienceMessages
{
    public const string GenericFailure = "Taslim could not complete the request.";
}

public enum ProviderCircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public enum ProviderFinalizationClaimResult
{
    Claimed,
    AlreadyClaimed,
    AlreadyCompleted,
    StaleWorker,
}

public sealed class ProviderResilienceOptions
{
    public int MaxProviders { get; set; } = 3;
    public int MaxRetriesPerProvider { get; set; } = 2;
    public int ProviderTimeoutSeconds { get; set; } = 180;
    public int InitialBackoffMilliseconds { get; set; } = 250;
    public int MaxBackoffMilliseconds { get; set; } = 4_000;
    public int CircuitFailureThreshold { get; set; } = 3;
    public int CircuitOpenSeconds { get; set; } = 30;
    public int CircuitProbeLeaseSeconds { get; set; } = 60;
    public decimal MaxEstimatedCostUsd { get; set; } = 5m;
}

public sealed record ProviderResilienceFailure(
    ProviderAttemptResultCategory Category,
    string? Code = null,
    string? SafeMessage = null);

public sealed record ProviderExecutionContext(
    Guid GenerationJobId,
    Guid JobConcurrencyToken,
    string Capability,
    string IdempotencyKey,
    int AttemptNumber,
    bool IsRetry,
    bool IsFallback,
    decimal? EstimatedCostUsd);

public sealed record ProviderExecutionSuccess<T>(T Value, decimal? EstimatedCostUsd = null);

public interface IResilientProvider<TInput, TOutput>
{
    string Key { get; }
    bool CanHandle(string capability, TInput input);
    Task<ProviderExecutionSuccess<TOutput>> ExecuteAsync(ProviderExecutionContext context, TInput input, CancellationToken cancellationToken);
}

public sealed class ProviderResilienceException(ProviderAttemptResultCategory category, string? code = null, string? safeMessage = null) : Exception(safeMessage)
{
    public ProviderAttemptResultCategory Category { get; } = category;
    public string? Code { get; } = code;
    public string? SafeMessage { get; } = safeMessage;
}

public sealed class StaleProviderWorkerException : Exception
{
    public StaleProviderWorkerException(Guid jobId) : base($"The provider worker claim for job {jobId} is no longer current.") { }
}

public sealed record ProviderAttemptRecord(
    Guid Id,
    Guid GenerationJobId,
    Guid JobConcurrencyToken,
    string IdempotencyKey,
    string Capability,
    string ProviderKey,
    int AttemptNumber,
    bool IsRetry,
    bool IsFallback,
    DateTime StartedAt);

public sealed record ProviderCircuitSnapshot(
    string ProviderKey,
    string Capability,
    ProviderCircuitState State,
    int ConsecutiveFailures,
    DateTime? OpenUntil,
    DateTime? ProbeExpiresAt);

public enum ProviderCircuitAdmission
{
    Allowed,
    Open,
    ProbeInProgress,
}

public sealed record ProviderCostGuardContext(
    Guid GenerationJobId,
    string Capability,
    string ProviderKey,
    int AttemptNumber,
    decimal? EstimatedCostUsd,
    decimal CumulativeEstimatedCostUsd,
    decimal MaxEstimatedCostUsd);

public sealed record ProviderCostGuardDecision(bool Allowed, string? Code = null);

public interface IProviderCostGuard
{
    Task<ProviderCostGuardDecision> CanAttemptAsync(ProviderCostGuardContext context, CancellationToken cancellationToken = default);
}

public sealed class AllowAllProviderCostGuard : IProviderCostGuard
{
    public Task<ProviderCostGuardDecision> CanAttemptAsync(ProviderCostGuardContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProviderCostGuardDecision(true));
}

public interface IProviderResilienceStore
{
    Task<ProviderFinalizationClaimResult> TryClaimFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, DateTime now, TimeSpan lease, CancellationToken cancellationToken = default);
    Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default);
    Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default);
    Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default);
    Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default);
    Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default);
    Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default);
    Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default);
    Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default);
}

public sealed record ProviderResilienceExecutionResult<T>(
    ProviderAttemptResultCategory Category,
    T? Value,
    string? ErrorCode,
    int AttemptCount,
    string? ProviderKey,
    bool IsReplay = false)
{
    public bool Succeeded => Category == ProviderAttemptResultCategory.Success;
}

public sealed class ProviderResilienceOrchestrator(
    IProviderResilienceStore store,
    IProviderCostGuard costGuard,
    IOptions<ProviderResilienceOptions> options,
    TimeProvider timeProvider,
    ILogger<ProviderResilienceOrchestrator> logger) : IProviderResilienceOrchestrator
{
    private readonly ProviderResilienceOptions settings = options.Value;

    public async Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(
        Guid generationJobId,
        Guid jobConcurrencyToken,
        string capability,
        string idempotencyKey,
        TInput input,
        IReadOnlyList<IResilientProvider<TInput, TOutput>> providers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claim = await store.TryClaimFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, now, TimeSpan.FromSeconds(Math.Max(1, settings.ProviderTimeoutSeconds)), cancellationToken);
        if (claim == ProviderFinalizationClaimResult.StaleWorker)
            throw new StaleProviderWorkerException(generationJobId);
        if (claim == ProviderFinalizationClaimResult.AlreadyCompleted)
            return new(ProviderAttemptResultCategory.Success, default, null, 0, null, true);
        if (claim == ProviderFinalizationClaimResult.AlreadyClaimed)
            return new(ProviderAttemptResultCategory.TransientFailure, default, "EXECUTION_ALREADY_CLAIMED", 0, null, false);
        if (providers.Count == 0)
        {
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Unavailable, cancellationToken);
            return new(ProviderAttemptResultCategory.Unavailable, default, "NO_PROVIDER_AVAILABLE", 0, null);
        }

        var boundedProviders = providers.Take(Math.Max(1, settings.MaxProviders)).ToArray();
        var attemptCount = 0;
        ProviderAttemptResultCategory? lastCategory = null;
        string? lastCode = null;
        string? lastProvider = null;
        var finalCategory = ProviderAttemptResultCategory.Unavailable;

        try
        {
            foreach (var provider in boundedProviders)
            {
                if (!provider.CanHandle(capability, input))
                {
                    lastCategory = ProviderAttemptResultCategory.UnsupportedCapability;
                    lastCode = "CAPABILITY_UNSUPPORTED";
                    lastProvider = provider.Key;
                    continue;
                }

                var admission = await store.TryAcquireCircuitAsync(provider.Key, capability, timeProvider.GetUtcNow().UtcDateTime, TimeSpan.FromSeconds(Math.Max(1, settings.CircuitProbeLeaseSeconds)), cancellationToken);
                if (admission != ProviderCircuitAdmission.Allowed)
                {
                    finalCategory = ProviderAttemptResultCategory.CircuitOpen;
                    lastCategory = finalCategory;
                    lastCode = "CIRCUIT_OPEN";
                    lastProvider = provider.Key;
                    logger.LogInformation("Provider circuit admission denied. Capability={Capability}; ProviderKey={ProviderKey}; State={State}", capability, provider.Key, admission);
                    continue;
                }

                var attemptCounter = new AttemptCounter(attemptCount);
                var providerResult = await ExecuteProviderAsync(provider, generationJobId, jobConcurrencyToken, capability, idempotencyKey, input, provider == boundedProviders[0], attemptCounter, cancellationToken);
                attemptCount = attemptCounter.Value;
                if (providerResult.Succeeded)
                {
                    await store.RecordCircuitSuccessAsync(provider.Key, capability, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                    await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Success, cancellationToken);
                    return providerResult;
                }

                if (CountsAsProviderFailure(providerResult.Category))
                    await store.RecordCircuitFailureAsync(provider.Key, capability, timeProvider.GetUtcNow().UtcDateTime, Math.Max(1, settings.CircuitFailureThreshold), TimeSpan.FromSeconds(Math.Max(1, settings.CircuitOpenSeconds)), cancellationToken);
                lastCategory = providerResult.Category;
                lastCode = providerResult.ErrorCode;
                lastProvider = providerResult.ProviderKey;
                finalCategory = providerResult.Category;
                if (providerResult.Category is ProviderAttemptResultCategory.InvalidUserInput or ProviderAttemptResultCategory.ValidationRejected or ProviderAttemptResultCategory.Cancelled or ProviderAttemptResultCategory.CostGuardRejected)
                    break;
            }

            finalCategory = lastCategory ?? ProviderAttemptResultCategory.Unavailable;
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, finalCategory, cancellationToken);
            return new(finalCategory, default, lastCode ?? "PROVIDER_UNAVAILABLE", attemptCount, lastProvider);
        }
        catch (StaleProviderWorkerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryCompleteCancellationAsync(generationJobId, jobConcurrencyToken, idempotencyKey);
            return new(ProviderAttemptResultCategory.Cancelled, default, "CANCELLED", attemptCount, lastProvider);
        }
    }

    private async Task<ProviderResilienceExecutionResult<TOutput>> ExecuteProviderAsync<TInput, TOutput>(
        IResilientProvider<TInput, TOutput> provider,
        Guid generationJobId,
        Guid jobConcurrencyToken,
        string capability,
        string idempotencyKey,
        TInput input,
        bool isPrimary,
        AttemptCounter attemptCounter,
        CancellationToken cancellationToken)
    {
        ProviderResilienceExecutionResult<TOutput>? last = null;
        var maxAttempts = Math.Max(1, settings.MaxRetriesPerProvider + 1);
        for (var providerAttempt = 1; providerAttempt <= maxAttempts; providerAttempt++)
        {
            if (cancellationToken.IsCancellationRequested || await store.IsCancellationRequestedAsync(generationJobId, cancellationToken))
                return new(ProviderAttemptResultCategory.Cancelled, default, "CANCELLED", attemptCounter.Value, provider.Key);

            var estimatedCost = (decimal?)null;
            var cumulativeCost = await store.GetCumulativeEstimatedCostAsync(generationJobId, cancellationToken);
            var costDecision = await costGuard.CanAttemptAsync(new ProviderCostGuardContext(generationJobId, capability, provider.Key, providerAttempt, estimatedCost, cumulativeCost, settings.MaxEstimatedCostUsd), cancellationToken);
            if (!costDecision.Allowed)
                return new(ProviderAttemptResultCategory.CostGuardRejected, default, costDecision.Code ?? "COST_GUARD_REJECTED", attemptCounter.Value, provider.Key);

            attemptCounter.Value++;
            var attempt = await store.StartAttemptAsync(generationJobId, jobConcurrencyToken, idempotencyKey, capability, provider.Key, attemptCounter.Value, providerAttempt > 1, !isPrimary, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            ProviderAttemptResultCategory category;
            string? code = null;
            TOutput? value = default;
            decimal? actualCost = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.ProviderTimeoutSeconds)));
                var success = await provider.ExecuteAsync(new ProviderExecutionContext(generationJobId, jobConcurrencyToken, capability, idempotencyKey, attemptCounter.Value, providerAttempt > 1, !isPrimary, estimatedCost), input, timeout.Token);
                value = success.Value;
                actualCost = success.EstimatedCostUsd;
                category = ProviderAttemptResultCategory.Success;
            }
            catch (ProviderResilienceException exception)
            {
                category = exception.Category;
                code = exception.Code;
            }
            catch (OperationCanceledException)
            {
                var cancelled = cancellationToken.IsCancellationRequested || await store.IsCancellationRequestedAsync(generationJobId, CancellationToken.None);
                category = cancelled ? ProviderAttemptResultCategory.Cancelled : ProviderAttemptResultCategory.TimedOut;
                code = cancelled ? "CANCELLED" : "PROVIDER_TIMEOUT";
            }
            catch (Exception exception)
            {
                category = ProviderAttemptResultCategory.PermanentFailure;
                code = "PROVIDER_FAILURE";
                logger.LogWarning("Provider attempt failed with sanitized category. ProviderKey={ProviderKey}; Capability={Capability}; ExceptionType={ExceptionType}", provider.Key, capability, exception.GetType().Name);
            }
            stopwatch.Stop();
            await store.CompleteAttemptAsync(attempt, category, code, stopwatch.ElapsedMilliseconds, actualCost, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
            if (category == ProviderAttemptResultCategory.Success)
                return new(category, value, null, attemptCounter.Value, provider.Key);

            last = new(category, default, code, attemptCounter.Value, provider.Key);
            if (!IsRetryable(category) || providerAttempt >= maxAttempts)
                break;

            var backoff = Math.Min(settings.MaxBackoffMilliseconds, settings.InitialBackoffMilliseconds * Math.Pow(2, providerAttempt - 1));
            logger.LogInformation("Retrying provider attempt. ProviderKey={ProviderKey}; Capability={Capability}; AttemptNumber={AttemptNumber}; Category={Category}", provider.Key, capability, attemptCounter.Value, category);
            if (backoff > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(backoff), cancellationToken);
        }

        return last ?? new(ProviderAttemptResultCategory.Unavailable, default, "PROVIDER_UNAVAILABLE", attemptCounter.Value, provider.Key);
    }

    private async Task TryCompleteCancellationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey)
    {
        try
        {
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Cancelled, CancellationToken.None);
        }
        catch (StaleProviderWorkerException) { }
    }

    private static bool IsRetryable(ProviderAttemptResultCategory category) =>
        category is ProviderAttemptResultCategory.RateLimited or ProviderAttemptResultCategory.TransientFailure or ProviderAttemptResultCategory.TimedOut;

    private static bool CountsAsProviderFailure(ProviderAttemptResultCategory category) =>
        category is not (ProviderAttemptResultCategory.InvalidUserInput
            or ProviderAttemptResultCategory.ValidationRejected
            or ProviderAttemptResultCategory.UnsupportedCapability
            or ProviderAttemptResultCategory.Cancelled
            or ProviderAttemptResultCategory.CostGuardRejected);

    private sealed class AttemptCounter(int value)
    {
        public int Value { get; set; } = value;
    }
}

public interface IProviderResilienceOrchestrator
{
    Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(Guid generationJobId, Guid jobConcurrencyToken, string capability, string idempotencyKey, TInput input, IReadOnlyList<IResilientProvider<TInput, TOutput>> providers, CancellationToken cancellationToken = default);
}

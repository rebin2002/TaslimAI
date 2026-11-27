using System.Diagnostics;
using System.Net.Http;
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

/// <summary>
/// The caller's non-negotiable routing constraints. Provider and model identifiers
/// stay on the server; this contract is intentionally capability and policy based.
/// </summary>
public sealed record ProviderRouteRequirements(
    string Capability,
    int MinimumQualityRank = 0,
    decimal? MaxEstimatedCostUsd = null,
    bool RequireIdempotency = true,
    bool AllowFallback = true);

/// <summary>
/// Server-owned provider metadata used only by the router. A provider that does not
/// implement the optional profile interface remains compatible with the safe default.
/// </summary>
public sealed record ProviderRoutingProfile(
    int QualityRank = 0,
    decimal? EstimatedCostUsd = null,
    bool SupportsIdempotency = true)
{
    public static ProviderRoutingProfile Default { get; } = new();
}

public sealed record ProviderHealthSignal(
    string ProviderKey,
    string Capability,
    ProviderCircuitState CircuitState,
    int ConsecutiveFailures,
    DateTime? OpenUntil,
    DateTime? ProbeExpiresAt);

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

public interface IProviderRoutingProfile<in TInput>
{
    ProviderRoutingProfile GetRoutingProfile(string capability, TInput input);
}

public interface IResilientProvider<TInput, TOutput>
{
    string Key { get; }
    bool CanHandle(string capability, TInput input);

    // Default interface implementation keeps existing adapters source-compatible
    // while making quality, cost, and idempotency explicit for new adapters.
    ProviderRoutingProfile GetRoutingProfile(string capability, TInput input) =>
        this is IProviderRoutingProfile<TInput> profiled
            ? profiled.GetRoutingProfile(capability, input)
            : ProviderRoutingProfile.Default;

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

public sealed record ProviderAttemptHistory(
    int LastAttemptNumber,
    IReadOnlySet<string> TriedProviders,
    ProviderAttemptResultCategory? LastCategory,
    bool RequiresReconciliation);

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

public sealed record ProviderFinalizationOutcome(ProviderAttemptResultCategory Category, string? ErrorCode);

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
    Task<ProviderFinalizationOutcome?> ReadCompletedOutcomeAsync(Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default, string? errorCode = null);
    Task<ProviderAttemptHistory> ReadAttemptHistoryAsync(Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default, decimal? reservedCostUsd = null);
    Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default, Guid? replacedReservationId = null);
    Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default);
    Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default);
    Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default);
    Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default);
    Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default);
    Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default, Guid? excludeReservationId = null);
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

public static class ProviderFailureClassifier
{
    public static ProviderResilienceFailure Classify(Exception exception) => exception switch
    {
        ProviderResilienceException provider => new(provider.Category, provider.Code, provider.SafeMessage),
        TimeoutException => new(ProviderAttemptResultCategory.TimedOut, "PROVIDER_TIMEOUT"),
        HttpRequestException => new(ProviderAttemptResultCategory.TransientFailure, "PROVIDER_NETWORK_FAILURE"),
        OperationCanceledException => new(ProviderAttemptResultCategory.Cancelled, "CANCELLED"),
        _ => new(ProviderAttemptResultCategory.PermanentFailure, "PROVIDER_FAILURE"),
    };
}

public static class ProviderRetryPolicy
{
    public const int MaximumRetries = 8;

    public static int NormalizeRetries(int configuredRetries) => Math.Clamp(configuredRetries, 0, MaximumRetries);

    public static bool IsRetryable(ProviderAttemptResultCategory category) => category is
        ProviderAttemptResultCategory.RateLimited or
        ProviderAttemptResultCategory.TransientFailure or
        ProviderAttemptResultCategory.TimedOut;

    /// <summary>
    /// Outcomes that do not prove the provider rejected the submission before taking
    /// it on. A timeout or a transport failure can happen after the provider has
    /// already accepted the work, so a second request for the same generation is a
    /// duplicate paid submission unless the provider de-duplicates the key.
    /// </summary>
    public static bool IsResubmissionAmbiguous(ProviderAttemptResultCategory category) => category is
        ProviderAttemptResultCategory.TransientFailure or
        ProviderAttemptResultCategory.TimedOut;

    /// <summary>
    /// Retry admission for one selected provider. The category alone is not enough:
    /// when the provider has declared it cannot de-duplicate the idempotency key, an
    /// ambiguous outcome must fail closed instead of resubmitting accepted work.
    /// </summary>
    public static bool IsRetryable(ProviderAttemptResultCategory category, bool providerSupportsIdempotency) =>
        IsRetryable(category) && (providerSupportsIdempotency || !IsResubmissionAmbiguous(category));

    public static TimeSpan GetDelay(ProviderResilienceOptions options, int retryOrdinal)
    {
        var initial = Math.Max(0, options.InitialBackoffMilliseconds);
        var maximum = Math.Max(0, options.MaxBackoffMilliseconds);
        if (initial == 0 || maximum == 0) return TimeSpan.Zero;

        var exponent = Math.Clamp(retryOrdinal, 0, 10);
        var uncapped = initial * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(maximum, uncapped));
    }
}

public static class ProviderRoutingPolicy
{
    public static bool IsFallbackEligible(ProviderAttemptResultCategory category) => category is
        ProviderAttemptResultCategory.Unavailable or
        ProviderAttemptResultCategory.RateLimited or
        ProviderAttemptResultCategory.TransientFailure or
        ProviderAttemptResultCategory.PermanentFailure or
        ProviderAttemptResultCategory.TimedOut or
        ProviderAttemptResultCategory.CircuitOpen;

    public static bool MeetsRequirements(ProviderRoutingProfile profile, ProviderRouteRequirements requirements, decimal configuredMaxCostUsd)
    {
        if (profile.QualityRank < requirements.MinimumQualityRank) return false;
        if (requirements.RequireIdempotency && !profile.SupportsIdempotency) return false;
        if (profile.EstimatedCostUsd is not { } estimate) return true;
        if (estimate < 0) return false;

        var requestMax = requirements.MaxEstimatedCostUsd ?? decimal.MaxValue;
        var configuredMax = configuredMaxCostUsd >= 0 ? configuredMaxCostUsd : 0m;
        return estimate <= Math.Min(requestMax, configuredMax);
    }
}

public sealed class ProviderResilienceOrchestrator(
    IProviderResilienceStore store,
    IProviderCostGuard costGuard,
    IOptions<ProviderResilienceOptions> options,
    TimeProvider timeProvider,
    ILogger<ProviderResilienceOrchestrator> logger) : IProviderResilienceOrchestrator
{
    private readonly ProviderResilienceOptions settings = options.Value;

    public Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(
        Guid generationJobId,
        Guid jobConcurrencyToken,
        string capability,
        string idempotencyKey,
        TInput input,
        IReadOnlyList<IResilientProvider<TInput, TOutput>> providers,
        CancellationToken cancellationToken = default) => ExecuteAsync(
            generationJobId,
            jobConcurrencyToken,
            new ProviderRouteRequirements(capability),
            idempotencyKey,
            input,
            providers,
            cancellationToken);

    public async Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(
        Guid generationJobId,
        Guid jobConcurrencyToken,
        ProviderRouteRequirements requirements,
        string idempotencyKey,
        TInput input,
        IReadOnlyList<IResilientProvider<TInput, TOutput>> providers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirements.Capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claim = await store.TryClaimFinalizationAsync(
            generationJobId,
            jobConcurrencyToken,
            idempotencyKey,
            now,
            TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 1, 1_800)),
            cancellationToken);
        if (claim == ProviderFinalizationClaimResult.StaleWorker)
            throw new StaleProviderWorkerException(generationJobId);
        if (claim == ProviderFinalizationClaimResult.AlreadyCompleted)
        {
            var outcome = await store.ReadCompletedOutcomeAsync(generationJobId, idempotencyKey, cancellationToken);
            if (outcome is null)
                return new(ProviderAttemptResultCategory.Unavailable, default, "EXECUTION_OUTCOME_UNAVAILABLE", 0, null, true);
            return new(outcome.Category, default, outcome.ErrorCode, 0, null, true);
        }
        if (claim == ProviderFinalizationClaimResult.AlreadyClaimed)
            return new(ProviderAttemptResultCategory.TransientFailure, default, "EXECUTION_ALREADY_CLAIMED", 0, null, false);
        // A prior worker may have reached the vendor before it lost its lease. Its
        // request must be reconciled without starting a second execution of the job.
        var history = await store.ReadAttemptHistoryAsync(generationJobId, idempotencyKey, cancellationToken);
        if (history.RequiresReconciliation)
            return new(ProviderAttemptResultCategory.TransientFailure, default, "PROVIDER_RECONCILIATION_REQUIRED", 0, null);
        if (settings.MaxEstimatedCostUsd < 0)
        {
            await store.CompleteFinalizationAsync(
                generationJobId,
                jobConcurrencyToken,
                idempotencyKey,
                ProviderAttemptResultCategory.CostGuardRejected,
                cancellationToken, errorCode: "INVALID_COST_LIMIT");
            return new(ProviderAttemptResultCategory.CostGuardRejected, default, "INVALID_COST_LIMIT", 0, null);
        }
        if (providers.Count == 0)
        {
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Unavailable, cancellationToken, errorCode: "NO_PROVIDER_AVAILABLE");
            return new(ProviderAttemptResultCategory.Unavailable, default, "NO_PROVIDER_AVAILABLE", 0, null);
        }

        var boundedProviders = providers.Take(Math.Max(1, settings.MaxProviders)).ToArray();
        string? uncertainProviderKey = null;
        decimal? uncertainProviderCost = null;
        var attemptCount = history.LastAttemptNumber;
        var hasSelectedProvider = history.TriedProviders.Count > 0;
        ProviderAttemptResultCategory? lastCategory = history.LastCategory;
        string? lastCode = history.LastCategory switch
        {
            ProviderAttemptResultCategory.CircuitOpen => "CIRCUIT_OPEN",
            ProviderAttemptResultCategory.RateLimited => "RATE_LIMITED",
            _ => null,
        };
        string? lastProvider = null;

        try
        {
            foreach (var provider in boundedProviders)
            {
                // An alias/duplicate route entry must not submit to the same provider again.
                if (history.TriedProviders.Count > 0 && !requirements.AllowFallback) break;
                if (history.TriedProviders.Contains(provider.Key.Trim())
                    || string.Equals(uncertainProviderKey, provider.Key.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!provider.CanHandle(requirements.Capability, input))
                {
                    lastCategory = ProviderAttemptResultCategory.UnsupportedCapability;
                    lastCode = "CAPABILITY_UNSUPPORTED";
                    lastProvider = provider.Key;
                    if (!requirements.AllowFallback) break;
                    continue;
                }

                var profile = provider.GetRoutingProfile(requirements.Capability, input);
                if (!ProviderRoutingPolicy.MeetsRequirements(profile, requirements, settings.MaxEstimatedCostUsd))
                {
                    lastCategory = profile.QualityRank < requirements.MinimumQualityRank || !profile.SupportsIdempotency && requirements.RequireIdempotency
                        ? ProviderAttemptResultCategory.UnsupportedCapability
                        : ProviderAttemptResultCategory.CostGuardRejected;
                    lastCode = profile.QualityRank < requirements.MinimumQualityRank
                        ? "QUALITY_REQUIREMENT_UNMET"
                        : !profile.SupportsIdempotency && requirements.RequireIdempotency
                            ? "IDEMPOTENCY_REQUIRED"
                            : "COST_LIMIT_EXCEEDED";
                    lastProvider = provider.Key;
                    if (!requirements.AllowFallback) break;
                    continue;
                }

                if ((uncertainProviderKey is not null && (uncertainProviderCost is null || profile.EstimatedCostUsd is null))
                    || (profile.EstimatedCostUsd is null && (requirements.MaxEstimatedCostUsd == 0m || settings.MaxEstimatedCostUsd == 0m)))
                {
                    lastCategory = ProviderAttemptResultCategory.CostGuardRejected;
                    lastCode = "COST_ESTIMATE_REQUIRED";
                    lastProvider = provider.Key;
                    if (!requirements.AllowFallback) break;
                    continue;
                }

                var isFallback = hasSelectedProvider;
                hasSelectedProvider = true;
                var admission = await store.TryAcquireCircuitAsync(
                    provider.Key,
                    requirements.Capability,
                    timeProvider.GetUtcNow().UtcDateTime,
                    TimeSpan.FromSeconds(Math.Clamp(settings.CircuitProbeLeaseSeconds, 1, 3_600)),
                    cancellationToken);
                if (admission != ProviderCircuitAdmission.Allowed)
                {
                    attemptCount++;
                    var skippedAttempt = await store.StartAttemptAsync(
                        generationJobId,
                        jobConcurrencyToken,
                        idempotencyKey,
                        requirements.Capability,
                        provider.Key,
                        attemptCount,
                        false,
                        isFallback,
                        timeProvider.GetUtcNow().UtcDateTime,
                        cancellationToken);
                    await store.CompleteAttemptAsync(
                        skippedAttempt,
                        ProviderAttemptResultCategory.CircuitOpen,
                        "CIRCUIT_OPEN",
                        0,
                        null,
                        timeProvider.GetUtcNow().UtcDateTime,
                        cancellationToken);
                    lastCategory = ProviderAttemptResultCategory.CircuitOpen;
                    lastCode = "CIRCUIT_OPEN";
                    lastProvider = provider.Key;
                    logger.LogInformation("Provider circuit admission denied. Capability={Capability}; ProviderKey={ProviderKey}; State={State}", requirements.Capability, provider.Key, admission);
                    if (!requirements.AllowFallback) break;
                    continue;
                }

                var attempts = new ProviderAttempts(attemptCount);
                var providerResult = await ExecuteProviderAsync(
                    provider,
                    generationJobId,
                    jobConcurrencyToken,
                    requirements,
                    idempotencyKey,
                    input,
                    !isFallback,
                    profile,
                    attempts,
                    cancellationToken);
                var dispatched = attempts.Count > attemptCount;
                attemptCount = attempts.Count;
                if (providerResult.Succeeded)
                {
                    await store.RecordCircuitSuccessAsync(provider.Key, requirements.Capability, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                    await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Success, cancellationToken);
                    return providerResult;
                }

                if (CountsAsProviderFailure(providerResult.Category))
                    await store.RecordCircuitFailureAsync(
                        provider.Key,
                        requirements.Capability,
                        timeProvider.GetUtcNow().UtcDateTime,
                        Math.Max(1, settings.CircuitFailureThreshold),
                        TimeSpan.FromSeconds(Math.Clamp(settings.CircuitOpenSeconds, 1, 3_600)),
                        cancellationToken);
                lastCategory = providerResult.Category;
                lastCode = providerResult.ErrorCode;
                lastProvider = providerResult.ProviderKey;
                // Once acceptance is uncertain, only one additional vendor may be
                // contacted. A candidate rejected before dispatch does not use that slot.
                if (attempts.RejectedAfterSuccess || (uncertainProviderKey is not null && dispatched))
                    break;
                if (attempts.UncertainAttemptId is not null)
                {
                    uncertainProviderKey = provider.Key.Trim();
                    uncertainProviderCost = profile.EstimatedCostUsd;
                }
                if (providerResult.Category is ProviderAttemptResultCategory.InvalidUserInput
                    or ProviderAttemptResultCategory.ValidationRejected
                    or ProviderAttemptResultCategory.Cancelled)
                    break;
                if (providerResult.Category == ProviderAttemptResultCategory.CostGuardRejected && requirements.AllowFallback)
                    continue;
                if (!requirements.AllowFallback || !ProviderRoutingPolicy.IsFallbackEligible(providerResult.Category))
                    break;
            }

            var finalCategory = lastCategory ?? ProviderAttemptResultCategory.Unavailable;
            var finalCode = lastCode ?? "PROVIDER_UNAVAILABLE";
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, finalCategory, cancellationToken, finalCode);
            return new(finalCategory, default, finalCode, attemptCount, lastProvider);
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
        ProviderRouteRequirements requirements,
        string idempotencyKey,
        TInput input,
        bool isPrimary,
        ProviderRoutingProfile profile,
        ProviderAttempts attempts,
        CancellationToken cancellationToken)
    {
        ProviderResilienceExecutionResult<TOutput>? last = null;
        var maxAttempts = ProviderRetryPolicy.NormalizeRetries(settings.MaxRetriesPerProvider) + 1;
        for (var providerAttempt = 1; providerAttempt <= maxAttempts; providerAttempt++)
        {
            if (cancellationToken.IsCancellationRequested || await store.IsCancellationRequestedAsync(generationJobId, cancellationToken))
                return new(ProviderAttemptResultCategory.Cancelled, default, "CANCELLED", attempts.Count, provider.Key);

            var cumulativeCost = await store.GetCumulativeEstimatedCostAsync(generationJobId, cancellationToken, attempts.UncertainAttemptId);
            var maxCost = Math.Min(
                requirements.MaxEstimatedCostUsd ?? decimal.MaxValue,
                Math.Max(0m, settings.MaxEstimatedCostUsd));
            if (ExceedsCostLimit(cumulativeCost, profile.EstimatedCostUsd, maxCost))
            {
                return new(ProviderAttemptResultCategory.CostGuardRejected, default, "COST_LIMIT_EXCEEDED", attempts.Count, provider.Key);
            }

            var costDecision = await costGuard.CanAttemptAsync(
                new ProviderCostGuardContext(
                    generationJobId,
                    requirements.Capability,
                    provider.Key,
                    providerAttempt,
                    profile.EstimatedCostUsd,
                    cumulativeCost,
                    maxCost),
                cancellationToken);
            if (!costDecision.Allowed)
                return new(ProviderAttemptResultCategory.CostGuardRejected, default, costDecision.Code ?? "COST_GUARD_REJECTED", attempts.Count, provider.Key);

            attempts.Count++;
            var attempt = await store.StartAttemptAsync(
                generationJobId,
                jobConcurrencyToken,
                idempotencyKey,
                requirements.Capability,
                provider.Key,
                attempts.Count,
                providerAttempt > 1,
                !isPrimary,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken,
                reservedCostUsd: attempts.UncertainAttemptId is null ? profile.EstimatedCostUsd : 0m);
            var stopwatch = Stopwatch.StartNew();
            ProviderAttemptResultCategory category;
            string? code = null;
            TOutput? value = default;
            decimal? attemptCost = null;
            var accepted = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.ProviderTimeoutSeconds, 1, 1_800)));
                var success = await provider.ExecuteAsync(
                    new ProviderExecutionContext(
                        generationJobId,
                        jobConcurrencyToken,
                        requirements.Capability,
                        idempotencyKey,
                        attempts.Count,
                        providerAttempt > 1,
                        !isPrimary,
                        profile.EstimatedCostUsd),
                    input,
                    timeout.Token);
                accepted = true;
                attemptCost = success.EstimatedCostUsd ?? profile.EstimatedCostUsd;
                if (ExceedsCostLimit(cumulativeCost, attemptCost, maxCost))
                {
                    category = ProviderAttemptResultCategory.CostGuardRejected;
                    code = "COST_LIMIT_EXCEEDED";
                    attempts.RejectedAfterSuccess = true;
                }
                else
                {
                    value = success.Value;
                    category = ProviderAttemptResultCategory.Success;
                }
            }
            catch (OperationCanceledException)
            {
                var cancelled = cancellationToken.IsCancellationRequested || await store.IsCancellationRequestedAsync(generationJobId, CancellationToken.None);
                category = cancelled ? ProviderAttemptResultCategory.Cancelled : ProviderAttemptResultCategory.TimedOut;
                code = cancelled ? "CANCELLED" : "PROVIDER_TIMEOUT";
            }
            catch (Exception exception)
            {
                var failure = ProviderFailureClassifier.Classify(exception);
                category = failure.Category;
                code = failure.Code;
                logger.LogWarning(
                    "Provider attempt failed with sanitized category. ProviderKey={ProviderKey}; Capability={Capability}; ExceptionType={ExceptionType}; Category={Category}",
                    provider.Key,
                    requirements.Capability,
                    exception.GetType().Name,
                    category);
            }
            stopwatch.Stop();
            // Retries use the same vendor key. Carry one uncertain reservation
            // until an accepted result replaces it with the vendor's reported cost.
            var replacedReservation = accepted ? attempts.UncertainAttemptId : null;
            if (ProviderRetryPolicy.IsResubmissionAmbiguous(category))
            {
                attemptCost = attempts.UncertainAttemptId is null ? profile.EstimatedCostUsd : 0m;
                attempts.UncertainAttemptId ??= attempt.Id;
            }
            else if (category == ProviderAttemptResultCategory.Cancelled)
            {
                // Cancellation may arrive after the vendor accepted the request.
                attemptCost = attempts.UncertainAttemptId is null ? profile.EstimatedCostUsd : 0m;
            }
            await store.CompleteAttemptAsync(
                attempt,
                category,
                code,
                stopwatch.ElapsedMilliseconds,
                attemptCost,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken,
                replacedReservation);
            if (category == ProviderAttemptResultCategory.Success)
                return new(category, value, null, attempts.Count, provider.Key);

            last = new(category, default, code, attempts.Count, provider.Key);
            // A provider that cannot de-duplicate the key is only ever re-sent an
            // outcome that proves it refused the request before accepting it. Anything
            // else fails closed here rather than creating a second paid submission
            // for the same generation job.
            if (!ProviderRetryPolicy.IsRetryable(category, profile.SupportsIdempotency) || providerAttempt >= maxAttempts)
                break;

            var backoff = ProviderRetryPolicy.GetDelay(settings, providerAttempt - 1);
            logger.LogInformation(
                "Retrying provider attempt. ProviderKey={ProviderKey}; Capability={Capability}; AttemptNumber={AttemptNumber}; Category={Category}",
                provider.Key,
                requirements.Capability,
                attempts.Count,
                category);
            if (backoff > TimeSpan.Zero)
                await Task.Delay(backoff, cancellationToken);
        }

        return last ?? new(ProviderAttemptResultCategory.Unavailable, default, "PROVIDER_UNAVAILABLE", attempts.Count, provider.Key);
    }

    // Subtraction avoids decimal overflow when accumulated and next estimated costs
    // are individually representable but their sum is not. Unknown next costs
    // retain their existing provider-policy handling; invalid/corrupt totals fail closed.
    private static bool ExceedsCostLimit(decimal accumulatedUsd, decimal? nextUsd, decimal ceilingUsd)
    {
        if (accumulatedUsd < 0m || accumulatedUsd > ceilingUsd) return true;
        return nextUsd is { } cost && (cost < 0m || cost > ceilingUsd - accumulatedUsd);
    }

    private async Task TryCompleteCancellationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey)
    {
        try
        {
            await store.CompleteFinalizationAsync(generationJobId, jobConcurrencyToken, idempotencyKey, ProviderAttemptResultCategory.Cancelled, CancellationToken.None, errorCode: "CANCELLED");
        }
        catch (StaleProviderWorkerException) { }
    }

    private static bool CountsAsProviderFailure(ProviderAttemptResultCategory category) => category is not (
        ProviderAttemptResultCategory.InvalidUserInput or
        ProviderAttemptResultCategory.ValidationRejected or
        ProviderAttemptResultCategory.UnsupportedCapability or
        ProviderAttemptResultCategory.Cancelled or
        ProviderAttemptResultCategory.CostGuardRejected);

    private sealed class ProviderAttempts(int count)
    {
        public int Count { get; set; } = count;
        public Guid? UncertainAttemptId { get; set; }
        public bool RejectedAfterSuccess { get; set; }
    }
}

public interface IProviderResilienceOrchestrator
{
    Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(Guid generationJobId, Guid jobConcurrencyToken, string capability, string idempotencyKey, TInput input, IReadOnlyList<IResilientProvider<TInput, TOutput>> providers, CancellationToken cancellationToken = default);
    Task<ProviderResilienceExecutionResult<TOutput>> ExecuteAsync<TInput, TOutput>(Guid generationJobId, Guid jobConcurrencyToken, ProviderRouteRequirements requirements, string idempotencyKey, TInput input, IReadOnlyList<IResilientProvider<TInput, TOutput>> providers, CancellationToken cancellationToken = default);
}

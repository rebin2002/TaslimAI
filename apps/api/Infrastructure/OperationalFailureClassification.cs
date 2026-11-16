using Microsoft.Extensions.Logging;
using Taslim.Api.Domain;

namespace Taslim.Api.Infrastructure;

/// <summary>
/// Bounded operational classification for generation failures. These values are
/// intentionally derived only from the persisted public-safe error code; they do
/// not include exception messages, provider payloads, prompts, or credentials.
/// </summary>
public sealed record GenerationFailureClassification(
    string Code,
    string Category,
    bool Retryable);

public static class GenerationFailureClassifier
{
    public static GenerationFailureClassification Classify(string? code)
    {
        var normalized = string.IsNullOrWhiteSpace(code)
            ? "UNKNOWN_FAILURE"
            : code.Trim().ToUpperInvariant();

        if (normalized == GenerationJobErrorCodes.Cancelled || normalized.EndsWith("_CANCELLED", StringComparison.Ordinal))
            return new(normalized, "cancelled", false);

        if (normalized is GenerationJobErrorCodes.Poisoned or "RETRY_EXHAUSTED")
            return new(normalized, "recovery", false);

        if (normalized.Contains("STORAGE", StringComparison.Ordinal))
            return new(normalized, "storage", true);

        if (normalized.Contains("OUTPUT_INVALID", StringComparison.Ordinal)
            || normalized.EndsWith("_RENDER_FAILED", StringComparison.Ordinal)
            || normalized.EndsWith("_QC_FAILED", StringComparison.Ordinal)
            || normalized == GenerationJobErrorCodes.NoBillableAsset)
            return new(normalized, "output", true);

        if (normalized.Contains("PROVIDER", StringComparison.Ordinal))
        {
            var retryable = normalized.Contains("UNAVAILABLE", StringComparison.Ordinal)
                || normalized.Contains("TIMEOUT", StringComparison.Ordinal)
                || normalized.Contains("RATE_LIMITED", StringComparison.Ordinal)
                || normalized.Contains("TRANSIENT", StringComparison.Ordinal)
                || normalized.EndsWith("_PROVIDER_FAILED", StringComparison.Ordinal);
            return new(normalized, "provider", retryable);
        }

        if (normalized.Contains("INVALID", StringComparison.Ordinal)
            || normalized.Contains("UNSUPPORTED", StringComparison.Ordinal)
            || normalized.EndsWith("_REJECTED", StringComparison.Ordinal)
            || normalized.EndsWith("_REFUSAL", StringComparison.Ordinal)
            || normalized.Contains("CONTEXT_TOO_LARGE", StringComparison.Ordinal))
            return new(normalized, "validation", false);

        return new(normalized, "internal", false);
    }
}

/// <summary>
/// Adds stable, non-sensitive correlation properties to all logs emitted while
/// a generation job is claimed and executed, including downstream handler logs.
/// </summary>
public static class GenerationJobOperationalScope
{
    public static IDisposable Begin(ILogger logger, GenerationJob job)
    {
        var requestId = string.IsNullOrWhiteSpace(job.RequestId) ? null : job.RequestId.Trim();
        return logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = requestId ?? $"job:{job.Id:N}",
            ["RequestId"] = requestId,
            ["JobId"] = job.Id,
            ["JobType"] = job.JobType,
            ["RetryOfJobId"] = job.RetryOfJobId,
        }) ?? NullScope.Instance;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

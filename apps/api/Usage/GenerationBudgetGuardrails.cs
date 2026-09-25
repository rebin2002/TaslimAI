using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public sealed class GenerationBudgetOptions
{
    public bool Enabled { get; set; }
    public decimal? MaxEstimatedCostPerJobUsd { get; set; }
    public int? MaxProviderAttemptsPerJob { get; set; }
    public decimal? MaxCumulativeEstimatedCostPerJobUsd { get; set; }
    public decimal? WorkspaceInternalSafetyCeilingUsd { get; set; }
    public decimal? UserInternalSafetyCeilingUsd { get; set; }
    public bool RejectUnknownEstimates { get; set; } = true;
}

public sealed record GenerationBudgetSnapshot(
    int ProviderAttempts,
    decimal CumulativeEstimatedCostUsd,
    decimal WorkspaceEstimatedCostUsd,
    decimal UserEstimatedCostUsd);

public sealed record GenerationBudgetDecision(
    bool Allowed,
    string? RejectionCode = null,
    string? RejectionMessage = null)
{
    public static GenerationBudgetDecision Allow() => new(true);
    public static GenerationBudgetDecision Reject(string code, string message) => new(false, code, message);
}

public static class GenerationBudgetGuardrail
{
    public static GenerationBudgetDecision Evaluate(
        GenerationCostEstimate estimate,
        GenerationBudgetSnapshot snapshot,
        GenerationBudgetOptions options)
    {
        if (!options.Enabled) return GenerationBudgetDecision.Allow();
        if (!estimate.IsKnown || !estimate.AmountUsd.HasValue)
        {
            return options.RejectUnknownEstimates
                ? GenerationBudgetDecision.Reject("COST_ESTIMATE_UNKNOWN", "The provider cost could not be estimated safely.")
                : GenerationBudgetDecision.Allow();
        }

        var amount = Math.Max(0m, estimate.AmountUsd.Value);
        if (options.MaxEstimatedCostPerJobUsd is { } perJob && amount > perJob)
            return GenerationBudgetDecision.Reject("MAX_ESTIMATED_COST_PER_JOB_EXCEEDED", "This generation exceeds the configured per-job provider safety ceiling.");
        if (options.MaxProviderAttemptsPerJob is { } attempts && snapshot.ProviderAttempts >= attempts)
            return GenerationBudgetDecision.Reject("MAX_PROVIDER_ATTEMPTS_EXCEEDED", "This generation has reached the configured provider-attempt safety ceiling.");
        if (options.MaxCumulativeEstimatedCostPerJobUsd is { } cumulative && snapshot.CumulativeEstimatedCostUsd + amount > cumulative)
            return GenerationBudgetDecision.Reject("MAX_CUMULATIVE_ESTIMATED_COST_EXCEEDED", "This generation has reached the configured cumulative provider safety ceiling.");
        if (options.WorkspaceInternalSafetyCeilingUsd is { } workspace && snapshot.WorkspaceEstimatedCostUsd + amount > workspace)
            return GenerationBudgetDecision.Reject("WORKSPACE_INTERNAL_SAFETY_CEILING_EXCEEDED", "This workspace has reached its internal provider safety ceiling.");
        if (options.UserInternalSafetyCeilingUsd is { } user && snapshot.UserEstimatedCostUsd + amount > user)
            return GenerationBudgetDecision.Reject("USER_INTERNAL_SAFETY_CEILING_EXCEEDED", "This user has reached the internal provider safety ceiling.");
        return GenerationBudgetDecision.Allow();
    }
}

public interface IGenerationBudgetService
{
    Task<GenerationProviderAttempt> BeginAttemptAsync(GenerationJob job, string provider, string? model, GenerationCostEstimate estimate, CancellationToken cancellationToken = default);
    Task CompleteAttemptAsync(GenerationProviderAttempt attempt, decimal? actualProviderCostUsd, bool actualCostKnown, GenerationProviderAttemptStatus status, string? failureCode = null, CancellationToken cancellationToken = default);
}

public sealed class GenerationBudgetService(
    TaslimDbContext db,
    IOptions<GenerationBudgetOptions> options,
    ILogger<GenerationBudgetService> logger) : IGenerationBudgetService
{
    private readonly GenerationBudgetOptions settings = options.Value;

    public async Task<GenerationProviderAttempt> BeginAttemptAsync(GenerationJob job, string provider, string? model, GenerationCostEstimate estimate, CancellationToken cancellationToken = default)
    {
        var priorAttempts = await db.GenerationProviderAttempts.AsNoTracking().Where(item => item.GenerationJobId == job.Id).ToListAsync(cancellationToken);
        var snapshot = new GenerationBudgetSnapshot(
            priorAttempts.Count(item => item.Status != GenerationProviderAttemptStatus.Rejected),
            priorAttempts.Where(item => item.EstimatedProviderCostKnown).Sum(item => item.EstimatedProviderCostUsd ?? 0m),
            await EstimatedCostForWorkspaceAsync(job.WorkspaceId, cancellationToken),
            await EstimatedCostForUserAsync(job.CreatedByUserId, cancellationToken));
        var decision = GenerationBudgetGuardrail.Evaluate(estimate, snapshot, settings);
        if (!decision.Allowed)
        {
            logger.LogWarning("Generation provider attempt rejected by budget guardrail. JobId={JobId}; Code={Code}", job.Id, decision.RejectionCode);
            throw new GenerationBudgetRejectedException(decision.RejectionCode!, decision.RejectionMessage!);
        }

        var attempt = new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            JobConcurrencyToken = job.ConcurrencyToken,
            IdempotencyKey = $"generation:{job.Id:N}:attempt:{snapshot.ProviderAttempts + 1}",
            Capability = job.JobType,
            AttemptNumber = snapshot.ProviderAttempts + 1,
            Provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim(),
            Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            Status = GenerationProviderAttemptStatus.Started,
            ResultClassification = "Started",
            EstimatedProviderCostUsd = estimate.AmountUsd,
            EstimatedProviderCostKnown = estimate.IsKnown && estimate.AmountUsd.HasValue,
            CostEstimateJson = estimate.ToJson(),
            PricingVersion = estimate.PricingVersion,
            Currency = estimate.Currency,
            FinalizationKey = $"generation:{job.Id:N}:attempt:{snapshot.ProviderAttempts + 1}",
            StartedAt = DateTime.UtcNow,
        };
        db.GenerationProviderAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task CompleteAttemptAsync(GenerationProviderAttempt attempt, decimal? actualProviderCostUsd, bool actualCostKnown, GenerationProviderAttemptStatus status, string? failureCode = null, CancellationToken cancellationToken = default)
    {
        if (attempt.Status is GenerationProviderAttemptStatus.Succeeded or GenerationProviderAttemptStatus.Failed or GenerationProviderAttemptStatus.Cancelled) return;
        attempt.Status = status;
        attempt.ActualProviderCostUsd = actualCostKnown ? actualProviderCostUsd : null;
        attempt.ActualProviderCostKnown = actualCostKnown && actualProviderCostUsd.HasValue;
        attempt.FailureCode = failureCode;
        attempt.ResultClassification = status.ToString();
        attempt.RateLimited = HasFailureToken(failureCode, "RATE_LIMIT");
        attempt.TimedOut = HasFailureToken(failureCode, "TIMEOUT") || HasFailureToken(failureCode, "TIMED_OUT");
        attempt.QualityControlRejected = HasFailureToken(failureCode, "QUALITY")
            || HasFailureToken(failureCode, "OUTPUT_INVALID")
            || HasFailureToken(failureCode, "CITATION_VALIDATION");
        attempt.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool HasFailureToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<decimal> EstimatedCostForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        (decimal)await db.GenerationProviderAttempts
            .Where(item => item.GenerationJob.WorkspaceId == workspaceId && item.EstimatedProviderCostKnown)
            .Select(item => (double?)(item.EstimatedProviderCostUsd ?? 0m))
            .SumAsync(cancellationToken);

    private async Task<decimal> EstimatedCostForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        (decimal)await db.GenerationProviderAttempts
            .Where(item => item.GenerationJob.CreatedByUserId == userId && item.EstimatedProviderCostKnown)
            .Select(item => (double?)(item.EstimatedProviderCostUsd ?? 0m))
            .SumAsync(cancellationToken);
}

public sealed class GenerationBudgetRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

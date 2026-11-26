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
    public decimal? UserEstimatedCostCeilingUsd { get; set; }
    public decimal? ProjectEstimatedCostCeilingUsd { get; set; }
    public decimal? ExpensiveGenerationWarningThresholdUsd { get; set; }
    public bool RequireConfirmationForExpensiveGeneration { get; set; } = true;
    public bool RequireConfirmationForUnknownEstimates { get; set; } = true;
    public bool RejectUnknownEstimates { get; set; } = true;
}

public sealed record GenerationBudgetSnapshot(
    int ProviderAttempts,
    decimal CumulativeEstimatedCostUsd,
    decimal WorkspaceEstimatedCostUsd,
    decimal UserEstimatedCostUsd,
    decimal ProjectEstimatedCostUsd = 0m);

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
        if (options.UserEstimatedCostCeilingUsd is { } userCap && snapshot.UserEstimatedCostUsd + amount > userCap)
            return GenerationBudgetDecision.Reject("USER_COST_CAP_EXCEEDED", "This user has reached the configured generation cost cap.");
        if (options.ProjectEstimatedCostCeilingUsd is { } projectCap && snapshot.ProjectEstimatedCostUsd + amount > projectCap)
            return GenerationBudgetDecision.Reject("PROJECT_COST_CAP_EXCEEDED", "This project has reached the configured generation cost cap.");
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

        var finalStatus = status;
        var finalActualCost = actualCostKnown ? actualProviderCostUsd : null;
        var finalActualCostKnown = actualCostKnown && actualProviderCostUsd.HasValue;
        var finalFailureCode = failureCode;
        var finalClassification = status.ToString();
        var finalRateLimited = HasFailureToken(failureCode, "RATE_LIMIT");
        var finalTimedOut = HasFailureToken(failureCode, "TIMEOUT") || HasFailureToken(failureCode, "TIMED_OUT");
        var finalQualityRejected = HasFailureToken(failureCode, "QUALITY")
            || HasFailureToken(failureCode, "OUTPUT_INVALID")
            || HasFailureToken(failureCode, "CITATION_VALIDATION");
        var finalCompletedAt = DateTime.UtcNow;

        // Atomic compare-and-set against the durable row. The UPDATE only lands while
        // the attempt is still Started, so concurrent finalizers - a late provider
        // success, a resilience-layer failure and a cancellation fence - elect exactly
        // one winner: on PostgreSQL the loser blocks on the row lock, its
        // Status = Started predicate is re-evaluated against the committed row and
        // matches zero rows, so a stale reader can never overwrite the committed
        // terminal status or misattribute the winning finalization's provider cost.
        // The previous read-check-write guarded only on the caller's in-memory copy,
        // which let two contexts that had each observed Started last-write-win the row.
        var affectedRows = await db.GenerationProviderAttempts
            .Where(item => item.Id == attempt.Id && item.Status == GenerationProviderAttemptStatus.Started)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, finalStatus)
                .SetProperty(item => item.ActualProviderCostUsd, finalActualCost)
                .SetProperty(item => item.ActualProviderCostKnown, finalActualCostKnown)
                .SetProperty(item => item.FailureCode, finalFailureCode)
                .SetProperty(item => item.ResultClassification, finalClassification)
                .SetProperty(item => item.RateLimited, finalRateLimited)
                .SetProperty(item => item.TimedOut, finalTimedOut)
                .SetProperty(item => item.QualityControlRejected, finalQualityRejected)
                .SetProperty(item => item.CompletedAt, finalCompletedAt),
                cancellationToken);

        if (affectedRows == 1)
        {
            // This finalization is the committed winner; mirror it onto the local copy
            // so the caller and any later flush of this context agree with the database.
            ApplyFinalization(attempt, finalStatus, finalActualCost, finalActualCostKnown, finalFailureCode, finalClassification, finalRateLimited, finalTimedOut, finalQualityRejected, finalCompletedAt);
        }
        else
        {
            // Explicit zero-row behaviour: another finalizer committed a terminal
            // outcome between this context's read and the compare-and-set. Adopt the
            // durable winner so the tracked copy - and the flush below - repeats the
            // committed outcome instead of overwriting it.
            var committed = await db.GenerationProviderAttempts
                .AsNoTracking()
                .Where(item => item.Id == attempt.Id)
                .Select(item => new
                {
                    item.Status,
                    item.ActualProviderCostUsd,
                    item.ActualProviderCostKnown,
                    item.FailureCode,
                    item.ResultClassification,
                    item.RateLimited,
                    item.TimedOut,
                    item.QualityControlRejected,
                    item.CompletedAt,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (committed is not null)
            {
                logger.LogInformation(
                    "Provider attempt finalization lost the finalization race; adopting the committed outcome. AttemptId={AttemptId}; JobId={JobId}; CommittedStatus={CommittedStatus}; SupersededStatus={SupersededStatus}",
                    attempt.Id, attempt.GenerationJobId, committed.Status, status);
                ApplyFinalization(attempt, committed.Status, committed.ActualProviderCostUsd, committed.ActualProviderCostKnown, committed.FailureCode, committed.ResultClassification, committed.RateLimited, committed.TimedOut, committed.QualityControlRejected, committed.CompletedAt);
            }
            else
            {
                // The row is not durable yet (an attempt its owning context has not
                // saved). Preserve the previous behaviour and let the flush below
                // persist this finalization together with the row.
                ApplyFinalization(attempt, finalStatus, finalActualCost, finalActualCostKnown, finalFailureCode, finalClassification, finalRateLimited, finalTimedOut, finalQualityRejected, finalCompletedAt);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ApplyFinalization(
        GenerationProviderAttempt attempt,
        GenerationProviderAttemptStatus status,
        decimal? actualProviderCostUsd,
        bool actualCostKnown,
        string? failureCode,
        string resultClassification,
        bool rateLimited,
        bool timedOut,
        bool qualityControlRejected,
        DateTime? completedAt)
    {
        attempt.Status = status;
        attempt.ActualProviderCostUsd = actualProviderCostUsd;
        attempt.ActualProviderCostKnown = actualCostKnown;
        attempt.FailureCode = failureCode;
        attempt.ResultClassification = resultClassification;
        attempt.RateLimited = rateLimited;
        attempt.TimedOut = timedOut;
        attempt.QualityControlRejected = qualityControlRejected;
        attempt.CompletedAt = completedAt;
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

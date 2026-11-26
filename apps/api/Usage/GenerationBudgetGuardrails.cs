using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    // Admission exposure: active attempts reserve estimates; settled attempts use known actuals.
    // This cannot guarantee a provider will not bill more than an estimate before that actual is reported.
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
    // For admission, known actual cost replaces the estimate; otherwise the estimate remains reserved.
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
        // The project row is the per-project admission mutex. Lock it before reading exposure,
        // then commit the attempt reservation in the same transaction. This works across API
        // processes and avoids relying on an in-process semaphore or a race-prone aggregate read.
        await using var projectAdmissionTransaction = await BeginProjectAdmissionTransactionAsync(
            job.ProjectId,
            job.WorkspaceId,
            cancellationToken);

        var priorAttempts = await db.GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == job.Id)
            .ToListAsync(cancellationToken);
        var billablePriorAttempts = priorAttempts
            .Where(item => item.Status != GenerationProviderAttemptStatus.Rejected)
            .ToList();

        var workspaceCost = await ProviderCostExposureAsync(
            db.GenerationProviderAttempts.Where(item => item.GenerationJob.WorkspaceId == job.WorkspaceId),
            cancellationToken);
        var userCost = await ProviderCostExposureAsync(
            db.GenerationProviderAttempts.Where(item => item.GenerationJob.CreatedByUserId == job.CreatedByUserId),
            cancellationToken);
        var projectCost = settings.Enabled && settings.ProjectEstimatedCostCeilingUsd.HasValue && job.ProjectId.HasValue
            ? await ProviderCostExposureAsync(
                db.GenerationProviderAttempts.Where(item =>
                    item.GenerationJob.WorkspaceId == job.WorkspaceId
                    && item.GenerationJob.ProjectId == job.ProjectId),
                cancellationToken)
            : 0m;

        var snapshot = new GenerationBudgetSnapshot(
            billablePriorAttempts.Count,
            billablePriorAttempts.Sum(ProviderCostExposure),
            workspaceCost,
            userCost,
            projectCost);
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
        if (projectAdmissionTransaction is not null)
            await projectAdmissionTransaction.CommitAsync(cancellationToken);
        return attempt;
    }

    public async Task CompleteAttemptAsync(GenerationProviderAttempt attempt, decimal? actualProviderCostUsd, bool actualCostKnown, GenerationProviderAttemptStatus status, string? failureCode = null, CancellationToken cancellationToken = default)
    {
        if (attempt.Status is GenerationProviderAttemptStatus.Succeeded or GenerationProviderAttemptStatus.Failed or GenerationProviderAttemptStatus.Cancelled) return;

        // Serialize settlement with project admissions so an already-known actual cost is
        // visible to the next admission instead of racing its exposure snapshot.
        IDbContextTransaction? projectAdmissionTransaction = null;
        if (settings.Enabled && settings.ProjectEstimatedCostCeilingUsd.HasValue)
        {
            var scope = await db.GenerationJobs.AsNoTracking()
                .Where(job => job.Id == attempt.GenerationJobId)
                .Select(job => new { job.ProjectId, job.WorkspaceId })
                .SingleOrDefaultAsync(cancellationToken);
            if (scope is not null)
                projectAdmissionTransaction = await BeginProjectAdmissionTransactionAsync(
                    scope.ProjectId,
                    scope.WorkspaceId,
                    cancellationToken);
        }

        await using (projectAdmissionTransaction)
        {
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
            if (projectAdmissionTransaction is not null)
                await projectAdmissionTransaction.CommitAsync(cancellationToken);
        }
    }

    private async Task<IDbContextTransaction?> BeginProjectAdmissionTransactionAsync(
        Guid? projectId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled || !settings.ProjectEstimatedCostCeilingUsd.HasValue || !projectId.HasValue)
            return null;

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var lockedRows = await db.Projects
                .Where(project => project.Id == projectId.Value && project.WorkspaceId == workspaceId)
                .ExecuteUpdateAsync(update => update.SetProperty(project => project.UpdatedAt, project => project.UpdatedAt), cancellationToken);
            if (lockedRows != 1)
                throw new InvalidOperationException("A generation project budget lock could not be acquired for the requested project.");
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private async Task<decimal> ProviderCostExposureAsync(
        IQueryable<GenerationProviderAttempt> attempts,
        CancellationToken cancellationToken)
    {
        var costs = attempts.AsNoTracking()
            .Where(item => item.Status != GenerationProviderAttemptStatus.Rejected)
            .Select(item => item.ActualProviderCostKnown && item.ActualProviderCostUsd.HasValue
                ? item.ActualProviderCostUsd.Value
                : item.EstimatedProviderCostKnown ? item.EstimatedProviderCostUsd ?? 0m : 0m)
            .Select(cost => cost < 0m ? 0m : cost);
        if (string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
            return (await costs.ToListAsync(cancellationToken)).Sum();
        return await costs.SumAsync(cancellationToken);
    }

    private static decimal ProviderCostExposure(GenerationProviderAttempt attempt) => ProviderCostExposure(
        attempt.ActualProviderCostUsd,
        attempt.ActualProviderCostKnown,
        attempt.EstimatedProviderCostUsd,
        attempt.EstimatedProviderCostKnown);

    private static decimal ProviderCostExposure(
        decimal? actualProviderCostUsd,
        bool actualProviderCostKnown,
        decimal? estimatedProviderCostUsd,
        bool estimatedProviderCostKnown)
    {
        if (actualProviderCostKnown && actualProviderCostUsd.HasValue)
            return Math.Max(0m, actualProviderCostUsd.Value);
        return estimatedProviderCostKnown
            ? Math.Max(0m, estimatedProviderCostUsd ?? 0m)
            : 0m;
    }

    private static bool HasFailureToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class GenerationBudgetRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

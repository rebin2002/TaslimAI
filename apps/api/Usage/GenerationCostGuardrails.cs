using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

public sealed record GenerationCostWarning(string Code, string Severity, string Message);

public sealed record GenerationCostCapState(
    string Scope,
    decimal? LimitUsd,
    decimal UsedUsd,
    bool UsageKnown,
    decimal? RemainingUsd,
    bool WouldExceed);

public sealed record GenerationCostPreflightResult(
    GenerationCostEstimate Estimate,
    bool Allowed,
    bool ConfirmationRequired,
    bool ConfirmationAccepted,
    GenerationCostCapState? WorkspaceCap,
    GenerationCostCapState? UserCap,
    GenerationCostCapState? ProjectCap,
    IReadOnlyList<GenerationCostWarning> Warnings,
    string? RejectionCode = null,
    string? RejectionMessage = null)
{
    /// <summary>
    /// A preview may be allowed by caps while still requiring an explicit user confirmation.
    /// Callers must use this property before queueing an expensive operation.
    /// </summary>
    public bool CanProceed => Allowed && (!ConfirmationRequired || ConfirmationAccepted);
}

public interface IGenerationCostGuardrailService
{
    Task<GenerationCostPreflightResult> EvaluateAsync(
        Guid userId,
        Guid workspaceId,
        Guid? projectId,
        GenerationCostEstimate estimate,
        bool confirmationAccepted = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates user-facing generation economics without exposing provider/model metadata.
/// This is an internal preflight service; it never charges a customer or creates an overage.
/// </summary>
public sealed class GenerationCostGuardrailService(
    TaslimDbContext db,
    Microsoft.Extensions.Options.IOptions<GenerationBudgetOptions> options) : IGenerationCostGuardrailService
{
    private readonly GenerationBudgetOptions settings = options.Value;

    public async Task<GenerationCostPreflightResult> EvaluateAsync(
        Guid userId,
        Guid workspaceId,
        Guid? projectId,
        GenerationCostEstimate estimate,
        bool confirmationAccepted = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedEstimate = Normalize(estimate);
        var userLimit = settings.UserEstimatedCostCeilingUsd ?? settings.UserInternalSafetyCeilingUsd;
        var workspaceExposure = await ExposureForAsync(
            settings.WorkspaceInternalSafetyCeilingUsd,
            row => row.WorkspaceId == workspaceId,
            includeCancelledPresentationSpend: true, cancellationToken);
        var userExposure = await ExposureForAsync(
            userLimit,
            row => row.UserId == userId,
            includeCancelledPresentationSpend: true, cancellationToken);
        var projectExposure = projectId.HasValue
            ? await ExposureForAsync(
                settings.ProjectEstimatedCostCeilingUsd,
                row => row.WorkspaceId == workspaceId && row.ProjectId == projectId,
                includeCancelledPresentationSpend: false, cancellationToken)
            : Exposure.Empty;

        var workspaceCap = CreateCap("workspace", settings.WorkspaceInternalSafetyCeilingUsd, workspaceExposure, normalizedEstimate);
        var userCap = CreateCap("user", userLimit, userExposure, normalizedEstimate);
        var projectCap = projectId.HasValue
            ? CreateCap("project", settings.ProjectEstimatedCostCeilingUsd, projectExposure, normalizedEstimate)
            : null;

        var warnings = new List<GenerationCostWarning>();
        if (!normalizedEstimate.IsKnown || !normalizedEstimate.AmountUsd.HasValue)
        {
            warnings.Add(new GenerationCostWarning(
                "COST_ESTIMATE_UNKNOWN",
                "warning",
                "The generation estimate is unavailable. Review the request before continuing."));
        }
        else if (settings.ExpensiveGenerationWarningThresholdUsd is { } threshold
            && normalizedEstimate.AmountUsd.Value >= Math.Max(0m, threshold))
        {
            warnings.Add(new GenerationCostWarning(
                "EXPENSIVE_GENERATION",
                "warning",
                "This generation may use a high amount of provider resources. Review the estimate before continuing."));
        }

        var rejectUnknownExposure = settings.Enabled && settings.RejectUnknownEstimates;
        if (rejectUnknownExposure && workspaceCap is { UsageKnown: false })
            return Reject(normalizedEstimate, confirmationAccepted, workspaceCap, userCap, projectCap, warnings, "WORKSPACE_COST_UNKNOWN", "This workspace has provider exposure that cannot be estimated safely.");
        if (rejectUnknownExposure && userCap is { UsageKnown: false })
            return Reject(normalizedEstimate, confirmationAccepted, workspaceCap, userCap, projectCap, warnings, "USER_COST_UNKNOWN", "This user has provider exposure that cannot be estimated safely.");
        if (rejectUnknownExposure && projectCap is { UsageKnown: false })
            return Reject(normalizedEstimate, confirmationAccepted, workspaceCap, userCap, projectCap, warnings, "PROJECT_COST_UNKNOWN", "This project has provider exposure that cannot be estimated safely.");

        var snapshot = new GenerationBudgetSnapshot(
            ProviderAttempts: 0,
            CumulativeEstimatedCostUsd: 0m,
            WorkspaceEstimatedCostUsd: workspaceExposure.KnownCostUsd,
            UserEstimatedCostUsd: userExposure.KnownCostUsd,
            ProjectEstimatedCostUsd: projectExposure.KnownCostUsd);
        var decision = GenerationBudgetGuardrail.Evaluate(normalizedEstimate, snapshot, settings);
        if (!decision.Allowed)
            return Reject(normalizedEstimate, confirmationAccepted, workspaceCap, userCap, projectCap, warnings, decision.RejectionCode!, decision.RejectionMessage!);

        var confirmationRequired = settings.Enabled && (
            (warnings.Any(item => item.Code == "EXPENSIVE_GENERATION") && settings.RequireConfirmationForExpensiveGeneration)
            || (warnings.Any(item => item.Code == "COST_ESTIMATE_UNKNOWN") && settings.RequireConfirmationForUnknownEstimates));
        return new GenerationCostPreflightResult(
            normalizedEstimate,
            true,
            confirmationRequired,
            confirmationAccepted,
            workspaceCap,
            userCap,
            projectCap,
            warnings);
    }

    private static GenerationCostPreflightResult Reject(
        GenerationCostEstimate estimate,
        bool confirmationAccepted,
        GenerationCostCapState? workspaceCap,
        GenerationCostCapState? userCap,
        GenerationCostCapState? projectCap,
        IReadOnlyList<GenerationCostWarning> warnings,
        string code,
        string message) => new(
            estimate,
            false,
            false,
            confirmationAccepted,
            workspaceCap,
            userCap,
            projectCap,
            warnings,
            code,
            message);

    private static GenerationCostEstimate Normalize(GenerationCostEstimate estimate)
    {
        if (!estimate.IsKnown || !estimate.AmountUsd.HasValue)
            return estimate with { IsKnown = false, AmountUsd = null };
        return estimate with { IsKnown = true, AmountUsd = decimal.Round(Math.Max(0m, estimate.AmountUsd.Value), 8, MidpointRounding.AwayFromZero) };
    }

    private static GenerationCostCapState? CreateCap(string scope, decimal? limit, Exposure exposure, GenerationCostEstimate estimate)
    {
        if (!limit.HasValue) return null;
        var normalizedLimit = Math.Max(0m, limit.Value);
        var amount = estimate.IsKnown && estimate.AmountUsd.HasValue ? estimate.AmountUsd.Value : 0m;
        return new GenerationCostCapState(
            scope,
            normalizedLimit,
            exposure.KnownCostUsd,
            exposure.UnknownCount == 0,
            normalizedLimit - exposure.KnownCostUsd,
            exposure.KnownCostUsd + amount > normalizedLimit);
    }

    private async Task<Exposure> ExposureForAsync(
        decimal? limit,
        System.Linq.Expressions.Expression<Func<UsageTransaction, bool>> predicate,
        bool includeCancelledPresentationSpend,
        CancellationToken cancellationToken)
    {
        if (!limit.HasValue) return Exposure.Empty;

        var reservations = db.UsageTransactions.AsNoTracking().Where(predicate);
        var amounts = reservations.Select(row => new
        {
            Cost = row.Status == UsageTransactionStatus.Pending
                ? row.EstimatedProviderCostUsd ?? 0m
                : (row.Status == UsageTransactionStatus.Completed || row.Status == UsageTransactionStatus.Failed || row.Status == UsageTransactionStatus.Refunded) && row.ProviderCostKnown
                    ? row.ProviderCostUsd : 0m,
            Unknown = row.Status == UsageTransactionStatus.Pending
                ? !row.EstimatedProviderCostUsd.HasValue
                : (row.Status == UsageTransactionStatus.Completed || row.Status == UsageTransactionStatus.Failed || row.Status == UsageTransactionStatus.Refunded) && !row.ProviderCostKnown,
        });

        if (includeCancelledPresentationSpend)
            amounts = amounts.Concat(CancelledPresentationSpend.ForCancelledReservations(db, reservations)
                .Select(cost => new { Cost = cost, Unknown = false }));

        if (db.Database.IsNpgsql())
        {
            var total = await amounts.GroupBy(row => 1)
                .Select(group => new { Cost = group.Sum(row => row.Cost), Unknown = group.Count(row => row.Unknown) })
                .SingleOrDefaultAsync(cancellationToken);
            return total is null ? Exposure.Empty : new Exposure(total.Cost, total.Unknown);
        }

        // SQLite has no exact decimal SUM. Stream its rows to keep the ledger precision
        // without allocating an entire user's cross-workspace history.
        decimal cost = 0m;
        int unknown = 0;
        await foreach (var row in amounts.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            cost += row.Cost;
            if (row.Unknown) unknown++;
        }
        return new Exposure(cost, unknown);
    }

    private sealed record Exposure(decimal KnownCostUsd, int UnknownCount)
    {
        public static Exposure Empty { get; } = new(0m, 0);
    }
}

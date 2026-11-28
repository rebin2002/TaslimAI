using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

internal static class CancelledPresentationSpend
{
    // One amount per provider attempt. Cancellation leaves the customer reservation at zero.
    internal static IQueryable<decimal> ReportedUsd(IQueryable<GenerationProviderAttempt> attempts) =>
        attempts.Where(attempt => attempt.GenerationJob.JobType == GenerationJobTypes.PresentationGenerate
                && attempt.GenerationJob.Status == GenerationJobStatus.Cancelled
                && attempt.Status == GenerationProviderAttemptStatus.Cancelled
                && attempt.ActualProviderCostKnown && attempt.ActualProviderCostUsd.HasValue
                && attempt.ActualProviderCostUsd >= 0m && attempt.Currency == UsageCurrencies.Usd)
            .Select(attempt => attempt.ActualProviderCostUsd!.Value);

    internal static IQueryable<decimal> ForCancelledReservations(
        TaslimDbContext db, IQueryable<UsageTransaction> reservations)
    {
        var cancelled = reservations.Where(row => row.Feature == UsageFeature.Presentation
            && row.Status == UsageTransactionStatus.Cancelled
            && row.GenerationJobId.HasValue
            && row.RequestId == "generation:" + row.GenerationJobId.Value.ToString().Replace("-", "").ToLower());
        // The ledger key includes both feature and request ID. An alias in another feature may have a different date.
        return ReportedUsd(db.GenerationProviderAttempts.AsNoTracking().Where(attempt =>
            cancelled.Any(row => row.GenerationJobId == attempt.GenerationJobId
                && row.WorkspaceId == attempt.GenerationJob.WorkspaceId && row.UserId == attempt.GenerationJob.CreatedByUserId)));
    }
}

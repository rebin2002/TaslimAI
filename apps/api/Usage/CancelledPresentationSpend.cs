using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

internal static class CancelledPresentationSpend
{
    // One amount per provider attempt. Cancellation leaves the customer reservation at zero.
    internal static IQueryable<decimal> ReportedUsd(IQueryable<GenerationProviderAttempt> attempts) =>
        attempts.Where(attempt => attempt.GenerationJob.JobType == GenerationJobTypes.PresentationGenerate
                && attempt.Status == GenerationProviderAttemptStatus.Cancelled
                && attempt.ActualProviderCostKnown && attempt.ActualProviderCostUsd.HasValue
                && attempt.ActualProviderCostUsd >= 0m && attempt.Currency == UsageCurrencies.Usd)
            .Select(attempt => attempt.ActualProviderCostUsd!.Value);

    internal static IQueryable<decimal> ForCancelledReservations(
        TaslimDbContext db, IQueryable<UsageTransaction> reservations)
    {
        var cancelled = reservations.Where(row => row.Status == UsageTransactionStatus.Cancelled);
        // EXISTS keeps each attempt singular even if more than one historical ledger row refers to it.
        return ReportedUsd(db.GenerationProviderAttempts.AsNoTracking().Where(attempt =>
            cancelled.Any(row => row.GenerationJobId == attempt.GenerationJobId
                && row.WorkspaceId == attempt.GenerationJob.WorkspaceId && row.UserId == attempt.GenerationJob.CreatedByUserId)));
    }
}

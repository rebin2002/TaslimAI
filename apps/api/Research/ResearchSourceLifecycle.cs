using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Research;

internal static class ResearchSourceLifecycle
{
    public static async Task<bool> ClearIfClaimOwnedAsync(
        TaslimDbContext db,
        GenerationJob job,
        CancellationToken cancellationToken)
    {
        if (!await OwnsRunningClaimAsync(db, job, cancellationToken)) return false;
        await ClearAsync(db, job.Id, job.ConcurrencyToken, cancellationToken);
        return true;
    }

    public static async Task ClearAsync(
        TaslimDbContext db,
        Guid generationJobId,
        Guid concurrencyToken,
        CancellationToken cancellationToken)
    {
        await db.ResearchEvidence
            .Where(item => item.GenerationJobId == generationJobId
                && db.GenerationJobs.Any(job => job.Id == generationJobId
                    && job.Status == GenerationJobStatus.Running
                    && job.ConcurrencyToken == concurrencyToken))
            .ExecuteDeleteAsync(cancellationToken);
        await db.ResearchSources
            .Where(item => item.GenerationJobId == generationJobId
                && db.GenerationJobs.Any(job => job.Id == generationJobId
                    && job.Status == GenerationJobStatus.Running
                    && job.ConcurrencyToken == concurrencyToken))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static Task<bool> OwnsRunningClaimAsync(
        TaslimDbContext db,
        GenerationJob job,
        CancellationToken cancellationToken) =>
        db.GenerationJobs.AsNoTracking().AnyAsync(item =>
            item.Id == job.Id
            && item.Status == GenerationJobStatus.Running
            && item.ConcurrencyToken == job.ConcurrencyToken,
            cancellationToken);
}

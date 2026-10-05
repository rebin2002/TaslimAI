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
        await ClearAsync(db, job.Id, cancellationToken);
        return true;
    }

    public static async Task ClearAsync(
        TaslimDbContext db,
        Guid generationJobId,
        CancellationToken cancellationToken)
    {
        await db.ResearchEvidence
            .Where(item => item.GenerationJobId == generationJobId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ResearchSources
            .Where(item => item.GenerationJobId == generationJobId)
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

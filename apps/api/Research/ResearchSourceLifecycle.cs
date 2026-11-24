using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Research;

internal static class ResearchSourceLifecycle
{
    public static Task<bool> ClearIfClaimOwnedAsync(
        TaslimDbContext db,
        GenerationJob job,
        CancellationToken cancellationToken)
        => TryRunUnderClaimFenceAsync(
            db,
            job,
            token => ClearAsync(db, job.Id, token, cancellationToken),
            cancellationToken);

    public static Task<bool> TryPersistUnderClaimAsync(
        TaslimDbContext db,
        GenerationJob job,
        Func<CancellationToken, Task> persistAsync,
        CancellationToken cancellationToken)
        => TryRunUnderClaimFenceAsync(db, job, async token =>
        {
            await ClearAsync(db, job.Id, token, cancellationToken);
            await persistAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private static async Task<bool> TryRunUnderClaimFenceAsync(
        TaslimDbContext db,
        GenerationJob job,
        Func<Guid, Task> workAsync,
        CancellationToken cancellationToken)
    {
        var concurrencyToken = job.ConcurrencyToken;
        if (db.Database.CurrentTransaction is not null)
        {
            if (!await TryAcquireClaimFenceAsync(db, job, concurrencyToken, cancellationToken)) return false;
            await workAsync(concurrencyToken);
            return true;
        }
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (!await TryAcquireClaimFenceAsync(db, job, concurrencyToken, cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
                await workAsync(concurrencyToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch
            {
                // A cancelled operation token must not mask the original failure: rollback runs on
                // a dedicated token so cleanup completes even when cancellation triggered this
                // path. If the connection itself is dead, PostgreSQL aborts the transaction when
                // the disposed connection returns to the pool, so no partial state can persist.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
                throw;
            }
        });
    }

    // The fence is a compare-and-set against the claim row and the first database access inside the
    // transaction: a no-op UPDATE that only matches while the job is Running with our token. It takes
    // the row lock (PostgreSQL) or the write lock (SQLite) that is held until commit or rollback, so
    // the claim can never be transferred between the ownership decision and cleanup or staging.
    // DELETE affected-row counts are deliberately not treated as an ownership signal, because a valid
    // empty source set legitimately deletes zero rows.
    private static async Task<bool> TryAcquireClaimFenceAsync(
        TaslimDbContext db,
        GenerationJob job,
        Guid concurrencyToken,
        CancellationToken cancellationToken)
    {
        var affected = await db.GenerationJobs
            .Where(item => item.Id == job.Id
                && item.Status == GenerationJobStatus.Running
                && item.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.RetryCount, item => item.RetryCount),
                cancellationToken);
        return affected == 1;
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
}

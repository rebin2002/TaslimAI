using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Email;

public sealed class PasswordResetOutboxService(
    TaslimDbContext db,
    TimeProvider timeProvider)
{
    public async Task<bool> TryQueueAsync(
        Guid userId,
        TimeSpan cooldown,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entry = await db.PasswordResetEmailOutbox
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);

        if (entry is null)
        {
            entry = new PasswordResetEmailOutboxEntry
            {
                UserId = userId,
                LastRequestedAt = now,
                NextAttemptAt = now,
                Status = PasswordResetDeliveryStates.Pending,
            };
            db.PasswordResetEmailOutbox.Add(entry);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                db.Entry(entry).State = EntityState.Detached;
                // A concurrent API replica may have inserted the unique user row.
                // Only treat that as a cooldown collision when it is now observable;
                // unrelated persistence errors must fail the request closed.
                if (await db.PasswordResetEmailOutbox.AnyAsync(item => item.UserId == userId, cancellationToken))
                    return false;
                throw;
            }
        }

        if (entry.LastRequestedAt > now.Subtract(cooldown))
            return false;

        entry.LastRequestedAt = now;
        entry.NextAttemptAt = now;
        entry.LeaseExpiresAt = null;
        entry.Status = PasswordResetDeliveryStates.Pending;
        entry.AttemptCount = 0;
        entry.LastFailureCode = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>
    /// Reports whether a delivery claim taken earlier no longer owns the row.
    /// <c>LastRequestedAt</c> is both the request epoch and a concurrency token, so
    /// every accepted forgot request - and every completed reset through
    /// <see cref="CancelForUserAsync"/> - moves it and thereby supersedes claims taken
    /// for an older request. The worker consults this before generating and mailing a
    /// token, so an obsolete claim cannot deliver a reset link the user has already
    /// moved past.
    /// </summary>
    public async Task<bool> IsClaimSupersededAsync(
        Guid userId,
        DateTime claimedLastRequestedAt,
        CancellationToken cancellationToken)
    {
        var current = await db.PasswordResetEmailOutbox
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.LastRequestedAt, item.Status })
            .SingleOrDefaultAsync(cancellationToken);

        // No row means the account or its delivery state is gone: treat the claim as
        // superseded so nothing is mailed for a request that can no longer be honoured.
        if (current is null)
            return true;

        // A row that left the claimable states was retired by a completed reset or a
        // terminal outcome, so the claim is obsolete even when the epoch still matches.
        if (current.Status is not (PasswordResetDeliveryStates.Pending or PasswordResetDeliveryStates.Processing))
            return true;

        return current.LastRequestedAt != claimedLastRequestedAt;
    }

    /// <summary>
    /// Retires an outstanding recovery email for an account that has already
    /// completed a password reset. A row in <c>Pending</c> or <c>Processing</c>
    /// state means a message is still queued or being delivered for a request
    /// that no longer needs to be honoured, so it becomes <c>Cancelled</c> and the
    /// worker's claim filter can no longer select it. Clearing <c>LeaseExpiresAt</c>
    /// also makes an in-flight delivery's completion save fail closed, because that
    /// column is a concurrency token: the newer state wins and no token generated
    /// before this call is reported as delivered.
    /// Deliberately does not swallow non-concurrency persistence faults; the caller
    /// decides how to report them so a storage outage cannot leak into a response
    /// that already succeeded.
    /// </summary>
    public async Task<bool> CancelForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var entry = await db.PasswordResetEmailOutbox
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (entry is null
            || entry.Status is not (PasswordResetDeliveryStates.Pending or PasswordResetDeliveryStates.Processing))
            return false;

        entry.Status = PasswordResetDeliveryStates.Cancelled;
        entry.LeaseExpiresAt = null;
        entry.LastFailureCode = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent request or worker moved the row first. Nothing is
            // cancelled by this call; the caller's recovery already completed.
            db.Entry(entry).State = EntityState.Detached;
            return false;
        }
    }
}

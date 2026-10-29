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
}

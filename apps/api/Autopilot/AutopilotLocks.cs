using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

public sealed record AutopilotLockHandle(string ResourceKey, string OwnerToken, long FencingToken, bool Acquired, string? HeldBy, DateTime? ExpiresAt)
{
    public static AutopilotLockHandle NotAcquired(string resourceKey, string? heldBy, DateTime? expiresAt) =>
        new(resourceKey, string.Empty, 0, false, heldBy, expiresAt);

    public bool IsAcquired => Acquired && OwnerToken.Length > 0;
}

/// <summary>
/// Exclusive, database-backed lock with fencing tokens. Duplicate completion
/// events can never launch duplicate integrations, releases, or waves because
/// only the holder of the lock (and the matching fencing token) may write the
/// downstream transition.
/// </summary>
public interface IAutopilotLockService
{
    Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default);
}

public sealed class EfAutopilotLockService(TaslimDbContext db, AutopilotOptions options) : IAutopilotLockService
{
    public static string WaveResource(string waveKey) => $"autopilot:wave:{waveKey}";

    public static string EventResource(string idempotencyKey) => $"autopilot:event:{idempotencyKey}";

    public static string ReleaseResource(string waveKey) => $"autopilot:release:{waveKey}";

    public async Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var leaseMinutes = Math.Max(1, options.LockLeaseMinutes);
        var ownerToken = Guid.NewGuid().ToString("N");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await db.AutopilotLocks.AsNoTracking()
                .FirstOrDefaultAsync(item => item.ResourceKey == resourceKey, cancellationToken);

            if (existing is null)
            {
                var candidate = new AutopilotLock
                {
                    Id = Guid.NewGuid(),
                    ResourceKey = resourceKey,
                    OwnerToken = ownerToken,
                    FencingToken = 1,
                    AcquiredAt = now,
                    ExpiresAt = now.AddMinutes(leaseMinutes),
                    HeldBy = heldBy,
                };
                db.AutopilotLocks.Add(candidate);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                    return new AutopilotLockHandle(resourceKey, ownerToken, candidate.FencingToken, true, heldBy, candidate.ExpiresAt);
                }
                catch (DbUpdateException)
                {
                    // A concurrent writer won the insert race. Detach and retry the read path.
                    db.Entry(candidate).State = EntityState.Detached;
                    continue;
                }
            }

            if (existing.ExpiresAt > now)
                return AutopilotLockHandle.NotAcquired(resourceKey, existing.HeldBy, existing.ExpiresAt);

            var expectedExpiry = existing.ExpiresAt;
            var expectedToken = existing.FencingToken;
            var nextToken = existing.FencingToken + 1;
            var updated = await db.AutopilotLocks
                .Where(item => item.ResourceKey == resourceKey
                    && item.FencingToken == expectedToken
                    && item.ExpiresAt == expectedExpiry)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.OwnerToken, ownerToken)
                    .SetProperty(item => item.FencingToken, nextToken)
                    .SetProperty(item => item.AcquiredAt, now)
                    .SetProperty(item => item.ExpiresAt, now.AddMinutes(leaseMinutes))
                    .SetProperty(item => item.HeldBy, heldBy), cancellationToken);

            if (updated == 1)
                return new AutopilotLockHandle(resourceKey, ownerToken, nextToken, true, heldBy, now.AddMinutes(leaseMinutes));

            return AutopilotLockHandle.NotAcquired(resourceKey, existing.HeldBy, existing.ExpiresAt);
        }

        return AutopilotLockHandle.NotAcquired(resourceKey, null, null);
    }

    public async Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
    {
        if (!handle.IsAcquired) return false;
        var released = await db.AutopilotLocks
            .Where(item => item.ResourceKey == handle.ResourceKey
                && item.OwnerToken == handle.OwnerToken
                && item.FencingToken == handle.FencingToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.OwnerToken, string.Empty)
                .SetProperty(item => item.ExpiresAt, DateTime.UtcNow), cancellationToken);
        return released == 1;
    }
}
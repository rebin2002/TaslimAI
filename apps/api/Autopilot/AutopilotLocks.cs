using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

public sealed record AutopilotLockHandle(string ResourceKey, string OwnerToken, long FencingToken, bool Acquired, string? HeldBy, DateTime? ExpiresAt)
{
    public DateTime? LeaseServerTimeUtc { get; init; }

    public static AutopilotLockHandle NotAcquired(string resourceKey, string? heldBy, DateTime? expiresAt) =>
        new(resourceKey, string.Empty, 0, false, heldBy, expiresAt);

    public bool IsAcquired => Acquired && OwnerToken.Length > 0;
}

/// <summary>Exclusive database lock with a monotonically increasing fencing token.</summary>
public interface IAutopilotLockService
{
    Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renew only a still-live lease owned by the exact owner/fencing-token pair.
/// Expired handles must reacquire and receive a strictly higher fencing token.
/// </summary>
public interface IAutopilotLockLeaseRenewalService
{
    Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default);
}

public sealed class EfAutopilotLockService(
    TaslimDbContext db,
    AutopilotOptions options,
    TimeProvider? timeProvider = null) : IAutopilotLockService, IAutopilotLockLeaseRenewalService
{
    private TimeProvider Clock => timeProvider ?? TimeProvider.System;

    public static string WaveResource(string waveKey) => $"autopilot:wave:{waveKey}";

    public static string EventResource(string idempotencyKey) => $"autopilot:event:{idempotencyKey}";

    public static string ReleaseResource(string waveKey) => $"autopilot:release:{waveKey}";

    public async Task<AutopilotLockHandle> AcquireAsync(string resourceKey, string heldBy, CancellationToken cancellationToken = default)
    {
        if (db.Database.ProviderName is not ("Npgsql.EntityFrameworkCore.PostgreSQL" or "Microsoft.EntityFrameworkCore.Sqlite"))
            return AutopilotLockHandle.NotAcquired(resourceKey, null, Clock.GetUtcNow().UtcDateTime);

        var leaseMinutes = Math.Max(1, options.LockLeaseMinutes);
        var ownerToken = Guid.NewGuid().ToString("N");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await db.AutopilotLocks.AsNoTracking()
                .FirstOrDefaultAsync(item => item.ResourceKey == resourceKey, cancellationToken);

            if (existing is null)
            {
                var id = Guid.NewGuid();
                int inserted;
                if (string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
                {
                    inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "AutopilotLocks" ("Id", "ResourceKey", "OwnerToken", "FencingToken", "AcquiredAt", "ExpiresAt", "HeldBy")
                        VALUES ({id}, {resourceKey}, {ownerToken}, 1, clock_timestamp(), clock_timestamp() + make_interval(mins => {leaseMinutes}), {heldBy})
                        ON CONFLICT ("ResourceKey") DO NOTHING
                        """, cancellationToken);
                }
                else
                {
                    inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "AutopilotLocks" ("Id", "ResourceKey", "OwnerToken", "FencingToken", "AcquiredAt", "ExpiresAt", "HeldBy")
                        VALUES ({id}, {resourceKey}, {ownerToken}, 1,
                            strftime('%Y-%m-%d %H:%M:%f', 'now'),
                            strftime('%Y-%m-%d %H:%M:%f', 'now', '+' || {leaseMinutes} || ' minutes'),
                            {heldBy})
                        ON CONFLICT ("ResourceKey") DO NOTHING
                        """, cancellationToken);
                }

                if (inserted == 1)
                {
                    return await RenewAsync(
                        new AutopilotLockHandle(resourceKey, ownerToken, 1, true, heldBy, null),
                        cancellationToken);
                }

                // Another first-row contender won the unique-key insert race.
                continue;
            }

            // Re-read the database clock after the row read: this query can wait
            // behind another writer, so the earlier `now` is not authoritative.
            var observedNow = await ReadDatabaseUtcNowAsync(cancellationToken);
            if (existing.ExpiresAt > observedNow)
                return AutopilotLockHandle.NotAcquired(resourceKey, existing.HeldBy, existing.ExpiresAt);

            // Never allow long overflow to wrap the fencing token and revive an old token value.
            if (existing.FencingToken <= 0 || existing.FencingToken == long.MaxValue)
                return AutopilotLockHandle.NotAcquired(resourceKey, existing.HeldBy, existing.ExpiresAt);

            var expectedToken = existing.FencingToken;
            var nextToken = existing.FencingToken + 1;
            int updated;
            if (string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AutopilotLocks"
                    SET "OwnerToken" = {ownerToken},
                        "FencingToken" = "FencingToken" + 1,
                        "AcquiredAt" = clock_timestamp(),
                        "ExpiresAt" = clock_timestamp() + make_interval(mins => {leaseMinutes}),
                        "HeldBy" = {heldBy}
                    WHERE "ResourceKey" = {resourceKey}
                      AND "FencingToken" = {expectedToken}
                      AND "FencingToken" > 0
                      AND "FencingToken" < 9223372036854775807
                      AND "ExpiresAt" <= clock_timestamp()
                    """, cancellationToken);
            }
            else
            {
                updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AutopilotLocks"
                    SET "OwnerToken" = {ownerToken},
                        "FencingToken" = "FencingToken" + 1,
                        "AcquiredAt" = strftime('%Y-%m-%d %H:%M:%f', 'now'),
                        "ExpiresAt" = strftime('%Y-%m-%d %H:%M:%f', 'now', '+' || {leaseMinutes} || ' minutes'),
                        "HeldBy" = {heldBy}
                    WHERE "ResourceKey" = {resourceKey}
                      AND "FencingToken" = {expectedToken}
                      AND "FencingToken" > 0
                      AND "FencingToken" < 9223372036854775807
                      AND julianday("ExpiresAt") <= julianday('now')
                    """, cancellationToken);
            }

            if (updated == 1)
            {
                var newExpiry = await db.AutopilotLocks.AsNoTracking()
                    .Where(item => item.ResourceKey == resourceKey
                        && item.OwnerToken == ownerToken
                        && item.FencingToken == nextToken)
                    .Select(item => (DateTime?)item.ExpiresAt)
                    .FirstOrDefaultAsync(cancellationToken);
                var leaseServerTime = await ReadDatabaseUtcNowAsync(cancellationToken);
                if (newExpiry.HasValue && newExpiry.Value > leaseServerTime)
                    return new AutopilotLockHandle(resourceKey, ownerToken, nextToken, true, heldBy, newExpiry.Value)
                    {
                        LeaseServerTimeUtc = leaseServerTime,
                    };
            }

            // The atomic server-clock predicate may lose to a competing owner;
            // retry one fresh read/update rather than making an expired lease
            // unavailable merely because it expired during the first attempt.
        }

        return AutopilotLockHandle.NotAcquired(resourceKey, null, null);
    }

    public async Task<AutopilotLockHandle> RenewAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
    {
        if (!handle.IsAcquired)
            return AutopilotLockHandle.NotAcquired(handle.ResourceKey, handle.HeldBy, handle.ExpiresAt);

        var leaseMinutes = Math.Max(1, options.LockLeaseMinutes);
        int updated;
        if (string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            // Use the database wall clock at statement execution, not an application
            // timestamp captured before a possible row-lock wait. clock_timestamp()
            // prevents a paused/stale caller from reviving a lease that expired while
            // this UPDATE was blocked.
            updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AutopilotLocks"
                SET "ExpiresAt" = GREATEST("ExpiresAt", clock_timestamp() + make_interval(mins => {leaseMinutes}))
                WHERE "ResourceKey" = {handle.ResourceKey}
                  AND "OwnerToken" = {handle.OwnerToken}
                  AND "FencingToken" = {handle.FencingToken}
                  AND "ExpiresAt" > clock_timestamp()
                """, cancellationToken);
        }
        else if (string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            // SQLite's julianday('now') is evaluated by the database for both the
            // liveness predicate and expiry calculation. Keep the existing later
            // expiration if concurrent renewals race for the same owner/token.
            updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AutopilotLocks"
                SET "ExpiresAt" = CASE
                    WHEN julianday("ExpiresAt") >= julianday('now', '+' || {leaseMinutes} || ' minutes')
                    THEN "ExpiresAt"
                    ELSE strftime('%Y-%m-%d %H:%M:%f', 'now', '+' || {leaseMinutes} || ' minutes')
                END
                WHERE "ResourceKey" = {handle.ResourceKey}
                  AND "OwnerToken" = {handle.OwnerToken}
                  AND "FencingToken" = {handle.FencingToken}
                  AND julianday("ExpiresAt") > julianday('now')
                """, cancellationToken);
        }
        else
        {
            // Unknown relational providers have no reviewed server-clock expression.
            // Fail closed instead of pretending a local clock is a fencing guarantee.
            return AutopilotLockHandle.NotAcquired(handle.ResourceKey, null, null);
        }

        if (updated != 1)
        {
            var observed = await db.AutopilotLocks.AsNoTracking()
                .Where(item => item.ResourceKey == handle.ResourceKey)
                .Select(item => new { item.HeldBy, item.ExpiresAt })
                .FirstOrDefaultAsync(cancellationToken);
            return AutopilotLockHandle.NotAcquired(handle.ResourceKey, observed?.HeldBy, observed?.ExpiresAt);
        }

        var renewedExpiry = await db.AutopilotLocks.AsNoTracking()
            .Where(item => item.ResourceKey == handle.ResourceKey
                && item.OwnerToken == handle.OwnerToken
                && item.FencingToken == handle.FencingToken)
            .Select(item => (DateTime?)item.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);
        var leaseServerTime = await ReadDatabaseUtcNowAsync(cancellationToken);
        if (!renewedExpiry.HasValue || renewedExpiry.Value <= leaseServerTime)
            return AutopilotLockHandle.NotAcquired(handle.ResourceKey, null, renewedExpiry);

        return handle with { ExpiresAt = renewedExpiry.Value, LeaseServerTimeUtc = leaseServerTime };
    }

    public async Task<bool> ReleaseAsync(AutopilotLockHandle handle, CancellationToken cancellationToken = default)
    {
        if (!handle.IsAcquired) return false;

        int released;
        if (string.Equals(db.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            released = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AutopilotLocks"
                SET "OwnerToken" = '', "ExpiresAt" = clock_timestamp()
                WHERE "ResourceKey" = {handle.ResourceKey}
                  AND "OwnerToken" = {handle.OwnerToken}
                  AND "FencingToken" = {handle.FencingToken}
                  AND "ExpiresAt" > clock_timestamp()
                """, cancellationToken);
        }
        else if (string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            released = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "AutopilotLocks"
                SET "OwnerToken" = '', "ExpiresAt" = strftime('%Y-%m-%d %H:%M:%f', 'now')
                WHERE "ResourceKey" = {handle.ResourceKey}
                  AND "OwnerToken" = {handle.OwnerToken}
                  AND "FencingToken" = {handle.FencingToken}
                  AND julianday("ExpiresAt") > julianday('now')
                """, cancellationToken);
        }
        else
        {
            return false;
        }

        return released == 1;
    }

    private async Task<DateTime> ReadDatabaseUtcNowAsync(CancellationToken cancellationToken)
    {
        var commandText = db.Database.ProviderName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => "SELECT clock_timestamp()",
            "Microsoft.EntityFrameworkCore.Sqlite" => "SELECT strftime('%Y-%m-%dT%H:%M:%fZ', 'now')",
            _ => null,
        };
        if (commandText is null)
            throw new NotSupportedException("The configured database provider has no reviewed server-clock query for Autopilot lock operations.");

        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is DateTime databaseDateTime)
                return DateTime.SpecifyKind(databaseDateTime, DateTimeKind.Utc);

            if (value is string databaseText
                && DateTimeOffset.TryParse(
                    databaseText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var databaseDateTimeOffset))
                return databaseDateTimeOffset.UtcDateTime;

            throw new InvalidOperationException("The database did not return a parseable UTC timestamp for the Autopilot lock service.");
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }
}

/// <summary>
/// Renews a held lease on a bounded timer while the provider request is in flight.
/// Any missing renewal support, expiry, token change, or store error cancels the
/// linked provider request and makes the caller treat its outcome as ambiguous.
/// </summary>
public sealed class AutopilotLockLeaseHeartbeat : IAsyncDisposable
{
    private readonly IAutopilotLockLeaseRenewalService renewalService;
    private readonly TimeSpan renewalInterval;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource lost = new();
    private readonly SemaphoreSlim renewalGate = new(1, 1);
    private readonly Task renewalLoop;
    private AutopilotLockHandle currentHandle;
    private int disposed;

    private AutopilotLockLeaseHeartbeat(
        IAutopilotLockLeaseRenewalService renewalService,
        AutopilotLockHandle renewedHandle,
        TimeSpan renewalInterval,
        ILogger logger)
    {
        this.renewalService = renewalService;
        currentHandle = renewedHandle;
        this.renewalInterval = renewalInterval > TimeSpan.Zero ? renewalInterval : TimeSpan.FromSeconds(1);
        this.logger = logger;
        renewalLoop = RunRenewalLoopAsync();
    }

    public bool IsLost => lost.IsCancellationRequested;

    public CancellationToken LeaseLostToken => lost.Token;

    public AutopilotLockHandle CurrentHandle => currentHandle;

    public static async Task<AutopilotLockLeaseHeartbeat?> StartAsync(
        IAutopilotLockService locks,
        AutopilotLockHandle handle,
        TimeSpan renewalInterval,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (locks is not IAutopilotLockLeaseRenewalService renewalService || !handle.IsAcquired)
            return null;

        AutopilotLockHandle renewed;
        try
        {
            renewed = await renewalService.RenewAsync(handle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Autopilot completion/launch fence could not be renewed before provider authorization. Resource={ResourceKey}; FencingToken={FencingToken}",
                handle.ResourceKey, handle.FencingToken);
            return null;
        }

        if (!renewed.IsAcquired || renewed.FencingToken != handle.FencingToken
            || !string.Equals(renewed.OwnerToken, handle.OwnerToken, StringComparison.Ordinal))
            return null;

        return new AutopilotLockLeaseHeartbeat(renewalService, renewed, renewalInterval, logger);
    }

    public async Task<bool> RenewNowAsync(CancellationToken cancellationToken = default)
    {
        if (IsLost) return false;
        await renewalGate.WaitAsync(cancellationToken);
        try
        {
            if (IsLost) return false;
            var renewed = await renewalService.RenewAsync(currentHandle, cancellationToken);
            if (!renewed.IsAcquired
                || renewed.FencingToken != currentHandle.FencingToken
                || !string.Equals(renewed.OwnerToken, currentHandle.OwnerToken, StringComparison.Ordinal))
            {
                MarkLost();
                return false;
            }

            currentHandle = renewed;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Autopilot completion/launch fence renewal failed. Resource={ResourceKey}; FencingToken={FencingToken}",
                currentHandle.ResourceKey, currentHandle.FencingToken);
            MarkLost();
            return false;
        }
        finally
        {
            renewalGate.Release();
        }
    }

    public async Task StopRenewalLoopAsync()
    {
        stop.Cancel();
        try
        {
            await renewalLoop;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task RunRenewalLoopAsync()
    {
        using var timer = new PeriodicTimer(renewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (!await RenewNowAsync(stop.Token))
                    return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException exception)
        {
            logger.LogError(exception,
                "Autopilot completion/launch fence renewal loop was canceled unexpectedly. Resource={ResourceKey}; FencingToken={FencingToken}",
                currentHandle.ResourceKey, currentHandle.FencingToken);
            MarkLost();
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Autopilot completion/launch fence renewal loop failed unexpectedly. Resource={ResourceKey}; FencingToken={FencingToken}",
                currentHandle.ResourceKey, currentHandle.FencingToken);
            MarkLost();
        }
    }

    private void MarkLost()
    {
        if (!lost.IsCancellationRequested)
            lost.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await StopRenewalLoopAsync();
        stop.Dispose();
        lost.Dispose();
        renewalGate.Dispose();
    }
}

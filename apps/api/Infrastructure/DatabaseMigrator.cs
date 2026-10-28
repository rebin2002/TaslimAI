using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Infrastructure;

public static class DatabaseMigrator
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MinimumTimeoutSeconds = 10;
    public const int MaximumTimeoutSeconds = 900;

    public static async Task ApplyAsync(
        TaslimDbContext db,
        ILogger logger,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var migrationTimeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        if (migrationTimeout <= TimeSpan.Zero || migrationTimeout.TotalSeconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Database migration timeout must be positive and fit the database command timeout.");

        var commandTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(migrationTimeout.TotalSeconds));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(migrationTimeout);
        var migrationCancellation = timeoutSource.Token;

        // Apply the same finite bound to EF migration commands and the raw
        // advisory-lock commands. A blocked lock must not hold a Railway
        // rollout open until the platform's deployment timeout.
        db.Database.SetCommandTimeout(commandTimeoutSeconds);

        try
        {
            await db.Database.OpenConnectionAsync(migrationCancellation);
            try
            {
                await using (var lockCommand = db.Database.GetDbConnection().CreateCommand())
                {
                    lockCommand.CommandTimeout = commandTimeoutSeconds;
                    lockCommand.CommandText = "SELECT pg_advisory_lock(hashtextextended('taslim_ef_migrations', 0));";
                    await lockCommand.ExecuteScalarAsync(migrationCancellation);
                }

                logger.LogInformation("Applying pending Taslim database migrations. TimeoutSeconds={TimeoutSeconds}", commandTimeoutSeconds);
                await db.Database.MigrateAsync(migrationCancellation);

                await using (var unlockCommand = db.Database.GetDbConnection().CreateCommand())
                {
                    unlockCommand.CommandTimeout = commandTimeoutSeconds;
                    unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtextextended('taslim_ef_migrations', 0));";
                    await unlockCommand.ExecuteScalarAsync(migrationCancellation);
                }
            }
            finally
            {
                // Closing the connection also releases PostgreSQL's session
                // advisory lock if migration or shutdown cancellation occurs.
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogCritical(exception, "Taslim database migration timed out. API startup is stopping to avoid an invalid schema. TimeoutSeconds={TimeoutSeconds}", commandTimeoutSeconds);
            throw new TimeoutException($"Database migration did not complete within {commandTimeoutSeconds} seconds.", exception);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Taslim database migration failed. API startup is stopping to avoid an invalid schema.");
            throw;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Taslim.Api.Persistence;

namespace Taslim.Api.Infrastructure;

public static class DatabaseMigrator
{
    public static async Task ApplyAsync(TaslimDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var lockCommand = db.Database.GetDbConnection().CreateCommand())
            {
                lockCommand.CommandText = "SELECT pg_advisory_lock(hashtextextended('taslim_ef_migrations', 0));";
                await lockCommand.ExecuteScalarAsync(cancellationToken);
            }

            logger.LogInformation("Applying pending Taslim database migrations.");
            await db.Database.MigrateAsync(cancellationToken);

            await using (var unlockCommand = db.Database.GetDbConnection().CreateCommand())
            {
                unlockCommand.CommandText = "SELECT pg_advisory_unlock(hashtextextended('taslim_ef_migrations', 0));";
                await unlockCommand.ExecuteScalarAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Taslim database migration failed. API startup is stopping to avoid an invalid schema.");
            throw;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

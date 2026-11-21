using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Taslim.Api.Persistence;
using Taslim.Api.Persistence.Migrations;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Schema/migration evidence for <c>AddPasswordResetEmailOutbox</c> (#1121 / #848), the one PostgreSQL
/// migration that ships with the forgot-password integration.
///
/// Every other recovery suite here builds its schema from the model with <c>EnsureCreated()</c>, so none
/// of them ever runs the migration. The production path does: <c>DatabaseMigrator</c> calls
/// <c>MigrateAsync()</c> when <c>Database:ApplyMigrations=true</c>, and the release checklist blocks on
/// independent migration-integrity evidence. These tests cover exactly that lane:
///
///   * the <c>Up</c>/<c>Down</c> operation sets are inverse and name only the outbox table, so no
///     unrelated table, column, constraint or index can be rewritten by this step;
///   * the step applies over a legacy (pre-migration) schema that already holds users, and its scripted
///     revert returns the schema to the byte-identical pre-<c>Up</c> state;
///   * it is refused on a truly empty schema, pinning the ordering precondition;
///   * applying it leaves no mail state and installs no executable database object, so the schema change
///     alone cannot make outbound SMTP active while <c>PasswordRecovery:Enabled</c> stays false.
///
/// All database work happens in throwaway databases inside the ephemeral test PostgreSQL instance,
/// populated only with synthetic <c>example.invalid</c> rows: no production database, no real
/// credentials, no live users, no mail, no provider calls. Feature-flag, readiness and delivery
/// behaviour remains owned by <c>PasswordRecoveryTests</c>,
/// <c>PasswordRecoveryEmailDeliveryEndToEndTests</c> and <c>PasswordRecoveryRateLimitHttpAcceptanceTests</c>;
/// nothing here repeats those assertions.
/// </summary>
public sealed class PasswordResetEmailOutboxMigrationSchemaTests : IClassFixture<OutboxLegacySchema>
{
    private const string OutboxMigrationId = "20261029070409_AddPasswordResetEmailOutbox";
    private const string OutboxTable = "PasswordResetEmailOutbox";
    private const string PrimaryKeyName = "PK_PasswordResetEmailOutbox";
    private const string ForeignKeyName = "FK_PasswordResetEmailOutbox_AspNetUsers_UserId";
    private const string LeaseIndexName = "IX_PasswordResetEmailOutbox_LeaseExpiresAt";
    private const string PollIndexName = "IX_PasswordResetEmailOutbox_Status_NextAttemptAt";

    public PasswordResetEmailOutboxMigrationSchemaTests(OutboxLegacySchema schema) => Schema = schema;

    private OutboxLegacySchema Schema { get; }

    /// <summary>True when no PostgreSQL instance is configured, so the database cases opt out cleanly.</summary>
    private static bool NoServerConfigured => OutboxLegacySchema.AdminConnectionStringIsAbsent;

    // ------------------------------------------------------- operation evidence, no database needed

    [Fact]
    public void Up_operations_create_only_the_outbox_table_and_its_two_indexes()
    {
        var operations = OutboxMigrationOperations.Read(up: true);

        Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal(2, operations.OfType<CreateIndexOperation>().Count(operation => operation.Table == OutboxTable));
        Assert.Empty(operations.OfType<DropTableOperation>());
        Assert.Empty(operations.OfType<DropIndexOperation>());
        Assert.Empty(operations.OfType<DropColumnOperation>());
        Assert.Empty(operations.OfType<AlterTableOperation>());
        Assert.Empty(operations.OfType<AlterColumnOperation>());
        Assert.Empty(operations.OfType<RenameTableOperation>());
        Assert.Empty(operations.OfType<SqlOperation>());
        Assert.All(operations, operation => Assert.Equal(OutboxTable, operation switch
        {
            CreateTableOperation createTable => createTable.Name,
            CreateIndexOperation createIndex => createIndex.Table,
            _ => "no table named here",
        }));

        var create = operations.OfType<CreateTableOperation>().Single();
        Assert.Null(create.Schema);
        Assert.Equal(OutboxTable, create.Name);

        Assert.Equal(
            new[]
            {
                ("AttemptCount", "integer", false, (int?)null),
                ("LastFailureCode", "character varying(80)", true, (int?)80),
                ("LastRequestedAt", "timestamp with time zone", false, (int?)null),
                ("LeaseExpiresAt", "timestamp with time zone", true, (int?)null),
                ("NextAttemptAt", "timestamp with time zone", false, (int?)null),
                ("Status", "character varying(20)", false, (int?)20),
                ("UserId", "uuid", false, (int?)null),
            },
            create.Columns
                .Select(column => (column.Name, column.ColumnType, column.IsNullable, column.MaxLength))
                .OrderBy(row => row.Name, StringComparer.Ordinal));

        // No server-side defaults and no generated columns: the outbox only holds rows the API writes.
        Assert.All(create.Columns, column => Assert.Null(column.DefaultValue));
        Assert.All(create.Columns, column => Assert.Null(column.DefaultValueSql));
        Assert.All(create.Columns, column => Assert.Null(column.ComputedColumnSql));

        Assert.Equal(PrimaryKeyName, create.PrimaryKey.Name);
        Assert.Equal(new[] { "UserId" }, create.PrimaryKey.Columns);

        var foreignKey = Assert.Single(create.ForeignKeys);
        Assert.Equal(ForeignKeyName, foreignKey.Name);
        Assert.Equal(new[] { "UserId" }, foreignKey.Columns);
        Assert.Equal("AspNetUsers", foreignKey.PrincipalTable);
        Assert.Equal("Id", Assert.Single(foreignKey.PrincipalColumns));
        Assert.Equal(ReferentialAction.Cascade, foreignKey.OnDelete);
        Assert.Equal(ReferentialAction.NoAction, foreignKey.OnUpdate);

        Assert.Equal(
            new[] { LeaseIndexName, PollIndexName },
            operations.OfType<CreateIndexOperation>()
                .Select(index => index.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        var lease = operations.OfType<CreateIndexOperation>().Single(index => index.Name == LeaseIndexName);
        Assert.False(lease.IsUnique);
        Assert.Equal(new[] { "LeaseExpiresAt" }, lease.Columns);
        var poll = operations.OfType<CreateIndexOperation>().Single(index => index.Name == PollIndexName);
        Assert.False(poll.IsUnique);
        Assert.Equal(new[] { "Status", "NextAttemptAt" }, poll.Columns);
    }

    [Fact]
    public void Down_operation_drops_only_the_outbox_table()
    {
        var operations = OutboxMigrationOperations.Read(up: false);

        var drop = Assert.Single(operations);
        var dropTable = Assert.IsType<DropTableOperation>(drop);
        Assert.Null(dropTable.Schema);
        Assert.Equal(OutboxTable, dropTable.Name);
    }

    // ------------------------------------------------- throwaway PostgreSQL apply / revert evidence

    [Fact]
    public async Task Up_applies_over_a_legacy_schema_that_already_holds_users_and_leaves_unrelated_state_alone()
    {
        if (NoServerConfigured)
        {
            return;
        }

        await Schema.InitializeAsync();

        // The class shares one throwaway database and xunit does not guarantee test order, so this case
        // first returns the database to the legacy state it is supposed to upgrade from.
        if ((await Schema.CaptureAsync()).Tables.Contains(OutboxTable))
        {
            await Schema.ExecuteScriptAsync(Schema.GenerateScript(OutboxMigrationId, Schema.PreviousMigrationId));
        }

        var before = await Schema.CaptureAsync();
        Assert.DoesNotContain(OutboxTable, before.Tables);
        Assert.True(before.UserCount >= 2, "the synthetic legacy fixture must hold pre-existing users");
        Assert.Equal(Schema.PreviousMigrationId, before.HistoryTail);

        await Schema.MigrateToOutboxAsync();
        var after = await Schema.CaptureAsync();

        Assert.Contains(OutboxTable, after.Tables);
        Assert.Equal(OutboxMigrationId, after.HistoryTail);
        Assert.Equal(before.HistoryCount + 1, after.HistoryCount);

        // Unrelated structure and data survive the step: the migration adds, it never rewrites.
        Assert.Equal(before.UnrelatedFingerprint, after.UnrelatedFingerprint);
        Assert.Equal(before.DataFingerprint, after.DataFingerprint);
        Assert.Equal(before.UserCount, after.UserCount);

        // Delivered DDL equals the model the API is compiled against.
        Assert.Equal(
            new[]
            {
                "UserId|uuid|NO",
                "LastRequestedAt|timestamp with time zone|NO",
                "NextAttemptAt|timestamp with time zone|NO",
                "LeaseExpiresAt|timestamp with time zone|YES",
                "Status|character varying(20)|NO",
                "AttemptCount|integer|NO",
                "LastFailureCode|character varying(80)|YES",
            },
            after.OutboxColumns);
        Assert.Equal(new[] { "f " + ForeignKeyName, "p " + PrimaryKeyName }, after.OutboxKeyConstraints);

        // The constraints are enforced, not merely declared, and they are enforced with the shapes the
        // worker code assumes: one row per user, bounded status text, cascading removal with the user.
        await Schema.ExecuteAsync(
            "INSERT INTO \"PasswordResetEmailOutbox\" (\"UserId\",\"LastRequestedAt\",\"NextAttemptAt\",\"Status\",\"AttemptCount\") "
            + "SELECT \"Id\", now(), now(), 'Pending', 0 FROM \"AspNetUsers\" WHERE \"UserName\" = 'synthetic-legacy-1';");

        var duplicate = await Schema.TryExecuteAsync(
            "INSERT INTO \"PasswordResetEmailOutbox\" (\"UserId\",\"LastRequestedAt\",\"NextAttemptAt\",\"Status\",\"AttemptCount\") "
            + "SELECT \"Id\", now(), now() + interval '1 hour', 'Pending', 0 FROM \"AspNetUsers\" WHERE \"UserName\" = 'synthetic-legacy-1';");
        Assert.NotNull(duplicate);
        Assert.Contains(PrimaryKeyName, duplicate, StringComparison.Ordinal);

        var orphan = await Schema.TryExecuteAsync(
            "INSERT INTO \"PasswordResetEmailOutbox\" (\"UserId\",\"LastRequestedAt\",\"NextAttemptAt\",\"Status\",\"AttemptCount\") "
            + "VALUES (gen_random_uuid(), now(), now(), 'Pending', 0);");
        Assert.NotNull(orphan);
        Assert.Contains("violates foreign key constraint", orphan, StringComparison.Ordinal);
        Assert.Contains(ForeignKeyName, orphan, StringComparison.Ordinal);

        var tooLong = await Schema.TryExecuteAsync(
            "INSERT INTO \"PasswordResetEmailOutbox\" (\"UserId\",\"LastRequestedAt\",\"NextAttemptAt\",\"Status\",\"AttemptCount\") "
            + "SELECT \"Id\", now(), now(), 'this-status-value-is-deliberately-too-long', 0 FROM \"AspNetUsers\" WHERE \"UserName\" = 'synthetic-legacy-2';");
        Assert.NotNull(tooLong);
        Assert.Contains("value too long for type character varying(20)", tooLong, StringComparison.Ordinal);

        // Removing the owning user takes its queued row with it.
        await Schema.ExecuteAsync("DELETE FROM \"AspNetUsers\" WHERE \"UserName\" = 'synthetic-legacy-1';");
        Assert.Equal(0, await Schema.ScalarLongAsync(
            "SELECT count(*) FROM \"PasswordResetEmailOutbox\" "
            + "WHERE \"UserId\" NOT IN (SELECT \"Id\" FROM \"AspNetUsers\");"));
    }

    [Fact]
    public async Task Scripted_revert_returns_the_schema_to_the_pre_up_state_and_reapplies_identically()
    {
        if (NoServerConfigured)
        {
            return;
        }

        await Schema.InitializeAsync();
        var before = await Schema.CaptureAsync();

        await Schema.MigrateToOutboxAsync();
        var upgraded = await Schema.CaptureAsync();

        var upScript = Schema.GenerateScript(Schema.PreviousMigrationId, OutboxMigrationId);
        var downScript = Schema.GenerateScript(OutboxMigrationId, Schema.PreviousMigrationId);

        // The scripted form carries its own history bookkeeping in both directions.
        Assert.Contains("CREATE TABLE \"" + OutboxTable + "\"", upScript, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO \"__EFMigrationsHistory\"", upScript, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE \"" + OutboxTable + "\"", downScript, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM \"__EFMigrationsHistory\"", downScript, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER", downScript, StringComparison.Ordinal);

        await Schema.ExecuteScriptAsync(downScript);
        var reverted = await Schema.CaptureAsync();
        Assert.DoesNotContain(OutboxTable, reverted.Tables);
        Assert.Equal(before.HistoryCount, reverted.HistoryCount);
        Assert.Equal(before.HistoryTail, reverted.HistoryTail);
        Assert.Equal(before.UnrelatedFingerprint, reverted.UnrelatedFingerprint);
        Assert.Equal(before.DataFingerprint, reverted.DataFingerprint);

        await Schema.ExecuteScriptAsync(upScript);
        var replayed = await Schema.CaptureAsync();
        Assert.Equal(upgraded.AllFingerprint, replayed.AllFingerprint);
        Assert.Equal(upgraded.UnrelatedFingerprint, replayed.UnrelatedFingerprint);
        Assert.Equal(OutboxMigrationId, replayed.HistoryTail);
    }

}



/// <summary>
/// Inertness evidence, in its own throwaway database so no other case can have queued rows: applying the
/// schema step installs no mail state and nothing that could run on its own. The feature flag, readiness
/// and delivery behaviour is owned by PasswordRecoveryTests and the delivery/rate-limit suites.
/// </summary>
public sealed class PasswordResetEmailOutboxInertAfterApplyTests : IClassFixture<OutboxLegacySchema>
{
    private static bool NoServerConfigured => OutboxLegacySchema.AdminConnectionStringIsAbsent;

    public PasswordResetEmailOutboxInertAfterApplyTests(OutboxLegacySchema schema) => Schema = schema;

    private OutboxLegacySchema Schema { get; }

    [Fact]
    public async Task Applying_the_step_installs_no_mail_state_and_no_executable_database_object()
    {
        if (NoServerConfigured)
        {
            return;
        }

        await Schema.InitializeAsync();
        await Schema.MigrateToOutboxAsync();
        var after = await Schema.CaptureAsync();

        // A migrated schema cannot send anything by itself: the queue is empty and nothing in the
        // database can act on its own, so outbound SMTP stays inert until the operator opts in.
        Assert.Equal(0, after.OutboxRowCount);
        Assert.Equal(0, after.OutboxTriggerCount);
        Assert.Equal(0, after.OutboxRuleCount);
        Assert.Equal(0, after.MailNamedRoutineCount);
        Assert.Equal(0, after.OutboxReferencesMailObjectCount);
    }
}

/// <summary>
/// The outbox step is incremental: its foreign key names <c>AspNetUsers</c>, so it must be refused on a
/// schema with no baseline. This pins the precondition the release checklist depends on.
/// </summary>
public sealed class PasswordResetEmailOutboxEmptySchemaTests
{
    [Fact]
    public async Task Up_is_refused_on_an_empty_schema_and_creates_no_partial_objects()
    {
        if (OutboxLegacySchema.AdminConnectionStringIsAbsent)
        {
            return;
        }

        using var schema = new EmptyOutboxSchema();

        var error = await schema.TryExecuteAsync(schema.OutboxUpSql);
        Assert.NotNull(error);
        Assert.Contains("AspNetUsers", error, StringComparison.Ordinal);
        Assert.Equal(0, await schema.ScalarLongAsync("SELECT count(*) FROM pg_tables WHERE schemaname = 'public';"));
    }
}

/// <summary>
/// Throwaway database migrated to the last step before the outbox migration and filled with synthetic
/// rows only. One instance is shared by the class, created on first use and dropped on disposal.
/// </summary>
public sealed class OutboxLegacySchema : IAsyncDisposable
{
    internal const string OutboxMigrationId = "20261029070409_AddPasswordResetEmailOutbox";
    private const string OutboxTable = "PasswordResetEmailOutbox";
    private const string GuardTable = "SyntheticUnrelatedGuard";

    internal static readonly string? AdminConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");

    private Task? initialization;
    private string? databaseName;

    internal static bool AdminConnectionStringIsAbsent => string.IsNullOrWhiteSpace(AdminConnectionString);

    internal string? PreviousMigrationId { get; private set; }

    internal static string RequireAdmin()
        => AdminConnectionStringIsAbsent
            ? throw new InvalidOperationException("ConnectionStrings__Postgres is not configured.")
            : AdminConnectionString!;

    internal string ConnectionString => Build(RequireAdmin(), databaseName!);

    internal Task InitializeAsync() => initialization ??= BuildAsync();

    private async Task BuildAsync()
    {
        {

            databaseName = "outbox_mig_" + Guid.NewGuid().ToString("N")[..12];
            await ExecuteOnAdminAsync("CREATE DATABASE \"" + databaseName + "\"");

            using (var db = NewContext())
            {
                var ids = db.Database.GetMigrations().ToArray();
                var position = Array.IndexOf(ids, OutboxMigrationId);
                Assert.True(position > 0, OutboxMigrationId + " is missing from, or first in, the migration chain.");
                PreviousMigrationId = ids[position - 1];

                // Legacy state: the real chain, stopped one step before the outbox migration.
                Migrator(db).Migrate(PreviousMigrationId);

                for (var row = 1; row <= 3; row++)
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO \"AspNetUsers\" (\"Id\",\"UserName\",\"NormalizedUserName\",\"Email\",\"NormalizedEmail\","
                        + "\"EmailConfirmed\",\"PasswordHash\",\"SecurityStamp\",\"ConcurrencyStamp\",\"PhoneNumberConfirmed\","
                        + "\"TwoFactorEnabled\",\"LockoutEnabled\",\"AccessFailedCount\",\"DisplayName\",\"PreferredLanguage\","
                        + "\"CreatedAt\",\"UpdatedAt\",\"IsActive\") VALUES (gen_random_uuid(), "
                        + "'synthetic-legacy-" + row + "', 'SYNTHETIC-LEGACY-" + row + "', "
                        + "'synthetic-legacy-" + row + "@example.invalid', 'SYNTHETIC-LEGACY-" + row + "@EXAMPLE.INVALID', "
                        + "false, 'not-a-real-hash', 'not-a-real-stamp', 'not-a-real-stamp', false, false, true, 0, "
                        + "'Synthetic Legacy " + row + "', 'en', now(), now(), true);");
                }

                // An unrelated table with its own constraint, index and rows, absent from the model.
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TABLE \"" + GuardTable + "\" (\"Id\" integer NOT NULL PRIMARY KEY,"
                    + " \"Payload\" character varying(40) NOT NULL,"
                    + " \"OwnerUserId\" uuid NOT NULL REFERENCES \"AspNetUsers\"(\"Id\") ON DELETE CASCADE,"
                    + " CONSTRAINT \"CK_" + GuardTable + "_NotEmpty\" CHECK (\"Payload\" <> ''));");
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE UNIQUE INDEX \"UX_" + GuardTable + "_Payload\" ON \"" + GuardTable + "\" (\"Payload\");");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"" + GuardTable + "\" (\"Id\",\"Payload\",\"OwnerUserId\") "
                    + "SELECT g, 'unrelated-row-' || g, (SELECT \"Id\" FROM \"AspNetUsers\" ORDER BY \"UserName\" LIMIT 1) "
                    + "FROM generate_series(1, 5) AS g;");
            }

        }
    }

    internal TaslimDbContext NewContext()
        => new(new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(ConnectionString).Options);

    internal Task MigrateToOutboxAsync()
    {
        using var db = NewContext();
        Migrator(db).Migrate(OutboxMigrationId);
        return Task.CompletedTask;
    }

    internal string GenerateScript(string? from, string? to)
    {
        using var db = NewContext();
        return Migrator(db).GenerateScript(from, to, MigrationsSqlGenerationOptions.Default);
    }

    internal async Task ExecuteScriptAsync(string script)
    {
        // The scripted form embeds its own BEGIN/COMMIT pair; run it inside one explicit transaction so
        // a revert is all-or-nothing, exactly like the migrator's own transactions.
        var body = string.Join(
            Environment.NewLine,
            script
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("START TRANSACTION", StringComparison.OrdinalIgnoreCase)
                    && !line.TrimStart().StartsWith("COMMIT;", StringComparison.OrdinalIgnoreCase)));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = body;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<string?> TryExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.Message;
        }
    }

    internal async Task<string?> ScalarTextAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    internal async Task<long> ScalarLongAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    internal async Task<OutboxSchemaState> CaptureAsync()
    {
        var tables = await ListAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;");
        var present = tables.Contains(OutboxTable);

        var outboxColumns = present
            ? await ListAsync(
                "SELECT column_name || '|' || data_type || coalesce('(' || character_maximum_length || ')', '') "
                + "|| '|' || is_nullable FROM information_schema.columns "
                + "WHERE table_schema = 'public' AND table_name = '" + OutboxTable + "' ORDER BY ordinal_position;")
            : Array.Empty<string>();

        var outboxKeys = present
            ? await ListAsync(
                "SELECT con.contype::text || ' ' || con.conname FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid "
                + "WHERE con.connamespace = 'public'::regnamespace AND c.relname = '" + OutboxTable + "' "
                + "AND con.contype IN ('f', 'p') ORDER BY 1;")
            : Array.Empty<string>();

        // Fingerprint of everything the step must not touch, and of the whole schema.
        var excludedTables = "('PasswordResetEmailOutbox', '__EFMigrationsHistory')";
        var unrelated = await ScalarTextAsync(
            "SELECT md5(string_agg(t, '|' ORDER BY t)) FROM ("
            + "SELECT 'c:' || table_name || '.' || column_name || ':' || data_type "
            + "|| coalesce('(' || character_maximum_length || ')', '') || ':' || is_nullable || ':' || coalesce(column_default, '~') AS t "
            + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name NOT IN " + excludedTables
            + " UNION ALL SELECT 'i:' || indexname || ':' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "AND tablename NOT IN " + excludedTables
            + " UNION ALL SELECT 'n:' || c.relname || ':' || con.conname || ':' || pg_get_constraintdef(con.oid) "
            + "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid "
            + "WHERE con.connamespace = 'public'::regnamespace AND c.relname NOT IN " + excludedTables
            + ") x;");

        var all = await ScalarTextAsync(
            "SELECT md5(string_agg(t, '|' ORDER BY t)) FROM ("
            + "SELECT 'c:' || table_name || '.' || column_name || ':' || data_type "
            + "|| coalesce('(' || character_maximum_length || ')', '') || ':' || is_nullable || ':' || coalesce(column_default, '~') AS t "
            + "FROM information_schema.columns WHERE table_schema = 'public' "
            + "UNION ALL SELECT 'i:' || indexname || ':' || indexdef FROM pg_indexes WHERE schemaname = 'public' "
            + "UNION ALL SELECT 'n:' || c.relname || ':' || con.conname || ':' || pg_get_constraintdef(con.oid) "
            + "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid WHERE con.connamespace = 'public'::regnamespace) x;");

        var data = await ScalarTextAsync(
            "SELECT md5(string_agg(t, '|' ORDER BY t)) FROM ("
            + "SELECT 'u:' || \"Id\" || ':' || \"UserName\" AS t FROM \"AspNetUsers\" "
            + "UNION ALL SELECT 'g:' || \"Id\" || ':' || \"Payload\" FROM \"" + GuardTable + "\") x;");

        var mailReferences = present
            ? await ScalarLongAsync(
                "SELECT count(*) FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid "
                + "JOIN pg_class f ON f.oid = con.confrelid "
                + "WHERE con.connamespace = 'public'::regnamespace AND c.relname = '" + OutboxTable + "' "
                + "AND f.relname ILIKE '%mail%';")
            : 0;

        return new OutboxSchemaState(
            tables,
            outboxColumns,
            outboxKeys,
            await ScalarTextAsync("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;"),
            await ScalarLongAsync("SELECT count(*) FROM \"__EFMigrationsHistory\";"),
            await ScalarLongAsync("SELECT count(*) FROM \"AspNetUsers\";"),
            unrelated ?? string.Empty,
            all ?? string.Empty,
            data ?? string.Empty,
            present ? await ScalarLongAsync("SELECT count(*) FROM \"" + OutboxTable + "\";") : 0,
            await ScalarLongAsync(
                "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid "
                + "WHERE c.relname = '" + OutboxTable + "' AND NOT t.tgisinternal;"),
            await ScalarLongAsync(
                "SELECT count(*) FROM pg_rules WHERE schemaname = 'public' AND tablename = '" + OutboxTable + "';"),
            await ScalarLongAsync(
                "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
                + "WHERE n.nspname = 'public' "
                + "AND (p.proname ILIKE '%outbox%' OR p.proname ILIKE '%smtp%' OR p.proname ILIKE '%mail%');"),
            mailReferences);
    }

    private async Task<IReadOnlyList<string>> ListAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task ExecuteOnAdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(RequireAdmin() + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static IMigrator Migrator(TaslimDbContext context)
        => ((IInfrastructure<IServiceProvider>)context).Instance.GetRequiredService<IMigrator>();

    private static string Build(string adminConnectionString, string database)
        => new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = database }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        if (databaseName is null)
        {
            return;
        }

        var name = databaseName;
        databaseName = null;
        initialization = null;
        try
        {
            await ExecuteOnAdminAsync("DROP DATABASE IF EXISTS \"" + name + "\" WITH (FORCE)");
        }
        catch (PostgresException)
        {
            // Throwaway database: cleanup is best effort, the instance itself is ephemeral.
        }
    }
}

/// <summary>Throwaway database whose public schema stays empty: the refusal precondition case.</summary>
internal sealed class EmptyOutboxSchema : IDisposable
{
    private readonly string databaseName;

    internal string OutboxUpSql { get; }

    internal EmptyOutboxSchema()
    {
        databaseName = "outbox_empty_" + Guid.NewGuid().ToString("N")[..12];
        var admin = OutboxLegacySchema.RequireAdmin();
        using (var connection = new NpgsqlConnection(admin + ";Pooling=false"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE DATABASE \"" + databaseName + "\"";
            create.ExecuteNonQuery();
        }

        // Generate the step's DDL in-process; nothing here needs a database or a provider.
        OutboxUpSql = string.Join(
            Environment.NewLine,
            OutboxMigrationOperations.Sql(up: true, admin));
    }

    internal string ConnectionString
        => new NpgsqlConnectionStringBuilder(OutboxLegacySchema.RequireAdmin()) { Database = databaseName }.ConnectionString;

    internal async Task<string?> TryExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString + ";Pooling=false");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException exception)
        {
            return exception.Message;
        }
    }

    internal async Task<long> ScalarLongAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        try
        {
            using var connection = new NpgsqlConnection(OutboxLegacySchema.RequireAdmin() + ";Pooling=false");
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP DATABASE IF EXISTS \"" + databaseName + "\" WITH (FORCE)";
            drop.ExecuteNonQuery();
        }
        catch (PostgresException)
        {
            // Best effort.
        }
    }
}

/// <summary>Reads the outbox migration's declared operations and their Npgsql DDL without a database.</summary>
internal static class OutboxMigrationOperations
{
    internal static IReadOnlyList<MigrationOperation> Read(bool up)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var method = typeof(AddPasswordResetEmailOutbox).GetMethod(
            up ? "Up" : "Down",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method); // The outbox migration lost its Up/Down pair.
        method!.Invoke(new AddPasswordResetEmailOutbox(), new object[] { builder });
        return builder.Operations;
    }

    internal static string Sql(bool up, string adminConnectionString)
    {
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(adminConnectionString).Options;
        using var context = new TaslimDbContext(options);
        var generator = ((IInfrastructure<IServiceProvider>)context).Instance.GetRequiredService<IMigrationsSqlGenerator>();
        return string.Join(Environment.NewLine, generator.Generate(Read(up), context.Model).Select(command => command.CommandText));
    }
}

/// <summary>Catalog and data snapshot of the throwaway legacy database at one point in time.</summary>
internal sealed record OutboxSchemaState(
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> OutboxColumns,
    IReadOnlyList<string> OutboxKeyConstraints,
    string? HistoryTail,
    long HistoryCount,
    long UserCount,
    string UnrelatedFingerprint,
    string AllFingerprint,
    string DataFingerprint,
    long OutboxRowCount,
    long OutboxTriggerCount,
    long OutboxRuleCount,
    long MailNamedRoutineCount,
    long OutboxReferencesMailObjectCount);

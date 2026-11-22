using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieCharacterProductionSheetContractTests
{
    [Fact]
    public void Rejects_duplicate_identity_reference_assets()
    {
        var asset = Guid.NewGuid();
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = asset,
            BodyReferenceAssetId = asset,
        }));

        Assert.Equal("DUPLICATE_REFERENCE_ASSET", exception.Code);
    }

    [Fact]
    public void Approval_requires_all_five_identity_slots()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            CanonicalIdentityFaceAssetId = Guid.NewGuid(),
            BodyReferenceAssetId = Guid.NewGuid(),
            FrontReferenceAssetId = Guid.NewGuid(),
            SideReferenceAssetId = Guid.NewGuid(),
        }, requireComplete: true));

        Assert.Equal("INCOMPLETE_REFERENCE_SHEET", exception.Code);
    }

    [Fact]
    public void Look_keys_are_unique_within_a_version()
    {
        var exception = Assert.Throws<MovieCharacterProductionSheetValidationException>(() => MovieCharacterProductionSheetService.ValidateRequest(new MovieCharacterProductionSheetRequest
        {
            Looks =
            [
                new MovieCharacterProductionSheetLookRequest("hero", "Hero", Wardrobe: "Coat"),
                new MovieCharacterProductionSheetLookRequest("HERO", "Hero alternate", Wardrobe: "Jacket"),
            ],
        }));

        Assert.Equal("DUPLICATE_LOOK_KEY", exception.Code);
    }

    [Fact]
    public async Task Version_allocation_conflict_reloads_canonical_sheet_and_retries()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"taslim-movie-character-sheet-{Guid.NewGuid():N}.db");
        try
        {
            var seedOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            await using var seedDb = new TaslimDbContext(seedOptions);
            await seedDb.Database.EnsureCreatedAsync();
            var seed = await SeedAsync(seedDb);
            var seedService = CreateService(seedDb);
            var initial = await seedService.SaveDraftAsync(seed.UserId, seed.CharacterId, new MovieCharacterProductionSheetRequest
            {
                ProvenanceNote = "initial",
            });
            Assert.NotNull(initial);
            Assert.Equal(1, initial!.CurrentVersionNumber);

            var retryOptions = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .AddInterceptors(new VersionAllocationConflictInterceptor())
                .Options;
            await using var retryDb = new TaslimDbContext(retryOptions);
            var result = await CreateService(retryDb).SaveDraftAsync(seed.UserId, seed.CharacterId, new MovieCharacterProductionSheetRequest
            {
                ProvenanceNote = "retry-after-conflict",
            });
            Assert.NotNull(result);
            Assert.Equal(2, result!.CurrentVersionNumber);

            await using var verifyDb = new TaslimDbContext(seedOptions);
            var versions = await verifyDb.MovieCharacterProductionSheetVersions
                .AsNoTracking()
                .Where(item => item.MovieCharacterProductionSheetId == result.Id)
                .OrderBy(item => item.VersionNumber)
                .ToListAsync();
            Assert.Equal(new[] { 1, 2 }, versions.Select(item => item.VersionNumber));
            Assert.Contains(versions, item => item.ProvenanceJson.Contains("retry-after-conflict", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    private static MovieCharacterProductionSheetService CreateService(TaslimDbContext db) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    /// <summary>
    /// The version index is declared with 86 characters, so every provider reports a conflict on it
    /// under a shorter name than the one the migration wrote. The shapes below all have to keep working;
    /// the PostgreSQL shape is the one that was wrong, and it is asserted against the live catalog in the
    /// nested PostgreSQL class rather than being reasoned about here.
    /// </summary>
    [Fact]
    public void Version_allocation_conflict_is_recognised_under_every_reported_index_shape()
    {
        const string declared = "IX_MovieCharacterProductionSheetVersions_MovieCharacterProductionSheetId_VersionNumber";
        var shapes = new[]
        {
            declared,
            declared[..63],
            declared[..62] + "~",
            "MovieCharacterProductionSheetVersions.MovieCharacterProductionSheetId, MovieCharacterProductionSheetVersions.VersionNumber",
            "IX_MovieCharacterProductionSheets_MovieCharacterId",
        };

        foreach (var shape in shapes)
        {
            var exception = new DbUpdateException(
                "An error occurred while saving the entity changes. duplicate key value violates unique constraint \"" + shape + "\"");
            Assert.True(MovieCharacterProductionSheetService.IsVersionAllocationUniqueViolation(exception),
                "The retry gate must recognise a version conflict reported as \"" + shape + "\".");
        }
    }

    /// <summary>
    /// Retrying is only correct for the allocation this service owns. A conflict on an unrelated unique
    /// index has to surface instead of burning the retry budget on an error that cannot heal.
    /// </summary>
    [Fact]
    public void Unrelated_unique_conflicts_are_not_mistaken_for_version_allocation_conflicts()
    {
        var shapes = new[]
        {
            "IX_MovieTakes_MovieShotId_TakeNumber",
            "IX_MovieProductionSheets_MovieProjectId",
            "IX_Users_UserName",
        };

        foreach (var shape in shapes)
        {
            var exception = new DbUpdateException("duplicate key value violates unique constraint \"" + shape + "\"");
            Assert.False(MovieCharacterProductionSheetService.IsVersionAllocationUniqueViolation(exception),
                "A conflict on the unrelated index \"" + shape + "\" must not trigger the version retry.");
        }
    }

    /// <summary>
    /// These facts run against the disposable PostgreSQL that the API CI job provides, because the
    /// byte-truncated constraint name this lane depends on exists only on that provider: the repository's
    /// SQLite suites report a version conflict as a column list and stay green while the PostgreSQL
    /// classification is broken. The index is recreated under the exact name the migration declares, so
    /// the assertion covers production rather than whatever name a model-built schema happens to pick.
    /// </summary>
    public sealed class PostgresIndexNaming : IAsyncLifetime
    {
        private const string DeclaredVersionIndexName = "IX_MovieCharacterProductionSheetVersions_MovieCharacterProductionSheetId_VersionNumber";
        private const string VersionColumnsPredicate = "and indexdef like '%\"MovieCharacterProductionSheetId\", \"VersionNumber\"%'";
        private readonly string schema = "sheet_version_idx_" + Guid.NewGuid().ToString("N");
        private DbContextOptions<TaslimDbContext> options = null!;

        public async Task InitializeAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
            await using (var admin = new NpgsqlConnection(connectionString))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = "CREATE SCHEMA \"" + schema + "\"";
                await create.ExecuteNonQueryAsync();
            }

            options = new DbContextOptionsBuilder<TaslimDbContext>()
                .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
                .Options;
            await using var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();
        }

        public async Task DisposeAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                await using var admin = new NpgsqlConnection(connectionString);
                await admin.OpenAsync();
                await using var drop = admin.CreateCommand();
                drop.CommandText = "DROP SCHEMA IF EXISTS \"" + schema + "\" CASCADE";
                await drop.ExecuteNonQueryAsync();
            }
        }

        [DisposablePostgresFact]
        public async Task Postgres_stores_the_declared_version_index_under_its_byte_truncated_name()
        {
            await using var db = new TaslimDbContext(options);
            var stored = await RecreateVersionIndexUnderDeclaredNameAsync(db);

            Assert.NotEqual(DeclaredVersionIndexName, stored);
            Assert.Equal(63, stored.Length);
            Assert.Equal(DeclaredVersionIndexName[..63], stored);
            Assert.DoesNotContain('~', stored);
        }

        [DisposablePostgresFact]
        public async Task A_real_postgres_version_conflict_opens_the_retry_gate()
        {
            await using var db = new TaslimDbContext(options);
            var reportedName = await RecreateVersionIndexUnderDeclaredNameAsync(db);
            var seed = await SeedAsync(db);
            var now = DateTime.UtcNow;
            var sheetId = Guid.NewGuid();

            db.MovieCharacterProductionSheets.Add(new MovieCharacterProductionSheet
            {
                Id = sheetId, MovieCharacterId = seed.CharacterId, CurrentVersionNumber = 1,
                Status = MovieCharacterProductionSheetStatuses.Draft, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();

            db.MovieCharacterProductionSheetVersions.Add(Version(sheetId, seed.UserId, 1, now));
            await db.SaveChangesAsync();

            db.MovieCharacterProductionSheetVersions.Add(Version(sheetId, seed.UserId, 1, now));
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            Assert.Contains(reportedName, exception.ToString(), StringComparison.Ordinal);
            Assert.True(MovieCharacterProductionSheetService.IsVersionAllocationUniqueViolation(exception),
                "PostgreSQL reported the conflict as \"" + reportedName + "\" and the retry gate did not accept it.");
        }

        private static MovieCharacterProductionSheetVersion Version(Guid sheetId, Guid userId, int versionNumber, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            MovieCharacterProductionSheetId = sheetId,
            VersionNumber = versionNumber,
            SourceCharacterUpdatedAt = now,
            ProvenanceJson = "{}",
            ProvenanceHash = "postgres-index-naming-fixture",
            CreatedByUserId = userId,
            CreatedAt = now,
        };

        /// <summary>
        /// Swaps the model-built unique index for one carrying the name the migration declares, and
        /// returns the name PostgreSQL actually stored.
        /// </summary>
        private async Task<string> RecreateVersionIndexUnderDeclaredNameAsync(TaslimDbContext db)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();

            var autoName = await ReadAsync(connection,
                "select indexname from pg_indexes where schemaname = current_schema() and tablename = 'MovieCharacterProductionSheetVersions' and indexdef like '%UNIQUE%' "
                + VersionColumnsPredicate + " limit 1");

            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = "DROP INDEX IF EXISTS \"" + schema + "\".\"" + autoName + "\"";
                await drop.ExecuteNonQueryAsync();
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE UNIQUE INDEX \"" + DeclaredVersionIndexName + "\" ON \"" + schema
                    + "\".\"MovieCharacterProductionSheetVersions\" (\"MovieCharacterProductionSheetId\", \"VersionNumber\")";
                await create.ExecuteNonQueryAsync();
            }

            var stored = await ReadAsync(connection,
                "select indexname from pg_indexes where schemaname = current_schema() and tablename = 'MovieCharacterProductionSheetVersions' and indexname like 'IX_MovieCharacterProductionSheetVersions_MovieCharacterProduct%' limit 1");
            return stored ?? throw new InvalidOperationException("The version unique index is missing from the PostgreSQL catalog.");
        }

        private static async Task<string?> ReadAsync(System.Data.Common.DbConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? null : (string)value;
        }
    }

    private static async Task<Seed> SeedAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieProjectId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "character-sheet-owner",
            NormalizedUserName = "CHARACTER-SHEET-OWNER",
            Email = "character-sheet-owner@example.com",
            NormalizedEmail = "CHARACTER-SHEET-OWNER@EXAMPLE.COM",
            DisplayName = "Character Sheet Owner",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Workspaces.Add(new Workspace
        {
            Id = workspaceId,
            Name = "Character Sheet Workspace",
            Slug = $"character-sheet-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now,
        });
        var movie = new MovieProject
        {
            Id = movieProjectId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Title = "Character Sheet Film",
            Description = "A deterministic character-sheet race fixture.",
            DurationSeconds = 30,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now },
            Characters =
            [
                new MovieCharacter
                {
                    Id = characterId,
                    Name = "Mara",
                    Description = "A continuity fixture character.",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            ],
        };
        db.MovieProjects.Add(movie);
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UserId = userId,
            Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Seed(userId, characterId);
    }

    private sealed class VersionAllocationConflictInterceptor : SaveChangesInterceptor
    {
        private int injected;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref injected, 1) == 0)
                throw new DbUpdateException("UNIQUE constraint failed: MovieCharacterProductionSheetVersions.MovieCharacterProductionSheetId, MovieCharacterProductionSheetVersions.VersionNumber");

            return ValueTask.FromResult(result);
        }
    }

    private sealed record Seed(Guid UserId, Guid CharacterId);
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-parity QA for the world read model's media-family clause, raised but deliberately
/// left unasserted by #1184's matrix note: <c>#1177</c> inherits #1176's
/// <c>MimeType.StartsWith("image/")</c> with no <see cref="StringComparison"/>, while
/// <see cref="AssetContractMapper.ToDto"/> and the asset download route both use
/// <see cref="StringComparison.OrdinalIgnoreCase"/>.
///
/// The in-memory SQLite fixture used by #1176/#1184 cannot settle this, because SQL <c>LIKE</c>
/// is case-insensitive for ASCII there and case-sensitive on PostgreSQL - the deployed provider.
/// This lane therefore reads the same seeded rows through the same production code path on both
/// providers, using only a disposable schema in the CI test database.
///
/// Test-only. No migration, no production data, no provider call, no credential, no real user,
/// and nothing outside this file is added or changed.
/// </summary>
public sealed class MovieWorldReferenceMediaCasePostgresTests
{
    /// <summary>A MIME type whose family prefix is not lower case, as an uppercase HTTP header or a provider response could produce.</summary>
    private const string MixedCasePng = "Image/PNG";
    private const string LowercasePng = "image/png";
    private const string NonImagePdf = "application/pdf";

    private sealed record Flags(bool HasFile, bool CanPreview);

    private sealed record Fixture(
        Guid UserId,
        Guid MovieId,
        Guid WorkspaceId,
        Dictionary<string, Guid> AssetIdsByName);

    /// <summary>
    /// One movie, three reference rows: the mixed-case image under test, a lower-case image that
    /// must behave identically, and a non-image file that must never be advertised as previewable.
    /// The stored file's <c>ContentType</c> is set to the same text as <c>Asset.MimeType</c>, which
    /// is how <c>GeneratedAssetPublisher</c> populates the pair, so the download route sees the row
    /// exactly as the read models do.
    /// </summary>
    private static async Task<Fixture> SeedAsync(TaslimDbContext db)
    {
        var now = new DateTime(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"mime-{Guid.NewGuid():N}", DisplayName = "Media Case Owner", CreatedAt = now, UpdatedAt = now };
        user.NormalizedUserName = user.UserName.ToUpperInvariant();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Media Case Studio", Slug = $"media-case-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            Title = "Media Case Movie",
            Description = "A movie whose world rows differ only by MIME type casing.",
            DurationSeconds = 60,
            Status = MovieProjectStatuses.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Users.Add(user);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, UserId = user.Id, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(movie);
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movie.Id, UserId = user.Id, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });

        var ids = new Dictionary<string, Guid>();
        foreach (var (name, mimeType) in new[] { ("mixed", MixedCasePng), ("lower", LowercasePng), ("pdf", NonImagePdf) })
        {
            var storedFileId = Guid.NewGuid();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                OriginalFileName = $"{name}.bin",
                StoredFileName = $"{name}.bin",
                ContentType = mimeType,
                Extension = "bin",
                SizeBytes = 2048,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"synthetic/{name}.bin",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            });

            var assetId = Guid.NewGuid();
            ids[name] = assetId;
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = workspace.Id,
                CreatedByUserId = user.Id,
                StoredFileId = storedFileId,
                Name = name,
                AssetType = AssetTypes.File,
                MimeType = mimeType,
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });

            db.MovieLocations.Add(new MovieLocation
            {
                Id = Guid.NewGuid(),
                MovieProjectId = movie.Id,
                Name = $"Location {name}",
                Description = "A reference row.",
                ReferenceAssetId = assetId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return new Fixture(user.Id, movie.Id, workspace.Id, ids);
    }

    /// <summary>The fact under test, straight from the production projection #1177 changed.</summary>
    private static async Task<Dictionary<string, Flags>> WorldFlagsAsync(TaslimDbContext db, Fixture fixture)
    {
        var access = new WorkspaceAccessService(db);
        var service = new MovieStudioService(
            db,
            access,
            new MovieCollaborationAccess(db, access),
            null!, null!, null!, null!, null!, null!, null!, null!);

        var world = await service.GetWorldAsync(fixture.UserId, fixture.MovieId, CancellationToken.None)
            ?? throw new InvalidOperationException("Expected a world read model for the mounted movie.");

        var byId = world.Assets.ToDictionary(item => item.Id, item => new Flags(item.HasFile, item.CanPreview));
        return fixture.AssetIdsByName.ToDictionary(item => item.Key, item => byId.TryGetValue(item.Value, out var flags)
            ? flags
            : throw new InvalidOperationException($"The world read model dropped the {item.Key} reference row."));
    }

    /// <summary>
    /// The same rows through the Assets library read model - the consumer #1030 requires the world
    /// model to agree with, and the one whose rule is written case-insensitively.
    /// </summary>
    private static async Task<Dictionary<string, Flags>> LibraryFlagsAsync(TaslimDbContext db, Fixture fixture)
    {
        var assets = await db.Assets.AsNoTracking()
            .Include(item => item.StoredFile)
            .Where(item => item.WorkspaceId == fixture.WorkspaceId)
            .ToArrayAsync();
        var byId = assets.ToDictionary(item => item.Id, item =>
        {
            var dto = AssetContractMapper.ToDto(item);
            return new Flags(dto.HasFile, dto.CanPreview);
        });
        return fixture.AssetIdsByName.ToDictionary(item => item.Key, item => byId[item.Value]);
    }

    private static void AssertProvidersAgree(Dictionary<string, Flags> world, Dictionary<string, Flags> library)
    {
        foreach (var name in new[] { "mixed", "lower", "pdf" })
            Assert.True(
                world[name] == library[name],
                $"The world read model and the Assets library read model disagree on the {name} reference row (world {world[name]}, library {library[name]}).");
    }

    /// <summary>
    /// Baseline. On the provider the existing #1176/#1184 lanes use, the two read models agree on
    /// every row - including the mixed-case one, because SQLite's <c>LIKE</c> folds ASCII case.
    /// This is the leg that shows the divergence below is a provider fact, not a bad fixture.
    /// </summary>
    [Fact]
    public async Task Sqlite_world_projection_agrees_with_the_library_read_model_on_every_media_family()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var fixture = await SeedAsync(db);
        AssertProvidersAgree(await WorldFlagsAsync(db, fixture), await LibraryFlagsAsync(db, fixture));
    }

    /// <summary>
    /// The mechanism, stated as a fact rather than assumed from provider folklore: the identical
    /// <c>LIKE prefix</c> shape that PostgreSQL produces for <c>StartsWith("image/")</c> is
    /// case-sensitive, while the ordinal-ignore-case rule the other two readers use is not. SQLite
    /// folds ASCII case in <c>LIKE</c>; PostgreSQL does not, and <c>ILIKE</c> is the case-folded
    /// form. Both providers store the text itself verbatim.
    /// </summary>
    [DisposablePostgresFact]
    public async Task Like_prefix_semantics_differ_between_the_two_providers_for_one_row()
    {
        await using var sqlite = new SqliteConnection("DataSource=:memory:");
        await sqlite.OpenAsync();
        await using (var command = sqlite.CreateCommand())
        {
            command.CommandText = "SELECT CASE WHEN 'Image/PNG' LIKE 'image%' THEN 1 ELSE 0 END";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        await using var postgres = await OpenPostgresAsync();
        Assert.Equal(0, await PostgresScalarAsync(postgres, "SELECT ('Image/PNG' LIKE 'image%')::int"));
        Assert.Equal(1, await PostgresScalarAsync(postgres, "SELECT ('Image/PNG' ILIKE 'image%')::int"));
    }

    /// <summary>
    /// The contract under test. The world read model may not answer a different media family for
    /// the same row than the Assets library read model and the download route answer, whichever
    /// provider the app is deployed on. On PostgreSQL the mixed-case row is the divergence #1184
    /// flagged: the route serves it inline and the library advertises a preview, while the world
    /// projection's case-sensitive translated <c>StartsWith</c> withholds one.
    /// </summary>
    [DisposablePostgresFact]
    public async Task Postgres_world_projection_must_agree_with_the_library_read_model_on_every_media_family()
    {
        await using var scoped = await DisposableSchema.CreateAsync("media_case");
        var fixture = await SeedAsync(scoped.Db);
        AssertProvidersAgree(await WorldFlagsAsync(scoped.Db, fixture), await LibraryFlagsAsync(scoped.Db, fixture));
    }

    /// <summary>
    /// Rules out the other explanation for a divergence: that the database normalised the seeded
    /// text. The stored value must come back exactly as written, on the deployed provider.
    /// </summary>
    [DisposablePostgresFact]
    public async Task Postgres_stores_the_mixed_case_mime_type_verbatim()
    {
        await using var scoped = await DisposableSchema.CreateAsync("verbatim");
        var fixture = await SeedAsync(scoped.Db);
        var stored = await scoped.Db.Assets.AsNoTracking()
            .Where(item => item.Id == fixture.AssetIdsByName["mixed"])
            .Select(item => item.MimeType)
            .SingleAsync();
        Assert.Equal(MixedCasePng, stored);
    }

    private static async Task<NpgsqlConnection> OpenPostgresAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> PostgresScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// A schema created per run in the disposable <c>taslim_tests</c> database and dropped on
    /// dispose, matching the pattern the existing PostgreSQL lanes use. The model is created from
    /// the in-memory model only - no migration is applied and no other schema is touched.
    /// </summary>
    private sealed class DisposableSchema : IAsyncDisposable
    {
        public TaslimDbContext Db { get; }
        private string Schema { get; }

        private DisposableSchema(TaslimDbContext db, string schema)
        {
            Db = db;
            Schema = schema;
        }

        public static async Task<DisposableSchema> CreateAsync(string label)
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
            var schema = $"movie_world_media_case_{label}_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnectionStringBuilder(connectionString);
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\"";
                await create.ExecuteNonQueryAsync();
            }

            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
            var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DisposableSchema(db, schema);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            var admin = new NpgsqlConnectionStringBuilder(connectionString);
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

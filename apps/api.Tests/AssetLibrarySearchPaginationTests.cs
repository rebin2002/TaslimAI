using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Assets;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Media Library (asset library) search/filter pagination regressions.
///
/// The deployed provider is PostgreSQL, where the search filter is translated to
/// <c>ILIKE '%&lt;term&gt;%'</c>. PostgreSQL treats <c>%</c>, <c>_</c> and its default
/// escape character <c>\</c> inside that pattern as pattern syntax, so a member who
/// types a literal metacharacter into the Media Library search box gets rows that do
/// not contain their term - and loses rows that do - which also corrupts the
/// totalCount/totalPages pair the pager walks. SQLite, the provider the other asset
/// suites use, matches literally, so the divergence is invisible there. These
/// regressions pin both providers to literal-substring semantics and re-assert the
/// two pagination invariants the library relies on: a stable cursor across pages and
/// workspace isolation.
/// </summary>
public sealed class AssetLibrarySearchPaginationTests
{
    private const string PercentMatch = "Q4 50% growth plan";
    private const string PercentNearMiss = "Q4 500 growth plan";
    private const string UnderscoreMatch = "hero_v2 cover";
    private const string UnderscoreNearMiss = "heroXv2 cover";
    private const string BackslashMatch = "plan\\final sheet";
    private const string BackslashVictim = "plan%final sheet";
    private const string EscapeVictim = "the backlog note";
    private const string Unrelated = "quarterly budget review";
    private const string OtherWorkspaceMatch = "Q4 50% growth plan (Studio B)";

    private sealed record Fixture(
        TaslimDbContext Db,
        Guid UserId,
        Guid OutsiderId,
        Guid WorkspaceA,
        Guid WorkspaceB,
        Dictionary<string, Guid> IdsByName);

    private static async Task<Fixture> SeedAsync(TaslimDbContext db)
    {
        var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "librarian", NormalizedUserName = "LIBRARIAN", DisplayName = "Librarian", CreatedAt = now, UpdatedAt = now };
        var outsider = new ApplicationUser { Id = Guid.NewGuid(), UserName = "outsider", NormalizedUserName = "OUTSIDER", DisplayName = "Outsider", CreatedAt = now, UpdatedAt = now };
        var workspaceA = new Workspace { Id = Guid.NewGuid(), Name = "Studio A", Slug = $"studio-a-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var workspaceB = new Workspace { Id = Guid.NewGuid(), Name = "Studio B", Slug = $"studio-b-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now };
        var projectA = new Project { Id = Guid.NewGuid(), WorkspaceId = workspaceA.Id, Name = "Library Project", CreatedAt = now, UpdatedAt = now };
        var projectB = new Project { Id = Guid.NewGuid(), WorkspaceId = workspaceB.Id, Name = "Other Project", CreatedAt = now, UpdatedAt = now };

        db.Users.AddRange(user, outsider);
        db.Workspaces.AddRange(workspaceA, workspaceB);
        db.Projects.AddRange(projectA, projectB);
        // The caller is a member of BOTH workspaces, so a leak could only come from the
        // listing query itself and never from a missing membership check.
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceA.Id, UserId = user.Id, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceB.Id, UserId = user.Id, Role = WorkspaceRole.Member, JoinedAt = now });

        var ids = new Dictionary<string, Guid>();
        var created = now;
        void AddAsset(string name, Workspace workspace, Project project)
        {
            var id = Guid.NewGuid();
            ids[name] = id;
            created = created.AddSeconds(1);
            db.Assets.Add(new Asset
            {
                Id = id,
                WorkspaceId = workspace.Id,
                ProjectId = project.Id,
                CreatedByUserId = user.Id,
                Name = name,
                AssetType = AssetTypes.File,
                Status = AssetStatus.Active,
                CreatedAt = created,
                UpdatedAt = created,
            });
        }

        AddAsset(PercentMatch, workspaceA, projectA);
        AddAsset(PercentNearMiss, workspaceA, projectA);
        AddAsset(UnderscoreMatch, workspaceA, projectA);
        AddAsset(UnderscoreNearMiss, workspaceA, projectA);
        AddAsset(BackslashMatch, workspaceA, projectA);
        AddAsset(BackslashVictim, workspaceA, projectA);
        AddAsset(EscapeVictim, workspaceA, projectA);
        AddAsset(Unrelated, workspaceA, projectA);
        // The same literal term lives in the other workspace and must stay there.
        AddAsset(OtherWorkspaceMatch, workspaceB, projectB);

        await db.SaveChangesAsync();
        return new Fixture(db, user.Id, outsider.Id, workspaceA.Id, workspaceB.Id, ids);
    }

    private static Task<AssetListDto?> Search(
        Fixture fixture, Guid workspaceId, string term, int page = 1, int pageSize = 24) =>
        new AssetService(fixture.Db, new WorkspaceAccessService(fixture.Db))
            .ListAsync(fixture.UserId, new AssetFilter(workspaceId, null, null, AssetStatus.Active, term, null, page, pageSize));

    private static async Task<AssetListDto> ListAsync(Fixture fixture, Guid workspaceId, string term, int page = 1, int pageSize = 24) =>
        await Search(fixture, workspaceId, term, page, pageSize)
            ?? throw new InvalidOperationException("Expected a listing for a mounted workspace.");

    /// <summary>
    /// The contract every provider must satisfy: the search box returns the assets whose
    /// name literally contains the typed text, nothing more and nothing less.
    /// </summary>
    private static async Task AssertLiteralSubstringSemanticsAsync(Fixture fixture)
    {
        // A literal '%' must not widen into "any run of characters".
        var percent = await ListAsync(fixture, fixture.WorkspaceA, "50%");
        Assert.Equal(new[] { fixture.IdsByName[PercentMatch] }, percent.Items.Select(item => item.Id));
        Assert.Equal(1, percent.TotalCount);
        Assert.Equal(1, percent.TotalPages);

        // A literal '_' must not match an arbitrary single character.
        var underscore = await ListAsync(fixture, fixture.WorkspaceA, "hero_v2");
        Assert.Equal(new[] { fixture.IdsByName[UnderscoreMatch] }, underscore.Items.Select(item => item.Id));

        // A trailing '\' must not consume the closing wildcard of the generated pattern.
        var trailing = await ListAsync(fixture, fixture.WorkspaceA, "plan\\");
        Assert.Equal(new[] { fixture.IdsByName[BackslashMatch] }, trailing.Items.Select(item => item.Id));

        // A '\' must not silently escape the character that follows it.
        var escaping = await ListAsync(fixture, fixture.WorkspaceA, "\\back");
        Assert.Empty(escaping.Items);
        Assert.Equal(0, escaping.TotalCount);
    }

    [Fact]
    public async Task Sqlite_library_search_is_the_literal_substring_baseline()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var fixture = await SeedAsync(db);
        await AssertLiteralSubstringSemanticsAsync(fixture);
    }

    [DisposablePostgresFact]
    public async Task Postgres_library_search_must_match_the_same_literal_text_as_every_other_provider()
    {
        await using var scoped = await PostgresSchema.CreateAsync("literal");
        var fixture = await SeedAsync(scoped.Db);

        await AssertLiteralSubstringSemanticsAsync(fixture);

        // Pinning the same probe set that the SQLite baseline resolves literally keeps
        // the providers from drifting apart again, one search box at a time.
        var widened = await ListAsync(fixture, fixture.WorkspaceA, "plan%final");
        Assert.Equal(new[] { fixture.IdsByName[BackslashVictim] }, widened.Items.Select(item => item.Id));
    }

    [DisposablePostgresFact]
    public async Task Postgres_library_search_remains_literal_with_legacy_string_literals()
    {
        // A backslash SQL escape literal fails when standard_conforming_strings is off;
        // the non-backslash escape must work in both PostgreSQL modes.
        await using var scoped = await PostgresSchema.CreateAsync("legacy", legacyStringMode: true);
        var fixture = await SeedAsync(scoped.Db);

        await fixture.Db.Database.OpenConnectionAsync();
        try
        {
            await using var command = fixture.Db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SHOW standard_conforming_strings";
            Assert.Equal("off", await command.ExecuteScalarAsync());
        }
        finally
        {
            await fixture.Db.Database.CloseConnectionAsync();
        }

        await AssertLiteralSubstringSemanticsAsync(fixture);
        var percent = await ListAsync(fixture, fixture.WorkspaceA, "plan%final");
        Assert.Equal(new[] { fixture.IdsByName[BackslashVictim] }, percent.Items.Select(item => item.Id));
    }

    [DisposablePostgresFact]
    public async Task Postgres_library_search_pagination_walks_a_stable_cursor_over_the_filtered_set()
    {
        await using var scoped = await PostgresSchema.CreateAsync("cursor");
        var fixture = await SeedAsync(scoped.Db);

        var all = await ListAsync(fixture, fixture.WorkspaceA, "plan");
        Assert.Equal(4, all.TotalCount);
        Assert.True(all.Items.Count > 2, "The fixture must produce a multi-page filtered set.");

        var pages = (int)Math.Ceiling(all.TotalCount / 2d);
        var walked = new List<Guid>();
        for (var page = 1; page <= pages; page++)
        {
            var slice = await ListAsync(fixture, fixture.WorkspaceA, "plan", page: page, pageSize: 2);
            Assert.Equal(page, slice.Page);
            Assert.Equal(2, slice.PageSize);
            Assert.Equal(all.TotalCount, slice.TotalCount);
            Assert.Equal(pages, slice.TotalPages);
            Assert.True(slice.Items.Count is > 0 and <= 2, $"Page {page} returned {slice.Items.Count} rows.");
            walked.AddRange(slice.Items.Select(item => item.Id));
        }

        // Stable cursor: no row skipped, no row repeated, and the paged walk reproduces
        // the single-page ordering exactly, including when the same page is read again.
        Assert.Equal(all.Items.Select(item => item.Id), walked);
        Assert.Equal(walked.Count, walked.Distinct().Count());

        var replay = await ListAsync(fixture, fixture.WorkspaceA, "plan", page: 1, pageSize: 2);
        Assert.Equal(walked.Take(2), replay.Items.Select(item => item.Id));
    }

    [DisposablePostgresFact]
    public async Task Postgres_library_search_and_pagination_stay_inside_the_mounted_workspace()
    {
        await using var scoped = await PostgresSchema.CreateAsync("isolation");
        var fixture = await SeedAsync(scoped.Db);

        var first = await ListAsync(fixture, fixture.WorkspaceA, "50% growth");
        Assert.NotEmpty(first.Items);
        Assert.All(first.Items, item => Assert.Equal(fixture.WorkspaceA, item.WorkspaceId));
        Assert.DoesNotContain(first.Items, item => item.Name == OtherWorkspaceMatch);

        var second = await ListAsync(fixture, fixture.WorkspaceB, "50% growth");
        Assert.NotEmpty(second.Items);
        Assert.All(second.Items, item => Assert.Equal(fixture.WorkspaceB, item.WorkspaceId));
        Assert.DoesNotContain(second.Items, item => item.Name == PercentMatch);

        // Even a wildcard-only term cannot cross a workspace boundary, so a widened match
        // is a precision defect inside the caller's own library, never a cross-workspace leak.
        var wildcard = await ListAsync(fixture, fixture.WorkspaceA, "%");
        Assert.All(wildcard.Items, item => Assert.Equal(fixture.WorkspaceA, item.WorkspaceId));
        Assert.DoesNotContain(wildcard.Items, item => item.Name == OtherWorkspaceMatch);

        Assert.Null(await Search(fixture with { UserId = fixture.OutsiderId }, fixture.WorkspaceA, "%"));
    }

    /// <summary>
    /// The escaping contract itself, asserted without a database so it runs in every
    /// environment: each LIKE metacharacter in the typed term is prefixed with the
    /// declared escape character and everything else is passed through untouched.
    /// </summary>
    [Theory]
    [InlineData("quarterly", "%quarterly%")]
    [InlineData("50%", "%50!%%")]
    [InlineData("hero_v2", "%hero!_v2%")]
    [InlineData(@"plan\", @"%plan\%")]
    [InlineData(@"100%\_x", @"%100!%\!_x%")]
    [InlineData("wow!", "%wow!!%")]
    public void Search_pattern_neutralises_like_metacharacters(string term, string expected) =>
        Assert.Equal(expected, AssetSearchPattern.Contains(term));

    private sealed class PostgresSchema : IAsyncDisposable
    {
        public TaslimDbContext Db { get; }
        private string Schema { get; }

        private PostgresSchema(TaslimDbContext db, string schema)
        {
            Db = db;
            Schema = schema;
        }

        public static async Task<PostgresSchema> CreateAsync(string label, bool legacyStringMode = false)
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
            var schema = $"asset_search_{label}_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnectionStringBuilder(connectionString);
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\"";
                await create.ExecuteNonQueryAsync();
            }

            var scoped = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            if (legacyStringMode)
            {
                scoped.Pooling = false;
                scoped.Options = "-c standard_conforming_strings=off";
            }
            var options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(scoped.ConnectionString).Options;
            var db = new TaslimDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new PostgresSchema(db, schema);
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

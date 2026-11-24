using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Activity;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV-05 PostgreSQL locale proof (independent re-review of head 5df3254f): the fence folds
/// the stored JobType with SQL <c>lower()</c> so mixed-case rows behave like the canonical
/// <c>OrdinalIgnoreCase</c> in-memory rule. <c>lower()</c> is locale-sensitive - under a Turkish
/// collation ASCII <c>I</c> folds to dotless <c>ı</c> and the fold would stop matching the pinned
/// lowercase constants, silently reopening the fail-open window. These cases run on the same
/// PostgreSQL engine CI provisions (postgres:15 image, <c>taslim_tests</c>): the locale arm pins
/// the server collation to the ASCII-sane set and verifies the fold end-to-end, so a future
/// provider or image change to a locale like <c>tr_TR.UTF-8</c> fails here loudly instead of
/// leaking private rows; the fence arm seeds the mixed-case rows a case-sensitive database would
/// have kept visible and asserts the co-member projection hides them.
/// </summary>
public sealed class CreatorPrivacyFencePostgresLocaleTests : IAsyncLifetime
{
    // Private schema per suite so EnsureCreated cannot leak tables into the shared CI database.
    private readonly string schema = $"privacy_fence_locale_{Guid.NewGuid():N}";
    private DbContextOptions<TaslimDbContext> options = null!;
    private string schemaConnectionString = string.Empty;
    private bool ready;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        schemaConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
        options = new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(schemaConnectionString).Options;
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        ready = true;
    }

    public async Task DisposeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private TaslimDbContext NewContext() => new(options);

    /// <summary>
    /// The collations this proof is valid under: ASCII case folding in <c>lower()</c> matches the
    /// pinned lowercase job-type constants. Any other server locale (for example tr_TR.UTF-8, where
    /// <c>lower('I')</c> is dotless ı and the fold stops matching) fails the allowlist AND the
    /// functional fold assertions below, so the equivalence claim can never outlive the locale.
    /// Names are compared after normalization (lowercase, hyphens stripped) because PostgreSQL
    /// reports initdb locale names in its own canonical spelling: the hosted CI postgres:15
    /// service (run 45445603482, job 138628387109) reports datcollate 'en_US.utf8' — the same
    /// glibc en_US.UTF-8 locale, ASCII-sane — while a local initdb under C.UTF-8 reports
    /// 'C.UTF-8'. Normalization only folds spellings of the same locale identity; a genuinely
    /// different locale (tr_TR.utf8, de_DE.utf8, SQL_ASCII…) is still rejected by the name AND
    /// by the functional dotless-i canary below, which is the decisive gate.
    /// </summary>
    private static readonly string[] AsciiSaneCollations = ["c", "posix", "c.utf8", "enus.utf8"];

    private static string NormalizeCollationName(string name) =>
        name.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal);

    [DisposablePostgresFact]
    public async Task Postgres_locale_folds_ascii_job_types_like_the_canonical_rule()
    {
        Assert.True(ready);
        await using var db = NewContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        string collate = string.Empty;
        string ctype = string.Empty;
        string encoding = string.Empty;
        string version = string.Empty;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT datcollate, datctype, pg_encoding_to_char(encoding), current_setting('server_version') FROM pg_database WHERE datname = current_database()";
            await using var reader = await probe.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            collate = reader.GetString(0);
            ctype = reader.GetString(1);
            encoding = reader.GetString(2);
            version = reader.GetString(3);
        }

        Assert.Contains(NormalizeCollationName(collate), AsciiSaneCollations);
        Assert.Contains(NormalizeCollationName(ctype), AsciiSaneCollations);
        Assert.Equal("UTF8", encoding);

        await using (var fold = connection.CreateCommand())
        {
            fold.CommandText = """
                SELECT lower('I'),
                       lower('PRESENTATION.GENERATE') = 'presentation.generate',
                       lower('RESEARCH.GENERATE') = 'research.generate',
                       lower('SOCIAL.GENERATE') = 'social.generate',
                       lower('Image.Generate') = 'image.generate'
                """;
            await using var reader = await fold.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // The dotless-i canary: in a Turkish locale lower('I') is 'ı', which is NOT 'i'.
            Assert.Equal("i", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }
    }

    [DisposablePostgresFact]
    public async Task Mixed_case_private_rows_are_hidden_from_co_members_on_postgres()
    {
        Assert.True(ready);
        var viewerId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var setup = NewContext())
        {
            setup.Users.Add(new ApplicationUser { Id = viewerId, UserName = "fence-viewer", NormalizedUserName = "FENCE-VIEWER", DisplayName = "Fence Viewer", CreatedAt = now, UpdatedAt = now });
            setup.Users.Add(new ApplicationUser { Id = creatorId, UserName = "fence-creator", NormalizedUserName = "FENCE-CREATOR", DisplayName = "Fence Creator", CreatedAt = now, UpdatedAt = now });
            setup.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Fence Locale Workspace", Slug = $"fence-locale-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = viewerId, Role = WorkspaceRole.Member, JoinedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = creatorId, Role = WorkspaceRole.Member, JoinedAt = now });

            // Rows written by a writer that bypasses the enqueue normalization - the external or
            // legacy writers named in the finding. The database stores the casing it is given, so
            // only the SQL-side fold in the fence can make these behave like the canonical rule.
            setup.GenerationJobs.AddRange(
                FenceJob(creatorId, workspaceId, "RESEARCH.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "Presentation.Generate", projectId: null, now),
                FenceJob(creatorId, workspaceId, "SOCIAL.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "IMAGE.GENERATE", projectId: null, now));
            await setup.SaveChangesAsync();
        }

        await using var db = NewContext();
        var visibleToViewer = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), viewerId)
            .Select(item => item.JobType)
            .ToListAsync();

        // The three private classes stay creator-only even with non-canonical casing (fail closed);
        // the unrelated job type stays visible to the co-member (no over-hide).
        Assert.Equal(["IMAGE.GENERATE"], visibleToViewer);

        var visibleToCreator = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), creatorId)
            .Select(item => item.JobType)
            .ToListAsync();
        Assert.Equal(4, visibleToCreator.Count);
        Assert.Contains("RESEARCH.GENERATE", visibleToCreator);
        Assert.Contains("Presentation.Generate", visibleToCreator);
        Assert.Contains("SOCIAL.GENERATE", visibleToCreator);
    }

    [DisposablePostgresFact]
    public async Task Mixed_case_scoped_presentation_and_social_rows_stay_project_shared_on_postgres()
    {
        Assert.True(ready);
        var viewerId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using (var setup = NewContext())
        {
            setup.Users.Add(new ApplicationUser { Id = viewerId, UserName = "scoped-viewer", NormalizedUserName = "SCOPED-VIEWER", DisplayName = "Scoped Viewer", CreatedAt = now, UpdatedAt = now });
            setup.Users.Add(new ApplicationUser { Id = creatorId, UserName = "scoped-creator", NormalizedUserName = "SCOPED-CREATOR", DisplayName = "Scoped Creator", CreatedAt = now, UpdatedAt = now });
            setup.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Fence Scoped Workspace", Slug = $"fence-scoped-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = viewerId, Role = WorkspaceRole.Member, JoinedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = creatorId, Role = WorkspaceRole.Member, JoinedAt = now });
            setup.Projects.Add(new Project { Id = projectId, WorkspaceId = workspaceId, Name = "Fence project", CreatedAt = now, UpdatedAt = now });

            setup.GenerationJobs.AddRange(
                FenceJob(creatorId, workspaceId, "PRESENTATION.GENERATE", projectId, now),
                FenceJob(creatorId, workspaceId, "Social.Generate", projectId, now),
                FenceJob(creatorId, workspaceId, "RESEARCH.GENERATE", projectId, now));
            await setup.SaveChangesAsync();
        }

        await using var db = NewContext();
        var visibleToViewer = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), viewerId)
            .Select(item => item.JobType)
            .ToListAsync();

        // Project-scoped Presentation/Social keep the intentional sharing surface regardless of
        // casing, while project-scoped Research stays creator-private regardless of casing - the
        // mixed-case fold must not flip either arm of the rule.
        Assert.Equal(2, visibleToViewer.Count);
        Assert.Contains("PRESENTATION.GENERATE", visibleToViewer);
        Assert.Contains("Social.Generate", visibleToViewer);
        Assert.DoesNotContain("RESEARCH.GENERATE", visibleToViewer);
    }

    private static GenerationJob FenceJob(Guid creatorId, Guid workspaceId, string jobType, Guid? projectId, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        CreatedByUserId = creatorId,
        JobType = jobType,
        Status = GenerationJobStatus.Succeeded,
        InputJson = "{}",
        Title = $"locale probe {jobType}",
        CreatedAt = now,
    };
}

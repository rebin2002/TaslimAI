using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taslim.Api.Activity;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV-05 PostgreSQL collation proof: the shared SQL fence must classify the three ASCII
/// private-job identifiers deterministically even when the persisted JobType column has a locale
/// collation that differs from the database default. The Turkish ICU fixture makes an implicit
/// LOWER(JobType) turn the ASCII I in SOCIAL/PRESENTATION into dotless ı; the fence must override
/// that column collation with PostgreSQL's built-in C collation. Tests use only synthetic rows in
/// the disposable PostgreSQL schema provisioned by API CI.
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

    [DisposablePostgresFact]
    public async Task Postgres_shared_fence_overrides_a_Turkish_JobType_column_collation()
    {
        Assert.True(ready);
        await using var db = NewContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        // Some PostgreSQL builds do not ship ICU collations. The ordinary PostgreSQL privacy
        // cases still run there; when Turkish ICU is available, put it on the JobType column and
        // verify the shared query overrides the column-level locale.
        bool hasTurkishIcu;
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_collation WHERE collname = 'tr-x-icu' AND collprovider = 'i')";
            hasTurkishIcu = Convert.ToBoolean(await probe.ExecuteScalarAsync());
        }
        if (!hasTurkishIcu) return;

        await using (var alterColumn = connection.CreateCommand())
        {
            alterColumn.CommandText = "ALTER TABLE \"GenerationJobs\" ALTER COLUMN \"JobType\" TYPE character varying(100) COLLATE \"tr-x-icu\" USING \"JobType\"::character varying(100)";
            await alterColumn.ExecuteNonQueryAsync();
        }
        await using (var columnCollation = connection.CreateCommand())
        {
            columnCollation.CommandText = "SELECT collation_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'GenerationJobs' AND column_name = 'JobType'";
            Assert.Equal("tr-x-icu", Convert.ToString(await columnCollation.ExecuteScalarAsync()));
        }
        await using (var implicitFold = connection.CreateCommand())
        {
            implicitFold.CommandText = "SELECT lower('SOCIAL.GENERATE'::text COLLATE \"tr-x-icu\")";
            Assert.Equal("socıal.generate", Convert.ToString(await implicitFold.ExecuteScalarAsync()));
        }

        var viewerId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using (var setup = NewContext())
        {
            setup.Users.Add(new ApplicationUser { Id = viewerId, UserName = "turkish-viewer", NormalizedUserName = "TURKISH-VIEWER", DisplayName = "Turkish Viewer", CreatedAt = now, UpdatedAt = now });
            setup.Users.Add(new ApplicationUser { Id = creatorId, UserName = "turkish-creator", NormalizedUserName = "TURKISH-CREATOR", DisplayName = "Turkish Creator", CreatedAt = now, UpdatedAt = now });
            setup.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Turkish Fence Workspace", Slug = $"fence-turkish-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = viewerId, Role = WorkspaceRole.Member, JoinedAt = now });
            setup.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = creatorId, Role = WorkspaceRole.Member, JoinedAt = now });
            setup.Projects.Add(new Project { Id = projectId, WorkspaceId = workspaceId, Name = "Turkish fence project", CreatedAt = now, UpdatedAt = now });
            setup.GenerationJobs.AddRange(
                FenceJob(creatorId, workspaceId, "RESEARCH.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "PRESENTATION.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "SOCIAL.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "IMAGE.GENERATE", projectId: null, now),
                FenceJob(creatorId, workspaceId, "RESEARCH.GENERATE", projectId, now),
                FenceJob(creatorId, workspaceId, "PRESENTATION.GENERATE", projectId, now),
                FenceJob(creatorId, workspaceId, "SOCIAL.GENERATE", projectId, now));
            await setup.SaveChangesAsync();
        }

        var visibleToViewer = await CreatorPrivacyFence.VisibleToMember(
                db.GenerationJobs.AsNoTracking(), viewerId, db.Database.ProviderName)
            .Select(item => new { item.JobType, item.ProjectId })
            .ToListAsync();
        Assert.Equal(3, visibleToViewer.Count);
        Assert.Contains(visibleToViewer, item => item.JobType == "IMAGE.GENERATE" && item.ProjectId is null);
        Assert.Contains(visibleToViewer, item => item.JobType == "PRESENTATION.GENERATE" && item.ProjectId == projectId);
        Assert.Contains(visibleToViewer, item => item.JobType == "SOCIAL.GENERATE" && item.ProjectId == projectId);
        Assert.DoesNotContain(visibleToViewer, item => item.JobType == "RESEARCH.GENERATE");
        Assert.DoesNotContain(visibleToViewer, item => item.JobType == "PRESENTATION.GENERATE" && item.ProjectId is null);
        Assert.DoesNotContain(visibleToViewer, item => item.JobType == "SOCIAL.GENERATE" && item.ProjectId is null);

        var visibleToCreator = await CreatorPrivacyFence.VisibleToMember(
                db.GenerationJobs.AsNoTracking(), creatorId, db.Database.ProviderName)
            .CountAsync();
        Assert.Equal(7, visibleToCreator);
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
        var visibleToViewer = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), viewerId, db.Database.ProviderName)
            .Select(item => item.JobType)
            .ToListAsync();

        // The three private classes stay creator-only even with non-canonical casing (fail closed);
        // the unrelated job type stays visible to the co-member (no over-hide).
        Assert.Equal(["IMAGE.GENERATE"], visibleToViewer);

        var visibleToCreator = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), creatorId, db.Database.ProviderName)
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
        var visibleToViewer = await CreatorPrivacyFence.VisibleToMember(db.GenerationJobs.AsNoTracking(), viewerId, db.Database.ProviderName)
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

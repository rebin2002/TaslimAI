using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using System.IO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV-04 seam regression: the project overview endpoint (<c>GET /api/projects/{id}/overview</c>)
/// renders <see cref="GenerationJob"/> rows through the very same <see cref="ActivityContractMapper"/>
/// as the Activity Center, so it must honour the very same creator-private fence. The shipped invariant
/// in <c>GenerationJobExecution.IsPrivateExecutionRecord</c> hides a Research record from everyone but
/// its creator regardless of <c>ProjectId</c>, because its result JSON embeds report text derived from
/// that creator's private uploads; the Activity Center mirrors that rule
/// (<c>VisibleToMember</c>) and the job read model enforces it, but the overview path historically
/// queried <c>db.GenerationJobs</c> with only project and workspace predicates, so a co-member could
/// read another member's project-scoped research record out of <c>counts.activity</c> and
/// <c>recentActivity</c> - a disclosure on a fourth surface while the first three stayed closed.
/// These tests pin the observable HTTP behaviour of that surface and keep the intentionally shared
/// classes (image/document output, and a member's own research record) visible, so the fix cannot
/// silently turn project sharing off. Synthetic seeded rows only: the queue worker stays disabled, so
/// no provider, project execution, migration or paid path is involved.
/// </summary>
public sealed class ProjectOverviewCreatorPrivacyHttpApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-project-overview-creator-privacy-{Guid.NewGuid():N}.db");

    public ProjectOverviewCreatorPrivacyHttpApiFactory()
    {
        using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("GenerationJobs:WorkerEnabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={databasePath};Cache=Shared;Default Timeout=30"));
        });
    }

    /// <summary>
    /// Removes the synthetic SQLite file and its WAL sidecars when xUnit retires the class, mirroring
    /// <c>ActivityUnscopedCreatorPrivacyHttpApiFactory</c>. Best-effort: a lingering shared-cache handle
    /// must never turn a green privacy run red.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        base.Dispose(disposing);
    }
}

public sealed class ProjectOverviewCreatorPrivacyHttpTests : IClassFixture<ProjectOverviewCreatorPrivacyHttpApiFactory>
{
    private const string PrivateResearchTitle = "Private research brief about synthetic uploads";
    private const string OwnResearchTitle = "My own research brief in the same project";
    private const string SharedImageTitle = "Shared project image render";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ProjectOverviewCreatorPrivacyHttpApiFactory factory;

    public ProjectOverviewCreatorPrivacyHttpTests(ProjectOverviewCreatorPrivacyHttpApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ProjectOverview_Hides_Another_Members_Private_Research_Record_From_Activity_And_Counts()
    {
        var pair = await SetupProjectPair("overview-list");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var overview = await memberClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");

        // The other member's creator-private research record must not be rendered at all...
        var ids = overview.GetProperty("recentActivity").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("jobId").GetString()!))
            .ToArray();
        Assert.DoesNotContain(pair.CreatorResearchJobId, ids);
        Assert.Empty(overview.GetProperty("recentActivity").EnumerateArray()
            .Where(item => item.GetProperty("title").GetString() == PrivateResearchTitle));

        // ...and must not be counted, otherwise the count alone discloses its existence.
        Assert.Equal(2, overview.GetProperty("counts").GetProperty("activity").GetInt32());

        // The intentionally shared image job stays, and so does the member's own research record.
        Assert.Contains(pair.SharedImageJobId, ids);
        Assert.Contains(pair.OwnResearchJobId, ids);
    }

    [Fact]
    public async Task ProjectOverview_Keeps_The_Creators_Own_Research_Record_And_Hides_The_Other_Members()
    {
        var pair = await SetupProjectPair("overview-creator");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);

        var overview = await creatorClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");

        var ids = overview.GetProperty("recentActivity").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("jobId").GetString()!))
            .ToArray();
        // Creator privacy is symmetric, not a one-way rule: B keeps their own research record, the
        // shared image job stays, and A's research record is private from B exactly as B's is from A.
        Assert.Contains(pair.CreatorResearchJobId, ids);
        Assert.DoesNotContain(pair.OwnResearchJobId, ids);
        Assert.Contains(pair.SharedImageJobId, ids);
        Assert.Equal(2, overview.GetProperty("counts").GetProperty("activity").GetInt32());
    }

    [Fact]
    public async Task ProjectOverview_And_ActivityCenter_Agree_On_The_Same_Private_Set()
    {
        var pair = await SetupProjectPair("overview-agreement");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var activity = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        var overview = await memberClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");

        Assert.NotNull(activity);
        var activityIds = activity!.Items.Select(item => item.JobId).ToHashSet();
        var overviewIds = overview.GetProperty("recentActivity").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("jobId").GetString()!))
            .ToHashSet();

        // Every project the member can see is also visible in their activity feed: the fence is one
        // rule, not two. And the private record is absent from both surfaces.
        Assert.True(overviewIds.IsSubsetOf(activityIds), $"overview leaked ids not present in activity: {string.Join(",", overviewIds.Except(activityIds))}");
        Assert.DoesNotContain(pair.CreatorResearchJobId, activityIds);
        Assert.DoesNotContain(pair.CreatorResearchJobId, overviewIds);
    }

    [Fact]
    public async Task ProjectOverview_Rejects_A_Non_Member_Without_Exposing_The_Private_Record()
    {
        var pair = await SetupProjectPair("overview-outsider");
        using var outsiderClient = factory.CreateClient();
        await Register(outsiderClient, $"overview-outsider-{Guid.NewGuid():N}@example.com");

        var response = await outsiderClient.GetAsync($"/api/projects/{pair.ProjectId}/overview");

        Assert.False(response.IsSuccessStatusCode);
    }

    private async Task<ProjectOverviewPair> SetupProjectPair(string label)
    {
        using var creatorClient = factory.CreateClient();
        var creatorEmail = $"{label}-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creatorEmail);
        using var memberClient = factory.CreateClient();
        var noncreatorEmail = $"{label}-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreatorEmail);

        var workspaceId = creatorAuth.PersonalWorkspace.Id;
        var projectId = Guid.NewGuid();
        var creatorResearchJobId = Guid.NewGuid();
        var ownResearchJobId = Guid.NewGuid();
        var sharedImageJobId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                UserId = noncreatorAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            db.Projects.Add(new Project
            {
                Id = projectId,
                WorkspaceId = workspaceId,
                Name = $"{label} scoped project",
                Type = ProjectTypes.General,
                Status = ProjectStatuses.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            // Another member's research record: creator-private even though it is project-scoped.
            db.GenerationJobs.Add(NewJob(creatorResearchJobId, workspaceId, creatorAuth.User.Id, projectId, GenerationJobTypes.ResearchGenerate, PrivateResearchTitle, "{\"report\":\"creator-private\"}", GenerationJobStatus.Succeeded, 100));
            // The member's own research record: always visible to themselves.
            db.GenerationJobs.Add(NewJob(ownResearchJobId, workspaceId, noncreatorAuth.User.Id, projectId, GenerationJobTypes.ResearchGenerate, OwnResearchTitle, "{\"report\":\"mine\"}", GenerationJobStatus.Succeeded, 100));
            // An intentionally shared class: project-scoped image output stays shared.
            db.GenerationJobs.Add(NewJob(sharedImageJobId, workspaceId, creatorAuth.User.Id, projectId, GenerationJobTypes.ImageGenerate, SharedImageTitle, "{}", GenerationJobStatus.Succeeded, 100));
            await db.SaveChangesAsync();
        }

        return new ProjectOverviewPair(creatorEmail, noncreatorEmail, workspaceId, projectId, creatorResearchJobId, ownResearchJobId, sharedImageJobId);
    }

    private static GenerationJob NewJob(Guid id, Guid workspaceId, Guid creatorUserId, Guid? projectId, string jobType, string title, string resultJson, GenerationJobStatus status, int progress) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        CreatedByUserId = creatorUserId,
        JobType = jobType,
        Status = status,
        ProgressPercent = progress,
        Title = title,
        InputJson = "{}",
        ResultJson = resultJson,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = status == GenerationJobStatus.Succeeded ? DateTime.UtcNow : null,
        ConcurrencyToken = Guid.NewGuid(),
    };

    private async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Project Overview Privacy Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task Login(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password = "StrongPassword!123" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: WebJson);
        return await client.SendAsync(request);
    }

    private sealed record ProjectOverviewPair(
        string CreatorEmail,
        string NoncreatorEmail,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid CreatorResearchJobId,
        Guid OwnResearchJobId,
        Guid SharedImageJobId);
}

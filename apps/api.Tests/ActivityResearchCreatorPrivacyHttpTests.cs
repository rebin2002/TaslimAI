using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
/// Isolated host for the issue #331 creator-private Research activity regression. The queue worker
/// stays disabled so seeded synthetic records are never claimed or executed and no provider,
/// project, migration or paid path is ever involved.
/// </summary>
public sealed class ActivityResearchCreatorPrivacyHttpApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-activity-research-creator-privacy-{Guid.NewGuid():N}.db");

    public ActivityResearchCreatorPrivacyHttpApiFactory()
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
}

/// <summary>
/// Synthetic HTTP regression for issue #331: Research generation records are creator-private in the
/// job read model, so the Activity Center must not disclose another member's Research title,
/// progress or status, job id, or linked asset id through the activity list, its totals, the unread
/// badge, the direct read endpoint, or mark-all-read. Creator visibility must be preserved, and
/// intentionally shared non-Research activity must keep its existing workspace visibility.
/// Deliberately its own file and its own fixture: <c>apps/api/Activity/ActivityCenterService.cs</c>
/// and <c>apps/api.Tests/ActivityCenterTests.cs</c> are owned by PR #323/#327/#872 lanes, and
/// <c>apps/api/Controllers/ActivityController.cs</c> plus
/// <c>ActivityControllerPrincipalTests.cs</c> are owned by PR #634. This lane drives only the
/// public HTTP surface and adds no new types to those files.
/// </summary>
public sealed class ActivityResearchCreatorPrivacyHttpTests : IClassFixture<ActivityResearchCreatorPrivacyHttpApiFactory>
{
    private const string PrivateResearchTitle = "Private research report on synthetic quarterly figures";
    private const string SharedImageTitle = "Shared workspace image";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ActivityResearchCreatorPrivacyHttpApiFactory factory;

    public ActivityResearchCreatorPrivacyHttpTests(ActivityResearchCreatorPrivacyHttpApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ActivityList_Hides_AnotherMembers_Research_Job_From_Items_And_Totals()
    {
        var pair = await SetupWorkspacePair("creator-privacy-list");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var memberRaw = await memberClient.GetStringAsync($"/api/activity?workspaceId={pair.WorkspaceId}");
        var memberList = JsonSerializer.Deserialize<ActivityListDto>(memberRaw, WebJson)!;

        // The sensitive surface: title, progress/status, job id and linked asset id must be absent.
        Assert.Equal(pair.SharedImageJobId, Assert.Single(memberList.Items).JobId);
        Assert.Equal(1, memberList.TotalCount);
        Assert.DoesNotContain(memberList.Items, item => item.JobId == pair.ResearchJobId);
        Assert.DoesNotContain(PrivateResearchTitle, memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchJobId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchJobId.ToString("N"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchAssetId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchAssetId.ToString("N"), memberRaw, StringComparison.OrdinalIgnoreCase);

        // The creator keeps positive visibility of their own Research record.
        var creatorRaw = await creatorClient.GetStringAsync($"/api/activity?workspaceId={pair.WorkspaceId}");
        var creatorList = JsonSerializer.Deserialize<ActivityListDto>(creatorRaw, WebJson)!;
        Assert.Equal(2, creatorList.TotalCount);
        var privateItem = Assert.Single(creatorList.Items, item => item.JobId == pair.ResearchJobId);
        Assert.Equal(PrivateResearchTitle, privateItem.Title);
        Assert.Equal("research", privateItem.JobType);
        Assert.Equal("Completed", privateItem.Status);
        Assert.Equal(100, privateItem.ProgressPercent);
        Assert.Equal(pair.ResearchAssetId, privateItem.AssetId);
    }

    [Fact]
    public async Task Unread_Badge_And_List_Unread_Count_Exclude_Private_Research_Job()
    {
        var pair = await SetupWorkspacePair("creator-privacy-unread");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        // Before any acknowledgement: the member sees exactly the shared record, not the private one.
        var memberBadge = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={pair.WorkspaceId}");
        Assert.Equal(1, memberBadge.GetProperty("unreadCount").GetInt32());
        var memberList = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        Assert.Equal(1, memberList!.UnreadCount);

        // Mark-all-read as the member must acknowledge only the shared record and must not be able
        // to consume (or write state against) the hidden private record.
        var markAll = await SendWithCsrf(memberClient, HttpMethod.Post, "/api/activity/read-all", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, markAll.StatusCode);
        var badgeAfterMemberMarkAll = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={pair.WorkspaceId}");
        Assert.Equal(0, badgeAfterMemberMarkAll.GetProperty("unreadCount").GetInt32());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.ResearchJobId));
        }

        // The creator's own unread surface still includes their Research record until they read it.
        var creatorBadge = await creatorClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={pair.WorkspaceId}");
        Assert.Equal(2, creatorBadge.GetProperty("unreadCount").GetInt32());
        var creatorMarkAll = await SendWithCsrf(creatorClient, HttpMethod.Post, "/api/activity/read-all", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, creatorMarkAll.StatusCode);
        var creatorBadgeAfter = await creatorClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={pair.WorkspaceId}");
        Assert.Equal(0, creatorBadgeAfter.GetProperty("unreadCount").GetInt32());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.True(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.CreatorUserId && read.GenerationJobId == pair.ResearchJobId));
        }
    }

    [Fact]
    public async Task Direct_Read_Rejects_Private_Research_Job_For_Noncreator_But_Allows_Creator_And_Shared_Activity()
    {
        var pair = await SetupWorkspacePair("creator-privacy-direct-read");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        // A member who cannot see the record must not be able to acknowledge it either: the job is
        // indistinguishable from one that does not exist for them, so the endpoint fails closed.
        var memberPrivateRead = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{pair.ResearchJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.Forbidden, memberPrivateRead.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.ResearchJobId));
        }

        // Intentionally shared non-Research activity keeps its existing behaviour for the member.
        var memberSharedRead = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{pair.SharedImageJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, memberSharedRead.StatusCode);

        // Positive creator visibility: the creator can read their own private record.
        var creatorPrivateRead = await SendWithCsrf(creatorClient, HttpMethod.Post, $"/api/activity/{pair.ResearchJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, creatorPrivateRead.StatusCode);
        var creatorList = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        Assert.True(Assert.Single(creatorList!.Items, item => item.JobId == pair.ResearchJobId).IsRead);
    }

    [Fact]
    public async Task Filtering_By_Research_JobType_Does_Not_Leak_Private_Records()
    {
        var pair = await SetupWorkspacePair("creator-privacy-jobtype");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var memberFiltered = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&jobType={GenerationJobTypes.ResearchGenerate}");
        Assert.NotNull(memberFiltered);
        Assert.Empty(memberFiltered!.Items);
        Assert.Equal(0, memberFiltered.TotalCount);
        // The unread badge is workspace-wide rather than filter-scoped (existing behaviour, left
        // untouched here), so the member's only unread record stays the shared image, never the
        // hidden private Research job.
        Assert.Equal(1, memberFiltered.UnreadCount);

        var creatorFiltered = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&jobType={GenerationJobTypes.ResearchGenerate}");
        Assert.NotNull(creatorFiltered);
        Assert.Equal(pair.ResearchJobId, Assert.Single(creatorFiltered!.Items).JobId);
        Assert.Equal(1, creatorFiltered.TotalCount);
        Assert.Equal(2, creatorFiltered.UnreadCount);
    }

    private sealed record ActivityPrivacyPair(
        string CreatorEmail,
        string NoncreatorEmail,
        Guid CreatorUserId,
        Guid NoncreatorUserId,
        Guid WorkspaceId,
        Guid ResearchJobId,
        Guid ResearchAssetId,
        Guid SharedImageJobId);

    /// <summary>
    /// Registers two synthetic accounts, joins the second to the first account's personal workspace
    /// as a plain member, and seeds one creator-private Research record (with the linked asset that
    /// the activity feed resolves) plus one intentionally shared image record straight into the
    /// database, so no provider or paid path is involved.
    /// </summary>
    private async Task<ActivityPrivacyPair> SetupWorkspacePair(string label)
    {
        using var creatorClient = factory.CreateClient();
        var creatorEmail = $"{label}-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creatorEmail);

        using var memberClient = factory.CreateClient();
        var noncreatorEmail = $"{label}-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreatorEmail);

        var workspaceId = creatorAuth.PersonalWorkspace.Id;
        var researchJobId = Guid.NewGuid();
        var researchAssetId = Guid.NewGuid();
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
            db.GenerationJobs.Add(NewJob(researchJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ResearchGenerate, PrivateResearchTitle, GenerationJobStatus.Succeeded, 100));
            db.GenerationJobs.Add(NewJob(sharedImageJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ImageGenerate, SharedImageTitle, GenerationJobStatus.Queued, 0));
            // The linked asset id is part of the disclosure surface: the activity item resolves the
            // asset produced by the job, so the Research asset must disappear with the record.
            db.Assets.Add(new Asset
            {
                Id = researchAssetId,
                WorkspaceId = workspaceId,
                CreatedByUserId = creatorAuth.User.Id,
                SourceGenerationJobId = researchJobId,
                Name = "private-research-report",
                AssetType = AssetTypes.Research,
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new ActivityPrivacyPair(creatorEmail, noncreatorEmail, creatorAuth.User.Id, noncreatorAuth.User.Id, workspaceId, researchJobId, researchAssetId, sharedImageJobId);
    }

    private static GenerationJob NewJob(Guid id, Guid workspaceId, Guid creatorUserId, string jobType, string title, GenerationJobStatus status, int progress) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        CreatedByUserId = creatorUserId,
        JobType = jobType,
        Status = status,
        ProgressPercent = progress,
        InputJson = "{}",
        Title = title,
        CreatedAt = DateTime.UtcNow,
        QueuedAt = DateTime.UtcNow,
        CompletedAt = status == GenerationJobStatus.Succeeded ? DateTime.UtcNow : null,
        ConcurrencyToken = Guid.NewGuid(),
    };

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Activity Privacy Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

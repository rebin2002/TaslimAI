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
/// Synthetic regression for issue #331: Research generation records are creator-private in
/// <c>GenerationJobService</c>, so the Activity Center must not surface another member's Research
/// title, progress/status, job id or linked asset id through the activity list, the unread counts,
/// the direct unread badge, or the mark-read endpoints.
/// Deliberately kept in its own file: <c>apps/api/Activity/ActivityCenterService.cs</c> and
/// <c>apps/api.Tests/ActivityCenterTests.cs</c> are owned by PR #323/#327, and
/// <c>apps/api/Controllers/ActivityController.cs</c> plus <c>ActivityControllerPrincipalTests.cs</c>
/// are owned by PR #634. This file only adds new types and drives the public HTTP surface.
/// </summary>
public sealed class ActivityResearchPrivacyNoWorkerApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-activity-research-privacy-{Guid.NewGuid():N}.db");

    public ActivityResearchPrivacyNoWorkerApiFactory()
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
        // The queue worker stays off so seeded synthetic records are never executed and no
        // provider is ever contacted.
        builder.UseSetting("GenerationJobs:WorkerEnabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite($"Data Source={databasePath};Cache=Shared;Default Timeout=30"));
        });
    }
}

public sealed class ActivityResearchPrivacyTests : IClassFixture<ActivityResearchPrivacyNoWorkerApiFactory>
{
    private const string PrivateResearchTitle = "Private research report on synthetic quarterly figures";
    private const string SharedImageTitle = "Shared workspace image";

    private readonly ActivityResearchPrivacyNoWorkerApiFactory factory;

    public ActivityResearchPrivacyTests(ActivityResearchPrivacyNoWorkerApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Noncreator_cannot_see_private_research_job_through_activity_list_or_counts()
    {
        var (creatorEmail, noncreator, workspaceId, researchJobId, imageJobId) = await SetupWorkspacePair("activity-research-list");
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, creatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, noncreator.User.Email);

        // The list must not expose the Research title, progress, job id or linked asset id to a
        // noncreator member of the same workspace.
        var memberList = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={workspaceId}");
        Assert.NotNull(memberList);
        Assert.Equal(imageJobId, Assert.Single(memberList!.Items).JobId);
        Assert.Equal(1, memberList.TotalCount);
        Assert.DoesNotContain(memberList.Items, item => item.JobId == researchJobId);
        var memberRaw = await memberClient.GetStringAsync($"/api/activity?workspaceId={workspaceId}");
        Assert.DoesNotContain(researchJobId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateResearchTitle, memberRaw, StringComparison.OrdinalIgnoreCase);

        // The unread badge and its count must not include the private Research record either.
        var memberUnread = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={workspaceId}");
        Assert.Equal(1, memberUnread.GetProperty("unreadCount").GetInt32());
        var memberUnreadList = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={workspaceId}");
        Assert.Equal(1, memberUnreadList!.UnreadCount);

        // Asking for the hidden job type explicitly must yield an empty page, not a leak.
        var memberResearchFilter = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={workspaceId}&jobType={GenerationJobTypes.ResearchGenerate}");
        Assert.NotNull(memberResearchFilter);
        Assert.Empty(memberResearchFilter!.Items);
        Assert.Equal(0, memberResearchFilter.TotalCount);

        // The creator still sees their own Research record, and intentionally shared non-Research
        // activity stays visible to the member.
        var creatorList = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={workspaceId}");
        Assert.NotNull(creatorList);
        Assert.Contains(creatorList!.Items, item => item.JobId == researchJobId && item.JobType == "research" && item.Title == PrivateResearchTitle);
        Assert.Contains(creatorList.Items, item => item.JobId == imageJobId);
        Assert.Equal(2, creatorList.TotalCount);
    }

    [Fact]
    public async Task Noncreator_cannot_mark_private_research_job_read_but_keeps_shared_activity_readable()
    {
        var (creatorEmail, noncreator, workspaceId, researchJobId, imageJobId) = await SetupWorkspacePair("activity-research-read");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, noncreator.User.Email);

        // Marking the hidden Research job read must fail closed instead of writing a read state
        // that confirms the record exists.
        var denied = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{researchJobId}/read", new { workspaceId });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == noncreator.User.Id && read.GenerationJobId == researchJobId));
        }

        // Mark-all-read must not silently consume the private Research record either.
        var markAll = await SendWithCsrf(memberClient, HttpMethod.Post, "/api/activity/read-all", new { workspaceId });
        Assert.Equal(HttpStatusCode.OK, markAll.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == noncreator.User.Id && read.GenerationJobId == researchJobId));
        }

        var unreadAfterMarkAll = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={workspaceId}");
        Assert.Equal(0, unreadAfterMarkAll.GetProperty("unreadCount").GetInt32());

        // Intentionally shared non-Research activity remains readable by the member.
        var imageRead = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{imageJobId}/read", new { workspaceId });
        Assert.Equal(HttpStatusCode.OK, imageRead.StatusCode);
    }

    private sealed record ActivityPair(string CreatorEmail, AuthResponse NoncreatorAuth, Guid WorkspaceId, Guid ResearchJobId, Guid ImageJobId);

    /// <summary>
    /// Registers two synthetic accounts, joins the second one to the first one's workspace as a plain
    /// member, and seeds one creator-private Research record plus one intentionally shared image
    /// record directly in the database, so no provider, project, or paid path is involved.
    /// </summary>
    private async Task<ActivityPair> SetupWorkspacePair(string label)
    {
        using var creatorClient = factory.CreateClient();
        var creator = $"{label}-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creator);
        using var memberClient = factory.CreateClient();
        var noncreator = $"{label}-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreator);
        var workspaceId = creatorAuth.PersonalWorkspace.Id;

        var researchJobId = Guid.NewGuid();
        var imageJobId = Guid.NewGuid();
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
            // Creator-private: a finished Research report whose title, progress and linked asset are
            // the sensitive surface under test.
            db.GenerationJobs.Add(NewJob(researchJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ResearchGenerate, PrivateResearchTitle, GenerationJobStatus.Succeeded, 100));
            // Intentionally shared: non-Research activity must keep its existing visibility.
            db.GenerationJobs.Add(NewJob(imageJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ImageGenerate, SharedImageTitle, GenerationJobStatus.Queued, 0));
            await db.SaveChangesAsync();
        }

        return new ActivityPair(creator, noncreatorAuth, workspaceId, researchJobId, imageJobId);
    }

    private static GenerationJob NewJob(Guid id, Guid workspaceId, Guid ownerId, string jobType, string title, GenerationJobStatus status, int progress) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        CreatedByUserId = ownerId,
        JobType = jobType,
        Status = status,
        ProgressPercent = progress,
        InputJson = "{}",
        Title = title,
        CreatedAt = DateTime.UtcNow,
        QueuedAt = DateTime.UtcNow,
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

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
/// GATE-PRIV-01 seam regression: the creator-private classes that the job read model hides
/// (`GenerationJobExecution.IsPrivateExecutionRecord`: Research, unscoped Presentation, unscoped
/// Social) must be equally invisible through the public Activity Center HTTP surface, for every one
/// of its paths - the list and its totals, the unread badge, the direct read endpoint and
/// mark-all-read - while the creator keeps full visibility and project-scoped or intentionally
/// shared records stay shared. Mirrors the assertion families already used for Research in
/// `ActivityResearchCreatorPrivacyHttpTests`, so the two surfaces are proved the same way.
/// Synthetic seeded rows only: the queue worker stays disabled, so no provider, project execution,
/// migration or paid path is involved.
/// </summary>
public sealed class ActivityUnscopedCreatorPrivacyHttpApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-activity-unscoped-creator-privacy-{Guid.NewGuid():N}.db");

    public ActivityUnscopedCreatorPrivacyHttpApiFactory()
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
    /// Removes the synthetic SQLite file and its WAL sidecars when xUnit retires the class. The
    /// surrounding suite otherwise leaves one database per fixture instantiation in the temp
    /// directory; this lane is the file's only owner, so it cleans up after itself. Best-effort by
    /// design: a lingering shared-cache handle must never turn a green privacy run red.
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

public sealed class ActivityUnscopedCreatorPrivacyHttpTests : IClassFixture<ActivityUnscopedCreatorPrivacyHttpApiFactory>
{
    private const string PrivateSocialTitle = "Private unscoped social announcement about synthetic earnings";
    private const string PrivatePresentationTitle = "Private unscoped presentation export of synthetic figures";
    private const string ScopedSocialTitle = "Project scoped social post for the workspace";
    private const string SharedImageTitle = "Shared workspace image";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ActivityUnscopedCreatorPrivacyHttpApiFactory factory;

    public ActivityUnscopedCreatorPrivacyHttpTests(ActivityUnscopedCreatorPrivacyHttpApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task ActivityList_Hides_Unscoped_Social_And_Presentation_Jobs_From_Items_And_Totals()
    {
        var pair = await SetupWorkspacePair("unscoped-list");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var memberRaw = await memberClient.GetStringAsync($"/api/activity?workspaceId={pair.WorkspaceId}");
        var memberList = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");

        Assert.NotNull(memberList);
        // Only the intentionally shared records remain: the image job and the project-scoped social job.
        Assert.Equal(2, memberList!.TotalCount);
        Assert.DoesNotContain(memberList.Items, item => item.JobId == pair.UnscopedSocialJobId);
        Assert.DoesNotContain(memberList.Items, item => item.JobId == pair.UnscopedPresentationJobId);
        Assert.DoesNotContain(PrivateSocialTitle, memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivatePresentationTitle, memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedSocialJobId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedSocialJobId.ToString("N"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedPresentationJobId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedPresentationJobId.ToString("N"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedPresentationAssetId.ToString("D"), memberRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.UnscopedPresentationAssetId.ToString("N"), memberRaw, StringComparison.OrdinalIgnoreCase);

        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        var creatorList = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        Assert.Equal(4, creatorList!.TotalCount);

        var socialItem = Assert.Single(creatorList.Items, item => item.JobId == pair.UnscopedSocialJobId);
        Assert.Equal(PrivateSocialTitle, socialItem.Title);
        Assert.Equal("Completed", socialItem.Status);
        Assert.Equal(100, socialItem.ProgressPercent);

        var presentationItem = Assert.Single(creatorList.Items, item => item.JobId == pair.UnscopedPresentationJobId);
        Assert.Equal(pair.UnscopedPresentationAssetId, presentationItem.AssetId);
    }

    [Fact]
    public async Task Unread_Badge_And_List_Unread_Count_Exclude_Unscoped_Private_Records()
    {
        var pair = await SetupWorkspacePair("unscoped-badge");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var badge = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={pair.WorkspaceId}");
        Assert.Equal(2, badge.GetProperty("unreadCount").GetInt32());

        var memberList = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        Assert.Equal(2, memberList!.UnreadCount);
    }

    [Fact]
    public async Task MarkRead_And_ReadAll_Do_Not_Acknowledge_Records_The_Member_May_Not_See()
    {
        var pair = await SetupWorkspacePair("unscoped-read");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        var socialRead = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{pair.UnscopedSocialJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.Forbidden, socialRead.StatusCode);
        var presentationRead = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{pair.UnscopedPresentationJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.Forbidden, presentationRead.StatusCode);

        var markAll = await SendWithCsrf(memberClient, HttpMethod.Post, "/api/activity/read-all", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, markAll.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.UnscopedSocialJobId));
            Assert.False(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.UnscopedPresentationJobId));
            // The records the member may see are still acknowledged normally.
            Assert.True(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.SharedImageJobId));
            Assert.True(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.NoncreatorUserId && read.GenerationJobId == pair.ScopedSocialJobId));
        }

        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        var creatorSocialRead = await SendWithCsrf(creatorClient, HttpMethod.Post, $"/api/activity/{pair.UnscopedSocialJobId}/read", new { workspaceId = pair.WorkspaceId });
        Assert.Equal(HttpStatusCode.OK, creatorSocialRead.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            Assert.True(await db.ActivityReadStates.AnyAsync(read => read.UserId == pair.CreatorUserId && read.GenerationJobId == pair.UnscopedSocialJobId));
        }
    }

    [Fact]
    public async Task JobType_Filter_Bucket_Expansion_Does_Not_Resurrect_Hidden_Records()
    {
        var pair = await SetupWorkspacePair("unscoped-filter");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        // The bucket vocabulary expands social/presentation to several job types, so the expansion
        // path must not be able to bring a hidden record back into the page or the totals.
        var memberFiltered = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&jobType=social");
        Assert.NotNull(memberFiltered);
        Assert.DoesNotContain(memberFiltered!.Items, item => item.JobId == pair.UnscopedSocialJobId);
        Assert.Equal(1, memberFiltered.TotalCount);
        Assert.Equal(pair.ScopedSocialJobId, Assert.Single(memberFiltered.Items).JobId);

        var presentationFiltered = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&jobType=presentation");
        Assert.DoesNotContain(presentationFiltered!.Items, item => item.JobId == pair.UnscopedPresentationJobId);
        Assert.Equal(0, presentationFiltered.TotalCount);

        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        var creatorFiltered = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&jobType=social");
        Assert.Equal(2, creatorFiltered!.TotalCount);
    }

    private sealed record ActivityUnscopedPair(
        string CreatorEmail,
        string NoncreatorEmail,
        Guid CreatorUserId,
        Guid NoncreatorUserId,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid UnscopedSocialJobId,
        Guid UnscopedPresentationJobId,
        Guid UnscopedPresentationAssetId,
        Guid ScopedSocialJobId,
        Guid SharedImageJobId);

    /// <summary>
    /// Registers a creator and a plain workspace member, then seeds two creator-private unscoped
    /// records (social + presentation, the latter with the linked asset the feed resolves), one
    /// project-scoped social record that must stay shared, and one intentionally shared image
    /// record - all directly into the database.
    /// </summary>
    private async Task<ActivityUnscopedPair> SetupWorkspacePair(string label)
    {
        using var creatorClient = factory.CreateClient();
        var creatorEmail = $"{label}-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creatorEmail);
        using var memberClient = factory.CreateClient();
        var noncreatorEmail = $"{label}-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreatorEmail);

        var workspaceId = creatorAuth.PersonalWorkspace.Id;
        var projectId = Guid.NewGuid();
        var unscopedSocialJobId = Guid.NewGuid();
        var unscopedPresentationJobId = Guid.NewGuid();
        var unscopedPresentationAssetId = Guid.NewGuid();
        var scopedSocialJobId = Guid.NewGuid();
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
            db.GenerationJobs.Add(NewJob(unscopedSocialJobId, workspaceId, creatorAuth.User.Id, null, GenerationJobTypes.SocialGenerate, PrivateSocialTitle, "{\"posts\":[{\"body\":\"creator-private\"}]}", GenerationJobStatus.Succeeded, 100));
            db.GenerationJobs.Add(NewJob(unscopedPresentationJobId, workspaceId, creatorAuth.User.Id, null, GenerationJobTypes.PresentationGenerate, PrivatePresentationTitle, "{\"slides\":2}", GenerationJobStatus.Succeeded, 100));
            db.GenerationJobs.Add(NewJob(scopedSocialJobId, workspaceId, creatorAuth.User.Id, projectId, GenerationJobTypes.SocialGenerate, ScopedSocialTitle, "{\"posts\":[{\"body\":\"shared\"}]}", GenerationJobStatus.Succeeded, 100));
            db.GenerationJobs.Add(NewJob(sharedImageJobId, workspaceId, creatorAuth.User.Id, null, GenerationJobTypes.ImageGenerate, SharedImageTitle, "{}", GenerationJobStatus.Queued, 0));
            db.Assets.Add(new Asset
            {
                Id = unscopedPresentationAssetId,
                WorkspaceId = workspaceId,
                CreatedByUserId = creatorAuth.User.Id,
                SourceGenerationJobId = unscopedPresentationJobId,
                Name = "private-presentation-export",
                AssetType = AssetTypes.Presentation,
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new ActivityUnscopedPair(creatorEmail, noncreatorEmail, creatorAuth.User.Id, noncreatorAuth.User.Id, workspaceId, projectId,
            unscopedSocialJobId, unscopedPresentationJobId, unscopedPresentationAssetId, scopedSocialJobId, sharedImageJobId);
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
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Activity Unscoped Privacy Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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
}

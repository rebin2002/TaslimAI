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
/// Isolated host for the #331 status-filter negative control. The queue worker stays disabled so the
/// synthetic records are never claimed or executed: no provider call, no migration beyond
/// EnsureCreated on a throwaway SQLite file, no paid path, no production data.
/// </summary>
public sealed class ActivityResearchStatusPrivacyHttpApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"taslim-activity-research-status-privacy-{Guid.NewGuid():N}.db");

    public ActivityResearchStatusPrivacyHttpApiFactory()
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
/// Test-only negative control for issue #331, one uncovered leg of the creator-private Research
/// activity surface: the <c>status</c> filter branch of
/// <c>ActivityCenterService.ListAsync</c>. The completed-Research leak reproduces literally through
/// <c>?status=Completed</c> (the Succeeded→Completed mapping narrows the same disclosing query), yet
/// no existing lane drives the status parameter cross-user: the <c>jobType</c> filter is already
/// pinned by PR #1129 (<c>ActivityResearchCreatorPrivacyHttpTests</c>) and PR #779
/// (<c>ActivityResearchPrivacyTests</c>), and <c>ActivityCenterTests.cs</c> on main (owned by the
/// #323/#327/#872 lanes) has no cross-user Research case. There is no Activity search surface
/// (<c>ActivityFilter</c> carries only workspace/page/pageSize/status/jobType) and no activity
/// detail GET route, so list-filter is the remaining applicable leg. This file adds new types only;
/// it edits no owned file and touches no production policy.
/// RED against current main (<c>41f9fcc6</c>) by design; GREEN under the PR #1129 predicate, which
/// <c>VisibleToMember</c> applies to the base query before the status narrowing.
/// </summary>
public sealed class ActivityResearchStatusFilterPrivacyHttpTests : IClassFixture<ActivityResearchStatusPrivacyHttpApiFactory>
{
    private const string PrivateResearchTitle = "Private research report on synthetic half-year outlook";
    private const string SharedCompletedTitle = "Shared completed image";
    private const string SharedQueuedTitle = "Shared queued image";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ActivityResearchStatusPrivacyHttpApiFactory factory;

    public ActivityResearchStatusFilterPrivacyHttpTests(ActivityResearchStatusPrivacyHttpApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task Status_Filter_Hides_AnotherMembers_Private_Research_Job_On_Every_Matching_Leg()
    {
        var pair = await SetupWorkspacePair();
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);

        // Completed leg (maps to Succeeded, the status the private Research job is seeded with):
        // the co-member must receive exactly the intentionally shared completed record, never the
        // private Research title, job id or linked asset id.
        var completedRaw = await memberClient.GetStringAsync($"/api/activity?workspaceId={pair.WorkspaceId}&status=Completed");
        var completed = JsonSerializer.Deserialize<ActivityListDto>(completedRaw, WebJson)!;
        Assert.Equal(pair.SharedCompletedJobId, Assert.Single(completed.Items).JobId);
        Assert.Equal(1, completed.TotalCount);
        Assert.DoesNotContain(completed.Items, item => item.JobId == pair.ResearchJobId);
        Assert.DoesNotContain(PrivateResearchTitle, completedRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchJobId.ToString("D"), completedRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchJobId.ToString("N"), completedRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchAssetId.ToString("D"), completedRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchAssetId.ToString("N"), completedRaw, StringComparison.OrdinalIgnoreCase);

        // Same record reached through the raw enum leg of the status branch (Enum.TryParse path).
        var succeededRaw = await memberClient.GetStringAsync($"/api/activity?workspaceId={pair.WorkspaceId}&status=Succeeded");
        var succeeded = JsonSerializer.Deserialize<ActivityListDto>(succeededRaw, WebJson)!;
        Assert.Equal(pair.SharedCompletedJobId, Assert.Single(succeeded.Items).JobId);
        Assert.DoesNotContain(pair.ResearchJobId.ToString("D"), succeededRaw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pair.ResearchAssetId.ToString("D"), succeededRaw, StringComparison.OrdinalIgnoreCase);

        // Queued leg: the filter itself keeps working for shared activity (the member still gets
        // their visible Queued record) while the private record stays absent.
        var queued = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&status=Queued");
        Assert.NotNull(queued);
        Assert.Equal(pair.SharedQueuedJobId, Assert.Single(queued!.Items).JobId);
        Assert.Equal(1, queued.TotalCount);

        // The filtered page's inline unread count is workspace-wide by existing design and must
        // count only the two shared records for the member, never the hidden Research job.
        Assert.Equal(2, completed.UnreadCount);

        // Creator positive visibility on the same filter: their own Research record stays present
        // alongside the shared completed record, with title, status, progress and asset intact.
        var creatorCompleted = await creatorClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}&status=Completed");
        Assert.NotNull(creatorCompleted);
        Assert.Equal(2, creatorCompleted!.TotalCount);
        var privateItem = Assert.Single(creatorCompleted.Items, item => item.JobId == pair.ResearchJobId);
        Assert.Equal(PrivateResearchTitle, privateItem.Title);
        Assert.Equal("research", privateItem.JobType);
        Assert.Equal("Completed", privateItem.Status);
        Assert.Equal(100, privateItem.ProgressPercent);
        Assert.Equal(pair.ResearchAssetId, privateItem.AssetId);
        Assert.Equal(3, creatorCompleted.UnreadCount);
    }

    private sealed record ActivityStatusPair(
        string CreatorEmail,
        string NoncreatorEmail,
        Guid WorkspaceId,
        Guid ResearchJobId,
        Guid ResearchAssetId,
        Guid SharedCompletedJobId,
        Guid SharedQueuedJobId);

    /// <summary>
    /// Registers two synthetic accounts, joins the second to the first account's personal workspace
    /// as a plain member, and seeds one creator-private Succeeded Research record (with its linked
    /// asset) plus one Succeeded and one Queued shared image record directly into the database.
    /// </summary>
    private async Task<ActivityStatusPair> SetupWorkspacePair()
    {
        using var creatorClient = factory.CreateClient();
        var creatorEmail = $"status-privacy-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creatorEmail);

        using var memberClient = factory.CreateClient();
        var noncreatorEmail = $"status-privacy-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreatorEmail);

        var workspaceId = creatorAuth.PersonalWorkspace.Id;
        var researchJobId = Guid.NewGuid();
        var researchAssetId = Guid.NewGuid();
        var sharedCompletedJobId = Guid.NewGuid();
        var sharedQueuedJobId = Guid.NewGuid();

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
            db.GenerationJobs.Add(NewJob(sharedCompletedJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ImageGenerate, SharedCompletedTitle, GenerationJobStatus.Succeeded, 100));
            db.GenerationJobs.Add(NewJob(sharedQueuedJobId, workspaceId, creatorAuth.User.Id, GenerationJobTypes.ImageGenerate, SharedQueuedTitle, GenerationJobStatus.Queued, 0));
            db.Assets.Add(new Asset
            {
                Id = researchAssetId,
                WorkspaceId = workspaceId,
                CreatedByUserId = creatorAuth.User.Id,
                SourceGenerationJobId = researchJobId,
                Name = "private-research-status-filter-report",
                AssetType = AssetTypes.Research,
                Status = AssetStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return new ActivityStatusPair(creatorEmail, noncreatorEmail, workspaceId, researchJobId, researchAssetId, sharedCompletedJobId, sharedQueuedJobId);
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
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Status Privacy Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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

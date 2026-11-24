using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;
namespace Taslim.Api.Tests;
/// <summary>
/// GATE-PRIV-05 seam regression (independent review finding on the round-17 head): the translatable
/// creator-private fences (<c>CreatorPrivacyFence.VisibleToMember</c>, feeding the Activity Center,
/// the unread badge and the project overview, and the mirrored predicate inside
/// <c>GenerationJobExecution.ListAsync</c> for the job list) compared <c>JobType</c> with ordinal SQL
/// <c>!=</c>, while the canonical in-memory rule <c>IsPrivateExecutionRecord</c> they must agree with
/// compares <c>OrdinalIgnoreCase</c>. On any case-sensitive database comparison — SQLite's ASCII
/// equality and PostgreSQL's default collation both — a row persisted with a mixed-case
/// <c>JobType</c> ("Research.Generate", "RESEARCH.GENERATE", "SOCIAL.GENERATE", …) stopped matching
/// the SQL fence and stayed visible to co-members on the activity feed, the project overview and the
/// job list, even though direct job retrieval kept it private. The enqueue path normalises the
/// persisted value today, so no production exposure is claimed; the fence exists precisely for rows
/// that bypass it, so it must fail closed on case variants rather than fail open. These tests seed
/// mixed-case rows straight through the persistence context (simulating such a bypassing writer) and
/// pin that every member-facing surface hides them exactly like the canonical lowercase twins, while
/// the intentionally shared classes stay visible under any casing.
/// </summary>
public sealed class CreatorPrivacyFenceMixedCaseHttpTests : IClassFixture<ProjectOverviewCreatorPrivacyHttpApiFactory>
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private const string PrivateResearchMixedTitle = "MixedCase private research record";
    private const string PrivateResearchUpperTitle = "Upper private research record";
    private const string PrivatePresentationMixedTitle = "MixedCase private presentation record";
    private const string PrivateSocialUpperTitle = "Upper private social record";
    private const string OwnCanonicalResearchTitle = "Canonical lowercase own research record";
    private const string SharedImageMixedTitle = "MixedCase shared image record";
    private const string SharedDocumentTitle = "MixedCase shared document record";
    private readonly ProjectOverviewCreatorPrivacyHttpApiFactory factory;
    public CreatorPrivacyFenceMixedCaseHttpTests(ProjectOverviewCreatorPrivacyHttpApiFactory factory)
    {
        this.factory = factory;
    }
    [Fact]
    public async Task MixedCase_ProjectScoped_Research_Record_Is_Hidden_From_CoMember_On_Activity_Overview_And_JobList()
    {
        var pair = await SetupPair("mixedcase-scoped-research");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);
        // Surface 1: project overview (CreatorPrivacyFence through ProjectsController.Overview).
        var overview = await memberClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");
        var overviewIds = ReadOverviewIds(overview);
        Assert.DoesNotContain(pair.CreatorMixedResearchJobId, overviewIds);
        Assert.DoesNotContain(overview.GetProperty("recentActivity").EnumerateArray()
            .Select(item => item.GetProperty("title").GetString()), t => t == PrivateResearchMixedTitle);
        // Surface 2: activity feed and its totals.
        var activity = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        Assert.NotNull(activity);
        Assert.DoesNotContain(pair.CreatorMixedResearchJobId, activity!.Items.Select(item => item.JobId));
        // Surface 3: the job list SQL mirror (GenerationJobExecution.ListAsync).
        var jobs = await memberClient.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={pair.WorkspaceId}&pageSize=100");
        Assert.NotNull(jobs);
        Assert.DoesNotContain(pair.CreatorMixedResearchJobId, jobs!.Items.Select(item => item.Id));
        // The member's own canonical lowercase research record and the intentionally shared image
        // stay on all three surfaces: the case fix must not turn creator privacy into general hiding.
        Assert.Contains(pair.OwnCanonicalResearchJobId, overviewIds);
        Assert.Contains(pair.OwnCanonicalResearchJobId, activity.Items.Select(item => item.JobId));
        Assert.Contains(pair.OwnCanonicalResearchJobId, jobs.Items.Select(item => item.Id));
        Assert.Contains(pair.SharedImageJobId, overviewIds);
        Assert.Contains(pair.SharedImageJobId, jobs.Items.Select(item => item.Id));
        // The creator keeps their own mixed-case record everywhere.
        using var creatorClient = factory.CreateClient();
        await Login(creatorClient, pair.CreatorEmail);
        var creatorJobs = await creatorClient.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={pair.WorkspaceId}&pageSize=100");
        Assert.NotNull(creatorJobs);
        Assert.Contains(pair.CreatorMixedResearchJobId, creatorJobs!.Items.Select(item => item.Id));
    }
    [Fact]
    public async Task MixedCase_Unscoped_Research_Presentation_And_Social_Records_Are_Hidden_From_CoMember()
    {
        var pair = await SetupPair("mixedcase-unscoped");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);
        var activity = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        var jobs = await memberClient.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={pair.WorkspaceId}&pageSize=100");
        Assert.NotNull(activity);
        Assert.NotNull(jobs);
        // UPPER research, mixed-case unscoped presentation and UPPER unscoped social are the three
        // creator-private classes; none may survive the SQL fence merely because its stored casing
        // differs from the canonical constant.
        foreach (var privateId in new[] { pair.CreatorUpperResearchJobId, pair.CreatorMixedPresentationJobId, pair.CreatorUpperSocialJobId })
        {
            Assert.DoesNotContain(privateId, activity!.Items.Select(item => item.JobId));
            Assert.DoesNotContain(privateId, jobs!.Items.Select(item => item.Id));
        }
        // The mixed-case intentionally shared image record (no project) stays visible.
        Assert.Contains(pair.SharedImageJobId, activity.Items.Select(item => item.JobId));
        Assert.Contains(pair.SharedImageJobId, jobs.Items.Select(item => item.Id));
    }
    [Fact]
    public async Task MixedCase_Shared_Classes_Remain_Visible_Under_Any_Casing()
    {
        var pair = await SetupPair("mixedcase-shared");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);
        var jobs = await memberClient.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={pair.WorkspaceId}&pageSize=100");
        Assert.NotNull(jobs);
        // Fail-closed on the private classes must not over-hide the shared ones: "Image.Generate"
        // and "document.generate" are workspace-shared regardless of casing or scope.
        Assert.Contains(pair.SharedDocumentJobId, jobs!.Items.Select(item => item.Id));
        var overview = await memberClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");
        Assert.Contains(pair.SharedDocumentJobId, ReadOverviewIds(overview));
    }
    [Fact]
    public async Task MixedCase_And_Canonical_Siblings_Disclose_The_Same_Set_On_Every_Surface()
    {
        var pair = await SetupPair("mixedcase-agreement");
        using var memberClient = factory.CreateClient();
        await Login(memberClient, pair.NoncreatorEmail);
        var activity = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={pair.WorkspaceId}");
        var overview = await memberClient.GetFromJsonAsync<JsonElement>($"/api/projects/{pair.ProjectId}/overview");
        Assert.NotNull(activity);
        var activityIds = activity!.Items.Select(item => item.JobId).ToHashSet();
        var overviewIds = ReadOverviewIds(overview).ToHashSet();
        // The round-17 cross-surface invariant, now under case variants: the mixed-case twin of a
        // private record behaves exactly like its canonical twin on both surfaces.
        Assert.DoesNotContain(pair.CreatorCanonicalResearchJobId, activityIds);
        Assert.DoesNotContain(pair.CreatorMixedResearchJobId, activityIds);
        Assert.DoesNotContain(pair.CreatorCanonicalResearchJobId, overviewIds);
        Assert.DoesNotContain(pair.CreatorMixedResearchJobId, overviewIds);
        Assert.True(overviewIds.IsSubsetOf(activityIds), $"overview leaked ids not present in activity: {string.Join(",", overviewIds.Except(activityIds))}");
    }
    private static Guid[] ReadOverviewIds(JsonElement overview) =>
        overview.GetProperty("recentActivity").EnumerateArray()
            .Select(item => Guid.Parse(item.GetProperty("jobId").GetString()!))
            .ToArray();
    private async Task<MixedCasePair> SetupPair(string label)
    {
        using var creatorClient = factory.CreateClient();
        var creatorEmail = $"{label}-owner-{Guid.NewGuid():N}@example.com";
        var creatorAuth = await Register(creatorClient, creatorEmail);
        using var memberClient = factory.CreateClient();
        var noncreatorEmail = $"{label}-member-{Guid.NewGuid():N}@example.com";
        var noncreatorAuth = await Register(memberClient, noncreatorEmail);
        var workspaceId = creatorAuth.PersonalWorkspace.Id;
        var projectId = Guid.NewGuid();
        var creatorMixedResearchJobId = Guid.NewGuid();
        var creatorUpperResearchJobId = Guid.NewGuid();
        var creatorMixedPresentationJobId = Guid.NewGuid();
        var creatorUpperSocialJobId = Guid.NewGuid();
        var creatorCanonicalResearchJobId = Guid.NewGuid();
        var ownCanonicalResearchJobId = Guid.NewGuid();
        var sharedImageJobId = Guid.NewGuid();
        var sharedDocumentJobId = Guid.NewGuid();
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
            // Mixed-case rows are written straight through the persistence context, bypassing the
            // enqueue normalisation, to simulate exactly the external/legacy writer the fence exists
            // to contain.
            db.GenerationJobs.Add(NewJob(creatorMixedResearchJobId, workspaceId, creatorAuth.User.Id, projectId, "Research.Generate", PrivateResearchMixedTitle, "{\"report\":\"creator-private-mixedcase\"}"));
            db.GenerationJobs.Add(NewJob(creatorUpperResearchJobId, workspaceId, creatorAuth.User.Id, null, "RESEARCH.GENERATE", PrivateResearchUpperTitle, "{\"report\":\"creator-private-upper\"}"));
            db.GenerationJobs.Add(NewJob(creatorMixedPresentationJobId, workspaceId, creatorAuth.User.Id, null, "Presentation.Generate", PrivatePresentationMixedTitle, "{\"export\":\"creator-private-mixedcase\"}"));
            db.GenerationJobs.Add(NewJob(creatorUpperSocialJobId, workspaceId, creatorAuth.User.Id, null, "SOCIAL.GENERATE", PrivateSocialUpperTitle, "{\"posts\":\"creator-private-upper\"}"));
            db.GenerationJobs.Add(NewJob(creatorCanonicalResearchJobId, workspaceId, creatorAuth.User.Id, projectId, GenerationJobTypes.ResearchGenerate, "Canonical private research record", "{\"report\":\"creator-private-canonical\"}"));
            db.GenerationJobs.Add(NewJob(ownCanonicalResearchJobId, workspaceId, noncreatorAuth.User.Id, projectId, GenerationJobTypes.ResearchGenerate, OwnCanonicalResearchTitle, "{\"report\":\"mine\"}"));
            db.GenerationJobs.Add(NewJob(sharedImageJobId, workspaceId, creatorAuth.User.Id, label.Contains("unscoped") ? null : projectId, "Image.Generate", SharedImageMixedTitle, "{}"));
            db.GenerationJobs.Add(NewJob(sharedDocumentJobId, workspaceId, creatorAuth.User.Id, projectId, "document.GENERATE", SharedDocumentTitle, "{}"));
            await db.SaveChangesAsync();
        }
        return new MixedCasePair(
            creatorEmail, noncreatorEmail, workspaceId, projectId,
            creatorMixedResearchJobId, creatorUpperResearchJobId, creatorMixedPresentationJobId,
            creatorUpperSocialJobId, creatorCanonicalResearchJobId, ownCanonicalResearchJobId,
            sharedImageJobId, sharedDocumentJobId);
    }
    private static GenerationJob NewJob(Guid id, Guid workspaceId, Guid creatorUserId, Guid? projectId, string jobType, string title, string resultJson) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        ProjectId = projectId,
        CreatedByUserId = creatorUserId,
        JobType = jobType,
        Status = GenerationJobStatus.Succeeded,
        ProgressPercent = 100,
        Title = title,
        InputJson = "{}",
        ResultJson = resultJson,
        CreatedAt = DateTime.UtcNow,
        CompletedAt = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid(),
    };
    private async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "MixedCase Fence Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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
    private sealed record MixedCasePair(
        string CreatorEmail,
        string NoncreatorEmail,
        Guid WorkspaceId,
        Guid ProjectId,
        Guid CreatorMixedResearchJobId,
        Guid CreatorUpperResearchJobId,
        Guid CreatorMixedPresentationJobId,
        Guid CreatorUpperSocialJobId,
        Guid CreatorCanonicalResearchJobId,
        Guid OwnCanonicalResearchJobId,
        Guid SharedImageJobId,
        Guid SharedDocumentJobId);
}

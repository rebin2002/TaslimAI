using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

// Issue #349: a no-project `social.generate` execution record carries the generated posts in
// its result JSON, and those posts are creator-private because the resulting asset is private
// when the job has no project. A workspace co-member must therefore not receive the record
// through the job GET, must not see it (or have it inflate their totals) through the workspace
// list, and must not be able to retry or cancel it without any disclosure of the result JSON.
// The creator keeps full access, and project-scoped Social jobs keep the intentional
// workspace sharing surface. Deterministic, in-process fake provider, no paid inference.
public sealed class SocialJobPrivacyBoundaryTests : IClassFixture<SocialGenerationApiFactory>
{
    private const string PrivateRetryMarker = "creator-private-retry-result";
    private const string PrivateCancelMarker = "creator-private-cancel-result";

    private readonly SocialGenerationApiFactory factory;

    public SocialJobPrivacyBoundaryTests(SocialGenerationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task No_project_social_job_is_hidden_from_workspace_member_but_stays_visible_to_creator()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator);
        var job = await CreateSocialJob(creator, creatorAuth, projectId: null, prompt: "Create a creator-private launch announcement.");
        var terminal = await WaitForTerminal(creator, job.Id);
        Assert.Equal("Succeeded", terminal.Status);
        Assert.Contains("posts", terminal.ResultJson, StringComparison.Ordinal);

        using var member = factory.CreateClient();
        var memberAuth = await Register(member);
        await AddMember(creatorAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        // Direct retrieval follows the existing not-found policy and never carries the result.
        using var memberDirect = await member.GetAsync($"/api/generation/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.NotFound, memberDirect.StatusCode);
        var memberDirectBody = await memberDirect.Content.ReadAsStringAsync();
        Assert.DoesNotContain("posts", memberDirectBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(terminal.ResultJson!, memberDirectBody, StringComparison.Ordinal);

        // The workspace listing omits the job and the pagination total excludes it.
        var memberList = await member.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(memberList);
        Assert.Equal(0, memberList!.TotalCount);
        Assert.Empty(memberList.Items);

        // The creator keeps access through both seams.
        using var creatorDirect = await creator.GetAsync($"/api/generation/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.OK, creatorDirect.StatusCode);
        var creatorList = await creator.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&jobType={GenerationJobTypes.SocialGenerate}&pageSize=100");
        Assert.NotNull(creatorList);
        Assert.Contains(creatorList!.Items, item => item.Id == job.Id);
        Assert.True(creatorList.TotalCount >= 1);
    }

    [Fact]
    public async Task Project_scoped_social_job_stays_shared_with_workspace_member()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator);
        using var member = factory.CreateClient();
        var memberAuth = await Register(member);
        await AddMember(creatorAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        var project = await CreateProject(creator, creatorAuth.PersonalWorkspace.Id, $"Shared social project {Guid.NewGuid():N}");
        var job = await CreateSocialJob(creator, creatorAuth, project.Id, prompt: "Create a project-scoped launch announcement.");
        var terminal = await WaitForTerminal(creator, job.Id);
        Assert.Equal("Succeeded", terminal.Status);

        using var memberDirect = await member.GetAsync($"/api/generation/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.OK, memberDirect.StatusCode);
        var memberDto = await memberDirect.Content.ReadFromJsonAsync<GenerationJobDto>();
        Assert.NotNull(memberDto);
        Assert.Contains("posts", memberDto!.ResultJson, StringComparison.Ordinal);

        var memberList = await member.GetFromJsonAsync<GenerationJobListDto>(
            $"/api/generation/jobs?workspaceId={creatorAuth.PersonalWorkspace.Id}&projectId={project.Id}&jobType={GenerationJobTypes.SocialGenerate}");
        Assert.NotNull(memberList);
        Assert.Equal(1, memberList!.TotalCount);
        Assert.Equal(job.Id, Assert.Single(memberList.Items).Id);
    }

    [Fact]
    public async Task Member_retry_and_cancel_of_no_project_social_job_are_denied_without_result_disclosure()
    {
        using var creator = factory.CreateClient();
        var creatorAuth = await Register(creator);
        using var member = factory.CreateClient();
        var memberAuth = await Register(member);
        await AddMember(creatorAuth.PersonalWorkspace.Id, memberAuth.User.Id);

        // Synthetic terminal/queued creator records: authorization is exercised before any
        // provider or worker participation, so the fake provider is never invoked here.
        var retryJobId = Guid.NewGuid();
        var cancelJobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.AddRange(
                new GenerationJob
                {
                    Id = retryJobId,
                    WorkspaceId = creatorAuth.PersonalWorkspace.Id,
                    CreatedByUserId = creatorAuth.User.Id,
                    JobType = GenerationJobTypes.SocialGenerate,
                    Status = GenerationJobStatus.Failed,
                    InputJson = "{}",
                    ResultJson = $"{{\"posts\":[{{\"body\":\"{PrivateRetryMarker}\"}}]}}",
                    CreatedAt = now,
                    FailedAt = now,
                },
                new GenerationJob
                {
                    Id = cancelJobId,
                    WorkspaceId = creatorAuth.PersonalWorkspace.Id,
                    CreatedByUserId = creatorAuth.User.Id,
                    JobType = GenerationJobTypes.SocialGenerate,
                    Status = GenerationJobStatus.Pending,
                    InputJson = "{}",
                    ResultJson = $"{{\"posts\":[{{\"body\":\"{PrivateCancelMarker}\"}}]}}",
                    CreatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        using var memberRetry = await SendWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{retryJobId}/retry", null);
        Assert.Equal(HttpStatusCode.NotFound, memberRetry.StatusCode);
        var retryBody = await memberRetry.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateRetryMarker, retryBody, StringComparison.Ordinal);

        using var memberCancel = await SendWithCsrf(member, HttpMethod.Post, $"/api/generation/jobs/{cancelJobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, memberCancel.StatusCode);
        var cancelBody = await memberCancel.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PrivateCancelMarker, cancelBody, StringComparison.Ordinal);

        // The denial left the record untouched; the creator still owns both seams. The fixture
        // stays in Pending so the background worker (which claims Queued rows only) cannot
        // race this assertion.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var stillQueued = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == cancelJobId);
            Assert.Equal(GenerationJobStatus.Pending, stillQueued.Status);
        }

        using var creatorCancel = await SendWithCsrf(creator, HttpMethod.Post, $"/api/generation/jobs/{cancelJobId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, creatorCancel.StatusCode);
        using var creatorRetry = await SendWithCsrf(creator, HttpMethod.Post, $"/api/generation/jobs/{retryJobId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, creatorRetry.StatusCode);
    }

    private static async Task<GenerationJobDto> CreateSocialJob(HttpClient client, AuthResponse auth, Guid? projectId, string prompt)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/social-generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId,
            prompt,
            socialType = "announcement",
            platform = "linkedin",
            tone = "professional",
            language = "en",
            assetIds = Array.Empty<Guid>(),
            attachmentIds = Array.Empty<Guid>(),
        });
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new Exception($"Social job creation returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        return (await response.Content.ReadFromJsonAsync<CreateSocialGenerationResponse>())!.Job;
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Social Privacy Tester",
            email = $"social-privacy-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name, type = "General" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (path.EndsWith("/retry", StringComparison.Ordinal) || path == "/api/social-generation/jobs")
        {
            request.Headers.Add("Idempotency-Key", $"social-privacy-{Guid.NewGuid():N}");
        }
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private async Task AddMember(Guid workspaceId, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            JoinedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid jobId)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/generation/jobs/{jobId}");
            Assert.NotNull(job);
            if (job.Status is "Succeeded" or "Failed" or "Cancelled") return job;
            await Task.Delay(50);
        }
        throw new TimeoutException("Social job did not reach a terminal state.");
    }
}

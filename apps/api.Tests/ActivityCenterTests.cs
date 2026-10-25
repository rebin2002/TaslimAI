using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ActivityCenterTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public ActivityCenterTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Activity_projects_existing_jobs_safely_and_supports_unread_read_all()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"activity-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.DocumentGenerate,
            title = "Quarterly brief",
            inputJson = "{}",
        });

        var list = await client.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.NotNull(list);
        var item = Assert.Single(list!.Items);
        Assert.Equal(created.Id, item.JobId);
        Assert.Equal("document", item.JobType);
        Assert.Equal("Queued", item.Status);
        Assert.Equal("Quarterly brief", item.Title);
        Assert.False(item.IsRead);
        Assert.Equal(1, list.UnreadCount);
        Assert.Null(item.AssetId);

        var read = await SendWithCsrf(client, HttpMethod.Post, $"/api/activity/{created.Id}/read", new { workspaceId = auth.PersonalWorkspace.Id });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var count = await client.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(0, count.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Activity_does_not_cross_workspace_boundaries_or_expose_internal_fields()
    {
        using var owner = factory.CreateClient();
        var first = await Register(owner, $"activity-owner-{Guid.NewGuid():N}@example.com");
        var created = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = first.PersonalWorkspace.Id,
            CreatedByUserId = first.User.Id,
            JobType = GenerationJobTypes.ImageGenerate,
            Status = GenerationJobStatus.Queued,
            InputJson = "{}",
            Title = "Private image",
            CreatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid(),
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.GenerationJobs.Add(created);
            await db.SaveChangesAsync();
        }
        using var other = factory.CreateClient();
        var second = await Register(other, $"activity-other-{Guid.NewGuid():N}@example.com");

        var response = await other.GetAsync($"/api/activity?workspaceId={first.PersonalWorkspace.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var own = await other.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={second.PersonalWorkspace.Id}");
        Assert.Empty(own!.Items);

        var json = await owner.GetStringAsync($"/api/activity?workspaceId={first.PersonalWorkspace.Id}");
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(created.Id, Guid.Parse(JsonDocument.Parse(json).RootElement.GetProperty("items")[0].GetProperty("jobId").GetString()!));
    }

    [Fact]
    public async Task Activity_hides_creator_private_research_records_from_workspace_members()
    {
        using var ownerClient = factory.CreateClient();
        var owner = await Register(ownerClient, $"activity-research-owner-{Guid.NewGuid():N}@example.com");
        using var memberClient = factory.CreateClient();
        var member = await Register(memberClient, $"activity-research-member-{Guid.NewGuid():N}@example.com");
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = owner.PersonalWorkspace.Id,
                UserId = member.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = now,
            });
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = jobId,
                WorkspaceId = owner.PersonalWorkspace.Id,
                CreatedByUserId = owner.User.Id,
                JobType = GenerationJobTypes.ResearchGenerate,
                Status = GenerationJobStatus.Succeeded,
                Title = "Creator-private research title",
                InputJson = "{\"private\":true}",
                ProgressPercent = 100,
                CreatedAt = now,
                CompletedAt = now,
                ConcurrencyToken = Guid.NewGuid(),
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = owner.PersonalWorkspace.Id,
                CreatedByUserId = owner.User.Id,
                SourceGenerationJobId = jobId,
                Name = "Creator-private research asset",
                AssetType = AssetTypes.File,
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var list = await memberClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={owner.PersonalWorkspace.Id}");
        Assert.NotNull(list);
        Assert.Empty(list!.Items);
        Assert.Equal(0, list.UnreadCount);
        var memberJson = await memberClient.GetStringAsync($"/api/activity?workspaceId={owner.PersonalWorkspace.Id}");
        Assert.DoesNotContain(jobId.ToString(), memberJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(assetId.ToString(), memberJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Creator-private research title", memberJson, StringComparison.Ordinal);

        var unread = await memberClient.GetFromJsonAsync<JsonElement>($"/api/activity/unread-count?workspaceId={owner.PersonalWorkspace.Id}");
        Assert.Equal(0, unread.GetProperty("unreadCount").GetInt32());

        var read = await SendWithCsrf(memberClient, HttpMethod.Post, $"/api/activity/{jobId}/read", new { workspaceId = owner.PersonalWorkspace.Id });
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var readAll = await SendWithCsrf(memberClient, HttpMethod.Post, "/api/activity/read-all", new { workspaceId = owner.PersonalWorkspace.Id });
        Assert.Equal(HttpStatusCode.OK, readAll.StatusCode);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await verificationDb.ActivityReadStates.AnyAsync(item => item.UserId == member.User.Id && item.GenerationJobId == jobId));

        var ownerList = await ownerClient.GetFromJsonAsync<ActivityListDto>($"/api/activity?workspaceId={owner.PersonalWorkspace.Id}");
        var ownerItem = Assert.Single(ownerList!.Items);
        Assert.Equal(jobId, ownerItem.JobId);
        Assert.Equal(assetId, ownerItem.AssetId);
        Assert.Equal("Creator-private research title", ownerItem.Title);
    }

    [Fact]
    public async Task Activity_bounds_deep_page_values_without_offset_overflow()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"activity-page-{Guid.NewGuid():N}@example.com");

        var response = await client.GetAsync($"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&page={int.MaxValue}&pageSize={int.MaxValue}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<ActivityListDto>())!;
        Assert.Equal(ApiPagination.MaxPage, list.Page);
        Assert.Equal(ApiPagination.MaxPageSize, list.PageSize);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task Concurrent_mark_read_requests_are_idempotent_at_the_unique_boundary()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"activity-read-race-{Guid.NewGuid():N}@example.com");
        var created = await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.DocumentGenerate,
            title = "Concurrent read marker",
            inputJson = "{}",
        });
        var token = await GetCsrfToken(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            SendWithToken(client, HttpMethod.Post, $"/api/activity/{created.Id}/read", token, new { workspaceId = auth.PersonalWorkspace.Id })));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.ActivityReadStates.CountAsync(item => item.UserId == auth.User.Id && item.GenerationJobId == created.Id));
    }

    [Fact]
    public async Task Concurrent_mark_all_read_requests_are_idempotent_at_the_unique_boundary()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"activity-read-all-race-{Guid.NewGuid():N}@example.com");
        for (var index = 0; index < 8; index++)
        {
            await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
            {
                workspaceId = auth.PersonalWorkspace.Id,
                jobType = GenerationJobTypes.DocumentGenerate,
                title = $"Concurrent read-all marker {index}",
                inputJson = "{}",
            });
        }
        var token = await GetCsrfToken(client);

        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            SendWithToken(client, HttpMethod.Post, "/api/activity/read-all", token, new { workspaceId = auth.PersonalWorkspace.Id })));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var jobIds = await db.GenerationJobs.Where(item => item.WorkspaceId == auth.PersonalWorkspace.Id).Select(item => item.Id).ToListAsync();
        Assert.Equal(jobIds.Count, await db.ActivityReadStates.CountAsync(item => item.UserId == auth.User.Id && jobIds.Contains(item.GenerationJobId)));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Activity Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendWithToken(HttpClient client, HttpMethod method, string path, string token, object? payload)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrfToken(HttpClient client)
    {
        var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        return response.GetProperty("token").GetString()!;
    }
}

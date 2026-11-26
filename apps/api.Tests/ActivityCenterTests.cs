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

public sealed class ActivityCenterTests : IClassFixture<DocumentEnabledNoWorkerFactory>
{
    // This fixture projects an existing document.generate job, so it opts into
    // Document Studio explicitly: the generic Generation Job route rejects that
    // job type while the Document kill switch is off.
    private readonly DocumentEnabledNoWorkerFactory factory;

    public ActivityCenterTests(DocumentEnabledNoWorkerFactory factory)
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
    public async Task Activity_job_type_filter_selects_exactly_the_bucket_vocabulary_items_report()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"activity-jobtype-{Guid.NewGuid():N}@example.com");
        var rawTypes = new[]
        {
            GenerationJobTypes.ImageGenerate,
            GenerationJobTypes.DocumentGenerate,
            GenerationJobTypes.PresentationGenerate,
            GenerationJobTypes.ResearchGenerate,
            GenerationJobTypes.SocialGenerate,
            GenerationJobTypes.VoiceGenerate,
            GenerationJobTypes.MusicGenerate,
            GenerationJobTypes.MovieQuickGenerate,
            GenerationJobTypes.MovieClipGenerate,
            GenerationJobTypes.MovieAssembly,
            GenerationJobTypes.MovieDialogueVoiceGenerate,
            GenerationJobTypes.MovieSoundGenerate,
            GenerationJobTypes.SystemTest,
        };
        var created = new Dictionary<string, Guid>();
        var baseTime = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            for (var i = 0; i < rawTypes.Length; i++)
            {
                var id = Guid.NewGuid();
                created[rawTypes[i]] = id;
                db.GenerationJobs.Add(new GenerationJob
                {
                    Id = id,
                    WorkspaceId = auth.PersonalWorkspace.Id,
                    CreatedByUserId = auth.User.Id,
                    JobType = rawTypes[i],
                    Status = GenerationJobStatus.Queued,
                    InputJson = "{}",
                    Title = $"Activity type probe {i}",
                    CreatedAt = baseTime.AddSeconds(-i),
                    ConcurrencyToken = Guid.NewGuid(),
                });
            }
            await db.SaveChangesAsync();
        }

        // The endpoint's own reported buckets define the expected partition; historic rows
        // (system test, movie dialogue voice, movie sound) land in the default arm.
        var unfiltered = await client.GetFromJsonAsync<ActivityListDto>(
            $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&pageSize=100");
        Assert.NotNull(unfiltered);
        var expectedByBucket = unfiltered.Items
            .GroupBy(item => item.JobType)
            .ToDictionary(group => group.Key, group => group.Select(item => item.JobId).ToHashSet());

        Assert.Equal(rawTypes.Length, expectedByBucket.Values.Sum(ids => ids.Count));
        Assert.Equal(
            new[] { created[GenerationJobTypes.MovieQuickGenerate], created[GenerationJobTypes.MovieClipGenerate], created[GenerationJobTypes.MovieAssembly] }.OrderBy(id => id),
            expectedByBucket["movie"].OrderBy(id => id));
        Assert.Equal(
            new[] { created[GenerationJobTypes.MovieDialogueVoiceGenerate], created[GenerationJobTypes.MovieSoundGenerate], created[GenerationJobTypes.SystemTest] }.OrderBy(id => id),
            expectedByBucket["other"].OrderBy(id => id));

        foreach (var (bucket, expectedIds) in expectedByBucket)
        {
            var filtered = await client.GetFromJsonAsync<ActivityListDto>(
                $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType={bucket}&pageSize=100");
            Assert.NotNull(filtered);
            Assert.Equal(expectedIds.Count, filtered.TotalCount);
            Assert.Equal(expectedIds, filtered.Items.Select(item => item.JobId).ToHashSet());
            Assert.All(filtered.Items, item => Assert.Equal(bucket, item.JobType));
        }

        // The raw persisted vocabulary keeps working and stays case-insensitive, matching the
        // canonical casing the create path stores for supported types.
        var rawFiltered = await client.GetFromJsonAsync<ActivityListDto>(
            $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=MUSIC.GENERATE&pageSize=100");
        Assert.NotNull(rawFiltered);
        Assert.Equal(new[] { created[GenerationJobTypes.MusicGenerate] }, rawFiltered.Items.Select(item => item.JobId));

        var bucketCased = await client.GetFromJsonAsync<ActivityListDto>(
            $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=Movie&pageSize=100");
        Assert.NotNull(bucketCased);
        Assert.Equal(3, bucketCased.TotalCount);

        // Paginating a bucket stays consistent with the filtered total.
        var secondPage = await client.GetFromJsonAsync<ActivityListDto>(
            $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=movie&page=2&pageSize=2");
        Assert.NotNull(secondPage);
        Assert.Equal(3, secondPage.TotalCount);
        Assert.Equal(2, secondPage.TotalPages);
        Assert.Single(secondPage.Items);
        Assert.All(secondPage.Items, item => Assert.Equal("movie", item.JobType));

        // Unknown values preserve the previous exact-match behavior: no rows, no error.
        var unknown = await client.GetFromJsonAsync<ActivityListDto>(
            $"/api/activity?workspaceId={auth.PersonalWorkspace.Id}&jobType=not.a.type&pageSize=100");
        Assert.NotNull(unknown);
        Assert.Equal(0, unknown.TotalCount);
        Assert.Empty(unknown.Items);
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

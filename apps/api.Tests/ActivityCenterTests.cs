using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
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
        var created = await SendWithCsrf<GenerationJobDto>(owner, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = first.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.ImageGenerate,
            inputJson = "{}",
            title = "Private image",
        });
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
}

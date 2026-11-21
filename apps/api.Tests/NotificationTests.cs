using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Notifications;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class NotificationTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public NotificationTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Notifications_are_durable_readable_and_mark_all_is_idempotent()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"notification-{Guid.NewGuid():N}@example.com");
        var notificationId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Notifications.AddRange(
                new Notification { Id = notificationId, UserId = auth.User.Id, WorkspaceId = auth.PersonalWorkspace.Id, Type = NotificationTypes.GenerationFailed, DeduplicationKey = $"test:{notificationId:N}", ResourceTitle = "Private job", CreatedAt = DateTime.UtcNow },
                new Notification { Id = Guid.NewGuid(), UserId = auth.User.Id, WorkspaceId = auth.PersonalWorkspace.Id, Type = NotificationTypes.GenerationAttention, DeduplicationKey = $"test:attention:{Guid.NewGuid():N}", ResourceTitle = "Long job", CreatedAt = DateTime.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.Equal(2, list.GetProperty("unreadCount").GetInt32());

        var read = await SendWithCsrf(client, HttpMethod.Post, $"/api/notifications/{notificationId}/read", new { workspaceId = auth.PersonalWorkspace.Id });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var count = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(1, count.GetProperty("unreadCount").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrf(client, HttpMethod.Post, "/api/notifications/read-all", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrf(client, HttpMethod.Post, "/api/notifications/read-all", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);
        count = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(0, count.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Completed_asset_notifications_deep_link_to_the_exact_asset()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"notification-asset-link-{Guid.NewGuid():N}@example.com");
        var assetId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                Name = "Exact notification asset",
                AssetType = AssetTypes.File,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Notifications.Add(new Notification
            {
                Id = notificationId,
                UserId = auth.User.Id,
                WorkspaceId = auth.PersonalWorkspace.Id,
                AssetId = assetId,
                Type = NotificationTypes.GenerationCompleted,
                DeduplicationKey = $"test:asset-link:{notificationId:N}",
                ResourceTitle = "Exact notification asset",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal($"/assets?assetId={assetId:N}&status=Active", item.GetProperty("destination").GetString());
    }

    [Fact]
    public async Task Notification_pagination_bounds_hostile_query_values()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"notification-pagination-{Guid.NewGuid():N}@example.com");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(), UserId = auth.User.Id, WorkspaceId = auth.PersonalWorkspace.Id,
                Type = NotificationTypes.GenerationFailed, DeduplicationKey = $"test:pagination:{Guid.NewGuid():N}",
                ResourceTitle = "Bounded page", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var normalized = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}&page=0&pageSize=0");
        Assert.Equal(1, normalized.GetProperty("page").GetInt32());
        Assert.Equal(1, normalized.GetProperty("pageSize").GetInt32());
        Assert.Single(normalized.GetProperty("items").EnumerateArray());

        var bounded = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}&page={int.MaxValue}&pageSize={int.MaxValue}");
        Assert.Equal(10_000, bounded.GetProperty("page").GetInt32());
        Assert.Equal(100, bounded.GetProperty("pageSize").GetInt32());
        Assert.Empty(bounded.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Notifications_are_workspace_authorized_and_duplicate_events_are_suppressed()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, $"notification-owner-{Guid.NewGuid():N}@example.com");
        var job = await SendWithCsrf<GenerationJobDto>(owner, HttpMethod.Post, "/api/generation/jobs", new { workspaceId = auth.PersonalWorkspace.Id, jobType = GenerationJobTypes.SystemTest, inputJson = "{}", title = "One event" });
        using (var scope = factory.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await writer.CreateGenerationFailedAsync(job.Id);
            await writer.CreateGenerationFailedAsync(job.Id);
        }
        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Single(list.GetProperty("items").EnumerateArray());

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, $"notification-other-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}")).StatusCode);
        var own = await other.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={otherAuth.PersonalWorkspace.Id}");
        Assert.Empty(own.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(other, HttpMethod.Post, $"/api/notifications/{list.GetProperty("items")[0].GetProperty("id").GetGuid()}/read", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);
    }

    [Fact]
    public async Task Notifications_bound_deep_page_values_without_offset_overflow()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"notification-page-{Guid.NewGuid():N}@example.com");

        var response = await client.GetAsync($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}&page={int.MaxValue}&pageSize={int.MaxValue}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Taslim.Api.Infrastructure.ApiPagination.MaxPage, list.GetProperty("page").GetInt32());
        Assert.Equal(Taslim.Api.Infrastructure.ApiPagination.MaxPageSize, list.GetProperty("pageSize").GetInt32());
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Concurrent_duplicate_events_are_idempotent()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, $"notification-race-{Guid.NewGuid():N}@example.com");
        var job = await SendWithCsrf<GenerationJobDto>(owner, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            jobType = GenerationJobTypes.SystemTest,
            inputJson = "{}",
            title = "Concurrent event",
        });

        var first = CreateNotificationAsync(job.Id);
        var second = CreateNotificationAsync(job.Id);
        await Task.WhenAll(first, second);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(1, await db.Notifications.CountAsync(item => item.UserId == auth.User.Id && item.GenerationJobId == job.Id));
    }

    [Fact]
    public async Task Notification_read_mutations_stay_inside_the_requested_workspace()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"notification-workspace-scope-{Guid.NewGuid():N}@example.com");
        var teamWorkspaceId = Guid.NewGuid();
        var personalNotificationId = Guid.NewGuid();
        var teamNotificationId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var now = DateTime.UtcNow;
            db.Workspaces.Add(new Workspace
            {
                Id = teamWorkspaceId,
                Name = "Notification scope workspace",
                Slug = $"notification-scope-{teamWorkspaceId:N}",
                Type = WorkspaceType.Business,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = teamWorkspaceId,
                UserId = auth.User.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            });
            db.Notifications.AddRange(
                new Notification { Id = personalNotificationId, UserId = auth.User.Id, WorkspaceId = auth.PersonalWorkspace.Id, Type = NotificationTypes.GenerationFailed, DeduplicationKey = $"test:ws-scope:{personalNotificationId:N}", ResourceTitle = "Personal alert", CreatedAt = now },
                new Notification { Id = teamNotificationId, UserId = auth.User.Id, WorkspaceId = teamWorkspaceId, Type = NotificationTypes.GenerationFailed, DeduplicationKey = $"test:ws-scope:{teamNotificationId:N}", ResourceTitle = "Team alert", CreatedAt = now });
            await db.SaveChangesAsync();
        }

        // Lists and counts are scoped per workspace even though both records
        // belong to the same authenticated user.
        var personalList = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(1, personalList.GetProperty("unreadCount").GetInt32());
        Assert.Equal(personalNotificationId, Assert.Single(personalList.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var teamList = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={teamWorkspaceId}");
        Assert.Equal(1, teamList.GetProperty("unreadCount").GetInt32());
        Assert.Equal(teamNotificationId, Assert.Single(teamList.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // A mark-one mutation may not reach into another workspace the same
        // user legitimately belongs to; the mismatched request is rejected
        // without changing either unread count.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(client, HttpMethod.Post, $"/api/notifications/{personalNotificationId}/read", new { workspaceId = teamWorkspaceId })).StatusCode);
        var personalCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(1, personalCount.GetProperty("unreadCount").GetInt32());
        var teamCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={teamWorkspaceId}");
        Assert.Equal(1, teamCount.GetProperty("unreadCount").GetInt32());

        // The matching workspace succeeds and only that workspace's count moves.
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrf(client, HttpMethod.Post, $"/api/notifications/{personalNotificationId}/read", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);
        personalCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(0, personalCount.GetProperty("unreadCount").GetInt32());
        teamCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={teamWorkspaceId}");
        Assert.Equal(1, teamCount.GetProperty("unreadCount").GetInt32());

        // Mark-all is confined to the requested workspace, and reading is
        // idempotent across repeats.
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrf(client, HttpMethod.Post, "/api/notifications/read-all", new { workspaceId = teamWorkspaceId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrf(client, HttpMethod.Post, "/api/notifications/read-all", new { workspaceId = teamWorkspaceId })).StatusCode);
        teamCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={teamWorkspaceId}");
        Assert.Equal(0, teamCount.GetProperty("unreadCount").GetInt32());
        personalCount = await client.GetFromJsonAsync<JsonElement>($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}");
        Assert.Equal(0, personalCount.GetProperty("unreadCount").GetInt32());

        // The team notification's persisted state proves the cross-workspace
        // mark-one never touched the personal record.
        var teamAfterAll = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?workspaceId={teamWorkspaceId}");
        var teamItem = teamAfterAll.GetProperty("items")[0];
        Assert.Equal(teamNotificationId, teamItem.GetProperty("id").GetGuid());
        Assert.True(teamItem.GetProperty("isRead").GetBoolean());
    }

    private async Task CreateNotificationAsync(Guid jobId)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationService>().CreateGenerationFailedAsync(jobId);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Notification Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
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

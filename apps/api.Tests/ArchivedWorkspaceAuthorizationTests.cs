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

public sealed class ArchivedWorkspaceAuthorizationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ArchivedWorkspaceAuthorizationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Archived_workspace_member_cannot_read_or_mutate_workspace_notifications()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var notificationId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var workspace = await db.Workspaces.SingleAsync(item => item.Id == auth.PersonalWorkspace.Id);
            workspace.IsArchived = true;
            db.Notifications.Add(new Notification
            {
                Id = notificationId,
                UserId = auth.User.Id,
                WorkspaceId = workspace.Id,
                Type = NotificationTypes.GenerationFailed,
                DeduplicationKey = $"archived:{notificationId:N}",
                ResourceTitle = "Archived private job",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/workspaces/{auth.PersonalWorkspace.Id}")).StatusCode);
        var workspaces = await client.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        Assert.NotNull(workspaces);
        Assert.DoesNotContain(workspaces!, workspace => workspace.Id == auth.PersonalWorkspace.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/notifications?workspaceId={auth.PersonalWorkspace.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/notifications/unread-count?workspaceId={auth.PersonalWorkspace.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(client, HttpMethod.Post, $"/api/notifications/{notificationId}/read", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendWithCsrf(client, HttpMethod.Post, "/api/notifications/read-all", new { workspaceId = auth.PersonalWorkspace.Id })).StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var notification = await verificationDb.Notifications.SingleAsync(item => item.Id == notificationId);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public async Task Archived_workspace_member_cannot_read_or_mutate_personal_memories()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var memoriesPath = $"/api/workspaces/{auth.PersonalWorkspace.Id}/memories";
        using var createBeforeArchive = await SendWithCsrf(client, HttpMethod.Post, memoriesPath, new
        {
            category = "Personal",
            title = "Before archive",
            content = "Synthetic private note.",
        });
        Assert.Equal(HttpStatusCode.Created, createBeforeArchive.StatusCode);
        var created = await createBeforeArchive.Content.ReadFromJsonAsync<PersonalMemoryDto>();
        Assert.NotNull(created);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var workspace = await db.Workspaces.SingleAsync(item => item.Id == auth.PersonalWorkspace.Id);
            workspace.IsArchived = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(memoriesPath)).StatusCode);
        using var createAfterArchive = await SendWithCsrf(client, HttpMethod.Post, memoriesPath, new
        {
            category = "Personal",
            title = "After archive",
            content = "Must not be stored.",
        });
        Assert.Equal(HttpStatusCode.Forbidden, createAfterArchive.StatusCode);

        using var updateAfterArchive = await SendWithCsrf(client, HttpMethod.Patch, $"/api/memories/{created!.Id}", new
        {
            category = "Other",
            title = "Changed",
            content = "Must not replace the original.",
        });
        Assert.Equal(HttpStatusCode.Forbidden, updateAfterArchive.StatusCode);
        using var deleteAfterArchive = await SendWithCsrf(client, HttpMethod.Delete, $"/api/memories/{created.Id}", null);
        Assert.Equal(HttpStatusCode.Forbidden, deleteAfterArchive.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var persisted = await verificationDb.PersonalMemories.AsNoTracking().SingleAsync(item => item.Id == created.Id);
        Assert.Equal(auth.User.Id, persisted.UserId);
        Assert.Equal(auth.PersonalWorkspace.Id, persisted.WorkspaceId);
        Assert.Equal(created.Category, persisted.Category);
        Assert.Equal(created.Title, persisted.Title);
        Assert.Equal(created.Content, persisted.Content);
        Assert.Equal(created.Source, persisted.Source);
        Assert.Equal(created.IsActive, persisted.IsActive);
        Assert.Equal(created.CreatedAt, persisted.CreatedAt);
        Assert.Equal(created.UpdatedAt, persisted.UpdatedAt);
        Assert.Equal(1, await verificationDb.PersonalMemories.AsNoTracking()
            .CountAsync(item => item.UserId == auth.User.Id && item.WorkspaceId == auth.PersonalWorkspace.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Archived Workspace Tester",
            email = $"archived-workspace-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
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

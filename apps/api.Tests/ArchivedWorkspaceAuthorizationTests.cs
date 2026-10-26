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

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

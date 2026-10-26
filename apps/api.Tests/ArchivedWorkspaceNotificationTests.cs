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

public sealed class ArchivedWorkspaceNotificationTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private readonly GenerationJobsNoWorkerFactory factory;

    public ArchivedWorkspaceNotificationTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Archived_workspace_does_not_receive_new_billing_notifications()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, $"archived-notification-{Guid.NewGuid():N}@example.com");
        var paymentAttemptId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var workspace = await db.Workspaces.SingleAsync(item => item.Id == auth.PersonalWorkspace.Id);
            workspace.IsArchived = true;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await writer.CreateBillingPaymentFailedAsync(auth.PersonalWorkspace.Id, paymentAttemptId);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Equal(0, await verifyDb.Notifications.CountAsync(item =>
            item.WorkspaceId == auth.PersonalWorkspace.Id && item.DeduplicationKey == $"{NotificationTypes.BillingPaymentFailed}:{paymentAttemptId:N}"));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Archived Notification Tester",
            email,
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

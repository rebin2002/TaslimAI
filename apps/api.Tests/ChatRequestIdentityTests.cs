using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ChatRequestIdentityTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ChatRequestIdentityTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Taslim.Api.Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Completed_request_id_cannot_be_reused_with_different_attachments()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Attachment Identity Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");

        var first = await SendMessage(client, conversation.Id, "Summarize this", requestId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var reused = await SendMessage(client, conversation.Id, "Summarize this", requestId, [Guid.NewGuid()]);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var error = await reused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REQUEST_ID_REUSED", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Failed_request_id_cannot_retry_with_different_attachments()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Failed Attachment Identity Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");

        var first = await SendMessage(client, conversation.Id, "[[mock-failure]]", requestId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);

        var reused = await SendMessage(client, conversation.Id, "[[mock-failure]]", requestId, [Guid.NewGuid()]);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var error = await reused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REQUEST_ID_REUSED", error.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> SendMessage(HttpClient client, Guid conversationId, string content, string requestId, IReadOnlyCollection<Guid>? attachmentIds = null)
    {
        return await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages", new
        {
            content,
            requestId,
            attachmentIds = attachmentIds ?? [],
        });
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-identity-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}

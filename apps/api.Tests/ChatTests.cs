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

public sealed class ChatTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ChatTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unauthenticated_user_cannot_create_conversation()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/workspaces/{Guid.NewGuid()}/conversations", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_user_creates_conversation_and_sends_through_mock_ai_core()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Chat Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        Assert.Equal("New chat", conversation.Title);

        var response = await SendMessage(client, conversation.Id, "Hello Taslim");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(result);
        Assert.Equal("Hello Taslim", result.UserMessage.Content);
        Assert.Equal(ChatMessageStatus.Completed.ToString(), result.AssistantMessage.Status);
        Assert.True(result.AssistantMessage.IsTestResponse);
        Assert.Contains("connected and ready", result.AssistantMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hello Taslim", result.Conversation.Title);

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(2, messages.Count);
        Assert.Equal("User", messages[0].Role);
        Assert.Equal("Assistant", messages[1].Role);
        Assert.True(messages[1].IsTestResponse);
    }

    [Fact]
    public async Task Conversation_list_rename_and_archive_are_persisted()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Lifecycle Chat Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var listed = await client.GetFromJsonAsync<List<ConversationDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations");
        Assert.Contains(listed!, item => item.Id == conversation.Id);

        var renamed = await SendWithCsrf<ConversationDto>(client, HttpMethod.Patch, $"/api/conversations/{conversation.Id}", new { title = "Planning session" });
        Assert.Equal("Planning session", renamed.Title);
        var archived = await SendWithCsrf<ConversationDto>(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/archive", null);
        Assert.Equal("Archived", archived.Status);
        var active = await client.GetFromJsonAsync<List<ConversationDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations?status=Active");
        Assert.DoesNotContain(active!, item => item.Id == conversation.Id);
    }

    [Fact]
    public async Task Cross_user_conversation_access_is_denied_without_content_leak()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Private Chat Owner");
        var conversation = await CreateConversation(owner, ownerAuth.PersonalWorkspace.Id);
        await SendMessage(owner, conversation.Id, "Private content");

        using var other = factory.CreateClient();
        await Register(other, "Other Chat User");
        var get = await other.GetAsync($"/api/conversations/{conversation.Id}");
        var messages = await other.GetAsync($"/api/conversations/{conversation.Id}/messages");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, messages.StatusCode);
        Assert.DoesNotContain("Private content", await get.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Private content", await messages.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cross_workspace_conversation_creation_is_denied()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Workspace Chat Owner");
        using var other = factory.CreateClient();
        await Register(other, "Workspace Chat Other");
        var response = await SendWithCsrf(other, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_message_is_rejected_before_ai_execution()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Long Message Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var response = await SendMessage(client, conversation.Id, new string('x', 20001));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Failed_mock_provider_marks_assistant_message_failed_and_preserves_user_message()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Failure Chat Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var response = await SendMessage(client, conversation.Id, "[[mock-failure]]");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AI_GENERATION_FAILED", error.GetProperty("error").GetProperty("code").GetString());

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Completed", messages[0].Status);
        Assert.Equal("Failed", messages[1].Status);
    }

    [Fact]
    public async Task Chat_mutations_require_csrf()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "CSRF Chat Owner");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations") { Content = JsonContent.Create(new { }) };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CSRF_VALIDATION_FAILED", error.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Streaming_endpoint_emits_provider_independent_events_and_persists_final_content()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Streaming Chat Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var response = await SendMessage(client, conversation.Id, "Hello Taslim", Guid.NewGuid().ToString("N"), stream: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message.started", body);
        Assert.Contains("event: message.delta", body);
        Assert.Contains("event: message.completed", body);
        Assert.Contains("connected and ready", body, StringComparison.OrdinalIgnoreCase);

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal("Completed", messages[^1].Status);
        Assert.Contains("connected and ready", messages[^1].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeating_the_same_request_id_returns_the_existing_result_without_duplicate_messages()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Idempotent Chat Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var first = await SendMessage(client, conversation.Id, "Hello Taslim", requestId);
        var second = await SendMessage(client, conversation.Id, "Hello Taslim", requestId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<SendMessageResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody.UserMessage.Id, secondBody.UserMessage.Id);
        Assert.Equal(firstBody.AssistantMessage.Id, secondBody.AssistantMessage.Id);

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(2, messages.Count);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en"
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

    private static async Task<HttpResponseMessage> SendMessage(HttpClient client, Guid conversationId, string content, string? requestId = null, bool stream = false) =>
        await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages{(stream ? "/stream" : string.Empty)}", new { content, requestId });

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
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

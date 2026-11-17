using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;
namespace Taslim.Api.Tests;

/// <summary>
/// Chat thread replay at equal-timestamp boundaries. In production the thread is persisted in
/// PostgreSQL, whose timestamptz stores microseconds: the 0.1-microsecond offset the send path
/// adds to the assistant row of a turn is truncated, so both rows of a turn share one stored
/// timestamp. Replays of a requestId must then still see exactly the thread items that were
/// already persisted - no skipped completed response, no duplicated item from another turn.
/// The synthetic helper below collapses a completed pair onto one timestamp to reproduce that
/// truncation deterministically without relying on the database clock inside the test.
/// </summary>
public sealed class ChatMessagePaginationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;
    public ChatMessagePaginationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Replay_of_a_turn_whose_pair_shares_one_stored_timestamp_returns_the_completed_response()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Boundary Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var first = await ReadResponse(await SendMessage(client, conversation.Id, "Hello Taslim", requestId));

        await MakePairShareOneTimestamp(first.UserMessage.Id, first.AssistantMessage.Id);

        var replay = await SendMessage(client, conversation.Id, "Hello Taslim", requestId);
        var replayBody = await ReadResponse(replay);

        // The boundary pair is already complete, so the replay must return the same two thread
        // items it persisted. A completed turn that replays as an in-progress conflict skips the
        // response the client is asking for and blocks every later retry.
        Assert.Equal(first.UserMessage.Id, replayBody.UserMessage.Id);
        Assert.Equal(first.AssistantMessage.Id, replayBody.AssistantMessage.Id);
        Assert.Equal("Completed", replayBody.AssistantMessage.Status);

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Replay_of_an_old_turn_never_surfaces_a_later_turns_response_at_the_equal_timestamp_boundary()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Cross-Turn Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstRequestId = Guid.NewGuid().ToString("N");
        var firstTurn = await ReadResponse(await SendMessage(client, conversation.Id, "First turn", firstRequestId));
        await SendMessage(client, conversation.Id, "Second turn", Guid.NewGuid().ToString("N"));
        var history = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(history);
        var secondAssistant = history.Single(message => message.Sequence == 4);

        await MakePairShareOneTimestamp(firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id);

        var replay = await SendMessage(client, conversation.Id, "First turn", firstRequestId);
        var replayBody = await ReadResponse(replay);

        // After the collapse, the only assistant row strictly newer than turn one is turn two's
        // response. A timestamp-driven pairing slides across the boundary and answers turn one
        // with turn two's content: the thread duplicates that item and skips turn one's own.
        Assert.Equal(firstTurn.AssistantMessage.Id, replayBody.AssistantMessage.Id);
        Assert.NotEqual(secondAssistant.Id, replayBody.AssistantMessage.Id);
        Assert.Equal("First turn", replayBody.UserMessage.Content);

        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(4, messages.Count);
        Assert.Equal([1L, 2L, 3L, 4L], messages.Select(message => message.Sequence));
    }

    [Fact]
    public async Task Replay_with_missing_assistant_never_uses_a_later_turns_answer()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Missing Pair Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var first = await ReadResponse(await SendMessage(client, conversation.Id, "First answer", requestId));
        await ReadResponse(await SendMessage(client, conversation.Id, "Later answer", Guid.NewGuid().ToString("N")));

        // Model a missing/malformed paired assistant without dropping another turn or touching
        // the provider. A broader Sequence > predicate incorrectly borrows the next turn's
        // completed assistant row and represents it as the response to the first request.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var paired = await db.ChatMessages.SingleAsync(message => message.Id == first.AssistantMessage.Id);
            paired.Role = Taslim.Api.Domain.ChatMessageRole.User;
            await db.SaveChangesAsync();
        }

        var replay = await SendMessage(client, conversation.Id, "First answer", requestId);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var payload = await replay.Content.ReadAsStringAsync();
        Assert.Contains("MESSAGE_IN_PROGRESS", payload);
    }

    private async Task MakePairShareOneTimestamp(Guid userMessageId, Guid assistantMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var user = await db.ChatMessages.SingleAsync(message => message.Id == userMessageId);
        var assistant = await db.ChatMessages.SingleAsync(message => message.Id == assistantMessageId);
        assistant.CreatedAt = user.CreatedAt;
        await db.SaveChangesAsync();
    }

    private static async Task<SendMessageResponse> ReadResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        return body;
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-pagination-{Guid.NewGuid():N}@example.com",
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

    private static async Task<HttpResponseMessage> SendMessage(HttpClient client, Guid conversationId, string content, string requestId) =>
        await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages", new { content, requestId });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

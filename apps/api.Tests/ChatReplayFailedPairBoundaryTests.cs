using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;
using Xunit;
namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA lane for PR #1002: complementary boundary cases for Chat turn replay at equal
/// persisted timestamps. PostgreSQL timestamptz stores microseconds, so the send path's
/// 0.1-microsecond assistant offset truncates both rows of a turn onto one stored timestamp. The
/// shipped fix pairs the replayed user message with its own assistant through the deterministic
/// Sequence key; these facts pin the replay branches the existing ChatMessagePaginationTests do
/// not cover at that boundary: a Failed pair being re-queued through its own row (never a later
/// turn's answer), a Failed last pair that must retry instead of sticking in progress, a Pending
/// pair with a later completed turn that must fail closed instead of borrowing, and the
/// REQUEST_ID_REUSED mismatch guard leaving the thread byte-identical. Deterministic and
/// synthetic like the lane's tests: in-repo mock provider only, no real provider calls, no real
/// user data, timestamps collapsed through the persistence context exactly as the shipped
/// truncation stores them.
/// </summary>
public sealed class ChatReplayFailedPairBoundaryTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;
    public ChatReplayFailedPairBoundaryTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Replay_of_a_failed_pair_at_one_stored_timestamp_requeues_its_own_turn_and_never_borrows_a_later_answer()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Failed Pair Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstRequestId = Guid.NewGuid().ToString("N");
        var failedRequestId = Guid.NewGuid().ToString("N");
        var firstTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "First turn", firstRequestId));
        var failedTurnResponse = await SendMessage(client, conversation.Id, "[[mock-failure]]", failedRequestId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedTurnResponse.StatusCode);
        var thirdTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "Third turn", Guid.NewGuid().ToString("N")));

        // Reproduce the PostgreSQL storage boundary: the failed pair's two rows share one stored
        // timestamp. A strict CreatedAt > comparison now hides that turn's own failed row, and
        // the scan slides onto the later completed turn's answer, replaying turn two as turn
        // three's content with zero regeneration - the fail-open duplicate this PR removes.
        await MakePairShareOneTimestamp(firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id);
        var failedPair = await PairOf(client, conversation.Id, 3);
        await MakePairShareOneTimestamp(failedPair.user.Id, failedPair.assistant.Id);

        var replay = await SendMessage(client, conversation.Id, "[[mock-failure]]", failedRequestId);

        // Sequence pairing selects turn two's own failed row, resets it to Pending and re-queues
        // it through the normal path: the regenerated answer lands on turn two's OWN assistant
        // row. Pre-fix, the equal CreatedAt hid that row and the scan answered with turn three's
        // completed content instead (a borrowed, duplicated thread item with zero regeneration).
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await replay.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(replayBody);
        Assert.Equal(failedPair.user.Id, replayBody.UserMessage.Id);
        Assert.Equal(failedPair.assistant.Id, replayBody.AssistantMessage.Id);
        Assert.NotEqual(thirdTurn.AssistantMessage.Id, replayBody.AssistantMessage.Id);
        Assert.Equal("Completed", replayBody.AssistantMessage.Status);

        var messages = await FetchMessages(client, conversation.Id);
        Assert.Equal(6, messages.Count);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], messages.Select(message => message.Sequence));
        Assert.Equal(firstTurn.AssistantMessage.Id, messages.Single(message => message.Sequence == 2).Id);
        Assert.Equal(thirdTurn.AssistantMessage.Id, messages.Single(message => message.Sequence == 6).Id);
        // Turn three's answer was not consumed, duplicated, or altered by turn two's replay.
        Assert.Equal("Completed", messages.Single(message => message.Sequence == 6).Status);
        Assert.Equal(thirdTurn.AssistantMessage.Content, messages.Single(message => message.Sequence == 6).Content);
        // Turn two re-queued its own assistant row: same identity, regenerated, no new rows.
        Assert.Equal(failedPair.assistant.Id, messages.Single(message => message.Sequence == 4).Id);
        Assert.Equal("Completed", messages.Single(message => message.Sequence == 4).Status);
    }

    [Fact]
    public async Task Replay_of_a_last_failed_pair_at_one_stored_timestamp_retries_instead_of_sticking_in_progress()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Last Failed Pair Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstRequestId = Guid.NewGuid().ToString("N");
        var failedRequestId = Guid.NewGuid().ToString("N");
        var firstTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "First turn", firstRequestId));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendMessage(client, conversation.Id, "[[mock-failure]]", failedRequestId)).StatusCode);

        await MakePairShareOneTimestamp(firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id);
        var failedPair = await PairOf(client, conversation.Id, 3);
        await MakePairShareOneTimestamp(failedPair.user.Id, failedPair.assistant.Id);

        // With no later turn to borrow, the old timestamp scan finds nothing strictly newer and
        // answers a finished failed turn forever with 409 MESSAGE_IN_PROGRESS: the user can never
        // retry that requestId. Sequence pairing re-queues the failed row instead.
        var replay = await SendMessage(client, conversation.Id, "[[mock-failure]]", failedRequestId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.Contains("AI_GENERATION_FAILED", await replay.Content.ReadAsStringAsync());

        var messages = await FetchMessages(client, conversation.Id);
        Assert.Equal(4, messages.Count);
        Assert.Equal([1L, 2L, 3L, 4L], messages.Select(message => message.Sequence));
        Assert.Equal(failedPair.assistant.Id, messages.Single(message => message.Sequence == 4).Id);
        Assert.Equal(firstTurn.AssistantMessage.Id, messages.Single(message => message.Sequence == 2).Id);
    }

    [Fact]
    public async Task Replay_of_a_pending_pair_at_one_stored_timestamp_with_a_later_turn_fails_closed_without_borrowing()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pending Boundary Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstRequestId = Guid.NewGuid().ToString("N");
        var secondRequestId = Guid.NewGuid().ToString("N");
        await ReadCompleted(await SendMessage(client, conversation.Id, "First turn", firstRequestId));
        var secondTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "Second turn", secondRequestId));
        var thirdTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "Third turn", Guid.NewGuid().ToString("N")));

        // Persisted in-flight shape at the truncation boundary: turn two's assistant is back to
        // Pending with an empty body and its pair shares one stored timestamp. The old scan hides
        // it (equal CreatedAt) and answers turn two's replay with turn three's completed
        // response - duplicating a thread item the replay never produced.
        await SetAssistantPending(secondTurn.AssistantMessage.Id);
        await MakePairShareOneTimestamp(secondTurn.UserMessage.Id, secondTurn.AssistantMessage.Id);

        var replay = await SendMessage(client, conversation.Id, "Second turn", secondRequestId);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("MESSAGE_IN_PROGRESS", await replay.Content.ReadAsStringAsync());

        var messages = await FetchMessages(client, conversation.Id);
        Assert.Equal(6, messages.Count);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], messages.Select(message => message.Sequence));
        Assert.Equal("Pending", messages.Single(message => message.Sequence == 4).Status);
        // Turn three is untouched by the rejected replay: no borrowed duplicate, nothing lost.
        Assert.Equal(thirdTurn.AssistantMessage.Id, messages.Single(message => message.Sequence == 6).Id);
        Assert.Equal("Completed", messages.Single(message => message.Sequence == 6).Status);
        Assert.Equal(thirdTurn.AssistantMessage.Content, messages.Single(message => message.Sequence == 6).Content);
    }

    [Fact]
    public async Task Reused_request_id_with_different_content_is_rejected_at_one_stored_timestamp_without_touching_the_thread()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Mismatch Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var firstTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "Hello Taslim", requestId));
        var secondTurn = await ReadCompleted(await SendMessage(client, conversation.Id, "Second turn", Guid.NewGuid().ToString("N")));
        await MakePairShareOneTimestamp(firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id);

        var before = await FetchMessages(client, conversation.Id);
        var replay = await SendMessage(client, conversation.Id, "Hello Taslim edited", requestId);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("REQUEST_ID_REUSED", await replay.Content.ReadAsStringAsync());

        // The mismatch guard runs before pairing, so even at the equal-timestamp boundary the
        // rejected replay must leave every stored row - including the collapsed pair and both
        // statuses and timestamps - exactly as it was: no regeneration, no duplication, no loss.
        var after = await FetchMessages(client, conversation.Id);
        Assert.Equal(4, after.Count);
        Assert.Equal([1L, 2L, 3L, 4L], after.Select(message => message.Sequence));
        Assert.Equal(before.Select(message => (message.Id, message.Status, message.Content, message.CreatedAt)),
                     after.Select(message => (message.Id, message.Status, message.Content, message.CreatedAt)));
        Assert.Equal("Hello Taslim", after.Single(message => message.Sequence == 1).Content);
        Assert.Equal(firstTurn.AssistantMessage.Id, after.Single(message => message.Sequence == 2).Id);
    }

    private async Task<(ChatMessageDto user, ChatMessageDto assistant)> PairOf(HttpClient client, Guid conversationId, long userSequence)
    {
        var messages = await FetchMessages(client, conversationId);
        return (messages.Single(message => message.Sequence == userSequence),
                messages.Single(message => message.Sequence == userSequence + 1));
    }

    private async Task<List<ChatMessageDto>> FetchMessages(HttpClient client, Guid conversationId)
    {
        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversationId}/messages");
        Assert.NotNull(messages);
        return messages;
    }

    private static async Task<SendMessageResponse> ReadCompleted(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        return body;
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

    private async Task SetAssistantPending(Guid assistantMessageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var assistant = await db.ChatMessages.SingleAsync(message => message.Id == assistantMessageId);
        assistant.Status = Taslim.Api.Domain.ChatMessageStatus.Pending;
        assistant.Content = string.Empty;
        await db.SaveChangesAsync();
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-replay-boundary-{Guid.NewGuid():N}@example.com",
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

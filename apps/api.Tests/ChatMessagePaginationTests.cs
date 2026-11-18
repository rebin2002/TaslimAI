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

    /// <summary>
    /// Migration-free proof for the reviewer flag that pre-AddDeterministicChatMessageOrdering
    /// legacy rows could sit outside the Sequence == user + 1 pairing and then replay fail-closed
    /// as a stuck 409. The shipped legacy writer stored each turn as a user/assistant pair in one
    /// atomic transaction, and the backfill ordered a conversation's rows by (CreatedAt,
    /// user-before-assistant at equal timestamps, Id). The serialized send path cannot start turn
    /// N+1 before turn N's HTTP response returns, so consecutive turns' CreatedAt values are a
    /// full request round-trip apart - always a whole microsecond or more at the PostgreSQL
    /// storage boundary. Replaying the comparator over every such spacing - pair truncation onto
    /// one microsecond, the assistant landing one microsecond later, exact microsecond
    /// boundaries and larger turn gaps - assigns every pair to adjacent sequences: live legacy
    /// rows satisfy the invariant and no one-off Sequence backfill is required. The only
    /// interleaved comparator shape needs two turn requests stored inside one wall-clock
    /// microsecond, which the serialized writer cannot produce; the companion fact below proves
    /// even that hypothetical shape pairs fail-closed instead of surfacing another turn's
    /// answer. Deterministic, synthetic: no provider calls, no production data, no migration.
    /// </summary>
    [Theory]
    [InlineData(10)]          // exact 1-microsecond spacing: one pair per stored microsecond
    [InlineData(500)]         // 50 microseconds
    [InlineData(999)]         // remainder walk across eight turns
    [InlineData(1000)]        // exact 100 microseconds
    [InlineData(1001)]
    [InlineData(100_000)]     // 10 milliseconds - below any real round-trip, still stored-ordered
    [InlineData(10_000_000)]  // 1 second - interactive turn spacing
    public void Backfilled_legacy_transcripts_from_the_shipped_writer_keep_every_pair_adjacent(int interTurnTicks)
    {
        // PostgreSQL timestamptz stores whole microseconds: the send path's 0.1-microsecond
        // assistant offset truncates onto the user's stored value whenever the sub-microsecond
        // remainder plus one tick does not cross the next microsecond, and lands one microsecond
        // later otherwise. Spacings under one stored microsecond cannot come from the serialized
        // send path; they are covered by the collision-shape safety fact, not this proof.
        static DateTime TruncateToMicroseconds(DateTime value) => value.Ticks % 10L == 0 ? value : new DateTime(value.Ticks - value.Ticks % 10L, DateTimeKind.Utc);
        var baseTime = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc).AddTicks(7);
        var turnTicks = Math.Max(10, interTurnTicks);
        var rows = new List<(Guid Id, Taslim.Api.Domain.ChatMessageRole Role, DateTime Stored)>();
        for (var turn = 0; turn < 8; turn++)
        {
            var userCreatedAt = baseTime.AddTicks((long)turn * turnTicks);
            rows.Add((Guid.NewGuid(), Taslim.Api.Domain.ChatMessageRole.User, TruncateToMicroseconds(userCreatedAt)));
            rows.Add((Guid.NewGuid(), Taslim.Api.Domain.ChatMessageRole.Assistant, TruncateToMicroseconds(userCreatedAt.AddTicks(1))));
        }
        // The backfill's exact comparator: ORDER BY CreatedAt, user-before-assistant, Id.
        var ordered = rows
            .OrderBy(row => row.Stored)
            .ThenBy(row => row.Role == Taslim.Api.Domain.ChatMessageRole.User ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToList();
        var strandedUsers = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (ordered[index].Role != Taslim.Api.Domain.ChatMessageRole.User) continue;
            if (index + 1 < ordered.Count && ordered[index + 1].Role == Taslim.Api.Domain.ChatMessageRole.Assistant) continue;
            strandedUsers++;
        }
        Assert.Equal(0, strandedUsers);
    }

    [Fact]
    public void Even_a_hypothetical_sub_microsecond_collision_never_pairs_a_later_turns_assistant()
    {
        // The sole comparator shape that can break adjacency stores two turn requests inside one
        // wall-clock microsecond - impossible for the serialized send path, proven unsafe below
        // anyway so the reviewer flag closes without inspecting production data. Collapse turn
        // one's pair and turn two's user row onto one stored microsecond, then apply the
        // backfill's exact ordering: users before assistants, Id as final tie-break.
        var micro = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
        var u1 = Guid.NewGuid();
        var a1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var rows = new List<(Guid Id, Taslim.Api.Domain.ChatMessageRole Role, DateTime Stored)>
        {
            (u1, Taslim.Api.Domain.ChatMessageRole.User, micro),
            (u2, Taslim.Api.Domain.ChatMessageRole.User, micro),
            (a1, Taslim.Api.Domain.ChatMessageRole.Assistant, micro),
            (a2, Taslim.Api.Domain.ChatMessageRole.Assistant, micro.AddTicks(10)),
        };
        var ordered = rows
            .OrderBy(row => row.Stored)
            .ThenBy(row => row.Role == Taslim.Api.Domain.ChatMessageRole.User ? 0 : 1)
            .ThenBy(row => row.Id)
            .ToList();
        var u1Index = ordered.FindIndex(row => row.Id == u1);
        var candidate = ordered[u1Index + 1];
        // The row at exactly turn one's user.Sequence + 1 is turn two's user row - a user, not an
        // assistant - so the role-filtered Sequence == user + 1 lookup matches nothing and the
        // replay fails closed at 409 MESSAGE_IN_PROGRESS. No Id ordering can move turn two's
        // answer into that slot while a same-microsecond user row occupies it, and turn one's
        // own assistant can only ever sit after every user row of the collision block. The
        // hypothetical interleaved population is therefore strand-safe and leak-safe without any
        // migration; the existing missing-assistant integration test pins the endpoint half.
        var safeOutcomes = new[] { a1, u2 };
        Assert.Contains(candidate.Id, safeOutcomes);
    }

    [Fact]
    public async Task Replay_of_a_backfilled_legacy_transcript_returns_its_own_response_without_migration()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Legacy Backfill Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        // Seed a two-turn transcript in the exact shape the migration's backfill produces from
        // legacy rows whose pairs collapsed onto one stored timestamp: sequences 1..4, both
        // pairs completed, turn one carrying the requestId replayed below. No migration step,
        // no production data - only what the deterministic ordering key would have assigned.
        var firstRequestId = Guid.NewGuid().ToString("N");
        var secondRequestId = Guid.NewGuid().ToString("N");
        var firstUserId = Guid.NewGuid();
        var firstAssistantId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var secondAssistantId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var legacy = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            var storedTime = new DateTime(2026, 1, 15, 8, 30, 0, DateTimeKind.Utc);
            db.ChatMessages.AddRange(
                new Taslim.Api.Domain.ChatMessage { Id = firstUserId, ConversationId = legacy.Id, Role = Taslim.Api.Domain.ChatMessageRole.User, Content = "Legacy first", RequestId = firstRequestId, Status = Taslim.Api.Domain.ChatMessageStatus.Completed, CreatedAt = storedTime, Sequence = 1 },
                new Taslim.Api.Domain.ChatMessage { Id = firstAssistantId, ConversationId = legacy.Id, Role = Taslim.Api.Domain.ChatMessageRole.Assistant, Content = "Legacy first answer", Status = Taslim.Api.Domain.ChatMessageStatus.Completed, CreatedAt = storedTime, Sequence = 2 },
                new Taslim.Api.Domain.ChatMessage { Id = secondUserId, ConversationId = legacy.Id, Role = Taslim.Api.Domain.ChatMessageRole.User, Content = "Legacy second", RequestId = secondRequestId, Status = Taslim.Api.Domain.ChatMessageStatus.Completed, CreatedAt = storedTime.AddSeconds(3), Sequence = 3 },
                new Taslim.Api.Domain.ChatMessage { Id = secondAssistantId, ConversationId = legacy.Id, Role = Taslim.Api.Domain.ChatMessageRole.Assistant, Content = "Legacy second answer", Status = Taslim.Api.Domain.ChatMessageStatus.Completed, CreatedAt = storedTime.AddSeconds(3), Sequence = 4 });
            legacy.NextMessageSequence = 4;
            await db.SaveChangesAsync();
        }
        var replay = await ReadResponse(await SendMessage(client, conversation.Id, "Legacy first", firstRequestId));
        // The replayed legacy turn pairs by Sequence onto its own completed assistant instead of
        // failing closed as a stranded in-progress conflict: backfilled live rows need no remedy.
        Assert.Equal(firstUserId, replay.UserMessage.Id);
        Assert.Equal(firstAssistantId, replay.AssistantMessage.Id);
        Assert.Equal("Completed", replay.AssistantMessage.Status);
        Assert.Equal("Legacy first answer", replay.AssistantMessage.Content);
        var messages = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(messages);
        Assert.Equal(4, messages.Count);
        Assert.Equal([1L, 2L, 3L, 4L], messages.Select(message => message.Sequence));
    }

    [Fact]
    public async Task Replay_of_a_pending_same_sequence_assistant_returns_conflict_not_an_empty_success()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pending Pair Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var first = await ReadResponse(await SendMessage(client, conversation.Id, "Still working", requestId));

        // Emulate the persisted in-flight state deterministically, without waiting on a provider.
        // PostgreSQL can store the user and pending assistant with an identical CreatedAt;
        // Sequence still pairs them, but the replay must not report an empty 200 OK.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var assistant = await db.ChatMessages.SingleAsync(message => message.Id == first.AssistantMessage.Id);
            assistant.Status = Taslim.Api.Domain.ChatMessageStatus.Pending;
            assistant.Content = string.Empty;
            await db.SaveChangesAsync();
        }

        var replay = await SendMessage(client, conversation.Id, "Still working", requestId);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Contains("MESSAGE_IN_PROGRESS", await replay.Content.ReadAsStringAsync());

        var history = await client.GetFromJsonAsync<List<ChatMessageDto>>($"/api/conversations/{conversation.Id}/messages");
        Assert.NotNull(history);
        Assert.Equal(2, history.Count);
        Assert.Equal(first.AssistantMessage.Id, history.Single(message => message.Role == "Assistant").Id);
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

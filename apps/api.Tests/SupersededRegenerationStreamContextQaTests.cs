using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA lane for #lane regen-superseded-context (#998). Test-only.
///
/// #998 pins that a superseded regeneration response stops replaying into the prompt built for a
/// later <em>SendMessage</em> turn. The sibling prompt call site is
/// <c>POST /api/conversations/{id}/messages/stream</c> (<c>ChatController.StreamMessage</c>), which
/// reaches <c>BuildContextAsync(prepared.Conversation!, prepared.UserMessage!.Id, cancellationToken)</c>
/// with no <c>excludedAssistantAfterSequence</c> argument — exactly the later-turn path the lane
/// fixes — yet no existing or in-flight test asserts the exclusion on a later stream turn (the
/// current stream tests in ContextIntegrationTests/FileTests only exercise first-turn attachment
/// sends), and none asserts the second half of the lane's promise: the transcript rows themselves
/// stay immutable while the model context is filtered.
///
/// This file is the deterministic cross-request negative control for that uncovered leg: request
/// one sends an ordinary JSON message, the assistant answer is superseded through the real
/// regeneration endpoint, and request two — a later stream send — must present a model prompt that
/// carries the live replacement alone, while the persisted transcript keeps every row, unedited,
/// in sequence order. Synthetic fixture with a recording fake provider: no real prompts,
/// no personal chats, no provider spend, no attachments, no shared-file edits.
/// </summary>
public sealed class SupersededRegenerationStreamContextQaTests : IClassFixture<StreamQaRecordingFactory>
{
    private readonly StreamQaRecordingFactory factory;

    public SupersededRegenerationStreamContextQaTests(StreamQaRecordingFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Superseded_response_stays_out_of_the_later_stream_prompt_while_the_transcript_remains_immutable()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Stream Supersession QA Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();

        // Request one: an ordinary JSON turn establishes the assistant answer to supersede.
        var firstTurn = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "STREAMQA_FIRST_USER",
            requestId = Guid.NewGuid().ToString("N"),
        });

        // Deterministically mark the answer that the upcoming regeneration will supersede.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var originalAssistant = await db.ChatMessages.SingleAsync(message => message.Id == firstTurn.AssistantMessage.Id);
        var supersededSequence = originalAssistant.Sequence;
        originalAssistant.Content = "STREAMQA_SUPERSEDED_MARKER";
        await db.SaveChangesAsync();

        // The user replaces the answer through the real regeneration endpoint.
        var regeneration = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate", new
        {
            requestId = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal(HttpStatusCode.OK, regeneration.StatusCode);
        Assert.Contains("message.completed", await regeneration.Content.ReadAsStringAsync());
        var liveAssistantId = ExtractEventData(await regeneration.Content.ReadAsStringAsync(), "message.completed")
            .RootElement.GetProperty("assistantMessage").GetProperty("id").GetGuid();
        Assert.NotEqual(firstTurn.AssistantMessage.Id, liveAssistantId);

        // Request two: the later turn whose prompt is built by StreamMessage -> BuildContextAsync.
        var callsBeforeFollowUp = recorder.CallCount;
        var followUp = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/stream", new
        {
            content = "STREAMQA_FOLLOW_UP_USER",
            requestId = Guid.NewGuid().ToString("N"),
        });
        Assert.Equal(HttpStatusCode.OK, followUp.StatusCode);
        Assert.Contains("message.completed", await followUp.Content.ReadAsStringAsync());
        Assert.Equal(callsBeforeFollowUp + 1, recorder.CallCount);

        // Negative control on the model prompt: the superseded answer must not re-enter it, the
        // live replacement must be the only assistant row, and no two assistant rows may sit
        // adjacent, which would mean a turn was dropped or duplicated while filtering.
        var prompt = recorder.LastRequest!.Messages.ToArray();
        Assert.DoesNotContain(prompt, message => message.Content == "STREAMQA_SUPERSEDED_MARKER");
        Assert.Contains(prompt, message => message.Content == "STREAMQA_LIVE_REPLACEMENT");
        Assert.Equal("STREAMQA_FOLLOW_UP_USER", prompt[^1].Content);
        var assistantContents = prompt.Where(message => message.Role == "assistant").Select(message => message.Content).ToArray();
        Assert.Single(assistantContents);
        Assert.Equal("STREAMQA_LIVE_REPLACEMENT", assistantContents[0]);
        for (var index = 1; index < prompt.Length; index++)
        {
            var adjacentAssistants = prompt[index].Role == "assistant" && prompt[index - 1].Role == "assistant";
            Assert.False(adjacentAssistants, $"Prompt messages at {index - 1} and {index} are both assistant rows, so a turn lost or duplicated its live answer.");
        }

        // The other half of the promise: filtering the context never edits the transcript. Every
        // row - including the superseded one and its verbatim marker - survives in sequence order.
        var transcript = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Sequence)
            .ToListAsync();
        // Five rows stand after the two turns: the first user message, the superseded answer,
        // its live replacement, the follow-up user message, and the follow-up answer. Building
        // the filtered context deletes and rewrites none of them.
        Assert.Equal(5, transcript.Count);
        var supersededRow = transcript.Single(message => message.Id == firstTurn.AssistantMessage.Id);
        Assert.Equal("STREAMQA_SUPERSEDED_MARKER", supersededRow.Content);
        Assert.Equal(ChatMessageRole.Assistant, supersededRow.Role);
        Assert.Equal(ChatMessageStatus.Completed, supersededRow.Status);
        Assert.Equal(supersededSequence, supersededRow.Sequence);
        var replacementRow = transcript.Single(message => message.Id == liveAssistantId);
        Assert.Equal("STREAMQA_LIVE_REPLACEMENT", replacementRow.Content);
        Assert.True(supersededRow.Sequence < replacementRow.Sequence);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L, 5L }, transcript.Select(message => message.Sequence).ToArray());
        Assert.Equal(
            new[] { ChatMessageRole.User, ChatMessageRole.Assistant, ChatMessageRole.Assistant, ChatMessageRole.User, ChatMessageRole.Assistant },
            transcript.Select(message => message.Role).ToArray());
        Assert.Equal(
            new[] { "STREAMQA_FIRST_USER", "STREAMQA_SUPERSEDED_MARKER", "STREAMQA_LIVE_REPLACEMENT", "STREAMQA_FOLLOW_UP_USER", StreamQaRecordingChatCompletionService.ReplacementContent },
            transcript.Select(message => message.Content).ToArray());
    }

    [Fact]
    public async Task Concurrent_send_that_wins_after_regeneration_admission_prevents_misordered_regeneration()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Send Wins Regeneration Race Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "STREAMQA_RACE_FIRST_USER",
            requestId = Guid.NewGuid().ToString("N"),
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var originalAssistant = await db.ChatMessages.SingleAsync(message => message.Id == firstTurn.AssistantMessage.Id);
            originalAssistant.Content = "STREAMQA_RACE_ORIGINAL_ASSISTANT";
            await db.SaveChangesAsync();
        }

        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        var providerCallsBeforeRace = recorder.CallCount;
        var gate = factory.Services.GetRequiredService<StreamQaInterleavingGate>();
        gate.ArmSequenceReservation();
        var regenerationRequestId = Guid.NewGuid().ToString("N");
        var regenerationTask = SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = regenerationRequestId });
        try
        {
            await gate.WaitForSequenceReservationAsync(TimeSpan.FromSeconds(10));
            var overlappingSend = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post,
                $"/api/conversations/{conversation.Id}/messages",
                new { content = StreamQaRecordingChatCompletionService.OverlappingSendUser, requestId = Guid.NewGuid().ToString("N") });
            Assert.Equal(StreamQaRecordingChatCompletionService.OverlappingSendUser, overlappingSend.UserMessage.Content);
        }
        finally
        {
            gate.ReleaseSequenceReservation();
        }

        using var regeneration = await regenerationTask;
        var regenerationBody = await regeneration.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, regeneration.StatusCode);
        Assert.Contains("MESSAGE_NOT_REGENERABLE", regenerationBody, StringComparison.Ordinal);
        Assert.DoesNotContain("message.completed", regenerationBody, StringComparison.Ordinal);
        Assert.Equal(providerCallsBeforeRace + 1, recorder.CallCount); // Only the overlapping send reaches the fake provider; stale regeneration does not.

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var transcript = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id)
                .OrderBy(message => message.Sequence)
                .ToListAsync();
            Assert.Equal(new long[] { 1, 2, 3, 4 }, transcript.Select(message => message.Sequence).ToArray());
            Assert.DoesNotContain(transcript, message => message.RequestId == regenerationRequestId);
            Assert.Contains(transcript, message => message.Content == "STREAMQA_RACE_ORIGINAL_ASSISTANT");
        }

        await SendMessage(client, conversation.Id, "STREAMQA_AFTER_REJECTED_REGEN_USER");
        var laterPrompt = recorder.LastRequest!.Messages.ToArray();
        Assert.Contains(laterPrompt, message => message.Content == "STREAMQA_RACE_ORIGINAL_ASSISTANT");
        Assert.Contains(laterPrompt, message => message.Content == StreamQaRecordingChatCompletionService.OverlappingSendAnswer);
        Assert.Equal(2, laterPrompt.Count(message => message.Role == "assistant"));
    }

    [Fact]
    public async Task Concurrent_send_after_regeneration_reservation_is_excluded_from_regeneration_prompt()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Regeneration Wins Send Race Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "STREAMQA_RESERVED_FIRST_USER",
            requestId = Guid.NewGuid().ToString("N"),
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var originalAssistant = await db.ChatMessages.SingleAsync(message => message.Id == firstTurn.AssistantMessage.Id);
            originalAssistant.Content = "STREAMQA_RESERVED_ORIGINAL_ASSISTANT";
            await db.SaveChangesAsync();
        }

        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        var gate = factory.Services.GetRequiredService<StreamQaInterleavingGate>();
        gate.ArmContextRead();
        var regenerationTask = SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = Guid.NewGuid().ToString("N") });
        try
        {
            await gate.WaitForContextReadAsync(TimeSpan.FromSeconds(10));
            var overlappingSend = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post,
                $"/api/conversations/{conversation.Id}/messages",
                new { content = StreamQaRecordingChatCompletionService.OverlappingSendUser, requestId = Guid.NewGuid().ToString("N") });
            Assert.Equal(StreamQaRecordingChatCompletionService.OverlappingSendUser, overlappingSend.UserMessage.Content);
        }
        finally
        {
            gate.ReleaseContextRead();
        }

        using var regeneration = await regenerationTask;
        var regenerationBody = await regeneration.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, regeneration.StatusCode);
        Assert.Contains("message.completed", regenerationBody, StringComparison.Ordinal);
        var regenerationPrompt = recorder.LastRequest!.Messages.ToArray();
        Assert.Contains(regenerationPrompt, message => message.Content == "STREAMQA_RESERVED_FIRST_USER");
        Assert.DoesNotContain(regenerationPrompt, message => message.Content == StreamQaRecordingChatCompletionService.OverlappingSendUser);
        Assert.DoesNotContain(regenerationPrompt, message => message.Content == StreamQaRecordingChatCompletionService.OverlappingSendAnswer);

        await SendMessage(client, conversation.Id, "STREAMQA_AFTER_SUCCESSFUL_RACE_USER");
        var laterPrompt = recorder.LastRequest!.Messages.ToArray();
        Assert.DoesNotContain(laterPrompt, message => message.Content == "STREAMQA_RESERVED_ORIGINAL_ASSISTANT");
        Assert.Contains(laterPrompt, message => message.Content == StreamQaRecordingChatCompletionService.ReplacementContent);
        Assert.Contains(laterPrompt, message => message.Content == StreamQaRecordingChatCompletionService.OverlappingSendAnswer);
        Assert.Equal(2, laterPrompt.Count(message => message.Role == "assistant"));
    }

    [Fact]
    public async Task Concurrent_regeneration_with_same_request_id_returns_the_winning_pending_state()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Duplicate Regeneration Request Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendMessage(client, conversation.Id, "STREAMQA_DUPLICATE_REQUEST_SOURCE");
        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        var providerCallsBeforeRace = recorder.CallCount;
        var gate = factory.Services.GetRequiredService<StreamQaSaveChangesGate>();
        var requestId = Guid.NewGuid().ToString("N");
        gate.ArmRequestIdInsertBarrier(requestId);
        recorder.ArmStreamBarrier();

        var firstRequest = SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId });
        Task<HttpResponseMessage>? secondRequest = null;
        try
        {
            await gate.WaitForFirstRequestIdInsertAsync(TimeSpan.FromSeconds(10));
            secondRequest = SendWithCsrf(client, HttpMethod.Post,
                $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
                new { requestId });
            await gate.WaitForSecondRequestIdInsertAsync(TimeSpan.FromSeconds(10));

            gate.ReleaseFirstRequestIdInsert();
            await recorder.WaitForStreamStartAsync(TimeSpan.FromSeconds(10));
            gate.ReleaseSecondRequestIdInsert();

            using var competingResponse = await secondRequest!;
            var competingBody = await competingResponse.Content.ReadAsStringAsync();
            Assert.Contains("MESSAGE_IN_PROGRESS", competingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("AI_GENERATION_FAILED", competingBody, StringComparison.Ordinal);
            Assert.DoesNotContain("message.completed", competingBody, StringComparison.Ordinal);

            recorder.ReleaseStream();
            using var winningResponse = await firstRequest;
            Assert.Contains("message.completed", await winningResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(providerCallsBeforeRace + 1, recorder.CallCount);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var claimedRows = await db.ChatMessages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id && message.RequestId == requestId)
                .ToListAsync();
            var claimedRow = Assert.Single(claimedRows);
            Assert.Equal(ChatMessageStatus.Completed, claimedRow.Status);
            Assert.Equal(firstTurn.AssistantMessage.Sequence + 1, claimedRow.Sequence);
        }
        finally
        {
            gate.ReleaseFirstRequestIdInsert();
            gate.ReleaseSecondRequestIdInsert();
            recorder.ReleaseStream();
        }
    }

    [Fact]
    public async Task Concurrent_same_user_two_turn_regenerations_reject_a_cross_turn_row_found_on_the_second_lookup()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Second Lookup Cross-Turn Race Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendMessage(client, conversation.Id, "SECOND_LOOKUP_FIRST_TURN_USER");
        var requestId = Guid.NewGuid().ToString("N");
        var saveGate = factory.Services.GetRequiredService<StreamQaSaveChangesGate>();
        var lookupGate = factory.Services.GetRequiredService<StreamQaInterleavingGate>();
        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        saveGate.ArmRequestIdInsertBarrier(requestId);
        recorder.ArmStreamBarrier();

        // This old-turn request passes both admission checks and reaches SaveChanges, but its row
        // is not committed yet. The later ordinary turn can therefore finish before the row exists.
        var firstTurnRegenerationTask = SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId });
        Task<HttpResponseMessage>? secondTurnRegenerationTask = null;
        try
        {
            await saveGate.WaitForFirstRequestIdInsertAsync(TimeSpan.FromSeconds(10));
            var secondTurn = await SendMessage(client, conversation.Id, "SECOND_LOOKUP_SECOND_TURN_USER");

            // The second-turn request's first controller lookup sees no row. Pause only its
            // second StreamRegenerationAsync lookup, then let the old-turn claim commit as Pending.
            lookupGate.ArmSecondRequestIdLookup(requestId);
            secondTurnRegenerationTask = SendWithCsrf(client, HttpMethod.Post,
                $"/api/conversations/{conversation.Id}/messages/{secondTurn.AssistantMessage.Id}/regenerate",
                new { requestId });
            await lookupGate.WaitForSecondRequestIdLookupAsync(TimeSpan.FromSeconds(10));

            saveGate.ReleaseFirstRequestIdInsert();
            await recorder.WaitForStreamStartAsync(TimeSpan.FromSeconds(10));
            var callsWhileFirstTurnIsPending = recorder.CallCount;
            lookupGate.ReleaseSecondRequestIdLookup();

            using var secondTurnResponse = await secondTurnRegenerationTask;
            var secondTurnBody = await secondTurnResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, secondTurnResponse.StatusCode);
            Assert.Contains("REQUEST_ID_REUSED", secondTurnBody, StringComparison.Ordinal);
            Assert.DoesNotContain("MESSAGE_IN_PROGRESS", secondTurnBody, StringComparison.Ordinal);
            Assert.DoesNotContain("message.completed", secondTurnBody, StringComparison.Ordinal);
            Assert.Equal(callsWhileFirstTurnIsPending, recorder.CallCount);
        }
        finally
        {
            saveGate.ReleaseFirstRequestIdInsert();
            saveGate.ReleaseSecondRequestIdInsert();
            lookupGate.ReleaseSecondRequestIdLookup();
            recorder.ReleaseStream();
        }

        using var firstTurnResponse = await firstTurnRegenerationTask;
        Assert.Contains("message.completed", await firstTurnResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var requestRows = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id && message.RequestId == requestId)
            .ToListAsync();
        var requestRow = Assert.Single(requestRows);
        Assert.Equal(ChatMessageStatus.Completed, requestRow.Status);
        Assert.Equal(firstTurn.AssistantMessage.Sequence + 1, requestRow.Sequence);
        var transcript = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Sequence)
            .ToListAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, transcript.Select(message => message.Sequence).ToArray());
    }

    [Fact]
    public async Task Failed_regeneration_retry_reuses_its_sequence_without_overwriting_a_later_turn_timestamp()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Regeneration Retry Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "STREAMQA_RETRY_SOURCE_USER",
            requestId = Guid.NewGuid().ToString("N"),
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var originalAssistant = await db.ChatMessages.SingleAsync(message => message.Id == firstTurn.AssistantMessage.Id);
            originalAssistant.Content = "STREAMQA_RETRY_SUPERSEDED_ASSISTANT";
            await db.SaveChangesAsync();
        }

        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        recorder.FailNextStream = true;
        var regenerationRequestId = Guid.NewGuid().ToString("N");
        using var failedAttempt = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = regenerationRequestId });
        Assert.Contains("message.failed", await failedAttempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        long failedSequence;
        DateTime failedCreatedAt;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var failedRow = await db.ChatMessages.SingleAsync(message => message.ConversationId == conversation.Id && message.RequestId == regenerationRequestId);
            Assert.Equal(ChatMessageStatus.Failed, failedRow.Status);
            failedSequence = failedRow.Sequence;
            failedCreatedAt = failedRow.CreatedAt;
            Assert.Equal(firstTurn.AssistantMessage.Sequence + 1, failedSequence);
        }

        // Pause the retry after it has loaded the conversation and built its source-bounded
        // prompt. A synthetic ordinary turn then commits a later LastMessageAt before the retry
        // saves its stale tracked Conversation entity.
        var providerCallsBeforeRetry = recorder.CallCount;
        recorder.ArmStreamBarrier();
        var retryTask = SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = regenerationRequestId });
        DateTime? lastMessageAtAfterLaterTurn = null;
        try
        {
            await recorder.WaitForStreamStartAsync(TimeSpan.FromSeconds(10));
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entity = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            var laterUserCreatedAt = failedCreatedAt.AddMinutes(1);
            var laterAssistantCreatedAt = laterUserCreatedAt.AddTicks(1);
            db.ChatMessages.AddRange(
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = "STREAMQA_RETRY_LATER_USER", Status = ChatMessageStatus.Completed, CreatedAt = laterUserCreatedAt, Sequence = failedSequence + 1, RequestId = Guid.NewGuid().ToString("N") },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = "STREAMQA_RETRY_LATER_ASSISTANT", Status = ChatMessageStatus.Completed, CreatedAt = laterAssistantCreatedAt, Sequence = failedSequence + 2 });
            entity.NextMessageSequence = failedSequence + 2;
            entity.LastMessageAt = laterAssistantCreatedAt;
            await db.SaveChangesAsync();
            lastMessageAtAfterLaterTurn = entity.LastMessageAt;
        }
        finally
        {
            recorder.ReleaseStream();
        }
        Assert.NotNull(lastMessageAtAfterLaterTurn);
        using var retriedAttempt = await retryTask;
        var retryBody = await retriedAttempt.Content.ReadAsStringAsync();
        Assert.Contains("message.completed", retryBody, StringComparison.Ordinal);
        Assert.Equal(providerCallsBeforeRetry + 1, recorder.CallCount);
        var retryPrompt = recorder.LastRequest!.Messages.ToArray();
        Assert.Contains(retryPrompt, message => message.Content == "STREAMQA_RETRY_SOURCE_USER");
        Assert.DoesNotContain(retryPrompt, message => message.Content == "STREAMQA_RETRY_LATER_USER");
        Assert.DoesNotContain(retryPrompt, message => message.Content == "STREAMQA_RETRY_SUPERSEDED_ASSISTANT");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var retriedRow = await db.ChatMessages.SingleAsync(message => message.ConversationId == conversation.Id && message.RequestId == regenerationRequestId);
            Assert.Equal(ChatMessageStatus.Completed, retriedRow.Status);
            Assert.Equal(failedSequence, retriedRow.Sequence);
            var retriedConversation = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
            Assert.Equal(lastMessageAtAfterLaterTurn, retriedConversation.LastMessageAt);
        }

        var providerCallsBeforeReplay = recorder.CallCount;
        using var replayedAttempt = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = regenerationRequestId });
        Assert.Contains("message.completed", await replayedAttempt.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(providerCallsBeforeReplay, recorder.CallCount);

        await SendMessage(client, conversation.Id, "STREAMQA_AFTER_RETRY_USER");
        var laterPrompt = recorder.LastRequest!.Messages.ToArray();
        Assert.DoesNotContain(laterPrompt, message => message.Content == "STREAMQA_RETRY_SUPERSEDED_ASSISTANT");
        Assert.Contains(laterPrompt, message => message.Content == StreamQaRecordingChatCompletionService.ReplacementContent);
        Assert.Contains(laterPrompt, message => message.Content == "STREAMQA_RETRY_LATER_ASSISTANT");
        Assert.Equal(2, laterPrompt.Count(message => message.Role == "assistant"));
    }

    [Fact]
    public async Task Completed_regeneration_request_id_cannot_be_replayed_for_a_different_turn()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Regeneration Request ID Scope Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var firstTurn = await SendMessage(client, conversation.Id, "REQUEST_SCOPE_FIRST_USER");
        var secondTurn = await SendMessage(client, conversation.Id, "REQUEST_SCOPE_SECOND_USER");
        var recorder = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>();
        var laterTurnRequestId = Guid.NewGuid().ToString("N");

        using var laterTurnRegeneration = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{secondTurn.AssistantMessage.Id}/regenerate",
            new { requestId = laterTurnRequestId });
        Assert.Contains("message.completed", await laterTurnRegeneration.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var providerCallsAfterValidRequest = recorder.CallCount;

        using var wrongTurnReplay = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{firstTurn.AssistantMessage.Id}/regenerate",
            new { requestId = laterTurnRequestId });
        var wrongTurnBody = await wrongTurnReplay.Content.ReadAsStringAsync();
        Assert.Contains("REQUEST_ID_REUSED", wrongTurnBody, StringComparison.Ordinal);
        Assert.DoesNotContain("message.completed", wrongTurnBody, StringComparison.Ordinal);
        Assert.Equal(providerCallsAfterValidRequest, recorder.CallCount);
    }

    [Fact]
    public async Task Equal_timestamp_legacy_backfill_preserves_ambiguous_assistant_runs()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Legacy Timestamp Ordering Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var legacyTimestamp = new DateTime(2026, 11, 26, 19, 0, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entity = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            entity.NextMessageSequence = 4;
            db.ChatMessages.AddRange(
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = "LEGACY_FIRST_USER", Status = ChatMessageStatus.Completed, CreatedAt = legacyTimestamp, Sequence = 1, RequestId = Guid.NewGuid().ToString("N") },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = "LEGACY_SECOND_USER", Status = ChatMessageStatus.Completed, CreatedAt = legacyTimestamp, Sequence = 2, RequestId = Guid.NewGuid().ToString("N") },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = "LEGACY_FIRST_ASSISTANT", Status = ChatMessageStatus.Completed, CreatedAt = legacyTimestamp.AddSeconds(1), Sequence = 3 },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = "LEGACY_SECOND_ASSISTANT", Status = ChatMessageStatus.Completed, CreatedAt = legacyTimestamp.AddSeconds(2), Sequence = 4 });
            await db.SaveChangesAsync();
        }

        // This is a semantically ambiguous migration shape: two old users share CreatedAt, so the
        // old schema cannot pair them reliably with the later assistant rows, even after timestamps advance.
        await SendMessage(client, conversation.Id, "LEGACY_LATER_USER");
        var prompt = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>().LastRequest!.Messages.ToArray();
        Assert.Contains(prompt, message => message.Content == "LEGACY_FIRST_ASSISTANT");
        Assert.Contains(prompt, message => message.Content == "LEGACY_SECOND_ASSISTANT");
        Assert.Equal(2, prompt.Count(message => message.Role == "assistant"));
    }

    [Fact]
    public async Task Equal_timestamp_users_in_separate_sequence_ordered_turns_do_not_retain_superseded_answers()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Sequence Ordered Timestamp Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var sharedUserTimestamp = new DateTime(2026, 11, 26, 17, 0, 0, DateTimeKind.Utc);
        var secondAssistantId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entity = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            entity.NextMessageSequence = 4;
            db.ChatMessages.AddRange(
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = "SEQUENCE_FIRST_USER", Status = ChatMessageStatus.Completed, CreatedAt = sharedUserTimestamp, Sequence = 1, RequestId = Guid.NewGuid().ToString("N") },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = "SEQUENCE_FIRST_ASSISTANT", Status = ChatMessageStatus.Completed, CreatedAt = sharedUserTimestamp.AddTicks(1), Sequence = 2 },
                new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id, Role = ChatMessageRole.User, Content = "SEQUENCE_SECOND_USER", Status = ChatMessageStatus.Completed, CreatedAt = sharedUserTimestamp, Sequence = 3, RequestId = Guid.NewGuid().ToString("N") },
                new ChatMessage { Id = secondAssistantId, ConversationId = conversation.Id, Role = ChatMessageRole.Assistant, Content = "SEQUENCE_SECOND_SUPERSEDED_ASSISTANT", Status = ChatMessageStatus.Completed, CreatedAt = sharedUserTimestamp.AddTicks(1), Sequence = 4 });
            await db.SaveChangesAsync();
        }

        using var regeneration = await SendWithCsrf(client, HttpMethod.Post,
            $"/api/conversations/{conversation.Id}/messages/{secondAssistantId}/regenerate",
            new { requestId = Guid.NewGuid().ToString("N") });
        Assert.Contains("message.completed", await regeneration.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await SendMessage(client, conversation.Id, "SEQUENCE_LATER_USER");
        var prompt = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>().LastRequest!.Messages.ToArray();
        Assert.DoesNotContain(prompt, message => message.Content == "SEQUENCE_SECOND_SUPERSEDED_ASSISTANT");
        Assert.Contains(prompt, message => message.Content == StreamQaRecordingChatCompletionService.ReplacementContent);
        Assert.Contains(prompt, message => message.Content == "SEQUENCE_FIRST_ASSISTANT");
        Assert.Equal(2, prompt.Count(message => message.Role == "assistant"));
    }

    [Fact]
    public async Task Long_unambiguous_regeneration_chain_compacts_to_one_assistant_answer()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Long Regeneration Chain Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        const int supersededAnswerCount = 2048;
        var firstCreatedAt = new DateTime(2026, 11, 26, 18, 0, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var entity = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            entity.NextMessageSequence = supersededAnswerCount + 1;
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = ChatMessageRole.User,
                Content = "LONG_CHAIN_SOURCE_USER",
                Status = ChatMessageStatus.Completed,
                CreatedAt = firstCreatedAt,
                Sequence = 1,
                RequestId = Guid.NewGuid().ToString("N"),
            });
            db.ChatMessages.AddRange(Enumerable.Range(0, supersededAnswerCount).Select(index => new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = ChatMessageRole.Assistant,
                Content = $"LONG_CHAIN_ANSWER_{index:D4}",
                Status = ChatMessageStatus.Completed,
                CreatedAt = firstCreatedAt.AddTicks(index + 1),
                Sequence = index + 2,
            }));
            await db.SaveChangesAsync();
        }

        await SendMessage(client, conversation.Id, "LONG_CHAIN_CURRENT_USER");
        var prompt = factory.Services.GetRequiredService<StreamQaRecordingChatCompletionService>().LastRequest!.Messages.ToArray();
        Assert.Contains(prompt, message => message.Content == "LONG_CHAIN_ANSWER_2047");
        Assert.DoesNotContain(prompt, message => message.Content == "LONG_CHAIN_ANSWER_0000");
        Assert.Equal(1, prompt.Count(message => message.Role == "assistant"));
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"streamqa-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { projectId = (Guid?)null });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static Task<SendMessageResponse> SendMessage(HttpClient client, Guid conversationId, string content) =>
        SendWithCsrf<SendMessageResponse>(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages", new
        {
            content,
            requestId = Guid.NewGuid().ToString("N"),
        });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
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

    private static JsonDocument ExtractEventData(string body, string eventName)
    {
        var block = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .First(item => item.StartsWith($"event: {eventName}\n", StringComparison.Ordinal));
        var dataLine = block.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(dataLine[6..]);
    }
}

/// <summary>
/// Isolated synthetic fixture: its own in-memory SQLite database and recording fake provider, so
/// it shares no service state with the other chat test classes while they run in parallel. No
/// network, no real provider, no attachment storage.
/// </summary>
public sealed class StreamQaRecordingFactory : WebApplicationFactory<Program>
{
    private readonly string connectionString = $"Data Source=streamqa-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection anchorConnection;

    public StreamQaRecordingFactory()
    {
        anchorConnection = new SqliteConnection(connectionString);
        anchorConnection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(anchorConnection);
            services.AddSingleton<StreamQaInterleavingGate>();
            services.AddSingleton<StreamQaSaveChangesGate>();
            services.AddDbContext<TaslimDbContext>((serviceProvider, options) =>
                options.UseSqlite(connectionString).AddInterceptors(
                    serviceProvider.GetRequiredService<StreamQaInterleavingGate>(),
                    serviceProvider.GetRequiredService<StreamQaSaveChangesGate>()));
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<StreamQaRecordingChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(services => services.GetRequiredService<StreamQaRecordingChatCompletionService>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) anchorConnection.Dispose();
    }
}

/// <summary>Pauses one matching SQLite command so the tests control the exact write/read interleave.</summary>
public sealed class StreamQaInterleavingGate : DbCommandInterceptor
{
    private TaskCompletionSource? reached;
    private TaskCompletionSource? release;
    private int armed;
    private int gateSequenceReservation;
    private int gateContextRead;
    private int gateSecondRequestIdLookup;
    private int requestIdLookupOccurrence;
    private string? gatedRequestId;

    public void ArmSequenceReservation() => Arm(ref gateSequenceReservation);
    public void ArmContextRead() => Arm(ref gateContextRead);
    public void ArmSecondRequestIdLookup(string requestId)
    {
        gatedRequestId = requestId;
        Volatile.Write(ref requestIdLookupOccurrence, 0);
        Arm(ref gateSecondRequestIdLookup);
    }

    public Task WaitForSequenceReservationAsync(TimeSpan timeout) => WaitForGateAsync(gateSequenceReservation, timeout);
    public Task WaitForContextReadAsync(TimeSpan timeout) => WaitForGateAsync(gateContextRead, timeout);
    public Task WaitForSecondRequestIdLookupAsync(TimeSpan timeout) => WaitForGateAsync(gateSecondRequestIdLookup, timeout);
    public void ReleaseSequenceReservation() => ReleaseGate(gateSequenceReservation);
    public void ReleaseContextRead() => ReleaseGate(gateContextRead);
    public void ReleaseSecondRequestIdLookup() => ReleaseGate(gateSecondRequestIdLookup);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default) =>
        PauseIfArmedAsync(command, result, cancellationToken);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) =>
        PauseIfArmedAsync(command, result, cancellationToken);

    private void Arm(ref int kind)
    {
        reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref gateSequenceReservation, 0);
        Volatile.Write(ref gateContextRead, 0);
        Volatile.Write(ref gateSecondRequestIdLookup, 0);
        Volatile.Write(ref kind, 1);
        Volatile.Write(ref armed, 1);
    }

    private Task WaitForGateAsync(int kind, TimeSpan timeout)
    {
        var signal = Volatile.Read(ref kind) == 1 ? reached : null;
        if (signal is null) throw new InvalidOperationException("The requested SQLite interleaving gate was not armed.");
        return signal.Task.WaitAsync(timeout);
    }

    private void ReleaseGate(int kind)
    {
        if (Volatile.Read(ref kind) == 1) release?.TrySetResult();
    }

    private async ValueTask<T> PauseIfArmedAsync<T>(DbCommand command, T result, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref armed) != 1) return result;
        var sql = command.CommandText;
        var isSequenceReservation = Volatile.Read(ref gateSequenceReservation) == 1
            && sql.Contains("UPDATE \"Conversations\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"NextMessageSequence\"", StringComparison.OrdinalIgnoreCase);
        var isContextRead = Volatile.Read(ref gateContextRead) == 1
            && sql.Contains("FROM \"ChatMessages\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"Sequence\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"CreatedAt\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"Content\"", StringComparison.OrdinalIgnoreCase)
            && !sql.Contains("DESC", StringComparison.OrdinalIgnoreCase);
        var isTargetRequestIdLookup = Volatile.Read(ref gateSecondRequestIdLookup) == 1
            && sql.Contains("FROM \"ChatMessages\"", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("\"RequestId\"", StringComparison.OrdinalIgnoreCase)
            && command.Parameters.Cast<DbParameter>().Any(parameter =>
                parameter.Value is string value && string.Equals(value, gatedRequestId, StringComparison.Ordinal));
        if (isTargetRequestIdLookup && Interlocked.Increment(ref requestIdLookupOccurrence) == 1) return result;
        if ((!isSequenceReservation && !isContextRead && !isTargetRequestIdLookup) || Interlocked.Exchange(ref armed, 0) != 1) return result;

        reached?.TrySetResult();
        if (release is not null) await release.Task.WaitAsync(cancellationToken);
        return result;
    }
}

/// <summary>Pauses matching regeneration claims before EF opens its SaveChanges transaction.</summary>
public sealed class StreamQaSaveChangesGate : SaveChangesInterceptor
{
    private string? requestId;
    private TaskCompletionSource? firstReached;
    private TaskCompletionSource? secondReached;
    private TaskCompletionSource? firstRelease;
    private TaskCompletionSource? secondRelease;
    private int armed;
    private int arrivalCount;

    public void ArmRequestIdInsertBarrier(string requestId)
    {
        this.requestId = requestId;
        firstReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        secondReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref arrivalCount, 0);
        Volatile.Write(ref armed, 1);
    }

    public Task WaitForFirstRequestIdInsertAsync(TimeSpan timeout) => (firstReached?.Task ?? throw new InvalidOperationException("The request-ID SaveChanges barrier was not armed.")).WaitAsync(timeout);
    public Task WaitForSecondRequestIdInsertAsync(TimeSpan timeout) => (secondReached?.Task ?? throw new InvalidOperationException("The request-ID SaveChanges barrier was not armed.")).WaitAsync(timeout);
    public void ReleaseFirstRequestIdInsert() => firstRelease?.TrySetResult();
    public void ReleaseSecondRequestIdInsert() => secondRelease?.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var matchingClaim = Volatile.Read(ref armed) == 1 && requestId is not null &&
            eventData.Context?.ChangeTracker.Entries<ChatMessage>().Any(entry =>
                entry.State == EntityState.Added &&
                entry.Entity.Role == ChatMessageRole.Assistant &&
                string.Equals(entry.Entity.RequestId, requestId, StringComparison.Ordinal)) == true;
        if (!matchingClaim) return result;

        var arrival = Interlocked.Increment(ref arrivalCount);
        if (arrival == 1)
        {
            firstReached?.TrySetResult();
            if (firstRelease is not null) await firstRelease.Task.WaitAsync(cancellationToken);
        }
        else if (arrival == 2)
        {
            Volatile.Write(ref armed, 0);
            secondReached?.TrySetResult();
            if (secondRelease is not null) await secondRelease.Task.WaitAsync(cancellationToken);
        }
        return result;
    }
}

/// <summary>
/// Fake provider: answers with the constant transcript content the negative control pins on, and
/// records the exact prompt of the most recent model call with its call count.
/// </summary>
public sealed class StreamQaRecordingChatCompletionService : IChatCompletionService
{
    public const string ReplacementContent = "STREAMQA_LIVE_REPLACEMENT";
    public const string OverlappingSendUser = "STREAMQA_OVERLAPPING_SEND_USER";
    public const string OverlappingSendAnswer = "STREAMQA_OVERLAPPING_SEND_ANSWER";

    public AiChatRequest? LastRequest { get; private set; }
    public int CallCount { get; private set; }
    public bool FailNextStream { get; set; }
    private TaskCompletionSource? streamStarted;
    private TaskCompletionSource? streamRelease;

    public void ArmStreamBarrier()
    {
        streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        streamRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Task WaitForStreamStartAsync(TimeSpan timeout) => (streamStarted?.Task ?? throw new InvalidOperationException("The fake stream barrier was not armed.")).WaitAsync(timeout);
    public void ReleaseStream() => streamRelease?.TrySetResult();

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        streamStarted?.TrySetResult();
        if (streamRelease is not null) await streamRelease.Task.WaitAsync(cancellationToken);
        await Task.Yield();
        if (FailNextStream)
        {
            FailNextStream = false;
            throw new InvalidOperationException("Synthetic stream failure for regeneration retry coverage.");
        }
        yield return new AiMessageDelta(string.Equals(request.Messages.LastOrDefault()?.Content, OverlappingSendUser, StringComparison.Ordinal)
            ? OverlappingSendAnswer
            : ReplacementContent);
        yield return new AiMessageCompleted(new AiUsageMetadata("mock", "taslim-stream-qa", null, null, null, 0m, 0m, 0, "test-complete", true));
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        var content = string.Equals(request.Messages.LastOrDefault()?.Content, OverlappingSendUser, StringComparison.Ordinal)
            ? OverlappingSendAnswer
            : ReplacementContent;
        return Task.FromResult(new AiGenerationResult(content, new AiUsageMetadata("mock", "taslim-stream-qa", null, null, null, 0m, 0m, 0, "test-complete", true)));
    }
}

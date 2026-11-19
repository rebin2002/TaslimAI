using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// one sends a message over the stream route, the assistant answer is superseded through the real
/// regeneration endpoint, and request two — a second stream send — must present a model prompt
/// that carries the live replacement alone, while the persisted transcript keeps every row,
/// unedited, in sequence order. Synthetic fixture with a recording fake provider: no real prompts,
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

        // Request one: an ordinary turn delivered over the stream route.
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
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public StreamQaRecordingFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<StreamQaRecordingChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(services => services.GetRequiredService<StreamQaRecordingChatCompletionService>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

/// <summary>
/// Fake provider: answers with the constant transcript content the negative control pins on, and
/// records the exact prompt of the most recent model call with its call count.
/// </summary>
public sealed class StreamQaRecordingChatCompletionService : IChatCompletionService
{
    public const string ReplacementContent = "STREAMQA_LIVE_REPLACEMENT";

    public AiChatRequest? LastRequest { get; private set; }
    public int CallCount { get; private set; }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        await Task.Yield();
        yield return new AiMessageDelta(ReplacementContent);
        yield return new AiMessageCompleted(new AiUsageMetadata("mock", "taslim-stream-qa", null, null, null, 0m, 0m, 0, "test-complete", true));
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(new AiGenerationResult(ReplacementContent, new AiUsageMetadata("mock", "taslim-stream-qa", null, null, null, 0m, 0m, 0, "test-complete", true)));
    }
}

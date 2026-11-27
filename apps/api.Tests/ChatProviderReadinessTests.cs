using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Release-launch configuration for chat: the real provider is not activated and the
/// development mock is refused, so no chat generation can be served at all.
/// </summary>
public sealed class ChatProviderUnavailableApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:OpenAI:Enabled", "false");
        builder.UseSetting("Ai:OpenAI:ApiKey", string.Empty);
        builder.UseSetting("Ai:AllowMockProvider", "false");
    }
}

/// <summary>
/// Misconfigured launch: OpenAI is switched on but its credential never arrived. The
/// provider itself refuses every call, so readiness must be judged the same way.
/// </summary>
public sealed class ChatProviderMissingCredentialApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:OpenAI:Enabled", "true");
        builder.UseSetting("Ai:OpenAI:ApiKey", string.Empty);
        builder.UseSetting("Ai:AllowMockProvider", "false");
    }
}

/// <summary>
/// Chat admission must refuse a generation that cannot be served before it persists chat
/// history or opens a usage reservation, the way Image Studio already refuses with
/// <c>IMAGE_PROVIDER_UNAVAILABLE</c>. These facts never contact a real provider and never
/// charge anyone.
/// </summary>
public sealed class ChatProviderReadinessTests : IClassFixture<ChatProviderUnavailableApiFactory>, IClassFixture<ChatProviderMissingCredentialApiFactory>
{
    private const string UnavailableCode = "AI_PROVIDER_UNAVAILABLE";

    private readonly ChatProviderUnavailableApiFactory unavailableFactory;
    private readonly ChatProviderMissingCredentialApiFactory missingCredentialFactory;

    public ChatProviderReadinessTests(ChatProviderUnavailableApiFactory unavailableFactory, ChatProviderMissingCredentialApiFactory missingCredentialFactory)
    {
        this.unavailableFactory = unavailableFactory;
        this.missingCredentialFactory = missingCredentialFactory;
        foreach (var factory in new TaslimApiFactory[] { unavailableFactory, missingCredentialFactory })
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
        }
    }

    [Fact]
    public async Task Nonstreaming_message_is_refused_before_messages_or_reservations_are_persisted()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Readiness Gate Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Hello Taslim",
            requestId = Guid.NewGuid().ToString("N"),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(UnavailableCode, body.GetProperty("error").GetProperty("code").GetString());

        var persisted = await PersistedState(unavailableFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(0, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
        Assert.Equal(0m, persisted.ChargedAmount);
    }

    [Fact]
    public async Task Streamed_message_emits_only_a_provider_failure_event_before_any_persistence()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Streaming Readiness Gate Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/stream", new
        {
            content = "Hello Taslim",
            requestId = Guid.NewGuid().ToString("N"),
        });

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message.failed", text);
        Assert.DoesNotContain("event: message.started", text);
        Assert.DoesNotContain("event: message.delta", text);
        Assert.DoesNotContain("event: message.completed", text);
        var failure = ExtractEventData(text, "message.failed");
        Assert.Equal(UnavailableCode, failure.RootElement.GetProperty("code").GetString());

        var persisted = await PersistedState(unavailableFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(0, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
        Assert.Equal(0m, persisted.ChargedAmount);
    }

    [Fact]
    public async Task Regenerate_is_refused_before_the_assistant_message_is_reset()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Regeneration Readiness Gate Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var assistantId = await SeedCompletedExchangeAsync(conversation);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/{assistantId}/regenerate", new
        {
            requestId = Guid.NewGuid().ToString("N"),
        });

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message.failed", text);
        Assert.DoesNotContain("event: message.started", text);
        var failure = ExtractEventData(text, "message.failed");
        Assert.Equal(UnavailableCode, failure.RootElement.GetProperty("code").GetString());

        using var scope = unavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var assistant = await db.ChatMessages.AsNoTracking().FirstAsync(message => message.Id == assistantId);
        Assert.Equal(ChatMessageStatus.Completed, assistant.Status);
        Assert.NotEqual(string.Empty, assistant.Content);

        var persisted = await PersistedState(unavailableFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(2, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
    }

    [Fact]
    public async Task Missing_openai_credential_is_refused_before_messages_or_reservations_are_persisted()
    {
        using var client = missingCredentialFactory.CreateClient();
        var auth = await Register(client, "Missing Credential Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Hello Taslim",
            requestId = Guid.NewGuid().ToString("N"),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(UnavailableCode, body.GetProperty("error").GetProperty("code").GetString());

        var persisted = await PersistedState(missingCredentialFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(0, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
        Assert.Equal(0m, persisted.ChargedAmount);
    }


    [Fact]
    public async Task Completed_request_replays_on_both_endpoints_even_if_provider_is_unavailable()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Readiness Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var assistantId = await SeedCompletedExchangeAsync(conversation);
        string requestId;
        using (var scope = unavailableFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            requestId = (await db.ChatMessages.AsNoTracking()
                .SingleAsync(message => message.ConversationId == conversation.Id && message.Role == ChatMessageRole.User)).RequestId!;
        }

        var payload = new { content = "Hello Taslim", requestId };
        var normal = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", payload);
        Assert.Equal(HttpStatusCode.OK, normal.StatusCode);
        var replay = await normal.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.Equal(assistantId, replay!.AssistantMessage.Id);

        var streamed = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/stream", payload);
        var events = await streamed.Content.ReadAsStringAsync();
        Assert.Contains("event: message.completed", events);
        Assert.DoesNotContain("event: message.failed", events);

        var persisted = await PersistedState(unavailableFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(2, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
        Assert.Equal(0m, persisted.ChargedAmount);
    }

    [Fact]
    public async Task Missing_conversation_is_reported_before_provider_readiness_on_both_endpoints()
    {
        using var client = unavailableFactory.CreateClient();
        await Register(client, "Readiness Missing Conversation Owner");
        var missingConversationId = Guid.NewGuid();
        var payload = new { content = "Hello Taslim", requestId = Guid.NewGuid().ToString("N") };

        var normal = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{missingConversationId}/messages", payload);
        Assert.Equal(HttpStatusCode.NotFound, normal.StatusCode);
        var body = await normal.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("CONVERSATION_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());

        var streamed = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{missingConversationId}/messages/stream", payload);
        var events = await streamed.Content.ReadAsStringAsync();
        var failed = ExtractEventData(events, "message.failed");
        Assert.Equal("CONVERSATION_NOT_FOUND", failed.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("event: message.started", events);
    }

    [Fact]
    public async Task Completed_regeneration_request_replays_without_a_ready_provider()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Regeneration Replay Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var originalAssistantId = await SeedCompletedExchangeAsync(conversation);
        var requestId = Guid.NewGuid().ToString("N");
        using (var scope = unavailableFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var tracked = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
            tracked.NextMessageSequence = 3;
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                RequestId = requestId,
                Role = ChatMessageRole.Assistant,
                Content = "Completed regeneration",
                Status = ChatMessageStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                Sequence = 3,
            });
            await db.SaveChangesAsync();
        }

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/{originalAssistantId}/regenerate", new { requestId });
        var events = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message.completed", events);
        Assert.DoesNotContain("event: message.failed", events);
        var persisted = await PersistedState(unavailableFactory, auth.PersonalWorkspace.Id, conversation.Id);
        Assert.Equal(3, persisted.MessageCount);
        Assert.Equal(0, persisted.ReservationCount);
    }

    [Fact]
    public async Task Conversation_creation_still_works_while_no_provider_is_ready()
    {
        using var client = unavailableFactory.CreateClient();
        var auth = await Register(client, "Conversation Creation Owner");

        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations", new { });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public void Router_refuses_openai_selection_when_the_credential_is_missing()
    {
        var options = Options.Create(new AiOptions { DefaultChatTier = "Smart", AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = string.Empty } });
        var router = new AiModelRouter(options, new AiModelCatalog(new ConfigurationBuilder().Build()));

        Assert.Throws<AiProviderUnavailableException>(() => router.Select(new AiChatRequest([], "system", "Smart")));
    }

    [Fact]
    public void Router_still_selects_openai_when_the_credential_is_present()
    {
        var options = Options.Create(new AiOptions { DefaultChatTier = "Smart", AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } });
        var router = new AiModelRouter(options, new AiModelCatalog(new ConfigurationBuilder().Build()));

        var selection = router.Select(new AiChatRequest([], "system", "Smart"));

        Assert.Equal("openai", selection.ProviderKey);
    }

    [Fact]
    public void Readiness_reports_false_only_when_no_chat_provider_can_be_selected()
    {
        var catalog = new AiModelCatalog(new ConfigurationBuilder().Build());
        var refused = new AiModelRouter(Options.Create(new AiOptions { AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = false } }), catalog);
        var mockReady = new AiModelRouter(Options.Create(new AiOptions { AllowMockProvider = true, OpenAI = new OpenAiOptions { Enabled = false } }), catalog);
        var openAiReady = new AiModelRouter(Options.Create(new AiOptions { AllowMockProvider = false, OpenAI = new OpenAiOptions { Enabled = true, ApiKey = "test-only" } }), catalog);

        Assert.False(refused.IsChatGenerationAvailable());
        Assert.True(mockReady.IsChatGenerationAvailable());
        Assert.True(openAiReady.IsChatGenerationAvailable());
    }

    private static async Task<(int MessageCount, int ReservationCount, decimal ChargedAmount)> PersistedState(TaslimApiFactory factory, Guid workspaceId, Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var messageCount = await db.ChatMessages.CountAsync(message => message.ConversationId == conversationId);
        // SQLite cannot translate a decimal Sum, so the rows are materialized first.
        var transactions = await db.UsageTransactions
            .Where(transaction => transaction.WorkspaceId == workspaceId)
            .Select(transaction => new { transaction.Status, transaction.ChargedAmount })
            .ToListAsync();
        return (messageCount, transactions.Count, transactions.Sum(transaction => transaction.ChargedAmount));
    }

    private async Task<Guid> SeedCompletedExchangeAsync(ConversationDto conversation)
    {
        using var scope = unavailableFactory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var tracked = await db.Conversations.FirstAsync(item => item.Id == conversation.Id);
        var now = DateTime.UtcNow;
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = tracked.Id,
            RequestId = Guid.NewGuid().ToString("N"),
            Role = ChatMessageRole.User,
            Content = "Hello Taslim",
            Status = ChatMessageStatus.Completed,
            CreatedAt = now,
            Sequence = 1,
        };
        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = tracked.Id,
            Role = ChatMessageRole.Assistant,
            Content = "Seeded completed response",
            Status = ChatMessageStatus.Completed,
            CreatedAt = now.AddSeconds(1),
            Sequence = 2,
        };
        tracked.NextMessageSequence = 2;
        db.ChatMessages.AddRange(userMessage, assistantMessage);
        await db.SaveChangesAsync();
        return assistantMessage.Id;
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-readiness-{Guid.NewGuid():N}@example.com",
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

using System.Net;
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
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ChatMidstreamRevocationNegativeTests : IClassFixture<ChatRevocationAcceptanceFactory>
{
    private const string Password = "StrongPassword!123";
    private readonly ChatRevocationAcceptanceFactory factory;

    public ChatMidstreamRevocationNegativeTests(ChatRevocationAcceptanceFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Session_revocation_during_stream_stops_provider_delta_before_completion()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: true);
        var email = $"chat-revoke-{Guid.NewGuid():N}@example.test";
        using var streamingSession = factory.CreateClient();
        var auth = await Register(streamingSession, email);
        using var revokingSession = factory.CreateClient();
        await Login(revokingSession, email);
        var conversation = await CreateConversation(streamingSession, auth.PersonalWorkspace.Id);

        var streamTask = StartStream(streamingSession, conversation.Id);
        await factory.Provider.FirstDeltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revoke = await SendWithCsrf(revokingSession, HttpMethod.Post, "/api/auth/sessions/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await streamingSession.GetAsync("/api/auth/me")).StatusCode);
        factory.Provider.Release();

        var body = await streamTask;
        var evidence = await ReadEvidence(conversation.Id);
        Assert.DoesNotContain("after-revocation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: message.completed", body, StringComparison.Ordinal);
        Assert.NotEqual(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(0, evidence.CompletedUsage);
    }

    [Fact]
    public async Task Workspace_membership_removal_during_stream_stops_provider_delta_before_completion()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: true);
        var email = $"chat-membership-{Guid.NewGuid():N}@example.test";
        using var streamingSession = factory.CreateClient();
        var auth = await Register(streamingSession, email);
        var conversation = await CreateConversation(streamingSession, auth.PersonalWorkspace.Id);
        var streamTask = StartStream(streamingSession, conversation.Id);
        await factory.Provider.FirstDeltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var membership = await db.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id && item.UserId == auth.User.Id);
            db.WorkspaceMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await streamingSession.GetAsync($"/api/conversations/{conversation.Id}/messages")).StatusCode);
        factory.Provider.Release();

        var body = await streamTask;
        var evidence = await ReadEvidence(conversation.Id);
        Assert.DoesNotContain("after-revocation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: message.completed", body, StringComparison.Ordinal);
        Assert.NotEqual(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(0, evidence.CompletedUsage);
    }

    [Fact]
    public async Task Session_revocation_during_regeneration_stream_stops_provider_delta_before_completion()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: false);
        var email = $"chat-regenerate-revoke-{Guid.NewGuid():N}@example.test";
        using var streamingSession = factory.CreateClient();
        var auth = await Register(streamingSession, email);
        using var revokingSession = factory.CreateClient();
        await Login(revokingSession, email);
        var conversation = await CreateConversation(streamingSession, auth.PersonalWorkspace.Id);
        var initialBody = await StartStream(streamingSession, conversation.Id);
        var assistantId = ExtractEventData(initialBody, "message.completed").RootElement.GetProperty("assistantMessage").GetProperty("id").GetGuid();

        factory.Provider.BeginScenario(pauseAfterFirstDelta: true);
        var requestId = Guid.NewGuid().ToString("N");
        var regenerationTask = StartRegeneration(streamingSession, conversation.Id, assistantId, requestId);
        await factory.Provider.FirstDeltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var revoke = await SendWithCsrf(revokingSession, HttpMethod.Post, "/api/auth/sessions/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await streamingSession.GetAsync("/api/auth/me")).StatusCode);
        factory.Provider.Release();

        var body = await regenerationTask;
        var evidence = await ReadEvidence(conversation.Id, requestId);
        Assert.DoesNotContain("after-revocation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: message.completed", body, StringComparison.Ordinal);
        Assert.NotEqual(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(0, evidence.CompletedUsage);
    }

    [Fact]
    public async Task Workspace_membership_removal_during_regeneration_stream_stops_provider_delta_before_completion()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: false);
        using var streamingSession = factory.CreateClient();
        var auth = await Register(streamingSession, $"chat-regenerate-membership-{Guid.NewGuid():N}@example.test");
        var conversation = await CreateConversation(streamingSession, auth.PersonalWorkspace.Id);
        var initialBody = await StartStream(streamingSession, conversation.Id);
        var assistantId = ExtractEventData(initialBody, "message.completed").RootElement.GetProperty("assistantMessage").GetProperty("id").GetGuid();

        factory.Provider.BeginScenario(pauseAfterFirstDelta: true);
        var requestId = Guid.NewGuid().ToString("N");
        var regenerationTask = StartRegeneration(streamingSession, conversation.Id, assistantId, requestId);
        await factory.Provider.FirstDeltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var membership = await db.WorkspaceMembers.SingleAsync(item => item.WorkspaceId == auth.PersonalWorkspace.Id && item.UserId == auth.User.Id);
            db.WorkspaceMembers.Remove(membership);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NotFound, (await streamingSession.GetAsync($"/api/conversations/{conversation.Id}/messages")).StatusCode);
        factory.Provider.Release();

        var body = await regenerationTask;
        var evidence = await ReadEvidence(conversation.Id, requestId);
        Assert.DoesNotContain("after-revocation", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event: message.completed", body, StringComparison.Ordinal);
        Assert.NotEqual(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(0, evidence.CompletedUsage);
    }

    [Fact]
    public async Task Pre_revocation_regeneration_stream_control_completes_and_finalizes_usage()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: false);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"chat-regenerate-control-{Guid.NewGuid():N}@example.test");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var initialBody = await StartStream(client, conversation.Id);
        var assistantId = ExtractEventData(initialBody, "message.completed").RootElement.GetProperty("assistantMessage").GetProperty("id").GetGuid();
        var requestId = Guid.NewGuid().ToString("N");

        var body = await StartRegeneration(client, conversation.Id, assistantId, requestId);
        var evidence = await ReadEvidence(conversation.Id, requestId);
        Assert.Contains("event: message.delta", body);
        Assert.Contains("event: message.completed", body);
        Assert.Equal(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(1, evidence.CompletedUsage);
    }

    [Fact]
    public async Task Pre_revocation_stream_control_completes_and_finalizes_usage()
    {
        factory.Provider.BeginScenario(pauseAfterFirstDelta: false);
        using var client = factory.CreateClient();
        var auth = await Register(client, $"chat-control-{Guid.NewGuid():N}@example.test");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);

        var body = await StartStream(client, conversation.Id);
        var evidence = await ReadEvidence(conversation.Id);
        Assert.Contains("event: message.delta", body);
        Assert.Contains("event: message.completed", body);
        Assert.Equal(ChatMessageStatus.Completed, evidence.AssistantStatus);
        Assert.Equal(1, evidence.CompletedUsage);
    }

    private async Task<string> StartStream(HttpClient client, Guid conversationId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{conversationId}/messages/stream")
        {
            Content = JsonContent.Create(new { content = "synthetic paused stream", requestId = Guid.NewGuid().ToString("N") })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> StartRegeneration(HttpClient client, Guid conversationId, Guid assistantId, string requestId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages/{assistantId}/regenerate", new { requestId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<(ChatMessageStatus? AssistantStatus, int CompletedUsage)> ReadEvidence(Guid conversationId, string? requestId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var assistant = await db.ChatMessages
            .Where(item => item.ConversationId == conversationId && item.Role == ChatMessageRole.Assistant)
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync();
        var usageQuery = db.UsageTransactions.Where(item => item.ConversationId == conversationId);
        if (requestId is not null) usageQuery = usageQuery.Where(item => item.RequestId == requestId);
        var usage = await usageQuery.ToListAsync();
        return (assistant?.Status, usage.Count(item => item.Status == UsageTransactionStatus.Completed));
    }

    private static JsonDocument ExtractEventData(string body, string eventName)
    {
        var block = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .First(item => item.StartsWith($"event: {eventName}\n", StringComparison.Ordinal));
        var dataLine = block.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal));
        return JsonDocument.Parse(dataLine[6..]);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Synthetic Chat User", email, password = Password, preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task Login(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
}

public sealed class ChatRevocationAcceptanceFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    public PausingChatCompletion Provider { get; } = new();

    public ChatRevocationAcceptanceFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(Provider);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public sealed class PausingChatCompletion : IChatCompletionService
{
    private TaskCompletionSource<bool> firstDeltaObserved = NewSignal();
    private TaskCompletionSource<bool> release = NewSignal();
    private bool pauseAfterFirstDelta;

    public TaskCompletionSource<bool> FirstDeltaObserved => firstDeltaObserved;

    public void BeginScenario(bool pauseAfterFirstDelta)
    {
        this.pauseAfterFirstDelta = pauseAfterFirstDelta;
        firstDeltaObserved = NewSignal();
        release = NewSignal();
        if (!pauseAfterFirstDelta) release.TrySetResult(true);
    }

    public void Release() => release.TrySetResult(true);

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AiMessageDelta("before-revocation");
        firstDeltaObserved.TrySetResult(true);
        if (pauseAfterFirstDelta) await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        yield return new AiMessageDelta("after-revocation");
        yield return new AiMessageCompleted(new AiUsageMetadata("synthetic", "synthetic", null, null, null, 0m, 0m, 0, "synthetic", true));
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

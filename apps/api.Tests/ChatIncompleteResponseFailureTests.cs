using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ChatIncompleteResponseFailureTests : IClassFixture<EmptyIncompleteResponseFactory>
{
    private readonly EmptyIncompleteResponseFactory factory;

    public ChatIncompleteResponseFailureTests(EmptyIncompleteResponseFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Empty_incomplete_stream_fails_without_delivered_success_and_preserves_known_cost_unbilled()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");

        var response = await SendMessage(client, conversation.Id, requestId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: message.started", body);
        Assert.Contains("event: message.failed", body);
        Assert.DoesNotContain("event: message.completed", body);
        Assert.DoesNotContain("private provider detail", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unapproved provider status", body, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Sequence)
            .ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageStatus.Failed, messages[1].Status);
        Assert.Equal(string.Empty, messages[1].Content);
        Assert.Null(messages[1].FinishReason);

        var transaction = await db.UsageTransactions.AsNoTracking().SingleAsync(item => item.RequestId == requestId);
        Assert.Equal(UsageTransactionStatus.Failed, transaction.Status);
        Assert.Equal("PROVIDER_FAILED", transaction.FailureCode);
        Assert.True(transaction.ProviderCostKnown);
        Assert.Equal(0.00026m, transaction.ProviderCostUsd);
        Assert.Equal(10, transaction.InputTokens);
        Assert.Equal(20, transaction.OutputTokens);
        Assert.False(transaction.IsBillable);
        Assert.Null(transaction.BillableAt);
        Assert.Equal(0m, transaction.ChargedAmount);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = "Incomplete Response Owner",
            email = $"empty-incomplete-{Guid.NewGuid():N}@example.com",
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

    private static Task<HttpResponseMessage> SendMessage(HttpClient client, Guid conversationId, string requestId) =>
        SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages/stream", new { content = "Please answer", requestId });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class EmptyIncompleteResponseFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public EmptyIncompleteResponseFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Models:gpt-5.6-terra:ProviderKey"] = "openai",
            ["Ai:Models:gpt-5.6-terra:InputPricePerMillion"] = "2",
            ["Ai:Models:gpt-5.6-terra:CachedInputPricePerMillion"] = "0.2",
            ["Ai:Models:gpt-5.6-terra:OutputPricePerMillion"] = "12",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<IChatCompletionService, EmptyIncompleteResponseCompletionService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public sealed class EmptyIncompleteResponseCompletionService : IChatCompletionService
{
    private static readonly AiUsageMetadata Usage = new("openai", "gpt-5.6-terra", 10, null, 20, null, null, 12, "incomplete", false);

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, CancellationToken cancellationToken = default) => StreamFailure();

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<AiGenerationResult>(CreateFailure());

    private static async IAsyncEnumerable<AiStreamEvent> StreamFailure()
    {
        await Task.Yield();
        yield return new AiMessageDelta(string.Empty);
        throw CreateFailure();
    }

    private static AiProviderException CreateFailure() => new(
        failureCategory: AiProviderFailureCategories.MalformedResponse,
        modelKey: Usage.ModelKey,
        structuredOutputRequested: false,
        streamingRequested: true,
        usage: Usage);
}

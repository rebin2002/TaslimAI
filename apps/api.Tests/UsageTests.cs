using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class UsageTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public UsageTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Successful_normal_chat_creates_one_completed_zero_charge_transaction()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Normal Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        await SendMessage(client, conversation.Id, "Normal usage", Guid.NewGuid().ToString("N"));

        var summary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");
        Assert.NotNull(summary);
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.CompletedRequests);
        Assert.Equal(0, summary.FailedRequests);
        Assert.Equal(0m, summary.CustomerChargedAmount);
    }

    [Fact]
    public async Task Successful_streaming_chat_creates_one_completed_transaction()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Stream Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var response = await SendMessage(client, conversation.Id, "Streaming usage", Guid.NewGuid().ToString("N"), stream: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: message.completed", await response.Content.ReadAsStringAsync());

        var history = await client.GetFromJsonAsync<UsageHistoryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage?page=1&pageSize=10");
        Assert.NotNull(history);
        Assert.Single(history.Items);
        Assert.Equal("Chat", history.Items[0].Feature);
        Assert.Equal("Completed", history.Items[0].Status);
    }

    [Fact]
    public async Task Same_request_id_does_not_create_or_charge_a_second_transaction()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Idempotency Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var requestId = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.OK, (await SendMessage(client, conversation.Id, "Repeat usage", requestId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendMessage(client, conversation.Id, "Repeat usage", requestId)).StatusCode);

        var history = await client.GetFromJsonAsync<UsageHistoryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage?page=1&pageSize=10");
        Assert.NotNull(history);
        Assert.Single(history.Items);
        Assert.Equal(0m, history.Items[0].ChargedAmount);
    }

    [Fact]
    public async Task Failed_generation_is_recorded_without_customer_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Failure Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var response = await SendMessage(client, conversation.Id, "[[mock-failure]]", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var summary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");
        Assert.NotNull(summary);
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(0, summary.CompletedRequests);
        Assert.Equal(1, summary.FailedRequests);
        Assert.Equal(0m, summary.CustomerChargedAmount);
    }

    [Fact]
    public async Task Usage_history_is_paginated_and_other_workspaces_are_forbidden()
    {
        using var owner = factory.CreateClient();
        var auth = await Register(owner, "Usage Pagination Owner");
        var conversation = await CreateConversation(owner, auth.PersonalWorkspace.Id);
        for (var index = 0; index < 3; index++)
            await SendMessage(owner, conversation.Id, $"Page usage {index}", Guid.NewGuid().ToString("N"));

        var page = await owner.GetFromJsonAsync<UsageHistoryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage?page=2&pageSize=2");
        Assert.NotNull(page);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Single(page.Items);

        using var other = factory.CreateClient();
        await Register(other, "Usage Other Owner");
        var forbidden = await other.GetAsync($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Usage_history_bounds_deep_page_values_without_offset_overflow()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Pagination Boundary");

        var response = await client.GetAsync($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage?page={int.MaxValue}&pageSize={int.MaxValue}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = (await response.Content.ReadFromJsonAsync<UsageHistoryDto>())!;
        Assert.Equal(Taslim.Api.Infrastructure.ApiPagination.MaxPage, history.Page);
        Assert.Equal(Taslim.Api.Infrastructure.ApiPagination.MaxPageSize, history.PageSize);
        Assert.Empty(history.Items);
    }

    [Fact]
    public async Task Usage_summary_aggregates_statuses_tokens_and_net_amount_without_loading_rows()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Usage Summary Aggregate");
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.UsageTransactions.AddRange(
                CreateTransaction(auth, UsageTransactionStatus.Completed, 10, 2, 4, 1.25m, 0.25m, now),
                CreateTransaction(auth, UsageTransactionStatus.Failed, null, 3, 1, 0m, 0m, now.AddSeconds(1)),
                CreateTransaction(auth, UsageTransactionStatus.Cancelled, 7, null, null, 0m, 0m, now.AddSeconds(2)),
                CreateTransaction(auth, UsageTransactionStatus.Refunded, 1, 1, 2, 4m, 1m, now.AddSeconds(3)));
            await db.SaveChangesAsync();
        }

        var summary = await client.GetFromJsonAsync<UsageSummaryDto>($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");

        Assert.NotNull(summary);
        Assert.Equal(4, summary.TotalRequests);
        Assert.Equal(1, summary.CompletedRequests);
        Assert.Equal(1, summary.FailedRequests);
        Assert.Equal(1, summary.CancelledRequests);
        Assert.Equal(1, summary.RefundedRequests);
        Assert.Equal(18, summary.InputTokens);
        Assert.Equal(6, summary.CachedInputTokens);
        Assert.Equal(7, summary.OutputTokens);
        Assert.Equal(4m, summary.CustomerChargedAmount);
    }

    private static UsageTransaction CreateTransaction(
        AuthResponse auth,
        UsageTransactionStatus status,
        int? inputTokens,
        int? cachedInputTokens,
        int? outputTokens,
        decimal chargedAmount,
        decimal reversedAmount,
        DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = auth.PersonalWorkspace.Id,
            UserId = auth.User.Id,
            RequestId = $"summary-{Guid.NewGuid():N}",
            Feature = UsageFeature.Chat,
            Provider = "test",
            Model = "test",
            Status = status,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            OutputTokens = outputTokens,
            ChargedAmount = chargedAmount,
            ReversedAmount = reversedAmount,
            CreatedAt = createdAt,
        };

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"usage-{Guid.NewGuid():N}@example.com",
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

    private static async Task<HttpResponseMessage> SendMessage(HttpClient client, Guid conversationId, string content, string requestId, bool stream = false) =>
        await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages{(stream ? "/stream" : string.Empty)}", new { content, requestId });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

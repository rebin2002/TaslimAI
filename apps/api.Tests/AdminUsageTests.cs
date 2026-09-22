using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Taslim.Api.Usage;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class AdminUsageTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public AdminUsageTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Admin_report_is_available_to_role_members_and_contains_internal_cost_fields()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Accounting Administrator");
        await AddAdminRole(auth.User.Email!);
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id);
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new { content = "Accounting report", requestId = Guid.NewGuid().ToString("N") });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        var reportResponse = await client.GetAsync("/api/admin/usage/report?page=1&pageSize=20");
        Assert.True(reportResponse.IsSuccessStatusCode, await reportResponse.Content.ReadAsStringAsync());
        var report = await reportResponse.Content.ReadFromJsonAsync<AdminUsageReportDto>();
        Assert.NotNull(report);
        Assert.True(report.Summary.TransactionCount >= 1);
        Assert.Contains(report.Breakdowns.ByFeature, item => item.Feature == "Chat");
        Assert.NotEmpty(report.Transactions.Items);
        Assert.Equal("mock", report.Transactions.Items[0].Provider);
        Assert.Equal("USD", report.Summary.Currency);
    }

    [Fact]
    public async Task Normal_usage_response_does_not_expose_provider_cost()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Accounting Customer");
        var summaryResponse = await client.GetAsync($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage/summary");
        var summaryJson = JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain(summaryJson.RootElement.EnumerateObject(), property => property.NameEquals("providerCostUsd"));

        var historyResponse = await client.GetAsync($"/api/workspaces/{auth.PersonalWorkspace.Id}/usage?page=1&pageSize=10");
        var historyJson = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain(historyJson.RootElement.GetProperty("items").EnumerateArray(), item => item.TryGetProperty("providerCostUsd", out _));
    }

    [Fact]
    public async Task Non_admin_and_unauthenticated_users_cannot_read_admin_usage()
    {
        using var customer = factory.CreateClient();
        await Register(customer, "Accounting Customer Without Role");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/admin/usage/summary")).StatusCode);

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/usage/summary")).StatusCode);
    }

    [Fact]
    public async Task Cancelled_usage_has_explicit_cancelled_state_and_zero_charge()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Accounting Cancellation");
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IUsageLedgerService>();
        var transaction = await ledger.GetOrCreatePendingAsync(auth.PersonalWorkspace.Id, auth.User.Id, null, null, $"cancel-{Guid.NewGuid():N}", UsageFeature.Image);
        await ledger.CancelAsync(transaction, "IMAGE_CANCELLED");

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var saved = await db.UsageTransactions.SingleAsync(item => item.Id == transaction.Id);
        Assert.Equal(UsageTransactionStatus.Cancelled, saved.Status);
        Assert.Equal(0m, saved.ProviderCostUsd);
        Assert.Equal(0m, saved.ChargedAmount);
        Assert.Equal("IMAGE_CANCELLED", saved.FailureCode);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"admin-usage-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private async Task AddAdminRole(string email)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (!await roleManager.RoleExistsAsync(AdminPolicies.Role))
            Assert.True((await roleManager.CreateAsync(new IdentityRole<Guid>(AdminPolicies.Role))).Succeeded);
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.True((await userManager.AddToRoleAsync(user!, AdminPolicies.Role)).Succeeded);
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
}

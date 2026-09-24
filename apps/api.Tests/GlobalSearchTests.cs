using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GlobalSearchTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public GlobalSearchTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Search_groups_authorized_projects_conversations_and_generation_activity()
    {
        using var client = factory.CreateClient();
        var authResponse = await Register(client, "Search Owner", $"search-owner-{Guid.NewGuid():N}@example.com");
        var auth = await authResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);

        var project = await SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth!.PersonalWorkspace.Id}/projects", new
        {
            name = "Aurora launch plan",
            description = "A private launch workspace",
            type = "Marketing",
        });
        await SendWithCsrf<ConversationDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/conversations", new
        {
            projectId = project.Id,
            title = "Aurora launch discussion",
        });
        await SendWithCsrf<GenerationJobDto>(client, HttpMethod.Post, "/api/generation/jobs", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            projectId = project.Id,
            jobType = "system.test",
            inputJson = "{\"brief\":\"Aurora launch\"}",
            title = "Aurora launch generation",
        });

        var response = await client.GetAsync("/api/search?q=Aurora&limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Aurora", body!.Query);
        Assert.Contains(body.Groups, group => group.Type == GlobalSearchResultTypes.Project && group.Items.Any(item => item.Title == "Aurora launch plan"));
        Assert.Contains(body.Groups, group => group.Type == GlobalSearchResultTypes.Conversation && group.Items.Any(item => item.Title == "Aurora launch discussion"));
        Assert.Contains(body.Groups, group => group.Type == GlobalSearchResultTypes.Generation && group.Items.Any(item => item.Title == "Aurora launch generation"));
    }

    [Fact]
    public async Task Search_does_not_return_another_workspace_or_users_private_conversation()
    {
        using var owner = factory.CreateClient();
        var ownerResponse = await Register(owner, "Private Search Owner", $"private-search-owner-{Guid.NewGuid():N}@example.com");
        var ownerAuth = await ownerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(ownerAuth);
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth!.PersonalWorkspace.Id}/projects", new { name = "Private Nebula Project" });
        await SendWithCsrf<ConversationDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations", new { projectId = project.Id, title = "Private Nebula Conversation" });

        using var member = factory.CreateClient();
        var memberResponse = await Register(member, "Workspace Member", $"workspace-member-{Guid.NewGuid():N}@example.com");
        var memberAuth = await memberResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(memberAuth);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth!.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var sharedWorkspaceSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=Nebula");
        Assert.NotNull(sharedWorkspaceSearch);
        Assert.DoesNotContain(sharedWorkspaceSearch!.Groups.SelectMany(group => group.Items), item => item.Type == GlobalSearchResultTypes.Conversation);
        Assert.Contains(sharedWorkspaceSearch.Groups.SelectMany(group => group.Items), item => item.Type == GlobalSearchResultTypes.Project && item.Title == "Private Nebula Project");

        using var unrelated = factory.CreateClient();
        await Register(unrelated, "Unrelated Search User", $"unrelated-search-{Guid.NewGuid():N}@example.com");
        var unrelatedSearch = await unrelated.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=Nebula");
        Assert.NotNull(unrelatedSearch);
        Assert.Empty(unrelatedSearch!.Groups);
        Assert.Equal(0, unrelatedSearch.TotalCount);
    }

    [Fact]
    public async Task Search_requires_authentication_and_empty_query_returns_empty_groups()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/search?q=anything")).StatusCode);

        var authResponse = await Register(anonymous, "Empty Search User", $"empty-search-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/search?q=%20%20")).StatusCode);
        var body = await anonymous.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=%20%20");
        Assert.NotNull(body);
        Assert.Empty(body!.Groups);
        Assert.Equal(0, body.TotalCount);
    }

    private static async Task<HttpResponseMessage> Register(HttpClient client, string displayName, string email) =>
        await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email, password = "StrongPassword!123", preferredLanguage = "en" });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

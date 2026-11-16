using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pins the safe-archive contract on the chat admission boundary: archiving a project is a
/// non-destructive state change (owned data survives and stays restorable), but an archived
/// project must not accept newly attached conversations. Workspace membership is still the
/// first gate, and archived state never leaks through the error contract (404, same envelope
/// as a missing project).
/// </summary>
public sealed class ChatArchivedProjectAdmissionTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public ChatArchivedProjectAdmissionTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Persistence.TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Active_project_still_admits_new_conversations()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Active Admission Owner");
        var workspaceId = auth.PersonalWorkspace.Id;
        var project = await CreateProject(client, workspaceId, "Quarterly plan");

        var response = await CreateConversation(client, workspaceId, project.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = await response.Content.ReadFromJsonAsync<ConversationDto>();
        Assert.NotNull(conversation);
        Assert.Equal(project.Id, conversation.ProjectId);
    }

    [Fact]
    public async Task Archived_project_is_rejected_by_new_conversation_admission()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Archived Admission Owner");
        var workspaceId = auth.PersonalWorkspace.Id;
        var project = await CreateProject(client, workspaceId, "Retired campaign");
        await ArchiveProject(client, project.Id);

        var response = await CreateConversation(client, workspaceId, project.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PROJECT_NOT_FOUND", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Archived_project_double_submit_is_rejected_on_every_attempt()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Archived Double Submit Owner");
        var workspaceId = auth.PersonalWorkspace.Id;
        var project = await CreateProject(client, workspaceId, "Shelved export");
        await ArchiveProject(client, project.Id);

        var first = await CreateConversation(client, workspaceId, project.Id);
        var second = await CreateConversation(client, workspaceId, project.Id);

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Archiving_keeps_existing_conversations_and_history_intact()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Archive Non Destructive Owner");
        var workspaceId = auth.PersonalWorkspace.Id;
        var project = await CreateProject(client, workspaceId, "Closed onboarding");
        var conversation = await CreateConversation(client, workspaceId, project.Id);
        var created = (await conversation.Content.ReadFromJsonAsync<ConversationDto>())!;

        var archived = await ArchiveProject(client, project.Id);

        Assert.Equal("Archived", archived.Status);
        Assert.NotNull(archived.ArchivedAt);
        var get = await client.GetAsync($"/api/conversations/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var messages = await client.GetAsync($"/api/conversations/{created.Id}/messages");
        Assert.Equal(HttpStatusCode.OK, messages.StatusCode);
    }

    [Fact]
    public async Task Restored_project_admits_new_conversations_again()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Restore Admission Owner");
        var workspaceId = auth.PersonalWorkspace.Id;
        var project = await CreateProject(client, workspaceId, "Paused research");
        await ArchiveProject(client, project.Id);

        var rejected = await CreateConversation(client, workspaceId, project.Id);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

        var restored = await RestoreProject(client, project.Id);
        Assert.Equal("Active", restored.Status);
        Assert.Null(restored.ArchivedAt);

        var admitted = await CreateConversation(client, workspaceId, project.Id);
        Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
    }

    [Fact]
    public async Task Non_member_cannot_probe_project_admission_across_workspaces()
    {
        using var owner = factory.CreateClient();
        using var outsider = factory.CreateClient();
        var ownerAuth = await Register(owner, "Scoped Project Owner");
        await Register(outsider, "Scoped Project Outsider");
        var project = await CreateProject(owner, ownerAuth.PersonalWorkspace.Id, "Private scoped plan");

        var response = await CreateConversation(outsider, ownerAuth.PersonalWorkspace.Id, project.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"chat-archive-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en"
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId, string name)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<ProjectDto> ArchiveProject(HttpClient client, Guid projectId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/projects/{projectId}/archive", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<ProjectDto> RestoreProject(HttpClient client, Guid projectId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/projects/{projectId}/restore", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<HttpResponseMessage> CreateConversation(HttpClient client, Guid workspaceId, Guid projectId) =>
        await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { projectId });

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        var conversation = await SendWithCsrf<ConversationDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations", new { projectId = project.Id, title = "Private Nebula Conversation" });
        var upload = await Upload(owner, ownerAuth.PersonalWorkspace.Id, "Nebula-private.txt", "Private Nebula source", project.Id, conversation.Id);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

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
        Assert.DoesNotContain(sharedWorkspaceSearch.Groups.SelectMany(group => group.Items), item => item.Type == GlobalSearchResultTypes.File);
        Assert.Contains(sharedWorkspaceSearch.Groups.SelectMany(group => group.Items), item => item.Type == GlobalSearchResultTypes.Project && item.Title == "Private Nebula Project");

        using var unrelated = factory.CreateClient();
        await Register(unrelated, "Unrelated Search User", $"unrelated-search-{Guid.NewGuid():N}@example.com");
        var unrelatedSearch = await unrelated.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=Nebula");
        Assert.NotNull(unrelatedSearch);
        Assert.Empty(unrelatedSearch!.Groups);
        Assert.Equal(0, unrelatedSearch.TotalCount);
    }

    [Fact]
    public async Task Search_does_not_return_private_assets_to_workspace_members()
    {
        using var owner = factory.CreateClient();
        var ownerResponse = await Register(owner, "Private Asset Search Owner", $"private-asset-owner-{Guid.NewGuid():N}@example.com");
        var ownerAuth = await ownerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(ownerAuth);

        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth!.PersonalWorkspace.Id}/projects", new { name = "Private asset search project" });
        var conversation = await SendWithCsrf<ConversationDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations", new { projectId = project.Id, title = "Private asset search conversation" });

        using var member = factory.CreateClient();
        var memberResponse = await Register(member, "Private Asset Search Member", $"private-asset-member-{Guid.NewGuid():N}@example.com");
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

            var now = DateTime.UtcNow;
            var personalFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, projectId: null, conversationId: null);
            var conversationFile = NewReadyFile(ownerAuth.PersonalWorkspace.Id, ownerAuth.User.Id, now, project.Id, conversation.Id);
            db.StoredFiles.AddRange(personalFile, conversationFile);
            db.Assets.AddRange(
                new Asset
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    StoredFileId = personalFile.Id,
                    Name = "Private personal asset search marker",
                    AssetType = AssetTypes.File,
                    MimeType = personalFile.ContentType,
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new Asset
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                    ProjectId = project.Id,
                    CreatedByUserId = ownerAuth.User.Id,
                    StoredFileId = conversationFile.Id,
                    Name = "Private conversation asset search marker",
                    AssetType = AssetTypes.File,
                    MimeType = conversationFile.ContentType,
                    Status = AssetStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        var ownerSearch = await owner.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=asset%20search%20marker");
        Assert.NotNull(ownerSearch);
        Assert.Equal(2, ownerSearch!.Groups.Where(group => group.Type == GlobalSearchResultTypes.Asset).SelectMany(group => group.Items).Count());

        var memberSearch = await member.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=asset%20search%20marker");
        Assert.NotNull(memberSearch);
        Assert.DoesNotContain(memberSearch!.Groups.SelectMany(group => group.Items), item => item.Type == GlobalSearchResultTypes.Asset);
    }

    [Fact]
    public async Task Search_does_not_project_foreign_workspace_relationship_metadata()
    {
        using var owner = factory.CreateClient();
        var ownerResponse = await Register(owner, "Search Relationship Owner", $"search-relationship-owner-{Guid.NewGuid():N}@example.com");
        var ownerAuth = await ownerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(ownerAuth);

        using var foreign = factory.CreateClient();
        var foreignResponse = await Register(foreign, "Search Relationship Foreign", $"search-relationship-foreign-{Guid.NewGuid():N}@example.com");
        var foreignAuth = await foreignResponse.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(foreignAuth);
        var foreignProject = await SendWithCsrf<ProjectDto>(foreign, HttpMethod.Post, $"/api/workspaces/{foreignAuth!.PersonalWorkspace.Id}/projects", new { name = "Foreign Workspace Project" });

        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var crossWorkspaceFile = NewReadyFile(ownerAuth!.PersonalWorkspace.Id, ownerAuth.User.Id, now, foreignProject.Id, conversationId: null);
            crossWorkspaceFile.OriginalFileName = "Cross workspace file marker.txt";
            crossWorkspaceFile.ExtractedText = "Cross workspace file marker";
            crossWorkspaceFile.ExtractedTextLength = crossWorkspaceFile.ExtractedText.Length;

            db.Conversations.Add(new Conversation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = foreignProject.Id,
                UserId = ownerAuth.User.Id,
                Title = "Cross workspace conversation marker",
                Status = ConversationStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StoredFiles.Add(crossWorkspaceFile);
            db.Assets.Add(new Asset
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = foreignProject.Id,
                CreatedByUserId = ownerAuth.User.Id,
                Name = "Cross workspace asset marker",
                AssetType = AssetTypes.File,
                MimeType = crossWorkspaceFile.ContentType,
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.GenerationJobs.Add(new GenerationJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                ProjectId = foreignProject.Id,
                CreatedByUserId = ownerAuth.User.Id,
                JobType = GenerationJobTypes.SystemTest,
                Status = GenerationJobStatus.Succeeded,
                Title = "Cross workspace generation marker",
                InputJson = "{\"brief\":\"Cross workspace generation marker\"}",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var response = await owner.GetFromJsonAsync<GlobalSearchResponseDto>("/api/search?q=Cross%20workspace%20marker&limit=12");
        Assert.NotNull(response);
        var results = response!.Groups.SelectMany(group => group.Items).ToArray();
        Assert.Equal(4, results.Length);
        Assert.All(results, result =>
        {
            Assert.Null(result.ProjectId);
            Assert.Null(result.ProjectName);
        });
        Assert.Contains(results, result => result.Type == GlobalSearchResultTypes.Conversation);
        Assert.Contains(results, result => result.Type == GlobalSearchResultTypes.Asset);
        Assert.Contains(results, result => result.Type == GlobalSearchResultTypes.File);
        Assert.Contains(results, result => result.Type == GlobalSearchResultTypes.Generation);
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

    private static StoredFile NewReadyFile(Guid workspaceId, Guid userId, DateTime createdAt, Guid? projectId, Guid? conversationId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        UserId = userId,
        ProjectId = projectId,
        ConversationId = conversationId,
        OriginalFileName = $"{Guid.NewGuid():N}.txt",
        StoredFileName = $"{Guid.NewGuid():N}.txt",
        ContentType = "text/plain",
        Extension = ".txt",
        SizeBytes = 32,
        StorageProvider = FileStorageProviders.Local,
        StorageKey = $"search-tests/{Guid.NewGuid():N}.txt",
        Status = StoredFileStatus.Ready,
        TextExtractionStatus = FileExtractionStatus.Ready,
        CreatedAt = createdAt,
        ProcessedAt = createdAt,
    };

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

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, string name, string content, Guid projectId, Guid conversationId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        form.Add(new StringContent(projectId.ToString()), "projectId");
        form.Add(new StringContent(conversationId.ToString()), "conversationId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}

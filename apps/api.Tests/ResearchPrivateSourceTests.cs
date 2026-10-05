using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Controllers;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ResearchPrivateSourceTests : IClassFixture<ResearchApiFactory>
{
    private readonly ResearchApiFactory factory;

    public ResearchPrivateSourceTests(ResearchApiFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Research_job_cannot_consume_another_members_conversation_private_source()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, $"research-private-owner-{Guid.NewGuid():N}@example.com");
        var project = await SendWithCsrf<ProjectDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/projects", new { name = "Private research project", type = "Business" });
        var conversation = await SendWithCsrf<ConversationDto>(owner, HttpMethod.Post, $"/api/workspaces/{ownerAuth.PersonalWorkspace.Id}/conversations", new { projectId = project.Id, title = "Private research source" });
        using var upload = await Upload(owner, ownerAuth.PersonalWorkspace.Id, project.Id, conversation.Id);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var privateFile = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;

        using var member = factory.CreateClient();
        var memberAuth = await Register(member, $"research-private-member-{Guid.NewGuid():N}@example.com");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = memberAuth.User.Id,
                Role = WorkspaceRole.Member,
                JoinedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var created = await SendWithCsrf<CreateResearchGenerationResponse>(member, HttpMethod.Post, "/api/research-generation/jobs", new
        {
            workspaceId = ownerAuth.PersonalWorkspace.Id,
            projectId = project.Id,
            question = "Summarize the selected source safely.",
            depth = "standard",
            reportType = "research_report",
            language = "en",
            useWebSources = false,
            attachmentIds = new[] { privateFile.Id },
        });
        var completed = await WaitForTerminal(member, created.Job.Id);

        Assert.Equal("Failed", completed.Status);
        Assert.Equal(GenerationJobErrorCodes.ResearchSourceUnavailable, completed.ErrorCode);
        Assert.Empty(completed.Outputs);
        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Empty(await verificationDb.ResearchSources.AsNoTracking().Where(source => source.GenerationJobId == created.Job.Id).ToListAsync());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string email)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName = "Research Privacy Tester", email, password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, Guid projectId, Guid conversationId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("PRIVATE RESEARCH SOURCE"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "private-research-source.txt");
        form.Add(new StringContent(projectId.ToString()), "projectId");
        form.Add(new StringContent(conversationId.ToString()), "conversationId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static async Task<GenerationJobDto> WaitForTerminal(HttpClient client, Guid id)
    {
        for (var attempt = 0; attempt < 160; attempt++)
        {
            using var response = await client.GetAsync($"/api/generation/jobs/{id}");
            if (response.IsSuccessStatusCode)
            {
                var current = await response.Content.ReadFromJsonAsync<GenerationJobDto>();
                if (current is not null && (current.Status is "Succeeded" or "Failed" or "Cancelled")) return current;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("Research privacy job did not reach a terminal state.");
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object body)
    {
        using var response = await SendWithCsrf(client, method, path, body);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? body)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}

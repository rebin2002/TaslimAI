using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class FileTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public FileTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Text_and_csv_extraction_is_bounded_and_normalized()
    {
        var extractor = factory.Services.GetRequiredService<IFileContentExtractor>();
        await using var text = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond"));
        var result = await extractor.ExtractAsync(".txt", text);
        Assert.Equal(FileExtractionStatus.Ready, result.Status);
        Assert.Equal("first\nsecond", result.ExtractedText);

        await using var csv = new MemoryStream(Encoding.UTF8.GetBytes("Name,Note\nTaslim,\"A, calm workspace\""));
        var csvResult = await extractor.ExtractAsync(".csv", csv);
        Assert.Equal(FileExtractionStatus.Ready, csvResult.Status);
        Assert.Contains("Taslim | A, calm workspace", csvResult.ExtractedText);
    }

    [Fact]
    public async Task Upload_list_delete_and_attach_file_to_chat_are_authorized()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "File Owner");
        var project = await CreateProject(client, auth.PersonalWorkspace.Id);
        var upload = await Upload(client, auth.PersonalWorkspace.Id, "brief.txt", "Project brief\nwith useful context", project.Id);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var file = (await upload.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Ready", file.Status);
        Assert.Equal("Ready", file.TextExtractionStatus);
        Assert.Equal(project.Id, file.ProjectId);

        var listed = await client.GetFromJsonAsync<List<StoredFileDto>>($"/api/workspaces/{auth.PersonalWorkspace.Id}/files?projectId={project.Id}");
        Assert.Contains(listed!, item => item.Id == file.Id);

        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, project.Id);
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/stream", new
        {
            content = "Use the attached project brief.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Contains("message.completed", await send.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Contains(db.ChatMessageAttachments, attachment => attachment.StoredFileId == file.Id);

        var delete = await SendWithCsrf(client, HttpMethod.Delete, $"/api/files/{file.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var afterDelete = await client.GetAsync($"/api/files/{file.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Unsupported_file_types_and_invalid_signatures_are_rejected()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "File Validation Owner");
        var unsupported = await Upload(client, auth.PersonalWorkspace.Id, "payload.exe", "MZ-not-allowed", null);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        var invalidPdf = await Upload(client, auth.PersonalWorkspace.Id, "notes.pdf", "not-a-pdf", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPdf.StatusCode);
    }

    private async Task<AuthResponse> Register(HttpClient client, string name)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName = name,
            email = $"files-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ProjectDto> CreateProject(HttpClient client, Guid workspaceId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/projects", new { name = "File project", description = "Files", type = "Business" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ProjectDto>())!;
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId, Guid projectId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { projectId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, string name, string content, Guid? projectId)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "text/plain");
        form.Add(file, "file", name);
        if (projectId is not null) form.Add(new StringContent(projectId.Value.ToString()), "projectId");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
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

using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Ai;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ContextRecordingFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");

    public ContextRecordingFactory() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaslimDbContext>>();
            services.AddSingleton(connection);
            services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
            services.RemoveAll<IChatCompletionService>();
            services.AddSingleton<RecordingChatCompletionService>();
            services.AddSingleton<IChatCompletionService>(services => services.GetRequiredService<RecordingChatCompletionService>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public sealed class ContextIntegrationTests : IClassFixture<ContextRecordingFactory>
{
    private readonly ContextRecordingFactory factory;

    public ContextIntegrationTests(ContextRecordingFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Project_context_and_personal_memory_are_scoped_and_normal_chats_do_not_receive_project_context()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Context Owner");
        var project = await SendWithCsrf<ProjectDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/projects", new
        {
            name = "GREEN Appliances Marketing", type = "Marketing",
            instructions = "Always write marketing material in a professional, simple tone.",
            contextNotes = "GREEN sells refrigerators, washing machines, dishwashers and freezers.",
        });
        Assert.Equal("Always write marketing material in a professional, simple tone.", project.Instructions);
        Assert.Equal("GREEN sells refrigerators, washing machines, dishwashers and freezers.", project.ContextNotes);
        var memory = await SendWithCsrf<PersonalMemoryDto>(client, HttpMethod.Post, $"/api/workspaces/{auth.PersonalWorkspace.Id}/memories", new
        {
            category = "Writing", title = "Preferred style", content = "Use concise paragraphs and clear headings.",
        });

        var projectConversation = await CreateConversation(client, auth.PersonalWorkspace.Id, project.Id);
        await SendMessage(client, projectConversation.Id, "Write a short campaign brief.");
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.Contains("professional, simple tone", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("GREEN sells refrigerators", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("concise paragraphs", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);

        var normalConversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        await SendMessage(client, normalConversation.Id, "Write a general note.");
        Assert.DoesNotContain("Project-specific context", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("GREEN sells refrigerators", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("concise paragraphs", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);

        await SendWithCsrf(client, HttpMethod.Delete, $"/api/memories/{memory.Id}", null);
        var afterDeleteConversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        await SendMessage(client, afterDeleteConversation.Id, "Write another general note.");
        Assert.DoesNotContain("concise paragraphs", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_users_memory_is_not_included_in_the_current_users_context()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Context Owner One");
        using var other = factory.CreateClient();
        var otherAuth = await Register(other, "Context Owner Two");
        await SendWithCsrf<PersonalMemoryDto>(other, HttpMethod.Post, $"/api/workspaces/{otherAuth.PersonalWorkspace.Id}/memories", new
        {
            category = "Personal", title = "Other user secret", content = "OTHER_USER_MEMORY_MARKER",
        });

        var conversation = await CreateConversation(owner, ownerAuth.PersonalWorkspace.Id, null);
        await SendMessage(owner, conversation.Id, "Check my context.");
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.DoesNotContain("OTHER_USER_MEMORY_MARKER", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicitly_selected_ready_file_is_added_to_context_without_exposing_storage_metadata()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "File Context Owner");
        var fileResponse = await UploadFile(client, auth.PersonalWorkspace.Id, "brief.txt", "PROJECT_FILE_CONTEXT_MARKER");
        Assert.Equal(HttpStatusCode.Created, fileResponse.StatusCode);
        var file = (await fileResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Ready", file.TextExtractionStatus);

        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Use the selected brief.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });
        Assert.True(send.IsSuccessStatusCode, await send.Content.ReadAsStringAsync());

        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.Contains("Attached file context", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("PROJECT_FILE_CONTEXT_MARKER", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain(file.StorageProvider, recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain(file.Id.ToString(), recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task New_conversation_first_message_with_pdf_is_associated_and_contextualized()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "PDF Context Owner");
        var fileResponse = await UploadPdfFile(client, auth.PersonalWorkspace.Id, "research proposal.pdf", "PDF_CONTEXT_MARKER");
        Assert.Equal(HttpStatusCode.Created, fileResponse.StatusCode);
        var file = (await fileResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Ready", file.Status);
        Assert.Equal("Ready", file.TextExtractionStatus);
        Assert.True(file.ExtractedTextLength > 0);

        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages/stream", new
        {
            content = "Summarize this PDF and tell me 3 important points from it.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.Contains(db.ChatMessageAttachments, attachment => attachment.StoredFileId == file.Id);

        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.Contains("[Attached file: research proposal.pdf]", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("PDF_CONTEXT_MARKER", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("[End attached file]", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Single(recorder.LastRequest.Attachments!);
    }

    [Fact]
    public async Task Existing_conversation_with_pdf_attachment_reaches_the_same_context_path()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Existing PDF Owner");
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        var fileResponse = await UploadPdfFile(client, auth.PersonalWorkspace.Id, "existing.pdf", "EXISTING_PDF_MARKER");
        var file = (await fileResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;

        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Use the attached PDF.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.Contains("[Attached file: existing.pdf]", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("EXISTING_PDF_MARKER", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_text_attachments_are_authorized_and_bounded_in_context()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Multiple File Owner");
        var firstResponse = await UploadFile(client, auth.PersonalWorkspace.Id, "one.txt", "FIRST_ATTACHMENT_MARKER");
        var secondResponse = await UploadFile(client, auth.PersonalWorkspace.Id, "two.txt", "SECOND_ATTACHMENT_MARKER");
        var first = (await firstResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;
        var second = (await secondResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;
        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);

        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Compare the attached notes.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { first.Id, second.Id },
        });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);

        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        Assert.Contains("[Attached file: one.txt]", recorder.LastRequest!.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("FIRST_ATTACHMENT_MARKER", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("[Attached file: two.txt]", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("SECOND_ATTACHMENT_MARKER", recorder.LastRequest.SystemInstruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attachment_from_another_workspace_is_rejected_before_provider_execution()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Attachment Owner");
        var fileResponse = await UploadFile(owner, ownerAuth.PersonalWorkspace.Id, "private.txt", "PRIVATE_ATTACHMENT_MARKER");
        var file = (await fileResponse.Content.ReadFromJsonAsync<StoredFileDto>())!;

        using var other = factory.CreateClient();
        var otherAuth = await Register(other, "Other Workspace");
        var conversation = await CreateConversation(other, otherAuth.PersonalWorkspace.Id, null);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;
        var response = await SendWithCsrf(other, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Read this file.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ATTACHMENT_NOT_AVAILABLE", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(callsBefore, recorder.CallCount);
    }

    [Fact]
    public async Task Failed_extraction_is_rejected_without_text_only_provider_fallback()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Unavailable File Owner");
        var response = await UploadFile(client, auth.PersonalWorkspace.Id, "failed.pdf", "%PDF-1.4\nnot a real PDF document");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var file = (await response.Content.ReadFromJsonAsync<StoredFileDto>())!;
        Assert.Equal("Failed", file.Status);
        Assert.Equal("Failed", file.TextExtractionStatus);

        var conversation = await CreateConversation(client, auth.PersonalWorkspace.Id, null);
        var recorder = factory.Services.GetRequiredService<RecordingChatCompletionService>();
        var callsBefore = recorder.CallCount;
        var send = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversation.Id}/messages", new
        {
            content = "Summarize the file.",
            requestId = Guid.NewGuid().ToString("N"),
            attachmentIds = new[] { file.Id },
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, send.StatusCode);
        var error = await send.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("We received your file, but couldn't read its contents. Please try the file again.", error.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(callsBefore, recorder.CallCount);
    }

    [Fact]
    public void Context_builder_reserves_output_budget_and_bounds_memory_content()
    {
        var builder = new AiContextBuilder(Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            ContextBudgetTokens = 256,
            ContextOutputReserveTokens = 64,
            ProjectContextBudgetTokens = 30,
            PersonalMemoryContextBudgetTokens = 30,
            MaxPersonalMemories = 3,
            SystemInstruction = "Taslim system",
        }));
        var memories = new[]
        {
            new AiMemoryContext("Writing", "First", new string('a', 500)),
            new AiMemoryContext("Writing", "Second", new string('b', 500)),
            new AiMemoryContext("Writing", "Third", "should be excluded"),
        };
        var request = builder.Build([new AiChatMessage("user", "old"), new AiChatMessage("assistant", "recent")], new string('p', 500), "notes", memories);
        Assert.Contains("Project-specific context", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("First", request.SystemInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("Third", request.SystemInstruction, StringComparison.Ordinal);
        Assert.Equal("recent", request.Messages[^1].Content);
    }

    private async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"context-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<ConversationDto> CreateConversation(HttpClient client, Guid workspaceId, Guid? projectId)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/workspaces/{workspaceId}/conversations", new { projectId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>())!;
    }

    private static async Task SendMessage(HttpClient client, Guid conversationId, string content)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, $"/api/conversations/{conversationId}/messages", new { content, requestId = Guid.NewGuid().ToString("N") });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> UploadFile(HttpClient client, Guid workspaceId, string fileName, string content)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "text/plain");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> UploadPdfFile(HttpClient client, Guid workspaceId, string fileName, string marker)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(CreatePdf(marker));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }

    private static byte[] CreatePdf(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length { $"BT /F1 12 Tf 72 720 Td ({escaped}) Tj ET".Length} >>\nstream\nBT /F1 12 Tf 72 720 Td ({escaped}) Tj ET\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        foreach (var (value, index) in objects.Select((value, index) => (value, index)))
        {
            offsets.Add(output.Position);
            WriteAscii(output, $"{index + 1} 0 obj\n{value}\nendobj\n");
        }
        var xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return output.ToArray();
    }

    private static void WriteAscii(Stream output, string value) => output.Write(Encoding.ASCII.GetBytes(value));
}

public sealed class RecordingChatCompletionService : IChatCompletionService
{
    public AiChatRequest? LastRequest { get; private set; }
    public int CallCount { get; private set; }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        await Task.Yield();
        yield return new AiMessageDelta("recorded");
        yield return new AiMessageCompleted(new AiUsageMetadata("mock", "taslim-test-context", null, null, null, 0m, 0m, 0, "test-complete", true));
    }

    public Task<AiGenerationResult> CompleteAsync(AiChatRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return Task.FromResult(new AiGenerationResult("recorded", new AiUsageMetadata("mock", "taslim-test-context", null, null, null, 0m, 0m, 0, "test-complete", true)));
    }
}

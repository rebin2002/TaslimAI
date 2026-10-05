using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class UploadProcessingFailureApiFactory : TaslimApiFactory
{
    internal readonly TestFileStorageService Storage = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(Storage);
            services.RemoveAll<IFileContentExtractor>();
            services.AddSingleton<IFileContentExtractor, ThrowingFileContentExtractor>();
        });
    }
}

public sealed class FileProcessingFailureTests : IClassFixture<UploadProcessingFailureApiFactory>
{
    private readonly UploadProcessingFailureApiFactory factory;

    public FileProcessingFailureTests(UploadProcessingFailureApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Unexpected_processing_failure_returns_safe_error_and_removes_uploaded_object()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client);
        var response = await Upload(client, auth.PersonalWorkspace.Id, "health-notes.txt", "private health detail");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<JsonElement>(rawBody);
        Assert.Equal("FILE_STORAGE_OPERATION_FAILED", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("We couldn't store this file. Please try again.", error.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("private extractor detail", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.True(factory.Storage.DeleteCalled);
        Assert.Empty(factory.Storage.StoredKeys);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var failed = await db.StoredFiles.AsNoTracking().SingleAsync(file => file.UserId == auth.User.Id);
        Assert.Equal(StoredFileStatus.Failed, failed.Status);
        Assert.Equal(FileExtractionStatus.Failed, failed.TextExtractionStatus);
        Assert.NotNull(failed.ProcessedAt);
    }

    private static async Task<AuthResponse> Register(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(new
        {
            displayName = "Upload Failure Owner",
            email = $"upload-failure-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid workspaceId, string name, string content)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await client.SendAsync(request);
    }
}

internal sealed class ThrowingFileContentExtractor : IFileContentExtractor
{
    public bool CanHandle(string extension) => string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase);

    public Task<FileExtractionResult> ExtractAsync(string extension, Stream content, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("private extractor detail");
}

internal sealed class TestFileStorageService : IFileStorageService
{
    private readonly Dictionary<string, byte[]> stored = new(StringComparer.Ordinal);

    public string ProviderKey => "test-storage";
    public bool DeleteCalled { get; private set; }
    public IReadOnlyCollection<string> StoredKeys => stored.Keys;

    public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        stored[storageKey] = buffer.ToArray();
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(stored.TryGetValue(storageKey, out var content) ? new MemoryStream(content, writable: false) : null);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        DeleteCalled = true;
        stored.Remove(storageKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(stored.ContainsKey(storageKey));
}

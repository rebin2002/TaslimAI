using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Taslim.Api.Contracts;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MusicReferenceAudioValidationApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Files:MaxFileSizeBytes", "8");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<ReferenceAudioRecordingStorageService>();
            services.AddSingleton<IFileStorageService>(provider =>
                provider.GetRequiredService<ReferenceAudioRecordingStorageService>());
        });
    }
}

public sealed class MusicReferenceAudioValidationRegressionTests : IClassFixture<MusicReferenceAudioValidationApiFactory>
{
    private readonly MusicReferenceAudioValidationApiFactory factory;

    public MusicReferenceAudioValidationRegressionTests(MusicReferenceAudioValidationApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Reference_audio_upload_candidates_enforce_type_mime_size_and_workspace_access_before_storage()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Reference Audio Owner");
        var storage = factory.Services.GetRequiredService<ReferenceAudioRecordingStorageService>();

        var unsupportedAudio = await Upload(
            owner,
            ownerAuth.PersonalWorkspace.Id,
            "reference.mp3",
            "audio/mpeg",
            [0x49, 0x44, 0x33, 0x03]);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedAudio.StatusCode);

        var audioMimeOnTextType = await Upload(
            owner,
            ownerAuth.PersonalWorkspace.Id,
            "reference.txt",
            "audio/mpeg",
            [0x01, 0x02, 0x03]);
        Assert.Equal(HttpStatusCode.BadRequest, audioMimeOnTextType.StatusCode);

        var oversized = await Upload(
            owner,
            ownerAuth.PersonalWorkspace.Id,
            "reference.txt",
            "text/plain",
            [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using var other = factory.CreateClient();
        await Register(other, "Other Workspace User");
        var foreignWorkspaceUpload = await Upload(
            other,
            ownerAuth.PersonalWorkspace.Id,
            "reference.txt",
            "text/plain",
            [0x01, 0x02, 0x03]);
        Assert.Equal(HttpStatusCode.Forbidden, foreignWorkspaceUpload.StatusCode);

        Assert.Equal(0, storage.StoreCalls);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        Assert.False(await db.StoredFiles.AsNoTracking().AnyAsync(item => item.WorkspaceId == ownerAuth.PersonalWorkspace.Id));
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register");
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        request.Content = JsonContent.Create(new
        {
            displayName,
            email = $"music-reference-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> Upload(
        HttpClient client,
        Guid workspaceId,
        string fileName,
        string contentType,
        byte[] bytes)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId}/files")
        {
            Content = form,
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        return await client.SendAsync(request);
    }
}

internal sealed class ReferenceAudioRecordingStorageService : IFileStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private int storeCalls;

    public string ProviderKey => "ReferenceAudioTestMemory";
    public int StoreCalls => Volatile.Read(ref storeCalls);

    public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        objects[storageKey] = buffer.ToArray();
        Interlocked.Increment(ref storeCalls);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        Stream? content = objects.TryGetValue(storageKey, out var bytes)
            ? new MemoryStream(bytes, writable: false)
            : null;
        return Task.FromResult(content);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(objects.ContainsKey(storageKey));
}

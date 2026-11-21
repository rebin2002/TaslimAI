using System.Collections.Concurrent;
using System.Net;
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
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MoviePrivateR2ReadbackBoundaryTests : IClassFixture<MoviePrivateR2ReadbackApiFactory>
{
    private static readonly byte[] FakeClipContent = Encoding.UTF8.GetBytes("fake-private-movie-clip-content");
    private readonly MoviePrivateR2ReadbackApiFactory factory;

    public MoviePrivateR2ReadbackBoundaryTests(MoviePrivateR2ReadbackApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Movie_clip_readback_is_available_to_its_owner_but_not_another_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Private Movie Clip Owner");
        using var otherWorkspace = factory.CreateClient();
        var otherAuth = await Register(otherWorkspace, "Other Movie Workspace Owner");
        Assert.NotEqual(ownerAuth.PersonalWorkspace.Id, otherAuth.PersonalWorkspace.Id);

        var now = DateTime.UtcNow;
        var movieProjectId = Guid.NewGuid();
        var movieClipId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var storageKey = $"private-r2/movie-clips/{storedFileId:N}/clip.mp4";
        var storage = factory.Services.GetRequiredService<FakePrivateR2Storage>();
        await storage.StoreAsync(storageKey, new MemoryStream(FakeClipContent, writable: false));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.MovieProjects.Add(new MovieProject
            {
                Id = movieProjectId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                Title = "Private clip readback fixture",
                Description = "A seeded Movie project; no generation is queued.",
                DurationSeconds = 3,
                CreatedAt = now,
                UpdatedAt = now,
                Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), MovieProjectId = movieProjectId, UpdatedAt = now },
            });
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                OriginalFileName = "private-clip.mp4",
                StoredFileName = "private-clip.mp4",
                ContentType = "video/mp4",
                Extension = ".mp4",
                SizeBytes = FakeClipContent.Length,
                StorageProvider = FileStorageProviders.S3Compatible,
                StorageKey = storageKey,
                Status = StoredFileStatus.Ready,
                TextExtractionStatus = FileExtractionStatus.NotApplicable,
                CreatedAt = now,
                ProcessedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Private Movie clip",
                AssetType = AssetTypes.Video,
                MimeType = "video/mp4",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.MovieClips.Add(new MovieClip
            {
                Id = movieClipId,
                MovieProjectId = movieProjectId,
                AssetId = assetId,
                StoredFileId = storedFileId,
                Status = MovieClipStatuses.Ready,
                DurationSeconds = 3,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        using var ownerMetadata = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, ownerMetadata.StatusCode);
        var ownerMetadataBody = await ownerMetadata.Content.ReadAsStringAsync();
        Assert.DoesNotContain(storageKey, ownerMetadataBody, StringComparison.Ordinal);

        using var ownerReadback = await owner.GetAsync($"/api/assets/{assetId}/download?inline=true");
        Assert.Equal(HttpStatusCode.OK, ownerReadback.StatusCode);
        Assert.Equal("video/mp4", ownerReadback.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeClipContent, await ownerReadback.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, storage.OpenReadCount);

        using var foreignMetadata = await otherWorkspace.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignMetadata.StatusCode);
        using var foreignReadback = await otherWorkspace.GetAsync($"/api/assets/{assetId}/download?inline=true");
        Assert.Equal(HttpStatusCode.NotFound, foreignReadback.StatusCode);
        var foreignBody = await foreignReadback.Content.ReadAsStringAsync();
        Assert.DoesNotContain(storageKey, foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(FakeClipContent), foreignBody, StringComparison.Ordinal);
        Assert.Equal(1, storage.OpenReadCount);
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"movie-private-readback-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

public sealed class MoviePrivateR2ReadbackApiFactory : TaslimApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<FakePrivateR2Storage>();
            services.AddSingleton<IFileStorageService>(provider => provider.GetRequiredService<FakePrivateR2Storage>());
        });
    }
}

public sealed class FakePrivateR2Storage : IFileStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);
    private int openReadCount;

    public string ProviderKey => FileStorageProviders.S3Compatible;
    public int OpenReadCount => Volatile.Read(ref openReadCount);

    public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        objects[storageKey] = buffer.ToArray();
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref openReadCount);
        Stream? stream = objects.TryGetValue(storageKey, out var content)
            ? new MemoryStream(content, writable: false)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        objects.TryRemove(storageKey, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(objects.ContainsKey(storageKey));
}

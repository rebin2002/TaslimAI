using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ImageAssetReadbackAuthorizationTests : IClassFixture<GenerationJobsNoWorkerFactory>
{
    private static readonly byte[] ImageBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly GenerationJobsNoWorkerFactory factory;

    public ImageAssetReadbackAuthorizationTests(GenerationJobsNoWorkerFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Image_asset_readback_returns_binary_only_to_its_owning_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Image Readback Owner");

        using var otherWorkspaceOwner = factory.CreateClient();
        var otherAuth = await Register(otherWorkspaceOwner, "Other Workspace Owner");
        Assert.NotEqual(ownerAuth.PersonalWorkspace.Id, otherAuth.PersonalWorkspace.Id);

        var now = DateTime.UtcNow;
        var storedFileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storageKey = $"image-readback/{Guid.NewGuid():N}.png";
        using (var scope = factory.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
            await using var imageStream = new MemoryStream(ImageBytes, writable: false);
            await storage.StoreAsync(storageKey, imageStream);

            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                OriginalFileName = "private-image.png",
                StoredFileName = "private-image.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = ImageBytes.Length,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = storageKey,
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFileId,
                Name = "Private Image Studio output",
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        try
        {
            using var ownerReadback = await owner.GetAsync($"/api/assets/{assetId}/download?inline=true");
            Assert.Equal(HttpStatusCode.OK, ownerReadback.StatusCode);
            Assert.Equal("image/png", ownerReadback.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ImageBytes, await ownerReadback.Content.ReadAsByteArrayAsync());

            using var foreignReadback = await otherWorkspaceOwner.GetAsync($"/api/assets/{assetId}/download?inline=true");
            Assert.Equal(HttpStatusCode.NotFound, foreignReadback.StatusCode);
            Assert.NotEqual("image/png", foreignReadback.Content.Headers.ContentType?.MediaType);
            var error = await foreignReadback.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ASSET_NOT_FOUND", error.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var asset = await db.Assets.SingleOrDefaultAsync(item => item.Id == assetId);
            if (asset is not null) db.Assets.Remove(asset);
            var storedFile = await db.StoredFiles.SingleOrDefaultAsync(item => item.Id == storedFileId);
            if (storedFile is not null) db.StoredFiles.Remove(storedFile);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IFileStorageService>().DeleteAsync(storageKey);
        }
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        using var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"image-readback-{Guid.NewGuid():N}@example.com",
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

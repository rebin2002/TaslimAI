using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DocumentWorkspaceReadDownloadAuthorizationTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public DocumentWorkspaceReadDownloadAuthorizationTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Document_asset_read_and_primary_download_are_denied_outside_owning_workspace()
    {
        using var owner = factory.CreateClient();
        var ownerAuth = await Register(owner, "Document Owner");
        using var outsider = factory.CreateClient();
        await Register(outsider, "Document Outsider");

        var assetId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            var storedFile = new StoredFile
            {
                Id = Guid.NewGuid(),
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                UserId = ownerAuth.User.Id,
                OriginalFileName = "confidential-workspace-document.pdf",
                StoredFileName = "confidential-workspace-document.pdf",
                ContentType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 1,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"denial-test/{Guid.NewGuid():N}.pdf",
                Status = StoredFileStatus.Ready,
                CreatedAt = now,
                ProcessedAt = now,
            };
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = ownerAuth.PersonalWorkspace.Id,
                CreatedByUserId = ownerAuth.User.Id,
                StoredFileId = storedFile.Id,
                Name = "Confidential workspace document",
                AssetType = AssetTypes.Document,
                MimeType = storedFile.ContentType,
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StoredFiles.Add(storedFile);
            await db.SaveChangesAsync();
        }

        var ownerRead = await owner.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        var ownerAsset = await ownerRead.Content.ReadFromJsonAsync<AssetDto>();
        Assert.NotNull(ownerAsset);
        Assert.Equal(AssetTypes.Document, ownerAsset!.AssetType);

        var foreignRead = await outsider.GetAsync($"/api/assets/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", (await foreignRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());

        var foreignDownload = await outsider.GetAsync($"/api/assets/{assetId}/download");
        Assert.Equal(HttpStatusCode.NotFound, foreignDownload.StatusCode);
        Assert.Equal("ASSET_NOT_FOUND", (await foreignDownload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                displayName,
                email = $"document-access-{Guid.NewGuid():N}@example.com",
                password = "StrongPassword!123",
                preferredLanguage = "en",
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }
}

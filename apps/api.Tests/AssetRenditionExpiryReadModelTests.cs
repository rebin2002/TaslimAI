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

/// <summary>
/// The asset read model is the only rendition-availability signal the Assets library detail view
/// has. Its media element and its download/open controls are driven by `hasFile`/`canPreview`, and
/// its fallback ("No file", controls withheld) fires only when those come back false. A stored
/// rendition that expires or is pruned keeps the asset row's `StoredFileId` — that is exactly what
/// `FileProcessingService.DeleteAsync` leaves behind — so a read model that reports availability
/// from the identity alone promises a preview and a download that dead-end in
/// `409 ASSET_FILE_UNAVAILABLE` / `404 ASSET_FILE_NOT_FOUND`. These tests pin the read model to the
/// same availability fact the download route already enforces, without changing that access policy.
/// </summary>
public sealed class AssetRenditionExpiryReadModelTests : IClassFixture<GenerationJobsApiFactory>
{
    private readonly GenerationJobsApiFactory factory;

    public AssetRenditionExpiryReadModelTests(GenerationJobsApiFactory factory)
    {
        this.factory = factory;
        Task.Delay(150).GetAwaiter().GetResult();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task Detail_view_falls_back_once_the_image_rendition_is_pruned()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Pruned Rendition Owner");
        var fixture = await CreateImageAsset(client, auth, "Expired poster", StoredFileStatus.Ready);

        // Baseline: a ready rendition is offered as a preview and a download.
        Assert.True((await ReadDetail(client, fixture.AssetId)).HasFile);
        Assert.True((await ReadDetail(client, fixture.AssetId)).CanPreview);

        // A retention/prune pass marks the backing file Deleted and clears its hashes, while the
        // asset row keeps its StoredFileId. The library list and the detail row load afterwards.
        await SetStoredFileStatus(fixture.StoredFileId, StoredFileStatus.Deleted);

        var detail = await ReadDetail(client, fixture.AssetId);
        Assert.False(detail.HasFile);
        Assert.False(detail.CanPreview);
        Assert.Null(detail.FileSizeBytes);

        var row = await ReadListRow(client, auth, "Expired poster");
        Assert.False(row.HasFile);
        Assert.False(row.CanPreview);
        Assert.Null(row.FileSizeBytes);

        // The download access policy is deliberately unchanged: still a conflict, never bytes.
        var download = await client.GetAsync($"/api/assets/{fixture.AssetId}/download");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Contains("ASSET_FILE_UNAVAILABLE", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_view_falls_back_while_an_unexpired_rendition_is_not_ready_yet()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Processing Rendition Owner");
        var fixture = await CreateImageAsset(client, auth, "Unprocessed poster", StoredFileStatus.Processing);

        var detail = await ReadDetail(client, fixture.AssetId);
        Assert.False(detail.HasFile);
        Assert.False(detail.CanPreview);

        var row = await ReadListRow(client, auth, "Unprocessed poster");
        Assert.False(row.HasFile);
        Assert.False(row.CanPreview);

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/assets/{fixture.AssetId}/download")).StatusCode);
    }

    [Fact]
    public async Task Detail_view_keeps_offering_a_rendition_that_is_still_ready()
    {
        using var client = factory.CreateClient();
        var auth = await Register(client, "Live Rendition Owner");
        var fixture = await CreateImageAsset(client, auth, "Live poster", StoredFileStatus.Ready);

        var detail = await ReadDetail(client, fixture.AssetId);
        Assert.True(detail.HasFile);
        Assert.True(detail.CanPreview);
        Assert.Equal(4L, detail.FileSizeBytes);

        var row = await ReadListRow(client, auth, "Live poster");
        Assert.True(row.HasFile);
        Assert.True(row.CanPreview);
        Assert.NotNull(row.Representations);
    }

    private static async Task<AssetDto> ReadDetail(HttpClient client, Guid assetId) =>
        (await client.GetFromJsonAsync<AssetDto>($"/api/assets/{assetId}"))!;

    private static async Task<AssetDto> ReadListRow(HttpClient client, AuthResponse auth, string name)
    {
        var list = await client.GetFromJsonAsync<AssetListDto>($"/api/assets?workspaceId={auth.PersonalWorkspace.Id}&status=Active&search={Uri.EscapeDataString(name)}");
        return Assert.Single(list!.Items);
    }

    private async Task<(Guid AssetId, Guid StoredFileId)> CreateImageAsset(HttpClient client, AuthResponse auth, string name, StoredFileStatus status)
    {
        var assetId = Guid.NewGuid();
        var storedFileId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
            db.StoredFiles.Add(new StoredFile
            {
                Id = storedFileId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                UserId = auth.User.Id,
                OriginalFileName = $"{name}.png",
                StoredFileName = $"{storedFileId:N}.png",
                ContentType = "image/png",
                Extension = ".png",
                SizeBytes = 4,
                StorageProvider = FileStorageProviders.Local,
                StorageKey = $"rendition-expiry/{storedFileId:N}.png",
                Status = status,
                CreatedAt = now,
                ProcessedAt = status == StoredFileStatus.Ready ? now : null,
            });
            db.Assets.Add(new Asset
            {
                Id = assetId,
                WorkspaceId = auth.PersonalWorkspace.Id,
                CreatedByUserId = auth.User.Id,
                StoredFileId = storedFileId,
                Name = name,
                AssetType = AssetTypes.Image,
                MimeType = "image/png",
                Status = AssetStatus.Active,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        return (assetId, storedFileId);
    }

    private async Task SetStoredFileStatus(Guid storedFileId, StoredFileStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaslimDbContext>();
        var storedFile = await db.StoredFiles.SingleAsync(item => item.Id == storedFileId);
        storedFile.Status = status;
        if (status == StoredFileStatus.Deleted)
        {
            storedFile.DeletedAt = DateTime.UtcNow;
            storedFile.ContentHashSha256 = null;
            storedFile.RetainUntil = null;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<AuthResponse> Register(HttpClient client, string displayName)
    {
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new { displayName, email = $"asset-rendition-{Guid.NewGuid():N}@example.com", password = "StrongPassword!123", preferredLanguage = "en" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

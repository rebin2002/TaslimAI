using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Controllers;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Response-cache contract for authenticated asset downloads.
///
/// Private asset bytes are served from a workspace- and user-independent URL
/// (/api/assets/{id}/download) under cookie authentication. Without an explicit
/// cache directive the response carries no freshness information at all, so an
/// intermediary is free to apply heuristic caching and reuse one user's bytes for
/// another caller of the same URL. These tests pin the fence on every response
/// that can carry private asset bytes or private storage state.
/// </summary>
public sealed class AssetDownloadCacheControlTests
{
    [Fact]
    public async Task Attachment_download_is_marked_private_non_storable_and_cookie_varying()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "application/pdf");

        var result = await controller.Download(Guid.NewGuid());

        Assert.IsType<FileStreamResult>(result);
        AssertDownloadFence(context.Response);
    }

    [Fact]
    public async Task Inline_media_preview_is_marked_private_non_storable_and_cookie_varying()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "video/mp4");

        var result = await controller.Download(Guid.NewGuid(), inline: true);

        Assert.IsType<FileStreamResult>(result);
        AssertDownloadFence(context.Response);
    }

    [Fact]
    public async Task Representation_download_is_marked_private_non_storable_and_cookie_varying()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "image/png");

        var result = await controller.DownloadRepresentation(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<FileStreamResult>(result);
        AssertDownloadFence(context.Response);
    }

    [Fact]
    public async Task Missing_asset_file_response_is_also_marked_non_storable()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "application/pdf", openRead: () => Task.FromResult<Stream?>(null));

        var result = await controller.Download(Guid.NewGuid());

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
        AssertDownloadFence(context.Response);
    }

    [Fact]
    public async Task Storage_failure_response_is_also_marked_non_storable()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "application/pdf",
            openRead: () => throw new IOException("synthetic local storage read failure"));

        var result = await controller.Download(Guid.NewGuid());

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
        AssertDownloadFence(context.Response);
    }

    [Fact]
    public async Task Download_fence_never_advertises_a_shared_or_timed_cache()
    {
        var context = CreateContext(out var userId, out var workspaceId);
        var controller = CreateController(context, userId, workspaceId, contentType: "application/pdf");

        await controller.Download(Guid.NewGuid());

        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.False(string.IsNullOrWhiteSpace(cacheControl),
            "An absent Cache-Control header cannot be treated as safe: intermediaries fall back to heuristic freshness rather than refusing to store.");
        Assert.DoesNotContain("public", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s-maxage", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Response.Headers.ContainsKey("Expires"));
    }

    private static DefaultHttpContext CreateContext(out Guid userId, out Guid workspaceId)
    {
        userId = Guid.NewGuid();
        workspaceId = Guid.NewGuid();
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                authenticationType: "test")),
        };
    }

    private static void AssertDownloadFence(HttpResponse response)
    {
        var cacheControl = response.Headers.CacheControl.ToString();
        Assert.False(string.IsNullOrWhiteSpace(cacheControl),
            "Authenticated asset downloads must declare an explicit cache directive; an absent Cache-Control header leaves the response open to heuristic caching by intermediaries.");
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("private", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Cookie", response.Headers.Vary.ToString());
    }

    private static AssetsController CreateController(
        HttpContext context,
        Guid userId,
        Guid workspaceId,
        string contentType,
        Func<Task<Stream?>>? openRead = null)
    {
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = "quarterly-report.pdf",
            ContentType = contentType,
            StorageKey = $"files/{Guid.NewGuid():N}",
            Status = StoredFileStatus.Ready,
        };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Name = "Quarterly Report",
            StoredFile = storedFile,
        };
        var download = new AssetDownload(asset, storedFile, storedFile.OriginalFileName, storedFile.ContentType);

        return new AssetsController(
            new FixedAssetService(download),
            new DelegateFileStorage(openRead ?? (static () => Task.FromResult<Stream?>(new MemoryStream(new byte[] { 1, 2, 3 })))),
            NullLogger<AssetsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private sealed class FixedAssetService(AssetDownload download) : IAssetService
    {
        public Task<Asset?> GetAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AssetListDto?> ListAsync(Guid userId, AssetFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Asset?> UpdateAsync(Guid userId, Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Asset?> ArchiveAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Asset?> RestoreAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AssetDownload?> GetDownloadAsync(Guid userId, Guid assetId, Guid? representationId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssetDownload?>(download);
    }

    private sealed class DelegateFileStorage(Func<Task<Stream?>> openRead) : IFileStorageService
    {
        public string ProviderKey => FileStorageProviders.Local;
        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => openRead();
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

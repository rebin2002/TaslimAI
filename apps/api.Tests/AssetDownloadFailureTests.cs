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

public sealed class AssetDownloadFailureTests
{
    [Fact]
    public async Task Local_storage_io_failure_returns_the_established_storage_error()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            OriginalFileName = "report.pdf",
            ContentType = "application/pdf",
            StorageKey = "files/report.pdf",
            Status = StoredFileStatus.Ready,
        };
        var asset = new Asset
        {
            Id = assetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Name = "Report",
            StoredFile = storedFile,
        };
        var controller = new AssetsController(
            new FixedAssetService(new AssetDownload(asset, storedFile, storedFile.OriginalFileName, storedFile.ContentType)),
            new ThrowingStorage(),
            NullLogger<AssetsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        authenticationType: "test")),
                },
            },
        };

        var result = await controller.Download(assetId);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        var envelope = Assert.IsType<ErrorEnvelope>(response.Value);
        Assert.Equal("FILE_STORAGE_OPERATION_FAILED", envelope.Error.Code);
    }

    private sealed class ThrowingStorage : IFileStorageService
    {
        public string ProviderKey => FileStorageProviders.Local;

        public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new IOException("synthetic local storage read failure");

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
}

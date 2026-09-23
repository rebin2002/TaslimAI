using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/assets")]
public sealed class AssetsController(
    IAssetService assets,
    IFileStorageService storage,
    ILogger<AssetsController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid workspaceId,
        [FromQuery] Guid? projectId,
        [FromQuery] string? assetType,
        [FromQuery] AssetStatus? status,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await assets.ListAsync(GetUserId(), new AssetFilter(workspaceId, projectId, assetType, status, search, page, pageSize), cancellationToken);
            return result is null ? Forbid() : Ok(result);
        }
        catch (AssetValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var asset = await assets.GetAsync(GetUserId(), id, cancellationToken);
        return asset is null
            ? ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_NOT_FOUND", "Asset not found.")
            : Ok(AssetContractMapper.ToDto(asset));
    }

    [HttpPatch("{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assets.UpdateAsync(GetUserId(), id, request, cancellationToken);
            return asset is null
                ? ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_NOT_FOUND", "Asset not found.")
                : Ok(AssetContractMapper.ToDto(asset));
        }
        catch (AssetValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status400BadRequest, exception.Code, exception.Message);
        }
    }

    [HttpPost("{id:guid}/archive")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken) => SetStatus(id, true, cancellationToken);

    [HttpPost("{id:guid}/restore")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) => SetStatus(id, false, cancellationToken);

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] bool inline = false, CancellationToken cancellationToken = default)
    {
        AssetDownload? download;
        try
        {
            download = await assets.GetDownloadAsync(GetUserId(), id, cancellationToken: cancellationToken);
        }
        catch (AssetValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
        if (download is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_NOT_FOUND", "Asset not found.");

        return await StreamDownloadAsync(download, inline, cancellationToken);
    }

    [HttpGet("{id:guid}/representations/{representationId:guid}/download")]
    public async Task<IActionResult> DownloadRepresentation(Guid id, Guid representationId, CancellationToken cancellationToken)
    {
        AssetDownload? download;
        try
        {
            download = await assets.GetDownloadAsync(GetUserId(), id, representationId, cancellationToken);
        }
        catch (AssetValidationException exception)
        {
            return ApiResults.Error(this, StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
        if (download is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_NOT_FOUND", "Asset not found.");
        return await StreamDownloadAsync(download, false, cancellationToken);
    }

    private async Task<IActionResult> StreamDownloadAsync(AssetDownload download, bool inline, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await storage.OpenReadAsync(download.StoredFile.StorageKey, cancellationToken);
            if (stream is null) return ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_FILE_NOT_FOUND", "The asset file could not be found.");
            if (inline && (download.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || download.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || download.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
                return File(stream, download.ContentType, enableRangeProcessing: true);
            return File(stream, download.ContentType, download.FileName, enableRangeProcessing: true);
        }
        catch (FileStorageUnavailableException)
        {
            logger.LogWarning("Asset download rejected because private storage is unavailable. AssetId={AssetId}; WorkspaceId={WorkspaceId}", download.Asset.Id, download.Asset.WorkspaceId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "FILE_STORAGE_UNAVAILABLE", "Asset storage is not available right now.");
        }
        catch (FileStorageOperationException)
        {
            logger.LogWarning("Asset download failed because the configured private storage provider rejected the operation. AssetId={AssetId}; WorkspaceId={WorkspaceId}", download.Asset.Id, download.Asset.WorkspaceId);
            return ApiResults.Error(this, StatusCodes.Status503ServiceUnavailable, "FILE_STORAGE_OPERATION_FAILED", "We couldn't open this asset. Please try again.");
        }
    }

    private async Task<IActionResult> SetStatus(Guid id, bool archive, CancellationToken cancellationToken)
    {
        var asset = archive
            ? await assets.ArchiveAsync(GetUserId(), id, cancellationToken)
            : await assets.RestoreAsync(GetUserId(), id, cancellationToken);
        return asset is null
            ? ApiResults.Error(this, StatusCodes.Status404NotFound, "ASSET_NOT_FOUND", "Asset not found.")
            : Ok(AssetContractMapper.ToDto(asset));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

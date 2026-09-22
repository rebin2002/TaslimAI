using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Assets;

public interface IAssetService
{
    Task<Asset?> GetAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<AssetListDto?> ListAsync(Guid userId, AssetFilter filter, CancellationToken cancellationToken = default);
    Task<Asset?> UpdateAsync(Guid userId, Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken = default);
    Task<Asset?> ArchiveAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<Asset?> RestoreAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<AssetDownload?> GetDownloadAsync(Guid userId, Guid assetId, Guid? representationId = null, CancellationToken cancellationToken = default);
}

public sealed record AssetDownload(Asset Asset, StoredFile StoredFile, string FileName, string ContentType);

public sealed class AssetValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AssetService(TaslimDbContext db, WorkspaceAccessService access) : IAssetService
{
    public async Task<Asset?> GetAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var asset = await Query().FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken);
        return asset is not null && await access.IsMemberAsync(userId, asset.WorkspaceId, cancellationToken) ? asset : null;
    }

    public async Task<AssetListDto?> ListAsync(Guid userId, AssetFilter filter, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(userId, filter.WorkspaceId, cancellationToken)) return null;
        var page = Math.Clamp(filter.Page, 1, 1_000_000);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var query = Query().Where(asset => asset.WorkspaceId == filter.WorkspaceId && asset.Status == (filter.Status ?? AssetStatus.Active));
        if (filter.ProjectId.HasValue) query = query.Where(asset => asset.ProjectId == filter.ProjectId.Value);
        if (!string.IsNullOrWhiteSpace(filter.AssetType))
        {
            var type = filter.AssetType.Trim().ToLowerInvariant();
            if (!AssetTypes.Supported.Contains(type)) throw new AssetValidationException("ASSET_TYPE_NOT_SUPPORTED", "This asset type is not available.");
            query = query.Where(asset => asset.AssetType == type);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            if (search.Length > 120) throw new AssetValidationException("ASSET_SEARCH_TOO_LONG", "Search must be 120 characters or fewer.");
            query = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
                ? query.Where(asset => EF.Functions.ILike(asset.Name, $"%{search}%") || (asset.Description != null && EF.Functions.ILike(asset.Description, $"%{search}%")))
                : query.Where(asset => asset.Name.ToLower().Contains(search.ToLower()) || (asset.Description != null && asset.Description.ToLower().Contains(search.ToLower())));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(asset => asset.CreatedAt)
            .ThenBy(asset => asset.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new AssetListDto(items.Select(AssetContractMapper.ToDto).ToArray(), page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize));
    }

    public async Task<Asset?> UpdateAsync(Guid userId, Guid assetId, UpdateAssetRequest request, CancellationToken cancellationToken = default)
    {
        var asset = await Query(tracking: true).FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken);
        if (asset is null || !await access.IsMemberAsync(userId, asset.WorkspaceId, cancellationToken)) return null;
        if (string.IsNullOrWhiteSpace(request.Name)) throw new AssetValidationException("ASSET_NAME_REQUIRED", "Asset name is required.");
        if (request.ProjectId.HasValue && !await db.Projects.AnyAsync(project => project.Id == request.ProjectId && project.WorkspaceId == asset.WorkspaceId, cancellationToken))
            throw new AssetValidationException("PROJECT_NOT_IN_WORKSPACE", "The selected project is not in this workspace.");

        asset.Name = request.Name.Trim();
        asset.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        asset.ProjectId = request.ProjectId;
        asset.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (asset.ProjectId.HasValue) asset.Project = await db.Projects.AsNoTracking().FirstAsync(project => project.Id == asset.ProjectId, cancellationToken);
        else asset.Project = null;
        return asset;
    }

    public Task<Asset?> ArchiveAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(userId, assetId, AssetStatus.Archived, cancellationToken);

    public Task<Asset?> RestoreAsync(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
        SetStatusAsync(userId, assetId, AssetStatus.Active, cancellationToken);

    public async Task<AssetDownload?> GetDownloadAsync(Guid userId, Guid assetId, Guid? representationId = null, CancellationToken cancellationToken = default)
    {
        var asset = await Query().Include(item => item.StoredFile).FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken);
        if (asset is null || !await access.IsMemberAsync(userId, asset.WorkspaceId, cancellationToken)) return null;
        if (representationId.HasValue)
        {
            var representation = asset.Representations.FirstOrDefault(item => item.Id == representationId.Value);
            if (representation is null) return null;
            var representationFile = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(file => file.Id == representation.StoredFileId, cancellationToken);
            if (representationFile is null || representationFile.Status != StoredFileStatus.Ready)
                throw new AssetValidationException("ASSET_FILE_UNAVAILABLE", "This document format is not available.");
            return new AssetDownload(asset, representationFile, representation.FileName, representation.ContentType);
        }
        if (asset.StoredFile is null || asset.StoredFile.Status != StoredFileStatus.Ready)
            throw new AssetValidationException("ASSET_FILE_UNAVAILABLE", "This asset does not have an available file.");
        return new AssetDownload(asset, asset.StoredFile, asset.StoredFile.OriginalFileName, asset.StoredFile.ContentType);
    }

    private async Task<Asset?> SetStatusAsync(Guid userId, Guid assetId, AssetStatus status, CancellationToken cancellationToken)
    {
        var asset = await Query(tracking: true).FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken);
        if (asset is null || !await access.IsMemberAsync(userId, asset.WorkspaceId, cancellationToken)) return null;
        var now = DateTime.UtcNow;
        asset.Status = status;
        asset.ArchivedAt = status == AssetStatus.Archived ? now : null;
        asset.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return asset;
    }

    private IQueryable<Asset> Query(bool tracking = false)
    {
        var query = db.Assets.Include(asset => asset.Project).Include(asset => asset.Representations).AsQueryable();
        return tracking ? query : query.AsNoTracking();
    }
}

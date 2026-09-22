using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Assets;

public sealed record GeneratedFileArtifact(string FileName, string ContentType, ReadOnlyMemory<byte> Content);
public sealed record GeneratedAssetDescriptor(string Name, string? Description, string AssetType, string? MetadataJson = null);
public sealed record PreparedGenerationOutput(GenerationJobOutput Output, Asset? Asset, StoredFile? CreatedFile);

public interface IGeneratedAssetPublisher
{
    Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default);
    Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default);
}

public sealed class GeneratedAssetPublisher(TaslimDbContext db, FileProcessingService files, ILogger<GeneratedAssetPublisher> logger) : IGeneratedAssetPublisher
{
    public async Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default)
    {
        var assetType = output.Asset?.AssetType.Trim().ToLowerInvariant();
        if (assetType is not null && !AssetTypes.Supported.Contains(assetType)) throw new InvalidOperationException("Generated asset type is not supported.");
        StoredFile? createdFile = null;
        var storedFileId = output.StoredFileId;
        if (output.FileArtifact is not null)
        {
            createdFile = await files.StoreGeneratedAsync(
                job.WorkspaceId,
                job.CreatedByUserId,
                job.ProjectId,
                output.FileArtifact.FileName,
                output.FileArtifact.ContentType,
                output.FileArtifact.Content,
                cancellationToken);
            storedFileId = createdFile.Id;
        }

        StoredFile? storedFile = createdFile;
        if (storedFileId.HasValue && storedFile is null)
        {
            storedFile = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(
                file => file.Id == storedFileId && file.WorkspaceId == job.WorkspaceId && file.Status == StoredFileStatus.Ready,
                cancellationToken) ?? throw new InvalidOperationException("Generated output file is not available in the job workspace.");
        }

        var jobOutput = new GenerationJobOutput
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            StoredFileId = storedFileId,
            OutputType = output.OutputType,
            MetadataJson = output.MetadataJson,
            CreatedAt = DateTime.UtcNow,
        };

        Asset? asset = null;
        if (output.Asset is not null)
        {
            var now = DateTime.UtcNow;
            asset = new Asset
            {
                Id = Guid.NewGuid(),
                WorkspaceId = job.WorkspaceId,
                ProjectId = job.ProjectId,
                CreatedByUserId = job.CreatedByUserId,
                StoredFileId = storedFileId,
                SourceGenerationJobId = job.Id,
                Name = NormalizeName(output.Asset.Name),
                Description = NormalizeDescription(output.Asset.Description),
                AssetType = assetType!,
                MimeType = storedFile?.ContentType,
                Status = AssetStatus.Active,
                MetadataJson = output.Asset.MetadataJson,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        return new PreparedGenerationOutput(jobOutput, asset, createdFile);
    }

    public async Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default)
    {
        db.Entry(publication.Output).State = EntityState.Detached;
        if (publication.Asset is not null) db.Entry(publication.Asset).State = EntityState.Detached;
        if (publication.CreatedFile is not null && publication.CreatedFile.Status != StoredFileStatus.Deleted)
        {
            try { await files.DeleteAsync(publication.CreatedFile, cancellationToken); }
            catch (Exception exception)
            {
                publication.CreatedFile.Status = StoredFileStatus.Failed;
                publication.CreatedFile.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(exception, "Generated output cleanup could not remove private storage. FileId={FileId}; JobId={JobId}", publication.CreatedFile.Id, publication.Output.GenerationJobId);
            }
        }
    }

    private static string NormalizeName(string name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "Generated asset" : name.Trim();
        return value.Length <= 255 ? value : value[..255];
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var value = description.Trim();
        return value.Length <= 2_000 ? value : value[..2_000];
    }
}

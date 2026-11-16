using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Files;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;

namespace Taslim.Api.Assets;

public sealed record GeneratedFileArtifact(string FileName, string ContentType, ReadOnlyMemory<byte> Content, string? MetadataJson = null, string? RepresentationType = null);
public sealed record GeneratedStreamFileArtifact(string FileName, string ContentType, long SizeBytes, Func<CancellationToken, Task<Stream>> OpenReadAsync, string? MetadataJson = null);
public sealed record GeneratedAssetDescriptor(string Name, string? Description, string AssetType, string? MetadataJson = null);
public sealed record PreparedGenerationOutput(GenerationJobOutput Output, Asset? Asset, StoredFile? CreatedFile, GeneratedMediaProvenance? Provenance = null);

public interface IGeneratedAssetPublisher
{
    Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default);
    Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default);
}

public sealed class GeneratedAssetPublisher(
    TaslimDbContext db,
    FileProcessingService files,
    IGenerationQualityControl qualityControl,
    IGeneratedMediaRetentionHook retentionHook,
    ILogger<GeneratedAssetPublisher> logger) : IGeneratedAssetPublisher
{
    public async Task<PreparedGenerationOutput> PrepareAsync(GenerationJob job, GenerationHandlerOutput output, CancellationToken cancellationToken = default)
    {
        var assetType = output.Asset?.AssetType.Trim().ToLowerInvariant();
        if (assetType is not null && !AssetTypes.Supported.Contains(assetType)) throw new InvalidOperationException("Generated asset type is not supported.");
        var outputMetadata = GeneratedMediaSecurity.NormalizeMetadataJson(output.MetadataJson);
        var assetMetadata = GeneratedMediaSecurity.NormalizeMetadataJson(output.Asset?.MetadataJson);
        StoredFile? createdFile = null;
        StoredFile? storedFile = null;
        var storedFileId = output.StoredFileId;
        var storageResult = (GeneratedFileStorageResult?)null;
        if (output.FileArtifact is not null)
        {
            storageResult = await files.StoreGeneratedWithResultAsync(
                job.WorkspaceId,
                job.CreatedByUserId,
                job.ProjectId,
                output.FileArtifact.FileName,
                output.FileArtifact.ContentType,
                output.FileArtifact.Content,
                output.FileArtifact.MetadataJson,
                cancellationToken);
            storedFile = storageResult.File;
            createdFile = storageResult.WasCreated ? storageResult.File : null;
            storedFileId = storageResult.File.Id;
        }
        else if (output.StreamArtifact is not null)
        {
            storageResult = await files.StoreGeneratedStreamWithResultAsync(
                job.WorkspaceId,
                job.CreatedByUserId,
                job.ProjectId,
                output.StreamArtifact.FileName,
                output.StreamArtifact.ContentType,
                output.StreamArtifact.SizeBytes,
                output.StreamArtifact.OpenReadAsync,
                output.StreamArtifact.MetadataJson,
                cancellationToken);
            storedFile = storageResult.File;
            createdFile = storageResult.WasCreated ? storageResult.File : null;
            storedFileId = storageResult.File.Id;
        }

        if (storedFileId.HasValue && storedFile is null)
        {
            storedFile = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(
                file => file.Id == storedFileId
                    && file.WorkspaceId == job.WorkspaceId
                    && file.Status == StoredFileStatus.Ready
                    && file.ConversationId == null
                    && (!file.ProjectId.HasValue || file.ProjectId == job.ProjectId),
                cancellationToken) ?? throw new InvalidOperationException("Generated output file is not available in the job scope.");
        }
        if (output.Asset is not null && (storedFile is null || storedFile.Status != StoredFileStatus.Ready))
            throw new InvalidOperationException("Generated assets require a completed private stored file.");

        try
        {
            var quality = await qualityControl.ValidateAsync(job, output, cancellationToken);
            if (quality.IsFailure) throw new GenerationQualityControlException(quality);
        }
        catch
        {
            if (createdFile is not null)
            {
                try { await files.DeleteAsync(createdFile, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Generated file cleanup failed after QC rejection. FileId={FileId}; JobId={JobId}", createdFile.Id, job.Id); }
            }
            throw;
        }

        var jobOutput = new GenerationJobOutput
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            StoredFileId = storedFileId,
            OutputType = output.OutputType,
            MetadataJson = outputMetadata,
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
                MetadataJson = assetMetadata,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        GeneratedMediaProvenance? provenance = null;
        if (storedFile is not null && storedFile.ContentHashSha256 is not null)
        {
            var ownership = await ResolveMovieOwnershipAsync(job, cancellationToken);
            var parentHash = ReadParentHash(outputMetadata);
            var ingestionKey = $"{job.Id:N}:{output.OutputType}:{storedFile.ContentHashSha256}";
            var chainInput = string.Join(':', job.Id.ToString("N"), output.OutputType, storedFile.ContentHashSha256, parentHash ?? "root");
            provenance = new GeneratedMediaProvenance
            {
                Id = Guid.NewGuid(),
                WorkspaceId = job.WorkspaceId,
                StoredFileId = storedFile.Id,
                GenerationJobId = job.Id,
                AssetId = asset?.Id,
                MovieTakeId = ownership.TakeId,
                MovieClipId = ownership.ClipId,
                OutputType = output.OutputType,
                IngestionKey = ingestionKey,
                ContentHashSha256 = storedFile.ContentHashSha256,
                ParentContentHashSha256 = parentHash,
                ChainHashSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chainInput))),
                SafeMetadataJson = outputMetadata,
                CreatedAt = DateTime.UtcNow,
            };
        }

        return new PreparedGenerationOutput(jobOutput, asset, createdFile, provenance);
    }

    public async Task DiscardAsync(PreparedGenerationOutput publication, CancellationToken cancellationToken = default)
    {
        db.Entry(publication.Output).State = EntityState.Detached;
        if (publication.Asset is not null) db.Entry(publication.Asset).State = EntityState.Detached;
        if (publication.Provenance is not null) db.Entry(publication.Provenance).State = EntityState.Detached;
        if (publication.Asset is not null)
            foreach (var representation in publication.Asset.Representations)
                db.Entry(representation).State = EntityState.Detached;
        if (publication.CreatedFile is not null && publication.CreatedFile.Status != StoredFileStatus.Deleted)
        {
            try
            {
                await files.DeleteAsync(publication.CreatedFile, cancellationToken);
                if (publication.Provenance is not null)
                    await retentionHook.OnCleanupAsync(ToRetentionContext(publication, "publication_discarded"), CancellationToken.None);
            }
            catch (Exception exception)
            {
                publication.CreatedFile.Status = StoredFileStatus.Failed;
                publication.CreatedFile.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                if (publication.Provenance is not null)
                {
                    try { await retentionHook.OnCleanupAsync(ToRetentionContext(publication, "publication_cleanup_failed"), CancellationToken.None); }
                    catch (Exception hookException) { logger.LogWarning(hookException, "Generated media retention cleanup signaling was skipped. FileId={FileId}", publication.CreatedFile.Id); }
                }
                logger.LogWarning(exception, "Generated output cleanup could not remove private storage. FileId={FileId}; JobId={JobId}", publication.CreatedFile.Id, publication.Output.GenerationJobId);
            }
        }
    }

    private async Task<(Guid? ClipId, Guid? TakeId)> ResolveMovieOwnershipAsync(GenerationJob job, CancellationToken cancellationToken)
    {
        if (!GenerationJobTypes.MovieTypes.Contains(job.JobType)) return (null, null);
        MovieGenerationInput? input;
        try { input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson); }
        catch (JsonException) { return (null, null); }
        if (input is null || input.MovieClipId == Guid.Empty) return (null, null);

        var clip = await db.MovieClips.AsNoTracking().FirstOrDefaultAsync(item => item.Id == input.MovieClipId
            && item.MovieProjectId != Guid.Empty
            && item.GenerationJobId == job.Id
            && db.MovieProjects.Any(project => project.Id == item.MovieProjectId && project.WorkspaceId == job.WorkspaceId && project.ProjectId == job.ProjectId), cancellationToken);
        if (clip is null) return (null, null);

        var take = await db.MovieTakes.AsNoTracking().FirstOrDefaultAsync(item => item.GenerationJobId == job.Id
            && item.MovieClipId == clip.Id
            && (!input.MovieShotId.HasValue || item.MovieShotId == input.MovieShotId.Value), cancellationToken);
        return (clip.Id, take?.Id);
    }

    private static string? ReadParentHash(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty("parentContentHashSha256", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: 64 } hash
                && hash.All(Uri.IsHexDigit)
                ? hash.ToUpperInvariant()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static GeneratedMediaRetentionContext ToRetentionContext(PreparedGenerationOutput publication, string reason) =>
        new(
            publication.Provenance!.WorkspaceId,
            publication.Provenance.StoredFileId,
            publication.Provenance.GenerationJobId,
            publication.Provenance.AssetId,
            publication.Provenance.MovieTakeId,
            publication.Provenance.RetainUntil,
            reason);

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

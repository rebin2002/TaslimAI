using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Files;

public sealed class FileProcessingService(
    TaslimDbContext db,
    FileValidationService validation,
    IFileStorageService storage,
    IFileContentExtractor extractor,
    IOptions<FileOptions> options,
    ILogger<FileProcessingService> logger)
{
    private readonly FileOptions settings = options.Value;

    public async Task<StoredFile> UploadAsync(Guid workspaceId, Guid userId, Guid? projectId, Guid? conversationId, IFormFile upload, CancellationToken cancellationToken)
    {
        var validated = await validation.ValidateAsync(upload, cancellationToken);
        var id = Guid.NewGuid();
        var storedName = $"{id:N}{validated.Extension}";
        var storageKey = $"{workspaceId:N}/{id:N}/{storedName}";
        var now = DateTime.UtcNow;
        var file = new StoredFile
        {
            Id = id,
            WorkspaceId = workspaceId,
            UserId = userId,
            ProjectId = projectId,
            ConversationId = conversationId,
            OriginalFileName = validated.SafeFileName,
            StoredFileName = storedName,
            ContentType = validated.ContentType,
            Extension = validated.Extension,
            SizeBytes = validated.SizeBytes,
            StorageProvider = storage.ProviderKey,
            StorageKey = storageKey,
            Status = StoredFileStatus.Uploading,
            CreatedAt = now,
            TextExtractionStatus = FileContentTypes.IsImage(validated.Extension)
                ? FileExtractionStatus.NotApplicable
                : FileExtractionStatus.NotStarted,
        };
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await using (var input = upload.OpenReadStream())
            {
                await storage.StoreAsync(storageKey, input, cancellationToken);
            }

            file.Status = StoredFileStatus.Processing;
            file.TextExtractionStatus = FileContentTypes.IsImage(file.Extension)
                ? FileExtractionStatus.NotApplicable
                : FileExtractionStatus.Processing;
            await db.SaveChangesAsync(cancellationToken);

            if (extractor.CanHandle(file.Extension))
            {
                await using var input = await storage.OpenReadAsync(storageKey, cancellationToken) ?? throw new FileStorageUnavailableException();
                var result = await extractor.ExtractAsync(file.Extension, input, cancellationToken);
                file.TextExtractionStatus = result.Status;
                file.ExtractedText = result.ExtractedText;
                file.ExtractedTextLength = result.ExtractedTextLength;
                file.MetadataJson = result.MetadataJson;
                file.ProcessedAt = DateTime.UtcNow;
                file.Status = result.Status == FileExtractionStatus.Failed ? StoredFileStatus.Failed : StoredFileStatus.Ready;
            }
            else
            {
                file.ProcessedAt = DateTime.UtcNow;
                file.Status = StoredFileStatus.Ready;
            }
            await db.SaveChangesAsync(cancellationToken);
            return file;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileStorageUnavailableException)
        {
            file.Status = StoredFileStatus.Failed;
            file.TextExtractionStatus = FileExtractionStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            file.Status = StoredFileStatus.Failed;
            file.TextExtractionStatus = FileExtractionStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "File processing failed without logging file contents. FileId={FileId}; WorkspaceId={WorkspaceId}", file.Id, workspaceId);
            return file;
        }
    }

    public async Task<bool> DeleteAsync(StoredFile file, CancellationToken cancellationToken)
    {
        await storage.DeleteAsync(file.StorageKey, cancellationToken);
        file.Status = StoredFileStatus.Deleted;
        file.ExtractedText = null;
        file.ExtractedTextLength = null;
        file.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

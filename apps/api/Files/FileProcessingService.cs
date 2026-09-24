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

    public async Task<StoredFile> StoreGeneratedAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        string fileName,
        string contentType,
        ReadOnlyMemory<byte> content,
        string? metadataJson,
        CancellationToken cancellationToken)
        {
        if (content.Length <= 0 || content.Length > Math.Min(settings.MaxFileSizeBytes, 25 * 1_048_576))
            throw new FileUploadValidationException("Generated files must be between 1 byte and 25 MB.");
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 160)
            throw new FileUploadValidationException("Generated file content type is invalid.");
        if (metadataJson?.Length > 16_000)
            throw new FileUploadValidationException("Generated file metadata is too large.");

        var safeName = FileValidationService.SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 20)
            throw new FileUploadValidationException("Generated file extension is invalid.");

        var id = Guid.NewGuid();
        var storedName = $"{id:N}{extension}";
        var storageKey = $"{workspaceId:N}/{id:N}/{storedName}";
        var now = DateTime.UtcNow;
        var file = new StoredFile
        {
            Id = id,
            WorkspaceId = workspaceId,
            UserId = userId,
            ProjectId = projectId,
            OriginalFileName = safeName,
            StoredFileName = storedName,
            ContentType = contentType,
            Extension = extension,
            SizeBytes = content.Length,
            StorageProvider = storage.ProviderKey,
            StorageKey = storageKey,
            Status = StoredFileStatus.Uploading,
            CreatedAt = now,
            TextExtractionStatus = FileExtractionStatus.NotApplicable,
            MetadataJson = metadataJson,
        };
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            logger.LogInformation("Generated file storage started. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}; SizeBytes={SizeBytes}", file.Id, workspaceId, storage.ProviderKey, content.Length);
            await using var input = new MemoryStream(content.ToArray(), writable: false);
            await storage.StoreAsync(storageKey, input, cancellationToken);
            file.Status = StoredFileStatus.Ready;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return file;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Generated file storage cancelled. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}", file.Id, workspaceId, storage.ProviderKey);
            file.Status = StoredFileStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await TryDeleteGeneratedObjectAsync(storageKey, file.Id, workspaceId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Generated file storage failed. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}; FailureCategory={FailureCategory}", file.Id, workspaceId, storage.ProviderKey, exception is FileStorageUnavailableException ? "unavailable" : "operation_failed");
            file.Status = StoredFileStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await TryDeleteGeneratedObjectAsync(storageKey, file.Id, workspaceId);
            throw;
        }
    }

    private async Task TryDeleteGeneratedObjectAsync(string storageKey, Guid fileId, Guid workspaceId)
    {
        try { await storage.DeleteAsync(storageKey, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Generated file cleanup failed. FileId={FileId}; WorkspaceId={WorkspaceId}", fileId, workspaceId); }
    }

    public async Task<StoredFile> StoreGeneratedStreamAsync(
        Guid workspaceId,
        Guid userId,
        Guid? projectId,
        string fileName,
        string contentType,
        long sizeBytes,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        if (sizeBytes <= 0 || sizeBytes > Math.Max(1, settings.MaxGeneratedVideoBytes))
            throw new FileUploadValidationException("Generated video output is outside the configured size limit.");
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || contentType.Length > 160)
            throw new FileUploadValidationException("Generated video content type is invalid.");
        if (metadataJson?.Length > 16_000)
            throw new FileUploadValidationException("Generated file metadata is too large.");

        var safeName = FileValidationService.SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 20)
            throw new FileUploadValidationException("Generated video file extension is invalid.");

        var id = Guid.NewGuid();
        var storedName = $"{id:N}{extension}";
        var storageKey = $"{workspaceId:N}/{id:N}/{storedName}";
        var now = DateTime.UtcNow;
        var file = new StoredFile
        {
            Id = id,
            WorkspaceId = workspaceId,
            UserId = userId,
            ProjectId = projectId,
            OriginalFileName = safeName,
            StoredFileName = storedName,
            ContentType = contentType,
            Extension = extension,
            SizeBytes = sizeBytes,
            StorageProvider = storage.ProviderKey,
            StorageKey = storageKey,
            Status = StoredFileStatus.Uploading,
            CreatedAt = now,
            TextExtractionStatus = FileExtractionStatus.NotApplicable,
            MetadataJson = metadataJson,
        };
        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            logger.LogInformation("Generated stream storage started. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}; SizeBytes={SizeBytes}", file.Id, workspaceId, storage.ProviderKey, sizeBytes);
            await using var input = await openReadAsync(cancellationToken);
            await using var bounded = new CountingReadStream(input, Math.Max(1, settings.MaxGeneratedVideoBytes));
            await storage.StoreAsync(storageKey, bounded, cancellationToken);
            if (bounded.BytesRead != sizeBytes)
                throw new FileUploadValidationException("Generated video output size did not match its declared size.");
            file.Status = StoredFileStatus.Ready;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return file;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Generated stream storage cancelled. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}", file.Id, workspaceId, storage.ProviderKey);
            file.Status = StoredFileStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await TryDeleteGeneratedObjectAsync(storageKey, file.Id, workspaceId);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Generated stream storage failed. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}; FailureCategory={FailureCategory}", file.Id, workspaceId, storage.ProviderKey, exception is FileStorageUnavailableException ? "unavailable" : "operation_failed");
            file.Status = StoredFileStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await TryDeleteGeneratedObjectAsync(storageKey, file.Id, workspaceId);
            throw;
        }
    }

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
            logger.LogInformation("File upload started. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}; Extension={Extension}; SizeBytes={SizeBytes}", file.Id, workspaceId, storage.ProviderKey, file.Extension, file.SizeBytes);
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
                if (result.Status == FileExtractionStatus.Failed)
                    logger.LogWarning("File extraction failed. FileId={FileId}; WorkspaceId={WorkspaceId}; Extension={Extension}; FailureCode={FailureCode}", file.Id, workspaceId, file.Extension, result.FailureCode);
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
            logger.LogWarning("File storage is unavailable. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}", file.Id, workspaceId, storage.ProviderKey);
            file.Status = StoredFileStatus.Failed;
            file.TextExtractionStatus = FileExtractionStatus.Failed;
            file.ProcessedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (FileStorageOperationException exception)
        {
            logger.LogWarning(exception, "File storage operation failed. FileId={FileId}; WorkspaceId={WorkspaceId}; StorageProvider={StorageProvider}", file.Id, workspaceId, storage.ProviderKey);
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

internal sealed class CountingReadStream(Stream inner, long maxBytes) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, Math.Min(count, RemainingBufferSize(count)));
        Record(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..Math.Min(buffer.Length, RemainingBufferSize(buffer.Length))]);
        Record(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (BytesRead >= maxBytes)
        {
            var probe = new byte[1];
            var extra = await inner.ReadAsync(probe.AsMemory(), cancellationToken);
            if (extra > 0) throw new InvalidDataException("Generated stream exceeded the configured size limit.");
            return 0;
        }
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, RemainingBufferSize(buffer.Length))], cancellationToken);
        Record(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int RemainingBufferSize(int requested) => (int)Math.Min(requested, maxBytes - BytesRead);
    private void Record(int read) => BytesRead += read;
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

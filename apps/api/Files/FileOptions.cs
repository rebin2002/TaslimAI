using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed class FileOptions
{
    public long MaxFileSizeBytes { get; set; } = 25 * 1024 * 1024;
    public int MaxAttachmentsPerMessage { get; set; } = 5;
    public int FileContextBudgetTokens { get; set; } = 4_000;
    public int MaxExtractedTextCharacters { get; set; } = 80_000;
    public string StorageProvider { get; set; } = FileStorageProviders.Local;
    public string LocalRootPath { get; set; } = "App_Data/files";
    public string S3Endpoint { get; set; } = string.Empty;
    public string S3Region { get; set; } = "auto";
    public string S3Bucket { get; set; } = string.Empty;
    public string S3AccessKey { get; set; } = string.Empty;
    public string S3SecretKey { get; set; } = string.Empty;

    public bool IsS3Configured =>
        Uri.TryCreate(S3Endpoint, UriKind.Absolute, out var endpoint)
        && string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(S3Bucket)
        && !string.IsNullOrWhiteSpace(S3AccessKey)
        && !string.IsNullOrWhiteSpace(S3SecretKey)
        && !string.IsNullOrWhiteSpace(S3Region);
}

public sealed record StoredFileDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? ConversationId,
    string OriginalFileName,
    string ContentType,
    string Extension,
    long SizeBytes,
    string StorageProvider,
    string Status,
    string TextExtractionStatus,
    int? ExtractedTextLength,
    DateTime CreatedAt,
    DateTime? ProcessedAt);

public sealed record FileValidationResult(
    string SafeFileName,
    string Extension,
    string ContentType,
    long SizeBytes);

public interface IFileStorageService
{
    string ProviderKey { get; }
    Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default);
}

public sealed class FileStorageUnavailableException() : Exception("Persistent file storage is not configured.");

public sealed class FileStorageOperationException() : Exception("Persistent file storage operation failed.");

public sealed class FileContentUnavailableException() : Exception("Attached file content is unavailable.");

public sealed class LocalFileStorageService(IOptions<FileOptions> options, ILogger<LocalFileStorageService> logger) : IFileStorageService
{
    private readonly string rootPath = Path.GetFullPath(options.Value.LocalRootPath, AppContext.BaseDirectory);

    public string ProviderKey => FileStorageProviders.Local;

    public async Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await content.CopyToAsync(output, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ResolvePath(storageKey)));

    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Contains('\0')) throw new InvalidOperationException("Invalid storage key.");
        var path = Path.GetFullPath(storageKey.Replace('/', Path.DirectorySeparatorChar), rootPath);
        if (!path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(path, rootPath, StringComparison.Ordinal))
        {
            logger.LogWarning("Rejected unsafe local storage path.");
            throw new InvalidOperationException("Invalid storage key.");
        }
        return path;
    }
}

public sealed class UnconfiguredFileStorageService(IOptions<FileOptions> options) : IFileStorageService
{
    public string ProviderKey => options.Value.StorageProvider;
    public Task StoreAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => throw new FileStorageUnavailableException();
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => throw new FileStorageUnavailableException();
    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => throw new FileStorageUnavailableException();
    public Task<bool> ExistsAsync(string storageKey, CancellationToken cancellationToken = default) => throw new FileStorageUnavailableException();
}

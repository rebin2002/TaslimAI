using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;
using Taslim.Api.Files;

namespace Taslim.Api.Contracts;

public sealed record FileAttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    string Extension,
    long SizeBytes,
    string Status,
    string TextExtractionStatus,
    int? ExtractedTextLength,
    DateTime CreatedAt);

public sealed class SendMessageWithAttachmentsRequest
{
    [Required, StringLength(ChatMessageLimits.MaximumContentLength, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;

    [StringLength(80)]
    public string? RequestId { get; set; }

    public string? AttachmentIdsJson { get; set; }
}

public static class FileApiLimits
{
    public const int MaximumOriginalNameLength = 255;
}

public static class FileDtoMapper
{
    public static StoredFileDto ToStoredFileDto(StoredFile file) => new(
        file.Id,
        file.WorkspaceId,
        file.ProjectId,
        file.ConversationId,
        file.OriginalFileName,
        file.ContentType,
        file.Extension,
        file.SizeBytes,
        file.StorageProvider,
        file.Status.ToString(),
        file.TextExtractionStatus.ToString(),
        file.ExtractedTextLength,
        file.CreatedAt,
        file.ProcessedAt);

    public static FileAttachmentDto ToAttachmentDto(StoredFile file) => new(
        file.Id,
        file.OriginalFileName,
        file.ContentType,
        file.Extension,
        file.SizeBytes,
        file.Status.ToString(),
        file.TextExtractionStatus.ToString(),
        file.ExtractedTextLength,
        file.CreatedAt);
}

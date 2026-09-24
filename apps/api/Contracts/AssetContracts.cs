using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public sealed class UpdateAssetRequest
{
    [Required, StringLength(255, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2_000)]
    public string? Description { get; set; }

    public Guid? ProjectId { get; set; }
}

public sealed record AssetDto(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string? ProjectName,
    string Name,
    string? Description,
    string AssetType,
    string? MimeType,
    string Status,
    bool HasFile,
    bool CanPreview,
    long? FileSizeBytes,
    string? SourceStudio,
    string? SourceJobTitle,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ArchivedAt,
    IReadOnlyList<AssetRepresentationDto> Representations);

public sealed record AssetRepresentationDto(
    Guid Id,
    string RepresentationType,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime CreatedAt);

public sealed record AssetListDto(
    IReadOnlyList<AssetDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record AssetFilter(
    Guid WorkspaceId,
    Guid? ProjectId,
    string? AssetType,
    AssetStatus? Status,
    string? Search,
    string? Sort,
    int Page = 1,
    int PageSize = 24);

public static class AssetContractMapper
{
    public static AssetDto ToDto(Asset asset) => new(
        asset.Id,
        asset.WorkspaceId,
        asset.ProjectId,
        asset.Project?.Name,
        asset.Name,
        asset.Description,
        asset.AssetType,
        asset.MimeType,
        asset.Status.ToString(),
        asset.StoredFileId.HasValue,
        asset.StoredFileId.HasValue && (asset.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            || asset.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
            || asset.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true),
        asset.StoredFile?.SizeBytes,
        SourceStudioKey(asset.SourceGenerationJob?.JobType),
        asset.SourceGenerationJob?.Title,
        asset.CreatedAt,
        asset.UpdatedAt,
        asset.ArchivedAt,
        asset.Representations.OrderBy(item => item.RepresentationType)
            .Select(item => new AssetRepresentationDto(item.Id, item.RepresentationType, item.FileName, item.ContentType, item.SizeBytes, item.CreatedAt))
            .ToArray());

    private static string? SourceStudioKey(string? jobType) => jobType?.ToLowerInvariant() switch
    {
        "image.generate" => "image",
        "document.generate" => "document",
        "presentation.generate" => "presentation",
        "research.generate" => "research",
        "social.generate" => "social",
        "voice.generate" => "voice",
        "music.generate" => "music",
        "movie.quick.generate" or "movie.clip.generate" or "movie.assembly" => "movie",
        "system.test" => "system",
        _ => null,
    };
}

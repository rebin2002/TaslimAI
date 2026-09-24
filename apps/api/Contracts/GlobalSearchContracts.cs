namespace Taslim.Api.Contracts;

public static class GlobalSearchResultTypes
{
    public const string Project = "projects";
    public const string Conversation = "conversations";
    public const string Asset = "assets";
    public const string File = "files";
    public const string Generation = "generation";
}

public sealed record GlobalSearchResultDto(
    string Type,
    Guid Id,
    string Title,
    string? Description,
    Guid? ProjectId,
    Guid? ConversationId,
    Guid? AssetId,
    string? ProjectName,
    string? Status,
    string? Metadata,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record GlobalSearchGroupDto(
    string Type,
    int Count,
    IReadOnlyList<GlobalSearchResultDto> Items);

public sealed record GlobalSearchResponseDto(
    string Query,
    int TotalCount,
    IReadOnlyList<GlobalSearchGroupDto> Groups);

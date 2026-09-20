using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public sealed record PersonalMemoryDto(
    Guid Id,
    Guid WorkspaceId,
    string Category,
    string Title,
    string Content,
    string Source,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed class CreatePersonalMemoryRequest
{
    [Required, StringLength(30)]
    public string Category { get; set; } = PersonalMemoryCategories.Other;

    [Required, StringLength(160, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(4000, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;
}

public sealed class UpdatePersonalMemoryRequest
{
    [Required, StringLength(30)]
    public string Category { get; set; } = PersonalMemoryCategories.Other;

    [Required, StringLength(160, MinimumLength = 1)]
    public string Title { get; set; } = string.Empty;

    [Required, StringLength(4000, MinimumLength = 1)]
    public string Content { get; set; } = string.Empty;
}

using System.ComponentModel.DataAnnotations;
using Taslim.Api.Domain;

namespace Taslim.Api.Contracts;

public sealed record ErrorEnvelope(ErrorBody Error);
public sealed record ErrorBody(string Code, string Message);

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string PreferredLanguage,
    Guid PersonalWorkspaceId,
    DateTime CreatedAt);

public sealed record WorkspaceSummaryDto(Guid Id, string Name, string Slug, string Type, string Role);

public sealed record AuthResponse(UserDto User, WorkspaceSummaryDto PersonalWorkspace);

public sealed class RegisterRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(128, MinimumLength = 10)]
    public string Password { get; set; } = string.Empty;

    [StringLength(5)]
    public string? PreferredLanguage { get; set; }
}

public sealed class LoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class UpdateProfileRequest
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, StringLength(5)]
    public string PreferredLanguage { get; set; } = LanguageCodes.English;
}

public sealed record ProjectDto(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string? Description,
    string Type,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ArchivedAt);

public sealed class CreateProjectRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(50)]
    public string? Type { get; set; }
}

public sealed class UpdateProjectRequest
{
    [Required, StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [StringLength(50)]
    public string? Type { get; set; }
}

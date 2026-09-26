using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed class MovieStoryRevisionRequest
{
    public string Premise { get; set; } = string.Empty;
    public string Logline { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string Treatment { get; set; } = string.Empty;
    public string Authorship { get; set; } = MovieStoryAuthorship.Human;
    public Guid? ParentRevisionId { get; set; }
    public string? ChangeSummary { get; set; }
    public IReadOnlyList<MovieStorySceneRequest> Scenes { get; set; } = [];
}

public sealed class MovieStorySceneRequest
{
    public string SceneIdentifier { get; set; } = string.Empty;
    public int? ActNumber { get; set; }
    public int? SequenceNumber { get; set; }
    public Guid? MovieSceneId { get; set; }
    public string Slugline { get; set; } = string.Empty;
    public string? Synopsis { get; set; }
    public IReadOnlyList<MovieScreenplayElementRequest> Elements { get; set; } = [];
}

public sealed class MovieScreenplayElementRequest
{
    public string ElementType { get; set; } = MovieScreenplayElementTypes.Action;
    public string Content { get; set; } = string.Empty;
    public string? CharacterName { get; set; }
    public string? Parenthetical { get; set; }
}

public sealed class MovieStoryRejectRequest
{
    public string Reason { get; set; } = string.Empty;
}

public sealed record MovieScreenplayElementDto(
    Guid Id,
    int Ordinal,
    string ElementType,
    string Content,
    string? CharacterName,
    string? Parenthetical);

public sealed record MovieScreenplaySceneDto(
    Guid Id,
    int Ordinal,
    string SceneIdentifier,
    int? ActNumber,
    int? SequenceNumber,
    Guid? MovieSceneId,
    string Slugline,
    string? Synopsis,
    IReadOnlyList<MovieScreenplayElementDto> Elements);

public sealed record MovieStoryRevisionDto(
    Guid Id,
    int RevisionNumber,
    Guid? ParentRevisionId,
    Guid CreatedByUserId,
    string Premise,
    string Logline,
    string Synopsis,
    string Treatment,
    string Status,
    string Authorship,
    string? ChangeSummary,
    string? RejectionReason,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SubmittedAt,
    DateTime? ApprovedAt,
    Guid? ApprovedByUserId,
    IReadOnlyList<MovieScreenplaySceneDto> Scenes);

public sealed record MovieStoryRevisionSummaryDto(
    Guid Id,
    int RevisionNumber,
    string Status,
    string Authorship,
    string? ChangeSummary,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? ApprovedAt);

public sealed record MovieStoryDto(
    Guid Id,
    Guid MovieProjectId,
    Guid WorkspaceId,
    string Premise,
    string Logline,
    string Synopsis,
    string Treatment,
    string ApprovalState,
    Guid? CurrentRevisionId,
    Guid? ApprovedRevisionId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    MovieStoryRevisionDto? CurrentRevision,
    MovieStoryRevisionDto? ApprovedRevision,
    IReadOnlyList<MovieStoryRevisionSummaryDto> Revisions,
    bool CanEdit,
    bool CanApprove);

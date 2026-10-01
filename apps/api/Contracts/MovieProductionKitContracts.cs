namespace Taslim.Api.Contracts;

public sealed class MovieProductionKitRevisionRequest
{
    public int? SourceGuideRevisionNumber { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<MovieProductionKitReferenceRequest>? References { get; set; }
}

public sealed class MovieProductionKitReferenceRequest
{
    public string ReferenceType { get; set; } = string.Empty;
    // SourceType is an additive alias for clients that use source-oriented terminology.
    public string? SourceType { get; set; }
    public Guid SourceId { get; set; }
    public int? SourceRevision { get; set; }
    public string? Label { get; set; }
    public string? Role { get; set; }
    public bool IsRequired { get; set; } = true;
    public string? ProvenanceJson { get; set; }
    public int? SortOrder { get; set; }
}

public sealed class MovieProductionKitReviewRequest
{
    public int? RevisionNumber { get; set; }
    public string? Note { get; set; }
}

public sealed record MovieProductionKitReferenceDto(
    Guid Id,
    string ReferenceType,
    Guid SourceId,
    int? SourceRevision,
    string? Label,
    string? Role,
    bool IsRequired,
    string SourceHash,
    string ProvenanceJson,
    int SortOrder);

public sealed record MovieProductionKitRevisionDto(
    Guid Id,
    int RevisionNumber,
    string Status,
    Guid SourceGuideRevisionId,
    int SourceGuideRevisionNumber,
    string SourceGuideHash,
    string? Notes,
    string? ReviewNote,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    Guid? ReviewedByUserId,
    DateTime? ReviewedAt,
    Guid? LockedByUserId,
    DateTime? LockedAt,
    string RevisionHash,
    IReadOnlyList<MovieProductionKitReferenceDto> References);

public sealed record MovieProductionKitReadinessDto(
    bool Ready,
    string Status,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings,
    int ReferenceCount,
    int RequiredReferenceCount,
    int ResolvedReferenceCount,
    bool SourceGuideLocked,
    int SourceGuideRevisionNumber);

public sealed record MovieProductionKitDto(
    Guid Id,
    Guid MovieProjectId,
    int CurrentRevisionNumber,
    int? LockedRevisionNumber,
    DateTime? LockedAt,
    Guid? LockedByUserId,
    MovieProductionKitRevisionDto? CurrentRevision,
    MovieProductionKitReadinessDto Readiness,
    IReadOnlyList<MovieProductionKitRevisionDto> Revisions);

using Taslim.Api.Movies;

namespace Taslim.Api.Contracts;

public sealed class MovieTakeUpscaleRequest
{
    public string TargetMasterResolution { get; set; } = MovieUpscaleResolutionCatalog.P4K;
    public string? SourceResolution { get; set; }
}

public sealed record MovieTakeUpscaleEligibilityDto(
    Guid TakeId,
    Guid MovieShotId,
    bool Eligible,
    bool IsSelected,
    bool IsFinal,
    string Code,
    string Message,
    string TargetMasterResolution,
    string? SourceResolution,
    Guid? AuditId,
    string? AuditStatus,
    DateTime EvaluatedAt);

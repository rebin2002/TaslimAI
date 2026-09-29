using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

/// <summary>
/// Provider-neutral master targets. This vocabulary is intentionally compatible
/// with the adaptive-resolution contract without taking a dependency on Wave 2.
/// </summary>
public static class MovieUpscaleResolutionCatalog
{
    public const string P1080 = "1080p";
    public const string P2K = "2k";
    public const string P4K = "4k";

    public static readonly IReadOnlySet<string> MasterResolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        P1080, P2K, P4K,
    };

    public static readonly IReadOnlySet<string> SourceResolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "480p", "720p", P1080, "1440p", "2160p",
    };
}

public static class MovieTakeUpscaleAuditStatuses
{
    public const string PendingExecution = "PendingExecution";
    public const string Blocked = "Blocked";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public static class MovieTakeUpscaleEligibilityCodes
{
    public const string Eligible = "eligible";
    public const string AlreadyRequested = "already_requested";
    public const string InvalidMasterResolution = "invalid_master_resolution";
    public const string TakeNotSelected = "take_not_selected";
    public const string TakeNotReady = "take_not_ready";
}

/// <summary>
/// Append-only evidence for every premium-upscale eligibility request. A
/// PendingExecution row is a hand-off record only; it does not create a
/// GenerationJob or invoke a media provider.
/// </summary>
public sealed class MovieTakeUpscaleAudit
{
    public Guid Id { get; set; }
    public Guid MovieTakeId { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid? SourceAssetId { get; set; }
    public string TargetMasterResolution { get; set; } = MovieUpscaleResolutionCatalog.P4K;
    public string? SourceResolution { get; set; }
    public string Status { get; set; } = MovieTakeUpscaleAuditStatuses.PendingExecution;
    public string EligibilityCode { get; set; } = MovieTakeUpscaleEligibilityCodes.Eligible;
    public bool WasSelected { get; set; }
    public bool WasFinal { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public Guid? GenerationJobId { get; set; }
    public string? OutcomeNote { get; set; }

    public MovieTake MovieTake { get; set; } = null!;
    public MovieProject MovieProject { get; set; } = null!;
    public ApplicationUser RequestedByUser { get; set; } = null!;
}

public sealed record MovieTakeUpscaleEligibilityDecision(
    bool Eligible,
    bool IsSelected,
    bool IsFinal,
    string Code,
    string Message,
    string TargetMasterResolution);

public static class MovieTakeUpscaleEligibilityEvaluator
{
    private static readonly IReadOnlySet<string> ReadyStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MovieTakeStatuses.Ready,
        MovieTakeStatuses.ReviewRequired,
        MovieTakeStatuses.Approved,
    };

    public static MovieTakeUpscaleEligibilityDecision Evaluate(MovieTake take, MovieShot shot, string? targetMasterResolution)
    {
        var target = targetMasterResolution?.Trim().ToLowerInvariant() ?? string.Empty;
        if (target.Length > 20) target = target[..20];
        var isSelected = shot.SelectedTakeId == take.Id;
        var isFinal = shot.FinalTakeId == take.Id;
        if (!MovieUpscaleResolutionCatalog.MasterResolutions.Contains(target))
            return new(false, isSelected, isFinal, MovieTakeUpscaleEligibilityCodes.InvalidMasterResolution, "Choose a supported master resolution.", target);
        if (!isSelected && !isFinal)
            return new(false, false, false, MovieTakeUpscaleEligibilityCodes.TakeNotSelected, "Only the explicitly selected or final take can be sent for premium mastering.", target);
        if (!ReadyStatuses.Contains(take.Status))
            return new(false, isSelected, isFinal, MovieTakeUpscaleEligibilityCodes.TakeNotReady, "The selected take is not ready for premium mastering.", target);
        return new(true, isSelected, isFinal, MovieTakeUpscaleEligibilityCodes.Eligible, "The selected take is eligible for premium mastering.", target);
    }
}

public interface IMovieTakeUpscaleEligibilityService
{
    Task<MovieTakeUpscaleEligibilityDto?> EvaluateAsync(Guid userId, Guid takeId, string? targetMasterResolution, CancellationToken cancellationToken);
    Task<MovieTakeUpscaleEligibilityDto?> RequestAsync(Guid userId, Guid takeId, MovieTakeUpscaleRequest request, CancellationToken cancellationToken);
}

public sealed class MovieTakeUpscaleEligibilityService(
    TaslimDbContext db,
    WorkspaceAccessService access,
    MovieCollaborationAccess? collaboration = null) : IMovieTakeUpscaleEligibilityService
{
    public async Task<MovieTakeUpscaleEligibilityDto?> EvaluateAsync(Guid userId, Guid takeId, string? targetMasterResolution, CancellationToken cancellationToken)
    {
        var context = await LoadAuthorizedTakeAsync(userId, takeId, MoviePermissions.View, cancellationToken);
        if (context is null) return null;
        var decision = MovieTakeUpscaleEligibilityEvaluator.Evaluate(context.Take, context.Take.MovieShot, targetMasterResolution);
        return ToDto(context.Take, decision, null, null, DateTime.UtcNow);
    }

    public async Task<MovieTakeUpscaleEligibilityDto?> RequestAsync(Guid userId, Guid takeId, MovieTakeUpscaleRequest request, CancellationToken cancellationToken)
    {
        var context = await LoadAuthorizedTakeAsync(userId, takeId, MovieOperationalPolicies.RequiredPermission(MovieOperationalActions.TakeUpscale), cancellationToken);
        if (context is null) return null;

        var decision = MovieTakeUpscaleEligibilityEvaluator.Evaluate(context.Take, context.Take.MovieShot, request.TargetMasterResolution);
        var sourceResolution = NormalizeSourceResolution(request.SourceResolution);
        var existing = decision.Eligible
            ? await db.MovieTakeUpscaleAudits.AsNoTracking()
                .Where(item => item.MovieTakeId == takeId
                    && item.TargetMasterResolution == decision.TargetMasterResolution
                    && (item.Status == MovieTakeUpscaleAuditStatuses.PendingExecution || item.Status == MovieTakeUpscaleAuditStatuses.Completed))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (existing is not null)
        {
            var alreadyRequested = decision with
            {
                Code = MovieTakeUpscaleEligibilityCodes.AlreadyRequested,
                Message = "Premium mastering has already been requested for this take and target.",
            };
            return ToDto(context.Take, alreadyRequested, existing.Id, existing.Status, existing.CreatedAt);
        }

        var now = DateTime.UtcNow;
        var audit = new MovieTakeUpscaleAudit
        {
            Id = Guid.NewGuid(),
            MovieTakeId = context.Take.Id,
            MovieProjectId = context.ProjectId,
            RequestedByUserId = userId,
            SourceAssetId = context.Take.AssetId,
            TargetMasterResolution = decision.TargetMasterResolution,
            SourceResolution = sourceResolution,
            Status = decision.Eligible ? MovieTakeUpscaleAuditStatuses.PendingExecution : MovieTakeUpscaleAuditStatuses.Blocked,
            EligibilityCode = decision.Code,
            WasSelected = decision.IsSelected,
            WasFinal = decision.IsFinal,
            CreatedAt = now,
            OutcomeNote = decision.Message,
        };
        db.MovieTakeUpscaleAudits.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(context.Take, decision, audit.Id, audit.Status, now, sourceResolution);
    }

    private async Task<AuthorizedTake?> LoadAuthorizedTakeAsync(Guid userId, Guid takeId, string permission, CancellationToken cancellationToken)
    {
        var take = await db.MovieTakes
            .Include(item => item.MovieShot)
            .ThenInclude(item => item.Scene)
            .ThenInclude(item => item.MovieProject)
            .FirstOrDefaultAsync(item => item.Id == takeId, cancellationToken);
        if (take is null) return null;
        var project = take.MovieShot.Scene.MovieProject;
        var authorized = collaboration is not null
            ? await collaboration.HasPermissionAsync(userId, project.Id, permission, cancellationToken)
            : await access.IsMemberAsync(userId, project.WorkspaceId, cancellationToken);
        return authorized ? new AuthorizedTake(take, project.Id) : null;
    }

    private static string? NormalizeSourceResolution(string? sourceResolution)
    {
        if (string.IsNullOrWhiteSpace(sourceResolution)) return null;
        var normalized = sourceResolution.Trim().ToLowerInvariant();
        return MovieUpscaleResolutionCatalog.SourceResolutions.Contains(normalized) ? normalized : null;
    }

    private static MovieTakeUpscaleEligibilityDto ToDto(
        MovieTake take,
        MovieTakeUpscaleEligibilityDecision decision,
        Guid? auditId,
        string? auditStatus,
        DateTime evaluatedAt,
        string? sourceResolution = null) => new(
            take.Id,
            take.MovieShotId,
            decision.Eligible,
            decision.IsSelected,
            decision.IsFinal,
            decision.Code,
            decision.Message,
            decision.TargetMasterResolution,
            sourceResolution,
            auditId,
            auditStatus,
            evaluatedAt);

    private sealed record AuthorizedTake(MovieTake Take, Guid ProjectId);
}

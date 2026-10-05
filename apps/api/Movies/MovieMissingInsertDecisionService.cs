using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public interface IMovieMissingInsertDecisionService
{
    Task<IReadOnlyList<MovieMissingInsertDecisionDto>?> ListAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MovieMissingInsertDecisionDto>?> MaterializeAsync(Guid userId, Guid movieProjectId, MovieMissingInsertMaterializeRequest request, CancellationToken cancellationToken);
    Task<MovieMissingInsertDecisionDto?> ReviewAsync(Guid userId, Guid decisionId, MovieMissingInsertReviewRequest request, CancellationToken cancellationToken);
    Task<MovieMissingInsertDecisionDto?> ApplyAsync(Guid userId, Guid decisionId, CancellationToken cancellationToken);
}

public sealed class MovieMissingInsertDecisionService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IMovieMissingInsertPlannerService planner,
    IMovieProductionReferencePackageService productionReferences,
    IMovieTimelineService timeline) : IMovieMissingInsertDecisionService
{
    private const int MaxReviewCommentLength = 4_000;
    private const int MaxGroundingJsonLength = 4_000;
    private const int MaxAnchorJsonLength = 8_000;

    public async Task<IReadOnlyList<MovieMissingInsertDecisionDto>?> ListAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        if (!await ProjectCanAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        return await db.MovieMissingInsertDecisions.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => ToDto(item))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MovieMissingInsertDecisionDto>?> MaterializeAsync(
        Guid userId,
        Guid movieProjectId,
        MovieMissingInsertMaterializeRequest request,
        CancellationToken cancellationToken)
    {
        if (!await ProjectCanAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var plan = await planner.GetAsync(userId, movieProjectId, request.TimelineRevisionId, cancellationToken);
        if (plan is null) return null;
        if (plan.TimelineRevisionId is not Guid revisionId || plan.TimelineRevisionNumber is not int revisionNumber)
            return [];

        var existing = await db.MovieMissingInsertDecisions
            .Where(item => item.MovieProjectId == movieProjectId && item.TimelineRevisionId == revisionId)
            .ToDictionaryAsync(item => item.ProposalId, cancellationToken);
        foreach (var proposal in plan.Proposals)
        {
            if (existing.ContainsKey(proposal.Id)) continue;
            var gap = plan.Gaps.SingleOrDefault(item => item.Id == proposal.GapId);
            if (gap is null || proposal.Grounding.AnchorShotId is not Guid anchorShotId) continue;
            var decision = new MovieMissingInsertDecision
            {
                Id = Guid.NewGuid(), MovieProjectId = movieProjectId, ProposalId = proposal.Id, GapId = proposal.GapId,
                TimelineRevisionId = revisionId, TimelineRevisionNumber = revisionNumber,
                TrackId = gap.TrackId ?? Guid.Empty, BeforeTimelineItemId = gap.BeforeTimelineItemId, AfterTimelineItemId = gap.AfterTimelineItemId,
                TimelineInMilliseconds = gap.TimelineInMilliseconds, TimelineOutMilliseconds = gap.TimelineOutMilliseconds,
                BeforeShotId = gap.BeforeShotId, AfterShotId = gap.AfterShotId, SceneId = gap.SceneId,
                StorySceneId = proposal.Grounding.StorySceneId, AnchorShotId = anchorShotId,
                ApprovedStoryRevisionId = proposal.Grounding.ApprovedStoryRevisionId, LockedGuideRevisionId = proposal.Grounding.LockedGuideRevisionId,
                ProductionKitHash = proposal.Grounding.ProductionKitHash,
                ProductionKitSchemaVersion = plan.Grounding.ProductionKits.FirstOrDefault(item => item.ShotId == anchorShotId)?.SchemaVersion,
                AnchorTakeId = proposal.AnchorTakeId, AnchorSelectId = proposal.AnchorSelectId,
                ContractVersion = plan.ContractVersion, InsertType = proposal.InsertType, DurationMilliseconds = proposal.DurationMilliseconds,
                Description = LimitRequired(proposal.Description, 2_000, "Proposal description"),
                Purpose = LimitRequired(proposal.Purpose, 2_000, "Proposal purpose"),
                ProposalGroundingJson = BoundedJson(new { proposal.Grounding, proposal.AnchorTakeId, proposal.AnchorSelectId }, MaxGroundingJsonLength),
                ContinuityAnchorJson = BoundedJson(proposal.ContinuityAnchorJson, MaxAnchorJsonLength),
                ScreenDirectionAnchorJson = BoundedJson(proposal.ScreenDirectionAnchorJson, MaxAnchorJsonLength),
                Status = MovieMissingInsertDecisionStatuses.PendingReview, CreatedByUserId = userId, CreatedAt = DateTime.UtcNow,
            };
            db.MovieMissingInsertDecisions.Add(decision);
            existing.Add(proposal.Id, decision);
        }
        await db.SaveChangesAsync(cancellationToken);
        return existing.Values.OrderBy(item => item.TimelineInMilliseconds).ThenBy(item => item.Id).Select(ToDto).ToArray();
    }

    public async Task<MovieMissingInsertDecisionDto?> ReviewAsync(Guid userId, Guid decisionId, MovieMissingInsertReviewRequest request, CancellationToken cancellationToken)
    {
        var decision = await LoadDecisionAsync(decisionId, cancellationToken);
        if (decision is null || !await ProjectCanAsync(userId, decision.MovieProjectId, MoviePermissions.Approve, cancellationToken)) return null;
        var outcome = NormalizeDecision(request.Decision);
        if (request.Comment?.Trim().Length > MaxReviewCommentLength)
            throw Invalid("MOVIE_INSERT_REVIEW_INVALID", "The review comment is too long.");
        if (decision.Status != MovieMissingInsertDecisionStatuses.PendingReview)
            throw Invalid("MOVIE_INSERT_REVIEW_STATE_INVALID", "Only a pending missing-insert decision can be reviewed.");

        decision.Status = outcome;
        decision.ReviewedByUserId = userId;
        decision.ReviewedAt = DateTime.UtcNow;
        decision.ReviewNote = Clean(request.Comment);
        decision.SelectedTakeId = null;
        decision.SelectedSelectId = null;
        if (outcome == MovieMissingInsertDecisionStatuses.Approved)
        {
            var takeId = request.TakeId ?? decision.AnchorTakeId;
            if (takeId is not Guid selectedTakeId)
                throw Invalid("MOVIE_INSERT_SOURCE_REQUIRED", "Approval requires a server-grounded selected take.");
            if (decision.AnchorTakeId != selectedTakeId)
                throw Invalid("MOVIE_INSERT_SOURCE_NOT_GROUNDED", "The selected take must be the take grounded by the server planner.");
            var selection = await ValidateSourceAsync(decision, selectedTakeId, request.SelectId ?? decision.AnchorSelectId, cancellationToken);
            decision.SelectedTakeId = selection.TakeId;
            decision.SelectedSelectId = selection.SelectId;
        }
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(decision);
    }

    public async Task<MovieMissingInsertDecisionDto?> ApplyAsync(Guid userId, Guid decisionId, CancellationToken cancellationToken)
    {
        var decision = await LoadDecisionAsync(decisionId, cancellationToken);
        if (decision is null || !await ProjectCanAsync(userId, decision.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (decision.Status == MovieMissingInsertDecisionStatuses.Applied) return ToDto(decision);
        if (decision.Status != MovieMissingInsertDecisionStatuses.Approved)
            throw Invalid("MOVIE_INSERT_APPLY_NOT_APPROVED", "Only an approved missing-insert decision can be applied.");
        if (decision.SelectedTakeId is not Guid selectedTakeId)
            throw Invalid("MOVIE_INSERT_SOURCE_REQUIRED", "The approved decision has no selected take.");
        if (decision.ContractVersion != MovieMissingInsertPlanner.ContractVersion)
            throw Conflict("MOVIE_INSERT_CONTRACT_STALE", "The decision was created under an unsupported planner contract.");

        var timelineEntity = await db.MovieTimelines
            .Include(item => item.Revisions).ThenInclude(item => item.Tracks).ThenInclude(item => item.Items)
            .SingleOrDefaultAsync(item => item.MovieProjectId == decision.MovieProjectId, cancellationToken);
        var current = timelineEntity?.Revisions.SingleOrDefault(item => item.Id == timelineEntity.CurrentRevisionId);
        if (timelineEntity is null || current is null || current.Id != decision.TimelineRevisionId || current.RevisionNumber != decision.TimelineRevisionNumber)
            throw Conflict("MOVIE_INSERT_TIMELINE_STALE", "The canonical timeline revision changed after this decision was reviewed.");

        var package = decision.AnchorShotId is Guid anchorShotId
            ? await productionReferences.GetForShotAsync(userId, anchorShotId, cancellationToken)
            : null;
        if (package is null || !string.Equals(package.PackageHash, decision.ProductionKitHash, StringComparison.Ordinal))
            throw Conflict("MOVIE_INSERT_PRODUCTION_KIT_STALE", "The grounded Production Kit changed after this decision was reviewed.");
        await ValidateContinuityAnchorsAsync(decision, cancellationToken);
        var source = await ValidateSourceAsync(decision, selectedTakeId, decision.SelectedSelectId, cancellationToken);
        var duration = source.SelectDurationMilliseconds ?? decision.DurationMilliseconds;
        if (duration != decision.DurationMilliseconds)
            throw Conflict("MOVIE_INSERT_SOURCE_RANGE_STALE", "The approved source range no longer matches the bounded proposal range.");

        var targetTrack = current.Tracks.SingleOrDefault(item => item.Id == decision.TrackId && item.Kind == MovieTimelineTrackKinds.Video);
        if (targetTrack is null || targetTrack.Items.Any(item => item.Kind != MovieTimelineItemKinds.Gap && Overlaps(item.TimelineInMilliseconds, item.TimelineOutMilliseconds, decision.TimelineInMilliseconds, decision.TimelineInMilliseconds + duration)))
            throw Conflict("MOVIE_INSERT_TIMELINE_RANGE_STALE", "The proposed bounded range is no longer available on the canonical video track.");

        var requests = current.Tracks.OrderBy(item => item.TrackNumber).Select(track =>
        {
            var items = track.Items.OrderBy(item => item.Sequence)
                .Where(item => !(track.Id == decision.TrackId && item.Kind == MovieTimelineItemKinds.Gap
                    && item.TimelineInMilliseconds == decision.TimelineInMilliseconds
                    && item.TimelineOutMilliseconds == decision.TimelineOutMilliseconds))
                .Select(ToTimelineItemRequest)
                .ToList();
            if (track.Id == decision.TrackId)
            {
                items.Add(new MovieTimelineItemRequest
                {
                    Kind = MovieTimelineItemKinds.VisualTake,
                    SourceTakeId = selectedTakeId,
                    SourceSelectId = source.SelectId,
                    TimelineInMilliseconds = decision.TimelineInMilliseconds,
                    TimelineOutMilliseconds = decision.TimelineInMilliseconds + duration,
                    SourceInMilliseconds = source.SelectId.HasValue ? null : 0,
                    SourceOutMilliseconds = source.SelectId.HasValue ? null : duration,
                    Label = "Server-approved minimal insert",
                    MetadataJson = JsonSerializer.Serialize(new { decisionId = decision.Id, contractVersion = decision.ContractVersion, providerNeutral = true }),
                });
            }
            return new MovieTimelineTrackRequest
            {
                Kind = track.Kind, Name = track.Name, TrackNumber = track.TrackNumber, IsMuted = track.IsMuted, Items = items,
            };
        }).ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var appliedRevision = await timeline.CreateRevisionAsync(userId, decision.MovieProjectId, new MovieTimelineRevisionRequest
        {
            BaseRevisionId = current.Id,
            Label = "Missing insert apply",
            ChangeSummary = "Applied an approved provider-neutral missing-insert decision.",
            CanonicalTimelineJson = current.CanonicalTimelineJson,
            Tracks = requests,
        }, cancellationToken);
        if (appliedRevision is null) return null;
        decision.Status = MovieMissingInsertDecisionStatuses.Applied;
        decision.AppliedByUserId = userId;
        decision.AppliedAt = DateTime.UtcNow;
        decision.AppliedTimelineRevisionId = appliedRevision.Id;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToDto(decision);
    }

    private async Task<MovieMissingInsertDecision?> LoadDecisionAsync(Guid decisionId, CancellationToken cancellationToken) =>
        await db.MovieMissingInsertDecisions.Include(item => item.MovieProject).SingleOrDefaultAsync(item => item.Id == decisionId, cancellationToken);

    private async Task<bool> ProjectCanAsync(Guid userId, Guid movieProjectId, string permission, CancellationToken cancellationToken) =>
        await db.MovieProjects.AnyAsync(item => item.Id == movieProjectId, cancellationToken)
        && await collaboration.HasPermissionAsync(userId, movieProjectId, permission, cancellationToken);

    private async Task ValidateContinuityAnchorsAsync(MovieMissingInsertDecision decision, CancellationToken cancellationToken)
    {
        var shotIds = new[] { decision.BeforeShotId, decision.AfterShotId, decision.AnchorShotId }.Where(item => item.HasValue).Select(item => item!.Value).Distinct().ToArray();
        var shots = await db.MovieShots.AsNoTracking().Where(item => shotIds.Contains(item.Id) && item.Scene.MovieProjectId == decision.MovieProjectId).ToDictionaryAsync(item => item.Id, cancellationToken);
        if (shots.Count != shotIds.Length || decision.AnchorShotId is not Guid anchorId || !shots.TryGetValue(anchorId, out var anchor))
            throw Conflict("MOVIE_INSERT_CONTINUITY_STALE", "A continuity anchor was removed or moved to another workspace.");
        var currentContinuity = JsonSerializer.Serialize(new
        {
            beforeShotId = decision.BeforeShotId, afterShotId = decision.AfterShotId, anchorShotId = anchor.Id, sceneId = anchor.MovieSceneId,
            locationSet = Limit(anchor.LocationSet, 1_000), continuityReferences = Limit(anchor.ContinuityReferences, 1_000), visualContinuityNotes = Limit(anchor.VisualContinuityNotes, 1_000),
        });
        var currentScreenDirection = JsonSerializer.Serialize(new
        {
            beforeShotId = decision.BeforeShotId, afterShotId = decision.AfterShotId, anchorShotId = anchor.Id, screenDirection = Limit(anchor.ScreenDirectionJson, 2_000),
        });
        if (!string.Equals(currentContinuity, decision.ContinuityAnchorJson, StringComparison.Ordinal)
            || !string.Equals(currentScreenDirection, decision.ScreenDirectionAnchorJson, StringComparison.Ordinal))
            throw Conflict("MOVIE_INSERT_CONTINUITY_STALE", "Continuity or screen-direction anchors changed after this decision was reviewed.");
    }

    private async Task<SourceSelection> ValidateSourceAsync(MovieMissingInsertDecision decision, Guid takeId, Guid? selectId, CancellationToken cancellationToken)
    {
        var take = await db.MovieTakes
            .Include(item => item.MovieShot).ThenInclude(item => item.Scene)
            .Include(item => item.Asset)
            .Include(item => item.MovieClip).ThenInclude(item => item!.Asset)
            .Include(item => item.Selects)
            .SingleOrDefaultAsync(item => item.Id == takeId && item.MovieShot.Scene.MovieProjectId == decision.MovieProjectId, cancellationToken);
        if (take is null || take.MovieShotId != decision.AnchorShotId || take.Status is not (MovieTakeStatuses.Approved or MovieTakeStatuses.Selected)
            || !(take.SelectedAt.HasValue || take.MovieShot.SelectedTakeId == take.Id || take.MovieShot.FinalTakeId == take.Id))
            throw Conflict("MOVIE_INSERT_SOURCE_STALE", "The selected take is no longer approved and selected for this movie workspace.");
        MovieTakeSelect? select = null;
        if (selectId is Guid requestedSelectId)
        {
            select = take.Selects.SingleOrDefault(item => item.Id == requestedSelectId && item.Status == MovieTakeSelectStatuses.Approved);
            if (select is null) throw Conflict("MOVIE_INSERT_SELECT_STALE", "The selected take range is no longer approved.");
        }
        return new SourceSelection(take.Id, select?.Id, select?.DurationMilliseconds);
    }

    private static MovieTimelineItemRequest ToTimelineItemRequest(MovieTimelineItem item) => new()
    {
        Kind = item.Kind, SourceTakeId = item.SourceTakeId, SourceSelectId = item.SourceSelectId,
        SourceAssetId = item.Kind == MovieTimelineItemKinds.VisualTake ? null : item.SourceAssetId,
        TimelineInMilliseconds = item.TimelineInMilliseconds, TimelineOutMilliseconds = item.TimelineOutMilliseconds,
        SourceInMilliseconds = item.SourceInMilliseconds, SourceOutMilliseconds = item.SourceOutMilliseconds,
        Label = item.Label, MetadataJson = item.MetadataJson,
    };

    private static bool Overlaps(int start, int end, int targetStart, int targetEnd) => start < targetEnd && targetStart < end;

    private static string NormalizeDecision(string? value) =>
        value?.Trim().Equals("Rejected", StringComparison.OrdinalIgnoreCase) == true
            ? MovieMissingInsertDecisionStatuses.Rejected
            : value?.Trim().Equals("Approved", StringComparison.OrdinalIgnoreCase) == true
                ? MovieMissingInsertDecisionStatuses.Approved
                : throw Invalid("MOVIE_INSERT_REVIEW_INVALID", "Decision must be Approved or Rejected.");

    private static string BoundedJson(object? value, int maximum)
    {
        var json = value is string text ? text : JsonSerializer.Serialize(value);
        return json.Length <= maximum ? json : throw Invalid("MOVIE_INSERT_GROUNDING_INVALID", "Planner grounding exceeded the bounded review record size.");
    }

    private static string LimitRequired(string value, int maximum, string field) =>
        value.Length <= maximum ? value : throw Invalid("MOVIE_INSERT_GROUNDING_INVALID", $"{field} exceeded its bounded size.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Limit(string? value, int maximum) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private static MovieMissingInsertDecisionException Invalid(string code, string message) => new(code, message, false);
    private static MovieMissingInsertDecisionException Conflict(string code, string message) => new(code, message, true);

    private static MovieMissingInsertDecisionDto ToDto(MovieMissingInsertDecision item) => new(
        item.Id, item.MovieProjectId, item.ProposalId, item.GapId, item.TimelineRevisionId, item.TimelineRevisionNumber, item.TrackId,
        item.BeforeTimelineItemId, item.AfterTimelineItemId, item.TimelineInMilliseconds, item.TimelineOutMilliseconds,
        item.BeforeShotId, item.AfterShotId, item.SceneId, item.StorySceneId, item.AnchorShotId, item.ApprovedStoryRevisionId,
        item.LockedGuideRevisionId, item.ProductionKitHash, item.ProductionKitSchemaVersion, item.AnchorTakeId, item.AnchorSelectId,
        item.SelectedTakeId, item.SelectedSelectId, item.ContractVersion, item.InsertType, item.DurationMilliseconds, item.Description,
        item.Purpose, item.ProposalGroundingJson, item.ContinuityAnchorJson, item.ScreenDirectionAnchorJson, item.Status,
        item.CreatedByUserId, item.CreatedAt, item.ReviewedByUserId, item.ReviewedAt, item.ReviewNote, item.AppliedByUserId,
        item.AppliedAt, item.AppliedTimelineRevisionId);

    private sealed record SourceSelection(Guid TakeId, Guid? SelectId, int? SelectDurationMilliseconds);
}

public sealed class MovieMissingInsertDecisionException(string code, string message, bool isConflict) : Exception(message)
{
    public string Code { get; } = code;
    public bool IsConflict { get; } = isConflict;
}

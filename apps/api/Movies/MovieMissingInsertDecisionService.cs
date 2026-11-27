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

        var revisionDecisions = db.MovieMissingInsertDecisions.AsNoTracking()
            .Where(item => item.MovieProjectId == movieProjectId && item.TimelineRevisionId == revisionId);
        var existingIds = (await revisionDecisions.Select(item => item.ProposalId).ToListAsync(cancellationToken)).ToHashSet();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Overlapping plans acquire unique keys in the same order. Only the proposal identity can
        // be replayed; every other constraint violation rolls back the entire set of inserts.
        foreach (var proposal in plan.Proposals.OrderBy(item => item.Id))
        {
            if (existingIds.Contains(proposal.Id)) continue;
            var gap = plan.Gaps.SingleOrDefault(item => item.Id == proposal.GapId);
            if (gap is null || proposal.Grounding.AnchorShotId is not Guid anchorShotId) continue;
            var schemaVersion = plan.Grounding.ProductionKits.FirstOrDefault(item => item.ShotId == anchorShotId)?.SchemaVersion;
            var description = LimitRequired(proposal.Description, 2_000, "Proposal description");
            var purpose = LimitRequired(proposal.Purpose, 2_000, "Proposal purpose");
            var grounding = BoundedJson(new { proposal.Grounding, proposal.AnchorTakeId, proposal.AnchorSelectId }, MaxGroundingJsonLength);
            var continuity = BoundedJson(proposal.ContinuityAnchorJson, MaxAnchorJsonLength);
            var screenDirection = BoundedJson(proposal.ScreenDirectionAnchorJson, MaxAnchorJsonLength);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MovieMissingInsertDecisions" (
                    "Id", "MovieProjectId", "ProposalId", "GapId", "TimelineRevisionId", "TimelineRevisionNumber",
                    "TrackId", "BeforeTimelineItemId", "AfterTimelineItemId", "TimelineInMilliseconds", "TimelineOutMilliseconds",
                    "BeforeShotId", "AfterShotId", "SceneId", "StorySceneId", "AnchorShotId", "ApprovedStoryRevisionId",
                    "LockedGuideRevisionId", "ProductionKitHash", "ProductionKitSchemaVersion", "AnchorTakeId", "AnchorSelectId",
                    "ContractVersion", "InsertType", "DurationMilliseconds", "Description", "Purpose", "ProposalGroundingJson",
                    "ContinuityAnchorJson", "ScreenDirectionAnchorJson", "Status", "CreatedByUserId", "CreatedAt")
                VALUES (
                    {Guid.NewGuid()}, {movieProjectId}, {proposal.Id}, {proposal.GapId}, {revisionId}, {revisionNumber},
                    {gap.TrackId ?? Guid.Empty}, {gap.BeforeTimelineItemId}, {gap.AfterTimelineItemId}, {gap.TimelineInMilliseconds}, {gap.TimelineOutMilliseconds},
                    {gap.BeforeShotId}, {gap.AfterShotId}, {gap.SceneId}, {proposal.Grounding.StorySceneId}, {anchorShotId}, {proposal.Grounding.ApprovedStoryRevisionId},
                    {proposal.Grounding.LockedGuideRevisionId}, {proposal.Grounding.ProductionKitHash}, {schemaVersion}, {proposal.AnchorTakeId}, {proposal.AnchorSelectId},
                    {plan.ContractVersion}, {proposal.InsertType}, {proposal.DurationMilliseconds}, {description}, {purpose}, {grounding},
                    {continuity}, {screenDirection}, {MovieMissingInsertDecisionStatuses.PendingReview}, {userId}, {DateTime.UtcNow})
                ON CONFLICT ("MovieProjectId", "TimelineRevisionId", "ProposalId") DO NOTHING
                """, cancellationToken);
        }

        var persisted = await revisionDecisions.OrderBy(item => item.TimelineInMilliseconds).ThenBy(item => item.Id)
            .Select(item => ToDto(item)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persisted;
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

        // Approval sources are validated before anything is persisted, so a review that cannot
        // produce a server-grounded source leaves the decision untouched in PendingReview.
        Guid? selectedTakeId = null;
        Guid? selectedSelectId = null;
        if (outcome == MovieMissingInsertDecisionStatuses.Approved)
        {
            var takeId = request.TakeId ?? decision.AnchorTakeId;
            if (takeId is not Guid groundedTakeId)
                throw Invalid("MOVIE_INSERT_SOURCE_REQUIRED", "Approval requires a server-grounded selected take.");
            if (decision.AnchorTakeId != groundedTakeId)
                throw Invalid("MOVIE_INSERT_SOURCE_NOT_GROUNDED", "The selected take must be the take grounded by the server planner.");
            var selection = await ValidateSourceAsync(decision, groundedTakeId, request.SelectId ?? decision.AnchorSelectId, cancellationToken);
            selectedTakeId = selection.TakeId;
            selectedSelectId = selection.SelectId;
        }

        // Two reviewers can hold the same PendingReview row in their change trackers at the same
        // time (each precheck above sees only its own context's view), and a review can even be
        // delayed past an Apply. The write is therefore a compare-and-set: every review field is
        // updated by one statement whose WHERE clause still requires Status = PendingReview. The
        // loser observes zero matched rows and returns a conflict instead of silently overwriting
        // the winning review - or an already applied - state, mirroring the atomic claim used by
        // ApplyAsync below. See MovieMissingInsertDecisionReviewConcurrencyPostgresTests for the
        // concurrent two-context regression on the same PostgreSQL engine CI runs.
        var reviewedAt = DateTime.UtcNow;
        var reviewNote = Clean(request.Comment);
        Guid? reviewerId = userId;
        var reviewed = await db.MovieMissingInsertDecisions
            .Where(item => item.Id == decisionId && item.Status == MovieMissingInsertDecisionStatuses.PendingReview)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, outcome)
                .SetProperty(item => item.ReviewedByUserId, reviewerId)
                .SetProperty(item => item.ReviewedAt, reviewedAt)
                .SetProperty(item => item.ReviewNote, reviewNote)
                .SetProperty(item => item.SelectedTakeId, selectedTakeId)
                .SetProperty(item => item.SelectedSelectId, selectedSelectId), cancellationToken);
        if (reviewed != 1)
            throw Conflict("MOVIE_INSERT_REVIEW_RACE", "The missing-insert decision was reviewed or applied while this review was in progress.");

        // Keep the tracked instance consistent with the committed row (same discipline as the
        // claim in ApplyAsync) and answer from an independent read-back so the response cannot
        // claim fields the database does not hold.
        decision.Status = outcome;
        decision.ReviewedByUserId = reviewerId;
        decision.ReviewedAt = reviewedAt;
        decision.ReviewNote = reviewNote;
        decision.SelectedTakeId = selectedTakeId;
        decision.SelectedSelectId = selectedSelectId;
        var persisted = await db.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == decisionId, cancellationToken);
        return ToDto(persisted);
    }

    public async Task<MovieMissingInsertDecisionDto?> ApplyAsync(Guid userId, Guid decisionId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var decision = await LoadDecisionAsync(decisionId, cancellationToken);
        if (decision is null || !await ProjectCanAsync(userId, decision.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        if (decision.Status == MovieMissingInsertDecisionStatuses.Applied)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToDto(decision);
        }
        if (decision.Status != MovieMissingInsertDecisionStatuses.Approved)
            throw Invalid("MOVIE_INSERT_APPLY_NOT_APPROVED", "Only an approved missing-insert decision can be applied.");

        // Claim the decision inside the same transaction as the timeline write. A concurrent
        // request waits for this update, then re-reads the committed Applied record and replays
        // the exact result instead of creating a second canonical revision. Any failed apply
        // rolls the claim back, so a later request can retry normally.
        var claimed = await db.MovieMissingInsertDecisions
            .Where(item => item.Id == decisionId && item.Status == MovieMissingInsertDecisionStatuses.Approved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieMissingInsertDecisionStatuses.Applying), cancellationToken);
        if (claimed != 1)
        {
            var latest = await db.MovieMissingInsertDecisions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == decisionId, cancellationToken);
            if (latest?.Status == MovieMissingInsertDecisionStatuses.Applied)
            {
                await transaction.CommitAsync(cancellationToken);
                return ToDto(latest);
            }
            throw Conflict("MOVIE_INSERT_APPLY_RACE", "The missing-insert decision changed while it was being applied.");
        }
        decision.Status = MovieMissingInsertDecisionStatuses.Applying;
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
        if (decision.AnchorSelectId is Guid anchorSelectId && selectId != anchorSelectId)
            throw Conflict("MOVIE_INSERT_SELECT_NOT_GROUNDED", "The selected range must match the range grounded by the server planner.");
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

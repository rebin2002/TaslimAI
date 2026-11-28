using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// GATE-PRIV / round-19 follow-up (independent review of head 5df3254f): the pre-fix
/// <see cref="MovieMissingInsertDecisionService.ReviewAsync"/> read the decision into its change
/// tracker and wrote the review with a plain SaveChanges, so two reviewers that both held the same
/// PendingReview row let the slower write silently overwrite the faster review - or an already
/// applied - state. ReviewAsync now persists through a conditional UPDATE whose WHERE clause still
/// requires PendingReview, and these cases pin that contract on the same PostgreSQL engine CI runs
/// (SQLite cannot reproduce the overlapping-write window).
/// </summary>
public sealed class MovieMissingInsertDecisionReviewConcurrencyPostgresTests : IAsyncLifetime
{
    // Every PostgreSQL-backed suite owns a private schema so EnsureCreated cannot leak tables into
    // the shared CI database and make a later suite believe the schema already exists.
    private readonly string schema = $"movie_insert_review_{Guid.NewGuid():N}";
    private DbContextOptions<TaslimDbContext> options = null!;
    private bool ready;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await create.ExecuteNonQueryAsync();
        }

        options = BuildOptions(connectionString);
        await using var db = new TaslimDbContext(options);
        await db.Database.EnsureCreatedAsync();
        ready = true;
    }

    public async Task DisposeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
        await drop.ExecuteNonQueryAsync();
    }

    private DbContextOptions<TaslimDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString)
            .Options;

    private TaslimDbContext NewContext() => new(options);

    private static MovieMissingInsertDecisionService CreateService(TaslimDbContext db, IMovieMissingInsertPlannerService? planner = null) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)), planner!, null!, null!);

    [DisposablePostgresFact]
    public async Task Concurrent_materialization_of_two_proposals_replays_the_same_persisted_decisions()
    {
        Assert.True(ready);
        Fixture fixture;
        MovieMissingInsertDecision existing;
        await using (var setup = NewContext())
        {
            fixture = await SeedPendingDecisionAsync(setup);
            existing = await setup.MovieMissingInsertDecisions.SingleAsync(item => item.Id == fixture.DecisionId);
        }

        var gaps = new[] { 2_000, 4_000 }.Select(start => new MovieMissingInsertGapDto(
            Guid.NewGuid(), "Gap", existing.TrackId, start, start + 1_000, 1_000,
            null, null, null, existing.AnchorShotId, existing.SceneId, "Synthetic gap")).ToArray();
        var grounding = new MovieMissingInsertProposalGroundingDto(null, null, null, existing.AnchorShotId, "kit-hash", []);
        var proposals = gaps.Select(gap => new MovieMissingInsertProposalDto(
            Guid.NewGuid(), gap.Id, MovieMissingInsertApprovalStatuses.PendingApproval, true, false,
            MovieMissingInsertProposalTypes.Detail, existing.SceneId, null, existing.AnchorShotId, 1_000,
            "Detail insert", "Fill the bounded gap", null, null, null, null, null, null, null, grounding, fixture.TakeId)).ToArray();
        var plan = new MovieMissingInsertPlanDto(MovieMissingInsertPlanner.ContractVersion, existing.MovieProjectId,
            existing.TimelineRevisionId, existing.TimelineRevisionNumber, MovieTimelineRevisionStatuses.Draft, true, false, false,
            new MovieMissingInsertGroundingDto(null, null, null, null, [], true), gaps, [], proposals, [], DateTime.UtcNow);
        var request = new MovieMissingInsertMaterializeRequest { TimelineRevisionId = existing.TimelineRevisionId };

        // Both connections have already read the same persisted proposal set when the gate opens.
        var barrier = new MaterializationReadBarrier();
        var raceOptions = new DbContextOptionsBuilder<TaslimDbContext>(options).AddInterceptors(barrier).Options;
        await using var first = new TaslimDbContext(raceOptions);
        await using var second = new TaslimDbContext(raceOptions);
        var results = await Task.WhenAll(
            CreateService(first, new StaticPlan(plan)).MaterializeAsync(fixture.FirstReviewerId, existing.MovieProjectId, request, CancellationToken.None),
            CreateService(second, new StaticPlan(plan with { Proposals = proposals.Reverse().ToArray() }))
                .MaterializeAsync(fixture.SecondReviewerId, existing.MovieProjectId, request, CancellationToken.None));

        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking()
            .Where(item => item.MovieProjectId == existing.MovieProjectId && item.TimelineRevisionId == existing.TimelineRevisionId)
            .OrderBy(item => item.TimelineInMilliseconds).ToArrayAsync();
        Assert.Equal(new[] { existing.ProposalId, proposals[0].Id, proposals[1].Id }, persisted.Select(item => item.ProposalId).ToArray());
        foreach (var result in results)
        {
            Assert.NotNull(result);
            Assert.Equal(persisted.Select(item => item.Id).ToArray(), result!.Select(item => item.Id).ToArray());
            Assert.Equal(persisted.Select(item => item.CreatedByUserId).ToArray(), result.Select(item => item.CreatedByUserId).ToArray());
        }
    }

    private sealed class StaticPlan(MovieMissingInsertPlanDto plan) : IMovieMissingInsertPlannerService
    {
        public Task<MovieMissingInsertPlanDto?> GetAsync(Guid userId, Guid movieProjectId, Guid? timelineRevisionId, CancellationToken cancellationToken = default)
            => Task.FromResult<MovieMissingInsertPlanDto?>(plan);
    }

    private sealed class MaterializationReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"MovieMissingInsertDecisions\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2) released.TrySetResult();
                await released.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }

    [DisposablePostgresFact]
    public async Task Review_cannot_substitute_a_different_approved_select_for_the_planned_anchor()
    {
        Assert.True(ready);
        await using var setup = NewContext();
        var fixture = await SeedPendingDecisionAsync(setup);
        var decision = await setup.MovieMissingInsertDecisions.SingleAsync(item => item.Id == fixture.DecisionId);
        var anchorId = Guid.NewGuid();
        var alternateId = Guid.NewGuid();
        void AddSelect(Guid id, int number, int start) => setup.MovieTakeSelects.Add(new MovieTakeSelect
        {
            Id = id, MovieTakeId = fixture.TakeId, SelectNumber = number,
            Label = "Approved synthetic range", StartMilliseconds = start, EndMilliseconds = start + 1_000,
            Status = MovieTakeSelectStatuses.Approved, CreatedByUserId = fixture.FirstReviewerId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        AddSelect(anchorId, 1, 0);
        AddSelect(alternateId, 2, 1_000);
        decision.AnchorSelectId = anchorId;
        await setup.SaveChangesAsync();

        await using var reviewer = NewContext();
        var conflict = await Assert.ThrowsAsync<MovieMissingInsertDecisionException>(() => CreateService(reviewer)
            .ReviewAsync(fixture.FirstReviewerId, decision.Id, new MovieMissingInsertReviewRequest { SelectId = alternateId }, CancellationToken.None));
        Assert.Equal("MOVIE_INSERT_SELECT_NOT_GROUNDED", conflict.Code);
        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == decision.Id);
        Assert.Equal(MovieMissingInsertDecisionStatuses.PendingReview, persisted.Status);
        Assert.Null(persisted.SelectedSelectId);
        var approved = await CreateService(verify).ReviewAsync(fixture.FirstReviewerId, decision.Id,
            new MovieMissingInsertReviewRequest { SelectId = anchorId }, CancellationToken.None);
        Assert.Equal(anchorId, approved!.SelectedSelectId);
    }

    [DisposablePostgresFact]
    public async Task Stale_second_reviewer_conflicts_and_cannot_overwrite_the_committed_review()
    {
        Assert.True(ready);
        Fixture fixture;
        await using (var setup = NewContext())
            fixture = await SeedPendingDecisionAsync(setup);
        var decisionId = fixture.DecisionId;

        // The second reviewer's context loads the PendingReview row first - this is the exact stale
        // read the review described: the row sits in the change tracker while the first reviewer
        // commits an Approved, Apply-ready review out of band.
        await using var staleDb = NewContext();
        await staleDb.MovieMissingInsertDecisions
            .Include(item => item.MovieProject)
            .SingleAsync(item => item.Id == decisionId);

        await using var firstDb = NewContext();
        var approved = await CreateService(firstDb)
            .ReviewAsync(fixture.FirstReviewerId, decisionId, new MovieMissingInsertReviewRequest { Decision = "Approved", Comment = " first pass " }, CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(MovieMissingInsertDecisionStatuses.Approved, approved!.Status);
        Assert.Equal(fixture.FirstReviewerId, approved.ReviewedByUserId);
        Assert.Equal(fixture.TakeId, approved.SelectedTakeId);
        Assert.Equal("first pass", approved.ReviewNote);

        // The delayed reviewer now submits its Rejected verdict while its tracked entity still
        // reports PendingReview (EF Core does not overwrite values of an already-tracked row on
        // requery). Pre-fix this was a silent lost-update that stamped Rejected - clearing the
        // selection - over the committed Approved state; the compare-and-set must turn it into a
        // conflict instead.
        var race = await Assert.ThrowsAsync<MovieMissingInsertDecisionException>(
            () => CreateService(staleDb).ReviewAsync(fixture.SecondReviewerId, decisionId, new MovieMissingInsertReviewRequest { Decision = "Rejected", Comment = "too late" }, CancellationToken.None));
        Assert.Equal("MOVIE_INSERT_REVIEW_RACE", race.Code);
        Assert.True(race.IsConflict);

        // The authoritative row keeps the winning review; the loser's Rejected verdict never landed.
        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == decisionId);
        Assert.Equal(MovieMissingInsertDecisionStatuses.Approved, persisted.Status);
        Assert.Equal(fixture.FirstReviewerId, persisted.ReviewedByUserId);
        Assert.Equal(fixture.TakeId, persisted.SelectedTakeId);
        Assert.NotEqual("too late", persisted.ReviewNote);
    }

    [DisposablePostgresFact]
    public async Task Concurrent_reviewers_yield_exactly_one_winning_verdict()
    {
        Assert.True(ready);
        Fixture fixture;
        await using (var setup = NewContext())
            fixture = await SeedPendingDecisionAsync(setup);

        // Both contexts read the PendingReview row before either writes, so both pass the
        // precheck and reach the conditional update; the database must serialise them and let
        // only one verdict persist.
        await using var approveDb = NewContext();
        await using var rejectDb = NewContext();
        await approveDb.MovieMissingInsertDecisions.Include(item => item.MovieProject).SingleAsync(item => item.Id == fixture.DecisionId);
        await rejectDb.MovieMissingInsertDecisions.Include(item => item.MovieProject).SingleAsync(item => item.Id == fixture.DecisionId);

        var approveTask = Task.Run(async () =>
        {
            try
            {
                await CreateService(approveDb).ReviewAsync(fixture.FirstReviewerId, fixture.DecisionId,
                    new MovieMissingInsertReviewRequest { Decision = "Approved" }, CancellationToken.None);
                return (Exception?)null;
            }
            catch (MovieMissingInsertDecisionException exception) { return exception; }
        });
        var rejectTask = Task.Run(async () =>
        {
            try
            {
                await CreateService(rejectDb).ReviewAsync(fixture.SecondReviewerId, fixture.DecisionId,
                    new MovieMissingInsertReviewRequest { Decision = "Rejected", Comment = "no" }, CancellationToken.None);
                return (Exception?)null;
            }
            catch (MovieMissingInsertDecisionException exception) { return exception; }
        });
        var results = await Task.WhenAll(approveTask, rejectTask);

        var failures = results.Where(item => item is not null).ToArray();
        var winners = results.Length - failures.Length;
        Assert.Equal(1, winners);
        Assert.Equal(1, failures.Length);
        var loser = failures.Single()!;
        Assert.Equal("MOVIE_INSERT_REVIEW_RACE", loser is MovieMissingInsertDecisionException decisionException ? decisionException.Code : string.Empty);

        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == fixture.DecisionId);
        // Which arm reaches the conditional UPDATE first is scheduler-dependent; the invariant is
        // that exactly one verdict persisted intact and the other conflicted.
        if (persisted.Status == MovieMissingInsertDecisionStatuses.Approved)
        {
            Assert.Equal(fixture.FirstReviewerId, persisted.ReviewedByUserId);
            Assert.Equal(fixture.TakeId, persisted.SelectedTakeId);
            Assert.Null(persisted.ReviewNote);
        }
        else
        {
            Assert.Equal(MovieMissingInsertDecisionStatuses.Rejected, persisted.Status);
            Assert.Equal(fixture.SecondReviewerId, persisted.ReviewedByUserId);
            Assert.Equal("no", persisted.ReviewNote);
            Assert.Null(persisted.SelectedTakeId);
        }
    }

    [DisposablePostgresFact]
    public async Task Approval_failing_source_validation_persists_nothing_and_leaves_the_decision_pending()
    {
        Assert.True(ready);
        Fixture fixture;
        await using (var setup = NewContext())
            fixture = await SeedPendingDecisionAsync(setup);

        await using var db = NewContext();
        var service = CreateService(db);
        // A take that is not the planner-grounded anchor cannot be approved; the check must happen
        // before any review field is written, so the row stays PendingReview and re-reviewable.
        var invalid = await Assert.ThrowsAsync<MovieMissingInsertDecisionException>(() => service.ReviewAsync(
            fixture.FirstReviewerId, fixture.DecisionId,
            new MovieMissingInsertReviewRequest { Decision = "Approved", TakeId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Equal("MOVIE_INSERT_SOURCE_NOT_GROUNDED", invalid.Code);
        Assert.False(invalid.IsConflict);

        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == fixture.DecisionId);
        Assert.Equal(MovieMissingInsertDecisionStatuses.PendingReview, persisted.Status);
        Assert.Null(persisted.ReviewedByUserId);
        Assert.Null(persisted.ReviewedAt);
        Assert.Null(persisted.SelectedTakeId);

        // The decision remains reviewable by the real approval afterwards.
        var approved = await CreateService(verify).ReviewAsync(fixture.FirstReviewerId, fixture.DecisionId,
            new MovieMissingInsertReviewRequest { Decision = "Approved" }, CancellationToken.None);
        Assert.Equal(MovieMissingInsertDecisionStatuses.Approved, approved!.Status);
    }

    [DisposablePostgresFact]
    public async Task Rejection_response_matches_the_persisted_row_for_an_independent_context()
    {
        Assert.True(ready);
        Fixture fixture;
        await using (var setup = NewContext())
            fixture = await SeedPendingDecisionAsync(setup);

        await using var db = NewContext();
        var rejected = await CreateService(db).ReviewAsync(fixture.SecondReviewerId, fixture.DecisionId,
            new MovieMissingInsertReviewRequest { Decision = "Rejected", Comment = "  keep it out  " }, CancellationToken.None);
        Assert.NotNull(rejected);

        await using var verify = NewContext();
        var persisted = await verify.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == fixture.DecisionId);
        Assert.Equal(MovieMissingInsertDecisionStatuses.Rejected, persisted.Status);
        Assert.Equal(fixture.SecondReviewerId, persisted.ReviewedByUserId);
        Assert.Equal("keep it out", persisted.ReviewNote);
        Assert.Equal(rejected.Status, persisted.Status);
        Assert.Equal(rejected.ReviewNote, persisted.ReviewNote);
        Assert.Equal(rejected.SelectedTakeId, persisted.SelectedTakeId);
        Assert.Equal(rejected.SelectedSelectId, persisted.SelectedSelectId);
        Assert.Null(persisted.SelectedTakeId);
    }

    private sealed record Fixture(Guid DecisionId, Guid FirstReviewerId, Guid SecondReviewerId, Guid TakeId);

    private static async Task<Fixture> SeedPendingDecisionAsync(TaslimDbContext db)
    {
        var now = DateTime.UtcNow;
        var firstReviewer = Guid.NewGuid();
        var secondReviewer = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = firstReviewer, UserName = "reviewer-a", NormalizedUserName = "REVIEWER-A", DisplayName = "Reviewer A", CreatedAt = now, UpdatedAt = now });
        db.Users.Add(new ApplicationUser { Id = secondReviewer, UserName = "reviewer-b", NormalizedUserName = "REVIEWER-B", DisplayName = "Reviewer B", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Review Workspace", Slug = $"review-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = firstReviewer, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = secondReviewer, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = firstReviewer, Title = "Review Movie", Description = "Review race fixture", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = firstReviewer, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = secondReviewer, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Opening", Summary = "Review opening", CreatedAt = now, UpdatedAt = now });
        // Note: do not set shot.SelectedTakeId here - the take references the shot, so assigning
        // both directions in one SaveChanges creates an EF topological cycle. The take's
        // SelectedAt timestamp alone satisfies ValidateSourceAsync's selected-source check.
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Grounding shot", CreatedAt = now, UpdatedAt = now });
        db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "Selected take", Status = MovieTakeStatuses.Selected, SelectedAt = now, CreatedAt = now, UpdatedAt = now });
        db.MovieMissingInsertDecisions.Add(new MovieMissingInsertDecision
        {
            Id = decisionId, MovieProjectId = movieId, ProposalId = Guid.NewGuid(), GapId = Guid.NewGuid(),
            TimelineRevisionId = Guid.NewGuid(), TimelineRevisionNumber = 2, TrackId = Guid.NewGuid(),
            TimelineInMilliseconds = 1_000, TimelineOutMilliseconds = 2_000, SceneId = sceneId,
            AnchorShotId = shotId, AnchorTakeId = takeId, ProductionKitHash = "kit-hash",
            ContractVersion = MovieMissingInsertPlanner.ContractVersion, InsertType = MovieMissingInsertProposalTypes.Detail,
            DurationMilliseconds = 1_000, Description = "Bounded proposal", Purpose = "Review race",
            ProposalGroundingJson = "{}", ContinuityAnchorJson = "{}", ScreenDirectionAnchorJson = "{}",
            Status = MovieMissingInsertDecisionStatuses.PendingReview, CreatedByUserId = firstReviewer, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new Fixture(decisionId, firstReviewer, secondReviewer, takeId);
    }
}

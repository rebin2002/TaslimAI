using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieMissingInsertDecisionTests
{
    [Fact]
    public void Fresh_schema_contains_only_the_additive_durable_decision_record()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = CreateDb(connection);
        db.Database.EnsureCreated();

        Assert.NotNull(db.Model.FindEntityType(typeof(MovieMissingInsertDecision)));
        Assert.Contains("MovieMissingInsertDecisions", db.Model.GetEntityTypes().Select(item => item.GetTableName()));
    }

    [Fact]
    public async Task Decision_record_survives_reload_without_copying_movie_hierarchy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedProjectAsync(db);
        var decisionId = Guid.NewGuid();
        db.MovieMissingInsertDecisions.Add(new MovieMissingInsertDecision
        {
            Id = decisionId, MovieProjectId = fixture.MovieId, ProposalId = Guid.NewGuid(), GapId = Guid.NewGuid(),
            TimelineRevisionId = Guid.NewGuid(), TimelineRevisionNumber = 2, TrackId = Guid.NewGuid(),
            TimelineInMilliseconds = 1_000, TimelineOutMilliseconds = 2_000, AnchorShotId = Guid.NewGuid(),
            AnchorTakeId = Guid.NewGuid(), ProductionKitHash = "kit-hash", ContractVersion = MovieMissingInsertPlanner.ContractVersion,
            InsertType = MovieMissingInsertProposalTypes.Detail, DurationMilliseconds = 1_000,
            Description = "Bounded proposal", Purpose = "Review-only insert", ProposalGroundingJson = "{}",
            ContinuityAnchorJson = "{}", ScreenDirectionAnchorJson = "{}", Status = MovieMissingInsertDecisionStatuses.PendingReview,
            CreatedByUserId = fixture.UserId, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reloaded = await db.MovieMissingInsertDecisions.AsNoTracking().SingleAsync(item => item.Id == decisionId);
        Assert.Equal(MovieMissingInsertDecisionStatuses.PendingReview, reloaded.Status);
        Assert.Equal("kit-hash", reloaded.ProductionKitHash);
        Assert.Equal(MovieMissingInsertPlanner.ContractVersion, reloaded.ContractVersion);
        Assert.Equal(Guid.Empty, reloaded.AppliedTimelineRevisionId ?? Guid.Empty);
    }

    [Fact]
    public async Task Decision_listing_is_workspace_authorized()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedProjectAsync(db);
        var unauthorizedUserId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = unauthorizedUserId, UserName = "outsider", NormalizedUserName = "OUTSIDER", DisplayName = "Outsider", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var access = new MovieCollaborationAccess(db, new WorkspaceAccessService(db));
        var service = new MovieMissingInsertDecisionService(db, access, null!, null!, null!);

        Assert.Null(await service.ListAsync(unauthorizedUserId, fixture.MovieId, CancellationToken.None));
    }

    [Fact]
    public async Task Apply_rejects_a_stale_current_revision_before_any_provider_or_charge_path()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedProjectAsync(db);
        var currentRevision = new MovieTimelineRevision
        {
            Id = Guid.NewGuid(), MovieTimelineId = Guid.NewGuid(), RevisionNumber = 2,
            Status = MovieTimelineRevisionStatuses.Draft, CreatedByUserId = fixture.UserId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var timeline = new MovieTimeline
        {
            Id = currentRevision.MovieTimelineId, MovieProjectId = fixture.MovieId, CurrentRevisionNumber = 2,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Revisions = [currentRevision],
        };
        db.MovieTimelines.Add(timeline);
        await db.SaveChangesAsync();
        timeline.CurrentRevisionId = currentRevision.Id;
        await db.SaveChangesAsync();
        var decision = new MovieMissingInsertDecision
        {
            Id = Guid.NewGuid(), MovieProjectId = fixture.MovieId, ProposalId = Guid.NewGuid(), GapId = Guid.NewGuid(),
            TimelineRevisionId = Guid.NewGuid(), TimelineRevisionNumber = 1, TrackId = Guid.NewGuid(),
            TimelineInMilliseconds = 0, TimelineOutMilliseconds = 1_000, AnchorShotId = Guid.NewGuid(),
            AnchorTakeId = Guid.NewGuid(), SelectedTakeId = Guid.NewGuid(), ProductionKitHash = "kit-hash",
            ContractVersion = MovieMissingInsertPlanner.ContractVersion, InsertType = MovieMissingInsertProposalTypes.Detail,
            DurationMilliseconds = 1_000, Description = "Bounded", Purpose = "Bounded", ProposalGroundingJson = "{}",
            ContinuityAnchorJson = "{}", ScreenDirectionAnchorJson = "{}", Status = MovieMissingInsertDecisionStatuses.Approved,
            CreatedByUserId = fixture.UserId, CreatedAt = DateTime.UtcNow,
        };
        db.MovieMissingInsertDecisions.Add(decision);
        await db.SaveChangesAsync();
        var access = new MovieCollaborationAccess(db, new WorkspaceAccessService(db));
        var service = new MovieMissingInsertDecisionService(db, access, null!, null!, null!);

        var exception = await Assert.ThrowsAsync<MovieMissingInsertDecisionException>(() => service.ApplyAsync(fixture.UserId, decision.Id, CancellationToken.None));
        Assert.Equal("MOVIE_INSERT_TIMELINE_STALE", exception.Code);
        Assert.True(exception.IsConflict);
    }

    [Fact]
    public async Task Overlapping_apply_requests_share_one_atomic_result()
    {
        var postgresConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            // The full CI API job supplies the PostgreSQL service; local SQLite cannot hold
            // the overlapping write transactions needed to exercise this regression.
            return;
        }

        var timeline = new BlockingTimelineService();
        try
        {
            ApplyFixture fixture;
            await using (var seedDb = CreateApplyDb(postgresConnectionString!))
                fixture = await SeedApplyFixtureAsync(seedDb);

            await using var firstDb = CreateApplyDb(postgresConnectionString!);
            await using var secondDb = CreateApplyDb(postgresConnectionString!);
            var references = new StaticReferencePackageService(fixture.MovieId, fixture.SceneId, fixture.ShotId, "kit-hash");
            var firstService = new MovieMissingInsertDecisionService(firstDb, new MovieCollaborationAccess(firstDb, new WorkspaceAccessService(firstDb)), null!, references, timeline);
            var secondService = new MovieMissingInsertDecisionService(secondDb, new MovieCollaborationAccess(secondDb, new WorkspaceAccessService(secondDb)), null!, references, timeline);

            var firstApply = firstService.ApplyAsync(fixture.UserId, fixture.DecisionId, CancellationToken.None);
            await timeline.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondApply = secondService.ApplyAsync(fixture.UserId, fixture.DecisionId, CancellationToken.None);
            var racedIntoTimelineBeforeRelease = await Task.WhenAny(timeline.SecondCallStarted.Task, Task.Delay(TimeSpan.FromSeconds(1))) == timeline.SecondCallStarted.Task;
            timeline.ReleaseFirstCall.TrySetResult(true);

            var results = await Task.WhenAll(firstApply, secondApply);
            Assert.False(racedIntoTimelineBeforeRelease);
            Assert.Equal(1, timeline.CallCount);
            Assert.All(results, result => Assert.Equal(MovieMissingInsertDecisionStatuses.Applied, result!.Status));
            Assert.Equal(results[0]!.AppliedTimelineRevisionId, results[1]!.AppliedTimelineRevisionId);

            await using var verifyDb = CreateApplyDb(postgresConnectionString!);
            var persisted = await verifyDb.MovieMissingInsertDecisions.SingleAsync(item => item.Id == fixture.DecisionId);
            Assert.Equal(MovieMissingInsertDecisionStatuses.Applied, persisted.Status);
            Assert.Equal(timeline.AppliedRevisionId, persisted.AppliedTimelineRevisionId);
        }
        finally
        {
            timeline.ReleaseFirstCall.TrySetResult(true);
        }
    }

    [Fact]
    public void Materialize_request_cannot_carry_client_proposal_or_generation_fields()
    {
        var request = JsonSerializer.Deserialize<MovieMissingInsertMaterializeRequest>(
            "{\"timelineRevisionId\":\"00000000-0000-0000-0000-000000000001\",\"proposal\":{\"provider\":\"paid\"},\"generationQueued\":true}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(request);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), request!.TimelineRevisionId);
        Assert.DoesNotContain(typeof(MovieMissingInsertMaterializeRequest).GetProperties(), item => item.Name.Contains("Proposal", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("Generation", StringComparison.OrdinalIgnoreCase));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static TaslimDbContext CreateApplyDb(string connectionString) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<ApplyFixture> SeedApplyFixtureAsync(TaslimDbContext db)
    {
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var takeId = Guid.NewGuid();
        var timelineId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "apply-owner", NormalizedUserName = "APPLY-OWNER", DisplayName = "Apply Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Apply Workspace", Slug = $"apply-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Apply Movie", Description = "Apply fixture", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Opening", Summary = "Apply opening", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Grounding shot", CreatedAt = now, UpdatedAt = now });
        db.MovieTakes.Add(new MovieTake { Id = takeId, MovieShotId = shotId, VersionNumber = 1, Label = "Selected take", Status = MovieTakeStatuses.Selected, SelectedAt = now, CreatedAt = now, UpdatedAt = now });
        db.MovieTimelines.Add(new MovieTimeline
        {
            Id = timelineId, MovieProjectId = movieId, CurrentRevisionNumber = 1, CreatedAt = now, UpdatedAt = now,
            Revisions =
            [
                new MovieTimelineRevision
                {
                    Id = revisionId, MovieTimelineId = timelineId, RevisionNumber = 1, Status = MovieTimelineRevisionStatuses.Draft,
                    CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
                    Tracks =
                    [
                        new MovieTimelineTrack
                        {
                            Id = trackId, MovieTimelineRevisionId = revisionId, TrackNumber = 1, Kind = MovieTimelineTrackKinds.Video,
                            Items = [new MovieTimelineItem { Id = Guid.NewGuid(), MovieTimelineTrackId = trackId, Sequence = 0, Kind = MovieTimelineItemKinds.Gap, TimelineInMilliseconds = 0, TimelineOutMilliseconds = 1_000, DurationMilliseconds = 1_000, CreatedAt = now, UpdatedAt = now }],
                        },
                    ],
                },
            ],
        });
        await db.SaveChangesAsync();
        var timelineEntity = await db.MovieTimelines.SingleAsync(item => item.Id == timelineId);
        timelineEntity.CurrentRevisionId = revisionId;
        var continuityAnchor = JsonSerializer.Serialize(new { beforeShotId = (Guid?)null, afterShotId = (Guid?)null, anchorShotId = shotId, sceneId, locationSet = (string?)null, continuityReferences = (string?)null, visualContinuityNotes = (string?)null });
        var screenDirectionAnchor = JsonSerializer.Serialize(new { beforeShotId = (Guid?)null, afterShotId = (Guid?)null, anchorShotId = shotId, screenDirection = (string?)null });
        db.MovieShots.Single(item => item.Id == shotId).SelectedTakeId = takeId;
        db.MovieMissingInsertDecisions.Add(new MovieMissingInsertDecision
        {
            Id = decisionId, MovieProjectId = movieId, ProposalId = Guid.NewGuid(), GapId = Guid.NewGuid(), TimelineRevisionId = revisionId, TimelineRevisionNumber = 1,
            TrackId = trackId, TimelineInMilliseconds = 0, TimelineOutMilliseconds = 1_000, BeforeShotId = null, AfterShotId = null, SceneId = sceneId, AnchorShotId = shotId,
            AnchorTakeId = takeId, SelectedTakeId = takeId, ProductionKitHash = "kit-hash", ContractVersion = MovieMissingInsertPlanner.ContractVersion,
            InsertType = MovieMissingInsertProposalTypes.Detail, DurationMilliseconds = 1_000, Description = "Bounded", Purpose = "Bounded", ProposalGroundingJson = "{}",
            ContinuityAnchorJson = continuityAnchor, ScreenDirectionAnchorJson = screenDirectionAnchor, Status = MovieMissingInsertDecisionStatuses.Approved, CreatedByUserId = userId, CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new ApplyFixture(userId, movieId, sceneId, shotId, decisionId);
    }

    private sealed record ApplyFixture(Guid UserId, Guid MovieId, Guid SceneId, Guid ShotId, Guid DecisionId);

    private sealed class StaticReferencePackageService(Guid movieProjectId, Guid sceneId, Guid shotId, string packageHash) : IMovieProductionReferencePackageService
    {
        public Task<MovieProductionReferencePackageDto?> GetForShotAsync(Guid userId, Guid requestedShotId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MovieProductionReferencePackageDto?>(requestedShotId == shotId
                ? new MovieProductionReferencePackageDto(1, movieProjectId, sceneId, shotId, packageHash, DateTime.UtcNow, null!, null!, [], [], null!, [], [], null, [], [])
                : null);
    }

    private sealed class BlockingTimelineService : IMovieTimelineService
    {
        private int calls;
        public int CallCount => Volatile.Read(ref calls);
        public Guid AppliedRevisionId { get; } = Guid.NewGuid();
        public TaskCompletionSource<bool> FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MovieTimelineRevisionDto?> CreateRevisionAsync(Guid userId, Guid movieProjectId, MovieTimelineRevisionRequest request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                FirstCallStarted.TrySetResult(true);
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }
            else SecondCallStarted.TrySetResult(true);
            return new MovieTimelineRevisionDto(AppliedRevisionId, movieProjectId, 2, request.BaseRevisionId, MovieTimelineRevisionStatuses.Draft, request.Label, request.ChangeSummary, 1_000, DateTime.UtcNow, DateTime.UtcNow, null, null, []);
        }

        public Task<MovieTimelineDto?> GetAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MovieTimelineTrackDto?> AddTrackAsync(Guid userId, Guid revisionId, MovieTimelineTrackRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MovieTimelineItemDto?> AddItemAsync(Guid userId, Guid trackId, MovieTimelineItemRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MovieTimelineRevisionDto?> LockRevisionAsync(Guid userId, Guid revisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MovieTimelineRevisionDto?> ApplyTransitionEditAsync(Guid userId, Guid movieProjectId, Taslim.Api.Contracts.MovieTimelineTransitionEditRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static async Task<Fixture> SeedProjectAsync(TaslimDbContext db)
    {
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "decision-owner", NormalizedUserName = "DECISION-OWNER", DisplayName = "Decision Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Decision Workspace", Slug = $"decision-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Decision Movie", Description = "Decision fixture", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new Fixture(userId, movieId);
    }

    private sealed record Fixture(Guid UserId, Guid MovieId);
}

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

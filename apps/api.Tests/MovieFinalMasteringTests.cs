using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieFinalMasteringTests
{
    [Fact]
    public async Task Approved_selected_1080p_take_creates_awaiting_master_without_fake_4k_success_or_provider_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId, workspaceId) = await SeedAsync(db);
        var sourceAssetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = sourceAssetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Name = "Source clip",
            AssetType = AssetTypes.Video,
            MetadataJson = "{\"width\":1920,\"height\":1080}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var movies = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await movies.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = sourceAssetId }, CancellationToken.None);
        await movies.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await movies.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        var mastering = CreateService(db);

        var result = await mastering.RequestAsync(userId, take.Id, new MovieFinalMasteringRequest(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(movieId, result!.MovieProjectId);
        Assert.Equal(MovieFinalMasteringProfiles.Uhd4K, result.TargetProfile);
        Assert.Equal(1920, result.SourceWidth);
        Assert.Equal(1080, result.SourceHeight);
        Assert.Equal(MovieFinalMasteringProfiles.Uhd4KWidth, result.TargetWidth);
        Assert.Equal(MovieFinalMasteringProfiles.Uhd4KHeight, result.TargetHeight);
        Assert.Equal(MovieFinalMasteringStates.AwaitingProvider, result.State);
        Assert.Equal(MovieFinalMasteringQcStatuses.NotRun, result.QcStatus);
        Assert.Null(result.OutputAssetId);
        Assert.Null(result.GenerationJobId);
        Assert.Contains("no provider execution", result.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("movie_take", result.ProvenanceJson, StringComparison.Ordinal);
        Assert.Empty(await db.GenerationJobs.ToListAsync());
    }

    [Fact]
    public async Task Missing_source_dimensions_is_blocked_and_never_claims_completed_or_qc_passed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId, workspaceId) = await SeedAsync(db);
        var sourceAssetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = sourceAssetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Name = "Metadata-free source",
            AssetType = AssetTypes.Video,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var movies = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await movies.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = sourceAssetId }, CancellationToken.None);
        await movies.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await movies.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);

        var result = await CreateService(db).RequestAsync(userId, take.Id, new MovieFinalMasteringRequest(), CancellationToken.None);

        Assert.Equal(MovieFinalMasteringStates.Blocked, result!.State);
        Assert.Contains("dimensions", result.StateReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MovieFinalMasteringQcStatuses.NotRun, result.QcStatus);
        Assert.Null(result.OutputAssetId);
        Assert.Null(result.CompletedAt);
    }

    [Fact]
    public async Task Unapproved_or_unselected_take_cannot_enter_final_mastering()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId, _) = await SeedAsync(db);
        var take = await new MovieV2Service(db, new WorkspaceAccessService(db)).AddTakeAsync(userId, shotId, new MovieV2TakeRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MovieFinalMasteringValidationException>(() =>
            CreateService(db).RequestAsync(userId, take!.Id, new MovieFinalMasteringRequest(), CancellationToken.None));

        Assert.Equal("MASTERING_SOURCE_NOT_APPROVED_OR_SELECTED", exception.Code);
        Assert.Empty(await db.MovieFinalMasters.ToListAsync());
    }

    [Fact]
    public async Task Replacement_marks_previous_master_superseded_and_request_is_idempotent_without_replacement()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId, workspaceId) = await SeedAsync(db);
        var sourceAssetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = sourceAssetId,
            WorkspaceId = workspaceId,
            CreatedByUserId = userId,
            Name = "Source clip",
            AssetType = AssetTypes.Video,
            MetadataJson = "{\"resolution\":\"1920x1080\"}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var movies = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await movies.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = sourceAssetId }, CancellationToken.None);
        await movies.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await movies.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        var mastering = CreateService(db);
        var first = await mastering.RequestAsync(userId, take.Id, new MovieFinalMasteringRequest(), CancellationToken.None);
        var same = await mastering.RequestAsync(userId, take.Id, new MovieFinalMasteringRequest(), CancellationToken.None);
        var replacement = await mastering.RequestAsync(userId, take.Id, new MovieFinalMasteringRequest { SupersedesMasterId = first!.Id }, CancellationToken.None);

        var old = await db.MovieFinalMasters.SingleAsync(item => item.Id == first.Id);
        Assert.Equal(first.Id, same!.Id);
        Assert.NotEqual(first.Id, replacement!.Id);
        Assert.Equal(MovieFinalMasteringStates.Superseded, old.State);
        Assert.Equal(replacement.Id, old.SupersededByMasterId);
        Assert.Equal(first.Id, replacement.SupersedesMasterId);
        Assert.Equal(MovieFinalMasteringStates.AwaitingProvider, replacement.State);
        Assert.Equal(replacement.Id, (await mastering.GetForShotAsync(userId, shotId, CancellationToken.None))!.Id);
    }

    private static MovieFinalMasteringService CreateService(TaslimDbContext db) =>
        new(db, new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

    private static TaslimDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid UserId, Guid MovieId, Guid ShotId, Guid WorkspaceId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "master-owner", NormalizedUserName = "MASTER-OWNER", DisplayName = "Master Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Master Workspace", Slug = $"master-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Mastering Test", Description = "A movie.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Scene", Summary = "Existing scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Wide shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, movieId, shotId, workspaceId);
    }
}

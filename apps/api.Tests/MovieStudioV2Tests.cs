using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieStudioV2Tests
{
    [Fact]
    public void Client_json_cannot_supply_server_trusted_movie_cost_fields()
    {
        var request = JsonSerializer.Deserialize<MovieStudioGenerationRequest>("{\"title\":\"clip\",\"estimatedProviderCostUsd\":999,\"internalCostEstimate\":{\"isKnown\":true,\"amountUsd\":999}}", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Null(request!.EstimatedProviderCostUsd);
        Assert.Null(request.InternalCostEstimate);
    }

    [Fact]
    public async Task Canonical_hierarchy_orders_children_and_persists_quality_and_auto_director_separately()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var act = await service.AddActAsync(userId, movieId, new MovieV2ActRequest { Title = "Act One" }, CancellationToken.None);
        var sequence = await service.AddSequenceAsync(userId, act!.Id, new MovieV2SequenceRequest { Title = "Opening" }, CancellationToken.None);
        var scene = await service.AddSceneAsync(userId, sequence!.Id, new MovieV2SceneRequest { Title = "Dawn", Summary = "The story begins." }, CancellationToken.None);
        var canonicalShot = new MovieShot { Id = Guid.NewGuid(), MovieSceneId = scene!.Id, Sequence = 1, Description = "Canonical wide shot.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MovieShots.Add(canonicalShot);
        await db.SaveChangesAsync();
        var take = await service.AddTakeAsync(userId, canonicalShot.Id, new MovieV2TakeRequest { QualityLevel = MovieQualityLevels.Cinematic, AutoDirectorEnabled = true }, CancellationToken.None);
        var secondAct = await service.AddActAsync(userId, movieId, new MovieV2ActRequest { Title = "Act Two" }, CancellationToken.None);
        await service.ReorderAsync(userId, "act", secondAct!.Id, 1, CancellationToken.None);

        var hierarchy = await service.GetHierarchyAsync(userId, movieId, CancellationToken.None);

        Assert.Equal(MovieQualityLevels.Standard, hierarchy!.QualityLevel);
        Assert.False(hierarchy.AutoDirectorEnabled);
        Assert.Equal(secondAct.Id, hierarchy.Acts[0].Id);
        Assert.Equal(act.Id, hierarchy.Acts[1].Id);
        Assert.Equal(sequence.Id, hierarchy.Acts[1].Sequences.Single().Id);
        Assert.Equal(scene.Id, hierarchy.Acts[1].Sequences.Single().Scenes.Single().Id);
        Assert.Equal(take!.Id, hierarchy.Acts[1].Sequences.Single().Scenes.Single().Shots.Single().Takes.Single().Id);
        Assert.Equal(MovieQualityLevels.Cinematic, take.QualityLevel);
        Assert.True(take.AutoDirectorEnabled);
    }

    [Fact]
    public async Task Approval_then_finalize_persists_selected_and_final_take_audit_state()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest(), CancellationToken.None);

        var approved = await service.ApproveTakeAsync(userId, take!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved, Comment = "Looks good." }, CancellationToken.None);
        await service.SelectTakeAsync(userId, take.Id, false, CancellationToken.None);
        await service.SelectTakeAsync(userId, take.Id, true, CancellationToken.None);
        var shot = await db.MovieShots.SingleAsync(item => item.Id == shotId);

        Assert.Equal(MovieTakeStatuses.Approved, approved!.Status);
        Assert.Equal(take.Id, shot.SelectedTakeId);
        Assert.Equal(take.Id, shot.FinalTakeId);
        Assert.NotNull(await db.MovieTakeApprovals.SingleOrDefaultAsync(item => item.MovieTakeId == take.Id && item.Decision == MovieApprovalDecisions.Approved));
        Assert.NotNull(await db.MovieTakes.Where(item => item.Id == take.Id).Select(item => item.FinalizedAt).SingleAsync());
    }

    [Fact]
    public async Task Final_take_claim_rejects_replacing_an_existing_final_cut()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));
        var first = await service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { Label = "First final candidate" }, CancellationToken.None);
        var second = await service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { Label = "Stale final candidate" }, CancellationToken.None);
        await service.ApproveTakeAsync(userId, first!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await service.ApproveTakeAsync(userId, second!.Id, new MovieV2ApprovalRequest { Decision = MovieApprovalDecisions.Approved }, CancellationToken.None);
        await service.SelectTakeAsync(userId, first.Id, true, CancellationToken.None);

        await using var competingDb = CreateDb(connection);
        var competingService = new MovieV2Service(competingDb, new WorkspaceAccessService(competingDb));
        var conflict = await Assert.ThrowsAsync<MovieV2ConflictException>(() => competingService.SelectTakeAsync(userId, second.Id, true, CancellationToken.None));

        Assert.Contains("already committed", conflict.Message, StringComparison.OrdinalIgnoreCase);
        var shot = await competingDb.MovieShots.AsNoTracking().SingleAsync(item => item.Id == shotId);
        Assert.Equal(first.Id, shot.FinalTakeId);
        Assert.Equal(first.Id, shot.SelectedTakeId);
        var persistedSecond = await competingDb.MovieTakes.AsNoTracking().SingleAsync(item => item.Id == second.Id);
        Assert.Equal(MovieTakeStatuses.Approved, persistedSecond.Status);
        Assert.Null(persistedSecond.FinalizedAt);
    }

    [Fact]
    public async Task Direct_take_status_mutation_cannot_forge_approval_or_selection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, _, shotId) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));
        var take = await service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest(), CancellationToken.None);

        var takeId = take!.Id;
        await Assert.ThrowsAsync<MovieV2ValidationException>(() => service.SetStatusAsync(userId, "take", takeId, MovieTakeStatuses.Approved, CancellationToken.None));
        var persisted = await db.MovieTakes.SingleAsync(item => item.Id == takeId);
        Assert.Equal(MovieTakeStatuses.Planned, persisted.Status);
        Assert.Null((await db.MovieShots.SingleAsync(item => item.Id == shotId)).SelectedTakeId);
    }

    [Fact]
    public void Movie_job_projection_redacts_provider_and_upstream_payloads()
    {
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), JobType = GenerationJobTypes.MovieClipGenerate,
            Provider = "secret-provider", ProviderModel = "secret-model", ResultJson = "secret prompt and provider payload",
            ErrorCode = "MOVIE_GENERATION_FAILED", ErrorMessage = "upstream secret", CreatedAt = DateTime.UtcNow,
        };

        var dto = GenerationJobContractMapper.ToMovieDto(job);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Null(dto.ResultJson);
        Assert.Equal("The movie generation operation did not complete.", dto.ErrorMessage);
        Assert.DoesNotContain("secret-provider", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-model", json, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret prompt", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_member_cannot_read_or_mutate_movie_hierarchy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (_, movieId, shotId) = await SeedAsync(db);
        var outsiderId = Guid.NewGuid();
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        Assert.Null(await service.GetHierarchyAsync(outsiderId, movieId, CancellationToken.None));
        Assert.Null(await service.AddTakeAsync(outsiderId, shotId, new MovieV2TakeRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task Workspace_member_without_movie_team_permission_cannot_mutate_hierarchy()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (ownerId, movieId, _) = await SeedAsync(db);
        var memberId = Guid.NewGuid();
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == movieId);
        db.Users.Add(new ApplicationUser { Id = memberId, UserName = "workspace-only", NormalizedUserName = "WORKSPACE-ONLY", DisplayName = "Workspace Only", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, UserId = memberId, Role = WorkspaceRole.Member });
        await db.SaveChangesAsync();
        var service = new MovieV2Service(db, new WorkspaceAccessService(db), new MovieCollaborationAccess(db, new WorkspaceAccessService(db)));

        Assert.Null(await service.AddActAsync(memberId, movieId, new MovieV2ActRequest { Title = "Forged Act" }, CancellationToken.None));
        Assert.NotNull(await service.AddActAsync(ownerId, movieId, new MovieV2ActRequest { Title = "Owner Act" }, CancellationToken.None));
    }

    [Fact]
    public async Task Take_rejects_a_generation_job_that_does_not_match_the_selected_clip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == movieId);
        var now = DateTime.UtcNow;
        var clipJob = new GenerationJob { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, ProjectId = movie.ProjectId, CreatedByUserId = userId, JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Succeeded, InputJson = "{}", CreatedAt = now };
        var otherJob = new GenerationJob { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, ProjectId = movie.ProjectId, CreatedByUserId = userId, JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Succeeded, InputJson = "{}", CreatedAt = now };
        var sceneId = await db.MovieShots.Where(item => item.Id == shotId).Select(item => item.MovieSceneId).SingleAsync();
        var clip = new MovieClip { Id = Guid.NewGuid(), MovieProjectId = movieId, MovieSceneId = sceneId, MovieShotId = shotId, GenerationJobId = clipJob.Id, Status = MovieClipStatuses.Ready, CreatedAt = now, UpdatedAt = now };
        db.AddRange(clipJob, otherJob, clip);
        await db.SaveChangesAsync();
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        await Assert.ThrowsAsync<MovieV2ValidationException>(() => service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { MovieClipId = clip.Id, GenerationJobId = otherJob.Id }, CancellationToken.None));
    }

    [Fact]
    public async Task Take_rejects_archived_or_cross_project_asset_even_when_workspace_matches()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, shotId) = await SeedAsync(db);
        var movie = await db.MovieProjects.SingleAsync(item => item.Id == movieId);
        var otherProject = new Project { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, Name = "Other project", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(otherProject);
        db.Assets.Add(new Asset { Id = Guid.NewGuid(), WorkspaceId = movie.WorkspaceId, ProjectId = otherProject.Id, CreatedByUserId = userId, Name = "foreign", Status = AssetStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        var archivedId = Guid.NewGuid();
        db.Assets.Add(new Asset { Id = archivedId, WorkspaceId = movie.WorkspaceId, ProjectId = movie.ProjectId, CreatedByUserId = userId, Name = "archived", Status = AssetStatus.Archived, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var foreignId = await db.Assets.Where(item => item.Name == "foreign").Select(item => item.Id).SingleAsync();
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        await Assert.ThrowsAsync<MovieV2ValidationException>(() => service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = foreignId }, CancellationToken.None));
        await Assert.ThrowsAsync<MovieV2ValidationException>(() => service.AddTakeAsync(userId, shotId, new MovieV2TakeRequest { AssetId = archivedId }, CancellationToken.None));
    }

    [Fact]
    public async Task Overview_returns_bounded_progress_actions_and_warnings_without_crossing_workspace_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var (userId, movieId, _) = await SeedAsync(db);
        var service = new MovieV2Service(db, new WorkspaceAccessService(db));

        var overview = await service.GetOverviewAsync(userId, movieId, CancellationToken.None);

        Assert.NotNull(overview);
        Assert.Equal(1, overview!.Scenes.Total);
        Assert.Equal(1, overview.Shots.Total);
        Assert.Equal(0, overview.Progress.Production.Completed);
        Assert.Equal("Complete Story", overview.NextActions[0].Label);
        Assert.Contains(overview.Warnings, item => item.Key == "story-not-approved");
        Assert.Contains(overview.RecentActivity, item => item.Module == "scenes");
        Assert.Null(await service.GetOverviewAsync(Guid.NewGuid(), movieId, CancellationToken.None));
    }

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static async Task<(Guid UserId, Guid MovieId, Guid ShotId)> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var shotId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "movie-owner", NormalizedUserName = "MOVIE-OWNER", DisplayName = "Movie Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Movie Workspace", Slug = $"movie-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Foundation", Description = "A movie.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember { Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true, CreatedAt = now, UpdatedAt = now });
        db.MovieScenes.Add(new MovieScene { Id = sceneId, MovieProjectId = movieId, Sequence = 1, Title = "Legacy scene", Summary = "Existing scene.", CreatedAt = now, UpdatedAt = now });
        db.MovieShots.Add(new MovieShot { Id = shotId, MovieSceneId = sceneId, Sequence = 1, Description = "Wide shot.", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return (userId, movieId, shotId);
    }
}

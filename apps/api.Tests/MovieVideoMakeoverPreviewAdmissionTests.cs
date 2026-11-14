using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free acceptance slice for #698: the server-side admission contract for
/// viewing an existing Video Makeover result preview. The evaluator is pure and
/// deterministic; the service layer only reads persisted records. Nothing here calls
/// a provider, queues a job, or renders a transformation.
/// </summary>
public sealed class MovieVideoMakeoverPreviewAdmissionTests
{
    [Fact]
    public void Admits_workspace_member_binding_owned_source_to_finished_result()
    {
        var (project, source, result, job) = CreateHappyPath(out var userId);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.True(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.Admitted, decision.Code);
        Assert.Equal(project.Id, decision.MovieProjectId);
        Assert.Equal(source.Id, decision.SourceAssetId);
        Assert.Equal(result.Id, decision.ResultAssetId);
        // The honest contract: only identifiers of existing records are exposed.
        Assert.DoesNotContain("http", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Denies_principal_outside_the_owning_workspace()
    {
        var (project, source, result, job) = CreateHappyPath(out _);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, false, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PrincipalNotWorkspaceMember, decision.Code);
    }

    [Fact]
    public void Denies_unknown_project_even_for_a_member()
    {
        var (_, source, result, job) = CreateHappyPath(out _);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(null, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ProjectNotFound, decision.Code);
    }

    [Fact]
    public void Denies_missing_source_video_asset()
    {
        var (project, _, result, job) = CreateHappyPath(out _);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, null, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.SourceAssetMissing, decision.Code);
    }

    [Fact]
    public void Denies_missing_result_preview_asset()
    {
        var (project, source, _, _) = CreateHappyPath(out _);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, null, null);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ResultAssetMissing, decision.Code);
    }

    [Fact]
    public void Denies_presenting_the_unmodified_source_as_its_own_result()
    {
        var (project, source, _, _) = CreateHappyPath(out _);

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, source, null);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ResultBindsSourceToItself, decision.Code);
    }

    [Fact]
    public void Denies_source_asset_from_a_foreign_workspace()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        source.WorkspaceId = Guid.NewGuid();

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.AssetCrossWorkspace, decision.Code);
    }

    [Fact]
    public void Denies_result_asset_from_a_foreign_workspace()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        result.WorkspaceId = Guid.NewGuid();

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.AssetCrossWorkspace, decision.Code);
    }

    [Fact]
    public void Denies_asset_bound_to_a_different_project()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        project.ProjectId = Guid.NewGuid();
        result.ProjectId = Guid.NewGuid();

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.AssetCrossProject, decision.Code);
    }

    [Fact]
    public void Denies_non_video_result_asset()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        result.AssetType = AssetTypes.Image;

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.AssetNotVideo, decision.Code);
    }

    [Fact]
    public void Denies_archived_or_unstored_preview_asset()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        result.Status = AssetStatus.Archived;

        var archived = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);
        Assert.False(archived.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PreviewAssetUnavailable, archived.Code);

        result.Status = AssetStatus.Active;
        result.ArchivedAt = null;
        result.StoredFileId = null;
        var unstored = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);
        Assert.False(unstored.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PreviewAssetUnavailable, unstored.Code);
    }

    [Fact]
    public void Denies_result_without_an_existing_generation_binding()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        result.SourceGenerationJobId = null;

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ResultNotBoundToGeneration, decision.Code);
    }

    [Fact]
    public void Denies_unfinished_or_failed_makeover_generation()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        job.Status = GenerationJobStatus.Running;

        var running = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);
        Assert.False(running.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PreviewNotFinished, running.Code);

        job.Status = GenerationJobStatus.Failed;
        var failed = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);
        Assert.False(failed.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PreviewNotFinished, failed.Code);
    }

    [Fact]
    public void Denies_generation_job_from_a_foreign_workspace()
    {
        var (project, source, result, job) = CreateHappyPath(out _);
        job.WorkspaceId = Guid.NewGuid();

        var decision = MovieVideoMakeoverPreviewAdmissionEvaluator.Evaluate(project, true, source, result, job);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ResultJobCrossWorkspace, decision.Code);
    }

    [Fact]
    public async Task Service_admits_owner_and_never_creates_a_generation_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieVideoMakeoverPreviewAdmissionService(db, new WorkspaceAccessService(db));

        var decision = await service.EvaluateAsync(fixture.UserId, fixture.ProjectId, fixture.SourceAssetId, fixture.ResultAssetId, CancellationToken.None);

        Assert.True(decision.Admitted);
        Assert.Equal(fixture.ResultAssetId, decision.ResultAssetId);
        Assert.Equal(fixture.SeededJobCount, await db.GenerationJobs.CountAsync());
        Assert.Equal(0, await db.MovieTakeUpscaleAudits.CountAsync());
    }

    [Fact]
    public async Task Service_denies_non_member_before_touching_assets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieVideoMakeoverPreviewAdmissionService(db, new WorkspaceAccessService(db));
        var strangerId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = strangerId, UserName = "stranger", NormalizedUserName = "STRANGER", DisplayName = "Stranger", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var decision = await service.EvaluateAsync(strangerId, fixture.ProjectId, fixture.SourceAssetId, fixture.ResultAssetId, CancellationToken.None);

        Assert.False(decision.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PrincipalNotWorkspaceMember, decision.Code);
    }

    [Fact]
    public async Task Service_denies_unknown_project_and_unfinished_result()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        var fixture = await SeedAsync(db);
        var service = new MovieVideoMakeoverPreviewAdmissionService(db, new WorkspaceAccessService(db));

        var unknown = await service.EvaluateAsync(fixture.UserId, Guid.NewGuid(), fixture.SourceAssetId, fixture.ResultAssetId, CancellationToken.None);
        Assert.False(unknown.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.ProjectNotFound, unknown.Code);

        var job = await db.GenerationJobs.SingleAsync(item => item.Id == fixture.JobId);
        job.Status = GenerationJobStatus.Queued;
        await db.SaveChangesAsync();
        var unfinished = await service.EvaluateAsync(fixture.UserId, fixture.ProjectId, fixture.SourceAssetId, fixture.ResultAssetId, CancellationToken.None);
        Assert.False(unfinished.Admitted);
        Assert.Equal(MovieVideoMakeoverPreviewAdmissionCodes.PreviewNotFinished, unfinished.Code);
    }

    private sealed record Fixture(
        Guid UserId,
        Guid ProjectId,
        Guid SourceAssetId,
        Guid ResultAssetId,
        Guid JobId,
        int SeededJobCount);

    private static TaslimDbContext CreateDb(SqliteConnection connection) => new(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);

    private static (MovieProject Project, Asset Source, Asset Result, GenerationJob Job) CreateHappyPath(out Guid userId)
    {
        userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var project = new MovieProject { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Makeover project", Description = "Talking-head reel.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now };
        var source = new Asset { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Name = "source.mp4", AssetType = AssetTypes.Video, Status = AssetStatus.Active, StoredFileId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var job = new GenerationJob { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Succeeded, CreatedAt = now };
        var result = new Asset { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Name = "result.mp4", AssetType = AssetTypes.Video, Status = AssetStatus.Active, StoredFileId = Guid.NewGuid(), SourceGenerationJobId = job.Id, CreatedAt = now, UpdatedAt = now };
        return (project, source, result, job);
    }

    private static async Task<Fixture> SeedAsync(TaslimDbContext db)
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "makeover-owner", NormalizedUserName = "MAKEOVER-OWNER", DisplayName = "Makeover Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Makeover Workspace", Slug = $"makeover-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner });
        db.MovieProjects.Add(new MovieProject { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Makeover project", Description = "Talking-head reel.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        var projectId = db.MovieProjects.Local.Single().Id;
        var sourceFile = new StoredFile { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "source.mp4", StoredFileName = "source.mp4", ContentType = "video/mp4", Extension = ".mp4", SizeBytes = 1024, StorageKey = $"source-{Guid.NewGuid():N}", Status = StoredFileStatus.Ready, CreatedAt = now };
        var resultFile = new StoredFile { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, OriginalFileName = "result.mp4", StoredFileName = "result.mp4", ContentType = "video/mp4", Extension = ".mp4", SizeBytes = 2048, StorageKey = $"result-{Guid.NewGuid():N}", Status = StoredFileStatus.Ready, CreatedAt = now };
        db.StoredFiles.AddRange(sourceFile, resultFile);
        db.GenerationJobs.Add(new GenerationJob { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, JobType = GenerationJobTypes.MovieClipGenerate, Status = GenerationJobStatus.Succeeded, Title = "Makeover run", CreatedAt = now, CompletedAt = now });
        await db.SaveChangesAsync();
        var jobId = db.GenerationJobs.Local.Single().Id;
        db.Assets.Add(new Asset { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Name = "source.mp4", AssetType = AssetTypes.Video, Status = AssetStatus.Active, StoredFileId = sourceFile.Id, CreatedAt = now, UpdatedAt = now });
        db.Assets.Add(new Asset { Id = Guid.NewGuid(), WorkspaceId = workspaceId, CreatedByUserId = userId, Name = "result.mp4", AssetType = AssetTypes.Video, Status = AssetStatus.Active, StoredFileId = resultFile.Id, SourceGenerationJobId = jobId, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        return new Fixture(userId, projectId, db.Assets.Local.Single(a => a.Name == "source.mp4").Id, db.Assets.Local.Single(a => a.Name == "result.mp4").Id, jobId, 1);
    }
}

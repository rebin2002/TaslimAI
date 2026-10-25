using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieDialogueQueueIdempotencyTests
{
    [Fact]
    public async Task Replaying_the_same_idempotency_key_reuses_one_dialogue_take_and_job()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new TaslimDbContext(new DbContextOptionsBuilder<TaslimDbContext>().UseSqlite(connection).Options);
        var fixture = await SeedAsync(db);
        var job = await db.GenerationJobs.SingleAsync(item => item.Id == fixture.JobId);
        var service = new MovieDialogueProductionService(
            db,
            new MovieAuthorizationService(new MovieCollaborationAccess(db, new WorkspaceAccessService(db))),
            new ReplayJobService(job));

        var first = await service.QueueTakeAsync(fixture.UserId, fixture.LineId, new MovieDialogueTakeRequest { Label = "Retry-safe take" }, "dialogue-retry-key", CancellationToken.None);
        var replay = await service.QueueTakeAsync(fixture.UserId, fixture.LineId, new MovieDialogueTakeRequest { Label = "Retry-safe take" }, "dialogue-retry-key", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(replay);
        Assert.Equal(first!.Take.Id, replay!.Take.Id);
        Assert.Equal(first.Job.Id, replay.Job.Id);
        Assert.Single(await db.MovieDialogueTakes.Where(item => item.MovieDialogueLineId == fixture.LineId).ToListAsync());
        Assert.Equal(1, await db.MovieDialogueTakes.CountAsync(item => item.GenerationJobId == fixture.JobId));
    }

    private static async Task<Fixture> SeedAsync(TaslimDbContext db)
    {
        db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var movieId = Guid.NewGuid();
        var clipId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, UserName = "dialogue-idempotency-owner", NormalizedUserName = "DIALOGUE-IDEMPOTENCY-OWNER", DisplayName = "Dialogue Idempotency Owner", CreatedAt = now, UpdatedAt = now });
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Dialogue Idempotency Workspace", Slug = $"dialogue-idempotency-{Guid.NewGuid():N}", Type = WorkspaceType.Personal, CreatedAt = now, UpdatedAt = now });
        db.WorkspaceMembers.Add(new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId, Role = WorkspaceRole.Owner, JoinedAt = now });
        db.MovieProjects.Add(new MovieProject { Id = movieId, WorkspaceId = workspaceId, CreatedByUserId = userId, Title = "Dialogue Idempotency Movie", Description = "Retry fixture.", DurationSeconds = 30, CreatedAt = now, UpdatedAt = now, Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), UpdatedAt = now } });
        db.MovieTeamMembers.Add(new MovieTeamMember
        {
            Id = Guid.NewGuid(), MovieProjectId = movieId, UserId = userId, Role = MovieTeamRoles.Producer, IsProjectOwner = true,
            PermissionOverrides = [new MovieTeamMemberPermission { Id = Guid.NewGuid(), Permission = MoviePermissions.Generate, Granted = true }],
            CreatedAt = now, UpdatedAt = now,
        });
        db.MovieClips.Add(new MovieClip { Id = clipId, MovieProjectId = movieId, Status = MovieClipStatuses.Ready, CreatedAt = now, UpdatedAt = now });
        db.MovieDialogueLines.Add(new MovieDialogueLine
        {
            Id = lineId, MovieClipId = clipId, Sequence = 1, SpeakerName = "Mara", Language = MovieDialogueLanguages.English,
            Text = "The retry must not duplicate the take.", StartMilliseconds = 0, EndMilliseconds = 1_000, Status = MovieDialogueLineStatuses.Draft,
            CreatedByUserId = userId, CreatedAt = now, UpdatedAt = now,
        });
        db.GenerationJobs.Add(new GenerationJob
        {
            Id = jobId, WorkspaceId = workspaceId, CreatedByUserId = userId, JobType = GenerationJobTypes.MovieDialogueVoiceGenerate,
            Status = GenerationJobStatus.Pending, Title = "Retry-safe take", InputJson = "{}", IdempotencyKey = "dialogue-retry-key",
            RequestFingerprint = "fixture", CreatedAt = now,
        });
        await db.SaveChangesAsync();
        return new(userId, lineId, jobId);
    }

    private sealed record Fixture(Guid UserId, Guid LineId, Guid JobId);

    private sealed class ReplayJobService(GenerationJob job) : IGenerationJobService
    {
        public Task<GenerationJob> CreateAsync(Guid userId, CreateGenerationJobRequest request, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null, Guid? retryOfJobId = null) => Task.FromResult(job);
        public Task<GenerationJob?> RetryAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default, string? idempotencyKey = null, string? requestId = null) => throw new NotSupportedException();
        public Task<GenerationJob?> GetAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GenerationJobListDto?> ListAsync(Guid userId, GenerationJobFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GenerationJobCancelResult> CancelAsync(Guid userId, Guid jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

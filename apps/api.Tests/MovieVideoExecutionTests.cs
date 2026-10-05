using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class MovieVideoExecutionTests
{
    [Fact]
    public async Task Polling_timeout_cancels_remote_operation_and_marks_local_execution_timed_out()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TaslimDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Movie timeout test",
            Slug = $"movie-timeout-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"movie-timeout-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"MOVIE-TIMEOUT-{Guid.NewGuid():N}",
            Email = "movie-timeout@example.test",
            NormalizedEmail = "MOVIE-TIMEOUT@EXAMPLE.TEST",
            DisplayName = "Movie timeout",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            Title = "Timeout movie",
            Description = "A deterministic timeout fixture.",
            DurationSeconds = 5,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), MovieProjectId = Guid.Empty, UpdatedAt = now },
        };
        movie.Guide.MovieProjectId = movie.Id;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Status = GenerationJobStatus.Running,
            InputJson = "{}",
            ConcurrencyToken = Guid.NewGuid(),
            CreatedAt = now,
        };
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            GenerationJobId = job.Id,
            Status = MovieClipStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var input = new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            movie.Id,
            clip.Id,
            null,
            null,
            "A slow camera move.",
            5,
            "16:9",
            "cinematic",
            "en",
            null,
            null,
            null,
            null);
        job.InputJson = System.Text.Json.JsonSerializer.Serialize(input);

        db.AddRange(
            workspace,
            user,
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            },
            movie,
            job,
            clip);
        await db.SaveChangesAsync();

        var provider = new TimeoutMovieVideoProvider();
        var options = new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxStatusPolls = 1,
            StatusPollIntervalSeconds = 1,
            MaxTransientRetries = 0,
        };
        var handler = new MovieVideoGenerationJobHandler(
            db,
            provider,
            new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options),
            NullLogger<MovieVideoGenerationJobHandler>.Instance);

        await Assert.ThrowsAsync<MovieVideoProviderTimeoutException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(1, provider.CancelCalls);
        var execution = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Equal("provider-job", execution.ProviderJobId);
        Assert.Equal(MovieVideoExecutionStatuses.TimedOut, execution.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieProviderTimeout, execution.LastErrorCode);
        Assert.Equal(MovieClipStatuses.Failed, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Stale_submission_cancels_remote_operation_before_rethrowing_ownership_loss()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new TaslimDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = "Movie stale submission test",
            Slug = $"movie-stale-submission-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"movie-stale-submission-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"MOVIE-STALE-SUBMISSION-{Guid.NewGuid():N}",
            Email = "movie-stale-submission@example.test",
            NormalizedEmail = "MOVIE-STALE-SUBMISSION@EXAMPLE.TEST",
            DisplayName = "Movie stale submission",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            Title = "Stale submission movie",
            Description = "A deterministic stale-worker fixture.",
            DurationSeconds = 5,
            CreatedAt = now,
            UpdatedAt = now,
            Guide = new MovieContinuityGuide { Id = Guid.NewGuid(), MovieProjectId = Guid.Empty, UpdatedAt = now },
        };
        movie.Guide.MovieProjectId = movie.Id;
        var job = new GenerationJob
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Status = GenerationJobStatus.Running,
            InputJson = "{}",
            ConcurrencyToken = Guid.NewGuid(),
            CreatedAt = now,
        };
        var clip = new MovieClip
        {
            Id = Guid.NewGuid(),
            MovieProjectId = movie.Id,
            GenerationJobId = job.Id,
            Status = MovieClipStatuses.Queued,
            CreatedAt = now,
            UpdatedAt = now,
        };
        job.InputJson = System.Text.Json.JsonSerializer.Serialize(new MovieGenerationInput(
            MovieStudioOperations.SceneClip,
            movie.Id,
            clip.Id,
            null,
            null,
            "A slow camera move.",
            5,
            "16:9",
            "cinematic",
            "en",
            null,
            null,
            null,
            null));
        db.AddRange(
            workspace,
            user,
            new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                JoinedAt = now,
            },
            movie,
            job,
            clip);
        await db.SaveChangesAsync();

        var provider = new StaleSubmissionMovieVideoProvider(db, job.Id);
        var options = new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxTransientRetries = 0,
        };
        var handler = new MovieVideoGenerationJobHandler(
            db,
            provider,
            new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options),
            NullLogger<MovieVideoGenerationJobHandler>.Instance);

        await Assert.ThrowsAsync<MovieVideoStaleWorkerException>(() => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(1, provider.CancelCalls);
        Assert.Equal("provider-job", provider.CancelledProviderJobId);
        Assert.False(provider.CancelCancellationWasRequested);
        var execution = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Null(execution.ProviderJobId);
        Assert.Equal(MovieVideoExecutionStatuses.Submitted, execution.Status);
        var persistedJob = await db.GenerationJobs.AsNoTracking().SingleAsync(item => item.Id == job.Id);
        Assert.Equal(GenerationJobStatus.Running, persistedJob.Status);
        Assert.NotEqual(job.ConcurrencyToken, persistedJob.ConcurrencyToken);
    }

    private sealed class TimeoutMovieVideoProvider : IMovieVideoProvider
    {
        public string Key => "timeout-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        public int CancelCalls { get; private set; }

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieVideoSubmission("provider-job"));

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieVideoProviderStatus(MovieVideoProviderJobStatus.Running, 40));

        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderOutput>(new InvalidOperationException("retrieve should not run after timeout"));

        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class StaleSubmissionMovieVideoProvider(TaslimDbContext db, Guid jobId) : IMovieVideoProvider
    {
        public string Key => "stale-submission-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        public int CancelCalls { get; private set; }
        public string? CancelledProviderJobId { get; private set; }
        public bool CancelCancellationWasRequested { get; private set; }

        public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            await db.GenerationJobs
                .Where(item => item.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConcurrencyToken, Guid.NewGuid()), cancellationToken);
            return new MovieVideoSubmission("provider-job");
        }

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderStatus>(new InvalidOperationException("status should not run after stale submission"));

        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderOutput>(new InvalidOperationException("retrieve should not run after stale submission"));

        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            CancelledProviderJobId = providerJobId;
            CancelCancellationWasRequested = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }
    }
}

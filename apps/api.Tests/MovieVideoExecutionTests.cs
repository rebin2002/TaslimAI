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
            null,
            SourceResolution: "720p",
            TargetResolution: "1080p",
            QualityTier: "cinema");
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
        Assert.NotNull(provider.SubmittedRequest);
        Assert.Equal("720p", provider.SubmittedRequest.SourceResolution);
        Assert.Equal("1080p", provider.SubmittedRequest.TargetResolution);
        Assert.Equal("cinema", provider.SubmittedRequest.QualityTier);
        Assert.Null(provider.SubmittedRequest.MasterResolution);
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

    [Fact]
    public async Task Provider_failure_after_submission_cancels_active_remote_operation()
    {
        var fixture = await CreateRunningMovieJobAsync("Movie provider failure test", "A deterministic provider-failure fixture.");
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var provider = new ProviderFailureMovieVideoProvider();
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

        await Assert.ThrowsAsync<MovieVideoProviderException>(() => handler.ExecuteAsync(fixture.Job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(1, provider.CancelCalls);
        var execution = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Equal("provider-job", execution.ProviderJobId);
        Assert.Equal(MovieVideoExecutionStatuses.ProviderUnavailable, execution.Status);
        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnavailable, execution.LastErrorCode);
        Assert.Equal(MovieClipStatuses.Failed, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("1080p", "720p", "Standard", "native", 5, false)]
    [InlineData("720p", "1080p", "Standard", "native", 5, false)]
    [InlineData("720p", "720p", "Studio", "native", 5, false)]
    [InlineData("720p", "720p", "Standard", "upscale", 5, false)]
    [InlineData("720p", "720p", "Standard", "native", 11, false)]
    [InlineData("720p", "720p", "Standard", "native", 5, true)]
    // Older selective-regeneration jobs omit the priced execution profile.
    // Missing metadata must never be treated as permission to submit paid work.
    [InlineData(null, null, null, null, 5, false)]
    [InlineData("720p", "720p", "Standard", null, 5, false)]
    [InlineData("720p", "720p", null, "native", 5, false)]
    public async Task Worker_rejects_unsupported_priced_scene_intent_before_provider_submission(
        string? sourceResolution, string? targetResolution, string? qualityTier, string? processingPath,
        int durationSeconds, bool upscalingRequested)
    {
        var fixture = await CreateRunningMovieJobAsync("Movie execution capability test", "No paid provider submission allowed.");
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(fixture.Job.InputJson)!;
        fixture.Job.InputJson = System.Text.Json.JsonSerializer.Serialize(input with
        {
            SourceResolution = sourceResolution,
            TargetResolution = targetResolution,
            QualityTier = qualityTier,
            ProcessingPath = processingPath,
            DurationSeconds = durationSeconds,
            UpscalingRequested = upscalingRequested,
        });
        await db.SaveChangesAsync();

        var provider = new GuardedSceneMovieVideoProvider();
        var options = new MovieVideoOptions { Enabled = true, ProviderKey = provider.Key };
        var handler = new MovieVideoGenerationJobHandler(
            db, provider, new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options), NullLogger<MovieVideoGenerationJobHandler>.Instance);

        var failure = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => handler.ExecuteAsync(fixture.Job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, failure.Code);
        Assert.Equal(0, provider.SubmitCalls);
        Assert.Empty(await db.MovieVideoProviderExecutions.AsNoTracking().ToListAsync());
        Assert.Equal(MovieClipStatuses.Failed, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(GenerationJobStatus.Running, (await db.GenerationJobs.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Worker_rejects_unimplemented_scene_clip_mastering_before_provider_submission()
    {
        var fixture = await CreateRunningMovieJobAsync(
            "Movie mastering intent safety", "An older queued clip with a priced master output.");
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(fixture.Job.InputJson)!;
        fixture.Job.InputJson = System.Text.Json.JsonSerializer.Serialize(input with
        {
            SourceResolution = "720p",
            TargetResolution = "720p",
            QualityTier = DirectorQualityLevels.Standard,
            ProcessingPath = "native",
            MasterResolution = "1080p",
        });
        await db.SaveChangesAsync();

        var provider = new GuardedSceneMovieVideoProvider();
        var options = new MovieVideoOptions { Enabled = true, ProviderKey = provider.Key };
        var handler = new MovieVideoGenerationJobHandler(
            db, provider, new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options), NullLogger<MovieVideoGenerationJobHandler>.Instance);

        var error = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => handler.ExecuteAsync(fixture.Job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnsupportedRequest, error.Code);
        Assert.Equal(0, provider.SubmitCalls);
        Assert.Empty(await db.MovieVideoProviderExecutions.AsNoTracking().ToListAsync());
        Assert.Equal(MovieClipStatuses.Failed, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Transient_submission_failure_is_not_retried_without_provider_idempotency()
    {
        var fixture = await CreateRunningMovieJobAsync(
            "Movie submit ambiguity test", "A synthetic transient submission response.");
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var provider = new TransientSubmissionMovieVideoProvider();
        var options = new MovieVideoOptions
        {
            Enabled = true,
            ProviderKey = provider.Key,
            MaxTransientRetries = 3,
            RetryBaseDelaySeconds = 1,
        };
        var handler = new MovieVideoGenerationJobHandler(
            db, provider, new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options), NullLogger<MovieVideoGenerationJobHandler>.Instance);

        var failure = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => handler.ExecuteAsync(fixture.Job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieProviderUnavailable, failure.Code);
        Assert.False(failure.IsTransient);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(0, provider.CancelCalls);
        var execution = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Null(execution.ProviderJobId);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(MovieVideoExecutionStatuses.ProviderUnavailable, execution.Status);
        Assert.Equal(MovieClipStatuses.Failed, (await db.MovieClips.AsNoTracking().SingleAsync()).Status);

        // A later worker may reclaim the durable job, but must not send a
        // second potentially billable request after an ambiguous submission.
        db.ChangeTracker.Clear();
        var newToken = Guid.NewGuid();
        await db.GenerationJobs.Where(item => item.Id == fixture.Job.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConcurrencyToken, newToken));
        var reclaimedJob = await db.GenerationJobs.SingleAsync(item => item.Id == fixture.Job.Id);
        var reclaimedHandler = new MovieVideoGenerationJobHandler(
            db, provider, new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options), NullLogger<MovieVideoGenerationJobHandler>.Instance);
        var refused = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => reclaimedHandler.ExecuteAsync(reclaimedJob, new Progress<int>(), CancellationToken.None));
        Assert.Equal(GenerationJobErrorCodes.MovieGenerationFailed, refused.Code);
        Assert.False(refused.IsTransient);
        Assert.Equal(1, provider.SubmitCalls);
        Assert.Equal(1, (await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task Crash_after_durable_submit_marker_does_not_issue_a_second_provider_request()
    {
        var fixture = await CreateRunningMovieJobAsync(
            "Movie crash recovery test", "No new submit after an unknown remote outcome.");
        await using var connection = fixture.Connection;
        await using var db = fixture.Db;
        var provider = new TransientSubmissionMovieVideoProvider();
        var options = new MovieVideoOptions { Enabled = true, ProviderKey = provider.Key };
        var store = new MovieVideoExecutionStore(db, Options.Create(options));

        // Simulate process death after reserving the submission attempt but
        // before its HTTP response or provider job id is durably recorded.
        var input = System.Text.Json.JsonSerializer.Deserialize<MovieGenerationInput>(fixture.Job.InputJson)!;
        var execution = await store.GetOrCreateAsync(fixture.Job, input.MovieClipId, provider.Key, CancellationToken.None);
        await store.MarkSubmissionAttemptAsync(execution, fixture.Job.ConcurrencyToken, CancellationToken.None);
        Assert.Equal(1, (await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.Equal(0, provider.SubmitCalls);

        db.ChangeTracker.Clear();
        var newToken = Guid.NewGuid();
        await db.GenerationJobs.Where(item => item.Id == fixture.Job.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ConcurrencyToken, newToken));
        var reclaimedJob = await db.GenerationJobs.SingleAsync(item => item.Id == fixture.Job.Id);
        var reclaimedHandler = new MovieVideoGenerationJobHandler(
            db, provider, new MovieVideoExecutionStore(db, Options.Create(options)),
            Options.Create(options), NullLogger<MovieVideoGenerationJobHandler>.Instance);
        var refused = await Assert.ThrowsAsync<MovieVideoProviderException>(
            () => reclaimedHandler.ExecuteAsync(reclaimedJob, new Progress<int>(), CancellationToken.None));

        Assert.Equal(GenerationJobErrorCodes.MovieGenerationFailed, refused.Code);
        Assert.False(refused.IsTransient);
        Assert.Equal(0, provider.SubmitCalls);
        var persisted = await db.MovieVideoProviderExecutions.AsNoTracking().SingleAsync();
        Assert.Equal(1, persisted.AttemptCount);
        Assert.Null(persisted.ProviderJobId);
    }

    private static async Task<(SqliteConnection Connection, TaslimDbContext Db, GenerationJob Job)> CreateRunningMovieJobAsync(string title, string description)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new TaslimDbContext(dbOptions);
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"movie-provider-failure-{Guid.NewGuid():N}@example.test",
            NormalizedUserName = $"MOVIE-PROVIDER-FAILURE-{Guid.NewGuid():N}",
            Email = "movie-provider-failure@example.test",
            NormalizedEmail = "MOVIE-PROVIDER-FAILURE@EXAMPLE.TEST",
            DisplayName = "Movie provider failure",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = title,
            Slug = $"movie-provider-failure-{Guid.NewGuid():N}",
            Type = WorkspaceType.Personal,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var movie = new MovieProject
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            CreatedByUserId = user.Id,
            Title = title,
            Description = description,
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
            "A provider failure camera move.",
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
        return (connection, db, job);
    }

    private sealed class TimeoutMovieVideoProvider : IMovieVideoProvider
    {
        public string Key => "timeout-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        // Test double intentionally supports the fixture's arbitrary intent;
        // execution forwarding, timeout and cancellation remain under test.
        public MovieClipDurationRange? SceneClipDurationRange => new(1, 3600);
        public bool SupportsSceneClipCostIntent(string sourceResolution, string targetResolution,
            string qualityTier, string processingPath, string aspectRatio) => true;
        public int CancelCalls { get; private set; }
        public MovieVideoGenerationRequest? SubmittedRequest { get; private set; }

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            SubmittedRequest = request;
            return Task.FromResult(new MovieVideoSubmission("provider-job"));
        }

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
        // Test double intentionally supports the fixture's arbitrary intent;
        // execution forwarding, timeout and cancellation remain under test.
        public MovieClipDurationRange? SceneClipDurationRange => new(1, 3600);
        public bool SupportsSceneClipCostIntent(string sourceResolution, string targetResolution,
            string qualityTier, string processingPath, string aspectRatio) => true;
        public int CancelCalls { get; private set; }
        public string? CancelledProviderJobId { get; private set; }
        public bool CancelCancellationWasRequested { get; private set; }

        public async Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            var staleToken = Guid.NewGuid();
            await db.GenerationJobs
                .Where(item => item.Id == jobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ConcurrencyToken, staleToken), cancellationToken);
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

    private sealed class GuardedSceneMovieVideoProvider : IMovieVideoProvider
    {
        public string Key => "guarded-scene-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        public MovieClipDurationRange? SceneClipDurationRange => new(2, 10);
        public int SubmitCalls { get; private set; }

        public bool SupportsSceneClipCostIntent(string sourceResolution, string targetResolution,
            string qualityTier, string processingPath, string aspectRatio) =>
            string.Equals(sourceResolution, "720p", StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetResolution, "720p", StringComparison.OrdinalIgnoreCase)
            && string.Equals(qualityTier, DirectorQualityLevels.Standard, StringComparison.OrdinalIgnoreCase)
            && string.Equals(processingPath, "native", StringComparison.OrdinalIgnoreCase)
            && aspectRatio == "16:9";

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            throw new InvalidOperationException("Unsupported intent must not reach a provider submission.");
        }

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderStatus>(new InvalidOperationException("No status polling is expected."));
        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderOutput>(new InvalidOperationException("No output retrieval is expected."));
        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TransientSubmissionMovieVideoProvider : IMovieVideoProvider
    {
        public string Key => "ambiguous-submit-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        public MovieClipDurationRange? SceneClipDurationRange => new(1, 3600);
        public bool SupportsSceneClipCostIntent(string sourceResolution, string targetResolution,
            string qualityTier, string processingPath, string aspectRatio) => true;
        public int SubmitCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken)
        {
            SubmitCalls++;
            return Task.FromException<MovieVideoSubmission>(
                new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, true));
        }

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderStatus>(new InvalidOperationException("Status must not be polled after an ambiguous submit."));
        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderOutput>(new InvalidOperationException("No output should be retrieved."));
        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ProviderFailureMovieVideoProvider : IMovieVideoProvider
    {
        public string Key => "provider-failure-test";
        public bool IsAvailable => true;
        public IReadOnlyCollection<string> SupportedOperations { get; } = [MovieStudioOperations.SceneClip];
        // Test double intentionally supports the fixture's arbitrary intent;
        // execution forwarding, timeout and cancellation remain under test.
        public MovieClipDurationRange? SceneClipDurationRange => new(1, 3600);
        public bool SupportsSceneClipCostIntent(string sourceResolution, string targetResolution,
            string qualityTier, string processingPath, string aspectRatio) => true;
        public int CancelCalls { get; private set; }

        public Task<MovieVideoSubmission> SubmitAsync(MovieVideoGenerationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new MovieVideoSubmission("provider-job"));

        public Task<MovieVideoProviderStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderStatus>(new MovieVideoProviderException(GenerationJobErrorCodes.MovieProviderUnavailable, false));

        public Task<MovieVideoProviderOutput> RetrieveAsync(string providerJobId, MovieVideoProviderStatus status, CancellationToken cancellationToken) =>
            Task.FromException<MovieVideoProviderOutput>(new InvalidOperationException("retrieve should not run after provider failure"));

        public Task CancelAsync(string providerJobId, CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.IsCancellationRequested);
            CancelCalls++;
            return Task.CompletedTask;
        }
    }
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public sealed class MovieVideoOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string ApiBaseUrl { get; set; } = "https://api.dev.runwayml.com/v1/";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gen4.5";
    public int ProviderTimeoutSeconds { get; set; } = 30;
    public decimal CreditUsd { get; set; } = 0.01m;
    public int StatusPollIntervalSeconds { get; set; } = 5;
    public int MaxStatusPolls { get; set; } = 120;
    public int MaxTransientRetries { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 2;
    public int RetryMaxDelaySeconds { get; set; } = 30;
    public int ClaimLeaseMinutes { get; set; } = 15;
    public long MaxOutputBytes { get; set; } = 250 * 1024 * 1024;
}

public static class MovieVideoExecutionStatuses
{
    public const string Submitted = "Submitted";
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string TimedOut = "TimedOut";
    public const string ProviderUnavailable = "ProviderUnavailable";
    public const string Generating = "Generating";
    public const string Completed = "Completed";
}

public sealed class MovieVideoProviderExecution
{
    public Guid Id { get; set; }
    public Guid GenerationJobId { get; set; }
    public Guid MovieClipId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string? ProviderJobId { get; set; }
    public string Status { get; set; } = MovieVideoExecutionStatuses.Submitted;
    public int AttemptCount { get; set; }
    public int PollCount { get; set; }
    public int ProgressPercent { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? NextPollAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public GenerationJob GenerationJob { get; set; } = null!;
    public MovieClip MovieClip { get; set; } = null!;
}

public sealed class MovieVideoExecutionStore(TaslimDbContext db, IOptions<MovieVideoOptions> options)
{
    private readonly MovieVideoOptions settings = options.Value;

    public async Task<MovieVideoProviderExecution> GetOrCreateAsync(GenerationJob job, Guid movieClipId, string providerKey, CancellationToken cancellationToken)
    {
        var existing = await db.MovieVideoProviderExecutions.FirstOrDefaultAsync(item => item.GenerationJobId == job.Id, cancellationToken);
        if (existing is not null) return existing;

        var now = DateTime.UtcNow;
        var execution = new MovieVideoProviderExecution
        {
            Id = Guid.NewGuid(),
            GenerationJobId = job.Id,
            MovieClipId = movieClipId,
            ProviderKey = providerKey,
            Status = MovieVideoExecutionStatuses.Submitted,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MovieVideoProviderExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);
        return execution;
    }

    public async Task TouchAsync(Guid jobId, Guid concurrencyToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var renewed = await db.GenerationJobs.Where(item => item.Id == jobId && item.Status == GenerationJobStatus.Running && item.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClaimExpiresAt, now.AddMinutes(Math.Clamp(settings.ClaimLeaseMinutes, 1, 120))), cancellationToken);
        if (renewed == 0) throw new MovieVideoStaleWorkerException();
    }

    public async Task PersistSubmittedAsync(MovieVideoProviderExecution execution, Guid concurrencyToken, string providerJobId, CancellationToken cancellationToken)
    {
        execution.ProviderJobId = providerJobId;
        var now = DateTime.UtcNow;
        var persisted = await db.MovieVideoProviderExecutions
            .Where(item => item.Id == execution.Id && item.GenerationJobId == execution.GenerationJobId && item.GenerationJob.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ProviderJobId, providerJobId)
                .SetProperty(item => item.Status, MovieVideoExecutionStatuses.Queued)
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (persisted == 0) throw new MovieVideoStaleWorkerException();
        await db.MovieClips.Where(item => item.Id == execution.MovieClipId && db.GenerationJobs.Any(job => job.Id == execution.GenerationJobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, MovieClipStatuses.Generating)
                .SetProperty(item => item.ProviderKey, execution.ProviderKey)
                .SetProperty(item => item.ProviderClipId, providerJobId)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task PersistStatusAsync(MovieVideoProviderExecution execution, Guid concurrencyToken, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        var executionStatus = status.Status switch
        {
            MovieVideoProviderJobStatus.Submitted => MovieVideoExecutionStatuses.Queued,
            MovieVideoProviderJobStatus.Queued => MovieVideoExecutionStatuses.Queued,
            MovieVideoProviderJobStatus.Running => MovieVideoExecutionStatuses.Generating,
            MovieVideoProviderJobStatus.Succeeded => MovieVideoExecutionStatuses.Completed,
            MovieVideoProviderJobStatus.Failed => MovieVideoExecutionStatuses.Failed,
            MovieVideoProviderJobStatus.Cancelled => MovieVideoExecutionStatuses.Cancelled,
            _ => MovieVideoExecutionStatuses.Generating,
        };
        var now = DateTime.UtcNow;
        var terminalStatus = status.Status == MovieVideoProviderJobStatus.Succeeded || status.Status == MovieVideoProviderJobStatus.Failed || status.Status == MovieVideoProviderJobStatus.Cancelled;
        var persisted = await db.MovieVideoProviderExecutions
            .Where(item => item.Id == execution.Id && item.GenerationJobId == execution.GenerationJobId && item.GenerationJob.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, executionStatus)
                .SetProperty(item => item.ProgressPercent, Math.Clamp(status.ProgressPercent, 0, 100))
                .SetProperty(item => item.PollCount, item => item.PollCount + 1)
                .SetProperty(item => item.LastErrorCode, item => status.Status == MovieVideoProviderJobStatus.Failed ? GenerationJobErrorCodes.MovieGenerationFailed : status.Status == MovieVideoProviderJobStatus.Cancelled ? GenerationJobErrorCodes.MovieCancelled : item.LastErrorCode)
                .SetProperty(item => item.NextPollAt, item => terminalStatus ? null : now.AddSeconds(Math.Clamp(settings.StatusPollIntervalSeconds, 1, 300)))
                .SetProperty(item => item.CompletedAt, item => terminalStatus ? now : item.CompletedAt)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (persisted == 0) throw new MovieVideoStaleWorkerException();
    }

    public async Task MarkFailedAsync(Guid jobId, Guid concurrencyToken, string code, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var executionStatus = code == GenerationJobErrorCodes.MovieProviderUnavailable ? MovieVideoExecutionStatuses.ProviderUnavailable : code == GenerationJobErrorCodes.MovieProviderTimeout ? MovieVideoExecutionStatuses.TimedOut : MovieVideoExecutionStatuses.Failed;
        await db.MovieVideoProviderExecutions.Where(item => item.GenerationJobId == jobId && item.GenerationJob.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, executionStatus).SetProperty(item => item.LastErrorCode, code).SetProperty(item => item.CompletedAt, now).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        await db.MovieClips.Where(item => item.GenerationJobId == jobId && db.GenerationJobs.Any(job => job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieClipStatuses.Failed).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid jobId, Guid concurrencyToken, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieVideoProviderExecutions.Where(item => item.GenerationJobId == jobId && item.GenerationJob.Status == GenerationJobStatus.Running && item.GenerationJob.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieVideoExecutionStatuses.Cancelled).SetProperty(item => item.CompletedAt, now).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        await db.MovieClips.Where(item => item.GenerationJobId == jobId && db.GenerationJobs.Any(job => job.Id == jobId && job.Status == GenerationJobStatus.Running && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieClipStatuses.Cancelled).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task MarkReadyAsync(Guid jobId, Guid concurrencyToken, Guid? assetId, Guid? storedFileId, int? durationSeconds, string? metadataJson, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.MovieVideoProviderExecutions.Where(item => item.GenerationJobId == jobId && item.GenerationJob.Status == GenerationJobStatus.Succeeded && item.GenerationJob.ConcurrencyToken == concurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieVideoExecutionStatuses.Completed).SetProperty(item => item.ProgressPercent, 100).SetProperty(item => item.CompletedAt, now).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        await db.MovieClips.Where(item => item.GenerationJobId == jobId && db.GenerationJobs.Any(job => job.Id == jobId && job.Status == GenerationJobStatus.Succeeded && job.ConcurrencyToken == concurrencyToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieClipStatuses.Ready).SetProperty(item => item.AssetId, assetId).SetProperty(item => item.StoredFileId, storedFileId).SetProperty(item => item.DurationSeconds, item => durationSeconds ?? item.DurationSeconds).SetProperty(item => item.MetadataJson, metadataJson).SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }
}

public sealed class MovieVideoGenerationJobHandler(
    TaslimDbContext db,
    IMovieVideoProvider provider,
    MovieVideoExecutionStore executions,
    IOptions<MovieVideoOptions> options,
    ILogger<MovieVideoGenerationJobHandler> logger) : IGenerationJobHandler
{
    private readonly MovieVideoOptions settings = options.Value;

    public bool CanHandle(string jobType) => GenerationJobTypes.MovieTypes.Contains(jobType);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var input = Deserialize(job.InputJson);
        var clip = await db.MovieClips.Include(item => item.MovieProject).FirstOrDefaultAsync(item => item.Id == input.MovieClipId && item.MovieProjectId == input.MovieProjectId, cancellationToken)
            ?? throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false, "The movie clip could not be found.");
        if (!provider.IsAvailable || !provider.SupportedOperations.Contains(input.Operation, StringComparer.OrdinalIgnoreCase))
            throw new MovieProviderUnavailableException();

        var request = new MovieVideoGenerationRequest(
            input.Operation,
            job.Id,
            input.MovieProjectId,
            input.MovieClipId,
            input.MovieSceneId,
            input.MovieShotId,
            input.Description,
            input.DurationSeconds,
            input.AspectRatio,
            input.Style,
            input.Language,
            input.AdditionalInstructions,
            input.ContinuityGuideJson,
            input.SceneJson,
            input.ShotJson,
            input.SourceImageUri,
            input.ContinuationProviderJobId,
            input.WorldContextJson);
        var execution = await executions.GetOrCreateAsync(job, clip.Id, provider.Key, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        progress.Report(5);

        try
        {
            if (string.IsNullOrWhiteSpace(execution.ProviderJobId))
            {
                var submission = await WithTransientRetriesAsync(
                    token => provider.SubmitAsync(request, token),
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(submission.ProviderJobId) || submission.ProviderJobId.Length > 240)
                    throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false);
                await executions.PersistSubmittedAsync(execution, job.ConcurrencyToken, submission.ProviderJobId, cancellationToken);
            }

            MovieVideoProviderStatus? terminal = null;
            for (var poll = 0; poll < Math.Clamp(settings.MaxStatusPolls, 1, 10_000); poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await executions.TouchAsync(job.Id, job.ConcurrencyToken, cancellationToken);
                var status = await WithTransientRetriesAsync(token => provider.GetStatusAsync(execution.ProviderJobId!, token), cancellationToken);
                await executions.PersistStatusAsync(execution, job.ConcurrencyToken, status, cancellationToken);
                progress.Report(Math.Clamp(10 + (int)Math.Round(status.ProgressPercent * 0.8), 10, 90));
                if (status.Status is MovieVideoProviderJobStatus.Succeeded or MovieVideoProviderJobStatus.Failed or MovieVideoProviderJobStatus.Cancelled)
                {
                    terminal = status;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.StatusPollIntervalSeconds, 1, 300)), cancellationToken);
            }

            if (terminal is null)
                throw new MovieVideoProviderTimeoutException();
            if (terminal.Status == MovieVideoProviderJobStatus.Cancelled)
                throw new MovieVideoProviderCancelledException();
            if (terminal.Status == MovieVideoProviderJobStatus.Failed)
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false);

            var output = await WithTransientRetriesAsync(token => provider.RetrieveAsync(execution.ProviderJobId!, terminal, token), cancellationToken);
            ValidateOutput(output);
            progress.Report(92);
            var metadata = JsonSerializer.Serialize(new
            {
                assetType = AssetTypes.Video,
                contentType = output.ContentType,
                durationSeconds = output.DurationSeconds,
                referenceImageApplied = !string.IsNullOrWhiteSpace(input.SourceImageUri),
                unsupportedFeatures = new[] { "continuation" },
            });
            var artifact = new GeneratedStreamFileArtifact(
                string.IsNullOrWhiteSpace(output.FileName) ? $"movie-{job.Id:N}.mp4" : output.FileName,
                output.ContentType,
                output.SizeBytes,
                output.OpenReadAsync,
                metadata);
            var usage = new AiUsageMetadata(
                provider.Key,
                output.ProviderModelKey ?? "video",
                null,
                null,
                null,
                output.EstimatedCostUsd,
                output.ActualCostUsd,
                (int)Math.Clamp(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue),
                "completed",
                false,
                PricingVersion: null,
                PricingSnapshotJson: null,
                Currency: output.Currency,
                CostBasis: output.CostBasis,
                SafeMetadataJson: output.SafeMetadataJson);
            var result = JsonSerializer.Serialize(new
            {
                assetType = AssetTypes.Video,
                contentType = output.ContentType,
                durationSeconds = output.DurationSeconds,
                movieClipId = clip.Id,
                continuityPreserved = true,
            });
            progress.Report(98);
            logger.LogInformation("Movie provider job completed. JobId={JobId}; ClipId={ClipId}", job.Id, clip.Id);
            return new GenerationHandlerResult(
                result,
                [new GenerationHandlerOutput(
                    GenerationJobOutputTypes.StoredFile,
                    null,
                    metadata,
                    Asset: new GeneratedAssetDescriptor(job.Title ?? "Generated movie", "Generated movie clip.", AssetTypes.Video, metadata),
                    StreamArtifact: artifact)],
                usage);
        }
        catch (OperationCanceledException exception)
        {
            if (exception is MovieVideoStaleWorkerException) throw;
            if (!string.IsNullOrWhiteSpace(execution.ProviderJobId))
            {
                try { await provider.CancelAsync(execution.ProviderJobId, CancellationToken.None); }
                catch (Exception cancellationException) { logger.LogWarning("Movie provider cancellation could not be completed. JobId={JobId}; ExceptionType={ExceptionType}", job.Id, cancellationException.GetType().Name); }
            }
            await executions.MarkCancelledAsync(job.Id, job.ConcurrencyToken, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                MovieVideoProviderException providerException => providerException.Code,
                MovieVideoProviderTimeoutException => GenerationJobErrorCodes.MovieProviderTimeout,
                MovieVideoStaleWorkerException => GenerationJobErrorCodes.MovieCancelled,
                _ => GenerationJobErrorCodes.MovieGenerationFailed,
            };
            await executions.MarkFailedAsync(job.Id, job.ConcurrencyToken, code, CancellationToken.None);
            throw;
        }
    }

    private async Task<T> WithTransientRetriesAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Clamp(settings.MaxTransientRetries, 0, 8);
        for (var attempt = 0; ; attempt++)
        {
            try { return await operation(cancellationToken); }
            catch (MovieVideoProviderException exception) when (exception.IsTransient && attempt < maxRetries)
            {
                var seconds = Math.Min(Math.Max(1, settings.RetryMaxDelaySeconds), Math.Max(1, settings.RetryBaseDelaySeconds) * Math.Pow(2, attempt));
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }
    }

    private static MovieGenerationInput Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MovieGenerationInput>(json)
                ?? throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false);
        }
        catch (JsonException)
        {
            throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, false);
        }
    }

    private void ValidateOutput(MovieVideoProviderOutput output)
    {
        if (output.SizeBytes <= 0 || output.SizeBytes > Math.Min(2L * 1024 * 1024 * 1024, Math.Max(1, settings.MaxOutputBytes)))
            throw new MovieVideoProviderOutputException();
        if (!output.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            throw new MovieVideoProviderOutputException();
        if (string.IsNullOrWhiteSpace(output.FileName) || output.FileName.Length > 255)
            throw new MovieVideoProviderOutputException();
    }
}

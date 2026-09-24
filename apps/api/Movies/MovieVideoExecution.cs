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

    public async Task TouchAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.GenerationJobs.Where(item => item.Id == jobId && item.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ClaimExpiresAt, now.AddMinutes(Math.Clamp(settings.ClaimLeaseMinutes, 1, 120))), cancellationToken);
    }

    public async Task PersistSubmittedAsync(MovieVideoProviderExecution execution, string providerJobId, CancellationToken cancellationToken)
    {
        execution.ProviderJobId = providerJobId;
        execution.Status = MovieVideoExecutionStatuses.Submitted;
        execution.AttemptCount++;
        execution.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await db.MovieClips.Where(item => item.Id == execution.MovieClipId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, MovieClipStatuses.Generating)
                .SetProperty(item => item.ProviderKey, execution.ProviderKey)
                .SetProperty(item => item.ProviderClipId, providerJobId)
                .SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }

    public async Task PersistStatusAsync(MovieVideoProviderExecution execution, MovieVideoProviderStatus status, CancellationToken cancellationToken)
    {
        execution.Status = status.Status switch
        {
            MovieVideoProviderJobStatus.Submitted => MovieVideoExecutionStatuses.Submitted,
            MovieVideoProviderJobStatus.Queued => MovieVideoExecutionStatuses.Queued,
            MovieVideoProviderJobStatus.Running => MovieVideoExecutionStatuses.Running,
            MovieVideoProviderJobStatus.Succeeded => MovieVideoExecutionStatuses.Succeeded,
            MovieVideoProviderJobStatus.Failed => MovieVideoExecutionStatuses.Failed,
            MovieVideoProviderJobStatus.Cancelled => MovieVideoExecutionStatuses.Cancelled,
            _ => MovieVideoExecutionStatuses.Running,
        };
        execution.ProgressPercent = Math.Clamp(status.ProgressPercent, 0, 100);
        execution.PollCount++;
        execution.NextPollAt = status.Status is MovieVideoProviderJobStatus.Succeeded or MovieVideoProviderJobStatus.Failed or MovieVideoProviderJobStatus.Cancelled
            ? null
            : DateTime.UtcNow.AddSeconds(Math.Clamp(settings.StatusPollIntervalSeconds, 1, 300));
        execution.UpdatedAt = DateTime.UtcNow;
        if (status.Status is MovieVideoProviderJobStatus.Succeeded or MovieVideoProviderJobStatus.Failed or MovieVideoProviderJobStatus.Cancelled)
            execution.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid jobId, string code, CancellationToken cancellationToken)
    {
        var execution = await db.MovieVideoProviderExecutions.FirstOrDefaultAsync(item => item.GenerationJobId == jobId, cancellationToken);
        if (execution is not null)
        {
            execution.Status = MovieVideoExecutionStatuses.Failed;
            execution.LastErrorCode = code;
            execution.CompletedAt = DateTime.UtcNow;
            execution.UpdatedAt = DateTime.UtcNow;
        }
        await db.MovieClips.Where(item => item.GenerationJobId == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieClipStatuses.Failed).SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var execution = await db.MovieVideoProviderExecutions.FirstOrDefaultAsync(item => item.GenerationJobId == jobId, cancellationToken);
        if (execution is not null)
        {
            execution.Status = MovieVideoExecutionStatuses.Cancelled;
            execution.CompletedAt = DateTime.UtcNow;
            execution.UpdatedAt = DateTime.UtcNow;
        }
        await db.MovieClips.Where(item => item.GenerationJobId == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, MovieClipStatuses.Cancelled).SetProperty(item => item.UpdatedAt, DateTime.UtcNow), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkReadyAsync(Guid jobId, Guid? assetId, Guid? storedFileId, int? durationSeconds, string? metadataJson, CancellationToken cancellationToken)
    {
        var execution = await db.MovieVideoProviderExecutions.FirstOrDefaultAsync(item => item.GenerationJobId == jobId, cancellationToken);
        if (execution is not null)
        {
            execution.Status = MovieVideoExecutionStatuses.Succeeded;
            execution.ProgressPercent = 100;
            execution.CompletedAt = DateTime.UtcNow;
            execution.UpdatedAt = DateTime.UtcNow;
        }
        var clip = await db.MovieClips.FirstOrDefaultAsync(item => item.GenerationJobId == jobId, cancellationToken);
        if (clip is not null)
        {
            clip.Status = MovieClipStatuses.Ready;
            clip.AssetId = assetId;
            clip.StoredFileId = storedFileId;
            clip.DurationSeconds = durationSeconds;
            clip.MetadataJson = metadataJson;
            clip.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
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
            input.ShotJson);
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
                await executions.PersistSubmittedAsync(execution, submission.ProviderJobId, cancellationToken);
            }

            MovieVideoProviderStatus? terminal = null;
            for (var poll = 0; poll < Math.Clamp(settings.MaxStatusPolls, 1, 10_000); poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await executions.TouchAsync(job.Id, cancellationToken);
                var status = await WithTransientRetriesAsync(token => provider.GetStatusAsync(execution.ProviderJobId!, token), cancellationToken);
                await executions.PersistStatusAsync(execution, status, cancellationToken);
                progress.Report(Math.Clamp(10 + (int)Math.Round(status.ProgressPercent * 0.8), 10, 90));
                if (status.Status is MovieVideoProviderJobStatus.Succeeded or MovieVideoProviderJobStatus.Failed or MovieVideoProviderJobStatus.Cancelled)
                {
                    terminal = status;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.StatusPollIntervalSeconds, 1, 300)), cancellationToken);
            }

            if (terminal is null)
                throw new MovieVideoProviderException(GenerationJobErrorCodes.MovieGenerationFailed, true, "The movie provider did not finish within the configured polling limit.");
            if (terminal.Status == MovieVideoProviderJobStatus.Cancelled)
                throw new OperationCanceledException(cancellationToken);
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
                continuityPreserved = true,
                providerMetadata = output.MetadataJson,
            });
            var artifact = new GeneratedStreamFileArtifact(
                string.IsNullOrWhiteSpace(output.FileName) ? $"movie-{job.Id:N}.mp4" : output.FileName,
                output.ContentType,
                output.SizeBytes,
                output.OpenReadAsync,
                metadata);
            var usage = new AiUsageMetadata(
                provider.Key,
                "video",
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
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(execution.ProviderJobId))
            {
                try { await provider.CancelAsync(execution.ProviderJobId, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning("Movie provider cancellation could not be completed. JobId={JobId}; ExceptionType={ExceptionType}", job.Id, exception.GetType().Name); }
            }
            await executions.MarkCancelledAsync(job.Id, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            var code = exception is MovieVideoProviderException providerException ? providerException.Code : GenerationJobErrorCodes.MovieGenerationFailed;
            await executions.MarkFailedAsync(job.Id, code, CancellationToken.None);
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

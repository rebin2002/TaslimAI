using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taslim.Api.Ai;
using Taslim.Api.Assets;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;

namespace Taslim.Api.Video;

/// <summary>
/// Server-side controls for the neutral video adapter execution path. The path is
/// disabled by default so adding an adapter implementation cannot activate a paid
/// provider accidentally.
/// </summary>
public sealed class VideoGenerationAdapterOptions
{
    public bool Enabled { get; set; }
    public int MaxPollAttempts { get; set; } = 120;
    public int PollIntervalMilliseconds { get; set; } = 1_000;
    public int MaxExecutionSeconds { get; set; } = 1_800;
    public int MaxHandleLength { get; set; } = 240;
}

public enum VideoGenerationFailureCategory
{
    Unavailable,
    Authentication,
    RateLimited,
    InvalidRequest,
    UnsupportedCapability,
    ContentRejected,
    TimedOut,
    Cancelled,
    ArtifactUnavailable,
    MalformedResponse,
    StaleEvent,
    InternalFailure,
}

public sealed class VideoGenerationAdapterExecutionException(
    VideoGenerationFailureCategory category,
    VideoGenerationError error)
    : Exception(error.SafeMessage)
{
    public VideoGenerationFailureCategory Category { get; } = category;
    public VideoGenerationError Error { get; } = error;
}

public enum VideoGenerationEventDisposition
{
    Accepted,
    Duplicate,
    Ignored,
}

public sealed record VideoGenerationEventDecision(
    VideoGenerationEventDisposition Disposition,
    VideoGenerationStatus CurrentStatus,
    VideoGenerationError? Error = null);

/// <summary>
/// Fences callback and polling updates for one opaque operation. Callback IDs are
/// deduplicated in-process and older/regressive updates cannot move the operation
/// backwards. A durable consumer can persist the same event ID and observed-at
/// values beside its execution row without changing this boundary.
/// </summary>
public sealed class VideoGenerationEventGate
{
    private readonly object gate = new();
    private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);
    private readonly string handle;
    private VideoGenerationStatus current;

    public VideoGenerationEventGate(VideoGenerationHandle handle, VideoGenerationStatus initialStatus)
    {
        ArgumentNullException.ThrowIfNull(handle);
        VideoGenerationContract.ValidateStatus(initialStatus);
        if (string.IsNullOrWhiteSpace(handle.Value)) throw new ArgumentException("The operation handle is invalid.", nameof(handle));
        this.handle = handle.Value;
        current = initialStatus;
    }

    public VideoGenerationStatus CurrentStatus
    {
        get { lock (gate) return current; }
    }

    public bool TryApplyPolledStatus(VideoGenerationStatus status)
    {
        VideoGenerationContract.ValidateStatus(status);
        lock (gate)
        {
            if (IsOlderOrRegressive(status, current)) return false;
            current = status;
            return true;
        }
    }

    public VideoGenerationCallbackResult ApplyCallback(VideoGenerationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        try
        {
            VideoGenerationContract.ValidateStatus(callback.Status);
        }
        catch (ArgumentException)
        {
            return new(VideoCallbackDisposition.Rejected, Error: Error(VideoGenerationErrorCode.MalformedResponse, false, "The video callback was invalid."));
        }

        lock (gate)
        {
            if (callback.Handle is null || !string.Equals(callback.Handle.Value, handle, StringComparison.Ordinal))
                return new(VideoCallbackDisposition.Rejected, Error: Error(VideoGenerationErrorCode.InvalidRequest, false, "The video callback target was invalid."));
            if (string.IsNullOrWhiteSpace(callback.EventId))
                return new(VideoCallbackDisposition.Rejected, Error: Error(VideoGenerationErrorCode.MalformedResponse, false, "The video callback event was invalid."));
            if (!eventIds.Add(callback.EventId)) return new(VideoCallbackDisposition.Duplicate);
            if (IsOlderOrRegressive(callback.Status, current))
                return new(VideoCallbackDisposition.Ignored, Error: Error(VideoGenerationErrorCode.StaleEvent, false, "The video callback was stale."));

            current = callback.Status;
            return new(VideoCallbackDisposition.Accepted, callback with { Status = current });
        }
    }

    private static bool IsOlderOrRegressive(VideoGenerationStatus incoming, VideoGenerationStatus existing)
    {
        if (incoming.ObservedAt < existing.ObservedAt) return true;
        if (existing.IsTerminal) return true;
        return Rank(incoming.State) < Rank(existing.State);
    }

    private static int Rank(VideoGenerationState state) => state switch
    {
        VideoGenerationState.Submitted => 0,
        VideoGenerationState.Queued => 1,
        VideoGenerationState.Running => 2,
        VideoGenerationState.Succeeded or VideoGenerationState.Failed or VideoGenerationState.Cancelled => 3,
        _ => -1,
    };

    private static VideoGenerationError Error(VideoGenerationErrorCode code, bool transient, string message) =>
        new(code, transient, message);
}

/// <summary>
/// A submitted operation that exposes the same normalized update path for polling
/// and callbacks. It never exposes provider payloads or URLs.
/// </summary>
public sealed class VideoGenerationAdapterExecutionSession
{
    private readonly IVideoGenerationAdapter adapter;
    private readonly VideoGenerationEventGate events;

    internal VideoGenerationAdapterExecutionSession(
        IVideoGenerationAdapter adapter,
        VideoGenerationHandle handle,
        VideoGenerationStatus initialStatus)
    {
        this.adapter = adapter;
        Handle = handle;
        events = new VideoGenerationEventGate(handle, initialStatus);
    }

    public VideoGenerationHandle Handle { get; }
    public VideoGenerationStatus CurrentStatus => events.CurrentStatus;

    public async Task<VideoGenerationStatus> PollAsync(CancellationToken cancellationToken = default)
    {
        var status = await adapter.GetStatusAsync(Handle, cancellationToken);
        VideoGenerationContract.ValidateStatus(status);
        events.TryApplyPolledStatus(status);
        return CurrentStatus;
    }

    public async Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(
        VideoGenerationCallbackRequest callback,
        CancellationToken cancellationToken = default)
    {
        var normalized = await adapter.NormalizeCallbackAsync(callback, cancellationToken);
        if (normalized.Disposition != VideoCallbackDisposition.Accepted || normalized.Callback is null)
            return normalized;

        var applied = events.ApplyCallback(normalized.Callback);
        return applied.Disposition switch
        {
            VideoCallbackDisposition.Accepted => normalized with { Callback = applied.Callback },
            VideoCallbackDisposition.Duplicate => new(VideoCallbackDisposition.Duplicate),
            _ => new(applied.Disposition, Error: applied.Error),
        };
    }

    public async Task<VideoCancellationResult> CancelAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentStatus.IsTerminal)
            return new(VideoCancellationOutcome.AlreadyTerminal, CurrentStatus);

        var result = await adapter.CancelAsync(Handle, cancellationToken);
        if (result.CurrentStatus is not null)
        {
            VideoGenerationContract.ValidateStatus(result.CurrentStatus);
            events.TryApplyPolledStatus(result.CurrentStatus);
        }
        return result with { CurrentStatus = result.CurrentStatus ?? CurrentStatus };
    }

    public async Task<VideoGenerationArtifact> RetrieveArtifactAsync(CancellationToken cancellationToken = default)
    {
        var status = CurrentStatus;
        if (status.State != VideoGenerationState.Succeeded)
            throw new VideoGenerationAdapterExecutionException(
                VideoGenerationFailureCategory.ArtifactUnavailable,
                new(VideoGenerationErrorCode.ArtifactUnavailable, false, "The video artifact is not ready."));

        var artifact = await adapter.RetrieveArtifactAsync(Handle, status, cancellationToken);
        try { VideoGenerationContract.ValidateArtifact(artifact); }
        catch (ArgumentException)
        {
            throw new VideoGenerationAdapterExecutionException(
                VideoGenerationFailureCategory.MalformedResponse,
                new(VideoGenerationErrorCode.MalformedResponse, false, "The video artifact was invalid."));
        }
        return artifact;
    }
}

public sealed record VideoGenerationAdapterExecutionResult(
    VideoGenerationHandle Handle,
    VideoGenerationStatus CompletedStatus,
    VideoGenerationArtifact Artifact,
    VideoUsageEvidence? Usage,
    TimeSpan Elapsed);

/// <summary>
/// Drives submit, poll, callback normalization, cancellation, and artifact
/// retrieval through the neutral adapter boundary. It is intentionally unaware of
/// vendor SDKs and is safe to use from a canonical generation-job handler.
/// </summary>
public sealed class VideoGenerationAdapterExecutionService(
    IVideoGenerationAdapter adapter,
    IOptions<VideoGenerationAdapterOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly VideoGenerationAdapterOptions settings = options.Value;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<VideoGenerationAdapterExecutionSession> SubmitAsync(
        VideoGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!settings.Enabled || !adapter.IsAvailable)
            throw new VideoGenerationAdapterExecutionException(
                VideoGenerationFailureCategory.Unavailable,
                VideoGenerationError.Unavailable("Video generation is not available."));

        try
        {
            VideoGenerationContract.ValidateRequest(request);
            if (!adapter.Capabilities.Supports(request))
                throw new VideoGenerationAdapterExecutionException(
                    VideoGenerationFailureCategory.UnsupportedCapability,
                    new(VideoGenerationErrorCode.UnsupportedCapability, false, "The requested video capability is not available."));

            var submission = await adapter.SubmitAsync(request, cancellationToken);
            ValidateHandle(submission.Handle);
            VideoGenerationContract.ValidateStatus(submission.InitialStatus);
            return new VideoGenerationAdapterExecutionSession(adapter, submission.Handle, submission.InitialStatus);
        }
        catch (VideoGenerationAdapterExecutionException)
        {
            throw;
        }
        catch (VideoGenerationAdapterException exception)
        {
            throw ToExecutionException(exception.Error);
        }
        catch (ArgumentException)
        {
            throw new VideoGenerationAdapterExecutionException(
                VideoGenerationFailureCategory.InvalidRequest,
                new(VideoGenerationErrorCode.InvalidRequest, false, "The video request is invalid."));
        }
    }

    public async Task<VideoGenerationAdapterExecutionResult> ExecuteAsync(
        VideoGenerationRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = clock.GetTimestamp();
        VideoGenerationAdapterExecutionSession? session = null;
        try
        {
            session = await SubmitAsync(request, cancellationToken);
            progress?.Report(Math.Clamp(session.CurrentStatus.ProgressPercent, 0, 10));
            var maxPolls = Math.Clamp(settings.MaxPollAttempts, 1, 10_000);
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasTimedOut(started))
                    throw new VideoGenerationAdapterExecutionException(
                        VideoGenerationFailureCategory.TimedOut,
                        new(VideoGenerationErrorCode.TimedOut, true, "Video generation took too long to finish."));
                if (session.CurrentStatus.IsTerminal) break;
                if (attempt >= maxPolls)
                    throw new VideoGenerationAdapterExecutionException(
                        VideoGenerationFailureCategory.TimedOut,
                        new(VideoGenerationErrorCode.TimedOut, true, "Video generation took too long to finish."));

                var status = await session.PollAsync(cancellationToken);
                progress?.Report(Math.Clamp(10 + (int)Math.Round(status.ProgressPercent * 0.8), 10, 90));
                if (status.IsTerminal) break;
                var delay = Math.Max(0, settings.PollIntervalMilliseconds);
                if (delay > 0) await Task.Delay(delay, cancellationToken);
            }

            var completed = session.CurrentStatus;
            if (completed.State == VideoGenerationState.Failed)
                throw ToExecutionException(completed.Error ?? new(VideoGenerationErrorCode.InternalFailure, false, "Video generation failed."));
            if (completed.State == VideoGenerationState.Cancelled)
                throw ToExecutionException(new(VideoGenerationErrorCode.Cancelled, false, "Video generation was cancelled."));
            var artifact = await session.RetrieveArtifactAsync(cancellationToken);
            progress?.Report(100);
            var usage = VideoUsageEvidenceMapper.Enrich(completed.Usage, artifact, clock.GetElapsedTime(started));
            return new(session.Handle, completed, artifact, usage, clock.GetElapsedTime(started));
        }
        catch (VideoGenerationAdapterExecutionException)
        {
            if (session is not null) await CancelBestEffortAsync(session);
            throw;
        }
        catch (VideoGenerationAdapterException exception)
        {
            if (session is not null) await CancelBestEffortAsync(session);
            throw ToExecutionException(exception.Error);
        }
        catch (OperationCanceledException)
        {
            if (session is not null) await CancelBestEffortAsync(session);
            throw;
        }
    }

    private bool HasTimedOut(long started) =>
        settings.MaxExecutionSeconds > 0 && clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(Math.Clamp(settings.MaxExecutionSeconds, 1, 86_400));

    private void ValidateHandle(VideoGenerationHandle handle)
    {
        if (handle is null || string.IsNullOrWhiteSpace(handle.Value) || handle.Value.Length > Math.Clamp(settings.MaxHandleLength, 1, 4_096))
            throw new VideoGenerationAdapterExecutionException(
                VideoGenerationFailureCategory.MalformedResponse,
                new(VideoGenerationErrorCode.MalformedResponse, false, "The video operation handle was invalid."));
    }

    private static async Task CancelBestEffortAsync(VideoGenerationAdapterExecutionSession session)
    {
        try { await session.CancelAsync(CancellationToken.None); }
        catch (Exception) { }
    }

    private static VideoGenerationAdapterExecutionException ToExecutionException(VideoGenerationError error) =>
        new(MapCategory(error.Code), error);

    private static VideoGenerationFailureCategory MapCategory(VideoGenerationErrorCode code) => code switch
    {
        VideoGenerationErrorCode.Unavailable => VideoGenerationFailureCategory.Unavailable,
        VideoGenerationErrorCode.AuthenticationFailed => VideoGenerationFailureCategory.Authentication,
        VideoGenerationErrorCode.RateLimited => VideoGenerationFailureCategory.RateLimited,
        VideoGenerationErrorCode.InvalidRequest => VideoGenerationFailureCategory.InvalidRequest,
        VideoGenerationErrorCode.UnsupportedCapability => VideoGenerationFailureCategory.UnsupportedCapability,
        VideoGenerationErrorCode.ContentRejected => VideoGenerationFailureCategory.ContentRejected,
        VideoGenerationErrorCode.TimedOut => VideoGenerationFailureCategory.TimedOut,
        VideoGenerationErrorCode.Cancelled => VideoGenerationFailureCategory.Cancelled,
        VideoGenerationErrorCode.ArtifactUnavailable => VideoGenerationFailureCategory.ArtifactUnavailable,
        VideoGenerationErrorCode.MalformedResponse => VideoGenerationFailureCategory.MalformedResponse,
        VideoGenerationErrorCode.StaleEvent => VideoGenerationFailureCategory.StaleEvent,
        _ => VideoGenerationFailureCategory.InternalFailure,
    };
}

public static class VideoUsageEvidenceMapper
{
    public static VideoUsageEvidence? Enrich(VideoUsageEvidence? evidence, VideoGenerationArtifact artifact, TimeSpan elapsed)
    {
        if (evidence is null) return null;
        var measures = (evidence.Measures ?? []).ToList();
        if (!measures.Any(item => item.Metric == VideoUsageMetric.OutputBytes))
            measures.Add(new(VideoUsageMetric.OutputBytes, artifact.SizeBytes));
        if (!measures.Any(item => item.Metric == VideoUsageMetric.ProcessingMilliseconds))
            measures.Add(new(VideoUsageMetric.ProcessingMilliseconds, (decimal)Math.Max(0, elapsed.TotalMilliseconds)));
        return evidence with { Measures = measures };
    }

    public static AiUsageMetadata ToAiUsageMetadata(VideoUsageEvidence? evidence, string adapterKey, TimeSpan elapsed)
    {
        var normalized = evidence;
        return new(
            adapterKey,
            "video-adapter",
            null,
            null,
            null,
            normalized?.EstimatedCost,
            normalized?.ActualCost,
            (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue),
            "completed",
            false,
            Currency: normalized?.Currency,
            CostBasis: normalized?.CostBasis switch
            {
                VideoCostBasis.ProviderReported => UsageCostBasis.Actual,
                VideoCostBasis.Estimated => UsageCostBasis.Estimated,
                _ => null,
            },
            SafeMetadataJson: JsonSerializer.Serialize(new
            {
                modality = "video",
                evidence = normalized is not null,
                measureCount = normalized?.NormalizedMeasures.Count ?? 0,
            }));
    }
}

/// <summary>Production registration target. It never makes a network call.</summary>
public sealed class UnavailableVideoGenerationAdapter : IVideoGenerationAdapter
{
    public string AdapterKey => "unconfigured";
    public bool IsAvailable => false;
    public VideoGenerationCapabilities Capabilities { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<VideoResolution>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
        1,
        1,
        SupportsPolling: true,
        SupportsCallbacks: false,
        SupportsCancellation: false,
        SupportsContinuation: false,
        SupportsReferenceImages: false);

    public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<VideoGenerationSubmission>(new VideoGenerationAdapterException(VideoGenerationError.Unavailable("Video generation is not available.")));

    public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromException<VideoGenerationStatus>(new VideoGenerationAdapterException(VideoGenerationError.Unavailable("Video generation is not available.")));

    public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
        Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable("Video generation is not available.")));

    public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.NotSupported, Error: VideoGenerationError.Unavailable("Video generation is not available.")));

    public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected, Error: VideoGenerationError.Unavailable("Video generation is not available.")));
}

/// <summary>
/// Opt-in bridge for canonical movie generation jobs. The existing legacy movie
/// provider handler remains the default path until a neutral adapter is explicitly
/// enabled in server configuration and replaced with an approved implementation.
/// </summary>
public sealed class VideoGenerationAdapterJobHandler(
    VideoGenerationAdapterExecutionService executor,
    IVideoGenerationAdapter adapter,
    IOptions<VideoGenerationAdapterOptions> options,
    ILogger<VideoGenerationAdapterJobHandler> logger) : IGenerationJobHandler
{
    public bool CanHandle(string jobType) =>
        options.Value.Enabled && adapter.IsAvailable && GenerationJobTypes.MovieTypes.Contains(jobType);

    public async Task<GenerationHandlerResult> ExecuteAsync(GenerationJob job, IProgress<int> progress, CancellationToken cancellationToken)
    {
        MovieGenerationInput input;
        try
        {
            input = JsonSerializer.Deserialize<MovieGenerationInput>(job.InputJson)
                ?? throw new VideoGenerationAdapterExecutionException(VideoGenerationFailureCategory.InvalidRequest, new(VideoGenerationErrorCode.InvalidRequest, false, "The video request is invalid."));
        }
        catch (JsonException)
        {
            throw new VideoGenerationAdapterExecutionException(VideoGenerationFailureCategory.InvalidRequest, new(VideoGenerationErrorCode.InvalidRequest, false, "The video request is invalid."));
        }

        var request = new VideoGenerationRequest(
            input.Operation,
            input.Description,
            input.DurationSeconds,
            input.AspectRatio,
            Style: input.Style,
            Language: input.Language,
            AdditionalInstructions: input.AdditionalInstructions,
            ReferenceImages: string.IsNullOrWhiteSpace(input.SourceImageUri) ? null : [new VideoReferenceImage(input.SourceImageUri, "source")],
            Continuation: string.IsNullOrWhiteSpace(input.ContinuationProviderJobId) ? null : new VideoGenerationHandle(input.ContinuationProviderJobId),
            IdempotencyKey: $"generation:{job.Id:N}",
            Context: new Dictionary<string, string>
            {
                ["continuity"] = input.ContinuityGuideJson ?? string.Empty,
                ["scene"] = input.SceneJson ?? string.Empty,
                ["shot"] = input.ShotJson ?? string.Empty,
                ["world"] = input.WorldContextJson ?? string.Empty,
            });
        var completed = await executor.ExecuteAsync(request, progress, cancellationToken);
        var metadata = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Video,
            contentType = completed.Artifact.ContentType,
            durationSeconds = completed.Artifact.DurationSeconds,
            resolution = completed.Artifact.Resolution?.ToString(),
            adapterEvidence = completed.Usage is not null,
        });
        var streamArtifact = new GeneratedStreamFileArtifact(
            string.IsNullOrWhiteSpace(completed.Artifact.FileName) ? $"movie-{job.Id:N}.mp4" : completed.Artifact.FileName,
            completed.Artifact.ContentType,
            completed.Artifact.SizeBytes,
            completed.Artifact.OpenReadAsync,
            metadata);
        var resultJson = JsonSerializer.Serialize(new
        {
            assetType = AssetTypes.Video,
            contentType = completed.Artifact.ContentType,
            durationSeconds = completed.Artifact.DurationSeconds,
            movieClipId = input.MovieClipId,
            continuityPreserved = true,
        });
        logger.LogInformation("Neutral video adapter job completed. JobId={JobId}; DurationSeconds={DurationSeconds}; UsageEvidence={UsageEvidence}", job.Id, completed.Artifact.DurationSeconds, completed.Usage is not null);
        return new GenerationHandlerResult(
            resultJson,
            [new GenerationHandlerOutput(
                GenerationJobOutputTypes.StoredFile,
                null,
                metadata,
                Asset: new GeneratedAssetDescriptor(job.Title ?? "Generated movie", "Generated movie clip.", AssetTypes.Video, metadata),
                StreamArtifact: streamArtifact)],
            VideoUsageEvidenceMapper.ToAiUsageMetadata(completed.Usage, adapter.AdapterKey, completed.Elapsed));
    }
}

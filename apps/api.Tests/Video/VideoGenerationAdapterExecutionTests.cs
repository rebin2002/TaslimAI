using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Video;
using Taslim.Api.Tests.Video;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VideoGenerationAdapterExecutionTests
{
    [Fact]
    public async Task Execution_service_runs_submit_poll_retrieve_and_preserves_usage_evidence()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var service = Service(adapter, maxPollAttempts: 4);

        var result = await service.ExecuteAsync(Request());

        Assert.Equal(VideoGenerationState.Succeeded, result.CompletedStatus.State);
        Assert.Equal("video/mp4", result.Artifact.ContentType);
        Assert.Equal(result.Artifact.SizeBytes, result.Usage!.NormalizedMeasures.Single(item => item.Metric == VideoUsageMetric.OutputBytes).Value);
        Assert.Contains(result.Usage.NormalizedMeasures, item => item.Metric == VideoUsageMetric.ProcessingMilliseconds);
        Assert.Equal(VideoCostBasis.ProviderReported, result.Usage.CostBasis);
    }

    [Fact]
    public async Task Callback_and_polling_share_duplicate_and_stale_event_fencing()
    {
        var adapter = new MatchingCallbackVideoAdapter();
        var service = Service(adapter);
        var session = await service.SubmitAsync(Request());
        var callback = new VideoGenerationCallbackRequest(
            "{}"u8.ToArray(),
            new Dictionary<string, string> { ["X-Signature"] = "synthetic" },
            "application/json",
            DateTimeOffset.UtcNow);

        var accepted = await session.NormalizeCallbackAsync(callback);
        var duplicate = await session.NormalizeCallbackAsync(callback);

        Assert.Equal(VideoCallbackDisposition.Accepted, accepted.Disposition);
        Assert.Equal(VideoCallbackDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(VideoGenerationState.Running, session.CurrentStatus.State);

        var gate = new VideoGenerationEventGate(
            new VideoGenerationHandle("opaque"),
            new(VideoGenerationState.Running, 50, DateTimeOffset.UtcNow));
        var stale = gate.ApplyCallback(new(
            "event-old",
            new VideoGenerationHandle("opaque"),
            new(VideoGenerationState.Queued, 10, DateTimeOffset.UtcNow.AddSeconds(-1))));

        Assert.Equal(VideoCallbackDisposition.Ignored, stale.Disposition);
        Assert.Equal(VideoGenerationErrorCode.StaleEvent, stale.Error!.Code);
        Assert.Equal(VideoGenerationState.Running, gate.CurrentStatus.State);
    }

    [Fact]
    public async Task Cancellation_is_idempotent_and_terminal_retrieval_is_safe()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var service = Service(adapter);
        var session = await service.SubmitAsync(Request());

        var accepted = await session.CancelAsync();
        var terminal = await session.CancelAsync();

        Assert.Equal(VideoCancellationOutcome.Accepted, accepted.Outcome);
        Assert.Equal(VideoGenerationState.Cancelled, accepted.CurrentStatus!.State);
        Assert.Equal(VideoCancellationOutcome.AlreadyTerminal, terminal.Outcome);
        await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() => session.RetrieveArtifactAsync());
    }

    [Fact]
    public async Task Poll_budget_normalizes_timeout_and_attempts_best_effort_cancel()
    {
        var adapter = new NeverCompleteVideoAdapter();
        var service = Service(adapter, maxPollAttempts: 2);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() => service.ExecuteAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.TimedOut, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.TimedOut, exception.Error.Code);
        Assert.Equal(1, adapter.CancelCount);
    }

    [Fact]
    public async Task Disabled_adapter_is_fail_closed_and_unsupported_requests_are_normalized()
    {
        var disabled = new VideoGenerationAdapterExecutionService(
            new UnavailableVideoGenerationAdapter(),
            Options.Create(new VideoGenerationAdapterOptions { Enabled = false }));
        var unavailable = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() => disabled.SubmitAsync(Request()));
        Assert.Equal(VideoGenerationFailureCategory.Unavailable, unavailable.Category);

        var limited = new FakeVideoGenerationAdapter();
        var unsupported = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            Service(limited).SubmitAsync(Request(durationSeconds: 31, aspectRatio: "4:5")));
        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, unsupported.Category);
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, unsupported.Error.Code);
    }

    [Fact]
    public void Usage_mapping_remains_provider_neutral_for_normal_job_metadata()
    {
        var evidence = new VideoUsageEvidence(
            0.5m,
            0.4m,
            "USD",
            VideoCostBasis.ProviderReported,
            [new(VideoUsageMetric.GeneratedDurationSeconds, 5)]);

        var usage = VideoUsageEvidenceMapper.ToAiUsageMetadata(evidence, "internal-adapter", TimeSpan.FromMilliseconds(25));

        Assert.Equal("internal-adapter", usage.ProviderKey);
        Assert.Equal("video-adapter", usage.ModelKey);
        Assert.Equal(UsageCostBasis.Actual, usage.CostBasis);
        Assert.Contains("video", usage.SafeMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", usage.SafeMetadataJson, StringComparison.OrdinalIgnoreCase);
    }

    private static VideoGenerationAdapterExecutionService Service(IVideoGenerationAdapter adapter, int maxPollAttempts = 8) =>
        new(adapter, Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = maxPollAttempts,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }));

    private static VideoGenerationRequest Request(int durationSeconds = 5, string aspectRatio = "16:9") => new(
        "scene-clip",
        "A slow camera move through a sunlit forest.",
        durationSeconds,
        aspectRatio,
        new VideoResolution(1280, 720),
        "cinematic",
        "en",
        "No text overlays.",
        [new VideoReferenceImage("https://assets.example.test/reference.png", "first-frame")],
        null,
        "execution-test-1");
}

internal sealed class NeverCompleteVideoAdapter : IVideoGenerationAdapter
{
    private static readonly VideoGenerationCapabilities Supported = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
        new HashSet<VideoResolution> { new(1280, 720) },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
        1,
        10,
        SupportsPolling: true,
        SupportsCallbacks: true,
        SupportsCancellation: true,
        SupportsContinuation: false,
        SupportsReferenceImages: true);

    public int CancelCount { get; private set; }
    public string AdapterKey => "never-complete-fake";
    public bool IsAvailable => true;
    public VideoGenerationCapabilities Capabilities => Supported;

    public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationSubmission(new VideoGenerationHandle("never-complete"), Status(VideoGenerationState.Submitted, 0)));

    public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
        Task.FromResult(Status(VideoGenerationState.Running, 50));

    public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
        Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable()));

    public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
    {
        CancelCount++;
        return Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.Accepted, Status(VideoGenerationState.Cancelled, 0)));
    }

    public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Ignored));

    private static VideoGenerationStatus Status(VideoGenerationState state, int progress) =>
        new(state, progress, DateTimeOffset.UtcNow);
}

internal sealed class MatchingCallbackVideoAdapter : IVideoGenerationAdapter
{
    private readonly FakeVideoGenerationAdapter inner = new();
    private VideoGenerationHandle? submittedHandle;

    public string AdapterKey => inner.AdapterKey;
    public bool IsAvailable => inner.IsAvailable;
    public VideoGenerationCapabilities Capabilities => inner.Capabilities;

    public async Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var submission = await inner.SubmitAsync(request, cancellationToken);
        submittedHandle = submission.Handle;
        return submission;
    }

    public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
        inner.GetStatusAsync(handle, cancellationToken);

    public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
        inner.RetrieveArtifactAsync(handle, completedStatus, cancellationToken);

    public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(handle, cancellationToken);

    public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
        Task.FromResult(new VideoGenerationCallbackResult(
            VideoCallbackDisposition.Accepted,
            new VideoGenerationCallback(
                "matching-event-1",
                submittedHandle ?? new VideoGenerationHandle("missing"),
                new(VideoGenerationState.Running, 50, callback.ReceivedAt))));
}

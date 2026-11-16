using Microsoft.Extensions.Options;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class VideoCancellationStateTests
{
    [Fact]
    public async Task Cancellation_returns_the_gate_accepted_status_not_a_stale_adapter_status()
    {
        var now = DateTimeOffset.UtcNow;
        var adapter = new StaleCancellationAdapter(now);
        var service = new VideoGenerationAdapterExecutionService(
            adapter,
            Options.Create(new VideoGenerationAdapterOptions { Enabled = true, PollIntervalMilliseconds = 0 }));

        var session = await service.SubmitAsync(Request());
        var result = await session.CancelAsync();

        Assert.Equal(VideoGenerationState.Running, session.CurrentStatus.State);
        Assert.Equal(VideoGenerationState.Running, result.CurrentStatus!.State);
    }

    [Fact]
    public async Task A_fresh_terminal_cancellation_status_is_applied_through_the_event_gate()
    {
        var now = DateTimeOffset.UtcNow;
        var adapter = new GateCancellationAdapter(
            new VideoGenerationStatus(VideoGenerationState.Running, 50, now),
            new VideoCancellationResult(
                VideoCancellationOutcome.Accepted,
                new VideoGenerationStatus(VideoGenerationState.Cancelled, 100, now.AddSeconds(5))));
        var service = new VideoGenerationAdapterExecutionService(
            adapter,
            Options.Create(new VideoGenerationAdapterOptions { Enabled = true, PollIntervalMilliseconds = 0 }));

        var session = await service.SubmitAsync(Request());
        var result = await session.CancelAsync();

        Assert.Equal(VideoCancellationOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.CurrentStatus);
        Assert.Equal(VideoGenerationState.Cancelled, result.CurrentStatus!.State);
        Assert.Equal(VideoGenerationState.Cancelled, session.CurrentStatus.State);
        Assert.True(adapter.CancelWasCalled);
    }

    [Fact]
    public async Task A_terminal_session_reports_already_terminal_without_calling_the_adapter()
    {
        var now = DateTimeOffset.UtcNow;
        var adapter = new GateCancellationAdapter(
            new VideoGenerationStatus(
                VideoGenerationState.Failed, 100, now,
                Error: new VideoGenerationError(VideoGenerationErrorCode.InternalFailure, false, "Synthetic provider failure.")),
            new VideoCancellationResult(VideoCancellationOutcome.Accepted, null));
        var service = new VideoGenerationAdapterExecutionService(
            adapter,
            Options.Create(new VideoGenerationAdapterOptions { Enabled = true, PollIntervalMilliseconds = 0 }));

        var session = await service.SubmitAsync(Request());
        var result = await session.CancelAsync();

        Assert.Equal(VideoCancellationOutcome.AlreadyTerminal, result.Outcome);
        Assert.NotNull(result.CurrentStatus);
        Assert.Equal(VideoGenerationState.Failed, result.CurrentStatus!.State);
        Assert.False(adapter.CancelWasCalled);
    }

    private static VideoGenerationRequest Request() => new(
        "scene-clip", "A synthetic test clip.", 5, "16:9",
        new VideoResolution(1280, 720), "cinematic", "en", null, null, null, "cancel-stale-1");

    private sealed class StaleCancellationAdapter(DateTimeOffset now) : IVideoGenerationAdapter
    {
        private static readonly VideoGenerationCapabilities Supported = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
            new HashSet<VideoResolution> { new(1280, 720) },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
            1, 10, SupportsPolling: true, SupportsCallbacks: false,
            SupportsCancellation: true, SupportsContinuation: false,
            SupportsReferenceImages: false);

        public string AdapterKey => "stale-cancel-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => Supported;

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("opaque"),
                new VideoGenerationStatus(VideoGenerationState.Running, 50, now)));

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationStatus(VideoGenerationState.Running, 50, now));

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoCancellationResult(
                VideoCancellationOutcome.Accepted,
                new VideoGenerationStatus(VideoGenerationState.Cancelled, 100, now.AddSeconds(-1))));

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected));
    }

    /// <summary>
    /// Synthetic, provider-free adapter whose submit and cancellation statuses are supplied
    /// by the test, so the event gate decides the outcome that the caller observes.
    /// </summary>
    private sealed class GateCancellationAdapter(
        VideoGenerationStatus initialStatus,
        VideoCancellationResult cancellationResult) : IVideoGenerationAdapter
    {
        private static readonly VideoGenerationCapabilities Supported = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
            new HashSet<VideoResolution> { new(1280, 720) },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
            1, 10, SupportsPolling: true, SupportsCallbacks: false,
            SupportsCancellation: true, SupportsContinuation: false,
            SupportsReferenceImages: false);

        public string AdapterKey => "gate-cancel-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => Supported;
        public bool CancelWasCalled { get; private set; }

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationSubmission(new VideoGenerationHandle("opaque"), initialStatus));

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(initialStatus);

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
        {
            CancelWasCalled = true;
            return Task.FromResult(cancellationResult);
        }

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected));
    }
}

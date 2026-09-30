using System.Collections.Concurrent;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

public sealed class VideoGenerationAdapterContractTests
{
    [Fact]
    public async Task Fake_adapter_supports_submit_poll_retrieve_and_normalized_usage()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var request = Request();

        VideoGenerationContract.ValidateRequest(request);
        Assert.True(adapter.Capabilities.Supports(request));

        var submission = await adapter.SubmitAsync(request);
        Assert.Equal(VideoGenerationState.Submitted, submission.InitialStatus.State);
        Assert.NotEqual("provider-job-123", submission.Handle.Value);

        var queued = await adapter.GetStatusAsync(submission.Handle);
        var running = await adapter.GetStatusAsync(submission.Handle);
        var completed = await adapter.GetStatusAsync(submission.Handle);
        Assert.Equal(VideoGenerationState.Queued, queued.State);
        Assert.Equal(VideoGenerationState.Running, running.State);
        Assert.Equal(VideoGenerationState.Succeeded, completed.State);
        Assert.Equal(VideoCostBasis.ProviderReported, completed.Usage!.CostBasis);
        Assert.Equal(0.12m, completed.Usage.ActualCost);

        var artifact = await adapter.RetrieveArtifactAsync(submission.Handle, completed);
        VideoGenerationContract.ValidateArtifact(artifact);
        await using var stream = await artifact.OpenReadAsync(CancellationToken.None);
        Assert.Equal(artifact.SizeBytes, stream.Length);
        Assert.Equal("video/mp4", artifact.ContentType);
    }

    [Fact]
    public async Task Adapter_capabilities_are_explicit_and_reject_unsupported_requests_without_provider_types()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var unsupported = Request(durationSeconds: 31, aspectRatio: "4:5");

        Assert.False(adapter.Capabilities.Supports(unsupported));
        await Assert.ThrowsAsync<VideoGenerationAdapterException>(() => adapter.SubmitAsync(unsupported));
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, adapter.LastError!.Code);
        Assert.False(adapter.LastError.IsTransient);
    }

    [Fact]
    public async Task Callback_normalization_is_idempotent_and_returns_only_neutral_status()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var handle = new VideoGenerationHandle("opaque-operation-1");
        var payload = new VideoGenerationCallbackRequest(
            "{}"u8.ToArray(),
            new Dictionary<string, string> { ["X-Signature"] = "synthetic" },
            "application/json",
            DateTimeOffset.UtcNow);

        var first = await adapter.NormalizeCallbackAsync(payload);
        var duplicate = await adapter.NormalizeCallbackAsync(payload);

        Assert.Equal(VideoCallbackDisposition.Accepted, first.Disposition);
        Assert.Equal(VideoCallbackDisposition.Duplicate, duplicate.Disposition);
        Assert.NotNull(first.Callback);
        Assert.Equal(VideoGenerationState.Running, first.Callback!.Status.State);
        Assert.Equal(handle.Value, first.Callback.Handle.Value);
        Assert.Null(first.Error);
    }

    [Fact]
    public async Task Cancellation_is_normalized_and_does_not_throw_for_an_already_terminal_operation()
    {
        var adapter = new FakeVideoGenerationAdapter();
        var submission = await adapter.SubmitAsync(Request());

        var accepted = await adapter.CancelAsync(submission.Handle);
        var terminal = await adapter.CancelAsync(submission.Handle);

        Assert.Equal(VideoCancellationOutcome.Accepted, accepted.Outcome);
        Assert.Equal(VideoGenerationState.Cancelled, accepted.CurrentStatus!.State);
        Assert.Equal(VideoCancellationOutcome.AlreadyTerminal, terminal.Outcome);
        Assert.Equal(VideoGenerationState.Cancelled, terminal.CurrentStatus!.State);
    }

    [Fact]
    public void Contract_validation_rejects_invalid_terminal_status_and_artifact_metadata()
    {
        var error = new VideoGenerationError(VideoGenerationErrorCode.InternalFailure, false, "A safe failure.");
        var invalidStatus = new VideoGenerationStatus(VideoGenerationState.Failed, 100, DateTimeOffset.UtcNow);
        var invalidArtifact = new VideoGenerationArtifactDescriptor("text/plain", "../clip.mp4", 10);

        Assert.Throws<ArgumentException>(() => VideoGenerationContract.ValidateStatus(invalidStatus));
        Assert.Throws<ArgumentException>(() => VideoGenerationContract.ValidateArtifactDescriptor(invalidArtifact));
        VideoGenerationContract.ValidateStatus(invalidStatus with { Error = error });
    }

    [Fact]
    public void Normalized_errors_never_require_provider_or_model_identifiers()
    {
        var error = new VideoGenerationError(
            VideoGenerationErrorCode.RateLimited,
            true,
            "Video generation is temporarily busy.",
            TimeSpan.FromSeconds(10));

        Assert.Equal(VideoGenerationErrorCode.RateLimited, error.Code);
        Assert.DoesNotContain("provider", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

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
        "contract-test-1");
}

internal sealed class FakeVideoGenerationAdapter : IVideoGenerationAdapter
{
    private static readonly byte[] Clip = "deterministic-video"u8.ToArray();
    private readonly ConcurrentDictionary<string, int> polls = new(StringComparer.Ordinal);
    private readonly HashSet<string> callbackIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VideoGenerationStatus> terminalStatuses = new(StringComparer.Ordinal);

    public string AdapterKey => "fake-video-adapter";
    public bool IsAvailable => true;
    public VideoGenerationError? LastError { get; private set; }
    public VideoGenerationCapabilities Capabilities { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip", "quick-movie" },
        new HashSet<VideoResolution> { new(1280, 720), new(720, 1280) },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9", "9:16" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
        2,
        10,
        SupportsPolling: true,
        SupportsCallbacks: true,
        SupportsCancellation: true,
        SupportsContinuation: false,
        SupportsReferenceImages: true,
        MaxOutputs: 1);

    public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastError = null;
        VideoGenerationContract.ValidateRequest(request);
        if (!Capabilities.Supports(request))
        {
            LastError = new VideoGenerationError(VideoGenerationErrorCode.UnsupportedCapability, false, "The requested video capability is not available.");
            return Task.FromException<VideoGenerationSubmission>(new VideoGenerationAdapterException(LastError));
        }

        var handle = new VideoGenerationHandle($"opaque-operation-{Guid.NewGuid():N}");
        polls[handle.Value] = 0;
        return Task.FromResult(new VideoGenerationSubmission(handle, Status(VideoGenerationState.Submitted, 0)));
    }

    public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (terminalStatuses.TryGetValue(handle.Value, out var terminal)) return Task.FromResult(terminal);
        var poll = polls.AddOrUpdate(handle.Value, 1, (_, current) => current + 1);
        var status = poll switch
        {
            1 => Status(VideoGenerationState.Queued, 10),
            2 => Status(VideoGenerationState.Running, 50),
            _ => Status(VideoGenerationState.Succeeded, 100, includeArtifact: true),
        };
        if (status.IsTerminal) terminalStatuses[handle.Value] = status;
        return Task.FromResult(status);
    }

    public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (completedStatus.State != VideoGenerationState.Succeeded)
            return Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(new VideoGenerationError(VideoGenerationErrorCode.ArtifactUnavailable, false, "The video artifact is not ready.")));
        return Task.FromResult(new VideoGenerationArtifact(
            "video/mp4",
            "generated-clip.mp4",
            Clip.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(Clip, writable: false)),
            5,
            new VideoResolution(1280, 720),
            "8C4F0B7F3B1F7D9C7E08B5D33E5B8A9E6E4E1E6B7709E3A6E0C6C9F8A4E7C8A1"));
    }

    public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (terminalStatuses.TryGetValue(handle.Value, out var terminal))
            return Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.AlreadyTerminal, terminal));
        var cancelled = Status(VideoGenerationState.Cancelled, 0);
        terminalStatuses[handle.Value] = cancelled;
        return Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.Accepted, cancelled));
    }

    public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string eventId = "synthetic-event-1";
        lock (callbackIds)
        {
            if (!callbackIds.Add(eventId))
                return Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Duplicate));
        }
        return Task.FromResult(new VideoGenerationCallbackResult(
            VideoCallbackDisposition.Accepted,
            new VideoGenerationCallback(eventId, new VideoGenerationHandle("opaque-operation-1"), Status(VideoGenerationState.Running, 50))));
    }

    private static VideoGenerationStatus Status(VideoGenerationState state, int progress, bool includeArtifact = false) => new(
        state,
        progress,
        DateTimeOffset.UtcNow,
        includeArtifact ? new VideoGenerationArtifactDescriptor("video/mp4", "generated-clip.mp4", Clip.Length, 5, new VideoResolution(1280, 720)) : null,
        new VideoUsageEvidence(state == VideoGenerationState.Succeeded ? null : 0.12m, state == VideoGenerationState.Succeeded ? 0.12m : null, "USD", VideoCostBasis.ProviderReported, [new(VideoUsageMetric.GeneratedDurationSeconds, 5)]),
        null,
        new Dictionary<string, string> { ["state"] = state.ToString().ToLowerInvariant() });
}

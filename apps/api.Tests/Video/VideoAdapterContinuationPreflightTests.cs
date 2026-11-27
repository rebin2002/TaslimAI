using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;
using Taslim.Api.Movies;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-neutral admission contract for operation continuation on the neutral
/// <see cref="IVideoGenerationAdapter"/> boundary.
///
/// Continuation is the only capability dimension an adapter declares
/// (<see cref="VideoGenerationCapabilities.SupportsContinuation"/>) that
/// <see cref="VideoGenerationCapabilities.Supports(VideoGenerationRequest)"/> never
/// evaluates, so a continuation request is admitted and handed to the adapter - the
/// point at which provider work is queued and usage evidence is reserved - instead of
/// failing closed as an unsupported capability. Every sibling admission path in this
/// repository already fails closed on this exact dimension
/// (DirectVideoRequestNormalizer.Normalize, RunwayMovieVideoProvider.BuildPayload and
/// ManusMovieVideoProvider.BuildCreateRequest all reject a continuation identifier for
/// an adapter that does not support it), and
/// <see cref="VideoGenerationAdapterJobHandler"/> actively populates
/// <see cref="VideoGenerationRequest.Continuation"/> from
/// <see cref="MovieGenerationInput.ContinuationProviderJobId"/>, so the gap is reachable
/// from a canonical movie generation job.
///
/// Deterministic and provider-free: the recording adapter refuses and records any
/// submission attempt, and no usage reservation or customer charge is created anywhere
/// on this path.
/// </summary>
public sealed class VideoAdapterContinuationPreflightTests
{
    [Fact]
    public void Supports_continuation_only_when_the_adapter_declares_it()
    {
        var request = Request(new VideoGenerationHandle("provider-parent-1"));

        Assert.False(BuildCapabilities(supportsContinuation: false).Supports(request));
        Assert.True(BuildCapabilities(supportsContinuation: true).Supports(request));
    }

    [Fact]
    public void Supports_an_ordinary_request_when_continuation_is_unsupported()
    {
        var capabilities = BuildCapabilities(supportsContinuation: false);

        Assert.True(capabilities.Supports(Request()));
        Assert.True(capabilities.Supports(Request(continuation: null)));
    }

    [Fact]
    public void Supports_treats_a_blank_continuation_handle_as_a_continuation_request()
    {
        Assert.False(BuildCapabilities(supportsContinuation: false)
            .Supports(Request(new VideoGenerationHandle("   "))));
    }

    [Fact]
    public async Task SubmitAsync_fails_closed_before_the_adapter_is_asked_to_queue_the_operation()
    {
        var adapter = new RecordingVideoAdapter(supportsContinuation: false);
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => service.SubmitAsync(Request(new VideoGenerationHandle("provider-parent-1"))));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task ExecuteAsync_fails_closed_before_the_adapter_is_asked_to_queue_the_operation()
    {
        var adapter = new RecordingVideoAdapter(supportsContinuation: false);
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => service.ExecuteAsync(Request(new VideoGenerationHandle("provider-parent-1"))));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(0, adapter.SubmitCount);
        Assert.Equal(0, adapter.CancelCount);
    }

    [Fact]
    public async Task SubmitAsync_still_queues_continuation_for_an_adapter_that_declares_support()
    {
        var adapter = new RecordingVideoAdapter(supportsContinuation: true);
        var service = Service(adapter);

        var session = await service.SubmitAsync(Request(new VideoGenerationHandle("provider-parent-1")));

        Assert.Equal(1, adapter.SubmitCount);
        Assert.Equal("provider-parent-1", adapter.LastSubmitted?.Continuation?.Value);
        Assert.Equal(VideoGenerationState.Submitted, session.CurrentStatus.State);
    }

    [Fact]
    public async Task Job_handler_maps_the_persisted_continuation_identifier_and_fails_closed_before_submission()
    {
        var adapter = new RecordingVideoAdapter(supportsContinuation: false);
        var handler = Handler(adapter);

        Assert.True(handler.CanHandle(GenerationJobTypes.MovieClipGenerate));

        var job = Job(continuationProviderJobId: "provider-parent-1");

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task Job_handler_still_submits_an_ordinary_job_when_continuation_is_unsupported()
    {
        var adapter = new RecordingVideoAdapter(supportsContinuation: false);
        var handler = Handler(adapter);

        var job = Job(continuationProviderJobId: null);

        await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(1, adapter.SubmitCount);
        Assert.Null(adapter.LastSubmitted?.Continuation);
    }

    private static VideoGenerationAdapterJobHandler Handler(RecordingVideoAdapter adapter) => new(
        Service(adapter),
        adapter,
        Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 1,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }),
        NullLogger<VideoGenerationAdapterJobHandler>.Instance);

    private static GenerationJob Job(string? continuationProviderJobId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        CreatedByUserId = Guid.NewGuid(),
        JobType = GenerationJobTypes.MovieClipGenerate,
        InputJson = JsonSerializer.Serialize(Input(continuationProviderJobId)),
    };

    private static VideoGenerationAdapterExecutionService Service(IVideoGenerationAdapter adapter) =>
        new(adapter, Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 2,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }));

    private static VideoGenerationCapabilities BuildCapabilities(bool supportsContinuation) => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
        new HashSet<VideoResolution> { new(1280, 720) },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
        1,
        600,
        SupportsPolling: true,
        SupportsCallbacks: false,
        SupportsCancellation: true,
        SupportsContinuation: supportsContinuation,
        SupportsReferenceImages: true);

    private static VideoGenerationRequest Request(VideoGenerationHandle? continuation = null) => new(
        "scene-clip",
        "A slow camera move through a sunlit forest.",
        5,
        "16:9",
        new VideoResolution(1280, 720),
        "cinematic",
        "en",
        "No text overlays.",
        null,
        continuation,
        "continuation-preflight-1");

    private static MovieGenerationInput Input(string? continuationProviderJobId) => new(
        "scene-clip",
        Guid.NewGuid(),
        Guid.NewGuid(),
        null,
        null,
        "A slow camera move through a sunlit forest.",
        5,
        "16:9",
        "cinematic",
        "en",
        null,
        null,
        null,
        null,
        ContinuationProviderJobId: continuationProviderJobId);

    /// <summary>
    /// A deterministic, provider-free adapter. A submission means provider work would
    /// be queued, so unsupported submissions are recorded and refused.
    /// </summary>
    private sealed class RecordingVideoAdapter : IVideoGenerationAdapter
    {
        private readonly VideoGenerationCapabilities capabilities;

        public RecordingVideoAdapter(bool supportsContinuation) =>
            capabilities = BuildCapabilities(supportsContinuation);

        public int SubmitCount { get; private set; }
        public int CancelCount { get; private set; }
        public VideoGenerationRequest? LastSubmitted { get; private set; }

        public string AdapterKey => "recording-continuation-preflight";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => capabilities;

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubmitCount++;
            LastSubmitted = request;
            if (!HonoursDeclaredCapabilities(request))
                throw new VideoGenerationAdapterException(new VideoGenerationError(
                    VideoGenerationErrorCode.UnsupportedCapability, false, "The requested video capability is not available."));
            return Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("recording-operation-1"),
                Status(VideoGenerationState.Submitted, 0)));
        }

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status(VideoGenerationState.Running, 50));

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
            Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable()));

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.FromResult(new VideoCancellationResult(
                VideoCancellationOutcome.Accepted, Status(VideoGenerationState.Cancelled, 0)));
        }

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Ignored));

        /// <summary>
        /// Re-states the dimensions a correct adapter must refuse at submission,
        /// deliberately independent of
        /// <see cref="VideoGenerationCapabilities.Supports(VideoGenerationRequest)"/> so a
        /// fail-open in that shared preflight cannot hide inside this fixture.
        /// </summary>
        private bool HonoursDeclaredCapabilities(VideoGenerationRequest request) =>
            capabilities.SupportsOperation(request.Operation)
            && request.DurationSeconds >= capabilities.MinDurationSeconds
            && request.DurationSeconds <= capabilities.MaxDurationSeconds
            && capabilities.SupportsAspectRatio(request.AspectRatio)
            && !(request.ReferenceImages is { Count: > 0 } && !capabilities.SupportsReferenceImages)
            && !(request.Continuation is not null && !capabilities.SupportsContinuation);

        private static VideoGenerationStatus Status(VideoGenerationState state, int progress) =>
            new(state, progress, DateTimeOffset.UtcNow);
    }
}

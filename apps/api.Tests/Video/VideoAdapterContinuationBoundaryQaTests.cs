using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Generation;
using Taslim.Api.Movies;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

/// <summary>
/// Independent synthetic QA for the PR #886 boundary: the provider-neutral video
/// adapter must reject a continuation request on an adapter that declares
/// SupportsContinuation = false before IVideoGenerationAdapter.SubmitAsync is
/// asked to queue provider work, before submission-side usage evidence exists to
/// reserve, and before the canonical movie job handler can produce any billable
/// asset. Deterministic and provider-free: the recording fake below refuses
/// nothing on the network, contacts no provider, and books no charge.
/// Positive controls pin that supported continuations and ordinary requests are
/// still admitted, so the guard cannot be satisfied by over-rejecting.
/// </summary>
public sealed class VideoAdapterContinuationBoundaryQaTests
{
    private static readonly VideoGenerationHandle ContinuationHandle = new("synthetic-provider-job-continuation-1");

    // ---- failure axes (expected RED on main without #886's two-line guard) ----

    [Fact]
    public void Preflight_capability_contract_rejects_continuation_when_adapter_does_not_support_it()
    {
        var capabilities = Capabilities(supportsContinuation: false);

        Assert.False(capabilities.Supports(Request(ContinuationHandle)));
        Assert.False(capabilities.Supports(Request(new VideoGenerationHandle("   "))));
        Assert.True(capabilities.Supports(Request(continuation: null)));
    }

    [Fact]
    public async Task SubmitAsync_rejects_unsupported_continuation_before_the_adapter_is_asked_to_queue_work()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: false);
        var service = Service(recorder);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request(ContinuationHandle)));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, exception.Error.Code);
        Assert.Equal(0, recorder.SubmitCount);
        Assert.Equal(0, recorder.CancelCount);
        Assert.Empty(recorder.SubmittedRequests);
    }

    [Fact]
    public async Task SubmitAsync_treats_a_whitespace_continuation_handle_as_a_continuation_request_not_as_absent()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: false);
        var service = Service(recorder);

        await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request(new VideoGenerationHandle("   "))));

        // Malformed-flags negative case: a blank-but-present handle is fail-closed
        // as a continuation request, so the adapter is still never asked to queue.
        Assert.Equal(0, recorder.SubmitCount);
    }

    [Fact]
    public async Task ExecuteAsync_fails_closed_before_submission_so_no_usage_evidence_can_be_reserved_and_no_session_cancelled()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: false);
        var service = Service(recorder);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.ExecuteAsync(Request(ContinuationHandle)));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        // Usage evidence for the neutral path is produced by the adapter submission
        // itself; with zero submissions nothing exists beside the rejection to
        // reserve, and the pre-session failure means no cancel lifecycle started.
        Assert.Equal(0, recorder.SubmitCount);
        Assert.Equal(0, recorder.CancelCount);
    }

    [Fact]
    public async Task JobHandler_maps_persisted_continuation_id_and_fails_closed_before_the_handler_can_produce_billable_output()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: false);

        var handler = new VideoGenerationAdapterJobHandler(
            Service(recorder), recorder, EnabledOptions(), NullLogger<VideoGenerationAdapterJobHandler>.Instance);
        Assert.True(handler.CanHandle(GenerationJobTypes.MovieClipGenerate));

        var job = new GenerationJob
        {
            Id = Guid.Empty,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Title = "Synthetic QA continuation job",
            InputJson = JsonSerializer.Serialize(Input(ContinuationProviderJobId: ContinuationHandle.Value)),
        };

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        // The exception escapes the handler before any GenerationHandlerResult is
        // built, so the pipeline records no output asset and no usage evidence for
        // reservation at the canonical enqueue/claim boundary.
        Assert.Equal(0, recorder.SubmitCount);
        Assert.Equal(0, recorder.CancelCount);
    }

    // ---- positive controls (expected GREEN in both states: no over-rejection) ----

    [Fact]
    public async Task SubmitAsync_admits_continuation_when_the_adapter_declares_support()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: true);
        var service = Service(recorder);

        var session = await service.SubmitAsync(Request(ContinuationHandle));

        Assert.Equal(VideoGenerationState.Submitted, session.CurrentStatus.State);
        Assert.Equal(1, recorder.SubmitCount);
        Assert.Equal(ContinuationHandle.Value, recorder.SubmittedRequests.Single().Continuation?.Value);
    }

    [Fact]
    public async Task JobHandler_completes_a_supported_continuation_job_and_carries_it_through_the_full_lifecycle()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: true);
        var handler = new VideoGenerationAdapterJobHandler(
            Service(recorder), recorder, EnabledOptions(), NullLogger<VideoGenerationAdapterJobHandler>.Instance);

        var job = new GenerationJob
        {
            Id = Guid.Empty,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Title = "Synthetic QA supported continuation job",
            InputJson = JsonSerializer.Serialize(Input(ContinuationProviderJobId: ContinuationHandle.Value)),
        };

        var result = await handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);

        Assert.Equal(1, recorder.SubmitCount);
        Assert.Equal(ContinuationHandle.Value, recorder.SubmittedRequests.Single().Continuation?.Value);
        Assert.NotEmpty(result.Outputs);
    }

    [Fact]
    public async Task JobHandler_admits_ordinary_jobs_without_continuation_and_maps_the_absent_id_to_null()
    {
        var recorder = new RecordingVideoAdapter(supportsContinuation: false);
        var handler = new VideoGenerationAdapterJobHandler(
            Service(recorder), recorder, EnabledOptions(), NullLogger<VideoGenerationAdapterJobHandler>.Instance);

        var job = new GenerationJob
        {
            Id = Guid.Empty,
            JobType = GenerationJobTypes.MovieClipGenerate,
            Title = "Synthetic QA ordinary job",
            InputJson = JsonSerializer.Serialize(Input(ContinuationProviderJobId: " ")),
        };

        var result = await handler.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);

        // Non-regression guard: blank persisted id means absent continuation, and
        // an ordinary request must still reach a capable-enough adapter.
        Assert.Equal(1, recorder.SubmitCount);
        Assert.Null(recorder.SubmittedRequests.Single().Continuation);
        Assert.NotEmpty(result.Outputs);
    }

    // ---- fixtures ----

    private static VideoGenerationRequest Request(VideoGenerationHandle? continuation = null) => new(
        "scene-clip",
        "A slow camera move through a sunlit forest.",
        5,
        "16:9",
        new VideoResolution(1280, 720),
        "cinematic",
        "en",
        null,
        null,
        continuation,
        "qa-886-boundary-1");

    private static VideoGenerationCapabilities Capabilities(bool supportsContinuation) => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
        new HashSet<VideoResolution> { new(1280, 720) },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
        1,
        10,
        SupportsPolling: true,
        SupportsCallbacks: false,
        SupportsCancellation: true,
        SupportsContinuation: supportsContinuation,
        SupportsReferenceImages: true);

    private static MovieGenerationInput Input(string? ContinuationProviderJobId) => new(
        "scene-clip",
        Guid.Empty,
        Guid.Empty,
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
        null,
        ContinuationProviderJobId: ContinuationProviderJobId);

    private static VideoGenerationAdapterExecutionService Service(IVideoGenerationAdapter adapter) =>
        new(adapter, Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 8,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }));

    private static IOptions<VideoGenerationAdapterOptions> EnabledOptions() =>
        Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 8,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        });

    /// <summary>
    /// Self-contained recording fake. It never contacts a provider; it counts the
    /// lifecycle calls the boundary must not make for a rejected continuation and
    /// replays a deterministic submit-poll-retrieve walk for admitted requests.
    /// </summary>
    private sealed class RecordingVideoAdapter : IVideoGenerationAdapter
    {
        private static readonly byte[] Clip = "synthetic-qa-clip"u8.ToArray();
        private readonly Dictionary<string, int> polls = new(StringComparer.Ordinal);

        public RecordingVideoAdapter(bool supportsContinuation) =>
            Capabilities = Capabilities(supportsContinuation);

        public string AdapterKey => "qa-886-recording-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities { get; }
        public int SubmitCount { get; private set; }
        public int CancelCount { get; private set; }
        public List<VideoGenerationRequest> SubmittedRequests { get; } = [];

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoGenerationContract.ValidateRequest(request);
            SubmitCount++;
            SubmittedRequests.Add(request);
            var handle = new VideoGenerationHandle($"synthetic-handle-{SubmitCount}");
            polls[handle.Value] = 0;
            return Task.FromResult(new VideoGenerationSubmission(handle, Status(VideoGenerationState.Submitted, 0)));
        }

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
        {
            var poll = polls.TryGetValue(handle.Value, out var current) ? current + 1 : 1;
            polls[handle.Value] = poll;
            var status = poll switch
            {
                1 => Status(VideoGenerationState.Queued, 10),
                2 => Status(VideoGenerationState.Running, 50),
                _ => new VideoGenerationStatus(
                    VideoGenerationState.Succeeded,
                    100,
                    DateTimeOffset.UtcNow,
                    new VideoGenerationArtifactDescriptor("video/mp4", "qa-clip.mp4", Clip.Length, 5, new VideoResolution(1280, 720))),
            };
            return Task.FromResult(status);
        }

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default)
        {
            if (completedStatus.State != VideoGenerationState.Succeeded)
                return Task.FromException<VideoGenerationArtifact>(
                    new VideoGenerationAdapterException(new VideoGenerationError(VideoGenerationErrorCode.ArtifactUnavailable, false, "The video artifact is not ready.")));
            return Task.FromResult(new VideoGenerationArtifact(
                "video/mp4",
                "qa-clip.mp4",
                Clip.Length,
                _ => Task.FromResult<Stream>(new MemoryStream(Clip)),
                5,
                new VideoResolution(1280, 720)));
        }

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
        {
            CancelCount++;
            return Task.FromResult(new VideoCancellationResult(
                VideoCancellationOutcome.Accepted,
                new VideoGenerationStatus(VideoGenerationState.Cancelled, 0, DateTimeOffset.UtcNow)));
        }

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Ignored));

        private static VideoGenerationStatus Status(VideoGenerationState state, int progress) =>
            new(state, progress, DateTimeOffset.UtcNow);
    }
}

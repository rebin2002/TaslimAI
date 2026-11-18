using Microsoft.Extensions.Options;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

/// <summary>
/// Independent QA probe of the same pre-submission cost-preflight gate as PR #1033
/// (<see cref="VideoGenerationAdapterExecutionService.SubmitAsync"/>), covering the
/// boundary its nine tests leave open: provider-declared duration/resolution metadata
/// that is <em>absent</em> (a null capability collection, i.e. a provider capability
/// response in which the field is missing) or <em>nonfinite-extreme</em> (saturated
/// <see cref="int.MaxValue"/> / <see cref="int.MinValue"/> duration window), as opposed
/// to the present-but-invalid shapes #1033 pins (empty set, <c>0x0</c> entry, inverted or
/// non-positive window). The neutral envelope declares duration and resolution as
/// <c>int</c>, so the reachable nonfinite boundary in it is the saturation edge, not IEEE
/// NaN, and the decimal cost dimension is only ever emitted past the paid-call boundary.
/// Every rejection case asserts that the paid call (<c>SubmitCount</c>), the usage/billing
/// row the submission would carry (<c>UsageRowCount</c>), and the post-submission surfaces
/// a durable consumer would enqueue against are all untouched, and that the failure is a
/// normalized <see cref="VideoGenerationAdapterExecutionException"/> instead of a raw
/// <see cref="NullReferenceException"/> escaping into the worker. Deterministic and
/// provider-free: in-memory fakes only, no provider, credential, network, database or paid
/// call.
/// </summary>
public sealed class VideoGenerationAdapterMissingCapabilityMetadataPreflightTests
{
    [Fact]
    public async Task Absent_declared_output_content_types_do_not_admit_the_paid_call()
    {
        // A capability response missing the output content-type field carries no format or
        // pricing evidence at all. Unlike #1033's non-video content-type case, the set is
        // not populated with a bad value: it is null, and nothing on the unfixed
        // submission path ever reads this field, so the request is admitted and submitted.
        var adapter = new CountingMetadataAdapter(Declaring(missing: MissingFields.OutputContentTypes));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task An_absent_declared_resolution_set_is_normalized_instead_of_crashing_the_worker()
    {
        // Distinct from #1033's empty-set case: a null resolution declaration. On the
        // unfixed path this is dereferenced inside VideoGenerationCapabilities.Supports
        // and escapes SubmitAsync unhandled, because the catch list there covers
        // ArgumentException only, so the worker never receives a normalized rejection.
        var adapter = new CountingMetadataAdapter(Declaring(missing: MissingFields.SupportedResolutions));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task Absent_declared_operations_are_refused_as_malformed_before_the_paid_call()
    {
        // A missing operation declaration cannot be matched against the request at all;
        // it must fail closed as the deterministic malformed rejection rather than be
        // dereferenced on the submission path.
        var adapter = new CountingMetadataAdapter(Declaring(missing: MissingFields.Operations));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task Absent_declared_aspect_ratios_are_refused_as_malformed_before_the_paid_call()
    {
        // A missing aspect-ratio declaration is the same absent-metadata shape on the
        // framing dimension this cost preflight prices against.
        var adapter = new CountingMetadataAdapter(Declaring(missing: MissingFields.SupportedAspectRatios));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task A_saturated_positive_duration_window_is_refused_without_reaching_the_paid_call()
    {
        // The nonfinite edge of the int duration envelope: min == max == int.MaxValue is
        // internally consistent, so it survives envelope validation, but no admissible
        // request duration falls inside it. The gate must answer with a normalized
        // unsupported-capability refusal and no usage row, never an exception from the
        // comparison itself.
        var adapter = new CountingMetadataAdapter(Declaring(minDuration: int.MaxValue, maxDuration: int.MaxValue));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task A_saturated_negative_duration_window_fails_closed_as_malformed()
    {
        // Negative saturation of the same envelope: min == max == int.MinValue is an
        // impossible declaration. Gating on it yields the wrong category and lets the
        // broken metadata silently decide admissions; the boundary must be reported as
        // malformed before the paid call.
        var adapter = new CountingMetadataAdapter(Declaring(minDuration: int.MinValue, maxDuration: int.MinValue));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        AssertNoEnqueueOrBillingEvidence(adapter);
    }

    [Fact]
    public async Task A_fully_declared_envelope_still_submits_exactly_once_with_one_usage_row()
    {
        // Control: the probes above must not over-close the gate. A complete declaration
        // is admitted, crosses the paid-call boundary exactly once, and that single call
        // is the only place enqueue/billing evidence can be produced.
        var adapter = new CountingMetadataAdapter(Declaring());
        var service = Service(adapter);

        var session = await service.SubmitAsync(Request());

        Assert.Equal(VideoGenerationState.Running, session.CurrentStatus.State);
        Assert.Equal(1, adapter.SubmitCount);
        Assert.Equal(1, adapter.UsageRowCount);
        Assert.False(string.IsNullOrWhiteSpace(session.Handle.Value));
    }

    [Flags]
    private enum MissingFields
    {
        None = 0,
        Operations = 1,
        SupportedResolutions = 2,
        SupportedAspectRatios = 4,
        OutputContentTypes = 8,
    }

    private static readonly HashSet<string> DefaultOperations =
        new(StringComparer.OrdinalIgnoreCase) { "scene-clip" };

    private static readonly HashSet<string> DefaultAspectRatios =
        new(StringComparer.OrdinalIgnoreCase) { "16:9" };

    private static readonly HashSet<string> DefaultOutputContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "video/mp4" };

    private static readonly HashSet<VideoResolution> DefaultResolutions =
        new() { new VideoResolution(1280, 720) };

    /// <summary>
    /// Rejection witness: no paid call, no usage/billing row, no handle for a durable
    /// consumer to enqueue against, and no post-submission surface touched.
    /// </summary>
    private static void AssertNoEnqueueOrBillingEvidence(CountingMetadataAdapter adapter)
    {
        Assert.Equal(0, adapter.SubmitCount);
        Assert.Equal(0, adapter.UsageRowCount);
        Assert.Equal(0, adapter.StatusCallCount);
        Assert.Equal(0, adapter.ArtifactCallCount);
    }

    private static VideoGenerationAdapterExecutionService Service(IVideoGenerationAdapter adapter) =>
        new(adapter, Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 4,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }));

    private static VideoGenerationRequest Request() => new(
        "scene-clip",
        "A synthetic test clip for the absent-metadata capability preflight contract.",
        5,
        "16:9",
        new VideoResolution(1280, 720),
        null,
        null,
        null,
        null,
        null,
        "absent-metadata-preflight-1");

    /// <summary>
    /// Builds a provider-declared capability envelope. A flagged field is emitted as
    /// <c>null</c>, modelling a provider capability response in which that field is absent
    /// entirely; the healthy contract never produces that shape, but a hand-built or
    /// deserialized envelope can.
    /// </summary>
    private static VideoGenerationCapabilities Declaring(
        MissingFields missing = MissingFields.None,
        int minDuration = 1,
        int maxDuration = 600) => new(
            missing.HasFlag(MissingFields.Operations) ? null! : DefaultOperations,
            missing.HasFlag(MissingFields.SupportedResolutions) ? null! : DefaultResolutions,
            missing.HasFlag(MissingFields.SupportedAspectRatios) ? null! : DefaultAspectRatios,
            missing.HasFlag(MissingFields.OutputContentTypes) ? null! : DefaultOutputContentTypes,
            minDuration,
            maxDuration,
            SupportsPolling: true,
            SupportsCallbacks: false,
            SupportsCancellation: true,
            SupportsContinuation: false,
            SupportsReferenceImages: false);

    /// <summary>
    /// Synthetic provider-free adapter. The declared envelope comes from the test; the
    /// counters witness the paid-call boundary, the usage/billing row it would emit, and
    /// the post-submission surfaces a durable consumer would enqueue against.
    /// </summary>
    private sealed class CountingMetadataAdapter(VideoGenerationCapabilities capabilities) : IVideoGenerationAdapter
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

        public string AdapterKey => "absent-metadata-preflight-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => capabilities;
        public int SubmitCount { get; private set; }
        public int UsageRowCount { get; private set; }
        public int StatusCallCount { get; private set; }
        public int ArtifactCallCount { get; private set; }

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            // Crossing this line is the paid call, and the usage evidence it returns is
            // the row a billing/enqueue consumer would persist.
            SubmitCount++;
            UsageRowCount++;
            var pricedSeconds = (decimal)request.DurationSeconds;
            var pricedPixels = 0m;
            if (request.RequestedResolution is { } resolution)
                pricedPixels = (decimal)resolution.Width * resolution.Height;
            return Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("absent-metadata-preflight-opaque"),
                new VideoGenerationStatus(VideoGenerationState.Running, 0, now),
                new VideoUsageEvidence(0.05m, null, "USD", VideoCostBasis.Estimated, new[]
                {
                    new VideoUsageMeasure(VideoUsageMetric.GeneratedDurationSeconds, pricedSeconds),
                    new VideoUsageMeasure(VideoUsageMetric.OutputPixels, pricedPixels),
                })));
        }

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default)
        {
            StatusCallCount++;
            return Task.FromResult(new VideoGenerationStatus(VideoGenerationState.Running, 50, now));
        }

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default)
        {
            ArtifactCallCount++;
            return Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable()));
        }

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.Accepted, new VideoGenerationStatus(VideoGenerationState.Cancelled, 100, now)));

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected));
    }
}

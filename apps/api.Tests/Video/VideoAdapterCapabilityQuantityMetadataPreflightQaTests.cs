using Microsoft.Extensions.Options;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

/// <summary>
/// Independent narrow QA probe of the pre-submission cost preflight gate of PR #1033
/// (<see cref="VideoGenerationAdapterExecutionService.SubmitAsync"/>), pinned on the one
/// declared-capability-metadata axis that #1033's nine tests and the sibling #1090
/// absent/saturated-metadata probes both leave unpinned: the declared output-quantity
/// envelope <see cref="VideoGenerationCapabilities.MaxOutputs"/>. The quantity dimension
/// is not a duration or resolution case; it is the billing-count field of the capability
/// record ("how many billable outputs this provider declaration supports per request"),
/// and a non-positive declaration (0 or negative) is self-contradictory metadata: an
/// adapter that says it produces no outputs at all. Before #1033 the submission path
/// never consulted the envelope, so such a declaration silently admitted the request
/// and crossed the paid-call boundary (reproduced: fake adapter submit call count 1 on
/// the unfixed production file); with #1033's ValidateDeclaredCapabilities gate the
/// contract validator rejects it and the request fails closed as a deterministic,
/// non-transient malformed-response. Every rejection case asserts submit call count zero
/// on an in-memory counting fake — no provider, credential, network, billing, secret,
/// or customer data is touched. A positive control pins that a healthy multi-output
/// declaration is still admitted exactly once, so the gate does not over-close.
/// </summary>
public sealed class VideoAdapterCapabilityQuantityMetadataPreflightQaTests
{
    [Theory]
    [InlineData(0)]           // zero declared output capacity: an impossible provider declaration
    [InlineData(-1)]          // negative quantity: corrupt capability metadata
    [InlineData(int.MinValue)]// saturated negative quantity: corrupt metadata at the numeric edge
    public async Task A_non_positive_declared_output_quantity_fails_closed_before_the_paid_call(int maxOutputs)
    {
        // Everything else in the envelope is well-declared, so the only defect is the
        // quantity dimension. The request itself is fully admissible on the duration/
        // resolution/aspect axes pinned by #1033, isolating the unpinned boundary.
        var adapter = new CountingQuantityAdapter(Declaring(maxOutputs: maxOutputs));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        // The rejection must not echo provider-declared metadata values.
        Assert.DoesNotContain(maxOutputs.ToString(), exception.Error.SafeMessage, StringComparison.Ordinal);
        // The paid-call boundary was never reached.
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task A_valid_multi_output_declaration_is_still_admitted_exactly_once()
    {
        // Control: the quantity gate must not over-close. MaxOutputs is admissible at
        // any value >= 1; a healthy two-output declaration submits exactly one priced
        // request, proving the fail-closed case above turns on the malformed metadata,
        // not on any blanket suspicion of the quantity field.
        var adapter = new CountingQuantityAdapter(Declaring(maxOutputs: 2));
        var service = Service(adapter);

        var session = await service.SubmitAsync(Request());

        Assert.Equal(VideoGenerationState.Running, session.CurrentStatus.State);
        Assert.Equal(1, adapter.SubmitCount);
        Assert.Equal("quantity-preflight-opaque", session.Handle.Value);
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
        "A synthetic test clip for the capability quantity-metadata preflight contract.",
        5,
        "16:9",
        new VideoResolution(1280, 720),
        null,
        null,
        null,
        null,
        null,
        "quantity-preflight-1");

    private static VideoGenerationCapabilities Declaring(int maxOutputs) => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
            new HashSet<VideoResolution> { new(1280, 720) },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
            1,
            600,
            SupportsPolling: true,
            SupportsCallbacks: false,
            SupportsCancellation: true,
            SupportsContinuation: false,
            SupportsReferenceImages: false,
            MaxOutputs: maxOutputs);

    /// <summary>
    /// Synthetic, provider-free adapter whose declared capability envelope is supplied
    /// by the test and whose submit call count is the witness of the paid-call
    /// boundary (<see cref="IVideoGenerationAdapter.SubmitAsync"/>).
    /// </summary>
    private sealed class CountingQuantityAdapter(VideoGenerationCapabilities capabilities) : IVideoGenerationAdapter
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

        public string AdapterKey => "quantity-preflight-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => capabilities;
        public int SubmitCount { get; private set; }

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("quantity-preflight-opaque"),
                new VideoGenerationStatus(VideoGenerationState.Running, 0, now)));
        }

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationStatus(VideoGenerationState.Running, 50, now));

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default) =>
            Task.FromException<VideoGenerationArtifact>(new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable()));

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoCancellationResult(VideoCancellationOutcome.Accepted, new VideoGenerationStatus(VideoGenerationState.Cancelled, 100, now)));

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected));
    }
}

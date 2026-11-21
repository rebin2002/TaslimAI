using Microsoft.Extensions.Options;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

/// <summary>
/// Fail-closed contract for the pre-submission cost preflight of the neutral Direct
/// Video execution path. The adapter capability envelope is provider-declared
/// duration/resolution metadata, and it is the last gate between a priced request and
/// <see cref="IVideoGenerationAdapter.SubmitAsync"/>, the paid-call boundary. The
/// regressions pinned here are that a request carrying a concrete resolution must never
/// be admitted by a provider that declares no resolution capabilities, and that a
/// malformed declared envelope (inverted or non-positive duration window, invalid
/// resolution entry, non-video output content type) must fail closed as a deterministic
/// malformed-response rejection instead of silently gating on the broken metadata.
/// In every rejection case the adapter submit call count is asserted to be zero, so no
/// paid call is reachable. Provider-free and deterministic: only in-memory fakes are
/// used, so no provider, credential, or network dependency is involved.
/// </summary>
public sealed class VideoGenerationAdapterCapabilityPreflightTests
{
    [Fact]
    public async Task A_concrete_resolution_is_refused_when_the_provider_declares_no_resolution_capabilities()
    {
        // A capability record with an empty resolution set carries no priced resolution
        // evidence. Requesting 3840x2160 through such an envelope must not reach submit.
        var adapter = new CountingCapabilityAdapter(Declaring(resolutions: new HashSet<VideoResolution>()));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request(new VideoResolution(3840, 2160))));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.UnsupportedCapability, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task A_resolution_the_provider_did_not_declare_is_refused_before_submission()
    {
        var adapter = new CountingCapabilityAdapter(Declaring());
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request(new VideoResolution(1920, 1080))));

        Assert.Equal(VideoGenerationFailureCategory.UnsupportedCapability, exception.Category);
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task An_unspecified_resolution_stays_admissible_when_the_provider_declares_none()
    {
        // The provider-decides semantics (null requested resolution) is unchanged by the
        // fail-closed gate; only concrete resolutions require an explicit declaration.
        var adapter = new CountingCapabilityAdapter(Declaring(resolutions: new HashSet<VideoResolution>()));
        var service = Service(adapter);

        var session = await service.SubmitAsync(Request(providerDecidesResolution: true));

        Assert.Equal(1, adapter.SubmitCount);
        Assert.Equal("capability-preflight-opaque", session.Handle.Value);
    }

    [Theory]
    [InlineData(10, 5, false, false)]   // inverted duration window
    [InlineData(0, 600, false, false)]  // zero provider minimum admits durations the cost estimator never priced
    [InlineData(-5, -1, false, false)]  // wholly non-positive duration window
    [InlineData(1, 600, true, false)]   // invalid (0x0) resolution entry inside the declared set
    [InlineData(1, 600, false, true)]   // non-video declared output content type
    public async Task A_malformed_declared_envelope_fails_closed_before_the_paid_call(
        int minDuration,
        int maxDuration,
        bool includeInvalidResolution,
        bool nonVideoContentTypes)
    {
        var resolutions = new HashSet<VideoResolution> { new(1280, 720) };
        if (includeInvalidResolution)
            resolutions.Add(new VideoResolution(0, 0));
        var contentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in nonVideoContentTypes ? new[] { "video/mp4", "image/png" } : new[] { "video/mp4" })
            contentTypes.Add(value);

        var adapter = new CountingCapabilityAdapter(Declaring(
            resolutions: resolutions,
            minDuration: minDuration,
            maxDuration: maxDuration,
            contentTypes: contentTypes));
        var service = Service(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(() =>
            service.SubmitAsync(Request()));

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        // The rejection must not echo provider-declared metadata values.
        Assert.DoesNotContain("600", exception.Error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(0, adapter.SubmitCount);
    }

    [Fact]
    public async Task A_well_declared_envelope_submits_exactly_once()
    {
        // Control: the preflight gate must not over-close on a healthy capability record.
        var adapter = new CountingCapabilityAdapter(Declaring());
        var service = Service(adapter);

        var session = await service.SubmitAsync(Request());

        Assert.Equal(VideoGenerationState.Running, session.CurrentStatus.State);
        Assert.Equal(1, adapter.SubmitCount);
    }

    private static VideoGenerationAdapterExecutionService Service(IVideoGenerationAdapter adapter) =>
        new(adapter, Options.Create(new VideoGenerationAdapterOptions
        {
            Enabled = true,
            MaxPollAttempts = 4,
            PollIntervalMilliseconds = 0,
            MaxExecutionSeconds = 30,
        }));

    private static VideoGenerationRequest Request(VideoResolution? resolution = null, bool providerDecidesResolution = false) => new(
        "scene-clip",
        "A synthetic test clip for the capability preflight contract.",
        5,
        "16:9",
        providerDecidesResolution ? null : resolution ?? new VideoResolution(1280, 720),
        null,
        null,
        null,
        null,
        null,
        "capability-preflight-1");

    private static VideoGenerationCapabilities Declaring(
        IReadOnlySet<VideoResolution>? resolutions = null,
        int minDuration = 1,
        int maxDuration = 600,
        IReadOnlySet<string>? contentTypes = null) => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
            resolutions ?? new HashSet<VideoResolution> { new(1280, 720) },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
            contentTypes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
            minDuration,
            maxDuration,
            SupportsPolling: true,
            SupportsCallbacks: false,
            SupportsCancellation: true,
            SupportsContinuation: false,
            SupportsReferenceImages: false);

    /// <summary>
    /// Synthetic, provider-free adapter whose declared capability envelope is supplied
    /// by the test and whose submit call count proves whether the paid-call boundary
    /// was reached.
    /// </summary>
    private sealed class CountingCapabilityAdapter(VideoGenerationCapabilities capabilities) : IVideoGenerationAdapter
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

        public string AdapterKey => "capability-preflight-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => capabilities;
        public int SubmitCount { get; private set; }

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("capability-preflight-opaque"),
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

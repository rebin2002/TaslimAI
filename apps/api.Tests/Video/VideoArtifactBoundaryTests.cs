using Microsoft.Extensions.Options;
using Taslim.Api.Video;
using Xunit;

namespace Taslim.Api.Tests.Video;

/// <summary>
/// Fail-closed contract for the artifact boundary of the neutral video execution path.
/// The regressions pinned here are that retrieval is refused before the adapter is
/// asked whenever the operation has not reached the succeeded state, that a missing
/// (null) or structurally invalid artifact coming back from an adapter is normalized
/// into the safe malformed-response failure instead of surfacing a persistable output,
/// that an adapter reporting the asset as unavailable keeps its normalized error, that
/// the ordinary successful download still exposes readable stream-backed bytes, and
/// that no failure path yields a storable artifact. Provider-free and deterministic:
/// only in-memory fakes are used, so no provider, credential, or paid call is reachable
/// from these tests.
/// </summary>
public sealed class VideoArtifactBoundaryTests
{
    private static readonly byte[] ClipBytes = [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D];

    [Fact]
    public async Task Retrieval_is_refused_before_the_adapter_is_asked_for_a_non_terminal_operation()
    {
        var adapter = new ArtifactBoundaryVideoAdapter(VideoGenerationState.Running, null);
        var session = await SubmitAsync(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => session.RetrieveArtifactAsync());

        Assert.Equal(VideoGenerationFailureCategory.ArtifactUnavailable, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.ArtifactUnavailable, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
        Assert.Equal(0, adapter.RetrieveCount);
    }

    [Fact]
    public async Task Ordinary_download_streams_artifact_bytes_from_a_succeeded_operation()
    {
        var adapter = new ArtifactBoundaryVideoAdapter(VideoGenerationState.Succeeded, ValidArtifact());
        var session = await SubmitAsync(adapter);

        var artifact = await session.RetrieveArtifactAsync();

        Assert.Equal("video/mp4", artifact.ContentType);
        Assert.Equal("clip.mp4", artifact.FileName);
        Assert.Equal(ClipBytes.LongLength, artifact.SizeBytes);
        Assert.Equal(1, adapter.RetrieveCount);
        await using var stream = await artifact.OpenReadAsync(CancellationToken.None);
        using var reader = new BinaryReader(stream);
        Assert.Equal(ClipBytes, reader.ReadBytes(ClipBytes.Length));
    }

    [Theory]
    [InlineData("", "clip.mp4", 12)]
    [InlineData("text/plain", "clip.mp4", 12)]
    [InlineData("video", "clip.mp4", 12)]
    [InlineData("application/json", "clip.mp4", 12)]
    [InlineData("video/mp4", "", 12)]
    [InlineData("video/mp4", "../clip.mp4", 12)]
    [InlineData("video/mp4", "media/clip.mp4", 12)]
    [InlineData("video/mp4", "clip.mp4", 0)]
    [InlineData("video/mp4", "clip.mp4", -1)]
    public async Task An_artifact_that_is_not_a_storable_video_fails_closed_as_malformed(
        string contentType, string fileName, long sizeBytes)
    {
        var artifact = new VideoGenerationArtifact(
            contentType, fileName, sizeBytes, _ => Task.FromResult<Stream>(new MemoryStream(ClipBytes)));
        var adapter = new ArtifactBoundaryVideoAdapter(VideoGenerationState.Succeeded, artifact);
        var session = await SubmitAsync(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => session.RetrieveArtifactAsync());

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.Equal(VideoGenerationErrorCode.MalformedResponse, exception.Error.Code);
        Assert.Equal(1, adapter.RetrieveCount);
    }

    [Fact]
    public async Task A_null_artifact_from_the_adapter_fails_closed_as_malformed()
    {
        var adapter = new ArtifactBoundaryVideoAdapter(VideoGenerationState.Succeeded, null);
        var session = await SubmitAsync(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterExecutionException>(
            () => session.RetrieveArtifactAsync());

        Assert.Equal(VideoGenerationFailureCategory.MalformedResponse, exception.Category);
        Assert.DoesNotContain("clip", exception.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_adapter_reported_missing_asset_keeps_the_artifact_unavailable_code()
    {
        var adapter = new ArtifactBoundaryVideoAdapter(VideoGenerationState.Succeeded, null)
        {
            FailRetrieveWithArtifactUnavailable = true,
        };
        var session = await SubmitAsync(adapter);

        var exception = await Assert.ThrowsAsync<VideoGenerationAdapterException>(
            () => session.RetrieveArtifactAsync());

        Assert.Equal(VideoGenerationErrorCode.ArtifactUnavailable, exception.Error.Code);
        Assert.False(exception.Error.IsTransient);
    }

    private static VideoGenerationArtifact ValidArtifact() => new(
        "video/mp4",
        "clip.mp4",
        ClipBytes.LongLength,
        _ => Task.FromResult<Stream>(new MemoryStream(ClipBytes)));

    private static async Task<VideoGenerationAdapterExecutionSession> SubmitAsync(ArtifactBoundaryVideoAdapter adapter)
    {
        var service = new VideoGenerationAdapterExecutionService(
            adapter,
            Options.Create(new VideoGenerationAdapterOptions { Enabled = true, PollIntervalMilliseconds = 0 }));
        return await service.SubmitAsync(new VideoGenerationRequest(
            "scene-clip", "A synthetic test clip.", 5, "16:9",
            new VideoResolution(1280, 720), "cinematic", "en", null, null, null, "artifact-boundary-1"));
    }

    /// <summary>
    /// Synthetic, provider-free adapter whose terminal state and artifact payload are
    /// supplied by the test, so the boundary decides what the caller observes.
    /// </summary>
    private sealed class ArtifactBoundaryVideoAdapter(
        VideoGenerationState terminalState,
        VideoGenerationArtifact? artifact) : IVideoGenerationAdapter
    {
        private static readonly VideoGenerationCapabilities Supported = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scene-clip" },
            new HashSet<VideoResolution> { new(1280, 720) },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "16:9" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" },
            1, 10, SupportsPolling: true, SupportsCallbacks: false,
            SupportsCancellation: true, SupportsContinuation: false,
            SupportsReferenceImages: false);

        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

        public string AdapterKey => "artifact-boundary-fake";
        public bool IsAvailable => true;
        public VideoGenerationCapabilities Capabilities => Supported;
        public int RetrieveCount { get; private set; }
        public bool FailRetrieveWithArtifactUnavailable { get; init; }

        public Task<VideoGenerationSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationSubmission(
                new VideoGenerationHandle("opaque"),
                new VideoGenerationStatus(terminalState, 100, now)));

        public Task<VideoGenerationStatus> GetStatusAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationStatus(terminalState, 100, now));

        public Task<VideoGenerationArtifact> RetrieveArtifactAsync(VideoGenerationHandle handle, VideoGenerationStatus completedStatus, CancellationToken cancellationToken = default)
        {
            RetrieveCount++;
            return FailRetrieveWithArtifactUnavailable
                ? Task.FromException<VideoGenerationArtifact>(
                    new VideoGenerationAdapterException(VideoGenerationError.ArtifactUnavailable("The video asset is missing.")))
                : Task.FromResult(artifact!);
        }

        public Task<VideoCancellationResult> CancelAsync(VideoGenerationHandle handle, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoCancellationResult(
                VideoCancellationOutcome.Accepted,
                new VideoGenerationStatus(VideoGenerationState.Cancelled, 100, now)));

        public Task<VideoGenerationCallbackResult> NormalizeCallbackAsync(VideoGenerationCallbackRequest callback, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VideoGenerationCallbackResult(VideoCallbackDisposition.Rejected));
    }
}

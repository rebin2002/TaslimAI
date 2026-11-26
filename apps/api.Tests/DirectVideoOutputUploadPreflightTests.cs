using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Provider-free, deterministic contract for the Direct Video output upload preflight.
///
/// The regression: a Direct Video provider output whose declared container disagreed with
/// its declared MIME type, or whose length exceeded the configured output ceiling, survived
/// the adapter boundary and was only rejected later at the private-storage boundary. Three
/// observable consequences followed. (1) The whole provider body was materialised in process
/// memory before any length was checked. (2) The rejection surfaced as
/// FileUploadValidationException, which the job runner maps to a storage failure code rather
/// than the provider-output contract violation DIRECT_VIDEO_OUTPUT_INVALID. (3) The output
/// response was never released, so every rejected retrieval leaked its response body.
///
/// Every transport here is an in-memory handler over synthetic bytes: no provider,
/// credential, network, or paid call is reachable from these tests.
/// </summary>
public sealed class DirectVideoOutputUploadPreflightTests
{
    // A minimal ISO-BMFF header: 24 byte box, "ftyp", major brand "isom".
    private static readonly byte[] FtypMp4 = [
        0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D,
        0, 0, 0, 0, 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0,
    ];

    private const long Ceiling = 4096;

    private static DirectVideoProviderOptions ProviderOptions() => new()
    {
        Enabled = true,
        ProviderKey = "synthetic-direct",
        ModelKey = "synthetic-model",
        ApiBaseUrl = "https://provider.invalid/api/",
        ApiKey = "synthetic-key",
        MaxOutputBytes = Ceiling,
    };

    private static DirectVideoStatus Succeeded(string contentType, string fileName) =>
        new(DirectVideoJobStatus.Succeeded, 100, contentType, fileName, FtypMp4.Length, 1);

    private static DirectVideoProviderOutput Normalize(string contentType, string fileName) =>
        DirectVideoResultNormalizer.NormalizeOutput(
            contentType, fileName, FtypMp4.Length,
            _ => Task.FromResult<Stream>(new MemoryStream(FtypMp4, writable: false)),
            1, null, null, null, null, null, "synthetic-model", Ceiling);

    [Theory]
    [InlineData("video/mp4", "clip.mp4")]
    [InlineData("video/quicktime", "clip.mov")]
    [InlineData("video/webm", "clip.webm")]
    public void Output_preflight_accepts_every_video_container_the_storage_boundary_persists(
        string contentType, string fileName)
    {
        // Guard against over-tightening: anything the adapter accepts must be storable.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(fileName, contentType);
        var output = Normalize(contentType, fileName);

        Assert.Equal(descriptor.Extension, Path.GetExtension(output.FileName).ToLowerInvariant());
        Assert.Equal(contentType, output.ContentType, ignoreCase: true);
    }

    [Theory]
    [InlineData("video/webm", "clip.mp4")]
    [InlineData("video/quicktime", "clip.mp4")]
    [InlineData("video/mp4", "clip.mkv")]
    [InlineData("video/mp4", "clip")]
    public void Output_preflight_rejects_a_declared_container_that_disagrees_with_the_declared_mime(
        string contentType, string fileName)
    {
        // This pair used to survive the adapter and fail only at the storage boundary.
        Assert.Throws<DirectVideoOutputNormalizationException>(() => Normalize(contentType, fileName));
    }

    [Fact]
    public void Output_preflight_rejects_exactly_what_the_storage_boundary_would_reject()
    {
        // Tightening the preflight must not lose a clip the pipeline could have kept, and
        // must not keep one it could not. Both boundaries are asked about the same pairs.
        foreach (var (contentType, fileName) in new[]
        {
            ("video/mp4", "clip.mp4"), ("video/quicktime", "clip.mov"), ("video/webm", "clip.webm"),
        })
        {
            Assert.Null(Record.Exception(() => GeneratedMediaSecurity.ValidateDescriptor(fileName, contentType)));
            Assert.Null(Record.Exception(() => Normalize(contentType, fileName)));
        }

        foreach (var (contentType, fileName) in new[]
        {
            ("video/webm", "clip.mp4"), ("video/quicktime", "clip.mp4"), ("video/mp4", "clip.mkv"),
        })
        {
            Assert.Throws<FileUploadValidationException>(() =>
                GeneratedMediaSecurity.ValidateDescriptor(fileName, contentType));
            Assert.Throws<DirectVideoOutputNormalizationException>(() => Normalize(contentType, fileName));
        }
    }

    [Fact]
    public void Container_and_mime_disagreement_is_rejected_before_any_provider_byte_is_downloaded()
    {
        var handler = new OutputHandler(FtypMp4.Length, FtypMp4, "video/webm", "clip.mp4");
        using var client = Client(handler);
        var adapter = new DirectVideoHttpAdapter(client, Options.Create(ProviderOptions()));

        Assert.Throws<DirectVideoOutputNormalizationException>(() =>
            adapter.RetrieveAsync("provider-job-1", Succeeded("video/webm", "clip.mp4"), CancellationToken.None)
                .GetAwaiter().GetResult());

        Assert.Equal(0, handler.Stream!.BytesRead);
        Assert.True(handler.Stream.Released, "a rejected retrieval must release the output response body");
    }

    [Fact]
    public void Declared_size_over_the_ceiling_is_rejected_before_the_body_is_buffered()
    {
        var oversized = new byte[Ceiling + 1];
        Array.Copy(FtypMp4, oversized, FtypMp4.Length);
        var handler = new OutputHandler(Ceiling + 1, oversized, "video/mp4", "clip.mp4");
        using var client = Client(handler);
        var adapter = new DirectVideoHttpAdapter(client, Options.Create(ProviderOptions()));

        Assert.Throws<DirectVideoOutputNormalizationException>(() =>
            adapter.RetrieveAsync("provider-job-1", Succeeded("video/mp4", "clip.mp4"), CancellationToken.None)
                .GetAwaiter().GetResult());

        // The whole point of a size preflight: the oversized body is never entered.
        Assert.Equal(0, handler.Stream!.BytesRead);
        Assert.True(handler.Stream.Released, "an oversized output must still release the response body");
    }

    [Fact]
    public void Absent_or_lying_length_is_rejected_without_buffering_an_unbounded_body()
    {
        // No declared Content-Length, and the body keeps streaming far past the ceiling.
        var handler = new OutputHandler(null, new byte[1024 * 1024], "video/mp4", "clip.mp4");
        using var client = Client(handler);
        var adapter = new DirectVideoHttpAdapter(client, Options.Create(ProviderOptions()));

        Assert.Throws<DirectVideoOutputNormalizationException>(() =>
            adapter.RetrieveAsync("provider-job-1", Succeeded("video/mp4", "clip.mp4"), CancellationToken.None)
                .GetAwaiter().GetResult());

        // A bounded read stops at the ceiling plus at most one transfer chunk.
        Assert.InRange(handler.Stream!.BytesRead, 1, Ceiling + 256);
        Assert.True(handler.Stream.Released, "an over-ceiling stream must release the response body");
    }

    [Fact]
    public void Output_within_both_limits_is_returned_and_released()
    {
        var handler = new OutputHandler(FtypMp4.Length, FtypMp4, "video/mp4", "clip.mp4");
        using var client = Client(handler);
        var adapter = new DirectVideoHttpAdapter(client, Options.Create(ProviderOptions()));

        var output = adapter.RetrieveAsync("provider-job-1", Succeeded("video/mp4", "clip.mp4"), CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Equal("video/mp4", output.ContentType);
        Assert.Equal("clip.mp4", output.FileName);
        Assert.Equal(FtypMp4.Length, output.SizeBytes);
        Assert.True(handler.Stream!.Released, "a completed buffered retrieval must release the response body");

        // The bytes survive the released transport because the adapter returns a copy.
        using var stream = output.OpenReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var reader = new BinaryReader(stream);
        Assert.Equal(FtypMp4, reader.ReadBytes(FtypMp4.Length));
    }

    private static HttpClient Client(OutputHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://provider.invalid/api/") };

    /// <summary>
    /// In-memory transport standing in for GET generations/{id}/output.
    /// </summary>
    private sealed class OutputHandler : HttpMessageHandler
    {
        private readonly long? declaredContentLength;
        private readonly byte[] bodyBytes;
        private readonly string contentType;
        private readonly string? fileName;

        public OutputHandler(long? declaredContentLength, byte[] bodyBytes, string contentType, string? fileName)
        {
            this.declaredContentLength = declaredContentLength;
            this.bodyBytes = bodyBytes;
            this.contentType = contentType;
            this.fileName = fileName;
        }

        public TrackingReadStream? Stream { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stream = new TrackingReadStream(bodyBytes);
            Stream = stream;
            var content = new StreamContent(stream);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (declaredContentLength is long length) content.Headers.ContentLength = length;
            if (fileName is not null)
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    /// <summary>
    /// A read stream that hands out at most 256 bytes per call, counts the bytes the
    /// consumer actually took, and records whether it was ever released.
    /// </summary>
    private sealed class TrackingReadStream : Stream
    {
        private readonly byte[] body;
        private int position;

        public TrackingReadStream(byte[] body) => this.body = body;

        public long BytesRead { get; private set; }
        public bool Released { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => body.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, 256), body.Length - position);
            if (take <= 0) return 0;
            Array.Copy(body, position, buffer, offset, take);
            position += take;
            BytesRead += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var scratch = new byte[Math.Min(buffer.Length, 256)];
            var read = Read(scratch, 0, scratch.Length);
            scratch.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Released = true;
            base.Dispose(disposing);
        }
    }
}

using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Taslim.Api.Domain;
using Xunit;
using FileOptions = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

/// <summary>
/// Test-only, provider-free regression reproduction for issue #689:
/// the declared generated-video <c>sizeBytes</c> is not forwarded to
/// <c>CountingReadStream.ExpectedContentLength</c>, so a non-seekable video S3-compatible
/// upload receives no explicit <c>Content-Length</c>, and an oversized source can slip past the
/// post-store size guard once a length-only fix is applied.
///
/// Scope guard (issue #689 coordination): this file edits NOTHING owned by the active owners.
/// It does not touch <c>apps/api/Files/FileProcessingService.cs</c> or
/// <c>apps/api/Files/S3CompatibleFileStorageService.cs</c>, nor the four test files in draft
/// PR #680 (<c>GeneratedStreamLifecycleTests.cs</c>, <c>S3CompatibleStorageTests.cs</c>,
/// <c>S3CompatibleStorageDiagnosticsTests.cs</c>, <c>GeneratedMediaSecurityTests.cs</c>),
/// nor Movie PR #269 or Studio PR #1050 files. The DB-backed full lifecycle stays with #680;
/// this is the isolated storage-seam reproduction. No real R2/S3 call, provider call, inference,
/// secret, billing or config change is made — only deterministic fakes plus the real pure
/// request-builder <c>AwsS3CompatibleObjectClient.CreatePutObjectRequest</c>.
/// </summary>
public sealed class GeneratedVideoStreamContentLengthRegressionTests
{
    private const string Bucket = "taslim-private";
    private const long MaxVideoBytes = 250L * 1024 * 1024;

    // GREEN control: proves the S3 adapter itself is already correct once a declared length is
    // forwarded, which isolates the defect to the generated-video wrapping (FileProcessingService),
    // not the request builder.
    [Fact]
    public async Task Forwarded_declared_length_yields_explicit_nonchunked_content_length()
    {
        const int declared = 2048;
        await using var counting = new CountingReadStream(MakeNonSeekable(declared), MaxVideoBytes, expectedContentLength: declared);
        var client = new CapturingPutAdapterClient();

        await CreateService(client).StoreAsync("ws/video.mp4", counting);

        var request = client.Captured!;
        Assert.Equal(declared.ToString(CultureInfo.InvariantCulture), HeaderOrEmpty(request, "Content-Length"));
        Assert.False(request.UseChunkEncoding);
        Assert.True(request.DisablePayloadSigning);
        Assert.True(request.DisableDefaultChecksumValidation);
        Assert.False(request.AutoResetStreamPosition);
        Assert.False(request.AutoCloseStream);
        Assert.Same(counting, request.InputStream);
        Assert.Equal(declared, client.BytesSent);
        Assert.False(client.ExcessRemains);
    }

    // RED reproduction of #689: mirrors FileProcessingService.StoreGeneratedStreamWithResultAsync
    // wrapping (new CountingReadStream(input, MaxGeneratedVideoBytes)) that does NOT forward sizeBytes.
    // Acceptance requires an explicit, correct length on the non-seekable PUT; on the current main tip
    // it is absent, so this assertion fails by design and is closed by draft PR #680.
    [Fact]
    public async Task Current_generated_video_wrapping_omits_declared_length_reproduction()
    {
        const int declared = 4096;
        await using var counting = new CountingReadStream(MakeNonSeekable(declared), MaxVideoBytes);
        var client = new CapturingPutAdapterClient();

        await CreateService(client).StoreAsync("ws/video.mp4", counting);

        Assert.Equal(declared.ToString(CultureInfo.InvariantCulture), HeaderOrEmpty(client.Captured!, "Content-Length"));
    }

    // Current non-forwarded path still fully drains to EOF, so a truncated source is observable via
    // BytesRead < declared (the basis of the post-store size guard). Deterministic, provider-free.
    [Fact]
    public async Task Shorter_than_declared_input_is_detectable_and_rejected()
    {
        const int declared = 100;
        await using var counting = new CountingReadStream(MakeNonSeekable(declared - 30), 1024 * 1024);
        var client = new CapturingPutAdapterClient();

        await CreateService(client).StoreAsync("ws/video.mp4", counting);

        Assert.Equal(declared - 30, client.BytesSent);
        Assert.True(counting.BytesRead < declared);
        Assert.False(client.ExcessRemains);
    }

    // On the current (non-forwarded) path an oversized source is caught because the reader drains to
    // EOF and BytesRead > declared. This documents the guard behavior a fix must preserve.
    [Fact]
    public async Task Excess_trailing_bytes_are_detectable_on_current_full_drain_path()
    {
        const int declared = 100;
        await using var counting = new CountingReadStream(MakeNonSeekable(declared + 60), 1024 * 1024);
        var client = new CapturingPutAdapterClient();

        await CreateService(client).StoreAsync("ws/video.mp4", counting);

        Assert.Equal(declared + 60, client.BytesSent);
        Assert.True(counting.BytesRead > declared);
    }

    // The #689 caveat: a naive length-only fix (forward declared size and let the client stop at
    // Content-Length without verifying EOF) leaves BytesRead == declared while extra source bytes
    // remain, so the post-store guard "BytesRead != sizeBytes" would pass. Proves EOF verification
    // is required by any fix. Deterministic, provider-free.
    [Fact]
    public async Task Header_only_fix_silently_skips_excess_unless_eof_is_verified()
    {
        const int declared = 100;
        const int actual = 160;
        await using var counting = new CountingReadStream(MakeNonSeekable(actual), 1024 * 1024, expectedContentLength: declared);
        var client = new CapturingPutAdapterClient { TrustDeclaredLengthWithoutEofCheck = true };

        await CreateService(client).StoreAsync("ws/video.mp4", counting);

        Assert.Equal(declared, client.BytesSent);   // the client stopped exactly at the header length
        Assert.Equal(declared, counting.BytesRead); // so the "BytesRead != sizeBytes" guard would pass
        var probe = new byte[1];                     // yet the source genuinely still carried extra bytes
        Assert.True(await counting.ReadAsync(probe) > 0);
    }

    // Bounded memory: reads stream through without materializing the payload, retain only the 64-byte
    // sniff prefix, and the async read path fails closed past the configured limit.
    [Fact]
    public async Task Counting_stream_bounds_memory_and_fails_closed_over_limit()
    {
        const int limit = 16;
        await using var counting = new CountingReadStream(MakeNonSeekable(64), limit);
        var buffer = new byte[8];
        InvalidDataException? failure = null;
        try
        {
            while (await counting.ReadAsync(buffer) > 0)
            {
            }
        }
        catch (InvalidDataException exception)
        {
            failure = exception;
        }

        Assert.NotNull(failure);
        Assert.True(counting.BytesRead <= limit);
        Assert.True(counting.Prefix.Length <= 64);
    }

    // Cancellation raised mid-stream must propagate as OperationCanceledException so the caller's
    // failed-storage cleanup path runs, rather than being masked as a generic storage failure.
    [Fact]
    public async Task Cancellation_during_generated_video_upload_propagates_operation_canceled()
    {
        await using var counting = new CountingReadStream(new CancellingNonSeekableStream(failsAfterReads: 1), MaxVideoBytes);
        var client = new CapturingPutAdapterClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService(client).StoreAsync("ws/video.mp4", counting));
    }

    // A storage-side provider failure must surface as the safe FileStorageOperationException that the
    // service catches to mark the file Failed and attempt private-object cleanup, without leaking detail.
    [Fact]
    public async Task Failed_storage_maps_to_safe_operation_exception_for_cleanup_path()
    {
        await using var counting = new CountingReadStream(MakeNonSeekable(32), MaxVideoBytes, expectedContentLength: 32);
        var client = new CapturingPutAdapterClient
        {
            Failure = new AmazonS3Exception("provider failure with r2-secret-value")
            {
                StatusCode = System.Net.HttpStatusCode.Forbidden,
                ErrorCode = "AccessDenied",
                RequestId = "synthetic-request-id",
            },
        };

        var exception = await Assert.ThrowsAsync<FileStorageOperationException>(() => CreateService(client).StoreAsync("ws/video.mp4", counting));
        Assert.DoesNotContain("r2-secret-value", exception.Message, StringComparison.Ordinal);
    }

    private static S3CompatibleFileStorageService CreateService(IS3CompatibleObjectClient client) =>
        new(client, Options.Create(new FileOptions
        {
            StorageProvider = FileStorageProviders.S3Compatible,
            S3Bucket = Bucket,
        }), NullLogger<S3CompatibleFileStorageService>.Instance);

    private static string HeaderOrEmpty(PutObjectRequest request, string name)
    {
        try { return request.Headers[name] ?? string.Empty; }
        catch (KeyNotFoundException) { return string.Empty; }
    }

    private static DeterministicNonSeekableStream MakeNonSeekable(int length) => new(new byte[Math.Max(0, length)]);

    // Deterministic, single-pass, non-seekable fake for a generated video byte source.
    private class DeterministicNonSeekableStream(byte[] payload) : Stream
    {
        private int position;
        public int TotalLength => payload.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var remaining = payload.Length - position;
            if (remaining <= 0) return 0;
            var take = Math.Min(remaining, buffer.Length);
            payload.AsSpan(position, take).CopyTo(buffer[..take]);
            position += take;
            return take;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<int>(cancellationToken)
                : Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled<int>(cancellationToken)
                : new ValueTask<int>(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Non-seekable source that raises cancellation after a fixed number of async reads.
    private sealed class CancellingNonSeekableStream(int failsAfterReads) : DeterministicNonSeekableStream(new byte[failsAfterReads * 4 + 8])
    {
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++reads > failsAfterReads)
                throw new OperationCanceledException(cancellationToken);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    // Capturing adapter: never contacts a provider. It invokes the real pure request-builder and then
    // reads the stream the way an SDK would, to observe Content-Length and excess-byte semantics.
    private sealed class CapturingPutAdapterClient : IS3CompatibleObjectClient
    {
        public PutObjectRequest? Captured { get; private set; }
        public long BytesSent { get; private set; }
        public bool ExcessRemains { get; private set; }
        public bool TrustDeclaredLengthWithoutEofCheck { get; init; }
        public Exception? Failure { get; init; }

        public async Task PutAsync(string bucket, string key, Stream content, CancellationToken cancellationToken = default)
        {
            var request = AwsS3CompatibleObjectClient.CreatePutObjectRequest(bucket, key, content);
            Captured = request;
            if (Failure is not null)
                throw Failure;

            var header = Header(request, "Content-Length");
            long? declaredLength = header.Length > 0 && long.TryParse(header, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

            var buffer = new byte[64];
            long read = 0;
            long stop = declaredLength is > 0 && TrustDeclaredLengthWithoutEofCheck ? declaredLength.Value : long.MaxValue;
            int n;
            while (read < stop && (n = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, stop - read)), cancellationToken)) > 0)
                read += n;
            BytesSent = read;

            // Only probe for trailing data when we did not deliberately trust the header and stop early;
            // stopping early is precisely the failure mode a correct fix must avoid.
            if (!(declaredLength is > 0 && TrustDeclaredLengthWithoutEofCheck))
            {
                var probe = new byte[1];
                ExcessRemains = await content.ReadAsync(probe.AsMemory(), cancellationToken) > 0;
            }
        }

        public Task<Stream?> GetAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public void Dispose() { }

        private static string Header(PutObjectRequest request, string name)
        {
            try { return request.Headers[name] ?? string.Empty; }
            catch (KeyNotFoundException) { return string.Empty; }
        }
    }
}

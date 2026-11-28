using System.Buffers.Binary;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class FileValidationSecurityTests
{
    private static readonly byte[] ValidPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFUlEQVR4nGMUS9nCwMDAwMDAxAADABTaATIYltfAAAAAAElFTkSuQmCC");

    private static readonly byte[] ValidJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDlaKKK+vPlj//Z");

    private static readonly byte[] ValidWebpLossless = Convert.FromBase64String(
        "UklGRh4AAABXRUJQVlA4TBEAAAAvAkAAAAdQslqUtv+BiOh/AAA=");

    private static readonly byte[] ValidWebpLossy = Convert.FromBase64String(
        "UklGRjgAAABXRUJQVlA4ICwAAADQAQCdASoDAAIAAgA0JaACdLoB+AADsAD+771X/maJjmP5M//KVeCxub4AAA==");

    private static readonly byte[] ValidWebpExtended = Convert.FromBase64String(
        "UklGRloAAABXRUJQVlA4WAoAAAAQAAAAAgAAAQAAQUxQSAcAAAAAgICAgICAAFZQOCAsAAAA0AEAnQEqAwACAAIANCWgAnS6AfgAA7AA/u+9V/5miY5j+TP/ylXgsbm+AAA=");

    private static readonly byte[] ValidAnimatedWebp = ExtendedWebp(
        4,
        4,
        AnimationFrame(0, 0, 2, 2, WebpChunk("VP8 ", Vp8FrameHeader(2, 2))),
        AnimationFrame(2, 2, 2, 2, WebpChunk("VP8L", Vp8LFrameHeader(2, 2))));

    [Fact]
    public async Task Small_valid_png_jpeg_and_webp_images_remain_accepted()
    {
        var fixtures = new (string FileName, string ContentType, byte[] Bytes)[]
        {
            ("small.png", "image/png", ValidPng),
            ("small.jpg", "image/jpeg", ValidJpeg),
            ("small-lossless.webp", "image/webp", ValidWebpLossless),
            ("small-lossy.webp", "image/webp", ValidWebpLossy),
            ("small-extended.webp", "image/webp", ValidWebpExtended),
            ("small-animated.webp", "image/webp", ValidAnimatedWebp),
        };

        foreach (var fixture in fixtures)
        {
            var result = await CreateValidator().ValidateAsync(CreateFile(fixture.FileName, fixture.ContentType, fixture.Bytes));

            Assert.Equal(Path.GetExtension(fixture.FileName), result.Extension);
            Assert.Equal(fixture.ContentType, result.ContentType);
        }
    }

    [Fact]
    public async Task Tiny_png_with_extreme_declared_pixels_is_rejected_before_storage()
    {
        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            CreateValidator().ValidateAsync(CreateFile("image.png", "image/png", PngHeader(50_000, 50_000))));

        Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Per_axis_and_total_pixel_limits_are_enforced_for_png_jpeg_and_webp()
    {
        var builders = new (string FileName, string ContentType, Func<uint, uint, byte[]> Build)[]
        {
            ("image.png", "image/png", PngHeader),
            ("image.jpg", "image/jpeg", JpegHeader),
            ("image.webp", "image/webp", WebpHeader),
        };
        var oversizedDimensions = new (uint Width, uint Height)[]
        {
            (16_385, 1),
            (5_001, 5_000),
        };

        foreach (var builder in builders)
        {
            foreach (var dimensions in oversizedDimensions)
            {
                var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() =>
                    CreateValidator().ValidateAsync(CreateFile(
                        builder.FileName,
                        builder.ContentType,
                        builder.Build(dimensions.Width, dimensions.Height))));

                Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Dimension_limits_are_inclusive_at_the_configured_boundary()
    {
        var validator = CreateValidator();

        var maxAxis = await validator.ValidateAsync(CreateFile("axis.png", "image/png", PngHeader(16_384, 1)));
        var maxPixels = await validator.ValidateAsync(CreateFile("pixels.png", "image/png", PngHeader(5_000, 5_000)));

        Assert.Equal(".png", maxAxis.Extension);
        Assert.Equal(".png", maxPixels.Extension);
    }

    [Fact]
    public async Task Extended_webp_rejects_coded_frames_larger_than_the_declared_canvas()
    {
        var fixtures = new[]
        {
            ExtendedWebp(2, 2, WebpChunk("VP8 ", Vp8FrameHeader(3, 2))),
            ExtendedWebp(2, 2, WebpChunk("VP8L", Vp8LFrameHeader(2, 3))),
        };

        foreach (var fixture in fixtures)
        {
            await Assert.ThrowsAsync<FileUploadValidationException>(() =>
                CreateValidator().ValidateAsync(CreateFile("oversized-frame.webp", "image/webp", fixture)));
        }
    }

    [Fact]
    public async Task Extended_webp_rejects_animation_frames_outside_canvas_or_limits()
    {
        var fixtures = new[]
        {
            ExtendedWebp(2, 2, AnimationFrame(2, 0, 2, 2, WebpChunk("VP8 ", Vp8FrameHeader(2, 2)))),
            ExtendedWebp(16_384, 1, AnimationFrame(0, 0, 16_385, 1, WebpChunk("VP8 ", Vp8FrameHeader(16_383, 1)))),
        };

        foreach (var fixture in fixtures)
        {
            await Assert.ThrowsAsync<FileUploadValidationException>(() =>
                CreateValidator().ValidateAsync(CreateFile("oversized-animation.webp", "image/webp", fixture)));
        }
    }

    [Fact]
    public async Task Extended_webp_accepts_bounded_multiframe_image_payload_beyond_one_megabyte_without_reading_payload()
    {
        const int largeImagePayloadBytes = 1_100_000;
        var bytes = ExtendedWebp(
            4,
            4,
            AnimationFrame(0, 0, 2, 2, WebpChunk("VP8 ", Vp8FrameHeader(2, 2, largeImagePayloadBytes))),
            AnimationFrame(2, 2, 2, 2, WebpChunk("VP8L", Vp8LFrameHeader(2, 2))));
        Assert.True(bytes.Length > 1024 * 1024);

        var stream = new CountingSeekableStream(bytes);
        var result = await CreateValidator().ValidateAsync(CreateFile("large-animated.webp", "image/webp", stream, bytes.Length));

        Assert.Equal(".webp", result.Extension);
        Assert.True(stream.BytesRead < 1024, $"Expected header-only reads, observed {stream.BytesRead} bytes.");
    }

    [Fact]
    public async Task Extended_webp_rejects_out_of_bounds_late_frame_after_large_image_payload()
    {
        const int largeImagePayloadBytes = 1_100_000;
        var bytes = ExtendedWebp(
            4,
            4,
            AnimationFrame(0, 0, 2, 2, WebpChunk("VP8 ", Vp8FrameHeader(2, 2, largeImagePayloadBytes))),
            AnimationFrame(4, 2, 2, 2, WebpChunk("VP8L", Vp8LFrameHeader(2, 2))));
        Assert.True(bytes.Length > 1024 * 1024);

        var stream = new CountingSeekableStream(bytes);
        await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            CreateValidator().ValidateAsync(CreateFile("late-out-of-bounds.webp", "image/webp", stream, bytes.Length)));

        Assert.True(stream.BytesRead < 1024, $"Expected header-only reads, observed {stream.BytesRead} bytes.");
    }

    [Fact]
    public async Task Extended_webp_rejects_truncated_late_animation_frame()
    {
        var truncated = ValidAnimatedWebp[..^1];

        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            CreateValidator().ValidateAsync(CreateFile("truncated-animation.webp", "image/webp", truncated)));

        Assert.Contains("header", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extended_webp_rejects_when_aggregate_header_read_budget_is_exhausted()
    {
        var bytes = ExtendedWebpWithEmptyChunks(
            4,
            4,
            131_100,
            AnimationFrame(0, 0, 2, 2, WebpChunk("VP8 ", Vp8FrameHeader(2, 2))));
        var stream = new CountingSeekableStream(bytes);

        await Assert.ThrowsAsync<FileUploadValidationException>(() =>
            CreateValidator().ValidateAsync(CreateFile("header-budget.webp", "image/webp", stream, bytes.Length)));

        Assert.InRange(stream.BytesRead, 1_000_000L, 1024L * 1024);
    }

    [Fact]
    public async Task Malformed_or_incomplete_image_headers_are_rejected()
    {
        var malformedPng = PngHeader(320, 180);
        BinaryPrimitives.WriteUInt32BigEndian(malformedPng.AsSpan(8, 4), 12);

        var malformedWebp = (byte[])ValidWebpLossless.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(malformedWebp.AsSpan(16, 4), uint.MaxValue);

        var fixtures = new (string FileName, string ContentType, byte[] Bytes)[]
        {
            ("short.png", "image/png", ValidPng[..8]),
            ("bad-length.png", "image/png", malformedPng),
            ("short.jpg", "image/jpeg", JpegHeader(320, 180)[..6]),
            ("zero-width.jpg", "image/jpeg", JpegHeader(0, 180)),
            ("short-lossless.webp", "image/webp", ValidWebpLossless[..20]),
            ("bad-length.webp", "image/webp", malformedWebp),
        };

        foreach (var fixture in fixtures)
        {
            await Assert.ThrowsAsync<FileUploadValidationException>(() =>
                CreateValidator().ValidateAsync(CreateFile(fixture.FileName, fixture.ContentType, fixture.Bytes)));
        }
    }

    [Fact]
    public void Current_filename_sanitizer_still_removes_windows_paths_and_format_overrides()
    {
        var fileName = "C:\\uploads\\report" + '\u202E' + ".png";

        Assert.Equal("report.png", FileValidationService.SanitizeFileName(fileName));
    }

    private static FileValidationService CreateValidator() =>
        new(Options.Create(new Taslim.Api.Files.FileOptions()));

    private static FormFile CreateFile(string fileName, string contentType, byte[] bytes) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private static FormFile CreateFile(string fileName, string contentType, Stream stream, long length) =>
        new(stream, 0, length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };

    private static byte[] PngHeader(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    private static byte[] JpegHeader(uint width, uint height)
    {
        if (width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));

        // A minimal SOF0 frame header with one component.
        var bytes = new byte[15];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xC0;
        bytes[4] = 0;
        bytes[5] = 11;
        bytes[6] = 8;
        bytes[7] = (byte)(height >> 8);
        bytes[8] = (byte)height;
        bytes[9] = (byte)(width >> 8);
        bytes[10] = (byte)width;
        bytes[11] = 1;
        bytes[12] = 1;
        bytes[13] = 0x11;
        bytes[14] = 0;
        return bytes;
    }

    private static byte[] WebpHeader(uint width, uint height)
    {
        if (width is 0 or > 16_777_216 || height is 0 or > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(width));

        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 22);
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));
        "VP8X"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 10);
        WriteUInt24LittleEndian(bytes.AsSpan(24, 3), width - 1);
        WriteUInt24LittleEndian(bytes.AsSpan(27, 3), height - 1);
        return bytes;
    }

    private static byte[] ExtendedWebp(uint width, uint height, params byte[][] chunks)
    {
        var vp8xPayload = new byte[10];
        if (chunks.Any(chunk => chunk.AsSpan(0, 4).SequenceEqual("ANMF"u8)))
            vp8xPayload[0] = 0x02;
        WriteUInt24LittleEndian(vp8xPayload.AsSpan(4, 3), width - 1);
        WriteUInt24LittleEndian(vp8xPayload.AsSpan(7, 3), height - 1);

        var allChunks = new List<byte[]> { WebpChunk("VP8X", vp8xPayload) };
        allChunks.AddRange(chunks);
        var chunkBytesLength = allChunks.Sum(chunk => chunk.Length);
        var bytes = new byte[12 + chunkBytesLength];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));

        var offset = 12;
        foreach (var chunk in allChunks)
        {
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        return bytes;
    }

    private static byte[] ExtendedWebpWithEmptyChunks(uint width, uint height, int emptyChunkCount, params byte[][] chunks)
    {
        if (emptyChunkCount < 0) throw new ArgumentOutOfRangeException(nameof(emptyChunkCount));

        var vp8xPayload = new byte[10];
        if (chunks.Any(chunk => chunk.AsSpan(0, 4).SequenceEqual("ANMF"u8)))
            vp8xPayload[0] = 0x02;
        WriteUInt24LittleEndian(vp8xPayload.AsSpan(4, 3), width - 1);
        WriteUInt24LittleEndian(vp8xPayload.AsSpan(7, 3), height - 1);

        var vp8xChunk = WebpChunk("VP8X", vp8xPayload);
        var emptyChunksLength = checked(emptyChunkCount * 8);
        var trailingChunksLength = chunks.Sum(chunk => chunk.Length);
        var bytes = new byte[checked(12 + vp8xChunk.Length + emptyChunksLength + trailingChunksLength)];
        "RIFF"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));

        var offset = 12;
        vp8xChunk.CopyTo(bytes, offset);
        offset += vp8xChunk.Length;
        for (var index = 0; index < emptyChunkCount; index++)
        {
            "JUNK"u8.CopyTo(bytes.AsSpan(offset, 4));
            offset += 8;
        }

        foreach (var chunk in chunks)
        {
            chunk.CopyTo(bytes, offset);
            offset += chunk.Length;
        }

        return bytes;
    }

    private static byte[] WebpChunk(string fourCc, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Vp8FrameHeader(uint width, uint height, int trailingBytes = 0)
    {
        var payload = new byte[checked(10 + trailingBytes)];
        new byte[] { 0x9D, 0x01, 0x2A }.CopyTo(payload, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), (ushort)height);
        return payload;
    }

    private static byte[] Vp8LFrameHeader(uint width, uint height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        var dimensions = (width - 1) | ((height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), dimensions);
        return payload;
    }

    private static byte[] AnimationFrame(uint x, uint y, uint width, uint height, byte[] imageChunk)
    {
        if ((x & 1) != 0 || (y & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(x), "WebP frame offsets are stored divided by two.");

        var payload = new byte[16 + imageChunk.Length];
        WriteUInt24LittleEndian(payload.AsSpan(0, 3), x / 2);
        WriteUInt24LittleEndian(payload.AsSpan(3, 3), y / 2);
        WriteUInt24LittleEndian(payload.AsSpan(6, 3), width - 1);
        WriteUInt24LittleEndian(payload.AsSpan(9, 3), height - 1);
        imageChunk.CopyTo(payload, 16);
        return WebpChunk("ANMF", payload);
    }

    private static void WriteUInt24LittleEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    private sealed class CountingSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes, writable: false);

        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

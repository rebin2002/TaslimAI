using System.Buffers.Binary;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GeneratedMediaImageValidationTests
{
    [Fact]
    public void Signature_only_png_is_rejected_before_generated_storage()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("truncated.png", "image/png");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, PngSignature(), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Truncated_jpeg_is_rejected_before_generated_storage()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("truncated.jpg", "image/jpeg");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, new byte[] { 0xFF, 0xD8, 0xFF }, new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Incomplete_webp_is_rejected_before_generated_storage()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("truncated.webp", "image/webp");
        var incomplete = new byte[12];
        "RIFF"u8.CopyTo(incomplete.AsSpan(0, 4));
        "WEBP"u8.CopyTo(incomplete.AsSpan(8, 4));

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, incomplete, new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Complete_png_remains_accepted()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("complete.png", "image/png");

        GeneratedMediaSecurity.ValidateContent(descriptor, CompletePng(320, 180), new Taslim.Api.Files.FileOptions());
    }

    [Fact]
    public void Structurally_complete_png_with_invalid_dimensions_reaches_quality_control()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("invalid-dimensions.png", "image/png");

        GeneratedMediaSecurity.ValidateContent(descriptor, CompletePng(0, 0), new Taslim.Api.Files.FileOptions());
    }

    [Fact]
    public void Png_without_image_data_is_rejected_before_generated_storage()
    {
        // Header plus terminator with no IDAT chunk: the old presence test called this complete, but
        // it decodes to nothing, which is exactly what a response truncated inside the IDAT chain
        // looks like, so it must be refused rather than stored as an asset.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("no-image-data.png", "image/png");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, PngWithoutImageData(), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Png_with_only_empty_image_data_chunks_is_rejected_before_generated_storage()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("empty-idat.png", "image/png");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, PngWithEmptyImageData(), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Png_with_non_zero_bytes_after_the_terminator_is_rejected_before_generated_storage()
    {
        // Only zero fill may trail IEND, mirroring the rule the JPEG walk applies after EOI.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("appended.png", "image/png");
        var content = CompletePng(320, 180);
        var withAppendedData = new byte[content.Length + 4];
        content.CopyTo(withAppendedData, 0);
        new byte[] { 0x41, 0x42, 0x43, 0x44 }.CopyTo(withAppendedData, content.Length);

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, withAppendedData, new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Webp_with_only_an_extended_header_is_rejected_before_generated_storage()
    {
        // VP8X carries canvas metadata and flag bits only; without a following VP8/VP8L chunk or an
        // ANMF frame the container has no picture at all.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("header-only.webp", "image/webp");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xOnly(), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Webp_with_an_extended_header_and_a_frame_is_accepted()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("extended.webp", "image/webp");

        GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xThenVp8(), new Taslim.Api.Files.FileOptions());
    }

    [Fact]
    public void Webp_with_an_extended_header_and_an_animated_frame_is_accepted()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("animated.webp", "image/webp");

        GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xAnimated(), new Taslim.Api.Files.FileOptions());
    }

    [Fact]
    public void Webp_with_an_animated_frame_carrying_alpha_first_is_accepted()
    {
        // A compliant animated file may put the frame's ALPH chunk before its VP8/VP8L sub-chunk, so
        // the frame must be found by walking the ANMF sub-chunks rather than by reading one fixed
        // offset. Both ALPH sizes are exercised because the odd case carries a pad byte.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("animated-alpha.webp", "image/webp");
        var descriptorEven = GeneratedMediaSecurity.ValidateDescriptor("animated-alpha-even.webp", "image/webp");

        GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xAnimated(alphaPayloadLength: 7), new Taslim.Api.Files.FileOptions());
        GeneratedMediaSecurity.ValidateContent(descriptorEven, WebpVp8xAnimated(alphaPayloadLength: 8), new Taslim.Api.Files.FileOptions());
    }

    [Fact]
    public void Webp_with_an_animated_frame_holding_only_an_alpha_chunk_is_rejected_before_generated_storage()
    {
        // The frame header and an ALPH chunk describe transparency, not samples: an ANMF with no
        // VP8/VP8L sub-chunk is an empty animation.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("alpha-only.webp", "image/webp");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xAnimated(includeFrameImage: false), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public void Webp_with_an_animated_frame_shorter_than_the_frame_header_is_rejected_before_generated_storage()
    {
        // 12 bytes before the nested chunk was the shape an earlier version of this gate accepted.
        // The ANMF header is 16 bytes, so a payload that cannot hold it has no frame at all.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("short-anmf.webp", "image/webp");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, WebpVp8xAnimated(shortFrameHeader: true), new Taslim.Api.Files.FileOptions()));
    }

    private static byte[] PngSignature() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] CompletePng(uint width, uint height)
    {
        // Includes a non-empty IDAT chunk: a complete PNG must carry image data, not merely open with
        // IHDR and close with IEND.
        var bytes = new byte[58];
        PngSignature().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), 1);
        "IDAT"u8.CopyTo(bytes.AsSpan(37, 4));
        bytes[41] = 0x78;
        "IEND"u8.CopyTo(bytes.AsSpan(50, 4));
        return bytes;
    }

    private static byte[] PngWithoutImageData()
    {
        var bytes = new byte[45];
        PngSignature().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 320);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 180);
        "IEND"u8.CopyTo(bytes.AsSpan(37, 4));
        return bytes;
    }

    private static byte[] PngWithEmptyImageData()
    {
        var bytes = new byte[57];
        PngSignature().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 320);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), 180);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), 0);
        "IDAT"u8.CopyTo(bytes.AsSpan(37, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(45, 4), 0);
        "IEND"u8.CopyTo(bytes.AsSpan(49, 4));
        return bytes;
    }

    private static byte[] WebpBody(byte[][] chunks, int riffSize)
    {
        var body = new List<byte>();
        body.AddRange("RIFF"u8.ToArray());
        body.AddRange(BitConverter.GetBytes(riffSize));
        body.AddRange("WEBP"u8.ToArray());
        foreach (var chunk in chunks) body.AddRange(chunk);
        return [.. body];
    }

    private static byte[] WebpChunk(string fourCC, byte[] payload)
    {
        var chunk = new List<byte>();
        chunk.AddRange(System.Text.Encoding.ASCII.GetBytes(fourCC));
        chunk.AddRange(BitConverter.GetBytes(payload.Length));
        chunk.AddRange(payload);
        if (payload.Length % 2 == 1) chunk.Add(0x00);
        return [.. chunk];
    }

    private static byte[] WebpVp8xOnly()
    {
        var header = WebpChunk("VP8X", new byte[] { 0x04, 0, 0, 0, 0x3F, 0, 0, 0x3F, 0, 0 });
        return WebpBody([header], 4 + header.Length);
    }

    private static byte[] WebpVp8xThenVp8()
    {
        var header = WebpChunk("VP8X", new byte[] { 0x04, 0, 0, 0, 0x3F, 0, 0, 0x3F, 0, 0 });
        var frame = WebpChunk("VP8 ", Enumerable.Repeat((byte)0x20, 12).ToArray());
        return WebpBody([header, frame], 4 + header.Length + frame.Length);
    }

    private static byte[] WebpVp8xAnimated(int alphaPayloadLength = 0, bool includeFrameImage = true, bool shortFrameHeader = false)
    {
        var header = WebpChunk("VP8X", new byte[] { 0x04, 0, 0, 0, 0x3F, 0, 0, 0x3F, 0, 0 });
        var framePayload = new List<byte>();
        // Sixteen bytes precede the frame's sub-chunks: canvas x, canvas y, tile width, tile height
        // and duration as three-byte little-endian fields, then the disposal/blending/loop flags.
        var frameHeader = shortFrameHeader
            ? new byte[] { 0x3F, 0, 0x3F, 0, 0x3F, 0, 0x3F, 0, 0, 0, 0, 0x00 }
            : AnimatedFrameHeader();
        framePayload.AddRange(frameHeader);
        if (alphaPayloadLength > 0)
            framePayload.AddRange(WebpChunk("ALPH", Enumerable.Repeat((byte)0x01, alphaPayloadLength).ToArray()));
        if (includeFrameImage)
            framePayload.AddRange(WebpChunk("VP8 ", Enumerable.Repeat((byte)0x20, 12).ToArray()));
        var animated = WebpChunk("ANMF", [.. framePayload]);
        return WebpBody([header, animated], 4 + header.Length + animated.Length);
    }

    private static byte[] AnimatedFrameHeader() =>
        new byte[] { 0x3F, 0, 0, 0x3F, 0, 0, 0x1F, 0, 0, 0x1F, 0, 0, 0x0A, 0, 0, 0x00 };
}

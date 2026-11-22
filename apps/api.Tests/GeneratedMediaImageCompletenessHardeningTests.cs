using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Batch 8 carrier hardening for the generated-image completeness gate. These cases cover the
/// halves of the contract that the original provider-boundary lane left unproven: a JPEG whose
/// only FFD9 pair lives inside a metadata segment, fill bytes that legitimately follow EOI, and
/// the WebP length fields that must refuse an over-declared container.
/// </summary>
public sealed class GeneratedMediaImageCompletenessHardeningTests
{
    [Fact]
    public void Jpeg_with_stray_end_of_image_marker_inside_comment_and_no_terminal_marker_is_rejected()
    {
        // SOI, a comment segment that contains an FFD9 pair, a start-of-scan segment, then scan
        // bytes that stop before any real terminator.
        var truncated = Concat(
            new byte[] { 0xFF, 0xD8 },
            Comment(new byte[] { 0x41, 0xFF, 0xD9, 0x42 }),
            StartOfScan(),
            new byte[] { 0x12, 0x34, 0xFF, 0x00, 0x56, 0x78, 0x9A });

        Assert.Throws<FileUploadValidationException>(() => Validate(truncated, ".jpeg", "image/jpeg"));
    }

    [Fact]
    public void Jpeg_with_stray_marker_in_comment_and_terminal_marker_is_accepted()
    {
        var complete = Concat(
            new byte[] { 0xFF, 0xD8 },
            Comment(new byte[] { 0x41, 0xFF, 0xD9, 0x42 }),
            StartOfScan(),
            new byte[] { 0x12, 0x34, 0xFF, 0x00, 0x56, 0x78, 0x9A },
            new byte[] { 0xFF, 0xD9 });

        Validate(complete, ".jpeg", "image/jpeg");
    }

    [Fact]
    public void Jpeg_allows_only_fill_and_padding_bytes_after_the_terminal_marker()
    {
        var withFill = Concat(
            new byte[] { 0xFF, 0xD8 },
            StartOfScan(),
            new byte[] { 0x12, 0xFF, 0x00, 0x34 },
            new byte[] { 0xFF, 0xD9 },
            new byte[] { 0xFF, 0x00, 0xFF });

        Validate(withFill, ".jpg", "image/jpeg");

        var withTrailingGarbage = Concat(
            new byte[] { 0xFF, 0xD8 },
            StartOfScan(),
            new byte[] { 0x12, 0xFF, 0x00, 0x34 },
            new byte[] { 0xFF, 0xD9 },
            new byte[] { 0x41, 0x42 });

        Assert.Throws<FileUploadValidationException>(() => Validate(withTrailingGarbage, ".jpg", "image/jpeg"));
    }

    [Fact]
    public void Jpeg_survives_a_restart_marker_followed_by_more_scan_data()
    {
        var multiScan = Concat(
            new byte[] { 0xFF, 0xD8 },
            StartOfScan(),
            new byte[] { 0x12, 0x34, 0xFF, 0x00, 0x56 },
            new byte[] { 0xFF, 0xD2 },
            new byte[] { 0x78, 0x9A, 0xFF, 0x00, 0xBC },
            new byte[] { 0xFF, 0xD9 });

        Validate(multiScan, ".jpeg", "image/jpeg");
    }

    [Fact]
    public void Jpeg_rejects_a_segment_that_declares_more_bytes_than_the_container_holds()
    {
        // A comment segment whose declared length runs past the end of the buffer: the walk cannot
        // reach a terminator, so a truncated file is refused instead of trusted.
        var lyingSegment = Concat(
            new byte[] { 0xFF, 0xD8 },
            new byte[] { 0xFF, 0xFE, 0x00, 0x40, 0x41, 0xFF, 0xD9, 0x42 },
            new byte[] { 0xFF, 0xD9 });

        Assert.Throws<FileUploadValidationException>(() => Validate(lyingSegment, ".jpeg", "image/jpeg"));
    }

    [Fact]
    public void Webp_rejects_a_chunk_that_overflows_the_declared_riff_length()
    {
        var content = Webp(chunkPayloadLength: 12, declaredChunkLength: 40, declaredRiffLength: 24);

        Assert.Throws<FileUploadValidationException>(() => Validate(content, ".webp", "image/webp"));
    }

    [Fact]
    public void Webp_rejects_a_riff_length_larger_than_the_supplied_bytes()
    {
        var content = Webp(chunkPayloadLength: 12, declaredChunkLength: 12, declaredRiffLength: 400);

        Assert.Throws<FileUploadValidationException>(() => Validate(content, ".webp", "image/webp"));
    }

    [Fact]
    public void Webp_accepts_a_consistently_declared_lossy_chunk()
    {
        var payload = Enumerable.Repeat((byte)0x20, 12).ToArray();
        // 32 bytes total, so the RIFF size field must declare 24 (everything after the 8-byte
        // "RIFF"+size header): 4 for WEBP, 8 for the chunk header, 12 for the payload.
        var content = Webp(chunkPayloadLength: 12, declaredChunkLength: 12, declaredRiffLength: 4 + 8 + 12, payload: payload);

        Validate(content, ".webp", "image/webp");
    }

    private static void Validate(byte[] content, string extension, string contentType) =>
            GeneratedMediaSecurity.ValidateContent(
                GeneratedMediaSecurity.ValidateDescriptor($"generated-clip{extension}", contentType),
                content,
                new Taslim.Api.Files.FileOptions());

    private static byte[] Comment(byte[] payload)
    {
        var length = (ushort)(payload.Length + 2);
        return Concat(new byte[] { 0xFF, 0xFE, (byte)(length >> 8), (byte)(length & 0xFF) }, payload);
    }

    private static byte[] StartOfScan() =>
        Concat(
            new byte[] { 0xFF, 0xDA, 0x00, 0x08 },
            new byte[] { 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 });

    private static byte[] Webp(int chunkPayloadLength, int declaredChunkLength, int declaredRiffLength, byte[]? payload = null)
    {
        payload ??= Enumerable.Repeat((byte)0x20, chunkPayloadLength).ToArray();
        var chunk = Concat(
            System.Text.Encoding.ASCII.GetBytes("VP8 "),
            BitConverter.GetBytes(declaredChunkLength),
            payload);
        var body = Concat(System.Text.Encoding.ASCII.GetBytes("WEBP"), chunk);
        return Concat(System.Text.Encoding.ASCII.GetBytes("RIFF"), BitConverter.GetBytes(declaredRiffLength), body);
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}

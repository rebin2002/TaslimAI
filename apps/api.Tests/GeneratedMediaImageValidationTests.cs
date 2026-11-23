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

    private static byte[] PngSignature() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] CompletePng(uint width, uint height)
    {
        var bytes = new byte[45];
        PngSignature().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        "IEND"u8.CopyTo(bytes.AsSpan(37, 4));
        return bytes;
    }
}

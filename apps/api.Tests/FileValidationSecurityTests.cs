using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class FileValidationSecurityTests
{
    [Fact]
    public async Task Tiny_png_with_extreme_declared_pixels_is_rejected_before_storage()
    {
        var bytes = Png(50_000, 50_000);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "image.png")
        {
            ContentType = "image/png",
        };
        var validator = new FileValidationService(Options.Create(new Taslim.Api.Files.FileOptions()));

        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() => validator.ValidateAsync(file));

        Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bounded_png_header_remains_accepted()
    {
        var bytes = Png(320, 180);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "image.png")
        {
            ContentType = "image/png",
        };
        var validator = new FileValidationService(Options.Create(new Taslim.Api.Files.FileOptions()));

        var result = await validator.ValidateAsync(file);

        Assert.Equal(".png", result.Extension);
        Assert.Equal("image/png", result.ContentType);
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[40];
        Array.Copy(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, bytes, 0, 8);
        Array.Copy(new byte[] { 0, 0, 0, 13 }, 0, bytes, 8, 4);
        Array.Copy("IHDR"u8.ToArray(), 0, bytes, 12, 4);
        bytes[16] = (byte)(width >> 24);
        bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8);
        bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24);
        bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8);
        bytes[23] = (byte)height;
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }
}

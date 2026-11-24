using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class GeneratedMediaSecurityTests
{
    [Fact]
    public void Filename_is_sanitized_and_extension_is_canonicalized()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("../../safe-name.mp3", "audio/mpeg");

        Assert.Equal("safe-name.mp3", descriptor.SafeFileName);
        Assert.Equal(".mp3", descriptor.Extension);
        Assert.Equal("audio/mpeg", descriptor.ContentType);
    }

    [Fact]
    public void MIME_mismatch_is_rejected_before_storage_acceptance()
    {
        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateDescriptor("image.png", "image/jpeg"));
    }

    [Fact]
    public void HTML_cannot_masquerade_as_an_image()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("image.png", "image/png");

        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, Encoding.UTF8.GetBytes("<html>error</html>"), new Taslim.Api.Files.FileOptions()));
    }

    [Fact]
    public async Task Oversized_stream_is_stopped_by_the_bounded_reader()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
        using var bounded = new CountingReadStream(source, 4);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => bounded.CopyToAsync(destination));
        Assert.True(bounded.BytesRead <= 4);
    }

    [Fact]
    public async Task Exact_size_stream_is_hashed_without_buffering_the_payload()
    {
        var content = Encoding.UTF8.GetBytes("streamed generated media");
        using var source = new MemoryStream(content);
        using var bounded = new CountingReadStream(source, content.Length);
        using var destination = new MemoryStream();

        await bounded.CopyToAsync(destination);

        Assert.Equal(content, destination.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), bounded.Sha256Hex);
    }

    [Fact]
    public async Task Exact_length_verification_finalizes_hash_when_s3_stops_at_content_length()
    {
        var bytes = Encoding.UTF8.GetBytes("generated video payload");
        using var source = new MemoryStream(bytes);
        using var bounded = new CountingReadStream(source, maxBytes: 128, expectedContentLength: bytes.Length);
        var requestBytes = new byte[bytes.Length];

        // Simulate an S3 adapter that reads exactly Content-Length, never EOF.
        Assert.Equal(bytes.Length, await bounded.ReadAsync(requestBytes.AsMemory()));
        Assert.Equal(bytes, requestBytes);
        Assert.Null(bounded.Sha256Hex);

        await bounded.VerifyExactLengthAsync(bytes.Length);

        Assert.Equal(bytes.Length, bounded.BytesRead);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), bounded.Sha256Hex);
    }

    [Fact]
    public async Task Exact_length_verification_rejects_trailing_bytes_hidden_by_content_length()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var bounded = new CountingReadStream(source, maxBytes: 10, expectedContentLength: 3);
        var requestBytes = new byte[3];

        Assert.Equal(3, await bounded.ReadAsync(requestBytes.AsMemory()));
        await Assert.ThrowsAsync<FileUploadValidationException>(
            () => bounded.VerifyExactLengthAsync(3));
    }

    [Fact]
    public async Task Exact_length_verification_rejects_short_generated_stream()
    {
        using var source = new MemoryStream([1, 2]);
        using var bounded = new CountingReadStream(source, maxBytes: 10, expectedContentLength: 3);
        using var destination = new MemoryStream();

        await bounded.CopyToAsync(destination);
        await Assert.ThrowsAsync<FileUploadValidationException>(
            () => bounded.VerifyExactLengthAsync(3));
    }

    [Fact]
    public void Image_inspection_derives_container_and_dimensions_from_the_header()
    {
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("frame.png", "image/png");
        var content = new byte[32];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(content, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(content.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(16, 4), 640);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(content.AsSpan(20, 4), 360);

        var inspection = GeneratedMediaInspector.Inspect(descriptor, content, null, "ABCD");

        Assert.Equal("png", inspection.ContainerFormat);
        Assert.Equal(640, inspection.Width);
        Assert.Equal(360, inspection.Height);
        Assert.Equal("ABCD", inspection.ContentHashSha256);
    }

    [Fact]
    public void OpenXml_path_traversal_is_rejected()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", "<Types/>");
            AddEntry(archive, "word/document.xml", "<document/>");
            AddEntry(archive, "../../escape.txt", "do not extract");
        }

        var descriptor = GeneratedMediaSecurity.ValidateDescriptor(
            "report.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        Assert.Throws<FileUploadValidationException>(() =>
            GeneratedMediaSecurity.ValidateContent(descriptor, output.ToArray(), new Taslim.Api.Files.FileOptions()));
    }

    [Theory]
    [InlineData("https://127.0.0.1/media.mp3")]
    [InlineData("https://10.0.0.8/media.mp3")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/media.mp3")]
    public async Task Provider_url_policy_blocks_private_and_metadata_targets(string value)
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri(value)));
    }

    [Fact]
    public async Task Provider_url_policy_requires_https()
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("http://example.com/media.mp3")));
    }

    [Fact]
    public async Task Provider_url_policy_rejects_non_default_https_ports()
    {
        var policy = new ProviderUrlPolicy();

        await Assert.ThrowsAsync<InvalidDataException>(() => policy.EnsureSafeAsync(new Uri("https://example.com:8443/media.mp3")));
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8, 1024, leaveOpen: false);
        writer.Write(content);
    }
}

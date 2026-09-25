using System.IO.Compression;
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

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8, 1024, leaveOpen: false);
        writer.Write(content);
    }
}

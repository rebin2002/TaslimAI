using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Taslim.Api.Files;
using Xunit;
using FileSettings = Taslim.Api.Files.FileOptions;

namespace Taslim.Api.Tests;

public sealed class FileUploadSizeLimitTests
{
    [Fact]
    public async Task Normal_text_file_at_the_limit_is_accepted_and_one_byte_over_is_rejected()
    {
        var maxFileSize = FileSettings.MaxUserUploadFileSizeBytes;
        var content = new byte[checked((int)maxFileSize + 1)];
        Array.Fill(content, (byte)'a');
        var validator = new FileValidationService(Options.Create(new FileSettings()));

        var accepted = CreateTextFile(content, maxFileSize);
        var result = await validator.ValidateAsync(accepted);

        Assert.Equal(maxFileSize, result.SizeBytes);
        Assert.Equal("accepted.txt", result.SafeFileName);

        var oversized = CreateTextFile(content, maxFileSize + 1);
        var exception = await Assert.ThrowsAsync<FileUploadValidationException>(() => validator.ValidateAsync(oversized));

        Assert.Equal("Files must be 25 MB or smaller.", exception.Message);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(26214400, true)]
    [InlineData(26214401, false)]
    public void Configured_maximum_stays_within_the_supported_upload_envelope(long maxFileSizeBytes, bool isValid)
    {
        Assert.Equal(isValid, FileSettings.IsValidMaxFileSizeBytes(maxFileSizeBytes));
    }

    private static FormFile CreateTextFile(byte[] content, long length) => new(new MemoryStream(content, writable: false), 0, length, "file", "accepted.txt")
    {
        Headers = new HeaderDictionary(),
        ContentType = "text/plain",
    };
}

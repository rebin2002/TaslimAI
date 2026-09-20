using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed class FileValidationService(IOptions<FileOptions> options)
{
    private readonly FileOptions settings = options.Value;

    public async Task<FileValidationResult> ValidateAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length <= 0) throw new FileUploadValidationException("The selected file is empty.");
        if (file.Length > settings.MaxFileSizeBytes) throw new FileUploadValidationException($"Files must be {Math.Max(1, settings.MaxFileSizeBytes / (1024 * 1024))} MB or smaller.");

        var safeName = SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (!FileContentTypes.AllowedExtensions.TryGetValue(extension, out var expectedContentType))
            throw new FileUploadValidationException("This file type is not supported.");

        await using var input = file.OpenReadStream();
        var signature = new byte[16];
        var read = await input.ReadAsync(signature.AsMemory(), cancellationToken);
        input.Position = 0;
        if (!IsSafeContent(extension, file.ContentType, expectedContentType, signature[..read], input))
            throw new FileUploadValidationException("The file content does not match its declared type.");

        return new FileValidationResult(safeName, extension, expectedContentType, file.Length);
    }

    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(character => !char.IsControl(character) && !invalid.Contains(character)).ToArray());
        name = name.Replace("..", ".", StringComparison.Ordinal).Trim(' ', '.');
        if (name.Length == 0) name = "uploaded-file";
        return name.Length <= 255 ? name : name[..255];
    }

    private static bool IsSafeContent(string extension, string? suppliedContentType, string expectedContentType, ReadOnlySpan<byte> signature, Stream input)
    {
        var contentTypeMatches = string.IsNullOrWhiteSpace(suppliedContentType)
            || string.Equals(suppliedContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(suppliedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || (FileContentTypes.IsImage(extension) && suppliedContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        if (!contentTypeMatches) return false;

        return extension switch
        {
            ".pdf" => signature.Length >= 5 && signature[..5].SequenceEqual("%PDF-"u8),
            ".jpg" or ".jpeg" => signature.Length >= 3 && signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF,
            ".png" => signature.Length >= 8 && signature[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".webp" => signature.Length >= 12 && signature[..4].SequenceEqual("RIFF"u8) && signature[8..12].SequenceEqual("WEBP"u8),
            ".docx" => IsOpenXmlPackage(input, "word/document.xml"),
            ".xlsx" => IsOpenXmlPackage(input, "xl/workbook.xml"),
            _ => true,
        };
    }

    private static bool IsOpenXmlPackage(Stream input, string requiredEntry)
    {
        try
        {
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            return archive.GetEntry(requiredEntry) is not null && archive.GetEntry("[Content_Types].xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

public sealed class FileUploadValidationException(string message) : Exception(message);

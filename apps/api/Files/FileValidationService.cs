using System.Globalization;
using System.IO.Compression;
using System.Text;
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
        // The base name is resolved without depending on the host. Path.GetFileName treats '\' as a
        // separator only on Windows, so a Windows path survived verbatim inside the Linux API
        // container, and Path.GetInvalidFileNameChars() reports only U+0000 and '/' there. This value
        // becomes StoredFile.OriginalFileName and is echoed into the Content-Disposition download
        // header, so a path prefix, a Windows-reserved character or a Unicode format override
        // (RTL/zero-width) must never reach the browser on any host. These rules mirror the
        // exporter's DocumentFileName, which already documents this exact host-dependency hazard.
        var source = fileName ?? string.Empty;
        var lastSeparator = source.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSeparator >= 0)
        {
            source = source[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (char.IsHighSurrogate(character))
            {
                // Keep a well-formed supplementary character; drop a stray high surrogate that JSON
                // or header serialisation would otherwise re-encode as U+FFFD.
                if (index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]))
                {
                    builder.Append(character).Append(source[index + 1]);
                    index++;
                }
                continue;
            }

            if (char.IsLowSurrogate(character) || IsUnsafeFileNameCharacter(character))
            {
                continue;
            }

            builder.Append(character);
        }

        var name = builder.ToString().Trim();
        name = name.Replace("..", ".", StringComparison.Ordinal).Trim(' ', '.');
        if (name.Length == 0) name = "uploaded-file";
        return TruncateOnCharacterBoundary(name, 255);
    }

    private static bool IsUnsafeFileNameCharacter(char character) => character switch
    {
        // Path separators plus characters a browser, download manager or Windows treats as reserved.
        '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' => true,
        // Any C0/C1 control character, including tab, newline and DEL.
        _ when char.IsControl(character) => true,
        // Line/paragraph separators break line-based header handling.
        _ when character is '\u2028' or '\u2029' => true,
        // Formatting and invisible characters can spoof a base name or an extension.
        _ when char.GetUnicodeCategory(character) == UnicodeCategory.Format => true,
        // Non-characters do not survive UTF-8 encoding.
        '\uFFFE' or '\uFFFF' => true,
        _ => false,
    };

    private static string TruncateOnCharacterBoundary(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Never cut a supplementary character in half: a lone surrogate corrupts the download name.
        var length = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..Math.Max(0, length)];
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

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed class FileValidationService(IOptions<FileOptions> options)
{
    private const int MaxImageDimension = 16_384;
    private const long MaxImagePixels = 25_000_000;
    private const int MaxImageHeaderBytes = 1 * 1024 * 1024;

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
        if (FileContentTypes.IsImage(extension))
            await ValidateImageDimensionsAsync(extension, input, cancellationToken);

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

    private static async Task ValidateImageDimensionsAsync(string extension, Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[MaxImageHeaderBytes];
        var length = 0;
        while (length < header.Length)
        {
            var read = await input.ReadAsync(header.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }

        var dimensions = ReadImageDimensions(extension, header.AsSpan(0, length));
        if (dimensions is null)
            throw new FileUploadValidationException("The image header is invalid or incomplete.");

        var (width, height) = dimensions.Value;
        if (width <= 0 || height <= 0 || width > MaxImageDimension || height > MaxImageDimension || (long)width * height > MaxImagePixels)
            throw new FileUploadValidationException("The image dimensions exceed the configured safety limits.");
    }

    private static (int Width, int Height)? ReadImageDimensions(string extension, ReadOnlySpan<byte> bytes) => extension switch
    {
        ".png" => ReadPngDimensions(bytes),
        ".jpg" or ".jpeg" => ReadJpegDimensions(bytes),
        ".webp" => ReadWebpDimensions(bytes),
        _ => null,
    };

    private static (int Width, int Height)? ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(signature) || !bytes[12..16].SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width > 0 && height > 0 ? (width, height) : null;
    }

    private static (int Width, int Height)? ReadWebpDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WEBP"u8)) return null;
        if (bytes.Length >= 30 && bytes[12..16].SequenceEqual("VP8X"u8))
            return (1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16), 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16));
        if (bytes.Length >= 21 && bytes[12..16].SequenceEqual("VP8L"u8) && bytes[16] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[17..21]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (bytes.Length >= 30 && bytes[12..16].SequenceEqual("VP8 "u8) && bytes[20..23].SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..28]) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..30]) & 0x3FFF;
            return width > 0 && height > 0 ? (width, height) : null;
        }
        return null;
    }

    private static (int Width, int Height)? ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF) return null;
        var index = 2;
        while (index + 3 < bytes.Length)
        {
            if (bytes[index] != 0xFF) { index++; continue; }
            while (index < bytes.Length && bytes[index] == 0xFF) index++;
            if (index >= bytes.Length) break;
            var marker = bytes[index++];
            if (marker is 0x00 or 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7 or 0x01) continue;
            if (index + 2 > bytes.Length) break;
            var segmentLength = (bytes[index] << 8) | bytes[index + 1];
            if (segmentLength < 2 || index + segmentLength > bytes.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segmentLength < 7) return null;
                var height = (bytes[index + 3] << 8) | bytes[index + 4];
                var width = (bytes[index + 5] << 8) | bytes[index + 6];
                return width > 0 && height > 0 ? (width, height) : null;
            }
            index += segmentLength;
        }
        return null;
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

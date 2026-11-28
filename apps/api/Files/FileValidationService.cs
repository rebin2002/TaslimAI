using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
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

    private static async Task ValidateImageDimensionsAsync(string extension, Stream input, CancellationToken cancellationToken)
    {
        var header = new byte[(int)Math.Min(input.Length, MaxImageHeaderBytes)];
        var length = 0;
        while (length < header.Length)
        {
            var read = await input.ReadAsync(header.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }

        var dimensions = ReadImageDimensions(extension, header.AsSpan(0, length), input.Length);
        if (dimensions is null)
            throw new FileUploadValidationException("The image header is invalid or incomplete.");

        var (width, height) = dimensions.Value;
        if (!IsWithinImageLimits(width, height))
            throw new FileUploadValidationException("The image dimensions exceed the configured safety limits.");
    }

    private static (long Width, long Height)? ReadImageDimensions(string extension, ReadOnlySpan<byte> bytes, long fileLength) => extension switch
    {
        ".png" => ReadPngDimensions(bytes),
        ".jpg" or ".jpeg" => ReadJpegDimensions(bytes),
        ".webp" => ReadWebpDimensions(bytes, fileLength),
        _ => null,
    };

    private static (long Width, long Height)? ReadPngDimensions(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 33
            || !bytes[..8].SequenceEqual(signature)
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13
            || !bytes[12..16].SequenceEqual("IHDR"u8))
            return null;

        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        return width > 0 && height > 0 ? ((long)width, (long)height) : null;
    }

    private static (long Width, long Height)? ReadWebpDimensions(ReadOnlySpan<byte> bytes, long fileLength)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("WEBP"u8))
            return null;

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
        var riffEnd = 8UL + riffSize;
        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
        var chunkEnd = 20UL + chunkSize + (chunkSize & 1U);
        if (riffSize < 12 || riffEnd > (ulong)fileLength || chunkEnd > riffEnd)
            return null;

        if (bytes[12..16].SequenceEqual("VP8X"u8))
        {
            if (chunkSize != 10 || bytes.Length < 30) return null;
            var canvas = (
                1L + bytes[24] + ((long)bytes[25] << 8) + ((long)bytes[26] << 16),
                1L + bytes[27] + ((long)bytes[28] << 8) + ((long)bytes[29] << 16));
            if (!IsWithinImageLimits(canvas.Item1, canvas.Item2)) return canvas;
            return ReadWebpExtendedDimensions(bytes, riffEnd, canvas.Item1, canvas.Item2);
        }

        if (bytes[12..16].SequenceEqual("VP8L"u8))
        {
            return ReadWebpCodedFrameDimensions("VP8L"u8, bytes[20..], chunkSize);
        }

        if (bytes[12..16].SequenceEqual("VP8 "u8))
        {
            return ReadWebpCodedFrameDimensions("VP8 "u8, bytes[20..], chunkSize);
        }

        return null;
    }

    private static (long Width, long Height)? ReadWebpExtendedDimensions(
        ReadOnlySpan<byte> bytes,
        ulong riffEnd,
        long canvasWidth,
        long canvasHeight)
    {
        ulong offset = 30;
        var foundFrame = false;
        var hasAnimationFlag = (bytes[20] & 0x02) != 0;
        var foundAnimationFrame = false;

        while (offset < riffEnd)
        {
            if (offset > int.MaxValue || offset > (ulong)bytes.Length || bytes.Length - (int)offset < 8)
                return foundFrame && !hasAnimationFlag ? (canvasWidth, canvasHeight) : null;

            var chunkOffset = (int)offset;
            var chunkType = bytes.Slice(chunkOffset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(chunkOffset + 4)..]);
            var payloadOffset = offset + 8;
            var chunkEnd = payloadOffset + chunkSize;
            var paddedChunkEnd = chunkEnd + (chunkSize & 1U);
            if (paddedChunkEnd > riffEnd) return null;

            if (chunkType.SequenceEqual("VP8 "u8) || chunkType.SequenceEqual("VP8L"u8))
            {
                var dimensions = ReadWebpCodedFrameDimensions(
                    chunkType,
                    bytes[(chunkOffset + 8)..],
                    chunkSize);
                if (!IsCodedFrameWithinBounds(dimensions, canvasWidth, canvasHeight)) return null;
                foundFrame = true;
            }
            else if (chunkType.SequenceEqual("ANMF"u8))
            {
                if (!hasAnimationFlag) return null;
                if (!ReadWebpAnimationFrame(
                        bytes,
                        (int)payloadOffset,
                        chunkSize,
                        canvasWidth,
                        canvasHeight))
                    return null;
                foundFrame = true;
                foundAnimationFrame = true;
            }

            offset = paddedChunkEnd;
        }

        return foundFrame && (!hasAnimationFlag || foundAnimationFrame)
            ? (canvasWidth, canvasHeight)
            : null;
    }

    private static bool ReadWebpAnimationFrame(
        ReadOnlySpan<byte> bytes,
        int payloadOffset,
        uint chunkSize,
        long canvasWidth,
        long canvasHeight)
    {
        if (chunkSize < 16 || payloadOffset > bytes.Length - 16) return false;

        var frameHeader = bytes.Slice(payloadOffset, 16);
        var x = 2L * ReadUInt24LittleEndian(frameHeader);
        var y = 2L * ReadUInt24LittleEndian(frameHeader[3..]);
        var frameWidth = 1L + ReadUInt24LittleEndian(frameHeader[6..]);
        var frameHeight = 1L + ReadUInt24LittleEndian(frameHeader[9..]);
        if (!IsWithinImageLimits(frameWidth, frameHeight)
            || x + frameWidth > canvasWidth
            || y + frameHeight > canvasHeight)
            return false;

        var frameEnd = (ulong)payloadOffset + chunkSize;
        ulong offset = (ulong)payloadOffset + 16;
        var foundCodedFrame = false;
        while (offset < frameEnd)
        {
            if (offset > int.MaxValue || offset > (ulong)bytes.Length || bytes.Length - (int)offset < 8)
                return false;

            var chunkOffset = (int)offset;
            var chunkType = bytes.Slice(chunkOffset, 4);
            var nestedChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(chunkOffset + 4)..]);
            var payloadStart = offset + 8;
            var chunkEnd = payloadStart + nestedChunkSize;
            var paddedChunkEnd = chunkEnd + (nestedChunkSize & 1U);
            if (paddedChunkEnd > frameEnd) return false;

            if (chunkType.SequenceEqual("VP8 "u8) || chunkType.SequenceEqual("VP8L"u8))
            {
                var dimensions = ReadWebpCodedFrameDimensions(
                    chunkType,
                    bytes[(chunkOffset + 8)..],
                    nestedChunkSize);
                if (!IsCodedFrameWithinBounds(dimensions, frameWidth, frameHeight)) return false;
                foundCodedFrame = true;
            }

            offset = paddedChunkEnd;
        }

        return offset == frameEnd && foundCodedFrame;
    }

    private static (long Width, long Height)? ReadWebpCodedFrameDimensions(
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> payload,
        uint chunkSize)
    {
        if (chunkType.SequenceEqual("VP8L"u8))
        {
            if (chunkSize < 5 || payload.Length < 5 || payload[0] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..5]);
            return ((long)(bits & 0x3FFF) + 1, (long)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunkType.SequenceEqual("VP8 "u8))
        {
            ReadOnlySpan<byte> frameSignature = [0x9D, 0x01, 0x2A];
            if (chunkSize < 10 || payload.Length < 10 || !payload[3..6].SequenceEqual(frameSignature)) return null;
            var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]) & 0x3FFF;
            var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..10]) & 0x3FFF;
            return width > 0 && height > 0 ? (width, height) : null;
        }

        return null;
    }

    private static bool IsCodedFrameWithinBounds(
        (long Width, long Height)? dimensions,
        long boundWidth,
        long boundHeight) =>
        dimensions is { } frame
        && IsWithinImageLimits(frame.Width, frame.Height)
        && frame.Width <= boundWidth
        && frame.Height <= boundHeight;

    private static bool IsWithinImageLimits(long width, long height) =>
        width > 0
        && height > 0
        && width <= MaxImageDimension
        && height <= MaxImageDimension
        && width * height <= MaxImagePixels;

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));

    private static (long Width, long Height)? ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;
        var index = 2;
        while (index < bytes.Length)
        {
            if (bytes[index] != 0xFF) return null;
            while (index < bytes.Length && bytes[index] == 0xFF) index++;
            if (index >= bytes.Length) return null;

            var marker = bytes[index++];
            if (marker is 0xD9 or 0xDA or 0x00 or 0xD8) return null;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (index + 2 > bytes.Length) return null;

            var segmentLength = (bytes[index] << 8) | bytes[index + 1];
            if (segmentLength < 2 || index + segmentLength > bytes.Length) return null;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (segmentLength < 11) return null;
                var componentCount = bytes[index + 7];
                if (componentCount == 0 || segmentLength != 8 + 3 * componentCount) return null;

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

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed record GeneratedMediaDescriptor(string SafeFileName, string Extension, string ContentType);

/// <summary>
/// Validates provider output at the storage boundary. Provider-declared names,
/// MIME types, lengths, and bytes are all treated as untrusted input.
/// </summary>
public static class GeneratedMediaSecurity
{
    private static readonly IReadOnlyDictionary<string, string> SupportedGeneratedTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".aac"] = "audio/aac",
            [".csv"] = "text/csv",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".flac"] = "audio/flac",
            [".json"] = "application/json",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".m4a"] = "audio/mp4",
            [".md"] = "text/markdown",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
            [".ogg"] = "audio/ogg",
            [".opus"] = "audio/ogg",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain",
            [".wav"] = "audio/wav",
            [".webm"] = "video/webm",
            [".webp"] = "image/webp",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        };

    public static GeneratedMediaDescriptor ValidateDescriptor(string fileName, string contentType)
    {
        var safeName = FileValidationService.SanitizeFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension) || !SupportedGeneratedTypes.TryGetValue(extension, out var expectedType))
            throw new FileUploadValidationException("Generated file extension is not supported.");

        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 160
            || !string.Equals(contentType.Trim(), expectedType, StringComparison.OrdinalIgnoreCase))
            throw new FileUploadValidationException("Generated file content type does not match its extension.");

        return new GeneratedMediaDescriptor(safeName, extension, expectedType);
    }

    public static void ValidateContent(GeneratedMediaDescriptor descriptor, ReadOnlySpan<byte> content, FileOptions settings)
    {
        if (content.Length <= 0) throw new FileUploadValidationException("Generated file is empty.");
        ValidateHeader(descriptor, content);

        switch (descriptor.Extension)
        {
            case ".json":
                try
                {
                    using var document = JsonDocument.Parse(content.ToArray());
                    if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        throw new JsonException();
                }
                catch (JsonException)
                {
                    throw new FileUploadValidationException("Generated JSON is invalid.");
                }
                break;
            case ".pdf":
                if (!ContainsAscii(content, "%%EOF"))
                    throw new FileUploadValidationException("Generated PDF is truncated or invalid.");
                break;
            case ".docx":
                ValidateOpenXmlPackage(content, "word/document.xml", settings);
                break;
            case ".pptx":
                ValidateOpenXmlPackage(content, "ppt/presentation.xml", settings);
                break;
            case ".xlsx":
                ValidateOpenXmlPackage(content, "xl/workbook.xml", settings);
                break;
            case ".txt":
            case ".md":
            case ".csv":
                try { _ = new UTF8Encoding(false, true).GetString(content); }
                catch (DecoderFallbackException) { throw new FileUploadValidationException("Generated text is not valid UTF-8."); }
                break;
        }
    }

    /// <summary>
    /// Performs the header-only part of validation for one-pass streamed media.
    /// The caller must still enforce the declared length and cleanup on failure.
    /// </summary>
    public static void ValidateHeader(GeneratedMediaDescriptor descriptor, ReadOnlySpan<byte> content)
    {
        var valid = descriptor.Extension switch
        {
            ".png" => content.Length >= 8 && content[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            ".webp" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8),
            ".mp3" => content.Length >= 3 && (content[..3].SequenceEqual("ID3"u8) || IsMpegFrame(content)),
            ".wav" => content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WAVE"u8),
            ".flac" => content.Length >= 4 && content[..4].SequenceEqual("fLaC"u8),
            ".ogg" or ".opus" => content.Length >= 4 && content[..4].SequenceEqual("OggS"u8),
            ".aac" => content.Length >= 2 && content[0] == 0xFF && (content[1] & 0xF6) == 0xF0,
            ".m4a" or ".mp4" => IsFtyp(content),
            ".webm" => content.Length >= 4 && content[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),
            ".pdf" => content.Length >= 5 && content[..5].SequenceEqual("%PDF-"u8),
            ".docx" or ".pptx" or ".xlsx" => content.Length >= 4 && content[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
            ".json" => LooksLikeJson(content),
            ".txt" or ".md" or ".csv" => content.IndexOf((byte)0) < 0,
            _ => false,
        };

        if (!valid) throw new FileUploadValidationException("Generated file content does not match its declared type.");
    }

    public static string? NormalizeMetadataJson(string? metadataJson, int maximumCharacters = 16_000)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        if (metadataJson.Length > maximumCharacters) throw new FileUploadValidationException("Generated file metadata is too large.");
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
                throw new JsonException();
            var normalized = JsonSerializer.Serialize(document.RootElement);
            if (normalized.Length > maximumCharacters) throw new JsonException();
            return normalized;
        }
        catch (JsonException)
        {
            throw new FileUploadValidationException("Generated file metadata is invalid.");
        }
    }

    private static bool IsMpegFrame(ReadOnlySpan<byte> content) =>
        content.Length >= 2 && content[0] == 0xFF && (content[1] & 0xE0) == 0xE0;

    private static bool IsFtyp(ReadOnlySpan<byte> content) =>
        content.Length >= 12 && content[4..8].SequenceEqual("ftyp"u8);

    private static bool LooksLikeJson(ReadOnlySpan<byte> content)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.StartsWith('{') || text.StartsWith('[');
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> content, string value)
    {
        var needle = Encoding.ASCII.GetBytes(value);
        return content.IndexOf(needle) >= 0;
    }

    private static void ValidateOpenXmlPackage(ReadOnlySpan<byte> content, string requiredEntry, FileOptions settings)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(content.ToArray(), writable: false), ZipArchiveMode.Read);
            var maxEntries = Math.Clamp(settings.MaxArchiveEntries, 1, 10_000);
            var maxTotal = Math.Max(1, settings.MaxArchiveUncompressedBytes);
            var maxEntry = Math.Max(1, settings.MaxArchiveEntryBytes);
            var maxRatio = Math.Max(1, settings.MaxArchiveCompressionRatio);
            if (archive.Entries.Count > maxEntries || archive.GetEntry(requiredEntry) is null || archive.GetEntry("[Content_Types].xml") is null)
                throw new InvalidDataException();

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains('\0') || Path.IsPathRooted(entry.FullName)
                    || entry.FullName.Split('/', '\\').Any(part => part == ".."))
                    throw new InvalidDataException();
                if (entry.Length > maxEntry || entry.Length > maxTotal || entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > maxRatio)
                    throw new InvalidDataException();
                total = checked(total + entry.Length);
                if (total > maxTotal) throw new InvalidDataException();

                if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue;
                using var input = entry.Open();
                var buffer = new byte[64 * 1024];
                long readTotal = 0;
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    readTotal += read;
                    if (readTotal > maxEntry || readTotal > maxTotal) throw new InvalidDataException();
                }
            }
        }
        catch (InvalidDataException)
        {
            throw new FileUploadValidationException("Generated document archive is invalid or exceeds safety limits.");
        }
        catch (IOException)
        {
            throw new FileUploadValidationException("Generated document archive could not be read safely.");
        }
        catch (OverflowException)
        {
            throw new FileUploadValidationException("Generated document archive is too large.");
        }
    }
}

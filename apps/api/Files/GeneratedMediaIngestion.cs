using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.Domain;

namespace Taslim.Api.Files;

public sealed record GeneratedMediaInspection(
    string ContainerFormat,
    string? ContentHashSha256,
    int? Width,
    int? Height,
    double? DurationSeconds);

public sealed record GeneratedFileStorageResult(StoredFile File, bool WasCreated);

public sealed record GeneratedMediaRetentionContext(
    Guid WorkspaceId,
    Guid StoredFileId,
    Guid GenerationJobId,
    Guid? AssetId,
    Guid? MovieTakeId,
    DateTime? RetainUntil,
    string Reason);

/// <summary>
/// Extension point for retention/garbage-collection systems. The default implementation
/// is deliberately inert: persistence and cleanup remain safe when no retention service is connected.
/// </summary>
public interface IGeneratedMediaRetentionHook
{
    Task OnStoredAsync(GeneratedMediaRetentionContext context, CancellationToken cancellationToken = default);
    Task OnCleanupAsync(GeneratedMediaRetentionContext context, CancellationToken cancellationToken = default);
}

public sealed class NoopGeneratedMediaRetentionHook : IGeneratedMediaRetentionHook
{
    public Task OnStoredAsync(GeneratedMediaRetentionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task OnCleanupAsync(GeneratedMediaRetentionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class GeneratedMediaInspector
{
    public static GeneratedMediaInspection Inspect(
        GeneratedMediaDescriptor descriptor,
        ReadOnlySpan<byte> prefix,
        string? metadataJson,
        string? contentHashSha256 = null)
    {
        GeneratedMediaSecurity.ValidateHeader(descriptor, prefix);
        var extension = descriptor.Extension.TrimStart('.').ToLowerInvariant();
        var container = extension switch
        {
            "jpg" or "jpeg" => "jpeg",
            "m4a" => "mp4",
            "mp4" => ReadFtypBrand(prefix) ?? "mp4",
            "mov" => ReadFtypBrand(prefix) ?? "quicktime",
            "webm" or "ogg" or "opus" => extension,
            "wav" => "wav",
            "png" => "png",
            "webp" => "webp",
            "mp3" => "mp3",
            "flac" => "flac",
            "aac" => "aac",
            "pdf" => "pdf",
            "docx" or "pptx" or "xlsx" => "zip-openxml",
            _ => extension,
        };

        var (width, height) = descriptor.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? ReadImageDimensions(prefix, descriptor.ContentType)
            : (null, null);
        var duration = ReadDuration(metadataJson);
        return new GeneratedMediaInspection(container, contentHashSha256, width, height, duration);
    }

    public static string MergeMetadata(
        string? normalizedMetadataJson,
        GeneratedMediaInspection inspection,
        int maximumCharacters = 16_000)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(normalizedMetadataJson))
        {
            using var document = JsonDocument.Parse(normalizedMetadataJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    values[property.Name] = property.Value.Clone();
            }
        }

        values["containerFormat"] = JsonSerializer.SerializeToElement(inspection.ContainerFormat);
        if (!string.IsNullOrWhiteSpace(inspection.ContentHashSha256))
            values["contentHashSha256"] = JsonSerializer.SerializeToElement(inspection.ContentHashSha256);
        if (inspection.Width is > 0) values["width"] = JsonSerializer.SerializeToElement(inspection.Width.Value);
        if (inspection.Height is > 0) values["height"] = JsonSerializer.SerializeToElement(inspection.Height.Value);
        if (inspection.DurationSeconds is > 0) values["durationSeconds"] = JsonSerializer.SerializeToElement(inspection.DurationSeconds.Value);

        var normalized = JsonSerializer.Serialize(values);
        if (normalized.Length > maximumCharacters)
            throw new FileUploadValidationException("Generated file metadata is too large.");
        return normalized;
    }

    private static double? ReadDuration(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty("durationSeconds", out var value)
                || !value.TryGetDouble(out var duration)
                || !double.IsFinite(duration)
                || duration <= 0
                || duration > 86_400)
                return null;
            return duration;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int? Width, int? Height) ReadImageDimensions(ReadOnlySpan<byte> bytes, string contentType)
    {
        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            && bytes.Length >= 24
            && bytes[12..16].SequenceEqual("IHDR"u8))
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
            var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
            return width > 0 && height > 0 ? (width, height) : (null, null);
        }
        if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 30)
            return ReadWebpDimensions(bytes);
        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase))
            return ReadJpegDimensions(bytes);
        return (null, null);
    }

    private static (int? Width, int? Height) ReadJpegDimensions(ReadOnlySpan<byte> bytes)
    {
        var index = 2;
        while (index + 3 < bytes.Length)
        {
            if (bytes[index++] != 0xFF) continue;
            while (index < bytes.Length && bytes[index] == 0xFF) index++;
            if (index >= bytes.Length) break;
            var marker = bytes[index++];
            if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7) continue;
            if (index + 2 > bytes.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..(index + 2)]);
            if (length < 2 || index + length > bytes.Length) break;
            if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
            {
                if (length < 7) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..(index + 5)]);
                var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..(index + 7)]);
                return width > 0 && height > 0 ? (width, height) : (null, null);
            }
            index += length;
        }
        return (null, null);
    }

    private static (int? Width, int? Height) ReadWebpDimensions(ReadOnlySpan<byte> bytes)
    {
        var index = 12;
        while (index + 8 <= bytes.Length)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index + 4)..(index + 8)]);
            var dataStart = index + 8;
            if ((ulong)dataStart + chunkSize > (ulong)bytes.Length) break;
            if (bytes[index..(index + 4)].SequenceEqual("VP8X"u8) && chunkSize >= 10)
            {
                var width = 1 + bytes[dataStart + 4] + (bytes[dataStart + 5] << 8) + (bytes[dataStart + 6] << 16);
                var height = 1 + bytes[dataStart + 7] + (bytes[dataStart + 8] << 8) + (bytes[dataStart + 9] << 16);
                return (width, height);
            }
            index = dataStart + (int)chunkSize + (int)(chunkSize % 2);
        }
        return (null, null);
    }

    private static string? ReadFtypBrand(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8)
            ? Encoding.ASCII.GetString(bytes[8..12]).Trim()
            : null;
}

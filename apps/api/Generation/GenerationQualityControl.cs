using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taslim.Api.Assets;
using Taslim.Api.Domain;

namespace Taslim.Api.Generation;

public enum GenerationQualityOutcome
{
    Passed,
    FailedPermanent,
    FailedRetryable,
    Warning,
}

public static class GenerationQualityReasonCodes
{
    public const string NonEmpty = "QUALITY_OUTPUT_EMPTY";
    public const string MimeMissing = "QUALITY_OUTPUT_MIME_MISSING";
    public const string MimeUnrecognized = "QUALITY_OUTPUT_MIME_UNRECOGNIZED";
    public const string MimeRepresentationMismatch = "QUALITY_OUTPUT_MIME_MISMATCH";
    public const string SizeExceeded = "QUALITY_OUTPUT_SIZE_EXCEEDED";
    public const string SizeMismatch = "QUALITY_OUTPUT_SIZE_MISMATCH";
    public const string RepresentationCorrupt = "QUALITY_OUTPUT_REPRESENTATION_CORRUPT";
    public const string RepresentationTruncated = "QUALITY_OUTPUT_REPRESENTATION_TRUNCATED";
    public const string DimensionsMissing = "QUALITY_OUTPUT_DIMENSIONS_MISSING";
    public const string DimensionsInvalid = "QUALITY_OUTPUT_DIMENSIONS_INVALID";
    public const string StructuredInvalid = "QUALITY_OUTPUT_STRUCTURED_INVALID";
}

public sealed class GenerationQualityControlOptions
{
    public long MaxImageBytes { get; set; } = 10 * 1_048_576;
    public long MaxAudioBytes { get; set; } = 25 * 1_048_576;
    public long MaxVideoBytes { get; set; } = 250 * 1_048_576;
    public long MaxDocumentBytes { get; set; } = 25 * 1_048_576;
    public int MaxImageWidth { get; set; } = 20_000;
    public int MaxImageHeight { get; set; } = 20_000;
}

public sealed record GenerationQualityFinding(
    GenerationQualityOutcome Outcome,
    string ReasonCode,
    string Category);

public sealed record GenerationQualityControlResult(
    GenerationQualityOutcome Outcome,
    IReadOnlyList<GenerationQualityFinding> Findings,
    string? ContentType = null,
    long? SizeBytes = null,
    int? Width = null,
    int? Height = null,
    double? DurationSeconds = null)
{
    public bool IsFailure => Outcome is GenerationQualityOutcome.FailedPermanent or GenerationQualityOutcome.FailedRetryable;
    public string? PrimaryReasonCode => Findings.FirstOrDefault()?.ReasonCode;

    public static GenerationQualityControlResult Passed(string? contentType = null, long? sizeBytes = null, int? width = null, int? height = null, double? durationSeconds = null) =>
        new(GenerationQualityOutcome.Passed, [], contentType, sizeBytes, width, height, durationSeconds);
}

public interface IGenerationQualityControl
{
    Task<GenerationQualityControlResult> ValidateAsync(
        GenerationJob job,
        GenerationHandlerOutput output,
        CancellationToken cancellationToken = default);
}

public sealed class GenerationQualityControlException : Exception
{
    public GenerationQualityControlException(GenerationQualityControlResult result)
        : base("Generated output did not pass deterministic quality checks.")
    {
        Result = result;
        Classification = result.Outcome;
        ReasonCode = result.PrimaryReasonCode ?? GenerationQualityReasonCodes.RepresentationCorrupt;
    }

    public GenerationQualityControlResult Result { get; }
    public GenerationQualityOutcome Classification { get; }
    public string ReasonCode { get; }
}

public sealed class GenerationQualityControlService(
    IOptions<GenerationQualityControlOptions> options,
    ILogger<GenerationQualityControlService> logger) : IGenerationQualityControl
{
    private static readonly IReadOnlySet<string> ImageMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif",
    };

    private static readonly IReadOnlySet<string> AudioMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp3", "audio/wav", "audio/x-wav", "audio/ogg", "audio/opus",
        "audio/webm", "audio/flac", "audio/mp4", "audio/aac",
    };

    private static readonly IReadOnlySet<string> VideoMimeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/webm", "video/quicktime", "video/x-msvideo", "video/mpeg",
    };

    private readonly GenerationQualityControlOptions settings = options.Value;

    public async Task<GenerationQualityControlResult> ValidateAsync(
        GenerationJob job,
        GenerationHandlerOutput output,
        CancellationToken cancellationToken = default)
    {
        var contentType = (output.FileArtifact?.ContentType
            ?? output.StreamArtifact?.ContentType)?.Trim().ToLowerInvariant();
        var assetType = output.Asset?.AssetType?.Trim().ToLowerInvariant();

        if (output.FileArtifact is null && output.StreamArtifact is null)
            return GenerationQualityControlResult.Passed(contentType);

        if (string.IsNullOrWhiteSpace(contentType))
            return RecordFailure(job, output, Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeMissing, "mime"));
        if (contentType.Length > 160)
            return RecordFailure(job, output, Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeUnrecognized, "mime"), contentType);

        var kind = ResolveKind(assetType, contentType);
        var result = output.FileArtifact is not null
            ? ValidateBytes(output.FileArtifact.Content.Span, contentType, kind, output.FileArtifact.MetadataJson ?? output.MetadataJson)
            : await ValidateStreamAsync(output.StreamArtifact!, contentType, kind, output.StreamArtifact!.MetadataJson ?? output.MetadataJson, cancellationToken);

        if (result.IsFailure)
            return RecordFailure(job, output, result);
        return result;
    }

    private GenerationQualityControlResult RecordFailure(
        GenerationJob job,
        GenerationHandlerOutput output,
        GenerationQualityControlResult result,
        string? contentType = null)
    {
        var finding = result.Findings.FirstOrDefault();
        logger.LogWarning(
            "Generation output quality check failed. JobId={JobId}; JobType={JobType}; OutputType={OutputType}; Classification={Classification}; ReasonCode={ReasonCode}",
            job.Id,
            job.JobType,
            output.OutputType,
            result.Outcome,
            finding?.ReasonCode ?? GenerationQualityReasonCodes.RepresentationCorrupt);
        return result with { ContentType = contentType ?? result.ContentType };
    }

    private GenerationQualityControlResult ValidateBytes(ReadOnlySpan<byte> bytes, string contentType, MediaKind kind, string? metadataJson)
    {
        var size = (long)bytes.Length;
        var sizeFailure = ValidateSize(size, kind);
        if (sizeFailure is not null) return sizeFailure with { ContentType = contentType, SizeBytes = size };
        if (bytes.Length == 0)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.NonEmpty, "non_empty", contentType, size);

        return kind switch
        {
            MediaKind.Image => ValidateImage(bytes, contentType, size),
            MediaKind.Audio => ValidateAudio(bytes, contentType, size, metadataJson),
            MediaKind.Video => ValidateVideo(bytes, contentType, size, metadataJson),
            _ => ValidateStructuredOrGeneric(bytes, contentType, size),
        };
    }

    private async Task<GenerationQualityControlResult> ValidateStreamAsync(
        GeneratedStreamFileArtifact artifact,
        string contentType,
        MediaKind kind,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        var declaredSize = artifact.SizeBytes;
        var sizeFailure = ValidateSize(declaredSize, kind);
        if (sizeFailure is not null) return sizeFailure with { ContentType = contentType, SizeBytes = declaredSize };
        if (declaredSize <= 0)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.NonEmpty, "non_empty", contentType, declaredSize);

        await using var input = await artifact.OpenReadAsync(cancellationToken);
        if (input is null)
            return Failed(GenerationQualityOutcome.FailedRetryable, GenerationQualityReasonCodes.RepresentationTruncated, "representation", contentType, declaredSize);

        var prefix = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long actualSize = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            actualSize += read;
            if (prefix.Length < 4_096)
            {
                var take = (int)Math.Min(read, 4_096 - prefix.Length);
                prefix.Write(buffer, 0, take);
            }
            var maximum = MaximumBytes(kind);
            if (maximum > 0 && actualSize > maximum)
                return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.SizeExceeded, "size", contentType, actualSize);
        }

        if (actualSize != declaredSize)
            return Failed(GenerationQualityOutcome.FailedRetryable, GenerationQualityReasonCodes.SizeMismatch, "size", contentType, actualSize);

        var sample = prefix.ToArray();
        var signature = kind switch
        {
            MediaKind.Audio => ValidateAudio(sample, contentType, actualSize, metadataJson),
            MediaKind.Video => ValidateVideo(sample, contentType, actualSize, metadataJson),
            MediaKind.Image => ValidateImage(sample, contentType, actualSize),
            _ => ValidateStructuredOrGeneric(sample, contentType, actualSize),
        };
        return signature with { SizeBytes = actualSize };
    }

    private GenerationQualityControlResult ValidateImage(ReadOnlySpan<byte> bytes, string contentType, long size)
    {
        if (!ImageMimeTypes.Contains(contentType))
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeUnrecognized, "mime", contentType, size);
        var image = ImageRepresentation.Read(bytes);
        if (image is null)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.RepresentationCorrupt, "representation", contentType, size);
        if (!string.Equals(image.ContentType, NormalizeImageMime(contentType), StringComparison.OrdinalIgnoreCase))
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeRepresentationMismatch, "mime", contentType, size, image.Width, image.Height);
        if (image.Width <= 0 || image.Height <= 0)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.DimensionsInvalid, "dimensions", contentType, size, image.Width, image.Height);
        if (image.Width > settings.MaxImageWidth || image.Height > settings.MaxImageHeight)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.DimensionsInvalid, "dimensions", contentType, size, image.Width, image.Height);
        return GenerationQualityControlResult.Passed(contentType, size, image.Width, image.Height);
    }

    private GenerationQualityControlResult ValidateAudio(ReadOnlySpan<byte> bytes, string contentType, long size, string? metadataJson)
    {
        if (!AudioMimeTypes.Contains(contentType))
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeUnrecognized, "mime", contentType, size);
        if (!AudioRepresentation.IsRecognized(bytes, contentType))
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.RepresentationCorrupt, "representation", contentType, size);
        var duration = ReadDuration(metadataJson);
        return GenerationQualityControlResult.Passed(contentType, size, durationSeconds: duration);
    }

    private GenerationQualityControlResult ValidateVideo(ReadOnlySpan<byte> bytes, string contentType, long size, string? metadataJson)
    {
        if (!VideoMimeTypes.Contains(contentType))
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.MimeUnrecognized, "mime", contentType, size);
        if (!VideoRepresentation.IsRecognized(bytes, contentType))
            return Failed(GenerationQualityOutcome.FailedRetryable, GenerationQualityReasonCodes.RepresentationTruncated, "representation", contentType, size);
        return GenerationQualityControlResult.Passed(contentType, size, durationSeconds: ReadDuration(metadataJson));
    }

    private static GenerationQualityControlResult ValidateStructuredOrGeneric(ReadOnlySpan<byte> bytes, string contentType, long size)
    {
        if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(bytes.ToArray());
                _ = document.RootElement.ValueKind;
            }
            catch (JsonException)
            {
                return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.StructuredInvalid, "structured", contentType, size);
            }
        }
        return GenerationQualityControlResult.Passed(contentType, size);
    }

    private GenerationQualityControlResult? ValidateSize(long size, MediaKind kind)
    {
        var maximum = MaximumBytes(kind);
        if (size <= 0)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.NonEmpty, "non_empty", sizeBytes: size);
        if (maximum > 0 && size > maximum)
            return Failed(GenerationQualityOutcome.FailedPermanent, GenerationQualityReasonCodes.SizeExceeded, "size", sizeBytes: size);
        return null;
    }

    private long MaximumBytes(MediaKind kind) => kind switch
    {
        MediaKind.Image => settings.MaxImageBytes,
        MediaKind.Audio => settings.MaxAudioBytes,
        MediaKind.Video => settings.MaxVideoBytes,
        _ => settings.MaxDocumentBytes,
    };

    private static MediaKind ResolveKind(string? assetType, string contentType)
    {
        if (string.Equals(assetType, AssetTypes.Image, StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Image;
        if (string.Equals(assetType, AssetTypes.Audio, StringComparison.OrdinalIgnoreCase) || string.Equals(assetType, AssetTypes.Music, StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Audio;
        if (string.Equals(assetType, AssetTypes.Video, StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return MediaKind.Video;
        return MediaKind.Generic;
    }

    private static double? ReadDuration(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!document.RootElement.TryGetProperty("durationSeconds", out var duration)) return null;
            return duration.TryGetDouble(out var value) && value > 0 ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string NormalizeImageMime(string contentType) =>
        string.Equals(contentType, "image/jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : contentType;

    private static GenerationQualityFinding Failure(GenerationQualityOutcome outcome, string reasonCode, string category) =>
        new(outcome, reasonCode, category);

    private static GenerationQualityControlResult Failed(
        GenerationQualityOutcome outcome,
        string reasonCode,
        string category,
        string? contentType = null,
        long? sizeBytes = null,
        int? width = null,
        int? height = null) =>
        new(outcome, [Failure(outcome, reasonCode, category)], contentType, sizeBytes, width, height);

    private enum MediaKind
    {
        Generic,
        Image,
        Audio,
        Video,
    }

    private sealed record ImageRepresentation(string ContentType, int Width, int Height)
    {
        public static ImageRepresentation? Read(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                bytes[12..16].SequenceEqual("IHDR"u8))
            {
                var width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
                var height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
                var hasEnd = bytes.Length >= 12 && bytes[^8..^4].SequenceEqual("IEND"u8);
                return hasEnd && width > 0 && height > 0 ? new("image/png", width, height) : null;
            }

            if (bytes.Length >= 4 && bytes[..2].SequenceEqual(new byte[] { 0xFF, 0xD8 }))
                return ReadJpeg(bytes);

            if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
                return ReadWebp(bytes);

            if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
            {
                var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
                var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
                return width > 0 && height > 0 && bytes.Length >= 13 && bytes[^1] == 0x3B ? new("image/gif", width, height) : null;
            }
            return null;
        }

        private static ImageRepresentation? ReadJpeg(ReadOnlySpan<byte> bytes)
        {
            var index = 2;
            while (index + 1 < bytes.Length)
            {
                if (bytes[index] != 0xFF) { index++; continue; }
                while (index < bytes.Length && bytes[index] == 0xFF) index++;
                if (index >= bytes.Length) return null;
                var marker = bytes[index++];
                if (marker is 0xD8 or 0xD9 or >= 0xD0 and <= 0xD7) continue;
                if (index + 2 > bytes.Length) return null;
                var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..(index + 2)]);
                if (segmentLength < 2 || index + segmentLength > bytes.Length) return null;
                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                {
                    if (segmentLength < 7) return null;
                    var height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..(index + 5)]);
                    var width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..(index + 7)]);
                    var hasEnd = bytes.Length >= 2 && bytes[^2..].SequenceEqual(new byte[] { 0xFF, 0xD9 });
                    return hasEnd && width > 0 && height > 0 ? new("image/jpeg", width, height) : null;
                }
                index += segmentLength;
            }
            return null;
        }

        private static ImageRepresentation? ReadWebp(ReadOnlySpan<byte> bytes)
        {
            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
            if ((ulong)riffSize + 8 > (ulong)bytes.Length) return null;
            var index = 12;
            while (index + 8 <= bytes.Length)
            {
                var chunk = Encoding.ASCII.GetString(bytes[index..(index + 4)]);
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index + 4)..(index + 8)]);
                var dataStart = index + 8;
                if ((ulong)dataStart + chunkSize > (ulong)bytes.Length) return null;
                if (chunk == "VP8X" && chunkSize >= 10)
                {
                    var width = 1 + bytes[dataStart + 4] + (bytes[dataStart + 5] << 8) + (bytes[dataStart + 6] << 16);
                    var height = 1 + bytes[dataStart + 7] + (bytes[dataStart + 8] << 8) + (bytes[dataStart + 9] << 16);
                    return new("image/webp", width, height);
                }
                if (chunk == "VP8L" && chunkSize >= 5 && bytes[dataStart] == 0x2F)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(dataStart + 1)..(dataStart + 5)]);
                    var width = 1 + (int)(bits & 0x3FFF);
                    var height = 1 + (int)((bits >> 14) & 0x3FFF);
                    return new("image/webp", width, height);
                }
                if (chunk == "VP8 " && chunkSize >= 10 && bytes[dataStart..(dataStart + 3)].SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
                {
                    var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(dataStart + 6)..(dataStart + 8)]) & 0x3FFF;
                    var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(dataStart + 8)..(dataStart + 10)]) & 0x3FFF;
                    return new("image/webp", width, height);
                }
                index = dataStart + (int)chunkSize + (int)(chunkSize % 2);
            }
            return null;
        }
    }

    private static class AudioRepresentation
    {
        public static bool IsRecognized(ReadOnlySpan<byte> bytes, string contentType)
        {
            if (contentType is "audio/wav" or "audio/x-wav")
                return bytes.Length >= 44 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WAVE"u8) && (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) + 8 <= (ulong)bytes.Length;
            if (contentType is "audio/flac") return bytes.Length >= 8 && bytes[..4].SequenceEqual("fLaC"u8);
            if (contentType is "audio/ogg" or "audio/opus") return bytes.Length >= 27 && bytes[..4].SequenceEqual("OggS"u8) && bytes[4] == 0;
            if (contentType is "audio/mp4") return IsFtyp(bytes);
            if (contentType is "audio/webm") return bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
            if (contentType is "audio/aac") return HasAdtsFrame(bytes);
            return HasMp3Frame(bytes);
        }

        private static bool HasMp3Frame(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 10 && bytes[..3].SequenceEqual("ID3"u8))
            {
                var size = ((bytes[6] & 0x7F) << 21) | ((bytes[7] & 0x7F) << 14) | ((bytes[8] & 0x7F) << 7) | (bytes[9] & 0x7F);
                return 10 + size < bytes.Length;
            }
            return HasAdtsFrame(bytes) || (bytes.Length >= 4 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 && (bytes[1] & 0x06) != 0);
        }

        private static bool HasAdtsFrame(ReadOnlySpan<byte> bytes) => bytes.Length >= 7 && bytes[0] == 0xFF && (bytes[1] & 0xF6) == 0xF0 && (((bytes[3] & 0x03) << 11) | (bytes[4] << 3) | (bytes[5] >> 5)) <= bytes.Length;

        private static bool IsFtyp(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) >= 16;
    }

    private static class VideoRepresentation
    {
        public static bool IsRecognized(ReadOnlySpan<byte> bytes, string contentType)
        {
            if (contentType == "video/webm") return bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });
            if (contentType == "video/x-msvideo") return bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("AVI "u8);
            if (contentType == "video/mpeg") return bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0, 0, 1, 0xBA });
            return IsFtyp(bytes, "video/quicktime".Equals(contentType, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsFtyp(ReadOnlySpan<byte> bytes, bool quickTime)
        {
            if (bytes.Length < 12 || !bytes[4..8].SequenceEqual("ftyp"u8)) return false;
            var major = Encoding.ASCII.GetString(bytes[8..12]);
            return quickTime
                ? major == "qt  "
                : major is "isom" or "iso2" or "iso5" or "iso6" or "mp41" or "mp42" or "avc1" or "M4V " or "dash";
        }
    }

    private static bool IsFtyp(ReadOnlySpan<byte> bytes) => bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && BinaryPrimitives.ReadUInt32BigEndian(bytes[..4]) >= 16;
}

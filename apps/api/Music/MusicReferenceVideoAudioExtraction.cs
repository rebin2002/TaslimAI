using System.Diagnostics;
using System.Text.Json;

namespace Taslim.Api.Music;

// Scoped, self-contained server-side primitive for issue #708 acceptance criterion 3:
// a future Music Studio reference MP4/MOV must be reduced to its private audio track on the
// server and must never be passed whole to an AI model. This file intentionally wires to
// nothing: no route, DI registration, generation admission, provider capability, billing,
// storage or UI change is introduced here. It exposes one injectable seam
// (IMusicReferenceVideoAudioProcessor) so admission logic can later depend on a mockable
// extraction contract, plus a safe ffmpeg/ffprobe implementation that is not yet registered.
//
// Security posture:
// - Only a caller-authorized private source handle is accepted. The handle can be created
//   from an already-opened stream or from an absolute path proven to live inside a caller
//   supplied authorized private-storage root (path-traversal guarded via full-path resolution
//   and ordinal prefix containment). No URL, workspace id, asset id, or name-controlled file
//   path ever reaches this API.
// - Type (ISO-BMFF video only), declared size, actual size, container, duration and the
//   presence of at least one decodable audio track are all validated before extraction is
//   attempted, so a silent or malformed video never reaches the encoder.
// - Media work runs through a typed argument list (ProcessStartInfo.ArgumentList with
//   UseShellExecute=false); no command string is ever handed to a shell. Temporary file names
//   are server-generated GUIDs, never caller data.
// - Processor failures surface only fixed sanitized codes; raw stderr, temp paths and any
//   nested exception content are dropped so content and secrets cannot leak.
// - The private temp workspace is always removed (finally path) on success, validation
//   failure, processor failure and cancellation.

public static class MusicReferenceVideoAudioExtractionCodes
{
    public const string InvalidSource = "reference_video_source_invalid";
    public const string UnsupportedMediaType = "reference_video_media_type_unsupported";
    public const string InputTooLarge = "reference_video_input_too_large";
    public const string MalformedContainer = "reference_video_container_malformed";
    public const string MissingAudioTrack = "reference_video_audio_track_missing";
    public const string InvalidDuration = "reference_video_duration_invalid";
    public const string InputTooLong = "reference_video_input_too_long";
    public const string ProcessorTimeout = "reference_video_processor_timeout";
    public const string ProcessorFailed = "reference_video_processor_failed";
    public const string OutputTooLarge = "reference_video_output_too_large";
    public const string OutputEmpty = "reference_video_output_empty";
}

/// <summary>Fail-closed extraction failure carrying only a stable public code and a fixed
/// sanitized message; never chains the underlying processor exception.</summary>
public sealed class MusicReferenceVideoAudioExtractionException : Exception
{
    public MusicReferenceVideoAudioExtractionException(string code)
        : base(code switch
        {
            MusicReferenceVideoAudioExtractionCodes.InvalidSource => "The reference video source is not valid.",
            MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType => "Only MP4 and MOV reference videos are supported.",
            MusicReferenceVideoAudioExtractionCodes.InputTooLarge => "The reference video is too large.",
            MusicReferenceVideoAudioExtractionCodes.MalformedContainer => "The reference video could not be read as a valid media container.",
            MusicReferenceVideoAudioExtractionCodes.MissingAudioTrack => "The reference video does not contain an audio track.",
            MusicReferenceVideoAudioExtractionCodes.InvalidDuration => "The reference video duration is invalid.",
            MusicReferenceVideoAudioExtractionCodes.InputTooLong => "The reference video is too long.",
            MusicReferenceVideoAudioExtractionCodes.ProcessorTimeout => "The reference video audio extraction timed out.",
            MusicReferenceVideoAudioExtractionCodes.ProcessorFailed => "The reference video audio extraction failed.",
            MusicReferenceVideoAudioExtractionCodes.OutputTooLarge => "The extracted audio exceeds the allowed size.",
            MusicReferenceVideoAudioExtractionCodes.OutputEmpty => "The extracted audio is empty.",
            _ => "The reference video audio extraction could not be completed."
        })
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>A caller-authorized reference video that already lives in private storage.
/// Deliberately exposes no Url, WorkspaceId, AssetId, original name or host-controllable file
/// path; construction is only possible through the two guarded factories below.</summary>
public sealed class MusicReferenceVideoAudioSource
{
    public const string Mp4MediaType = "video/mp4";
    public const string QuickTimeMediaType = "video/quicktime";

    private readonly Func<Stream> openStream;

    private MusicReferenceVideoAudioSource(Func<Stream> openStream, string mediaType, long declaredSizeBytes)
    {
        this.openStream = openStream;
        MediaContentType = mediaType;
        DeclaredSizeBytes = declaredSizeBytes;
    }

    public string MediaContentType { get; }
    public long DeclaredSizeBytes { get; }

    /// <summary>Wraps a stream the caller has already authorized and opened from private
    /// storage (for example a just-downloaded reference upload). The stream and declared size
    /// must be positive and the media type must be an allowed reference video type.</summary>
    public static MusicReferenceVideoAudioSource FromAuthorizedPrivateStream(Stream content, long declaredSizeBytes, string mediaContentType)
    {
        if (content is null || !content.CanRead)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);
        if (declaredSizeBytes <= 0 || declaredSizeBytes > long.MaxValue)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);
        var normalized = NormalizeMediaType(mediaContentType);
        if (normalized is null)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType);
        return new MusicReferenceVideoAudioSource(() => content, normalized, declaredSizeBytes);
    }

    /// <summary>Opens an absolute file that lives inside the caller-provided authorized
    /// private-storage root. The path is fully resolved and must still be contained in the
    /// root after resolution, so "..\..\" traversal (or any escape) is rejected without the
    /// file ever being opened. The path must additionally carry an allowed video extension.</summary>
    public static MusicReferenceVideoAudioSource FromAuthorizedPrivateFile(string absoluteFilePath, string authorizedRootDirectory, string mediaContentType, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || string.IsNullOrWhiteSpace(authorizedRootDirectory) || !Path.IsPathRooted(absoluteFilePath))
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);
        var normalized = NormalizeMediaType(mediaContentType);
        if (normalized is null)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType);

        var extension = Path.GetExtension(absoluteFilePath);
        if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".mov", StringComparison.OrdinalIgnoreCase))
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.UnsupportedMediaType);

        var resolvedFile = Path.GetFullPath(absoluteFilePath);
        var resolvedRoot = Path.GetFullPath(authorizedRootDirectory);
        if (!resolvedRoot.EndsWith(Path.DirectorySeparatorChar))
            resolvedRoot += Path.DirectorySeparatorChar;
        if (!resolvedFile.StartsWith(resolvedRoot, StringComparison.Ordinal))
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);

        var info = new FileInfo(resolvedFile);
        if (!info.Exists || info.Length <= 0)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);

        return new MusicReferenceVideoAudioSource(() => OpenReadShared(resolvedFile), normalized, info.Length);
    }

    internal Stream OpenStream()
    {
        return openStream() ?? throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidSource);
    }

    internal long CopyTo(Stream target, long hardLimitBytes, ProbeHeaderCapture header, CancellationToken cancellationToken)
    {
        using var source = OpenStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > hardLimitBytes)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InputTooLarge);
            target.Write(buffer, 0, read);
            header.Offer(buffer, read);
        }
        return total;
    }

    /// <summary>Retains only the first fixed number of bytes of the staged file for the
    /// container magic check; media content itself is never buffered beyond the stream.</summary>
    internal sealed class ProbeHeaderCapture
    {
        private readonly byte[] buffer;
        private int filled;

        public ProbeHeaderCapture(int capacity)
        {
            buffer = new byte[capacity];
        }

        public void Offer(byte[] chunk, int length)
        {
            if (filled >= buffer.Length)
                return;
            var take = Math.Min(length, buffer.Length - filled);
            Array.Copy(chunk, 0, buffer, filled, take);
            filled += take;
        }

        public byte[] Snapshot()
        {
            return buffer.AsSpan(0, filled).ToArray();
        }
    }

    private static Stream OpenReadShared(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static string? NormalizeMediaType(string? mediaContentType)
    {
        return mediaContentType?.Trim().ToLowerInvariant() switch
        {
            Mp4MediaType => Mp4MediaType,
            QuickTimeMediaType => QuickTimeMediaType,
            _ => null,
        };
    }
}

/// <summary>Result of a structural probe of a reference video container. Values are
/// media-metadata only; they carry no audio or video content.</summary>
public sealed record MusicReferenceVideoProbeResult(string ContainerName, double DurationSeconds, int DecodableAudioTrackCount);

/// <summary>Immutable, fully validated plan for the audio-only extraction. The command is a
/// typed argument vector, never a shell string; consumers may re-check that no argument is a
/// URL and that video output is disabled before delegating to a processor.</summary>
public sealed record MusicReferenceVideoExtractionPlan(
    string InputFilePath,
    string OutputFilePath,
    IReadOnlyList<string> ArgumentList,
    TimeSpan ProcessTimeout,
    long MaxOutputBytes);

/// <summary>The injectable seam used for media work so admission logic is unit-testable with a
/// fake and no inference, provider or billing dependency is reachable from this primitive.</summary>
public interface IMusicReferenceVideoAudioProcessor
{
    Task<MusicReferenceVideoProbeResult> ProbeAsync(string inputFilePath, CancellationToken cancellationToken);
    Task<int> ExtractAudioAsync(MusicReferenceVideoExtractionPlan plan, CancellationToken cancellationToken);
}

/// <summary>Server-side extraction result: the private audio track as bounded bytes plus the
/// plan metadata that produced it. The temp workspace is already cleaned when this is returned.</summary>
public sealed record MusicReferenceVideoAudioExtractionResult(
    byte[] AudioBytes,
    string AudioContentType,
    TimeSpan SourceDuration,
    MusicReferenceVideoExtractionPlan Plan);

public sealed class MusicReferenceVideoAudioExtractionOptions
{
    public long MaxInputBytes { get; init; } = 200L * 1024 * 1024;
    public long MaxOutputBytes { get; init; } = 64L * 1024 * 1024;
    public double MinDurationSeconds { get; init; } = 0.5;
    public double MaxDurationSeconds { get; init; } = 600;
    public int ProcessTimeoutSeconds { get; init; } = 120;
    public int AudioBitrateKbps { get; init; } = 192;

    internal void Validate()
    {
        if (MaxInputBytes <= 0 || MaxOutputBytes <= 0 || MinDurationSeconds <= 0
            || MaxDurationSeconds < MinDurationSeconds || ProcessTimeoutSeconds <= 0 || AudioBitrateKbps is <= 0 or > 640)
            throw new InvalidOperationException("The reference video audio extraction options are invalid.");
    }
}

/// <summary>Validates an authorized private reference video and extracts only its audio track.
/// Deterministic and side-effect free apart from a private temp workspace that is always
/// removed before returning (success, failure or cancellation).</summary>
public sealed class MusicReferenceVideoAudioExtractor
{
    // ISO base media file format: four size bytes followed by the 'ftyp' box at offset 4.
    // Both MP4 and QuickTime reference videos are required to match before any work.
    private static readonly byte[] FtypMagic = "ftyp"u8.ToArray();
    private static readonly string[] AllowedContainerNames = ["mp4", "mov"];
    private const long ProbeReadBytes = 4096;

    private readonly MusicReferenceVideoAudioExtractionOptions options;
    private readonly IMusicReferenceVideoAudioProcessor processor;

    public MusicReferenceVideoAudioExtractor(MusicReferenceVideoAudioExtractionOptions options, IMusicReferenceVideoAudioProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processor);
        options.Validate();
        this.options = options;
        this.processor = processor;
    }

    public async Task<MusicReferenceVideoAudioExtractionResult> ExtractAudioFromVideoAsync(
        MusicReferenceVideoAudioSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Cheap gate first: a declared size beyond the hard limit never touches the disk.
        if (source.DeclaredSizeBytes > options.MaxInputBytes)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InputTooLarge);

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "taslim-music-reference-audio");
        Directory.CreateDirectory(workspaceRoot);
        var workspace = Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var extension = source.MediaContentType == MusicReferenceVideoAudioSource.QuickTimeMediaType ? ".mov" : ".mp4";
        var inputPath = Path.Combine(workspace, "reference" + extension);
        var outputPath = Path.Combine(workspace, "audio.mp3");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Stage 1: bounded copy of the authorized private source into the temp workspace.
            // Actual byte count is re-checked, so a lying declared size cannot exceed the cap.
            // Also capture the first bytes for the container magic check without buffering the
            // whole file.
            var header = new MusicReferenceVideoAudioSource.ProbeHeaderCapture((int)Math.Min(ProbeReadBytes, Math.Max(16, source.DeclaredSizeBytes)));
            long actualBytes;
            using (var file = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                actualBytes = source.CopyTo(file, options.MaxInputBytes, header, cancellationToken);
            }
            if (actualBytes <= 0)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);

            // Stage 2: structural sanity of the staged file before any processor work.
            if (!IsIsoBmffVideo(header.Snapshot()))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);

            // Stage 3: probe container metadata (mockable seam).
            MusicReferenceVideoProbeResult probe;
            try
            {
                probe = await processor.ProbeAsync(inputPath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Any probe failure is treated as unreadable media; the underlying exception is
                // intentionally dropped so stderr content, paths and secrets never propagate.
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);
            }

            var container = (probe.ContainerName ?? string.Empty).Trim().ToLowerInvariant();
            var containers = container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (containers.Length == 0 || !containers.Any(AllowedContainerNames.Contains))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);
            if (double.IsNaN(probe.DurationSeconds) || double.IsNegative(probe.DurationSeconds) || double.IsInfinity(probe.DurationSeconds))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidDuration);
            if (probe.DurationSeconds < options.MinDurationSeconds)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InvalidDuration);
            if (probe.DurationSeconds > options.MaxDurationSeconds)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.InputTooLong);

            // Stage 4: a reference video without a decodable audio track is rejected outright;
            // it must not be passed anywhere else and extraction must never start.
            if (probe.DecodableAudioTrackCount < 1)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MissingAudioTrack);

            // Stage 5: build the audio-only plan as a typed argument vector. No shell string,
            // no caller-controlled names (workspace file names are server-generated GUIDs).
            var plan = BuildExtractionPlan(inputPath, outputPath);

            // Stage 6: run extraction through the injected processor and enforce output bounds.
            int exitCode;
            try
            {
                exitCode = await processor.ExtractAudioAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MusicReferenceVideoAudioExtractionException)
            {
                throw;
            }
            catch
            {
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);
            }
            if (exitCode != 0)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);

            if (!File.Exists(outputPath))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.OutputEmpty);
            var outputInfo = new FileInfo(outputPath);
            if (outputInfo.Length <= 0)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.OutputEmpty);
            if (outputInfo.Length > plan.MaxOutputBytes)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.OutputTooLarge);

            var audioBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (audioBytes.Length > plan.MaxOutputBytes)
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.OutputTooLarge);

            return new MusicReferenceVideoAudioExtractionResult(audioBytes, "audio/mpeg", TimeSpan.FromSeconds(probe.DurationSeconds), plan);
        }
        finally
        {
            // Guarantee: the private workspace (staged source + any partial output) is removed
            // on success, validation failure, processor failure and cancellation.
            TryDeleteWorkspace(workspace);
        }
    }

    internal MusicReferenceVideoExtractionPlan BuildExtractionPlan(string inputPath, string outputPath)
    {
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-threads", "2",
            "-i", inputPath,
            "-vn",                       // drop video entirely: only the audio track is read
            "-map", "0:a:0",             // first audio stream of the single authorized input
            "-c:a", "libmp3lame",
            "-b:a", options.AudioBitrateKbps.ToString(System.Globalization.CultureInfo.InvariantCulture) + "k",
            "-ar", "44100",
            "-ac", "2",
            "-t", options.MaxDurationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "mp3",
            "-y",
            outputPath,
        };
        foreach (var argument in arguments)
            if (argument.Contains('\0'))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);
        return new MusicReferenceVideoExtractionPlan(
            inputPath,
            outputPath,
            arguments,
            TimeSpan.FromSeconds(Math.Clamp(options.ProcessTimeoutSeconds, 1, 86_400)),
            options.MaxOutputBytes);
    }

    private static bool IsIsoBmffVideo(byte[] header)
    {
        if (header.Length < 12)
            return false;
        // Box size must be sane for the first box and the fourcc must be 'ftyp'.
        var boxSize = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        if (boxSize < 8 || boxSize > header.Length)
            return false;
        return header.AsSpan(4, 4).SequenceEqual(FtypMagic);
    }

    private static void TryDeleteWorkspace(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
        catch
        {
            // Best-effort hygiene only; never leak cleanup failure details to the caller.
        }
    }
}

/// <summary>Default local implementation of the processor seam using ffmpeg/ffprobe with a
/// typed argument list (never shell string execution), bounded stderr capture and a hard
/// process timeout. NOT registered in dependency injection by this change; it exists so the
/// contract above has a real, reviewable execution path for a later wiring slice.</summary>
public sealed class FfmpegMusicReferenceVideoAudioProcessor : IMusicReferenceVideoAudioProcessor
{
    private const int StderrLimitBytes = 16 * 1024;
    private readonly string ffmpegPath;
    private readonly string ffprobePath;

    public FfmpegMusicReferenceVideoAudioProcessor(string ffmpegPath = "ffmpeg", string ffprobePath = "ffprobe")
    {
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        this.ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
    }

    public async Task<MusicReferenceVideoProbeResult> ProbeAsync(string inputFilePath, CancellationToken cancellationToken)
    {
        var arguments = new[]
        {
            "-v", "error",
            "-hide_banner",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "--", inputFilePath,
        };
        var (exitCode, stdout) = await RunAsync(ffprobePath, arguments, readStandardOutput: true, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);
        return ParseProbeJson(stdout);
    }

    public async Task<int> ExtractAudioAsync(MusicReferenceVideoExtractionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var (exitCode, _) = await RunAsync(ffmpegPath, plan.ArgumentList, readStandardOutput: false, plan.ProcessTimeout, cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private static MusicReferenceVideoProbeResult ParseProbeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12, AllowTrailingCommas = false });
            var formatName = string.Empty;
            var durationSeconds = -1d;
            var audioTracks = 0;
            if (document.RootElement.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("format_name", out var name) && name.ValueKind == JsonValueKind.String)
                    formatName = name.GetString() ?? string.Empty;
                if (format.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.String
                    && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    durationSeconds = parsed;
            }
            if (document.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.TryGetProperty("codec_type", out var type) && type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "audio", StringComparison.OrdinalIgnoreCase))
                        audioTracks++;
                    if (durationSeconds < 0 && stream.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.String
                        && double.TryParse(duration.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        durationSeconds = parsed;
                }
            }
            return new MusicReferenceVideoProbeResult(formatName, durationSeconds, audioTracks);
        }
        catch
        {
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.MalformedContainer);
        }
    }

    private static async Task<(int ExitCode, string StandardOutput)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool readStandardOutput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = readStandardOutput,
            RedirectStandardInput = false,
        };
        foreach (var argument in arguments)
        {
            if (argument.Contains('\0'))
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);
        }
        catch (Exception exception) when (exception is not MusicReferenceVideoAudioExtractionException and not OperationCanceledException)
        {
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorFailed);
        }

        // Stderr is drained up to a bounded tail and then intentionally discarded; it is never
        // attached to any exception so file paths and provider-side content cannot leak.
        var errorTask = DrainBoundedAsync(process.StandardError, cancellationToken);
        var outputTask = readStandardOutput
            ? ReadBoundedAsync(process.StandardOutput, StderrLimitBytes * 4, cancellationToken)
            : Task.FromResult(string.Empty);

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            throw new MusicReferenceVideoAudioExtractionException(MusicReferenceVideoAudioExtractionCodes.ProcessorTimeout);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await errorTask.ConfigureAwait(false);
        _ = stderr; // examined only in debug sessions; never propagated
        var stdout = await outputTask.ConfigureAwait(false);
        return (process.ExitCode, stdout);
    }

    private static async Task<string> DrainBoundedAsync(System.IO.StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        try
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (builder.Length < StderrLimitBytes)
                    builder.Append(buffer, 0, Math.Min(read, StderrLimitBytes - builder.Length));
            }
        }
        catch (OperationCanceledException)
        {
        }
        return builder.ToString();
    }

    private static async Task<string> ReadBoundedAsync(System.IO.StreamReader reader, int limitChars, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadBlockAsync(buffer, 0, Math.Min(buffer.Length, limitChars - builder.Length)).ConfigureAwait(false)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length >= limitChars)
                break;
        }
        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}

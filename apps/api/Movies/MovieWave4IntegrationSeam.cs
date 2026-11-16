using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.Movies;

/// <summary>
/// Product vocabulary for the Wave 4 timeline hand-off. These values describe
/// editorial intent only; they do not identify a provider, model, prompt, or
/// execution implementation.
/// </summary>
public static class MovieWave4TrackKinds
{
    public const string Video = "video";
    public const string Voice = "voice";
    public const string SoundEffect = "sfx";
    public const string Music = "music";
    public const string Captions = "captions";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        Video, Voice, SoundEffect, Music, Captions,
    };
}

public static class MovieWave4ExportCodes
{
    public const string ReadyForHandoff = "export_handoff_ready";
    public const string ProjectRequired = "project_required";
    public const string ShotRequired = "shot_required";
    public const string KeyframeRequired = "approved_keyframe_required";
    public const string FinalTakeRequired = "final_take_required";
    public const string VideoSourceRequired = "selected_video_source_required";
    public const string TrackAssetRequired = "track_asset_required";
    public const string CaptionTextRequired = "caption_text_required";
    public const string OutputFormatUnsupported = "output_format_unsupported";
}

public sealed class MovieWave4IntegrationValidationException(string message) : Exception(message);

/// <summary>
/// A deterministic, provider-neutral timeline item. Caption text is the only
/// content field because captions do not require a generated media asset.
/// </summary>
public sealed record MovieWave4TimelineItem(
    string TrackKind,
    Guid? AssetId,
    int StartMilliseconds,
    int DurationMilliseconds,
    Guid? SourceTakeId = null,
    string? CaptionText = null,
    int Order = 0)
{
    public int EndMilliseconds => checked(StartMilliseconds + DurationMilliseconds);
}

public sealed record MovieWave4Timeline(
    IReadOnlyList<MovieWave4TimelineItem> Items,
    int DurationMilliseconds,
    string ProvenanceHash);

public sealed record MovieWave4ExportRequest(
    Guid MovieProjectId,
    Guid MovieShotId,
    Guid SelectedTakeId,
    Guid FinalTakeId,
    Guid ApprovedKeyframeId,
    MovieWave4Timeline Timeline,
    string OutputFormat = "mp4");

public sealed record MovieWave4ExportDecision(
    bool Ready,
    string Code,
    string Message,
    string? HandoffProvenanceHash);

/// <summary>
/// The Wave 4 seam validates the assembled editorial graph and returns a
/// hand-off decision only. It never creates a GenerationJob, publishes an
/// Asset, invokes a provider, or charges usage.
/// </summary>
public static class MovieWave4ExportSeam
{
    private static readonly IReadOnlySet<string> SupportedOutputFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mov", "webm",
    };

    public static MovieWave4Timeline BuildTimeline(
        IEnumerable<MovieWave4TimelineItem> items,
        int durationMilliseconds)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (durationMilliseconds is < 1 or > 3_600_000)
            throw new MovieWave4IntegrationValidationException("Timeline duration must be between 1ms and 3,600,000ms.");

        var ordered = items
            .OrderBy(item => item.StartMilliseconds)
            .ThenBy(item => item.TrackKind, StringComparer.Ordinal)
            .ThenBy(item => item.AssetId)
            .ThenBy(item => item.SourceTakeId)
            .ThenBy(item => item.CaptionText, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .Select((item, index) => item with { Order = item.Order == 0 ? index + 1 : item.Order })
            .ToArray();

        foreach (var item in ordered)
        {
            if (!MovieWave4TrackKinds.Supported.Contains(item.TrackKind))
                throw new MovieWave4IntegrationValidationException($"Unsupported timeline track: {item.TrackKind}.");
            if (item.StartMilliseconds < 0 || item.DurationMilliseconds <= 0 || item.EndMilliseconds > durationMilliseconds)
                throw new MovieWave4IntegrationValidationException("Timeline items must be positive and fit inside the timeline duration.");
            if (item.TrackKind == MovieWave4TrackKinds.Captions)
            {
                if (string.IsNullOrWhiteSpace(item.CaptionText))
                    throw new MovieWave4IntegrationValidationException("Caption items require text.");
            }
            else if (!item.AssetId.HasValue)
            {
                throw new MovieWave4IntegrationValidationException("Media timeline items require an asset reference.");
            }
        }

        foreach (var track in ordered.GroupBy(item => item.TrackKind, StringComparer.Ordinal))
        {
            var previousEnd = -1;
            foreach (var item in track.OrderBy(item => item.StartMilliseconds).ThenBy(item => item.Order))
            {
                if (item.StartMilliseconds < previousEnd)
                    throw new MovieWave4IntegrationValidationException($"Timeline items overlap on the {track.Key} track.");
                previousEnd = item.EndMilliseconds;
            }
        }

        return new MovieWave4Timeline(ordered, durationMilliseconds, ComputeTimelineHash(ordered, durationMilliseconds));
    }

    public static MovieWave4ExportDecision Evaluate(MovieWave4ExportRequest request)
    {
        if (request.MovieProjectId == Guid.Empty)
            return Blocked(MovieWave4ExportCodes.ProjectRequired, "A movie project is required before export hand-off.");
        if (request.MovieShotId == Guid.Empty)
            return Blocked(MovieWave4ExportCodes.ShotRequired, "A movie shot is required before export hand-off.");
        if (request.ApprovedKeyframeId == Guid.Empty)
            return Blocked(MovieWave4ExportCodes.KeyframeRequired, "An approved keyframe is required before export hand-off.");
        if (request.SelectedTakeId == Guid.Empty || request.FinalTakeId == Guid.Empty || request.SelectedTakeId != request.FinalTakeId)
            return Blocked(MovieWave4ExportCodes.FinalTakeRequired, "The selected take must also be the final take before export hand-off.");
        if (!SupportedOutputFormats.Contains(request.OutputFormat.Trim()))
            return Blocked(MovieWave4ExportCodes.OutputFormatUnsupported, "Choose a supported export format.");

        var video = request.Timeline.Items.FirstOrDefault(item =>
            item.TrackKind == MovieWave4TrackKinds.Video
            && item.SourceTakeId == request.SelectedTakeId
            && item.AssetId.HasValue);
        if (video is null)
            return Blocked(MovieWave4ExportCodes.VideoSourceRequired, "The export must reference the selected take's video source.");
        if (request.Timeline.Items.Any(item => item.TrackKind != MovieWave4TrackKinds.Captions && !item.AssetId.HasValue))
            return Blocked(MovieWave4ExportCodes.TrackAssetRequired, "Every non-caption track must reference a media asset.");
        if (request.Timeline.Items.Any(item => item.TrackKind == MovieWave4TrackKinds.Captions && string.IsNullOrWhiteSpace(item.CaptionText)))
            return Blocked(MovieWave4ExportCodes.CaptionTextRequired, "Every caption item must contain text.");

        var handoffHash = ComputeHandoffHash(request);
        return new MovieWave4ExportDecision(true, MovieWave4ExportCodes.ReadyForHandoff, "The assembled timeline is ready for a future export executor.", handoffHash);
    }

    private static MovieWave4ExportDecision Blocked(string code, string message) => new(false, code, message, null);

    private static string ComputeTimelineHash(IEnumerable<MovieWave4TimelineItem> items, int durationMilliseconds)
    {
        var canonical = string.Join("|", new[] { durationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(items.Select(item => string.Join(":", item.TrackKind, item.AssetId?.ToString("N") ?? "-", item.StartMilliseconds, item.DurationMilliseconds, item.SourceTakeId?.ToString("N") ?? "-", item.CaptionText ?? "-", item.Order))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeHandoffHash(MovieWave4ExportRequest request)
    {
        var canonical = string.Join(":", request.MovieProjectId.ToString("N"), request.MovieShotId.ToString("N"), request.SelectedTakeId.ToString("N"), request.FinalTakeId.ToString("N"), request.ApprovedKeyframeId.ToString("N"), request.OutputFormat.Trim().ToLowerInvariant(), request.Timeline.ProvenanceHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Movies;

public static class MovieCaptionTrackTypes
{
    public const string Subtitle = "Subtitle";
    public const string Caption = "Caption";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Subtitle, Caption };
}

public static class MovieCaptionTrackStatuses
{
    public const string Draft = "Draft";
    public const string Ready = "Ready";
    public const string Archived = "Archived";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Draft, Ready, Archived };
}

public static class MovieCaptionFormats
{
    public const string Srt = "srt";
    public const string Vtt = "vtt";
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Srt, Vtt };

    public static string Normalize(string format) => format.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        Srt => Srt,
        Vtt or "webvtt" => Vtt,
        _ => throw new MovieCaptionFormatException($"Unsupported caption format '{format}'.")
    };
}

public sealed class MovieCaptionTrack
{
    public Guid Id { get; set; }
    public Guid MovieProjectId { get; set; }
    public Guid? MovieAssemblyId { get; set; }
    public int Sequence { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TrackType { get; set; } = MovieCaptionTrackTypes.Subtitle;
    public string Language { get; set; } = "en";
    public bool IsRtl { get; set; }
    public bool IsDefault { get; set; }
    public string Status { get; set; } = MovieCaptionTrackStatuses.Draft;
    public string? SourceFormat { get; set; }
    public string? SourceFileName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieProject MovieProject { get; set; } = null!;
    public MovieAssembly? MovieAssembly { get; set; }
    public ICollection<MovieCaptionCue> Cues { get; set; } = [];
}

public sealed class MovieCaptionCue
{
    public Guid Id { get; set; }
    public Guid MovieCaptionTrackId { get; set; }
    public int Sequence { get; set; }
    public long StartMilliseconds { get; set; }
    public long EndMilliseconds { get; set; }
    public string Text { get; set; } = string.Empty;
    public Guid? SpeakerCharacterId { get; set; }
    public string? SpeakerName { get; set; }
    public Guid? MovieSceneId { get; set; }
    public Guid? MovieShotId { get; set; }
    public Guid? MovieTakeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public MovieCaptionTrack MovieCaptionTrack { get; set; } = null!;
    public MovieCharacter? SpeakerCharacter { get; set; }
    public MovieScene? MovieScene { get; set; }
    public MovieShot? MovieShot { get; set; }
    public MovieTake? MovieTake { get; set; }
}

public sealed record MovieCaptionDocument(IReadOnlyList<MovieCaptionDocumentCue> Cues);
public sealed record MovieCaptionDocumentCue(long StartMilliseconds, long EndMilliseconds, string Text, string? SpeakerName = null);

public interface IMovieCaptionFormatAdapter
{
    string Format { get; }
    string ContentType { get; }
    MovieCaptionDocument Parse(string content);
    string Serialize(MovieCaptionDocument document);
}

public sealed class MovieCaptionFormatException(string message) : Exception(message);
public sealed class MovieCaptionValidationException(string message) : Exception(message);

public static class MovieCaptionTimecode
{
    private static readonly Regex Pattern = new("^(?<hours>\\d{1,4}):(?<minutes>\\d{2}):(?<seconds>\\d{2})(?<separator>[,.])(?<milliseconds>\\d{3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static long Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new MovieCaptionFormatException("A caption timecode is required.");
        var match = Pattern.Match(value.Trim());
        if (!match.Success || !int.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(match.Groups["seconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(match.Groups["milliseconds"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            || minutes > 59 || seconds > 59)
            throw new MovieCaptionFormatException($"Invalid caption timecode '{value}'. Use HH:MM:SS,mmm or HH:MM:SS.mmm.");
        return checked((((long)hours * 60 + minutes) * 60 + seconds) * 1_000 + milliseconds);
    }

    public static string FormatSrt(long milliseconds) => Format(milliseconds, ',');
    public static string FormatVtt(long milliseconds) => Format(milliseconds, '.');

    private static string Format(long milliseconds, char separator)
    {
        if (milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        var span = TimeSpan.FromMilliseconds(milliseconds);
        var hours = (long)span.TotalHours;
        return $"{hours:00}:{span.Minutes:00}:{span.Seconds:00}{separator}{span.Milliseconds:000}";
    }
}

public sealed class SrtMovieCaptionFormatAdapter : IMovieCaptionFormatAdapter
{
    public string Format => MovieCaptionFormats.Srt;
    public string ContentType => "application/x-subrip";

    public MovieCaptionDocument Parse(string content)
    {
        var blocks = Normalize(content).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cues = new List<MovieCaptionDocumentCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timecodeIndex = Array.FindIndex(lines, line => line.Contains(" --> ", StringComparison.Ordinal));
            if (timecodeIndex < 0) throw new MovieCaptionFormatException("Each SRT cue must contain a start and end timecode.");
            var timecodes = lines[timecodeIndex].Split(" --> ", 2, StringSplitOptions.None);
            var start = MovieCaptionTimecode.Parse(timecodes[0].Trim());
            var end = MovieCaptionTimecode.Parse(timecodes[1].Split(' ', 2)[0].Trim());
            if (end <= start) throw new MovieCaptionFormatException("Caption end time must be greater than its start time.");
            var text = string.Join("\n", lines[(timecodeIndex + 1)..]).Trim();
            cues.Add(new MovieCaptionDocumentCue(start, end, text));
        }
        return new MovieCaptionDocument(cues);
    }

    public string Serialize(MovieCaptionDocument document)
    {
        var builder = new StringBuilder();
        var ordered = document.Cues.OrderBy(cue => cue.StartMilliseconds).ThenBy(cue => cue.EndMilliseconds).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var cue = ordered[index];
            builder.Append(index + 1).Append("\r\n")
                .Append(MovieCaptionTimecode.FormatSrt(cue.StartMilliseconds)).Append(" --> ")
                .Append(MovieCaptionTimecode.FormatSrt(cue.EndMilliseconds)).Append("\r\n")
                .Append(cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))
                .Append("\r\n\r\n");
        }
        return builder.ToString().TrimEnd('\r', '\n') + (ordered.Length == 0 ? string.Empty : "\r\n");
    }

    private static string Normalize(string content) => (content ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}

public sealed class WebVttMovieCaptionFormatAdapter : IMovieCaptionFormatAdapter
{
    public string Format => MovieCaptionFormats.Vtt;
    public string ContentType => "text/vtt";

    public MovieCaptionDocument Parse(string content)
    {
        var lines = (content ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            throw new MovieCaptionFormatException("A WebVTT document must start with WEBVTT.");
        var cues = new List<MovieCaptionDocumentCue>();
        for (var index = 1; index < lines.Length;)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index >= lines.Length) break;
            if (lines[index].StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) || lines[index].StartsWith("STYLE", StringComparison.OrdinalIgnoreCase) || lines[index].StartsWith("REGION", StringComparison.OrdinalIgnoreCase))
            {
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) index++;
                continue;
            }
            if (!lines[index].Contains(" --> ", StringComparison.Ordinal)) index++;
            if (index >= lines.Length) break;
            var timecodes = lines[index++].Split(" --> ", 2, StringSplitOptions.None);
            var start = MovieCaptionTimecode.Parse(timecodes[0].Trim());
            var end = MovieCaptionTimecode.Parse(timecodes[1].Split(' ', 2)[0].Trim());
            if (end <= start) throw new MovieCaptionFormatException("Caption end time must be greater than its start time.");
            var textLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) textLines.Add(lines[index++]);
            var text = string.Join("\n", textLines).Trim();
            string? speakerName = null;
            var voice = Regex.Match(text, "^<v\\s+([^>]+)>(.*)</v>$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (voice.Success)
            {
                speakerName = WebUtility.HtmlDecode(voice.Groups[1].Value.Trim());
                text = WebUtility.HtmlDecode(voice.Groups[2].Value);
            }
            else text = WebUtility.HtmlDecode(text);
            cues.Add(new MovieCaptionDocumentCue(start, end, text, speakerName));
        }
        return new MovieCaptionDocument(cues);
    }

    public string Serialize(MovieCaptionDocument document)
    {
        var builder = new StringBuilder("WEBVTT\r\n\r\n");
        var ordered = document.Cues.OrderBy(cue => cue.StartMilliseconds).ThenBy(cue => cue.EndMilliseconds).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var cue = ordered[index];
            builder.Append(index + 1).Append("\r\n")
                .Append(MovieCaptionTimecode.FormatVtt(cue.StartMilliseconds)).Append(" --> ")
                .Append(MovieCaptionTimecode.FormatVtt(cue.EndMilliseconds)).Append("\r\n");
            var text = cue.Text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            builder.Append(cue.SpeakerName is null ? text : $"<v {WebUtility.HtmlEncode(cue.SpeakerName)}>{text}</v>")
                .Append("\r\n\r\n");
        }
        return builder.ToString();
    }
}

public sealed record MovieCaptionExport(string Format, string ContentType, string FileName, string Content);

public interface IMovieCaptionService
{
    Task<IReadOnlyList<MovieCaptionTrackDto>?> GetTracksAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieCaptionTrackDto?> GetTrackAsync(Guid userId, Guid trackId, CancellationToken cancellationToken);
    Task<MovieCaptionTimelineDto?> GetTimelineAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken);
    Task<MovieCaptionTrackDto?> CreateTrackAsync(Guid userId, Guid movieProjectId, MovieCaptionTrackRequest request, CancellationToken cancellationToken);
    Task<MovieCaptionCueDto?> AddCueAsync(Guid userId, Guid trackId, MovieCaptionCueRequest request, CancellationToken cancellationToken);
    Task<MovieCaptionCueDto?> UpdateCueAsync(Guid userId, Guid cueId, MovieCaptionCueRequest request, CancellationToken cancellationToken);
    Task DeleteCueAsync(Guid userId, Guid cueId, CancellationToken cancellationToken);
    Task<MovieCaptionTrackDto?> ImportAsync(Guid userId, Guid movieProjectId, MovieCaptionImportRequest request, CancellationToken cancellationToken);
    Task<MovieCaptionExport?> ExportAsync(Guid userId, Guid trackId, string format, CancellationToken cancellationToken);
}

public sealed class MovieCaptionService(
    TaslimDbContext db,
    MovieCollaborationAccess collaboration,
    IEnumerable<IMovieCaptionFormatAdapter> adapters) : IMovieCaptionService
{
    private const int MaxTrackNameLength = 160;
    private const int MaxCueTextLength = 10_000;
    private const int MaxCueCount = 50_000;
    private const int MaxImportLength = 2_000_000;

    public async Task<IReadOnlyList<MovieCaptionTrackDto>?> GetTracksAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var tracks = await TrackQuery().Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
        return tracks.Select(ToDto).ToArray();
    }

    public async Task<MovieCaptionTrackDto?> GetTrackAsync(Guid userId, Guid trackId, CancellationToken cancellationToken)
    {
        var track = await TrackQuery().FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        return track is null || !await collaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.View, cancellationToken) ? null : ToDto(track);
    }

    public async Task<MovieCaptionTimelineDto?> GetTimelineAsync(Guid userId, Guid movieProjectId, CancellationToken cancellationToken)
    {
        var tracks = await TrackQuery().Where(item => item.MovieProjectId == movieProjectId).OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
        if (tracks.Count == 0 && !await db.MovieProjects.AsNoTracking().AnyAsync(item => item.Id == movieProjectId, cancellationToken)) return null;
        if (!await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var cues = tracks.SelectMany(track => track.Cues.OrderBy(cue => cue.StartMilliseconds).ThenBy(cue => cue.Sequence).Select(cue => new MovieCaptionTimelineCueDto(
            track.Id, track.Name, track.Language, track.IsRtl, track.TrackType, ToCueDto(cue)))).OrderBy(item => item.Cue.StartMilliseconds).ThenBy(item => item.TrackId).ToArray();
        return new MovieCaptionTimelineDto(movieProjectId, tracks.Count, cues);
    }

    public async Task<MovieCaptionTrackDto?> CreateTrackAsync(Guid userId, Guid movieProjectId, MovieCaptionTrackRequest request, CancellationToken cancellationToken)
    {
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateTrackRequest(request);
        if (request.MovieAssemblyId.HasValue && !await db.MovieAssemblies.AnyAsync(item => item.Id == request.MovieAssemblyId && item.MovieProjectId == movieProjectId, cancellationToken))
            throw new MovieCaptionValidationException("The selected assembly does not belong to this movie project.");
        var now = DateTime.UtcNow;
        var track = new MovieCaptionTrack
        {
            Id = Guid.NewGuid(), MovieProjectId = movieProjectId, MovieAssemblyId = request.MovieAssemblyId,
            Sequence = await NextSequenceAsync(movieProjectId, cancellationToken), Name = request.Name.Trim(),
            TrackType = request.TrackType.Trim(), Language = NormalizeLanguage(request.Language), IsRtl = request.IsRtl,
            IsDefault = request.IsDefault, Status = request.Status.Trim(), SourceFormat = request.SourceFormat is null ? null : MovieCaptionFormats.Normalize(request.SourceFormat),
            SourceFileName = Clean(request.SourceFileName), CreatedAt = now, UpdatedAt = now,
        };
        if (track.IsDefault)
        {
            var defaults = await db.MovieCaptionTracks.Where(item => item.MovieProjectId == movieProjectId && item.Language == track.Language && item.IsDefault).ToListAsync(cancellationToken);
            foreach (var item in defaults) item.IsDefault = false;
        }
        db.MovieCaptionTracks.Add(track);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(track);
    }

    public async Task<MovieCaptionCueDto?> AddCueAsync(Guid userId, Guid trackId, MovieCaptionCueRequest request, CancellationToken cancellationToken)
    {
        var track = await TrackQuery().FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null || !await collaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var cue = await BuildCueAsync(track, request, null, cancellationToken);
        db.MovieCaptionCues.Add(cue);
        track.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return ToCueDto(cue);
    }

    public async Task<MovieCaptionCueDto?> UpdateCueAsync(Guid userId, Guid cueId, MovieCaptionCueRequest request, CancellationToken cancellationToken)
    {
        var cue = await db.MovieCaptionCues.Include(item => item.MovieCaptionTrack).FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        if (cue is null || !await collaboration.HasPermissionAsync(userId, cue.MovieCaptionTrack.MovieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        var replacement = await BuildCueAsync(cue.MovieCaptionTrack, request, cue.Id, cancellationToken);
        cue.Sequence = replacement.Sequence; cue.StartMilliseconds = replacement.StartMilliseconds; cue.EndMilliseconds = replacement.EndMilliseconds;
        cue.Text = replacement.Text; cue.SpeakerCharacterId = replacement.SpeakerCharacterId; cue.SpeakerName = replacement.SpeakerName;
        cue.MovieSceneId = replacement.MovieSceneId; cue.MovieShotId = replacement.MovieShotId; cue.MovieTakeId = replacement.MovieTakeId; cue.UpdatedAt = DateTime.UtcNow;
        cue.MovieCaptionTrack.UpdatedAt = cue.UpdatedAt;
        await db.SaveChangesAsync(cancellationToken);
        var savedCue = await db.MovieCaptionCues.AsNoTracking().Include(item => item.SpeakerCharacter).FirstAsync(item => item.Id == cue.Id, cancellationToken);
        return ToCueDto(savedCue);
    }

    public async Task DeleteCueAsync(Guid userId, Guid cueId, CancellationToken cancellationToken)
    {
        var cue = await db.MovieCaptionCues.Include(item => item.MovieCaptionTrack).FirstOrDefaultAsync(item => item.Id == cueId, cancellationToken);
        if (cue is null) throw new MovieCaptionValidationException("Caption cue was not found.");
        await collaboration.RequireAsync(userId, cue.MovieCaptionTrack.MovieProjectId, MoviePermissions.Edit, cancellationToken);
        db.MovieCaptionCues.Remove(cue);
        cue.MovieCaptionTrack.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MovieCaptionTrackDto?> ImportAsync(Guid userId, Guid movieProjectId, MovieCaptionImportRequest request, CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Content.Length > MaxImportLength) throw new MovieCaptionValidationException("Caption import content is required and must be 2,000,000 characters or fewer.");
        var format = MovieCaptionFormats.Normalize(request.Format);
        var adapter = FindAdapter(format);
        MovieCaptionDocument document;
        try { document = adapter.Parse(request.Content); }
        catch (MovieCaptionFormatException) { throw; }
        catch (Exception exception) { throw new MovieCaptionFormatException($"The {format.ToUpperInvariant()} document could not be parsed: {exception.Message}"); }
        if (document.Cues.Count > MaxCueCount) throw new MovieCaptionValidationException($"A caption track cannot contain more than {MaxCueCount:N0} cues.");
        var project = await db.MovieProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == movieProjectId, cancellationToken);
        if (project is null || !await collaboration.HasPermissionAsync(userId, movieProjectId, MoviePermissions.Edit, cancellationToken)) return null;
        ValidateDocument(document, project.DurationSeconds);
        var track = await CreateTrackAsync(userId, movieProjectId, new MovieCaptionTrackRequest
        {
            Name = request.Name, Language = request.Language, IsRtl = request.IsRtl, TrackType = request.TrackType,
            MovieAssemblyId = request.MovieAssemblyId, IsDefault = request.IsDefault, Status = request.Status,
            SourceFormat = format, SourceFileName = request.SourceFileName,
        }, cancellationToken);
        if (track is null) return null;
        var entity = await db.MovieCaptionTracks.FirstAsync(item => item.Id == track.Id, cancellationToken);
        var now = DateTime.UtcNow;
        var importedCues = document.Cues.Select((cue, index) => new MovieCaptionCue
        {
            Id = Guid.NewGuid(), MovieCaptionTrackId = entity.Id, Sequence = index + 1, StartMilliseconds = cue.StartMilliseconds,
            EndMilliseconds = cue.EndMilliseconds, Text = cue.Text.Trim(), SpeakerName = Clean(cue.SpeakerName), CreatedAt = now, UpdatedAt = now,
        }).ToList();
        db.MovieCaptionCues.AddRange(importedCues);
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return (await GetTrackAsync(userId, entity.Id, cancellationToken))!;
    }

    public async Task<MovieCaptionExport?> ExportAsync(Guid userId, Guid trackId, string format, CancellationToken cancellationToken)
    {
        var normalized = MovieCaptionFormats.Normalize(format);
        var track = await TrackQuery().FirstOrDefaultAsync(item => item.Id == trackId, cancellationToken);
        if (track is null || !await collaboration.HasPermissionAsync(userId, track.MovieProjectId, MoviePermissions.View, cancellationToken)) return null;
        var adapter = FindAdapter(normalized);
        var document = new MovieCaptionDocument(track.Cues.OrderBy(item => item.StartMilliseconds).ThenBy(item => item.Sequence).Select(item => new MovieCaptionDocumentCue(item.StartMilliseconds, item.EndMilliseconds, item.Text, item.SpeakerName ?? item.SpeakerCharacter?.Name)).ToArray());
        var safeName = Regex.Replace(track.Name.Trim(), "[^a-zA-Z0-9_-]+", "-", RegexOptions.CultureInvariant).Trim('-');
        if (safeName.Length == 0) safeName = "captions";
        return new MovieCaptionExport(normalized, adapter.ContentType, $"{safeName}.{normalized}", adapter.Serialize(document));
    }

    private IQueryable<MovieCaptionTrack> TrackQuery() => db.MovieCaptionTracks
        .Include(item => item.Cues).ThenInclude(item => item.SpeakerCharacter);

    private async Task<int> NextSequenceAsync(Guid projectId, CancellationToken cancellationToken) => (await db.MovieCaptionTracks.Where(item => item.MovieProjectId == projectId).MaxAsync(item => (int?)item.Sequence, cancellationToken) ?? 0) + 1;

    private async Task<MovieCaptionCue> BuildCueAsync(MovieCaptionTrack track, MovieCaptionCueRequest request, Guid? existingCueId, CancellationToken cancellationToken)
    {
        if (request.Sequence < 1) throw new MovieCaptionValidationException("Cue sequence must be 1 or greater.");
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > MaxCueTextLength) throw new MovieCaptionValidationException($"Cue text is required and must be {MaxCueTextLength:N0} characters or fewer.");
        var start = MovieCaptionTimecode.Parse(request.StartTimecode);
        var end = MovieCaptionTimecode.Parse(request.EndTimecode);
        ValidateTimeRange(start, end);
        var durationSeconds = await db.MovieProjects.AsNoTracking().Where(item => item.Id == track.MovieProjectId).Select(item => item.DurationSeconds).FirstOrDefaultAsync(cancellationToken);
        if (durationSeconds > 0 && end > (long)durationSeconds * 1_000) throw new MovieCaptionValidationException("Caption cues cannot extend beyond the movie duration.");
        if (await db.MovieCaptionCues.AnyAsync(item => item.MovieCaptionTrackId == track.Id && item.Id != existingCueId && item.Sequence == request.Sequence, cancellationToken))
            throw new MovieCaptionValidationException("Cue sequence must be unique within a track.");
        if (await db.MovieCaptionCues.AnyAsync(item => item.MovieCaptionTrackId == track.Id && item.Id != existingCueId && item.StartMilliseconds < end && start < item.EndMilliseconds, cancellationToken))
            throw new MovieCaptionValidationException("Caption cues on the same track cannot overlap.");
        await ValidateTimelineLinksAsync(track.MovieProjectId, request, cancellationToken);
        return new MovieCaptionCue
        {
            Id = existingCueId ?? Guid.NewGuid(), MovieCaptionTrackId = track.Id, Sequence = request.Sequence, StartMilliseconds = start,
            EndMilliseconds = end, Text = request.Text.Trim(), SpeakerCharacterId = request.SpeakerCharacterId, SpeakerName = Clean(request.SpeakerName),
            MovieSceneId = request.MovieSceneId, MovieShotId = request.MovieShotId, MovieTakeId = request.MovieTakeId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
    }

    private async Task ValidateTimelineLinksAsync(Guid projectId, MovieCaptionCueRequest request, CancellationToken cancellationToken)
    {
        if (request.MovieSceneId.HasValue && !await db.MovieScenes.AnyAsync(item => item.Id == request.MovieSceneId && item.MovieProjectId == projectId, cancellationToken)) throw new MovieCaptionValidationException("The selected scene does not belong to this movie project.");
        if (request.MovieShotId.HasValue && !await db.MovieShots.AnyAsync(item => item.Id == request.MovieShotId && item.Scene.MovieProjectId == projectId && (!request.MovieSceneId.HasValue || item.MovieSceneId == request.MovieSceneId), cancellationToken)) throw new MovieCaptionValidationException("The selected shot does not belong to the selected movie timeline.");
        if (request.MovieTakeId.HasValue && !await db.MovieTakes.AnyAsync(item => item.Id == request.MovieTakeId && item.MovieShot.Scene.MovieProjectId == projectId && (!request.MovieShotId.HasValue || item.MovieShotId == request.MovieShotId), cancellationToken)) throw new MovieCaptionValidationException("The selected take does not belong to the selected movie timeline.");
        if (request.SpeakerCharacterId.HasValue && !await db.MovieCharacters.AnyAsync(item => item.Id == request.SpeakerCharacterId && item.MovieProjectId == projectId, cancellationToken)) throw new MovieCaptionValidationException("The selected speaker does not belong to this movie project.");
    }

    private static void ValidateTrackRequest(MovieCaptionTrackRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MaxTrackNameLength) throw new MovieCaptionValidationException($"Track name is required and must be {MaxTrackNameLength} characters or fewer.");
        if (!MovieCaptionTrackTypes.Supported.Contains(request.TrackType.Trim())) throw new MovieCaptionValidationException("Track type must be Subtitle or Caption.");
        if (!MovieCaptionTrackStatuses.Supported.Contains(request.Status.Trim())) throw new MovieCaptionValidationException("Track status is not supported.");
        _ = NormalizeLanguage(request.Language);
        if (request.SourceFileName?.Length > 255) throw new MovieCaptionValidationException("Source file name must be 255 characters or fewer.");
        if (request.SourceFormat is not null) _ = MovieCaptionFormats.Normalize(request.SourceFormat);
    }

    private static void ValidateDocument(MovieCaptionDocument document, int durationSeconds = 0)
    {
        if (document.Cues.Count > MaxCueCount) throw new MovieCaptionValidationException($"A caption track cannot contain more than {MaxCueCount:N0} cues.");
        var previousEnd = -1L;
        foreach (var cue in document.Cues.OrderBy(item => item.StartMilliseconds))
        {
            ValidateTimeRange(cue.StartMilliseconds, cue.EndMilliseconds);
            if (durationSeconds > 0 && cue.EndMilliseconds > (long)durationSeconds * 1_000) throw new MovieCaptionValidationException("Caption cues cannot extend beyond the movie duration.");
            if (cue.StartMilliseconds < previousEnd) throw new MovieCaptionValidationException("Caption cues on the same track cannot overlap.");
            if (string.IsNullOrWhiteSpace(cue.Text) || cue.Text.Trim().Length > MaxCueTextLength) throw new MovieCaptionValidationException($"Cue text is required and must be {MaxCueTextLength:N0} characters or fewer.");
            previousEnd = cue.EndMilliseconds;
        }
    }

    private static void ValidateTimeRange(long start, long end)
    {
        if (start < 0 || end < 0 || end <= start) throw new MovieCaptionValidationException("Caption end time must be greater than its start time.");
    }

    private IMovieCaptionFormatAdapter FindAdapter(string format) => adapters.FirstOrDefault(item => string.Equals(item.Format, format, StringComparison.OrdinalIgnoreCase)) ?? throw new MovieCaptionFormatException($"No caption adapter is registered for '{format}'.");
    private static string NormalizeLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new MovieCaptionValidationException("Caption language is required.");
        var parts = value.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length is < 2 or > 8 || parts.Any(part => part.Length is < 1 or > 8 || !part.All(char.IsLetterOrDigit))) throw new MovieCaptionValidationException("Caption language must be a valid BCP-47 language tag.");
        return string.Join('-', parts.Select((part, index) => index == 0 ? part.ToLowerInvariant() : part.Length == 2 && part.All(char.IsLetter) ? part.ToUpperInvariant() : part));
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static MovieCaptionTrackDto ToDto(MovieCaptionTrack track) => new(track.Id, track.MovieProjectId, track.MovieAssemblyId, track.Sequence, track.Name, track.TrackType, track.Language, track.IsRtl, track.IsDefault, track.Status, track.SourceFormat, track.SourceFileName, track.CreatedAt, track.UpdatedAt, track.Cues.OrderBy(item => item.Sequence).Select(ToCueDto).ToArray());
    private static MovieCaptionCueDto ToCueDto(MovieCaptionCue cue) => new(cue.Id, cue.MovieCaptionTrackId, cue.Sequence, MovieCaptionTimecode.FormatVtt(cue.StartMilliseconds), MovieCaptionTimecode.FormatVtt(cue.EndMilliseconds), cue.StartMilliseconds, cue.EndMilliseconds, cue.Text, cue.SpeakerCharacterId, cue.SpeakerName ?? cue.SpeakerCharacter?.Name, cue.MovieSceneId, cue.MovieShotId, cue.MovieTakeId, cue.CreatedAt, cue.UpdatedAt);
}

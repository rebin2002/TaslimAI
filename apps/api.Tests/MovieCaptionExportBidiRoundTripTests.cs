using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The owner authors Arabic and Kurdish Sorani subtitles, so an exported
/// caption file is only usable if the right-to-left content survives the
/// download -> re-upload hop intact. This lane probes the bidi surface that
/// the ASCII-only grammar suite (MovieCaptionExportRoundTripTests, PR #1036)
/// never exercises: real Arabic/Sorani multi-line cues with mixed break
/// styles and invisible directional marks, a Sorani speaker name through the
/// WebVTT voice span, and the export-escape / import-unescape symmetry for
/// text that looks like a voice span or an HTML entity. Synthetic fixtures
/// only; no provider is contacted.
///
/// Expected states on current main (52a39468, without PR #1036's fold):
/// - The two FOLD-DEPENDENT pins are RED: a cue stored with an interior blank
///   line exports as an extra block with no timecode for both containers.
/// - The two structural-symmetry pins (entity text, voice-span-shaped text)
///   are GREEN on main and stay GREEN with #1036: they document the
///   exporter's protection boundary, which #1036 must not erode.
/// </summary>
public sealed class MovieCaptionExportBidiRoundTripTests : IClassFixture<TaslimApiFactory>
{
    private const char Rlm = '\u200F'; // RIGHT-TO-LEFT MARK
    private const char Zwj = '\u200D'; // ZERO-WIDTH JOINER

    private readonly TaslimApiFactory factory;

    public MovieCaptionExportBidiRoundTripTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>
    /// A stored Arabic cue can carry every break style at once (bare CR, CRLF,
    /// LF) plus an interior blank line and inline directional marks. Folding
    /// must normalise the breaks without touching any other character: the
    /// marks are content, not whitespace, and the cue count and timecodes must
    /// survive the export -> import hop untouched.
    /// FOLD-DEPENDENT: RED on main, GREEN with PR #1036.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Arabic_multiline_bidi_cue_survives_the_export_import_hop(string format)
    {
        var owner = await Register($"Caption Bidi {format} Owner");
        var track = await CreateTrackAsync(owner.Client, owner.MovieId, $"عناوين {format}", "ar", isRtl: true);
        // Line 1 ends with a bare CR, then a CRLF blank line, then an LF break.
        var stored = $"بەشی یەکەم {Rlm}\r\r\nدووەمی ڕیز{Zwj}کوردی\nسێیەم";
        await AddCueAsync(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", stored);
        await AddCueAsync(owner.Client, track.Id, 2, "00:00:04.000", "00:00:06.000", $"أهلاً، {Rlm}بالكوردية");

        var exported = await ExportAsync(owner.Client, track.Id, format);

        // Grammar boundary: every blank-line block is still a real cue.
        var blocks = CueBlocks(exported, format);
        Assert.Equal(2, blocks.Length);
        Assert.All(blocks, block => Assert.Contains("-->", block, StringComparison.Ordinal));

        var reimported = await ReimportAsync(owner, exported, format, $"Reimported bidi {format}", "ar");
        Assert.Equal("ar", reimported.Language);
        Assert.True(reimported.IsRtl);
        Assert.Equal(2, reimported.Cues.Count);
        Assert.Equal("00:00:01.000", reimported.Cues[0].StartTimecode);
        Assert.Equal("00:00:03.000", reimported.Cues[0].EndTimecode);
        Assert.Equal("00:00:04.000", reimported.Cues[1].StartTimecode);
        Assert.Equal("00:00:06.000", reimported.Cues[1].EndTimecode);

        // The blank line was folded; every surviving character, marks included,
        // is identical to what the owner authored.
        Assert.Equal($"بەشی یەکەم {Rlm}\nدووەمی ڕیز{Zwj}کوردی\nسێیەم", reimported.Cues[0].Text);
        Assert.Equal($"أهلاً، {Rlm}بالكوردية", reimported.Cues[1].Text);
    }

    /// <summary>
    /// A Sorani speaker name only has a carrier in WebVTT: the voice span. The
    /// exporter HTML-encodes the quoted name, the importer must hand back the
    /// exact Sorani name and cue text after the download -> re-upload hop.
    /// (SRT carries no speaker, so the pin is VTT-only by design.)
    /// </summary>
    [Fact]
    public async Task Sorani_speaker_name_survives_the_webvtt_voice_span_hop()
    {
        var owner = await Register("Caption Sorani Speaker Owner");
        var track = await CreateTrackAsync(owner.Client, owner.MovieId, "دەنگ", "ckb", isRtl: true);
        await AddCueAsync(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000",
            "سڵاو، چۆنیت؟", speakerName: "ئاناس \"پیاوە\"");

        var exported = await ExportAsync(owner.Client, track.Id, "vtt");
        Assert.Contains("<v ئاناس &quot;پیاوە&quot;>", exported, StringComparison.Ordinal);

        var reimported = await ReimportAsync(owner, exported, "vtt", "Reimported speaker", "ckb");
        var cue = Assert.Single(reimported.Cues);
        Assert.Equal("سڵاو، چۆنیت؟", cue.Text);
        Assert.Equal("ئاناس \"پیاوە\"", cue.SpeakerName);
    }

    /// <summary>
    /// WebVTT export escapes only the three structural characters (&, <, >)
    /// while import decodes the full HTML entity table. The hop is lossless
    /// only because each escaped entity is decoded exactly once: cue text
    /// that itself looks like an entity ("&hellip;", "&nbsp;") must return
    /// verbatim, not as U+2026 or U+00A0. Pins the asymmetry boundary the
    /// bidi owner's files depend on. GREEN on main; must stay GREEN.
    /// </summary>
    [Fact]
    public async Task WebVtt_entity_looking_cue_text_round_trips_verbatim()
    {
        var owner = await Register("Caption VTT Entity Owner");
        var track = await CreateTrackAsync(owner.Client, owner.MovieId, "کورتەبینرەوە", "ckb", isRtl: true);
        const string stored = "کۆتایی&hellip; بەرەبە &nbsp; چاوەڕوان";
        await AddCueAsync(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", stored);

        var exported = await ExportAsync(owner.Client, track.Id, "vtt");

        // The exporter only had to neutralise the ampersands, so the entity
        // text is present in its escaped form in the download.
        Assert.Contains("کۆتایی&amp;hellip; بەرەبە &amp;nbsp; چاوەڕوان", exported, StringComparison.Ordinal);

        var reimported = await ReimportAsync(owner, exported, "vtt", "Reimported vtt entity", "ckb");
        var cue = Assert.Single(reimported.Cues);
        Assert.Equal(stored, cue.Text);
        Assert.DoesNotContain('\u00A0', cue.Text);
        Assert.DoesNotContain('\u2026', cue.Text);
    }

    /// <summary>
    /// Cue text that starts like a voice span is content, not structure: the
    /// exporter escapes its angle brackets, so a leading "<v ...>" must not
    /// be read back as a speaker name. With the interior blank line folded
    /// away the whole multi-line cue comes back as one cue, text intact,
    /// speaker null. FOLD-DEPENDENT: RED on main, GREEN with PR #1036.
    /// </summary>
    [Fact]
    public async Task WebVtt_cue_text_starting_like_a_voice_span_stays_cue_text()
    {
        var owner = await Register("Caption VTT Span Owner");
        var track = await CreateTrackAsync(owner.Client, owner.MovieId, "دەق", "ckb", isRtl: true);
        await AddCueAsync(owner.Client, track.Id, 1, "00:00:01.000", "00:00:04.000",
            "<v ناکاو>دەق\n\nکۆتایی دەق");

        var exported = await ExportAsync(owner.Client, track.Id, "vtt");
        Assert.Equal(1, CueBlocks(exported, "vtt").Length);

        var reimported = await ReimportAsync(owner, exported, "vtt", "Reimported vtt span", "ckb");
        var cue = Assert.Single(reimported.Cues);
        Assert.Equal("<v ناکاو>دەق\nکۆتایی دەق", cue.Text);
        Assert.Null(cue.SpeakerName);
    }

    private static string[] CueBlocks(string document, string format)
    {
        var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return format == "vtt"
            ? blocks.Where(block => !block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)).ToArray()
            : blocks;
    }

    private async Task<MovieCaptionTrackDto> ReimportAsync((HttpClient Client, Guid MovieId) owner, string content, string format, string name, string language)
    {
        using var reimport = await SendWithCsrfAsync(owner.Client, HttpMethod.Post,
            $"/api/movie-studio/projects/{owner.MovieId}/caption-tracks/import",
            new
            {
                name,
                trackType = MovieCaptionTrackTypes.Subtitle,
                language,
                isRtl = true,
                status = MovieCaptionTrackStatuses.Draft,
                format,
                content,
            });
        Assert.True(reimport.StatusCode == HttpStatusCode.OK, await reimport.Content.ReadAsStringAsync());
        return (await reimport.Content.ReadFromJsonAsync<MovieCaptionTrackDto>())!;
    }

    private async Task<string> ExportAsync(HttpClient client, Guid trackId, string format)
    {
        using var export = await client.GetAsync($"/api/movie-studio/caption-tracks/{trackId}/export?format={format}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        return await export.Content.ReadAsStringAsync();
    }

    private async Task<(HttpClient Client, Guid MovieId)> Register(string displayName)
    {
        var client = factory.CreateClient();
        var response = await SendWithCsrfAsync(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-bidi-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        var movie = await SendWithCsrfAsync<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = $"Caption bidi export {Guid.NewGuid():N}",
            description = "A movie used to pin the Arabic/Sorani caption export round-trip contract.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "ar",
        });
        return (client, movie.Project.Id);
    }

    private static async Task<MovieCaptionTrackDto> CreateTrackAsync(HttpClient client, Guid movieId, string name, string language, bool isRtl) =>
        await SendWithCsrfAsync<MovieCaptionTrackDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/caption-tracks", new
        {
            name,
            trackType = MovieCaptionTrackTypes.Subtitle,
            language,
            isRtl,
            status = MovieCaptionTrackStatuses.Ready,
        });

    private static async Task AddCueAsync(HttpClient client, Guid trackId, int sequence, string start, string end, string text, string? speakerName = null) =>
        await SendWithCsrfAsync<MovieCaptionCueDto>(client, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence,
            startTimecode = start,
            endTimecode = end,
            text,
            speakerName,
        });

    private static async Task<T> SendWithCsrfAsync<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrfAsync(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrfAsync(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

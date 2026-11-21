using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Contracts;
using Taslim.Api.Movies;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA coverage for <c>GET /api/movie-studio/caption-tracks/{trackId}/export</c>,
/// taken strictly at the file-response boundary: the bytes on the wire, the declared media type
/// and the download file name.
///
/// <c>MovieCaptionExportRoundTripTests</c> pins the folding behaviour by reading the
/// exported document as a string and feeding it back through the importer. Nothing here repeats
/// that; the sibling class is named only to explain the division of labour. This class asserts
/// the two things a round trip cannot see:
///
///   1. what the HTTP layer promises the downloader - canonical media type and attachment name,
///      UTF-8 body, no byte-order mark - and that user-authored track metadata cannot reach the
///      response headers; and
///   2. that the physical byte layout of the served document keeps the container grammar, so
///      stored text can never displace the authoritative timecode line or open a cue/header.
///
/// Synthetic and provider-free throughout: cues are authored through the ordinary caption
/// endpoints and downloaded from the ordinary export endpoint.
/// </summary>
public sealed class MovieCaptionExportHttpResponseContractTests : IClassFixture<TaslimApiFactory>
{
    private readonly TaslimApiFactory factory;

    public MovieCaptionExportHttpResponseContractTests(TaslimApiFactory factory)
    {
        this.factory = factory;
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    /// <summary>A physical line that looks exactly like the start of a cue timing line.</summary>
    private static readonly Regex TimingLine = new(
        @"^\d{1,4}:\d{2}:\d{2}[.,]\d{3} --> ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// The export endpoint is the only place the product commits to a format: the media type tells
    /// the downloader how to decode the file and the attachment name tells it what to call it.
    /// Every accepted alias (case, leading dot, <c>webvtt</c>) has to collapse onto one canonical
    /// answer, and the document must open with its own container's grammar rather than a stray
    /// byte-order mark or the other container's header.
    /// </summary>
    [Theory]
    [InlineData("srt", "application/x-subrip", ".srt", "WEBVTT")]
    [InlineData("SRT", "application/x-subrip", ".srt", "WEBVTT")]
    [InlineData(".srt", "application/x-subrip", ".srt", "WEBVTT")]
    [InlineData("vtt", "text/vtt", ".vtt", "1\r\n")]
    [InlineData("webvtt", "text/vtt", ".vtt", "1\r\n")]
    [InlineData("VTT", "text/vtt", ".vtt", "1\r\n")]
    public async Task Export_commits_to_one_canonical_media_type_and_file_name_per_container(
        string requestedFormat,
        string expectedMediaType,
        string expectedExtension,
        string forbiddenOpeningToken)
    {
        var owner = await Register($"Export Contract {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, "Canonical download");
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:02.000", "First cue.");

        using var response = await owner.Client.GetAsync(
            $"/api/movie-studio/caption-tracks/{track.Id}/export?format={requestedFormat}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);

        var fileName = await AttachmentNameAsync(response);
        Assert.EndsWith(expectedExtension, fileName, StringComparison.Ordinal);

        // The download has to be readable without sniffing: no BOM ahead of the first token, and
        // the document must not open with the *other* container's grammar.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            $"The exported {requestedFormat} file opens with a UTF-8 byte-order mark.");
        var body = Encoding.UTF8.GetString(bytes);
        Assert.False(body.StartsWith(forbiddenOpeningToken, StringComparison.Ordinal),
            $"The exported {requestedFormat} file must not open with '{forbiddenOpeningToken}'.");
        Assert.True(body.Length > 0, $"Exporting a populated track as {requestedFormat} returned an empty file.");
    }

    /// <summary>
    /// A caption track name is user-authored, and it is the only content that reaches
    /// <c>Content-Disposition</c>. A name carrying a header break (CR/LF), quote delimiters,
    /// path separators or a directory escape must still yield exactly one well-formed header whose
    /// file name is a single ASCII segment with the container's own extension: nothing smuggled in
    /// may surface as an extra header, and nothing may travel into the served file either.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Hostile_track_name_cannot_reach_the_download_header(string format)
    {
        var owner = await Register($"Export Header {Guid.NewGuid():N} Owner");
        var hostileName = "بیانی \"quoted\"\r\nX-Injected: 1\n../../etc/passwd; q=1.0";
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, hostileName);
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:02.000", "Harmless cue text.");

        using var response = await owner.Client.GetAsync(
            $"/api/movie-studio/caption-tracks/{track.Id}/export?format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(response.Headers.TryGetValues("X-Injected", out _),
            "Track metadata leaked into a response header of its own.");
        var dispositions = response.Content.Headers.GetValues("Content-Disposition").ToArray();
        Assert.Single(dispositions);
        var raw = dispositions[0];
        Assert.DoesNotContain("\r", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("../", raw, StringComparison.Ordinal);

        var fileName = await AttachmentNameAsync(response);
        Assert.DoesNotContain('"', fileName);
        Assert.DoesNotContain('/', fileName);
        Assert.DoesNotContain('\\', fileName);
        Assert.All(fileName, character => Assert.True(
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.',
            $"Download file name '{fileName}' carries U+{(int)character:X4}."));
        Assert.EndsWith($".{format}", fileName, StringComparison.Ordinal);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("X-Injected", body, StringComparison.Ordinal);
        Assert.Contains("Harmless cue text.", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Taslim.ai's audience is Arabic, Kurdish and Persian subtitling, so exported files are
    /// routinely non-ASCII. Everything a cue can legally hold - RTL letters, bidi overrides and
    /// isolates, combining marks, astral emoji - has to reach the download byte-for-byte as valid
    /// UTF-8, and none of it may be mistaken for a line terminator: SRT and WebVTT end a line at
    /// CR, LF or CRLF only, so U+2028/U+2029 stay ordinary cue content and open no new cue.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Non_ascii_cue_content_survives_the_download_byte_for_byte(string format)
    {
        var owner = await Register($"Export Unicode {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Unicode {format}");

        const string rtl = "بەشی یەکەم؛ שלום";
        const string combining = "taslim\u0301";
        const string astral = "字幕\U0001F3AC\U0001F1F6";
        const string isolates = "a\u2066b\u2069c\u202Ed";
        const string separators = "before\u2028after\u2029end";
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:03.000", rtl);
        await AddCue(owner.Client, track.Id, 2, "00:00:04.000", "00:00:06.000", combining);
        await AddCue(owner.Client, track.Id, 3, "00:00:07.000", "00:00:09.000", astral);
        await AddCue(owner.Client, track.Id, 4, "00:00:10.000", "00:00:12.000", isolates);
        await AddCue(owner.Client, track.Id, 5, "00:00:13.000", "00:00:15.000", separators);

        var bytes = await ExportBytes(owner.Client, track.Id, format);
        // Strict decoding: an ill-formed byte sequence throws here instead of shipping a file the
        // subtitle renderer will refuse.
        var body = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.False(body.StartsWith("\uFEFF", StringComparison.Ordinal));

        foreach (var expected in new[] { rtl, combining, astral, isolates, separators })
        {
            Assert.Contains(Expected(expected, format), body, StringComparison.Ordinal);
        }

        // U+2028/U+2029 were content, not breaks: five cues authored, five blocks served.
        Assert.Equal(5, CueBlocks(body, format).Length);
    }

    /// <summary>
    /// The invariant the folding fix buys the product, restated on raw served bytes instead of on
    /// a re-imported DTO: the authoritative timecode is always the second physical line of its
    /// block, and every blank-line separated block is a cue. Text that imitates an index line, a
    /// timecode, a <c>WEBVTT</c> header or a <c>NOTE</c> stanza may therefore never displace
    /// structure, so a downstream reader cannot invent a cue or a header out of caption content.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task Served_document_keeps_the_timecode_line_in_the_structural_position(string format)
    {
        var owner = await Register($"Export Position {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Position {format}");

        // Cue 1 carries an interior blank line, which is what splits one stored cue into two served
        // blocks before the fold. Cue 2 carries a whitespace-only line, the subtler half of the same
        // defect: it opens no new block, but it does end the cue text for the reader the product uses,
        // so everything below it is dropped. Both are wrapped in text imitating an index line, a
        // timecode line, a WEBVTT header and a NOTE stanza, so structure is not preserved by accident.
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:04.000",
            "2\n00:00:10.000 --> 00:00:11.000\n\nWEBVTT\nNOTE fake header");
        await AddCue(owner.Client, track.Id, 2, "00:00:05.000", "00:00:08.000",
            "plain line\r\n   \r\n00:00:20.000 --> 00:00:21.000");

        var body = await ExportText(owner.Client, track.Id, format);
        var blocks = CueBlocks(body, format);
        // Two cues authored, two blocks served: the interior break neither split a cue open nor
        // merged the neighbours.
        Assert.Equal(2, blocks.Length);

        var separator = format == "srt" ? ',' : '.';
        var expectedStart = new[] { $"00:00:01{separator}000", $"00:00:05{separator}000" };

        for (var index = 0; index < blocks.Length; index++)
        {
            var lines = SplitLines(blocks[index]);
            // Line 0 is the block's own sequence number, line 1 the only authoritative timecode.
            Assert.Equal((index + 1).ToString(), lines[0]);
            Assert.Equal(1, Array.FindIndex(lines, line => line.Contains(" --> ", StringComparison.Ordinal)));
            Assert.StartsWith(expectedStart[index], lines[1], StringComparison.Ordinal);

            // No physical line of a served block may be blank or whitespace-only: the first would
            // open a new block, and the second would end the cue text for any container reader, so a
            // stored break of either kind has to be folded away before the bytes are served.
            Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line),
                $"Block {index + 1} of the served {format} file carries a blank-ish physical line."));

            // Any timecode-shaped content must sit strictly below the real timing line, so the
            // first such line in the block is always the one the exporter wrote.
            var shaped = new List<int>();
            for (var line = 0; line < lines.Length; line++)
            {
                if (TimingLine.IsMatch(lines[line])) shaped.Add(line);
            }

            Assert.Equal(1, shaped[0]);
            if (format == "vtt")
            {
                // WebVTT escapes markup, so a timecode can never survive as a second raw line.
                Assert.Single(shaped);
            }
        }
    }

    /// <summary>
    /// The exporter's CRLF promise, extended to the one field the round-trip tests never
    /// populate: the speaker name.
    ///
    /// SRT has no voice syntax, so a stored speaker must not reach the served file at all. That
    /// half of the contract holds and is pinned here.
    /// </summary>
    [Fact]
    public async Task Speaker_name_never_reaches_the_srt_download()
    {
        var owner = await Register($"Export Speaker SRT {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, "Speaker srt");
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:04.000", "Spoken line.",
            speakerName: "Announcer\n00:00:09.000 --> 00:00:10.000");

        var body = await ExportText(owner.Client, track.Id, "srt");
        Assert.DoesNotContain("Announcer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("00:00:09", body, StringComparison.Ordinal);
        Assert.Contains("Spoken line.", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The voice tag is the one place a stored break could still reach the served WebVTT file, because
    /// <c>SpeakerName</c> is the only exported field that does not pass through
    /// <c>MovieCaptionTextFolding</c>: the container escapes <c>&lt; &gt; &amp;</c> in the label, but an
    /// escaping function does not remove a CR or an LF, and SRT/WebVTT both end a cue text line at the
    /// first break.
    ///
    /// This case was authored against <c>fed75e4f</c> as a deliberately <c>Skip</c>ped reproduction -
    /// observed as "LF without a preceding CR at offset 56 of the served WebVTT file" - and the Skip is
    /// removed here because PR #1036 closed the gap it recorded: <c>0c460f06</c> collapses every Unicode
    /// whitespace run in the label to a single space and trims it before encoding, and <c>4cb6092e</c>
    /// pins the same rule at the adapter level with a Sorani fixture. Nothing is asserted twice: the
    /// parent's regression reads the string the adapter returns, while every assertion below reads the
    /// bytes the download endpoint actually serves.
    ///
    /// The cases are the shapes that can be stored through the ordinary cue endpoint: a break that
    /// imitates the container's own timecode syntax, a Sorani label mixing CRLF, tab and U+00A0, a label
    /// carrying markup and <c>&amp;</c>, and U+2028, which stays ordinary content inside cue <b>text</b>
    /// (<see cref="Non_ascii_cue_content_survives_the_download_byte_for_byte"/>) yet is a break the
    /// <b>label</b> may not keep. The asymmetry is deliberate: text may be multi-line, a voice tag may
    /// not.
    /// </summary>
    [Theory]
    [InlineData("Announcer\n00:00:09.000 --> 00:00:10.000", "Announcer 00:00:09.000 --&gt; 00:00:10.000")]
    [InlineData("\u06AF\u06D5\u067E\r\n\t\u0698\u06CC\u0644\u0627\u0646\u00A0\u06A9\u06C6\u0646", "\u06AF\u06D5\u067E \u0698\u06CC\u0644\u0627\u0646 \u06A9\u06C6\u0646")]
    [InlineData("\"\u062F\u0627\u0646\u0627\" <b> & \u202EQout ", "&quot;\u062F\u0627\u0646\u0627&quot; &lt;b&gt; &amp; \u202EQout")]
    [InlineData("\u062F\u0627\u0646\u0627\u2028\u0698\u06CC\u0644\u0627\u0646", "\u062F\u0627\u0646\u0627 \u0698\u06CC\u0644\u0627\u0646")]
    public async Task Speaker_name_cannot_break_the_webvtt_cue_line(string storedSpeakerName, string expectedEncodedLabel)
    {
        var owner = await Register($"Export Speaker VTT {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, "Speaker vtt");
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:04.000", "Spoken line.",
            speakerName: storedSpeakerName);

        var body = await ExportText(owner.Client, track.Id, "vtt");

        // Both directions of the exporter's CRLF promise, so neither a bare LF nor a bare CR survives:
        // every LF is preceded by its CR and every CR is followed by its LF.
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '\n')
            {
                Assert.True(index > 0 && body[index - 1] == '\r',
                    $"LF without a preceding CR at offset {index} of the served WebVTT file.");
            }
            else if (body[index] == '\r')
            {
                Assert.True(index + 1 < body.Length && body[index + 1] == '\n',
                    $"CR not followed by LF at offset {index} of the served WebVTT file.");
            }
        }

        var cue = Assert.Single(CueBlocks(body, "vtt"));
        var lines = SplitLines(cue);
        Assert.Equal(3, lines.Length);
        Assert.Equal("1", lines[0]);
        Assert.Equal("00:00:01.000 --> 00:00:04.000", lines[1]);
        // The whole label, markup neutralised and every break collapsed, on one physical line.
        Assert.Equal($"<v {expectedEncodedLabel}>Spoken line.</v>", lines[2]);

        // Structure was not imitated either: the timing line the exporter wrote stays the only
        // index/timecode-shaped line in the block, so the stored label cannot open a second cue.
        Assert.Equal(1, Array.FindIndex(lines, line => TimingLine.IsMatch(line)));
    }

    /// <summary>
    /// Authorisation for the download belongs to the caption track, not to the format string, and the
    /// export route is keyed by a bare track id. A caller in a different personal workspace - the same
    /// shape as two unrelated Taslim accounts - therefore has to be denied the owner's bytes, with the
    /// container's own media type and file name staying out of the response. The owner's request on the
    /// identical route and format is asserted in the same test, so a denial cannot be an artefact of a
    /// broken endpoint, and neither party's cue text leaks into the other's download.
    /// </summary>
    [Theory]
    [InlineData("srt", "application/x-subrip")]
    [InlineData("vtt", "text/vtt")]
    public async Task Another_workspace_cannot_download_the_caption_track(string format, string expectedMediaType)
    {
        var owner = await Register($"Export Foreign {Guid.NewGuid():N} Owner");
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, $"Foreign {format}");
        await AddCue(owner.Client, track.Id, 1, "00:00:01.000", "00:00:02.000", "Private subtitle.");

        var stranger = await Register($"Export Stranger {Guid.NewGuid():N} Owner");
        var strangerTrack = await CreateCaptionTrack(stranger.Client, stranger.MovieId, $"Stranger {format}");
        await AddCue(stranger.Client, strangerTrack.Id, 1, "00:00:01.000", "00:00:02.000", "Stranger subtitle.");

        // Positive control first: the route works, so the denial below is about scope, not plumbing.
        using var mine = await ExportResponse(owner.Client, track.Id, format);

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(expectedMediaType, mine.Content.Headers.ContentType?.MediaType);

        using var stolen = await ExportResponse(stranger.Client, track.Id, format);

        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        Assert.False(stolen.Content.Headers.Contains("Content-Disposition"),
            $"A cross-workspace {format} denial still offered a download file name.");
        Assert.NotEqual(expectedMediaType, stolen.Content.Headers.ContentType?.MediaType);
        var denial = await stolen.Content.ReadAsStringAsync();
        Assert.Contains("MOVIE_CAPTION_TRACK_NOT_FOUND", denial, StringComparison.Ordinal);
        Assert.DoesNotContain("Private subtitle.", denial, StringComparison.Ordinal);

        // And the stranger's own download stays clean: the shared fixture must not have crossed bytes.
        var strangerBody = await ExportText(stranger.Client, strangerTrack.Id, format);
        Assert.Contains("Stranger subtitle.", strangerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Private subtitle.", strangerBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty track is a legal download, not an error: the container still has to be declared
    /// and named canonically, and the served bytes must stay inside the grammar - WebVTT keeps its
    /// mandatory header, SRT serves an empty file rather than a dangling index line.
    /// </summary>
    [Theory]
    [InlineData("srt", "application/x-subrip")]
    [InlineData("vtt", "text/vtt")]
    public async Task Empty_track_still_downloads_as_a_well_formed_document(string format, string expectedMediaType)
    {
        var owner = await Register($"Export Empty {Guid.NewGuid():N} Owner");
        // The track name is the only thing that becomes the download name, so it has to be
        // written in a form whose sanitised shape is known before the request is made.
        var track = await CreateCaptionTrack(owner.Client, owner.MovieId, "empty-subtitles");

        using var response = await owner.Client.GetAsync(
            $"/api/movie-studio/caption-tracks/{track.Id}/export?format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        var fileName = await AttachmentNameAsync(response);
        Assert.Equal($"empty-subtitles.{format}", fileName);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(format == "vtt" ? "WEBVTT\r\n\r\n" : string.Empty, body);
        Assert.Empty(CueBlocks(body, format));
    }

    /// <summary>
    /// How the stored cue text is expected to read back from the served file: blank lines folded,
    /// breaks normalised to CRLF, and - for WebVTT only - the three markup characters escaped.
    /// The markup escape is deliberately the narrow one: WebVTT has to neutralise tag openers,
    /// but it must not reach for a full HTML encoder, which would also turn every astral emoji and
    /// non-ASCII glyph into a numeric character reference and destroy the byte-for-byte fidelity
    /// a subtitle download is supposed to keep.
    /// </summary>
    private static string Expected(string value, string format)
    {
        var folded = string.Join('\n', value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(static line => !string.IsNullOrWhiteSpace(line)));
        if (format == "vtt")
        {
            folded = folded
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
        }
        return folded.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static string[] CueBlocks(string document, string format)
    {
        var normalized = document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return format == "vtt"
            ? blocks.Where(block => !block.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)).ToArray()
            : blocks;
    }

    private static string[] SplitLines(string document) =>
        document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static async Task<string> AttachmentNameAsync(HttpResponseMessage response)
    {
        var dispositions = response.Content.Headers.GetValues("Content-Disposition").ToArray();
        Assert.Single(dispositions);
        var parsed = ContentDispositionHeaderValue.Parse(dispositions[0]);
        Assert.Equal("attachment", parsed.DispositionType);
        var fileName = (parsed.FileNameStar ?? parsed.FileName)?.Trim('"');
        Assert.False(string.IsNullOrWhiteSpace(fileName));
        return fileName!;
    }

    private static async Task<HttpResponseMessage> ExportResponse(HttpClient client, Guid trackId, string format) =>
        await client.GetAsync($"/api/movie-studio/caption-tracks/{trackId}/export?format={format}");

    private static async Task<byte[]> ExportBytes(HttpClient client, Guid trackId, string format)
    {
        using var response = await client.GetAsync($"/api/movie-studio/caption-tracks/{trackId}/export?format={format}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> ExportText(HttpClient client, Guid trackId, string format) =>
        Encoding.UTF8.GetString(await ExportBytes(client, trackId, format));

    private async Task<(HttpClient Client, Guid MovieId)> Register(string displayName)
    {
        var client = factory.CreateClient();
        var response = await SendWithCsrf(client, HttpMethod.Post, "/api/auth/register", new
        {
            displayName,
            email = $"caption-http-{Guid.NewGuid():N}@example.com",
            password = "StrongPassword!123",
            preferredLanguage = "en",
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        var movie = await SendWithCsrf<MovieStudioProjectResponse>(client, HttpMethod.Post, "/api/movie-studio/projects", new
        {
            workspaceId = auth.PersonalWorkspace.Id,
            mode = MovieProjectModes.Full,
            title = $"Caption download contract {Guid.NewGuid():N}",
            description = "A movie used to pin the caption file-response contract.",
            durationSeconds = 120,
            aspectRatio = "16:9",
            style = "cinematic",
            language = "en",
        });
        return (client, movie.Project.Id);
    }

    private static async Task<MovieCaptionTrackDto> CreateCaptionTrack(HttpClient client, Guid movieId, string name) =>
        await SendWithCsrf<MovieCaptionTrackDto>(client, HttpMethod.Post, $"/api/movie-studio/projects/{movieId}/caption-tracks", new
        {
            name,
            trackType = MovieCaptionTrackTypes.Subtitle,
            language = "en",
            status = MovieCaptionTrackStatuses.Ready,
        });

    private static async Task AddCue(HttpClient client, Guid trackId, int sequence, string start, string end, string text, string? speakerName = null) =>
        await SendWithCsrf<MovieCaptionCueDto>(client, HttpMethod.Post, $"/api/movie-studio/caption-tracks/{trackId}/cues", new
        {
            sequence,
            startTimecode = start,
            endTimecode = end,
            text,
            speakerName,
        });

    private static async Task<T> SendWithCsrf<T>(HttpClient client, HttpMethod method, string path, object payload)
    {
        using var response = await SendWithCsrf(client, method, path, payload);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendWithCsrf(HttpClient client, HttpMethod method, string path, object? payload)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString()!);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return await client.SendAsync(request);
    }
}

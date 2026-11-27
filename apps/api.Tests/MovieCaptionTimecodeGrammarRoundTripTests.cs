using System.Globalization;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Independent QA for #1036 (caption export grammar lane): the TIMECODE FIELD grammar.
///
/// #1036 owns blank-line folding so cue blocks stay inside the container grammar;
/// #1168 pins Unicode fidelity of that text; #1076 pins bidi round-trip; #1118 pins
/// the HTTP file-response boundary. None of them pin the arithmetic the exporters
/// perform on cue bounds themselves: the second-field rollover at 59.999, the strict
/// non-normalised forms the importer refuses, zero-duration cues, multi-hour
/// timestamps, and the time-order renumbering a download carries — for a track that
/// spans a whole 2-hour movie. This lane closes that gap test-only.
///
/// Everything here is synthetic: no video, no provider, no customer data, no database.
/// The assertions run the real production exporter (<c>Serialize</c>) and the real
/// production importer (<c>Parse</c>) back to back, so the pinned contract is exactly
/// the round trip an owner performs between download and re-import. GREEN on main and
/// stays GREEN with #1036: it documents the grammar both branches must not break.
/// </summary>
public sealed class MovieCaptionTimecodeGrammarRoundTripTests
{
    private const long SecondRolloverMs = 59_999;      // last millisecond before a minute carries
    private const long MinuteCarryMs = 60_000;         // "00:01:00,000": written only after rollover
    private const long HourCarryMs = 3_600_000;        // "01:00:00,000"
    private const long TwoHourMs = 7_200_000;          // a 120-minute movie boundary
    private const long DayCeilingMs = 86_400_000;      // MovieDurationBudgetPolicy.MaxMovieDurationSeconds

    private static readonly SrtMovieCaptionFormatAdapter Srt = new();
    private static readonly WebVttMovieCaptionFormatAdapter Vtt = new();

    /// <summary>
    /// The exporter carries each field on integer boundaries — 59.999 becomes 01:00.000,
    /// never 00:60.000 or 00:59.1000 — and the importer's strict grammar reads the
    /// carried form back to the same millisecond. If either side drifts, a cue that
    /// straddles a minute boundary re-imports at a different time than it was authored.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SecondRolloverMs)]
    [InlineData(MinuteCarryMs)]
    [InlineData(119_999)]
    [InlineData(120_000)]
    [InlineData(599_999)]
    [InlineData(3_599_999)]
    [InlineData(HourCarryMs)]
    [InlineData(TwoHourMs - 1)]
    [InlineData(TwoHourMs)]
    [InlineData(DayCeilingMs - 1)]
    [InlineData(DayCeilingMs)]
    public void Exporter_rolls_each_field_forward_and_the_importer_reads_it_back_exact(long milliseconds)
    {
        var srtText = MovieCaptionTimecode.FormatSrt(milliseconds);
        var vttText = MovieCaptionTimecode.FormatVtt(milliseconds);

        // The grammar the importer enforces: two-digit minutes/seconds, three-digit fraction.
        Assert.Matches(@"^\d{1,4}:\d{2}:\d{2},\d{3}$", srtText);
        Assert.Matches(@"^\d{1,4}:\d{2}:\d{2}\.\d{3}$", vttText);
        Assert.DoesNotContain(":60:", srtText, StringComparison.Ordinal);
        Assert.DoesNotContain(":60:", vttText, StringComparison.Ordinal);

        Assert.Equal(milliseconds, MovieCaptionTimecode.Parse(srtText));
        Assert.Equal(milliseconds, MovieCaptionTimecode.Parse(vttText));
    }

    /// <summary>
    /// A 1-millisecond cue that sits exactly on the minute rollover is the smallest
    /// thing an export can carry across the boundary. It must survive as one cue with
    /// both bounds intact; if the exporter ever floored the fraction or the importer
    /// accepted a non-normalised "00:00:60,000", this pair would merge, shift, or split.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void One_millisecond_cue_on_the_rollover_boundary_round_trips_whole(string format)
    {
        var adapter = Adapter(format);
        var document = new MovieCaptionDocument([
            new MovieCaptionDocumentCue(58_000, SecondRolloverMs, "Before the carry."),
            new MovieCaptionDocumentCue(SecondRolloverMs, MinuteCarryMs, "One millisecond across the carry."),
            new MovieCaptionDocumentCue(MinuteCarryMs, 61_000, "After the carry."),
        ]);

        var parsed = adapter.Parse(adapter.Serialize(document));

        Assert.Equal(3, parsed.Cues.Count);
        Assert.Equal(
            document.Cues.Select(cue => (cue.StartMilliseconds, cue.EndMilliseconds)),
            parsed.Cues.Select(cue => (cue.StartMilliseconds, cue.EndMilliseconds)));
        Assert.Equal("One millisecond across the carry.", parsed.Cues[1].Text);
    }

    /// <summary>
    /// The import grammar refuses the non-normalised forms a rollover could leak into
    /// ("00:00:60,000", "00:60:00,000") instead of silently carrying them — and it
    /// refuses a four-digit fraction and a five-digit hour, so a file the exporter would
    /// not produce cannot be smuggled in as a valid cue.
    /// </summary>
    [Theory]
    [InlineData("00:00:60,000")]
    [InlineData("00:60:00,000")]
    [InlineData("00:00:00,0000")]
    [InlineData("10000:00:00,000")]
    [InlineData("00:00:59.99")]
    [InlineData("00:00:59")]
    public void Importer_grammar_refuses_non_normalised_timecode_forms(string timecode)
    {
        Assert.Throws<MovieCaptionFormatException>(() => MovieCaptionTimecode.Parse(timecode));
    }

    /// <summary>
    /// Zero-duration cues cannot ride the round trip: both readers reject a block whose
    /// end equals its start. The API never stores such a cue (BuildCueAsync and
    /// ValidateDocument both require end &gt; start), so the exporter's unvalidated
    /// Serialize cannot be reached with zero-duration data — the readers are the fence.
    /// </summary>
    [Fact]
    public void Both_readers_reject_a_zero_duration_cue()
    {
        var srt = Assert.Throws<MovieCaptionFormatException>(() =>
            Srt.Parse("1\n00:00:01,000 --> 00:00:01,000\nZero duration\n"));
        Assert.Contains("greater than its start", srt.Message, StringComparison.Ordinal);

        var vtt = Assert.Throws<MovieCaptionFormatException>(() =>
            Vtt.Parse("WEBVTT\n\n1\n00:00:01.000 --> 00:00:01.000\nZero duration\n"));
        Assert.Contains("greater than its start", vtt.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole-film contract the 685 lane sizes and #1036 folds: a dense two-hour
    /// track — every cue carrying text across seconds, minutes, the first hour and the
    /// 02:00:00.000 boundary — downloads inside the grammar the same product imports,
    /// with no cue truncated, merged or shifted, and stays far under the documented
    /// 2,000,000-character import cap so a real feature film never hits the ceiling.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void Dense_two_hour_track_survives_the_export_import_hop_without_truncation(string format)
    {
        var adapter = Adapter(format);
        // 2,400 cues, one every 3 s of a 7,200 s movie, each with a genuine second line.
        var document = new MovieCaptionDocument(Enumerable.Range(0, 2_400)
            .Select(index => new MovieCaptionDocumentCue(
                index * 3_000L + 500,
                index * 3_000L + 2_500L,
                $"Cue {index + 1} first line.\nSecond line."))
            .ToList());

        var exported = adapter.Serialize(document);
        Assert.True(exported.Length < 2_000_000,
            $"A two-hour {format} export measured {exported.Length:N0} characters and must stay inside the 2,000,000-character import boundary.");

        var parsed = adapter.Parse(exported);

        Assert.Equal(2_400, parsed.Cues.Count);
        Assert.Equal(document.Cues.Select(cue => (cue.StartMilliseconds, cue.EndMilliseconds)),
                     parsed.Cues.Select(cue => (cue.StartMilliseconds, cue.EndMilliseconds)));
        // Nothing at the tails: the movie's first authored cue and its last millisecond block.
        Assert.Equal(500, parsed.Cues[0].StartMilliseconds);
        Assert.Equal(TwoHourMs - 500, parsed.Cues[^1].EndMilliseconds);
        Assert.Contains("Cue 1201 first line.", exported, StringComparison.Ordinal); // the first cue after the 01:00:00 carry
        Assert.Contains("Cue 2400 first line.", exported, StringComparison.Ordinal); // the cue before 02:00:00
    }

    /// <summary>
    /// A download is a time-ordered document: cues authored out of sequence — including
    /// bounds that straddle the hour carry — come back renumbered 1..N in time order,
    /// and the re-imported sequence matches the exported block order exactly.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void Export_renumbers_cues_in_time_order_and_the_import_keeps_that_order(string format)
    {
        var adapter = Adapter(format);
        var document = new MovieCaptionDocument([
            new MovieCaptionDocumentCue(TwoHourMs, TwoHourMs + 1_000, "After the two-hour boundary."),
            new MovieCaptionDocumentCue(SecondRolloverMs, MinuteCarryMs, "On the minute carry."),
            new MovieCaptionDocumentCue(3_599_999, HourCarryMs, "On the hour carry."),
            new MovieCaptionDocumentCue(0, 500, "Opening."),
        ]);

        var exported = adapter.Serialize(document);
        var parsed = adapter.Parse(exported);

        Assert.Equal(
            new[] { 0, SecondRolloverMs, 3_599_999, TwoHourMs },
            parsed.Cues.Select(cue => cue.StartMilliseconds));
        Assert.Equal(
            new[] { "Opening.", "On the minute carry.", "On the hour carry.", "After the two-hour boundary." },
            parsed.Cues.Select(cue => cue.Text));

        // The written block identifiers ascend 1..N in the same time order.
        var identifiers = exported.Split('\n')
            .Select(line => line.Trim('\r'))
            .Where(line => int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(int.Parse)
            .ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, identifiers);
    }

    private static IMovieCaptionFormatAdapter Adapter(string format) => format == "srt" ? Srt : Vtt;
}

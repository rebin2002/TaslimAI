using System.Text;
using System.Text.RegularExpressions;
using Taslim.Api.Movies;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// This lane pins the character-level fidelity of the two caption containers at the
/// adapter boundary that ships the download: <c>Serialize</c> and <c>Parse</c> on
/// <see cref="SrtMovieCaptionFormatAdapter"/> and <see cref="WebVttMovieCaptionFormatAdapter"/>.
///
/// Division of labour with the caption lanes already on the board:
/// - PR #1036 (grammar) pins blank-line folding, break normalisation, grammar-imitating
///   text and timecode survival for out-of-order sequences with distinct times.
/// - PR #1076 pins the Arabic/Sorani content through the HTTP export/import hop, two of
///   its cases fold-dependent.
/// - PR #1118 pins served bytes: media type, file name, no leading BOM on the response,
///   and a handful of non-ASCII families through the controller.
/// Never pinned anywhere: the exporter/parser pair applied to Sorani and Arabic COMBINING
/// MARKS (Arabic harakat) as exact codepoint sequences that must not be normalised, the
/// full directional-control repertoire (LRM/RLM, LRE/RLE/LRO/RLO, PDF, isolates) surviving
/// WebVTT's escape/unescape pair with byte-identical order (no directional rewriting),
/// CJK ideographs staying literal (never rewritten to numeric character references),
/// the import-side BOM boundary (a leading U+FEFF is absorbed, a mid-text U+FEFF is
/// content, and export never emits or accumulates one), the tie-timestamp ordering
/// boundary that the service-level overlap validation makes unreachable over HTTP, and
/// negative controls proving those assertions can actually fail.
///
/// Every fixture here is synthetic, authored from explicit U+ escapes so no editor or
/// bidi reordering can alter the file, single-line and free of interior blank lines, so
/// the pins are fold-independent: they hold identically on current main and on top of
/// PR #1036's text folding. Pure adapter calls: no provider, no network, no database.
/// </summary>
public sealed class MovieCaptionExportUnicodeFidelityTests
{
    // Sorani with Arabic combining marks (harakat) intentionally kept in decomposed
    // order: seen + fatha, ra + tanween, peh + kasra, waw + sukun, lam + fatha, then a
    // zero-width joiner between two marks-heavy words. U+064E/U+064B/U+0650/U+0652 are
    // Mn (non-spacing marks); the pin is that no codepoint of that run is stripped,
    // reordered or composed away between stored cue and served file.
    private const string SoraniMarks =
        "\u0633\u064E\u0631\u064B \u067E\u0650\u0648\u0652\u0644\u064E\u200D\u06A9\u0648\u06B5\u064A\u06CC";

    // Plain Arabic RTL run plus one explicit RIGHT-TO-LEFT MARK inside it.
    private const string ArabicRtl = "\u0645\u0631\u062D\u0628\u0627\u060C \u0628\u0627\u0644\u0639\u0627\u0644\u0645 \u0645\u064F\u0633\u0652\u0644\u0650\u0645\u0627\u0629\u200F";

    // CJK ideographs (Japanese kanji + kana, Korean) plus a HANGUL SYLLABLE left in its
    // decomposed jamo form: NFC would compose U+1112 U+1161 U+11AB into U+D55C, so a
    // fidelity check against the raw run carries no weight if the fixture is already
    // normal - that non-vacuity is asserted directly below.
    private const string CjkIdeographs = "\u5B57\u5E55\u6F22\u5B57 \u65E5\u672C\u8A9E\u30C6\u30B9\u30C8 \u5B57\u5E55\u6E2C\u8A66";
    private const string DecomposedHangul = "\u1112\u1161\u11AB\u1107\u1161"; // NFD of \uD55C\uAC00

    // The directional-control repertoire: LRM/RLM, LRE/RLE plus their terminating PDF,
    // an LRO override, an RLO override, and an isolate pair LRI..PDI. None of these may
    // be dropped, "cleaned", or used as an excuse to reverse the surrounding text.
    private const string BidiControls =
        "L\u202AR\u202CE\u202Bm\u202Cl\u200Eo\u200F \u202Dover\u202C\u202Eride\u202C \u2066iso\u2069";

    private static readonly SrtMovieCaptionFormatAdapter Srt = new();
    private static readonly WebVttMovieCaptionFormatAdapter WebVtt = new();

    private static readonly MovieCaptionDocument FidelityDocument = new(
    [
        new MovieCaptionDocumentCue(1_000, 2_500, SoraniMarks),
        new MovieCaptionDocumentCue(3_000, 5_000, ArabicRtl),
        new MovieCaptionDocumentCue(5_500, 7_000, CjkIdeographs + " " + DecomposedHangul),
        new MovieCaptionDocumentCue(7_500, 9_000, BidiControls),
    ]);

    private static readonly string[] FidelityTexts = [SoraniMarks, ArabicRtl, CjkIdeographs + " " + DecomposedHangul, BidiControls];

    /// <summary>
    /// The exported document is the stored text: every fixture run must appear verbatim,
    /// in order, as literal characters - never normalised, never rewritten to numeric
    /// character references, never reordered by the surrounding script. Re-importing the
    /// served text has to hand back the identical codepoint sequences, in the identical
    /// cue order. Holds for both containers.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void Serialize_writes_marks_rtl_and_ideographs_verbatim_and_Parse_hands_them_back(string format)
    {
        var adapter = Adapter(format);
        var body = adapter.Serialize(FidelityDocument);

        Assert.True(PassesFidelityProbe(body, FidelityTexts),
            $"The exported {format} document no longer preserves the authored codepoint runs.");

        // The decomposed hangul fixture really is decomposed, otherwise the probe above
        // could pass for a normalised export: NFC of the raw run differs from the raw run,
        // and the raw run is what the served file carries.
        var composed = DecomposedHangul.Normalize(NormalizationForm.FormC);
        Assert.NotEqual(DecomposedHangul, composed);
        Assert.Contains(DecomposedHangul, body, StringComparison.Ordinal);
        Assert.DoesNotContain(composed, body, StringComparison.Ordinal);

        var parsed = adapter.Parse(body);
        Assert.Equal(FidelityDocument.Cues.Count, parsed.Cues.Count);
        for (var index = 0; index < parsed.Cues.Count; index++)
        {
            Assert.Equal(FidelityTexts[index], parsed.Cues[index].Text);
            Assert.Equal(FidelityDocument.Cues[index].StartMilliseconds, parsed.Cues[index].StartMilliseconds);
            Assert.Equal(FidelityDocument.Cues[index].EndMilliseconds, parsed.Cues[index].EndMilliseconds);
        }
    }

    /// <summary>
    /// WebVTT neutralises markup only with literal UTF-8, not by entityising: the cue
    /// text escapes just the three structural characters, so ideographs, marks and
    /// controls must reach the file as themselves, and the import-side decode must not
    /// invent any entity out of them. A switch to WebUtility.HtmlEncode on the text would
    /// rewrite every non-ASCII character to a numeric reference and fail this pin.
    /// </summary>
    [Fact]
    public void WebVtt_text_is_never_rewritten_into_numeric_character_references()
    {
        var body = WebVtt.Serialize(FidelityDocument);

        Assert.DoesNotContain("&#", body, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("&gt;", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The BOM boundary: a leading U+FEFF on an imported document is absorbed by both
    /// parsers instead of becoming cue content, a mid-text U+FEFF stays exactly where the
    /// author put it, and export never emits one, so a download - re-import - download
    /// cycle cannot accumulate byte-order marks.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void Byte_order_mark_is_absorbed_on_import_never_emitted_or_accumulated(string format)
    {
        var adapter = Adapter(format);
        var clean = adapter.Serialize(FidelityDocument);

        // Export emits no BOM at all: not at the opening, not anywhere inside.
        Assert.False(clean.StartsWith("\uFEFF", StringComparison.Ordinal));
        Assert.DoesNotContain('\uFEFF', clean);
        Assert.StartsWith(format == "srt" ? "1\r\n" : "WEBVTT\r\n", clean, StringComparison.Ordinal);

        // Import absorbs exactly one leading BOM: cue text and structure match the
        // unprefixed document, so the mark never travels into a cue or a speaker name.
        var fromBommed = adapter.Parse("\uFEFF" + clean);
        var fromClean = adapter.Parse(clean);
        Assert.Equal(fromClean.Cues.Count, fromBommed.Cues.Count);
        for (var index = 0; index < fromClean.Cues.Count; index++)
        {
            Assert.Equal(fromClean.Cues[index].Text, fromBommed.Cues[index].Text);
            Assert.Equal(fromClean.Cues[index].StartMilliseconds, fromBommed.Cues[index].StartMilliseconds);
        }

        // One more hop, BOM prefixed again: the re-export is byte-identical and BOM-free.
        var recycled = adapter.Serialize(fromBommed);
        Assert.Equal(clean, recycled);
        Assert.DoesNotContain('\uFEFF', recycled);

        // A U+FEFF inside cue text is authored content, not a BOM: it survives export
        // and re-import exactly once, in the same position.
        var midMark = new MovieCaptionDocument([new MovieCaptionDocumentCue(1_000, 2_000, "\u0628\u06D5\u200Fx\uFEFFy")]);
        var midBody = adapter.Serialize(midMark);
        Assert.Contains("\u200Fx\uFEFFy", midBody, StringComparison.Ordinal);
        Assert.Equal(1, midBody.Split('\uFEFF').Length - 1);
        var midParsed = adapter.Parse(midBody);
        Assert.Equal("\u0628\u06D5\u200Fx\uFEFFy", Assert.Single(midParsed.Cues).Text);
    }

    /// <summary>
    /// Cue timestamp order on the boundary no HTTP route can produce: the service's
    /// overlap validation refuses two cues sharing a range, so equal start AND equal end
    /// cues exist only in the document handed to the adapters. Serialisation must keep
    /// them in the authored order (stable), stamp every block with the identical timecode
    /// line, and re-import must return the same order - the one tie case PR #1036's
    /// distinct-times timecode pin never reaches.
    /// </summary>
    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public void Equal_timestamp_cues_keep_their_authored_order(string format)
    {
        var separator = format == "srt" ? ',' : '.';
        var expectedTiming = $"00:00:01{separator}000 --> 00:00:02{separator}000";
        var orderedTexts = new[] { "Alpha tie cue", "\u0623\u062E\u0628\u0627\u0631 tie cue", "\u5B57\u5E55 tie cue" };
        var document = new MovieCaptionDocument(
        [
            new MovieCaptionDocumentCue(1_000, 2_000, orderedTexts[0]),
            new MovieCaptionDocumentCue(1_000, 2_000, orderedTexts[1]),
            new MovieCaptionDocumentCue(1_000, 2_000, orderedTexts[2]),
        ]);

        var adapter = Adapter(format);
        var body = adapter.Serialize(document);

        Assert.True(PassesFidelityProbe(body, orderedTexts),
            $"Tie-timecode {format} cues lost their authored order or exact text.");

        // Every block carries the same authoritative timecode line at the structural
        // position, numbered 1..3 in order.
        var blocks = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (format == "vtt")
        {
            blocks = blocks.Skip(1).ToArray();
        }
        Assert.Equal(3, blocks.Length);
        for (var index = 0; index < blocks.Length; index++)
        {
            var lines = blocks[index].Split('\n');
            Assert.Equal((index + 1).ToString(), lines[0]);
            Assert.Equal(expectedTiming, lines[1]);
            Assert.Equal(orderedTexts[index], lines[2]);
        }

        var parsed = adapter.Parse(body);
        Assert.Equal(orderedTexts, parsed.Cues.Select(cue => cue.Text).ToArray());
    }

    /// <summary>
    /// Negative controls: the probe that the pins above rest on must actually fail when
    /// the exported text is rewritten the ways this lane is about - a prepended BOM,
    /// marks stripped, a directional override "cleaned" away, RTL runs reversed,
    /// ideographs normalised to their composed form, or tie cues reordered. Each mutated
    /// document is produced locally inside the test; no product file is touched.
    /// </summary>
    [Fact]
    public void Fidelity_probe_fails_on_simulated_rewrites_it_exists_to_detect()
    {
        var body = Srt.Serialize(FidelityDocument);
        Assert.True(PassesFidelityProbe(body, FidelityTexts), "the unmutated export must pass the probe.");

        // A prepended BOM is detected by the opening-token rule.
        Assert.False(PassesFidelityProbe("\uFEFF" + body, FidelityTexts));

        // Stripping the combining marks and format controls is detected.
        Assert.False(PassesFidelityProbe(Regex.Replace(body, @"\p{Mn}|\p{Cf}", string.Empty), FidelityTexts));

        // Reversing the Arabic run (a directional rewrite) is detected.
        Assert.False(PassesFidelityProbe(body.Replace(ArabicRtl, new string(ArabicRtl.Reverse().ToArray())), FidelityTexts));

        // Composing the decomposed hangul away (an NFC rewrite) is detected.
        Assert.False(PassesFidelityProbe(body.Replace(DecomposedHangul, DecomposedHangul.Normalize(NormalizationForm.FormC)), FidelityTexts));

        // Rewriting the ideographs into numeric character references (the escaping
        // regression WebVTT is pinned against) is detected.
        Assert.False(PassesFidelityProbe(body.Replace(CjkIdeographs, CjkIdeographs.EncodeToHtmlEscapes()), FidelityTexts));

        // Swapping two tie cues is detected by the order walk.
        var swapped = new MovieCaptionDocument(
        [
            new MovieCaptionDocumentCue(1_000, 2_000, "Alpha tie cue"),
            new MovieCaptionDocumentCue(1_000, 2_000, "\u5B57\u5E55 tie cue"),
            new MovieCaptionDocumentCue(1_000, 2_000, "\u0623\u062E\u0628\u0627\u0631 tie cue"),
        ]);
        Assert.False(PassesFidelityProbe(Srt.Serialize(swapped), ["Alpha tie cue", "\u0623\u062E\u0628\u0627\u0631 tie cue", "\u5B57\u5E55 tie cue"]));
    }

    private static IMovieCaptionFormatAdapter Adapter(string format) =>
        format == "srt" ? Srt : WebVtt;

    /// <summary>
    /// The character-level rules this lane pins, as one reusable probe:
    /// 1. the document opens clean - no BOM, and it must not smuggle one anywhere;
    /// 2. every authored run appears verbatim and in order (exact codepoint substring
    ///    tests, so any strip, reorder, normalise or entity rewrite breaks the walk);
    /// 3. non-ASCII is never carried as a numeric character reference.
    /// </summary>
    private static bool PassesFidelityProbe(string document, string[] orderedTexts)
    {
        if (document.StartsWith("\uFEFF", StringComparison.Ordinal) || document.Contains('\uFEFF'))
        {
            return false;
        }

        if (document.Contains("&#", StringComparison.Ordinal))
        {
            return false;
        }

        var walked = -1;
        foreach (var text in orderedTexts)
        {
            var found = document.IndexOf(text, walked + 1, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            walked = found;
        }

        return true;
    }
}

internal static class MovieCaptionExportUnicodeFidelityTestExtensions
{
    /// <summary>
    /// Builds the numeric-reference form a buggy encoder would emit, so the probe can be
    /// shown to reject it; used only to construct a mutation inside the negative control.
    /// </summary>
    public static string EncodeToHtmlEscapes(this string value) =>
        string.Concat(value.Select(character => $"&#{(int)character};"));
}

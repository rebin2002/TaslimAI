using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Validation;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Taslim.Api.AcademicTemplates;

namespace Taslim.Api.AcademicExport;

/// <summary>
/// What "the file is real" means before an export is offered for download. This is not a byte-length
/// sniff: the artifact is re-opened through the format's own reader, and the content contract is
/// re-checked on the parsed result — the report title, the citation markers, the bibliography heading
/// and the integrity note must all be present in the document that was actually written, not merely
/// intended. An export that fails validation returns a stable error instead of a broken file.
/// </summary>
public static class AcademicExportFileValidator
{
    public sealed record ValidationReport(bool Passed, IReadOnlyList<string> Violations);

    public static ValidationReport ValidateDocx(byte[] bytes, AcademicSavedReport report, AcademicDesignSpec spec)
    {
        var violations = new List<string>();
        try
        {
            using var package = WordprocessingDocument.Open(new MemoryStream(bytes), false);
            var body = package.MainDocumentPart?.Document?.Body;
            if (body is null)
                return new ValidationReport(false, new[] { "docx-main-part" });

            var paragraphs = body.Descendants<Paragraph>().ToList();
            var texts = paragraphs.SelectMany(p => p.Descendants<Text>()).Select(t => t.Text).ToList();
            var allText = string.Join('\n', texts);

            if (paragraphs.Count < 2) violations.Add("docx-paragraph-count");
            if (!allText.Contains(report.Title, StringComparison.Ordinal)) violations.Add("docx-title-missing");
            if (!allText.Contains(spec.ConfigurationId, StringComparison.Ordinal)
                && !(package.PackageProperties.Keywords?.Contains(spec.ConfigurationId, StringComparison.Ordinal) ?? false))
                violations.Add("docx-template-identity-missing");
            if (report.CitedCitationIds.Count > 0 && !report.CitedCitationIds.Any(id => allText.Contains($"[{id}]", StringComparison.Ordinal)))
                violations.Add("docx-citations-missing");
            if (!allText.Contains(report.Labels.ReferencesHeading, StringComparison.Ordinal)) violations.Add("docx-bibliography-heading-missing");
            if (!allText.Contains(report.Labels.IntegrityNote, StringComparison.Ordinal)) violations.Add("docx-integrity-note-missing");
            // The disclosure belongs in every export now, so the check no longer waits for a slot: a
            // template that never reserved one used to pass validation while dropping the student's
            // statement of AI use.
            if (!allText.Contains(report.Labels.DisclosureHeading, StringComparison.Ordinal))
                violations.Add("docx-disclosure-heading-missing");
            foreach (var source in report.Sources.Where(source => !string.IsNullOrWhiteSpace(source.AccessNote)))
                if (!allText.Contains(source.AccessNote, StringComparison.Ordinal))
                    violations.Add($"docx-access-note-missing-{source.CitationId}");
            // Body content must survive the round trip. Before the reader accepted the payload's real
            // nested spelling, every section decoded to an empty heading with no blocks, and this
            // document still "passed": title page, summary, references, no report.
            foreach (var bodyText in report.Sections.SelectMany(section => section.Blocks).Where(block => !string.IsNullOrWhiteSpace(block.Text)).Select(block => block.Text!).Take(3))
                if (!allText.Contains(Collapse(bodyText), StringComparison.Ordinal) && !allText.Contains(bodyText, StringComparison.Ordinal))
                    violations.Add("docx-body-content-missing");

            // Section definitions, counted wherever they legally live: exactly one as a direct body
            // child (the final section) plus one per earlier section break inside a paragraph's
            // properties. Counting only body-level elements is what made the invalid two-break
            // document look correct here.
            if (body.Descendants<SectionProperties>().Count() < 2) violations.Add("docx-numbering-split-missing");

            // Then ask the format itself. A DOCX that Word refuses to open is not a delivered artifact,
            // however readable its text stream is.
            foreach (var error in new OpenXmlValidator().Validate(package))
            {
                violations.Add($"docx-openxml-{error.Id}:{error.Description}");
                break;
            }
        }
        catch (Exception exception)
        {
            violations.Add($"docx-unopenable:{exception.GetType().Name}");
        }
        return new ValidationReport(violations.Count == 0, violations);
    }

    public static ValidationReport ValidatePdf(byte[] bytes, AcademicSavedReport report)
    {
        var violations = new List<string>();
        if (bytes.Length < 1024) violations.Add("pdf-suspiciously-small");
        if (bytes.Length < 4 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
            violations.Add("pdf-header-missing");
        try
        {
            using var document = PdfDocument.Open(new MemoryStream(bytes));
            if (document.NumberOfPages < 1) violations.Add("pdf-zero-pages");
            var firstPageText = document.GetPage(1).Text ?? string.Empty;
            var pages = Enumerable.Range(1, document.NumberOfPages).Select(number => document.GetPage(number)).ToList();
            // A gate is satisfied when the saved string is visible in ANY reading of the artifact
            // (see Readings). Presence of prose must not depend on where a template happens to print
            // a page number: the chicago-humanities-monograph rows place their folio about 103pt from
            // the foot — outside the 100pt band, which is where QuestPDF puts a bottom-aligned footer
            // on that geometry — so a sentence that wrapped the break read back as
            // "…was not accessed in this4 workspace." and the gate reported
            // pdf-access-note-missing on an export that printed the note correctly.
            var readings = Readings(pages);

            bool Holds(string raw) => HoldsInText(raw, readings, StringComparison.Ordinal);

            // Case-insensitive, because the PDF draws the transform rather than storing it. A
            // design with H1Uppercase (the chicago-humanities-monograph registry rows, including
            // the heads=caps variant) renders the title as ToUpperInvariant(), and a run-level
            // uppercase is not applied to extracted text the way w:caps is handled by a Word
            // reader: the glyphs that come back are simply uppercase. An ordinal match therefore
            // reported pdf-title-missing on a correct export whenever the title appeared only on
            // the cover and RunningHeadContent was "none" — a real title in the artifact was
            // refused. The DOCX side stays ordinal on purpose: it emits w:caps over the original
            // text, so the stored characters are the report's own and an exact comparison is the
            // honest one there. This gate asserts presence, not letter case.
            if (!HoldsInText(report.Title, readings, StringComparison.OrdinalIgnoreCase))
                violations.Add("pdf-title-missing");
            // The cover must render: if the first page has zero extractable characters, layout failed.
            if (string.IsNullOrWhiteSpace(firstPageText) && document.NumberOfPages == 1) violations.Add("pdf-empty");

            if (!Holds(report.Labels.IntegrityNote)) violations.Add("pdf-integrity-note-missing");
            if (!Holds(report.Labels.ReferencesHeading)) violations.Add("pdf-bibliography-heading-missing");
            if (!Holds(report.Labels.DisclosureHeading)) violations.Add("pdf-disclosure-heading-missing");
            if (report.CitedCitationIds.Count > 0
                && !report.CitedCitationIds.Any(id => Holds($"[{id}]")))
                violations.Add("pdf-citations-missing");
            foreach (var source in report.Sources.Where(source => !string.IsNullOrWhiteSpace(source.AccessNote)))
                if (!Holds(source.AccessNote!))
                    violations.Add($"pdf-access-note-missing-{source.CitationId}");
            // A two-item list used to abort the render, and an untitled section used to vanish; both
            // are content the student paid to have filed, so the reopened PDF has to show it.
            foreach (var item in report.Sections.SelectMany(section => section.Blocks)
                         .SelectMany(block => block.Items ?? Array.Empty<string>()).Take(6))
                if (!Holds(item)) violations.Add("pdf-list-item-missing");
            foreach (var untitled in report.Sections.Where(section => string.IsNullOrWhiteSpace(section.Heading))
                         .SelectMany(section => section.Blocks).Where(block => !string.IsNullOrWhiteSpace(block.Text)).Select(block => block.Text!).Take(3))
                if (!Holds(untitled)) violations.Add("pdf-untitled-section-content-missing");
        }
        catch (Exception exception)
        {
            violations.Add($"pdf-unopenable:{exception.GetType().Name}");
        }
        return new ValidationReport(violations.Count == 0, violations);
    }

    private static string Collapse(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    /// <summary>
    /// The texts a PDF gate may witness a saved string in. A running page number can be drawn inside
    /// the margin band, outside it, or on a header/footer line it shares with other text, and each
    /// placement splices digits into the middle of a sentence that wraps across the page break — so
    /// one dump of the words is not enough, and no rule can pick the right dump from the SAVED text
    /// ("is there a bare number in it?" is a question about how the extractor tokenised "1912.",
    /// which a student's sentence cannot be held to answer). Every reading below is produced from the
    /// page by DELETING tokens and nothing else: no insertion, no reordering, no case folding. A
    /// string that matches is therefore present in the artifact character for character, and a
    /// heading, citation marker, access note or number that was really dropped is absent from all of
    /// them, because the only deletions are of numerals the page did print.
    /// <list type="number">
    /// <item><description>plain: every word, as printed;</description></item>
    /// <item><description>banded: numerals dropped where they sit in the header/footer band;</description></item>
    /// <item><description>folioless: a line holding nothing but numerals is the running number,
    /// dropped wherever it sits, while numerals inside prose and table cells survive;</description></item>
    /// <item><description>unnumbered: every standalone numeral dropped, which covers a folio printed on
    /// a line shared with other footer text.</description></item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<string> Readings(IReadOnlyList<Page> pages)
    {
        var plain = new StringBuilder();
        var banded = new StringBuilder();
        var folioless = new StringBuilder();
        var unnumbered = new StringBuilder();
        foreach (var page in pages)
        {
            var words = page.GetWords().ToList();
            var height = (double)page.Height;
            var band = Math.Min(100d, height * 0.12d);
            // Words sharing a baseline share a rounded bottom edge: a two-point bucket is finer than
            // any inter-line leading a template uses, and coarse enough to survive glyph jitter.
            var numericLines = words
                .GroupBy(word => (long)Math.Round((double)word.BoundingBox.Bottom / 2d))
                .Where(group => group.All(word => IsNumericToken(word.Text)))
                .Select(group => group.Key)
                .ToHashSet();
            foreach (var word in words)
            {
                var text = word.Text;
                var top = (double)word.BoundingBox.Top;
                var numeric = IsNumericToken(text);
                var inBand = top < band || top > height - band;
                plain.Append(text).Append(' ');
                if (!numeric || !inBand) banded.Append(text).Append(' ');
                if (!numeric || !numericLines.Contains((long)Math.Round((double)word.BoundingBox.Bottom / 2d)))
                    folioless.Append(text).Append(' ');
                if (!numeric) unnumbered.Append(text).Append(' ');
            }
        }
        return new[]
        {
            Collapse(plain.ToString()), Collapse(banded.ToString()),
            Collapse(folioless.ToString()), Collapse(unnumbered.ToString()),
        };
    }

    private static bool IsNumericToken(string text) => text.Length > 0 && text.All(char.IsDigit);

    /// <summary>
    /// True when the whitespace-free wanted string appears in one of the readings of the rendered
    /// pages. Inter-word spacing is not guaranteed by the extractor, so both sides are whitespace-free.
    /// </summary>
    private static bool HoldsInText(string raw, IReadOnlyList<string> readings, StringComparison comparison)
    {
        var wanted = Collapse(raw);
        return readings.Any(reading => reading.Contains(wanted, comparison));
    }

}

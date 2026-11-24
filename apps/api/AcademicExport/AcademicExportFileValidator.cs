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
            // PdfPig concatenates positioned words without guaranteeing inter-word spaces, so the
            // containment check ignores whitespace rather than pretending the extractor preserves it.
            var wanted = Collapse(report.Title);
            var anyPageHasTitle = Enumerable.Range(1, document.NumberOfPages).Any(number =>
                Collapse(document.GetPage(number).Text ?? string.Empty).Contains(wanted, StringComparison.Ordinal));
            if (!anyPageHasTitle) violations.Add("pdf-title-missing");
            // The cover must render: if the first page has zero extractable characters, layout failed.
            if (string.IsNullOrWhiteSpace(firstPageText) && document.NumberOfPages == 1) violations.Add("pdf-empty");

            var everyPage = Collapse(string.Concat(Enumerable.Range(1, document.NumberOfPages)
                .Select(number => PageText(document.GetPage(number)))));
            if (!everyPage.Contains(Collapse(report.Labels.IntegrityNote), StringComparison.Ordinal))
                violations.Add("pdf-integrity-note-missing");
            if (!everyPage.Contains(Collapse(report.Labels.ReferencesHeading), StringComparison.Ordinal))
                violations.Add("pdf-bibliography-heading-missing");
            if (!everyPage.Contains(Collapse(report.Labels.DisclosureHeading), StringComparison.Ordinal))
                violations.Add("pdf-disclosure-heading-missing");
            if (report.CitedCitationIds.Count > 0
                && !report.CitedCitationIds.Any(id => everyPage.Contains(Collapse($"[{id}]"), StringComparison.Ordinal)))
                violations.Add("pdf-citations-missing");
            foreach (var source in report.Sources.Where(source => !string.IsNullOrWhiteSpace(source.AccessNote)))
                if (!everyPage.Contains(Collapse(source.AccessNote), StringComparison.Ordinal))
                    violations.Add($"pdf-access-note-missing-{source.CitationId}");
            // A two-item list used to abort the render, and an untitled section used to vanish; both
            // are content the student paid to have filed, so the reopened PDF has to show it.
            foreach (var item in report.Sections.SelectMany(section => section.Blocks)
                         .SelectMany(block => block.Items ?? Array.Empty<string>()).Take(6))
                if (!everyPage.Contains(Collapse(item), StringComparison.Ordinal))
                    violations.Add("pdf-list-item-missing");
            foreach (var untitled in report.Sections.Where(section => string.IsNullOrWhiteSpace(section.Heading))
                         .SelectMany(section => section.Blocks).Where(block => !string.IsNullOrWhiteSpace(block.Text)).Select(block => block.Text!).Take(3))
                if (!everyPage.Contains(Collapse(untitled), StringComparison.Ordinal))
                    violations.Add("pdf-untitled-section-content-missing");
        }
        catch (Exception exception)
        {
            violations.Add($"pdf-unopenable:{exception.GetType().Name}");
        }
        return new ValidationReport(violations.Count == 0, violations);
    }

    private static string Collapse(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());

    /// <summary>
    /// A page's prose with the running page number removed. The number is drawn in the margin, so it
    /// is not part of any sentence; but when a paragraph flows across a page break, a naive text dump
    /// splices it into the middle of that paragraph — "in none of those cases" became "in3none" and
    /// the integrity note looked absent from a document that printed it correctly. Only numerals that
    /// sit inside the header/footer band are dropped, so a number in the body stays visible to checks.
    /// </summary>
    private static string PageText(Page page)
    {
        var height = page.Height;
        // PdfPig reports word boxes in PDF coordinates: y grows upward from the bottom of the page, so
        // the running number sits at a SMALL top value even when it prints at the foot of the page.
        var band = Math.Min(100d, height * 0.12d);
        return string.Concat(page.GetWords()
            .Where(word =>
            {
                var top = (double)word.BoundingBox.Top;
                var inBand = top < band || top > height - band;
                return !(inBand && word.Text.All(character => char.IsDigit(character)));
            })
            .Select(word => word.Text + " "));
    }
}

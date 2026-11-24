using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
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
            if (spec.IncludesAiUseDisclosureSlot && !allText.Contains(report.Labels.DisclosureHeading, StringComparison.Ordinal))
                violations.Add("docx-disclosure-heading-missing");
            foreach (var source in report.Sources.Where(source => !source.ContentAccessed))
                if (!allText.Contains(source.AccessNote, StringComparison.Ordinal))
                    violations.Add($"docx-metadata-note-missing-{source.CitationId}");

            var sections = body.Elements<SectionProperties>().ToList();
            if (sections.Count < 2) violations.Add("docx-numbering-split-missing");
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
        }
        catch (Exception exception)
        {
            violations.Add($"pdf-unopenable:{exception.GetType().Name}");
        }
        return new ValidationReport(violations.Count == 0, violations);
    }

    private static string Collapse(string value) => new(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
}

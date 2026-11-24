using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using Taslim.Api.AcademicTemplates;

namespace Taslim.Api.AcademicExport;

/// <summary>
/// What "the file is real" means before an export is offered for download. This is not a byte-length
/// sniff: the artifact is re-opened through the format's own reader, and the content contract is
/// re-checked on the parsed result — the report title, EVERY citation marker, the bibliography
/// heading, the always-present disclosure heading and the integrity note must all be present in the
/// document that was actually written, not merely intended. Table citations are checked in table
/// adjacency context (finding #1 on a707938: a stray marker elsewhere must not excuse a dropped one),
/// and DOCX packages additionally run through the real OpenXML validator with a schema-legal
/// section-break shape asserted (finding #5 on a707938). An export that fails validation returns a
/// stable error instead of a broken file.
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

            // Real ECMA-376 validation, not just "the package reopens": the previous checks happily
            // accepted a body with an illegally placed mid-document sectPr (finding #5 on a707938).
            var openXmlViolations = new OpenXmlValidator().Validate(package).ToList();
            if (openXmlViolations.Count > 0)
                violations.Add($"docx-openxml-invalid:{openXmlViolations.Count}");

            var paragraphs = body.Descendants<Paragraph>().ToList();
            var texts = paragraphs.SelectMany(p => p.Descendants<Text>()).Select(t => t.Text).ToList();
            var allText = string.Join('\n', texts);

            if (paragraphs.Count < 2) violations.Add("docx-paragraph-count");
            if (!allText.Contains(report.Title, StringComparison.Ordinal)) violations.Add("docx-title-missing");
            if (!allText.Contains(spec.ConfigurationId, StringComparison.Ordinal)
                && !(package.PackageProperties.Keywords?.Contains(spec.ConfigurationId, StringComparison.Ordinal) ?? false))
                violations.Add("docx-template-identity-missing");

            // EVERY cited id must survive, not merely "some citation exists" (finding #1).
            foreach (var id in report.CitedCitationIds)
                if (!allText.Contains($"[{id}]", StringComparison.Ordinal))
                    violations.Add($"docx-citation-missing-{id}");
            foreach (var violation in DocxTableCitationViolations(body, report))
                violations.Add(violation);

            if (!allText.Contains(report.Labels.ReferencesHeading, StringComparison.Ordinal)) violations.Add("docx-bibliography-heading-missing");
            if (!allText.Contains(report.Labels.IntegrityNote, StringComparison.Ordinal)) violations.Add("docx-integrity-note-missing");
            // The disclosure is rendered under every design now, so the check is unconditional (finding #2).
            if (!allText.Contains(report.Labels.DisclosureHeading, StringComparison.Ordinal))
                violations.Add("docx-disclosure-heading-missing");
            foreach (var source in report.Sources.Where(source => !source.ContentAccessed))
                if (!allText.Contains(source.AccessNote, StringComparison.Ordinal))
                    violations.Add($"docx-metadata-note-missing-{source.CitationId}");

            // Legal section-break shape: exactly one body-level sectPr, as the last body child; the
            // front-matter/body numbering split lives in an embedded paragraph-level sectPr.
            var topLevelSections = body.Elements<SectionProperties>().ToList();
            if (topLevelSections.Count != 1 || body.LastChild is not SectionProperties)
                violations.Add("docx-final-sectpr-shape");
            if (!paragraphs.Any(paragraph => paragraph.ChildElements.OfType<ParagraphProperties>().Any(properties => properties.ChildElements.OfType<SectionProperties>().Any())))
                violations.Add("docx-numbering-split-missing");
        }
        catch (Exception exception)
        {
            violations.Add($"docx-unopenable:{exception.GetType().Name}");
        }
        return new ValidationReport(violations.Count == 0, violations);
    }

    /// <summary>
    /// For every table block that carries citation ids, at least one of those ids must appear as a
    /// marker in the table's own cells or in a paragraph directly adjacent to that table (the caption
    /// above or the citation line below the renderer emits). Presence of the same marker in unrelated
    /// body paragraphs does not excuse a dropped table citation.
    /// </summary>
    private static IEnumerable<string> DocxTableCitationViolations(Body body, AcademicSavedReport report)
    {
        var tables = body.Elements<Table>().ToList();
        if (tables.Count == 0) yield break;

        var tableContexts = tables.Select(table =>
        {
            var before = (table.PreviousSibling() as Paragraph)?.Descendants<Text>().Select(t => t.Text) ?? Enumerable.Empty<string>();
            var after = (table.NextSibling() as Paragraph)?.Descendants<Text>().Select(t => t.Text) ?? Enumerable.Empty<string>();
            var inside = table.Descendants<Text>().Select(t => t.Text);
            return string.Join(' ', before.Concat(after).Concat(inside));
        }).ToList();

        foreach (var block in report.AllBlocks.Where(block =>
                     string.Equals(block.Type, "table", StringComparison.OrdinalIgnoreCase)
                     && block.Rows is { Count: > 0 }
                     && block.CitationIds is { Count: > 0 }))
        {
            if (!block.CitationIds.All(id => tableContexts.Any(context => context.Contains($"[{id}]", StringComparison.Ordinal))))
                foreach (var id in block.CitationIds)
                    if (!tableContexts.Any(context => context.Contains($"[{id}]", StringComparison.Ordinal)))
                        yield return $"docx-table-citation-context-missing-{id}";
        }
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
            // containment checks ignore whitespace rather than pretending the extractor preserves it.
            var pages = Enumerable.Range(1, document.NumberOfPages).Select(number => document.GetPage(number).Text ?? string.Empty).ToList();
            var whole = Collapse(string.Concat(pages));

            if (!whole.Contains(Collapse(report.Title), StringComparison.Ordinal)) violations.Add("pdf-title-missing");
            // Content contract, mirroring the DOCX side: citations, disclosure, bibliography, notes.
            foreach (var id in report.CitedCitationIds)
                if (!whole.Contains(Collapse($"[{id}]"), StringComparison.Ordinal))
                    violations.Add($"pdf-citation-missing-{id}");
            if (!whole.Contains(Collapse(report.Labels.ReferencesHeading), StringComparison.Ordinal)) violations.Add("pdf-bibliography-heading-missing");
            if (!whole.Contains(Collapse(report.Labels.IntegrityNote), StringComparison.Ordinal)) violations.Add("pdf-integrity-note-missing");
            if (!whole.Contains(Collapse(report.Labels.DisclosureHeading), StringComparison.Ordinal)) violations.Add("pdf-disclosure-heading-missing");
            foreach (var source in report.Sources.Where(source => !source.ContentAccessed))
                if (!whole.Contains(Collapse(source.AccessNote), StringComparison.Ordinal))
                    violations.Add($"pdf-access-note-missing-{source.CitationId}");
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

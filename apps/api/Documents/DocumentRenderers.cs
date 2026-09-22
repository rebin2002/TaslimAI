using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Drawing;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Documents;

public sealed record RenderedDocument(string RepresentationType, string FileName, string ContentType, byte[] Content);

public interface IDocumentRenderer
{
    RenderedDocument RenderDocx(DocumentDraft draft, DocumentGenerationInput input, DocumentGenerationOptions options);
    RenderedDocument RenderPdf(DocumentDraft draft, DocumentGenerationInput input, DocumentGenerationOptions options);
}

public sealed class DocumentRenderer : IDocumentRenderer
{
    private static int questPdfConfigured;

    public DocumentRenderer()
    {
        if (Interlocked.Exchange(ref questPdfConfigured, 1) == 0)
        {
            QuestPDF.Settings.License = LicenseType.Community;
            var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansArabic-Regular.ttf");
            if (File.Exists(fontPath)) FontManager.RegisterFontFromFile(fontPath);
        }
    }

    public RenderedDocument RenderDocx(DocumentDraft draft, DocumentGenerationInput input, DocumentGenerationOptions options)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new WordprocessingDocumentModelBuilder(draft, input).Build();
            main.Document.Save();
        }
        return new RenderedDocument(AssetRepresentationTypes.Docx, SafeFileName(input.Title, ".docx"), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", stream.ToArray());
    }

    public RenderedDocument RenderPdf(DocumentDraft draft, DocumentGenerationInput input, DocumentGenerationOptions options)
    {
        var rtl = IsRtl(input.Language);
        var bytes = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontSize(10).FontFamily(options.DefaultFontFamily, options.RtlFontFamily));
                page.Header().Text(draft.Title).SemiBold().FontSize(20).FontColor(Colors.Blue.Darken2);
                var content = page.Content();
                var columnHost = rtl ? content.ContentFromRightToLeft() : content;
                columnHost.Column(column =>
                {
                    column.Spacing(10);
                    if (!string.IsNullOrWhiteSpace(draft.Summary)) column.Item().Text(draft.Summary).FontColor(Colors.Grey.Darken2);
                    if (input.IncludeTableOfContents)
                    {
                        column.Item().Text("Contents").Bold().FontSize(12);
                        foreach (var section in draft.Sections) column.Item().Text($"• {section.Heading}");
                    }
                    foreach (var section in draft.Sections) RenderSection(column, section, rtl);
                });
                page.Footer().AlignCenter().Text(text => { text.Span("Taslim · "); text.CurrentPageNumber(); });
            });
        }).GeneratePdf();
        return new RenderedDocument(AssetRepresentationTypes.Pdf, SafeFileName(input.Title, ".pdf"), "application/pdf", bytes);
    }

    private static void RenderSection(ColumnDescriptor column, DocumentSection section, bool rtl)
    {
        column.Item().PaddingTop(8).Text(section.Heading).Bold().FontSize(14).FontColor(Colors.Blue.Darken2);
        foreach (var block in section.Blocks)
        {
            switch (block.Type.ToLowerInvariant())
            {
                case DocumentBlockTypes.Heading:
                    column.Item().Text(block.Text ?? string.Empty).Bold().FontSize(12);
                    break;
                case DocumentBlockTypes.BulletList:
                    foreach (var item in block.Items ?? []) column.Item().Text($"• {item}");
                    break;
                case DocumentBlockTypes.NumberedList:
                    var index = 1;
                    foreach (var item in block.Items ?? []) column.Item().Text($"{index++}. {item}");
                    break;
                case DocumentBlockTypes.Table:
                    RenderTable(column, block.Rows ?? [], rtl);
                    break;
                default:
                    column.Item().Text(block.Text ?? string.Empty);
                    break;
            }
        }
    }

    private static void RenderTable(ColumnDescriptor column, IReadOnlyList<DocumentTableRow> rows, bool rtl)
    {
        if (rows.Count == 0) return;
        var columns = Math.Clamp(rows.Max(row => row.Cells.Count), 1, 8);
        column.Item().Table(table =>
        {
            table.ColumnsDefinition(definition =>
            {
                for (var index = 0; index < columns; index++) definition.RelativeColumn();
            });
            foreach (var row in rows)
            {
                for (var index = 0; index < columns; index++)
                {
                    var cell = index < row.Cells.Count ? row.Cells[index] : string.Empty;
                    table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(cell);
                }
            }
        });
    }

    private static bool IsRtl(string language) => language is "ar" or "ku";

    private static string SafeFileName(string title, string extension)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (safe.Length == 0) safe = "Taslim document";
        return (safe.Length > 120 ? safe[..120] : safe) + extension;
    }
}

internal sealed class WordprocessingDocumentModelBuilder(DocumentDraft draft, DocumentGenerationInput input)
{
    public DocumentFormat.OpenXml.Wordprocessing.Document Build()
    {
        var body = new Body();
        body.Append(Paragraph(draft.Title, "Title"));
        if (!string.IsNullOrWhiteSpace(draft.Summary)) body.Append(Paragraph(draft.Summary, null));
        if (input.IncludeTableOfContents)
        {
            body.Append(Paragraph("Contents", "Heading1"));
            foreach (var section in draft.Sections) body.Append(Paragraph($"• {section.Heading}", null));
        }
        foreach (var section in draft.Sections)
        {
            body.Append(Paragraph(section.Heading, "Heading1"));
            foreach (var block in section.Blocks) AppendBlock(body, block);
        }
        body.Append(new SectionProperties(
            new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = 11906, Height = 16838 },
            new PageMargin { Top = 1440, Bottom = 1440, Left = 1440, Right = 1440 }));
        return new DocumentFormat.OpenXml.Wordprocessing.Document(body);
    }

    private void AppendBlock(Body body, DocumentBlock block)
    {
        switch (block.Type.ToLowerInvariant())
        {
            case DocumentBlockTypes.Heading:
                body.Append(Paragraph(block.Text ?? string.Empty, "Heading2"));
                break;
            case DocumentBlockTypes.BulletList:
                foreach (var item in block.Items ?? []) body.Append(Paragraph($"• {item}", null));
                break;
            case DocumentBlockTypes.NumberedList:
                var index = 1;
                foreach (var item in block.Items ?? []) body.Append(Paragraph($"{index++}. {item}", null));
                break;
            case DocumentBlockTypes.Table:
                body.Append(Table(block.Rows ?? []));
                break;
            default:
                body.Append(Paragraph(block.Text ?? string.Empty, null));
                break;
        }
    }

    private Paragraph Paragraph(string text, string? style)
    {
        var properties = new ParagraphProperties();
        if (!string.Equals(input.Language, "en", StringComparison.OrdinalIgnoreCase) && input.Language is "ar" or "ku")
            properties.Append(new BiDi());
        if (!string.IsNullOrWhiteSpace(style)) properties.Append(new ParagraphStyleId { Val = style });
        return new Paragraph(properties, new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private Table Table(IReadOnlyList<DocumentTableRow> rows)
    {
        var table = new Table(new TableProperties(new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4 },
            new BottomBorder { Val = BorderValues.Single, Size = 4 },
            new LeftBorder { Val = BorderValues.Single, Size = 4 },
            new RightBorder { Val = BorderValues.Single, Size = 4 },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row.Cells) tableRow.Append(new TableCell(Paragraph(cell, null)));
            table.Append(tableRow);
        }
        return table;
    }
}

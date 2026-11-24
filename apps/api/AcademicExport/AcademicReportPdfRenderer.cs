using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Taslim.Api.AcademicTemplates;

namespace Taslim.Api.AcademicExport;

/// <summary>
/// QuestPDF counterpart of <see cref="AcademicReportDocxRenderer"/>: the saved research payload under
/// the selected design spec. Same honesty rules — real bibliography with per-row access-status notes,
/// an ALWAYS-visible disclosure section (regardless of whether the design carries a dedicated slot,
/// review finding #2 on a707938), preserved citation ids beside tables (review finding #1), rendering
/// of every saved section even when its heading is blank (review finding #4), and an integrity footer
/// under every design. Unlike the DOCX surface (whose contents list is a field Word fills on open),
/// the PDF carries real page numbers: every contents-listed heading registers a captured content
/// position and each contents row resolves that anchor at layout time, so no heading can ever be
/// attributed to a page it does not appear on.
/// </summary>
public static class AcademicReportPdfRenderer
{
    /// <summary>A heading that the contents list tracks, in rendering order.</summary>
    private sealed record PlannedHeading(string Title, string AnchorId);

    public static byte[] Render(AcademicDesignSpec spec, AcademicSavedReport report, string? studentDisclosure)
    {
        AcademicTemplatePdfRenderer.EnsureQuestPdfConfigured();
        return GenerateWithFontFallbacks(attempt =>
        {
            var bodyFont = Candidate(spec, spec.BodyFontToken, attempt);
            var headingFont = Candidate(spec, spec.HeadingFontToken, attempt);
            var captionFont = Candidate(spec, spec.CaptionFontToken, attempt);
            return Generate(spec, report, studentDisclosure, bodyFont, headingFont, captionFont);
        });
    }

    private static byte[] GenerateWithFontFallbacks(Func<int, byte[]> generate)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return generate(attempt);
            }
            catch (Exception exception) when (attempt < 3 && IsFontFamilyFailure(exception))
            {
                // step down the registered font ladder before surfacing a failure
            }
        }
        throw new InvalidOperationException("Academic report PDF could not be rendered for the selected design.");
    }

    private static bool IsFontFamilyFailure(Exception exception) =>
        exception.Message.Contains("font", StringComparison.OrdinalIgnoreCase)
        || exception.InnerException is not null && IsFontFamilyFailure(exception.InnerException);

    private static string Candidate(AcademicDesignSpec spec, string token, int attempt)
    {
        var candidates = AcademicFontTokens.Resolve(token).PdfFamilyCandidates;
        if (attempt < candidates.Count) return candidates[attempt];
        return spec.IsRtl ? "Noto Sans Arabic" : "Default";
    }

    private static byte[] Generate(AcademicDesignSpec spec, AcademicSavedReport report, string? studentDisclosure,
        string bodyFont, string headingFont, string captionFont)
    {
        var (marginLeft, marginRight) = spec.MirroredSideMargins();
        var textColor = Hex(spec.TextColorHex);
        var accentColor = Hex(spec.AccentColorHex);
        var labels = report.Labels;

        // The contents plan is computed before layout so the contents rows and the body headings
        // agree on anchor identity by construction. Only non-blank headings are listed; a saved
        // section with a blank heading is still rendered in the body (finding #4), just not tracked.
        var headings = new List<PlannedHeading>();
        var hasExec = !string.IsNullOrWhiteSpace(report.ExecutiveSummary);
        var hasKeyFindings = report.KeyFindings.Count > 0;
        var hasConclusion = !string.IsNullOrWhiteSpace(report.Conclusion);
        if (hasExec) headings.Add(new PlannedHeading(labels.ExecutiveSummaryHeading, "academic-toc-0"));
        if (hasKeyFindings) headings.Add(new PlannedHeading(labels.KeyFindingsHeading, $"academic-toc-{headings.Count}"));
        foreach (var section in report.Sections)
            if (!string.IsNullOrWhiteSpace(section.Heading))
                headings.Add(new PlannedHeading(section.Heading, $"academic-toc-{headings.Count}"));
        if (hasConclusion) headings.Add(new PlannedHeading(labels.ConclusionHeading, $"academic-toc-{headings.Count}"));
        headings.Add(new PlannedHeading(labels.ReferencesHeading, $"academic-toc-{headings.Count}"));
        var cursor = 0;
        string? NextAnchor() => spec.IncludeToc ? headings[cursor++].AnchorId : null;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizeFor(spec));
                page.MarginTop((float)spec.MarginTopPt);
                page.MarginBottom((float)spec.MarginBottomPt);
                page.MarginLeft((float)marginLeft);
                page.MarginRight((float)marginRight);
                page.DefaultTextStyle(style => style
                    .FontSize((float)spec.BodySizePt)
                    .FontFamily(bodyFont)
                    .FontColor(textColor)
                    .LineHeight((float)spec.LineSpacing));

                if (!string.Equals(spec.RunningHeadContent, "none", StringComparison.Ordinal))
                {
                    page.Header().Column(header =>
                        header.Item().Text(report.Title).FontSize((float)(spec.BodySizePt * 0.8)).FontFamily(captionFont));
                }

                if (!string.Equals(spec.PageNumberPosition, "none", StringComparison.Ordinal))
                {
                    page.Footer().Row(footer =>
                    {
                        var align = spec.PageNumberPhysicalAlignment();
                        if (align != "left") footer.RelativeItem();
                        footer.ConstantItem(64).AlignCenter().Text(text =>
                        {
                            text.DefaultTextStyle(style => style.FontSize((float)(spec.BodySizePt * 0.85)).FontColor(textColor));
                            text.CurrentPageNumber();
                        });
                        if (align != "right") footer.RelativeItem();
                    });
                }

                var content = spec.IsRtl ? page.Content().ContentFromRightToLeft() : page.Content();
                content.Column(column =>
                {
                    column.Spacing((float)spec.ParagraphAfterPt);

                    // ── front matter ──
                    if (!string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal))
                    {
                        column.Item().PaddingVertical(48).AlignCenter().Text(text =>
                        {
                            text.DefaultTextStyle(style =>
                            {
                                var withFont = style.FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).FontColor(textColor);
                                return spec.HeadingsBold ? withFont.Bold() : withFont;
                            });
                            text.Span(spec.H1Uppercase ? report.Title.ToUpperInvariant() : report.Title);
                        });
                        if (!string.IsNullOrWhiteSpace(report.Subtitle))
                            column.Item().AlignCenter().Text(report.Subtitle!);
                        column.Item().AlignCenter().Text($"Saved {report.GeneratedAtUtc:yyyy-MM-dd}");
                        column.Item().PageBreak();
                    }

                    if (spec.IncludesApprovalPage)
                    {
                        foreach (var role in new[] { "Supervisor", "Examiner" })
                        {
                            column.Item().Text($"{role} name: ______________________________");
                            column.Item().Text($"{role} signature: ________________________  Date: ____________");
                        }
                        column.Item().PageBreak();
                    }

                    // ── disclosure: honesty invariant, rendered under EVERY design (finding #2) ──
                    // Designs with a dedicated slot keep their traditional page-break treatment; designs
                    // without one (e.g. literature-review-apa) still receive the disclosure inline, so a
                    // supplied student disclosure can never be silently dropped.
                    column.Item().PaddingTop((float)spec.HeadingTopGapPt).Text(labels.DisclosureHeading)
                        .FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(2)).Bold();
                    var disclosureCompleted = !string.IsNullOrWhiteSpace(studentDisclosure);
                    column.Item().Text(disclosureCompleted ? studentDisclosure!.Trim() : labels.DisclosureMissing);
                    if (!disclosureCompleted)
                        column.Item().Text("This export leaves the disclosure visible and incomplete rather than removing it.")
                            .Italic().FontSize((float)(spec.BodySizePt * 0.9));
                    if (spec.IncludesAiUseDisclosureSlot)
                        column.Item().PageBreak();

                    if (spec.IncludeToc)
                    {
                        column.Item().Text(labels.ContentsHeading).FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).Bold();
                        foreach (var entry in headings)
                        {
                            column.Item().Row(row =>
                            {
                                row.RelativeItem().Text(entry.Title);
                                row.ConstantItem(52).AlignRight().Text(text => text.BeginPageNumberOfSection(entry.AnchorId));
                            });
                        }
                        column.Item().PageBreak();
                    }

                    // ── body: EVERY saved section renders; blank headings are skipped as headings
                    // only, never as containers (finding #4) ──
                    // Cover-less designs (e.g. literature-review-apa) still carry the report title at
                    // the head of the body: an export that fails its own integrity gate must not ship
                    // nameless.
                    if (string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal))
                        column.Item().AlignCenter().Text(report.Title)
                            .FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).Bold();
                    if (hasExec)
                    {
                        Heading(column, labels.ExecutiveSummaryHeading, spec, headingFont, NextAnchor());
                        column.Item().Text(report.ExecutiveSummary);
                    }
                    if (hasKeyFindings)
                    {
                        Heading(column, labels.KeyFindingsHeading, spec, headingFont, NextAnchor());
                        foreach (var block in report.KeyFindings)
                            ComposeBlock(column, block, spec, accentColor);
                    }
                    foreach (var section in report.Sections)
                    {
                        if (!string.IsNullOrWhiteSpace(section.Heading))
                            Heading(column, section.Heading, spec, headingFont, NextAnchor());
                        foreach (var block in section.Blocks)
                            ComposeBlock(column, block, spec, accentColor);
                    }
                    if (hasConclusion)
                    {
                        Heading(column, labels.ConclusionHeading, spec, headingFont, NextAnchor());
                        column.Item().Text(report.Conclusion);
                    }

                    // ── bibliography from persisted sources, honest per-row access status ──
                    Heading(column, labels.ReferencesHeading, spec, headingFont, NextAnchor());
                    foreach (var source in report.Sources)
                    {
                        column.Item()
                            .PaddingLeft((float)(!spec.IsRtl && spec.BibliographyHangingIndent ? spec.BibliographyIndentPt : 0))
                            .PaddingRight((float)(spec.IsRtl && spec.BibliographyHangingIndent ? spec.BibliographyIndentPt : 0))
                            .Text(AcademicReportDocxRenderer.BibliographyLine(source))
                            .LineHeight((float)Math.Min(spec.LineSpacing, 1.3));
                        if (!source.ContentAccessed)
                            column.Item()
                                .PaddingLeft((float)(!spec.IsRtl && spec.BibliographyHangingIndent ? spec.BibliographyIndentPt : 0))
                                .PaddingRight((float)(spec.IsRtl && spec.BibliographyHangingIndent ? spec.BibliographyIndentPt : 0))
                                .Text(source.AccessNote)
                                .Italic().FontSize((float)(spec.BodySizePt * 0.85));
                    }

                    // ── integrity footer: never hidden by a template choice ──
                    column.Item().PaddingTop(18).LineHorizontal(0.6f).LineColor(accentColor);
                    column.Item().Text(labels.IntegrityNote).Italic().FontSize((float)(spec.BodySizePt * 0.9));
                });
            });
        }).GeneratePdf();
    }

    private static void Heading(ColumnDescriptor column, string heading, AcademicDesignSpec spec, string headingFont, string? anchorId)
    {
        var item = column.Item();
        if (anchorId is not null)
            item = item.Section(anchorId);
        var text = item.PaddingTop((float)spec.HeadingTopGapPt)
            .Text(heading)
            .FontFamily(headingFont)
            .FontSize((float)spec.HeadingSizePt(1));
        if (spec.HeadingsBold) text.Bold();
    }

    /// <summary>Renders one saved report block as its own set of column items. Takes the
    /// <see cref="ColumnDescriptor"/> rather than a single container so every paragraph, list item,
    /// caption and table is a distinct element: QuestPDF's fluent surface mutates one container per
    /// call chain, and re-calling Text(...) on the same item container for multi-item blocks risked
    /// dropping all but one element (review finding #5 source-level note on a707938).</summary>
    private static void ComposeBlock(ColumnDescriptor column, AcademicReportBlockModel block, AcademicDesignSpec spec, string accentColor)
    {
        switch (block.Type.ToLowerInvariant())
        {
            case "bullets":
                foreach (var item in block.Items ?? Array.Empty<string>())
                    column.Item().PaddingLeft((float)(spec.IsRtl ? 0 : 14)).PaddingRight((float)(spec.IsRtl ? 14 : 0))
                        .Text("•  " + AcademicReportDocxRenderer.WithCitations(item, block.CitationIds));
                break;
            case "numbered_list":
                var number = 1;
                foreach (var item in block.Items ?? Array.Empty<string>())
                    column.Item().PaddingLeft((float)(spec.IsRtl ? 0 : 14)).PaddingRight((float)(spec.IsRtl ? 14 : 0))
                        .Text($"{number++}. {AcademicReportDocxRenderer.WithCitations(item, block.CitationIds)}");
                break;
            case "table" when block.Rows is { Count: > 0 }:
                // Finding #1 fix: the table block's caption text and citation ids used to be dropped.
                // A caption renders above the table with the same "[S1]" append shape paragraphs use;
                // with no caption text, a citation line follows the table so the ids survive in
                // correct table context.
                if (!string.IsNullOrWhiteSpace(block.Text))
                    column.Item().Text(AcademicReportDocxRenderer.WithCitations(block.Text, block.CitationIds)).Italic();
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        for (var index = 0; index < block.Rows[0].Count; index++)
                            columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var cell in block.Rows[0])
                            header.Cell().Background(accentColor).Padding(4).Text(cell).FontSize((float)(spec.BodySizePt * 0.9));
                    });
                    foreach (var row in block.Rows.Skip(1))
                        foreach (var cell in row)
                            table.Cell().BorderTop(0.5f).BorderColor("#999999").Padding(4).Text(cell);
                });
                if (string.IsNullOrWhiteSpace(block.Text) && block.CitationIds is { Count: > 0 })
                    column.Item().PaddingLeft((float)(spec.IsRtl ? 0 : 14)).PaddingRight((float)(spec.IsRtl ? 14 : 0))
                        .Text(AcademicReportDocxRenderer.CitationOnlyLine(block.CitationIds)).Italic();
                break;
            default:
                var text = AcademicReportDocxRenderer.WithCitations(block.Text, block.CitationIds);
                if (!string.IsNullOrWhiteSpace(text)) column.Item().Text(text);
                break;
        }
    }

    private static PageSize PageSizeFor(AcademicDesignSpec spec) => spec.PaperFormat.ToLowerInvariant() switch
    {
        "letter" => PageSizes.Letter,
        "a5" => PageSizes.A5,
        _ => PageSizes.A4,
    };

    private static string Hex(string color) => color.StartsWith("#", StringComparison.Ordinal) ? color : $"#{color}";
}

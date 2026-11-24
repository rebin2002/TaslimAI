using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Taslim.Api.AcademicTemplates;

namespace Taslim.Api.AcademicExport;

/// <summary>
/// Renders a real saved research report (<see cref="AcademicSavedReport"/>) under a template design
/// spec. This is the integration-lane adoption of the same spec→wordprocessing mapping proven in the
/// template library (AcademicTemplateDocxRenderer): identical section geometry, style ladder, RTL
/// bidi/gutter behaviour, front-matter/body numbering split and hanging-indent bibliography, but the
/// content is the student's own stored payload, citations included.
///
/// Honest by construction:
/// - the bibliography comes from the job's persisted sources, with an explicit "catalogue record
///   only" note on every row whose full text was never accessed;
/// - the generative-AI disclosure slot is always rendered when the design has one, completed by the
///   student's own text or failing closed to a "not completed" statement — the export can never
///   silently drop the disclosure;
/// - a research-integrity footer is rendered even under designs without a slot;
/// - no synthetic sample text is reachable from this renderer.
/// </summary>
public static class AcademicReportDocxRenderer
{
    public static byte[] Render(AcademicDesignSpec spec, AcademicSavedReport report, string? studentDisclosure)
    {
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = package.AddMainDocumentPart();
            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = BuildStyles(spec);
            stylesPart.Styles.Save();

            // Word must offer to update the TOC field on open so the contents list can carry real
            // page numbers; a static list would either lie about pagination or omit page numbers.
            var settingsPart = main.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
            settingsPart.Settings.Save();

            main.Document = BuildDocument(spec, report, studentDisclosure);
            main.Document.Save();

            var properties = package.PackageProperties;
            properties.Creator = "Taslim Academic Research Studio";
            properties.LastModifiedBy = "Taslim Academic Research Studio";
            properties.Title = report.Title;
            properties.Subject = "Templated re-render of a saved research report";
            properties.Keywords = $"{spec.TemplateId};{spec.ConfigurationId}";
            properties.Description = report.Labels.IntegrityNote;
            properties.Category = "academic-export";
        }
        return stream.ToArray();
    }

    private static Document BuildDocument(AcademicDesignSpec spec, AcademicSavedReport report, string? studentDisclosure)
    {
        var body = new Body();
        var labels = report.Labels;

        // ── front matter ────────────────────────────────────────────────────────────────
        if (!string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal))
        {
            body.Append(Paragraph(report.Title, null, spec, centered: true, sizePt: spec.HeadingSizePt(1), bold: spec.HeadingsBold));
            if (!string.IsNullOrWhiteSpace(report.Subtitle))
                body.Append(Paragraph(report.Subtitle!, null, spec, centered: true));
            body.Append(Paragraph($"Saved {report.GeneratedAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", null, spec, centered: true));
            body.Append(PageBreak());
        }

        if (spec.IncludesApprovalPage)
        {
            foreach (var role in new[] { "Supervisor", "Examiner" })
            {
                body.Append(Paragraph($"{role} name: ______________________________", null, spec));
                body.Append(Paragraph($"{role} signature: ________________________  Date: ____________", null, spec));
            }
            body.Append(PageBreak());
        }

        if (spec.IncludesAiUseDisclosureSlot)
        {
            body.Append(Paragraph(labels.DisclosureHeading, "Heading2", spec));
            var completed = !string.IsNullOrWhiteSpace(studentDisclosure);
            body.Append(Paragraph(completed ? studentDisclosure!.Trim() : labels.DisclosureMissing, null, spec));
            if (!completed)
                body.Append(Paragraph("This export leaves the disclosure visible and incomplete rather than removing it.", null, spec, italic: true));
        }

        if (spec.IncludeToc)
        {
            body.Append(Paragraph(labels.ContentsHeading, "Heading1", spec));
            body.Append(TocField(spec));
            body.Append(PageBreak());
        }
        body.Append(SectionPropertiesFor(spec, frontMatter: true));

        // ── body: the saved report, verbatim content, citations preserved ──────────────
        if (!string.IsNullOrWhiteSpace(report.ExecutiveSummary))
        {
            body.Append(Paragraph(labels.ExecutiveSummaryHeading, "Heading1", spec));
            body.Append(Paragraph(WithCitations(report.ExecutiveSummary, Array.Empty<string>()), null, spec));
        }
        if (report.KeyFindings.Count > 0)
        {
            body.Append(Paragraph(labels.KeyFindingsHeading, "Heading1", spec));
            foreach (var block in report.KeyFindings)
                foreach (var element in RenderBlock(block, spec))
                    body.Append(element);
        }
        foreach (var section in report.Sections)
        {
            if (!string.IsNullOrWhiteSpace(section.Heading))
                body.Append(Paragraph(section.Heading, "Heading1", spec));
            foreach (var block in section.Blocks)
                foreach (var element in RenderBlock(block, spec))
                    body.Append(element);
        }
        if (!string.IsNullOrWhiteSpace(report.Conclusion))
        {
            body.Append(Paragraph(labels.ConclusionHeading, "Heading1", spec));
            body.Append(Paragraph(report.Conclusion, null, spec));
        }

        // ── bibliography from persisted sources ─────────────────────────────────────────
        body.Append(Paragraph(labels.ReferencesHeading, "Heading1", spec));
        foreach (var source in report.Sources)
        {
            body.Append(Paragraph(BibliographyLine(source), "Bibliography", spec));
            if (!source.ContentAccessed)
                body.Append(Paragraph(source.AccessNote, null, spec, italic: true, indentLeftPt: spec.IsRtl ? 0 : spec.BibliographyIndentPt));
        }

        // Integrity footer: never hidden, regardless of template family.
        body.Append(Paragraph(labels.IntegrityNote, null, spec, italic: true));

        body.Append(SectionPropertiesFor(spec, frontMatter: false));
        return new Document(body);
    }

    public static string BibliographyLine(AcademicReportSourceRow source)
    {
        var parts = new List<string> { $"[{source.CitationId}] {source.Title}" };
        var tail = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.Publisher)) tail.Add(source.Publisher!);
        if (!string.IsNullOrWhiteSpace(source.PublishedYear)) tail.Add($"({source.PublishedYear})");
        if (tail.Count > 0) parts.Add(string.Join(' ', tail));
        if (!string.IsNullOrWhiteSpace(source.Url)) parts.Add(source.Url!);
        return string.Join(". ", parts);
    }

    private static IEnumerable<OpenXmlElement> RenderBlock(AcademicReportBlockModel block, AcademicDesignSpec spec)
    {
        switch (block.Type.ToLowerInvariant())
        {
            case "bullets":
                foreach (var item in block.Items ?? Array.Empty<string>())
                    yield return Paragraph("•  " + WithCitations(item, block.CitationIds), null, spec, indentLeftPt: spec.IsRtl ? 0 : 18, indentRightPt: spec.IsRtl ? 18 : 0);
                break;
            case "numbered_list":
                var number = 1;
                foreach (var item in block.Items ?? Array.Empty<string>())
                {
                    yield return Paragraph($"{number++}.  {WithCitations(item, block.CitationIds)}", null, spec, indentLeftPt: spec.IsRtl ? 0 : 18, indentRightPt: spec.IsRtl ? 18 : 0);
                }
                break;
            case "table":
                if (block.Rows is { Count: > 0 })
                    yield return BuildTable(spec, block.Rows);
                else
                    yield return Paragraph(WithCitations(block.Text, block.CitationIds), null, spec);
                break;
            default:
                var text = WithCitations(block.Text, block.CitationIds);
                if (!string.IsNullOrWhiteSpace(text))
                    yield return Paragraph(text, null, spec);
                break;
        }
    }

    /// <summary>Same citation append shape the report's own document renderer used when the job first
    /// produced files, so a templated export shows the identical "[S1]" markers a reader saw before.</summary>
    public static string WithCitations(string? text, IReadOnlyList<string> citations)
        => string.IsNullOrWhiteSpace(text) ? string.Empty
            : citations is null || citations.Count == 0 ? text
                : $"{text.Trim()} {string.Join(' ', citations.Select(citation => $"[{citation}]"))}";

    private static Paragraph TocField(AcademicDesignSpec spec)
    {
        var properties = new ParagraphProperties();
        if (spec.IsRtl) properties.Append(new BiDi());
        properties.Append(new ParagraphStyleId { Val = "TOCEntry" });
        var instruction = $"TOC \\o \"1-{Math.Max(1, spec.HeadingLevels)}\" \\h \\z \\u";
        var field = new SimpleField { Instruction = instruction };
        field.Append(new Run(new Text("(Open in Word and accept the field update to populate the contents list.)") { Space = SpaceProcessingModeValues.Preserve }));
        return new Paragraph(properties, field);
    }

    private static Styles BuildStyles(AcademicDesignSpec spec)
    {
        var bodyFont = Font(spec.BodyFontToken);
        var headingFont = Font(spec.HeadingFontToken);
        var captionFont = Font(spec.CaptionFontToken);
        var lineTwips = (int)Math.Round(spec.LineSpacing * 240);
        var lineTwipsS = lineTwips.ToString(CultureInfo.InvariantCulture);
        var styles = new Styles();

        styles.Append(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = bodyFont, HighAnsi = bodyFont, EastAsia = bodyFont, ComplexScript = bodyFont },
                    new FontSize { Val = HalfPoints(spec.BodySizePt) },
                    new FontSizeComplexScript { Val = HalfPoints(spec.BodySizePt) })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = lineTwipsS, LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(new Style(
            new StyleName { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines { After = TwipsI(spec.ParagraphAfterPt), Line = lineTwipsS, LineRule = LineSpacingRuleValues.Auto },
                spec.JustifyBody ? new Justification { Val = JustificationValues.Both } : null!,
                spec.FirstLineIndentPt > 0 ? new Indentation { FirstLine = TwipsI(spec.FirstLineIndentPt) } : null!))
        { StyleId = "Normal", Type = StyleValues.Paragraph, Default = OnOffValue.FromBoolean(true) });

        for (var level = 1; level <= Math.Max(1, spec.HeadingLevels); level++)
        {
            var id = $"Heading{level}";
            styles.Append(new Style(
                new StyleName { Val = $"heading {level}" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new StyleParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = TwipsI(spec.HeadingTopGapPt), After = TwipsI(spec.ParagraphAfterPt / 2) },
                    new Indentation { FirstLine = "0" },
                    new OutlineLevel { Val = level - 1 }),
                new StyleRunProperties(
                    new RunFonts { Ascii = headingFont, HighAnsi = headingFont, EastAsia = headingFont, ComplexScript = headingFont },
                    new FontSize { Val = HalfPoints(spec.HeadingSizePt(level)) },
                    new FontSizeComplexScript { Val = HalfPoints(spec.HeadingSizePt(level)) },
                    spec.HeadingsBold ? new Bold() : null!,
                    spec.HeadingsBold ? new BoldComplexScript() : null!,
                    level == 1 && spec.H1Uppercase ? new Caps() : null!))
            { StyleId = id, Type = StyleValues.Paragraph });
        }

        styles.Append(new Style(
            new StyleName { Val = "toc 1" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new Tabs(new TabStop { Val = TabStopValues.Right, Position = TwipsN(spec.PaperWidthPt - spec.MarginLeftPt - spec.MarginRightPt) }),
                new SpacingBetweenLines { After = "40" }))
        { StyleId = "TOCEntry", Type = StyleValues.Paragraph });

        styles.Append(new Style(
            new StyleName { Val = "Bibliography" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                new SpacingBetweenLines { After = "80" },
                spec.BibliographyHangingIndent
                    ? new Indentation { Left = TwipsI(spec.BibliographyIndentPt), Hanging = TwipsI(spec.BibliographyIndentPt) }
                    : new Indentation { Left = "0" }))
        { StyleId = "Bibliography", Type = StyleValues.Paragraph });

        return styles;
    }

    private static Paragraph PageBreak() => new(new Run(new Break { Type = BreakValues.Page }));

    private static Paragraph Paragraph(string text, string? style, AcademicDesignSpec spec,
        bool centered = false, double? sizePt = null, bool bold = false, bool italic = false,
        double indentLeftPt = 0, double indentRightPt = 0)
    {
        var properties = new ParagraphProperties();
        if (spec.IsRtl) properties.Append(new BiDi());
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (centered) properties.Append(new Justification { Val = JustificationValues.Center });
        if (indentLeftPt > 0 || indentRightPt > 0)
            properties.Append(new Indentation
            {
                Left = indentLeftPt > 0 ? TwipsI(indentLeftPt) : null!,
                Right = indentRightPt > 0 ? TwipsI(indentRightPt) : null!,
            });
        var run = new Run();
        if (sizePt is not null || bold || italic)
            run.RunProperties = new RunProperties(
                sizePt is not null ? new FontSize { Val = HalfPoints(sizePt.Value) } : null!,
                sizePt is not null ? new FontSizeComplexScript { Val = HalfPoints(sizePt.Value) } : null!,
                bold ? new Bold() : null!,
                bold ? new BoldComplexScript() : null!,
                italic ? new Italic() : null!,
                italic ? new ItalicComplexScript() : null!);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static Table BuildTable(AcademicDesignSpec spec, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        foreach (var (rowIndex, row) in rows.Select((row, index) => (index, row)))
        {
            var tableRow = new TableRow();
            if (rowIndex == 0) tableRow.Append(new TableRowProperties(new TableHeader()));
            foreach (var cell in row)
                tableRow.Append(new TableCell(Paragraph(cell, null, spec)));
            table.Append(tableRow);
        }
        return table;
    }

    private static SectionProperties SectionPropertiesFor(AcademicDesignSpec spec, bool frontMatter)
    {
        var properties = new SectionProperties();
        var scheme = frontMatter ? spec.FrontMatterNumbering : spec.BodyNumbering;
        if (!string.Equals(scheme, "none", StringComparison.Ordinal))
            properties.Append(new PageNumberType
            {
                Format = PageNumberFormat(scheme),
                Start = frontMatter || spec.RestartNumbersAfterFrontMatter ? new Int32Value(1) : null,
            });
        properties.Append(new PageSize
        {
            Width = new UInt32Value((uint)Math.Round(spec.PaperWidthPt * AcademicDesignSpec.TwipsPerPoint)),
            Height = new UInt32Value((uint)Math.Round(spec.PaperHeightPt * AcademicDesignSpec.TwipsPerPoint)),
            Orient = PageOrientationValues.Portrait,
        });
        properties.Append(new PageMargin
        {
            Top = TwipsN(spec.MarginTopPt),
            Bottom = TwipsN(spec.MarginBottomPt),
            Left = new UInt32Value((uint)Math.Max(0, TwipsN(spec.MarginLeftPt))),
            Right = new UInt32Value((uint)Math.Max(0, TwipsN(spec.MarginRightPt))),
            Gutter = new UInt32Value((uint)Math.Max(0, TwipsN(spec.BindingEdgePt))),
        });
        if (spec.IsRtl) properties.Append(new BiDi());
        if (spec.IsRtl && string.Equals(spec.BindingEdge, "right", StringComparison.Ordinal)) properties.Append(new GutterOnRight());
        if (spec.ColumnCount > 1)
        {
            var columnWidth = (uint)Math.Round((spec.PaperWidthPt - spec.MarginLeftPt - spec.MarginRightPt - (spec.ColumnCount - 1) * spec.ColumnGapPt) / spec.ColumnCount * AcademicDesignSpec.TwipsPerPoint);
            properties.Append(new Columns(new Column
            {
                Width = columnWidth.ToString(CultureInfo.InvariantCulture),
                Space = TwipsI(spec.ColumnGapPt),
            })
            { ColumnCount = (short)spec.ColumnCount, EqualWidth = OnOffValue.FromBoolean(true) });
        }
        else
        {
            properties.Append(new Columns());
        }
        return properties;
    }

    private static NumberFormatValues PageNumberFormat(string scheme) => scheme switch
    {
        "roman-lower" => NumberFormatValues.LowerRoman,
        "roman-upper" => NumberFormatValues.UpperRoman,
        _ => NumberFormatValues.Decimal,
    };

    private static string TwipsI(double points) => ((int)Math.Round(points * AcademicDesignSpec.TwipsPerPoint)).ToString(CultureInfo.InvariantCulture);
    private static int TwipsN(double points) => (int)Math.Round(points * AcademicDesignSpec.TwipsPerPoint);
    private static string HalfPoints(double points) => ((int)Math.Round(points * 2)).ToString(CultureInfo.InvariantCulture);
    private static string Font(string token) => AcademicFontTokens.Resolve(token).DocxFamily;
}

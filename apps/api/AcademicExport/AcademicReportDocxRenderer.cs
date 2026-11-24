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
/// - the bibliography comes from the job's persisted sources, with an explicit access-status note on
///   every row whose full text was never verifiably read in this workspace;
/// - the generative-AI disclosure is ALWAYS rendered, whether or not the design carries a dedicated
///   slot, completed by the student's own text or failing closed to a "not completed" statement —
///   the export can never silently drop the disclosure (review finding #2 on a707938);
/// - every block's citation ids survive, including table blocks, whose ids render beside the table in
///   correct context (review finding #1 on a707938);
/// - a research-integrity footer is rendered under every design;
/// - the section split is legal WordprocessingML: the non-final front-matter sectPr lives inside a
///   paragraph's properties, only the final sectPr is a direct body child (review finding #5).
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

        // The disclosure is a honesty invariant, not a design decoration: designs without a slot
        // still render it (finding #2: literature-review-apa has no slot, yet the controller
        // documents "this path can never hide the disclosure").
        body.Append(Paragraph(labels.DisclosureHeading, "Heading2", spec));
        var disclosureCompleted = !string.IsNullOrWhiteSpace(studentDisclosure);
        body.Append(Paragraph(disclosureCompleted ? studentDisclosure!.Trim() : labels.DisclosureMissing, null, spec));
        if (!disclosureCompleted)
            body.Append(Paragraph("This export leaves the disclosure visible and incomplete rather than removing it.", null, spec, italic: true));

        if (spec.IncludeToc)
        {
            body.Append(Paragraph(labels.ContentsHeading, "Heading1", spec));
            body.Append(TocField(spec));
            body.Append(PageBreak());
        }
        // WordprocessingML rule: a non-final section break belongs inside the last paragraph of the
        // section it closes; only the document-final sectPr may be a direct child of <w:body>.
        body.Append(SectionBreakParagraph(SectionPropertiesFor(spec, frontMatter: true)));

        // ── body: the saved report, verbatim content, citations preserved ──────────────
        // Designs without a cover page (e.g. literature-review-apa) must still carry the report title:
        // the validator contract says every export contains its title, and an untitled document would
        // fail its own integrity gate rather than ship nameless.
        if (string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal))
            body.Append(Paragraph(report.Title, null, spec, centered: true, sizePt: spec.HeadingSizePt(1), bold: spec.HeadingsBold));
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
                {
                    // Finding #1 fix: a table block's own Text (a caption/lead-in) and its CitationIds
                    // used to be dropped entirely. The caption renders above the table with the same
                    // "[S1]" append shape paragraphs use; with no caption text, the citation line
                    // renders directly after the table so the ids always survive in table context.
                    if (!string.IsNullOrWhiteSpace(block.Text))
                        yield return Paragraph(WithCitations(block.Text, block.CitationIds), null, spec, italic: true);
                    yield return BuildTable(spec, block.Rows);
                    if (string.IsNullOrWhiteSpace(block.Text) && block.CitationIds is { Count: > 0 })
                        yield return Paragraph(CitationOnlyLine(block.CitationIds), null, spec, italic: true, indentLeftPt: spec.IsRtl ? 0 : 18, indentRightPt: spec.IsRtl ? 18 : 0);
                }
                else
                {
                    yield return Paragraph(WithCitations(block.Text, block.CitationIds), null, spec);
                }
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

    /// <summary>Citation markers alone, for a table whose block carries ids but no caption text.</summary>
    public static string CitationOnlyLine(IReadOnlyList<string> citations)
        => string.Join(' ', citations.Select(citation => $"[{citation}]"));

    private static Paragraph TocField(AcademicDesignSpec spec)
    {
        var properties = new ParagraphProperties();
        properties.Append(new ParagraphStyleId { Val = "TOCEntry" });
        if (spec.IsRtl) properties.Append(new BiDi());
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
            BuildNormalParagraphProperties(spec, lineTwipsS)
        )
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
                BuildHeadingRunProperties(headingFont, spec, level))
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

    // ECMA-376 defines a strict child order inside CT_PPr (spacing, then ind, then jc) and CT_RPr
    // (rFonts, b/bCs, i/iCs, caps, then sz/szCs). Elements must be appended in schema order or
    // WordprocessingML validators reject them; conditional children are appended, never passed as
    // null placeholders, because a null attribute assignment survives as an empty attribute and
    // empty ST_SignedTwipsMeasure values are themselves schema violations.
    private static StyleParagraphProperties BuildNormalParagraphProperties(AcademicDesignSpec spec, string lineTwipsS)
    {
        var properties = new StyleParagraphProperties(
            new SpacingBetweenLines { After = TwipsI(spec.ParagraphAfterPt), Line = lineTwipsS, LineRule = LineSpacingRuleValues.Auto });
        if (spec.FirstLineIndentPt > 0) properties.Append(new Indentation { FirstLine = TwipsI(spec.FirstLineIndentPt) });
        if (spec.JustifyBody) properties.Append(new Justification { Val = JustificationValues.Both });
        return properties;
    }

    private static StyleRunProperties BuildHeadingRunProperties(string headingFont, AcademicDesignSpec spec, int level)
    {
        var properties = new StyleRunProperties();
        properties.Append(new RunFonts { Ascii = headingFont, HighAnsi = headingFont, EastAsia = headingFont, ComplexScript = headingFont });
        if (spec.HeadingsBold)
        {
            properties.Append(new Bold());
            properties.Append(new BoldComplexScript());
        }
        if (level == 1 && spec.H1Uppercase) properties.Append(new Caps());
        properties.Append(new FontSize { Val = HalfPoints(spec.HeadingSizePt(level)) });
        properties.Append(new FontSizeComplexScript { Val = HalfPoints(spec.HeadingSizePt(level)) });
        return properties;
    }

    private static Paragraph PageBreak() => new(new Run(new Break { Type = BreakValues.Page }));

    /// <summary>Closes a section: the sectPr rides inside the paragraph's properties, which is the
    /// only schema-legal home for a non-final section break.</summary>
    private static Paragraph SectionBreakParagraph(SectionProperties properties)
        => new(new ParagraphProperties(properties));

    private static Paragraph Paragraph(string text, string? style, AcademicDesignSpec spec,
        bool centered = false, double? sizePt = null, bool bold = false, bool italic = false,
        double indentLeftPt = 0, double indentRightPt = 0)
    {
        var properties = new ParagraphProperties();
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (spec.IsRtl) properties.Append(new BiDi());
        if (indentLeftPt > 0 || indentRightPt > 0)
        {
            var indentation = new Indentation();
            if (indentLeftPt > 0) indentation.Left = TwipsI(indentLeftPt);
            if (indentRightPt > 0) indentation.Right = TwipsI(indentRightPt);
            properties.Append(indentation);
        }
        if (centered) properties.Append(new Justification { Val = JustificationValues.Center });
        var run = new Run();
        if (sizePt is not null || bold || italic)
        {
            var runProperties = new RunProperties();
            if (bold)
            {
                runProperties.Append(new Bold());
                runProperties.Append(new BoldComplexScript());
            }
            if (italic)
            {
                runProperties.Append(new Italic());
                runProperties.Append(new ItalicComplexScript());
            }
            if (sizePt is not null)
            {
                runProperties.Append(new FontSize { Val = HalfPoints(sizePt.Value) });
                runProperties.Append(new FontSizeComplexScript { Val = HalfPoints(sizePt.Value) });
            }
            run.RunProperties = runProperties;
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static Table BuildTable(AcademicDesignSpec spec, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        // tblPr children follow CT_TblBorders order (top, left/start, bottom, right/end, insideH,
        // insideV), and a w:tblGrid is mandatory between tblPr and the first w:tr.
        var table = new Table(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })),
            new TableGrid(Enumerable.Range(0, rows.Count > 0 ? rows[0].Count : 0)
                .Select(_ => new GridColumn { Width = TwipsI(Math.Max(10, (spec.PaperWidthPt - spec.MarginLeftPt - spec.MarginRightPt) / Math.Max(1, rows.Count > 0 ? rows[0].Count : 1))) })));
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
        // CT_SectPr child order: pgSz, pgMar, pgNumType, cols, bidi, rtlGutter. Null-assigned
        // attributes serialize as empty strings and are rejected by the validator, so optional
        // attributes are only assigned when they carry a value.
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
        if (!string.Equals(scheme, "none", StringComparison.Ordinal))
        {
            var pageNumberType = new PageNumberType { Format = PageNumberFormat(scheme) };
            if (frontMatter || spec.RestartNumbersAfterFrontMatter) pageNumberType.Start = 1;
            properties.Append(pageNumberType);
        }
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
        if (spec.IsRtl) properties.Append(new BiDi());
        if (spec.IsRtl && string.Equals(spec.BindingEdge, "right", StringComparison.Ordinal)) properties.Append(new GutterOnRight());
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

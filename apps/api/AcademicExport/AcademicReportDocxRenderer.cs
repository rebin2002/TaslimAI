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
        else
        {
            // A coverless design (APA-style reports put the title on the first page rather than on a
            // title page of their own) still has to identify itself: without this the export is a
            // stack of unlabelled prose, and the integrity check could not even locate the title.
            body.Append(Paragraph(report.Title, null, spec, centered: true, sizePt: spec.HeadingSizePt(1), bold: spec.HeadingsBold));
            if (!string.IsNullOrWhiteSpace(report.Subtitle))
                body.Append(Paragraph(report.Subtitle!, null, spec, centered: true));
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

        // The disclosure is a statement the student made, not a template decoration. Templates differ
        // in whether they reserve a styled slot for it, and a slot-less template used to drop the text
        // silently — the export said nothing about an AI-use statement the reader believed was filed,
        // while the panel copy promised it was "included in every export". It is now included in every
        // export: the slot decides styling, never existence.
        body.Append(Paragraph(labels.DisclosureHeading, spec.IncludesAiUseDisclosureSlot ? "Heading2" : null, spec, bold: !spec.IncludesAiUseDisclosureSlot));
        {
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
        // WordprocessingML allows exactly one SectionProperties as a direct child of the body: the final
        // one, which describes the last section. Every EARLIER section break belongs to the paragraph
        // that ends its section. A body-level sectPr in the middle is invalid markup — Word reports the
        // file as corrupt, and OpenXmlValidator flags it — so the front-matter break rides on a
        // paragraph of its own, and only the closing break stays at body level.
        body.Append(new Paragraph(new ParagraphProperties(SectionPropertiesFor(spec, frontMatter: true))));

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
            // Note-driven, not flag-driven: a row can hold a snippet (so "some content") and still have
            // to say plainly that the document itself was not read here.
            if (!string.IsNullOrWhiteSpace(source.AccessNote))
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
            case "bullets" or "numbered_list":
                // Both renderers treat a list the same way: one paragraph per item with the marker the
                // template's family implies, and — if a producer ever sends a list block with no items
                // but with prose — that prose rather than nothing.
                if (block.Items is { Count: > 0 } list)
                {
                    var number = 1;
                    foreach (var item in list)
                    {
                        var marker = block.Type.Equals("numbered_list", StringComparison.OrdinalIgnoreCase) ? $"{number++}.  " : "•  ";
                        yield return Paragraph(marker + WithCitations(item, block.CitationIds), null, spec,
                            indentLeftPt: spec.IsRtl ? 0 : 18, indentRightPt: spec.IsRtl ? 18 : 0);
                    }
                }
                else
                {
                    var prose = WithCitations(block.Text, block.CitationIds);
                    if (!string.IsNullOrWhiteSpace(prose)) yield return Paragraph(prose, null, spec);
                }
                break;
            case "table":
                if (block.Rows is { Count: > 0 } tableRows)
                {
                    // A table's own caption and its citations used to disappear whenever the row data
                    // was present, which is the normal case: the paragraph path rendered them, the
                    // table path rendered neither. A caption carries its ids in the same append shape a
                    // paragraph uses, so the citation marker sits with the claim it supports instead of
                    // floating below the grid; a caption-less table has nothing to append to, so its ids
                    // become their own line directly after the grid.
                    var hasCaption = !string.IsNullOrWhiteSpace(block.Text);
                    if (hasCaption)
                        yield return Paragraph(WithCitations(block.Text, block.CitationIds), null, spec);
                    yield return BuildTable(spec, tableRows);
                    if (!hasCaption && block.CitationIds.Count > 0)
                        yield return Paragraph(CitationMarkers(block.CitationIds), null, spec, italic: true);
                }
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

    /// <summary>The bare "[S1] [S3]" markers, for content with no prose to append them to. Reused by
    /// the PDF renderer so a table's citations read identically in both formats.</summary>
    public static string CitationMarkers(IReadOnlyList<string>? citations)
        => citations is null || citations.Count == 0
            ? string.Empty
            : string.Join(' ', citations.Select(citation => $"[{citation}]"));

    private static Paragraph TocField(AcademicDesignSpec spec)
    {
        var properties = new ParagraphProperties();
        properties.Append(new ParagraphStyleId { Val = "TOCEntry" });
        // w:pStyle is the first element of CT_PPr; appending the RTL flag ahead of it is a schema
        // violation that only an RTL template hits, which is why the Kurdish export failed validation.
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
                // CT_RPr order: rFonts, b, bCs, i, iCs, caps, ..., sz, szCs. Bold and caps used to be
                // appended after the size, which the schema reads as an unexpected w:b.
                BuildHeadingRunProperties(headingFont, spec, level))
            { StyleId = id, Type = StyleValues.Paragraph });
        }

        styles.Append(new Style(
            new StyleName { Val = "toc 1" },
            new BasedOn { Val = "Normal" },
            new StyleParagraphProperties(
                // CT_PPrBase order: tabs, ..., spacing.
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

    /// <summary>Heading character properties in schema order (rFonts, b, bCs, caps, sz, szCs).</summary>
    private static StyleRunProperties BuildHeadingRunProperties(string headingFont, AcademicDesignSpec spec, int level)
    {
        var properties = new StyleRunProperties();
        properties.Append(new RunFonts { Ascii = headingFont, HighAnsi = headingFont, EastAsia = headingFont, ComplexScript = headingFont });
        if (spec.HeadingsBold) properties.Append(new Bold(), new BoldComplexScript());
        if (level == 1 && spec.H1Uppercase) properties.Append(new Caps());
        properties.Append(new FontSize { Val = HalfPoints(spec.HeadingSizePt(level)) },
            new FontSizeComplexScript { Val = HalfPoints(spec.HeadingSizePt(level)) });
        return properties;
    }

    private static Paragraph Paragraph(string text, string? style, AcademicDesignSpec spec,
        bool centered = false, double? sizePt = null, bool bold = false, bool italic = false,
        double indentLeftPt = 0, double indentRightPt = 0)
    {
        // WordprocessingML declares an exact child order inside w:pPr and w:rPr, and the OpenXml
        // validator rejects a document that appends them in whatever order was convenient: w:pStyle
        // must precede w:bidi, and w:b/w:i must precede w:sz. Word's own repair dialog used to swallow
        // this; a schema check cannot. Build in schema order instead of relying on the reader's
        // tolerance, and never emit an attribute with an empty value (w:left="" is not "unset").
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
            if (bold) runProperties.Append(new Bold(), new BoldComplexScript());
            if (italic) runProperties.Append(new Italic(), new ItalicComplexScript());
            if (sizePt is not null) runProperties.Append(new FontSize { Val = HalfPoints(sizePt.Value) }, new FontSizeComplexScript { Val = HalfPoints(sizePt.Value) });
            run.RunProperties = runProperties;
        }
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static Table BuildTable(AcademicDesignSpec spec, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table(new TableProperties(
            new TableBorders(
                // w:tblBorders is an ordered group: top, left, bottom, right, then the inside edges.
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        // w:tblGrid is required between w:tblPr and the first w:tr; without it the table is only
        // renderable by forgiving readers, and the validator reports the rows as unexpected children.
        var columnCount = rows.Count == 0 ? 0 : rows[0].Count;
        var grid = new TableGrid();
        var usableWidthPt = Math.Max(1, spec.PaperWidthPt - spec.MarginLeftPt - spec.MarginRightPt);
        for (var index = 0; index < columnCount; index++)
            grid.Append(new GridColumn { Width = TwipsI(usableWidthPt / Math.Max(1, columnCount)) });
        table.Append(grid);
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
        // EG_SectPrContents is an ordered group: w:pgSz, w:pgMar, then w:pgNumType, w:cols, w:bidi and
        // w:rtlGutter. Emitting the numbering first made the validator call w:pgSz an unexpected child,
        // which is the difference between a file Word opens and a file Word offers to repair.
        if (!string.Equals(scheme, "none", StringComparison.Ordinal))
            properties.Append(new PageNumberType
            {
                Format = PageNumberFormat(scheme),
                Start = frontMatter || spec.RestartNumbersAfterFrontMatter ? new Int32Value(1) : null,
            });
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

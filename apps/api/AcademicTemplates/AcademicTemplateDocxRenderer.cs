using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Globalization;

namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// Turns one <see cref="AcademicDesignSpec"/> plus the synthetic sample into a real .docx so the
/// template library is proven against Word's own grid instead of an approximation:
/// section geometry in twips, styles on the half-point grid, roman/arabic page-number sections with
/// restart, <c>w:bidi</c> + <c>w:rtlGutter</c> (GutterOnRight) for Kurdish/Arabic layouts,
/// hanging-indent bibliography paragraphs, real footnotes in the footnotes part and an external
/// hyperlink relationship.
///
/// It is deliberately separate from the production Document renderers (lane boundary): the integration
/// lane adopts the same spec→wordprocessing mapping when academic export jobs are wired in. The
/// document identity written into core properties is neutral by design — no provider, model, routing
/// or cost metadata ever appears in an artifact produced here.
///
/// Digit note: the OOXML SDK's ST_NumberFormat carries no arabic-indic token; Word renders page digits
/// by document locale, so <c>digitSystem=arabic-indic</c> maps to <c>decimal</c> in DOCX and is applied
/// explicitly in the preview/PDF surfaces.
/// </summary>
public static class AcademicTemplateDocxRenderer
{
    public static byte[] Render(AcademicDesignSpec spec, AcademicSampleContent? sample = null)
    {
        sample ??= AcademicSampleContent.For(spec);
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = package.AddMainDocumentPart();
            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = BuildStyles(spec);
            stylesPart.Styles.Save();

            FootnotesPart? footnotesPart = null;
            if (spec.FootnotesEnabled)
            {
                footnotesPart = main.AddNewPart<FootnotesPart>();
                footnotesPart.Footnotes = BuildFootnotes(spec, sample);
                footnotesPart.Footnotes.Save();
            }

            main.Document = BuildDocument(spec, sample, footnotesPart is not null);

            // The hyperlinked integrity-note paragraph needs a real external relationship so Word
            // treats it as a citation-style hyperlink target; the id is allocated by the package, so
            // assignment happens post-build.
            var hyperlink = main.Document.Descendants<Hyperlink>().FirstOrDefault();
            if (hyperlink is not null)
                hyperlink.Id = main.AddHyperlinkRelationship(new Uri("https://example.org/academic-integrity-statement", UriKind.Absolute), true).Id;

            main.Document.Save();

            var properties = package.PackageProperties;
            properties.Creator = "Taslim Academic Templates";
            properties.LastModifiedBy = "Taslim Academic Templates";
            properties.Title = $"Template sample preview — {spec.ConfigurationId}";
            properties.Subject = "Synthetic layout preview; contains no research content";
            properties.Keywords = "synthetic preview; not a research document";
            properties.Description = sample.Banner;
        }
        return stream.ToArray();
    }

    private static Styles BuildStyles(AcademicDesignSpec spec)
    {
        var body = Font(spec.BodyFontToken);
        var heading = Font(spec.HeadingFontToken);
        var caption = Font(spec.CaptionFontToken);
        var lineTwips = (int)Math.Round(spec.LineSpacing * 240);
        var lineTwipsS = lineTwips.ToString(CultureInfo.InvariantCulture);
        var styles = new Styles();

        styles.Append(new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = body, HighAnsi = body, EastAsia = body, ComplexScript = body },
                    new FontSize { Val = HalfPoints(spec.BodySizePt) },
                    new FontSizeComplexScript { Val = HalfPoints(spec.BodySizePt) })),
                    new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = lineTwipsS, LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(NormalStyle(spec, lineTwips));
        styles.Append(HeadingStyle("Heading1", "heading 1", spec, heading, spec.HeadingSizePt(1), level1: true, outline: 0));
        styles.Append(HeadingStyle("Heading2", "heading 2", spec, heading, spec.HeadingSizePt(2), level1: false, outline: 1));
        styles.Append(HeadingStyle("Heading3", "heading 3", spec, heading, spec.HeadingSizePt(3), level1: false, outline: 2));
        styles.Append(TocEntryStyle(spec));
        styles.Append(CaptionStyle(spec, caption));
        styles.Append(QuoteStyle(spec, lineTwips));
        styles.Append(BibliographyStyle(spec));
        styles.Append(FootnoteTextStyle(spec, caption));
        return styles;
    }

    private static Style NormalStyle(AcademicDesignSpec spec, int lineTwips) => new(
        new StyleName { Val = "Normal" },
        new StyleParagraphProperties(
            new SpacingBetweenLines { After = TwipsI(spec.ParagraphAfterPt), Line = lineTwips.ToString(CultureInfo.InvariantCulture), LineRule = LineSpacingRuleValues.Auto },
            spec.JustifyBody ? new Justification { Val = JustificationValues.Both } : null!,
            spec.FirstLineIndentPt > 0 ? new Indentation { FirstLine = TwipsI(spec.FirstLineIndentPt) } : null!))
    {
        StyleId = "Normal",
        Type = StyleValues.Paragraph,
        Default = OnOffValue.FromBoolean(true),
    };

    private static Style HeadingStyle(string id, string name, AcademicDesignSpec spec, string font, double sizePt, bool level1, int outline) => new(
        new StyleName { Val = name },
        new BasedOn { Val = "Normal" },
        new NextParagraphStyle { Val = "Normal" },
        new StyleParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = TwipsI(spec.HeadingTopGapPt), After = TwipsI(spec.ParagraphAfterPt / 2) },
            new Indentation { FirstLine = "0" },
            new OutlineLevel { Val = outline }),
        new StyleRunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, EastAsia = font, ComplexScript = font },
            new FontSize { Val = HalfPoints(sizePt) },
            new FontSizeComplexScript { Val = HalfPoints(sizePt) },
            spec.HeadingsBold ? new Bold() : null!,
            spec.HeadingsBold ? new BoldComplexScript() : null!,
            level1 && spec.H1Uppercase ? new Caps() : null!))
    {
        StyleId = id,
        Type = StyleValues.Paragraph,
    };

    private static Style TocEntryStyle(AcademicDesignSpec spec) => new(
        new StyleName { Val = "toc 1" },
        new BasedOn { Val = "Normal" },
        new StyleParagraphProperties(
            new Tabs(new TabStop { Val = TabStopValues.Right, Position = TwipsN(spec.PaperWidthPt - spec.MarginLeftPt - spec.MarginRightPt) }),
            new SpacingBetweenLines { After = "40" }))
    {
        StyleId = "TOCEntry",
        Type = StyleValues.Paragraph,
    };

    private static Style CaptionStyle(AcademicDesignSpec spec, string font) => new(
        new StyleName { Val = "caption" },
        new BasedOn { Val = "Normal" },
        new StyleParagraphProperties(new KeepLines()),
        new StyleRunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font },
            new Italic(),
            new ItalicComplexScript(),
            new FontSize { Val = HalfPoints(spec.BodySizePt * 0.85) }))
    {
        StyleId = "Caption",
        Type = StyleValues.Paragraph,
    };

    private static Style QuoteStyle(AcademicDesignSpec spec, int lineTwips) => new(
        new StyleName { Val = "Block Text" },
        new BasedOn { Val = "Normal" },
        new StyleParagraphProperties(
            new SpacingBetweenLines { Line = ((int)Math.Round(lineTwips * 0.9)).ToString(CultureInfo.InvariantCulture), LineRule = LineSpacingRuleValues.Auto },
            new Indentation
            {
                Left = TwipsI(spec.IsRtl ? 0 : 36),
                Right = TwipsI(spec.IsRtl ? 36 : 0),
            }))
    {
        StyleId = "BlockText",
        Type = StyleValues.Paragraph,
    };

    private static Style BibliographyStyle(AcademicDesignSpec spec) => new(
        new StyleName { Val = "Bibliography" },
        new BasedOn { Val = "Normal" },
        new StyleParagraphProperties(
            new SpacingBetweenLines { After = "80" },
            spec.BibliographyHangingIndent
                ? new Indentation { Left = TwipsI(spec.BibliographyIndentPt), Hanging = TwipsI(spec.BibliographyIndentPt) }
                : new Indentation { Left = "0" }))
    {
        StyleId = "Bibliography",
        Type = StyleValues.Paragraph,
    };

    private static Style FootnoteTextStyle(AcademicDesignSpec spec, string font) => new(
        new StyleName { Val = "footnote text" },
        new StyleParagraphProperties(new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
        new StyleRunProperties(
            new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font },
            new FontSize { Val = HalfPoints(Math.Max(8, spec.BodySizePt * 0.8)) }))
    {
        StyleId = "FootnoteText",
        Type = StyleValues.Paragraph,
    };

    private static Document BuildDocument(AcademicDesignSpec spec, AcademicSampleContent sample, bool hasFootnotes)
    {
        var body = new Body();

        // Front matter block: cover, approval, disclosure, contents — numbered with the front scheme.
        if (spec.CoverStyle != "none")
        {
            body.Append(Paragraph(sample.Title, null, spec, centered: true, sizePt: spec.HeadingSizePt(1), bold: spec.HeadingsBold));
            body.Append(PageBreak());
            body.Append(Paragraph(sample.Subtitle, null, spec, centered: true));
            body.Append(Paragraph($"{sample.AuthorLine} · {sample.DateLine}", null, spec, centered: true));
            body.Append(Paragraph(sample.InstitutionLine, null, spec, centered: true));
            body.Append(PageBreak());
        }
        if (spec.IncludesApprovalPage)
        {
            body.Append(Paragraph(sample.InstitutionLine, null, spec, centered: true));
            body.Append(Paragraph($"{sample.AuthorLine} — {sample.DateLine}", null, spec, centered: true));
            body.Append(PageBreak());
        }
        if (spec.IncludesAiUseDisclosureSlot)
        {
            body.Append(Paragraph(sample.AiDisclosureHeading, "Heading2", spec));
            body.Append(Paragraph(sample.AiDisclosureText, null, spec));
        }
        if (spec.IncludeToc)
        {
            body.Append(Paragraph(sample.ContentsHeading, "Heading1", spec));
            foreach (var entry in sample.Contents)
            {
                var pageLabel = AcademicRtlPolicy.PageLabel(int.TryParse(entry.Page, out var parsed) ? parsed : 0, spec.BodyNumbering, spec);
                body.Append(Paragraph($"{(entry.Number.Length > 0 ? entry.Number + "\t" : "")}{entry.Title}\t{pageLabel}", "TOCEntry", spec));
            }
            body.Append(PageBreak());
        }
        body.Append(SectionPropertiesFor(spec, frontMatter: true, hasFootnotes));

        // Body block: chapters, exhibits, bibliography, appendix — restart with body numbering.
        foreach (var section in sample.Sections)
        {
            body.Append(Paragraph(section.Heading, "Heading1", spec));
            var paragraph = Paragraph(section.Body, null, spec);
            if (hasFootnotes && section.Body.Contains("[1]", StringComparison.Ordinal))
                paragraph.Append(FootnoteMarkerRun(1));
            body.Append(paragraph);
            if (section.SubHeading is not null) body.Append(Paragraph(section.SubHeading, "Heading2", spec));
        }

        body.Append(Paragraph(sample.TableCaption, "Caption", spec));
        body.Append(BuildTable(spec, sample.TableRows));
        body.Append(Paragraph(sample.FigureCaption, "Caption", spec));
        body.Append(Paragraph(sample.QuoteText + " — " + sample.QuoteAttribution, "BlockText", spec));

        body.Append(Paragraph(sample.BibliographyHeading, "Heading1", spec));
        foreach (var entry in sample.Bibliography)
            body.Append(Paragraph(entry.Text, "Bibliography", spec));

        body.Append(Paragraph(sample.AppendixHeading, "Heading1", spec));
        body.Append(Paragraph(sample.AppendixText, null, spec));
        body.Append(HyperlinkParagraph(spec));

        body.Append(SectionPropertiesFor(spec, frontMatter: false, hasFootnotes));
        return new Document(body);
    }

    private static Paragraph PageBreak() => new(new Run(new Break { Type = BreakValues.Page }));

    private static Paragraph Paragraph(string text, string? style, AcademicDesignSpec spec, bool centered = false, double? sizePt = null, bool bold = false)
    {
        var properties = new ParagraphProperties();
        if (spec.IsRtl) properties.Append(new BiDi());
        if (style is not null) properties.Append(new ParagraphStyleId { Val = style });
        if (centered) properties.Append(new Justification { Val = JustificationValues.Center });
        var run = new Run();
        if (sizePt is not null || bold)
            run.RunProperties = new RunProperties(
                sizePt is not null ? new FontSize { Val = HalfPoints(sizePt.Value) } : null!,
                sizePt is not null ? new FontSizeComplexScript { Val = HalfPoints(sizePt.Value) } : null!,
                bold ? new Bold() : null!,
                bold ? new BoldComplexScript() : null!);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(properties, run);
    }

    private static Run FootnoteMarkerRun(int id) => new(
        new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
        new FootnoteReference { Id = new IntegerValue((long)id) });

    private static Paragraph HyperlinkParagraph(AcademicDesignSpec spec)
    {
        var properties = new ParagraphProperties();
        if (spec.IsRtl) properties.Append(new BiDi());
        return new Paragraph(properties,
            new Run(new Text("Citation target formatting reference: ") { Space = SpaceProcessingModeValues.Preserve }),
            new Hyperlink(new Run(new Text("integrity statement (example.org)"))),
            new Run(new Text(" — verifies hyperlink relationship preservation.") { Space = SpaceProcessingModeValues.Preserve }));
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
            if (rowIndex == 0)
                tableRow.Append(new TableRowProperties(new TableHeader()));
            foreach (var cell in row)
                tableRow.Append(new TableCell(Paragraph(cell, null, spec)));
            table.Append(tableRow);
        }
        return table;
    }

    private static SectionProperties SectionPropertiesFor(AcademicDesignSpec spec, bool frontMatter, bool hasFootnotes)
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
            {
                ColumnCount = (short)spec.ColumnCount,
                EqualWidth = OnOffValue.FromBoolean(true),
            });
        }
        else
        {
            properties.Append(new Columns());
        }
        if (hasFootnotes)
            properties.Append(new FootnoteProperties(new NumberingRestart { Val = RestartNumberValues.EachPage }));
        return properties;
    }

    private static NumberFormatValues PageNumberFormat(string scheme) => scheme switch
    {
        "roman-lower" => NumberFormatValues.LowerRoman,
        "roman-upper" => NumberFormatValues.UpperRoman,
        _ => NumberFormatValues.Decimal,
    };

    private static Footnotes BuildFootnotes(AcademicDesignSpec spec, AcademicSampleContent sample)
    {
        var footnotes = new Footnotes();
        footnotes.Append(
            new Footnote(new Paragraph(new Run(new SeparatorMark())))
            {
                Type = FootnoteEndnoteValues.Separator,
                Id = new IntegerValue(-1L),
            },
            new Footnote(new Paragraph(new Run(new ContinuationSeparatorMark())))
            {
                Type = FootnoteEndnoteValues.ContinuationSeparator,
                Id = new IntegerValue(0L),
            });
        var properties = new ParagraphProperties();
        if (spec.IsRtl) properties.Append(new BiDi());
        properties.Append(new ParagraphStyleId { Val = "FootnoteText" });
        footnotes.Append(new Footnote(
            new Paragraph(properties,
                new Run(new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }), new Text("1 ")),
                new Run(new Text(sample.FootnoteText.TrimStart('[', '1', ']', ' ')) { Space = SpaceProcessingModeValues.Preserve })))
        {
            Type = FootnoteEndnoteValues.Normal,
            Id = new IntegerValue(1L),
        });
        return footnotes;
    }

    // ── unit helpers ──
    private static string TwipsI(double points) => ((int)Math.Round(points * AcademicDesignSpec.TwipsPerPoint)).ToString(CultureInfo.InvariantCulture);
    private static int TwipsN(double points) => (int)Math.Round(points * AcademicDesignSpec.TwipsPerPoint);
    private static string HalfPoints(double points) => ((int)Math.Round(points * 2)).ToString(CultureInfo.InvariantCulture);
    private static string Font(string token) => AcademicFontTokens.Resolve(token).DocxFamily;
}

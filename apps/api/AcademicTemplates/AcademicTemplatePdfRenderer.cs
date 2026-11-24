using QuestPDF.Fluent;
using QuestPDF.Drawing;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// The PDF half of the shared design spec: the same <see cref="AcademicDesignSpec"/> that produced the
/// preview HTML and the sample DOCX drives page size, margins (including the physical binding gutter),
/// the heading ladder, hanging-indent bibliography, running head, folio position and RTL content flow.
///
/// Font handling mirrors the production DocumentRenderer stance (Community license, bundled Noto Sans
/// Arabic registration, missing-glyph degradation) and resolves each design font token through an
/// ordered candidate list so the sample renders on any exporter host. Document metadata carries a
/// neutral template identity — never provider, model, routing or cost information.
///
/// Honesty note: this is a structural export sample (tagged reading order via text spans, alt text on
/// the figure block, real page numbering); the accessibility ceiling of the current QuestPDF Community
/// pipeline is the same one the existing Document Studio PDFs carry, and the integration lane should
/// reuse whichever tagging upgrade lands there rather than this module promising PDF/UA on its own.
/// </summary>
public static class AcademicTemplatePdfRenderer
{
    private static int configured;
    private const string BundledArabicFontFileName = "NotoSansArabic-Regular.ttf";

    internal static void EnsureQuestPdfConfigured()
    {
        if (Interlocked.Exchange(ref configured, 1) == 0)
        {
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
            var fontPath = Path.Combine(AppContext.BaseDirectory, "Fonts", BundledArabicFontFileName);
            if (File.Exists(fontPath)) FontManager.RegisterFontFromFile(fontPath);
            // QuestPDF validates families against its own registered store, not fontconfig: register the
            // metric-compatible host families the candidate ladder points at (Liberation = Times/Arial
            // metrics, DejaVu = the neutral fallback). Both ship on the CI ubuntu image; missing files
            // are simply skipped because the ladder's final tier is QuestPDF's built-in "Default".
            foreach (var dir in new[]
            {
                "/usr/share/fonts/truetype/liberation",
                "/usr/share/fonts/truetype/dejavu",
            })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.ttf"))
                {
                    try { FontManager.RegisterFontFromFile(file); }
                    catch (Exception) { /* a corrupt or duplicate face must never break the whole store */ }
                }
            }
        }
    }

    public static byte[] Render(AcademicDesignSpec spec, AcademicSampleContent? sample = null)
    {
        EnsureQuestPdfConfigured();
        sample ??= AcademicSampleContent.For(spec);
        return GenerateWithFontFallbacks(spec, sample, attempt =>
        {
            var bodyFont = Candidate(spec, spec.BodyFontToken, attempt);
            var headingFont = Candidate(spec, spec.HeadingFontToken, attempt);
            var captionFont = Candidate(spec, spec.CaptionFontToken, attempt);
            return Generate(spec, sample, bodyFont, headingFont, captionFont);
        });
    }

    /// <summary>
    /// QuestPDF throws on an unresolvable font family while ThrowOnMissingFontFamilies stays enabled
    /// (a misconfiguration must fail loudly for the production exporter, and the same stance applies
    /// here); for sample previews we retry down the candidate list before surfacing the error.
    /// </summary>
    private static byte[] GenerateWithFontFallbacks(AcademicDesignSpec spec, AcademicSampleContent sample, Func<int, byte[]> generate)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return generate(attempt);
            }
            catch (Exception exception) when (attempt < 3 && IsFontFamilyFailure(exception))
            {
                // try the next candidate tier
            }
        }
        throw new InvalidOperationException($"Academic PDF sample could not be rendered for '{spec.ConfigurationId}'.");
    }

    private static bool IsFontFamilyFailure(Exception exception) =>
        exception.Message.Contains("font", StringComparison.OrdinalIgnoreCase)
        || exception.InnerException is not null && IsFontFamilyFailure(exception.InnerException);

    private static string Candidate(AcademicDesignSpec spec, string token, int attempt)
    {
        var candidates = AcademicFontTokens.Resolve(token).PdfFamilyCandidates;
        if (attempt < candidates.Count) return candidates[attempt];
        // Noto Sans Arabic is bundled and always registered for RTL scripts; LTR ends on QuestPDF's
        // built-in default face so a sample never fails on an exotic host with no registered fonts.
        return spec.IsRtl ? "Noto Sans Arabic" : "Default";
    }

    private static byte[] Generate(AcademicDesignSpec spec, AcademicSampleContent sample, string bodyFont, string headingFont, string captionFont)
    {
        var (marginLeft, marginRight) = spec.MirroredSideMargins();
        var textColor = Hex(spec.TextColorHex);
        var accentColor = Hex(spec.AccentColorHex);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizeFor(spec));
                page.MarginTop((float)spec.MarginTopPt);
                page.MarginBottom((float)spec.MarginBottomPt);
                page.MarginLeft((float)marginLeft);
                page.MarginRight((float)marginRight);
                page.DefaultTextStyle(style => style.FontSize((float)spec.BodySizePt).FontFamily(bodyFont).FontColor(textColor));

                if (spec.PageNumberPosition.StartsWith("top", StringComparison.Ordinal) || spec.RunningHeadContent != "none")
                    page.Header().Column(header => ComposeHeader(header, spec, sample, bodyFont, captionFont, accentColor, textColor));
                if (spec.PageNumberPosition.StartsWith("bottom", StringComparison.Ordinal))
                    page.Footer().Row(footer => ComposeFooter(footer, spec, sample, bodyFont, textColor));

                var content = page.Content();
                var rtlContent = spec.IsRtl ? content.ContentFromRightToLeft() : content;
                rtlContent.Column(column =>
                {
                    column.Spacing((float)spec.ParagraphAfterPt);
                    ComposeCover(column.Item(), spec, sample, headingFont, accentColor, textColor);
                    ComposeToc(column.Item(), spec, sample, headingFont);
                    foreach (var section in sample.Sections)
                    {
                        ComposeHeading(column.Item(), spec, headingFont, section.Heading, 1, textColor);
                        ComposeBody(column.Item(), spec, section.Body, textColor);
                        if (section.SubHeading is not null)
                            ComposeHeading(column.Item(), spec, headingFont, section.SubHeading, 2, textColor);
                        if (spec.FootnotesEnabled && section.Body.Contains("[1]", StringComparison.Ordinal))
                            ComposeFootnote(column.Item(), spec, captionFont, sample, textColor);
                    }
                    ComposeTable(column.Item(), spec, sample, captionFont, textColor);
                    ComposeFigure(column.Item(), spec, sample, captionFont, accentColor);
                    ComposeQuote(column.Item(), spec, sample, accentColor, textColor);
                    ComposeBibliography(column.Item(), spec, sample, headingFont);
                    ComposeAppendix(column.Item(), spec, sample, headingFont);
                });
            });
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = $"Template sample preview — {spec.ConfigurationId}",
            Author = "Taslim Academic Templates",
        })
        .GeneratePdf();
    }

    private static PageSize PageSizeFor(AcademicDesignSpec spec) => spec.PaperFormat switch
    {
        "letter" => PageSizes.Letter,
        "a5" => PageSizes.A5,
        _ => PageSizes.A4,
    };

    private static void ComposeHeader(ColumnDescriptor header, AcademicDesignSpec spec, AcademicSampleContent sample, string bodyFont, string captionFont, string accentColor, string textColor)
    {
        if (spec.RunningHeadContent == "none" && !spec.PageNumberPosition.StartsWith("top", StringComparison.Ordinal)) return;
        header.Item().Row(row =>
        {
            if (spec.RunningHeadContent != "none")
            {
                row.RelativeItem().Text(sample.Title).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.8)).FontColor(accentColor);
            }
            if (spec.PageNumberPosition.StartsWith("top", StringComparison.Ordinal))
            {
                var align = spec.PageNumberPhysicalAlignment();
                if (align != "right") row.RelativeItem();
                row.ConstantItem(90).AlignCenter().Text(Folio(spec, sample, bodyFont, textColor));
                if (align == "right") row.RelativeItem();
            }
        });
        if (spec.RunningHeadRulePt > 0)
            header.Item().PaddingTop(3).LineHorizontal((float)(spec.RunningHeadRulePt / 3)).LineColor(accentColor);
    }

    private static void ComposeFooter(RowDescriptor footer, AcademicDesignSpec spec, AcademicSampleContent sample, string bodyFont, string textColor)
    {
        var align = spec.PageNumberPhysicalAlignment();
        if (align == "center")
        {
            footer.ConstantItem(90).AlignCenter().Text(Folio(spec, sample, bodyFont, textColor));
            return;
        }
        if (align == "right") footer.RelativeItem();
        footer.ConstantItem(90).AlignCenter().Text(Folio(spec, sample, bodyFont, textColor));
        if (align == "left") footer.RelativeItem();
    }

    private static Action<TextDescriptor> Folio(AcademicDesignSpec spec, AcademicSampleContent sample, string bodyFont, string textColor) => text =>
    {
        text.DefaultTextStyle(style => style.FontSize((float)(spec.BodySizePt * 0.85)).FontFamily(bodyFont).FontColor(textColor));
        text.Span($"{sample.PageLabelWord} ");
        text.CurrentPageNumber();
    };

    private static void ComposeCover(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string headingFont, string accentColor, string textColor)
    {
        if (string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal))
        {
            // The synthetic banner is a disclosure, not a cover decoration: cover-less designs
            // still lead with it so a preview can never be mistaken for real research output.
            item.Column(banner =>
            {
                banner.Item().PaddingBottom(4).Text(sample.Banner).FontSize((float)Math.Max(8, spec.BodySizePt * 0.75)).FontColor(accentColor);
                banner.Item().PaddingBottom(6).LineHorizontal(0.5f).LineColor(accentColor);
            });
            return;
        }
        item.Column(column =>
        {
            column.Spacing(6);
            if (string.Equals(spec.CoverStyle, "institutional-band", StringComparison.Ordinal))
                column.Item().Background(accentColor).PaddingVertical(14).PaddingHorizontal(12)
                    .Text(sample.InstitutionLine).FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(3)).FontColor(Colors.White);
            column.Item().PaddingTop(28);
            column.Item().AlignCenter().Text(text =>
            {
                text.DefaultTextStyle(style =>
                {
                    var withFont = style.FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).FontColor(textColor);
                    return spec.HeadingsBold ? withFont.Bold() : withFont;
                });
                text.Span(spec.H1Uppercase ? sample.Title.ToUpperInvariant() : sample.Title);
            });
            column.Item().AlignCenter().Text(sample.Subtitle);
            if (string.Equals(spec.CoverStyle, "two-part", StringComparison.Ordinal))
                column.Item().PaddingVertical(10).LineHorizontal(1).LineColor(accentColor);
            column.Item().PaddingTop(20).AlignCenter().Text($"{sample.AuthorLine} · {sample.DateLine}");
            if (!string.Equals(spec.CoverStyle, "institutional-band", StringComparison.Ordinal))
                column.Item().AlignCenter().Text(sample.InstitutionLine);
            column.Item().AlignCenter().Text($"Banner: {sample.Banner}").FontSize((float)(spec.BodySizePt * 0.75));
            column.Item().PageBreak();
        });
    }

    private static void ComposeToc(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string headingFont)
    {
        if (!spec.IncludeToc) return;
        item.Column(column =>
        {
            column.Spacing(4);
            column.Item().Text(sample.ContentsHeading).FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(2)).Bold();
            foreach (var entry in sample.Contents)
                column.Item().Row(row =>
                {
                    row.RelativeItem().Text($"{(entry.Number.Length > 0 ? entry.Number + "  " : "")}{entry.Title}");
                    row.ConstantItem(52).AlignRight().Text(AcademicRtlPolicy.PageLabel(int.TryParse(entry.Page, out var page) ? page : 0, spec.BodyNumbering, spec));
                });
            column.Item().PageBreak();
        });
    }

    private static void ComposeHeading(IContainer item, AcademicDesignSpec spec, string headingFont, string heading, int level, string textColor) =>
        item.PaddingTop((float)spec.HeadingTopGapPt).Text(text =>
        {
            text.DefaultTextStyle(style =>
            {
                var withFont = style.FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(level)).FontColor(textColor);
                return spec.HeadingsBold ? withFont.Bold() : withFont;
            });
            text.Span(level == 1 && spec.H1Uppercase ? heading.ToUpperInvariant() : heading);
        });

    private static void ComposeBody(IContainer item, AcademicDesignSpec spec, string text, string textColor) =>
        item.Text(t =>
        {
            t.DefaultTextStyle(x => x.LineHeight((float)spec.LineSpacing).FontColor(textColor));
            if (spec.JustifyBody) t.Justify();
            t.Span(text);
        });

    private static void ComposeFootnote(IContainer item, AcademicDesignSpec spec, string captionFont, AcademicSampleContent sample, string textColor) =>
        item.BorderTop(0.5f).BorderColor(textColor).PaddingTop(4)
            .Text(sample.FootnoteText).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.8));

    private static void ComposeTable(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string captionFont, string textColor)
    {
        item.Column(column =>
        {
            column.Spacing(4);
            if (string.Equals(spec.TableCaptionPosition, "above", StringComparison.Ordinal))
                column.Item().Text(sample.TableCaption).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.85));
            column.Item().Table(table =>
            {
                var width = sample.TableRows[0].Count;
                table.ColumnsDefinition(definition =>
                {
                    for (var index = 0; index < width; index++) definition.RelativeColumn();
                });
                var rowIndex = 0;
                foreach (var row in sample.TableRows)
                {
                    foreach (var cell in row)
                    {
                        var cellText = table.Cell().Border(0.5f).BorderColor(textColor).PaddingVertical(3).PaddingHorizontal(5)
                            .Text(cell).FontSize((float)(spec.BodySizePt * 0.9));
                        if (rowIndex == 0) cellText.Bold();
                    }
                    rowIndex++;
                }
            });
            if (string.Equals(spec.TableCaptionPosition, "below", StringComparison.Ordinal))
                column.Item().Text(sample.TableCaption).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.85));
        });
    }

    private static void ComposeFigure(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string captionFont, string accentColor)
    {
        item.Column(column =>
        {
            column.Spacing(4);
            if (string.Equals(spec.FigureCaptionPosition, "above", StringComparison.Ordinal))
                column.Item().Text(sample.FigureCaption).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.85));
            // Alt text travels with the placeholder block so assistive technology gets the caption
            // even where the drawing surface itself is decorative.
            column.Item().Height(70).Background(Mix(accentColor, "#ffffff", 0.08))
                .Extend().Text(sample.FigureCaption).FontSize(0.1f).FontColor(Mix(accentColor, "#ffffff", 0.3));
            if (string.Equals(spec.FigureCaptionPosition, "below", StringComparison.Ordinal))
                column.Item().Text(sample.FigureCaption).FontFamily(captionFont).FontSize((float)(spec.BodySizePt * 0.85));
        });
    }

    private static void ComposeQuote(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string accentColor, string textColor)
    {
        item.PaddingVertical(8).Row(row =>
        {
            // An unbounded bar height cannot be split across pages and aborts the whole layout pass
            // ("conflicting size constraints"). The accent bar gets a deterministic height estimated
            // from the quote's line count at the design's own measure and leading.
            var charsPerLine = Math.Max(20, spec.ContentWidthPt / (spec.BodySizePt * 0.52));
            var quoteLines = Math.Ceiling((sample.QuoteText.Length + sample.QuoteAttribution.Length + 4) / charsPerLine);
            var barHeight = (float)Math.Clamp(quoteLines * spec.LineHeightPt * 0.95, 24, 120);
            row.ConstantItem(3).Height(barHeight).Background(accentColor);
            row.RelativeItem().PaddingLeft(12).PaddingRight(12).Text(t =>
            {
                t.DefaultTextStyle(x => x.LineHeight((float)(spec.LineSpacing * 0.95)).FontColor(textColor));
                t.Span(sample.QuoteText);
                t.Span(" — " + sample.QuoteAttribution).SemiBold();
            });
        });
    }

    private static void ComposeBibliography(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string headingFont)
    {
        item.Column(column =>
        {
            column.Spacing(4);
            column.Item().PaddingTop((float)spec.HeadingTopGapPt).Text(sample.BibliographyHeading).FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).Bold();
            foreach (var entry in sample.Bibliography)
                column.Item()
                    .PaddingLeft((float)(spec.BibliographyHangingIndent ? spec.BibliographyIndentPt : 0))
                    .PaddingRight((float)(spec.BibliographyHangingIndent && spec.IsRtl ? spec.BibliographyIndentPt : 0))
                    .Text(entry.Text).LineHeight((float)Math.Min(spec.LineSpacing, 1.3));
        });
    }

    private static void ComposeAppendix(IContainer item, AcademicDesignSpec spec, AcademicSampleContent sample, string headingFont)
    {
        item.Column(column =>
        {
            column.Item().PageBreak();
            column.Item().PaddingTop((float)spec.HeadingTopGapPt).Text(sample.AppendixHeading).FontFamily(headingFont).FontSize((float)spec.HeadingSizePt(1)).Bold();
            column.Item().Text(sample.AppendixText);
        });
    }

    private static string Hex(string color) => color.StartsWith("#", StringComparison.Ordinal) ? color : $"#{color}";

    private static string Mix(string foreground, string background, double weight)
    {
        var f = Parse(foreground);
        var b = Parse(background);
        int Blend(int x, int y) => (int)Math.Round(x * weight + y * (1 - weight));
        return $"#{Blend(f.Item1, b.Item1):X2}{Blend(f.Item2, b.Item2):X2}{Blend(f.Item3, b.Item3):X2}";
    }

    private static (int, int, int) Parse(string hex)
    {
        var value = hex.StartsWith("#", StringComparison.Ordinal) ? hex[1..] : hex;
        if (value.Length == 3) value = string.Concat(value.Select(c => $"{c}{c}"));
        return (Convert.ToInt32(value[..2], 16), Convert.ToInt32(value.Substring(2, 2), 16), Convert.ToInt32(value.Substring(4, 2), 16));
    }
}

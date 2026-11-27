using System.Globalization;
using System.Net;
using System.Text;

namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// Deterministic HTML rendering of a design spec over the synthetic sample — the same spec object the
/// DOCX/PDF renderers consume, so the gallery cannot show a design the exporter will not produce.
///
/// Contract:
/// • Input is (spec, sample content) only. No user data path exists in this class — previews are
///   structurally incapable of disclosing private research.
/// • Every text run is HTML-escaped and no attribute accepts raw content, so arbitrary HTML/script
///   can never enter a preview, even if a future caller passes user-adjacent strings.
/// • Output is stable for stable input (fixed key order, invariant culture), which lets CI diff
///   snapshot files.
/// </summary>
public static class AcademicPreviewRenderer
{
    public const string PreviewVersion = "1";

    public static string Render(AcademicDesignSpec spec, AcademicSampleContent? sample = null)
    {
        sample ??= AcademicSampleContent.For(spec);
        var builder = new StringBuilder(8192);
        var dir = spec.IsRtl ? "rtl" : "ltr";
        var align = spec.IsRtl ? "right" : "left";
        var (marginLeft, marginRight) = spec.MirroredSideMargins();
        var culture = CultureInfo.InvariantCulture;

        builder.Append("<div class=\"academic-preview\" dir=\"").Append(dir)
            .Append("\" lang=\"").Append(Esc(spec.LanguageCode))
            .Append("\" style=\"").Append(Style(
                ("width", $"{spec.PaperWidthPt:0.#}pt"),
                ("background", spec.BackgroundColorHex),
                ("color", spec.TextColorHex),
                ("font-family", AcademicRtlPolicy.CssBodyStack(spec)),
                ("font-size", $"{spec.BodySizePt:0.##}pt"),
                ("line-height", spec.LineSpacing.ToString("0.###", culture))))
            .Append('>');

        builder.Append("<div class=\"academic-preview-banner\" role=\"note\" style=\"").Append(Style(
                ("padding", "6pt 8pt"),
                ("font-size", "8pt"),
                ("font-family", AcademicRtlPolicy.CssCaptionStack(spec)),
                ("background", MixHex(spec.AccentColorHex, spec.BackgroundColorHex, 0.08)),
                ("border", $"0.5pt solid {EscapeCssColor(spec.AccentColorHex)}")))
            .Append("\">").Append(Esc(sample.Banner)).Append("</div>");

        RenderCover(builder, spec, sample, dir);
        if (spec.IncludesApprovalPage) RenderApproval(builder, spec, sample);
        if (spec.IncludesAiUseDisclosureSlot) RenderDisclosure(builder, spec, sample, align);
        if (spec.IncludeToc) RenderToc(builder, spec, sample, align);
        RenderBody(builder, spec, sample, align);
        RenderBibliography(builder, spec, sample, align);
        RenderPageFurniture(builder, spec, sample, align);
        RenderApproximatePagination(builder, spec);

        builder.Append("</div>");
        return builder.ToString();
    }

    private static void RenderCover(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string dir)
    {
        if (string.Equals(spec.CoverStyle, "none", StringComparison.Ordinal)) return;
        var centered = spec.CoverStyle is "classic-title-page" or "minimal-centered";
        builder.Append("<section class=\"academic-cover academic-cover-").Append(Esc(spec.CoverStyle)).Append("\" aria-label=\"Title page sample\" style=\"").Append(Style(
            ("padding", "24pt"),
            ("text-align", centered ? "center" : dir))).Append("\">");
        if (spec.CoverStyle == "institutional-band")
        {
            builder.Append("<div class=\"academic-cover-band\" style=\"").Append(Style(
                ("background", EscapeCssColor(spec.AccentColorHex)),
                ("color", "#ffffff"),
                ("padding", "10pt 12pt"),
                ("font-family", AcademicRtlPolicy.CssHeadingStack(spec)),
                ("font-size", $"{spec.HeadingSizePt(2):0.#}pt")))
                .Append("\">").Append(Esc(sample.InstitutionLine)).Append("</div>");
        }
        builder.Append("<h1 class=\"academic-cover-title\" style=\"").Append(Style(
                ("font-family", AcademicRtlPolicy.CssHeadingStack(spec)),
                ("font-size", $"{spec.HeadingSizePt(1):0.#}pt"),
                ("text-transform", spec.H1Uppercase ? "uppercase" : "none"),
                ("margin", "16pt 0 6pt")))
            .Append("\">").Append(Esc(sample.Title)).Append("</h1>");
        if (spec.CoverStyle == "two-part")
        {
            builder.Append("<div class=\"academic-cover-separator\" style=\"").Append(Style(
                ("border-top", $"1pt solid {EscapeCssColor(spec.AccentColorHex)}"),
                ("margin", "12pt auto"),
                ("width", "40%")))
                .Append("\" aria-hidden=\"true\"></div>");
        }
        builder.Append("<p class=\"academic-cover-subtitle\" style=\"").Append(Style(("margin", "0 0 12pt")))
            .Append("\">").Append(Esc(sample.Subtitle)).Append("</p>");
        builder.Append("<p class=\"academic-cover-author\">").Append(Esc(sample.AuthorLine)).Append("</p>");
        if (spec.CoverStyle != "institutional-band")
            builder.Append("<p class=\"academic-cover-institution\">").Append(Esc(sample.InstitutionLine)).Append("</p>");
        builder.Append("<p class=\"academic-cover-date\">").Append(Esc(sample.DateLine)).Append("</p>");
        builder.Append("</section>");
    }

    private static void RenderApproval(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample)
    {
        builder.Append("<section class=\"academic-approval\" aria-label=\"Approval page sample\" style=\"").Append(Style(("padding", "16pt 24pt")))
            .Append("><h2 style=\"").Append(Style(("font-family", AcademicRtlPolicy.CssHeadingStack(spec)), ("font-size", $"{spec.HeadingSizePt(2):0.#}pt")))
            .Append("\">").Append(Esc(sample.PageLabelWord)).Append(" — approval slot</h2><p>").Append(Esc(sample.AuthorLine)).Append(" · ").Append(Esc(sample.DateLine)).Append("</p></section>");
    }

    private static void RenderDisclosure(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string align)
    {
        builder.Append("<section class=\"academic-disclosure\" aria-label=\"AI use disclosure slot\" style=\"").Append(Style(("padding", "12pt 16pt")))
            .Append("><h3 style=\"").Append(Style(("font-family", AcademicRtlPolicy.CssHeadingStack(spec)), ("font-size", $"{spec.HeadingSizePt(3):0.#}pt"), ("text-align", align)))
            .Append("\">").Append(Esc(sample.AiDisclosureHeading)).Append("</h3><p style=\"margin:0\">").Append(Esc(sample.AiDisclosureText)).Append("</p></section>");
    }

    private static void RenderToc(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string align)
    {
        builder.Append("<nav class=\"academic-toc\" aria-label=\"Contents sample\" style=\"").Append(Style(("padding", "12pt 16pt")))
            .Append("><h2 style=\"").Append(Style(("font-family", AcademicRtlPolicy.CssHeadingStack(spec)), ("font-size", $"{spec.HeadingSizePt(2):0.#}pt"), ("text-align", align)))
            .Append("\">").Append(Esc(sample.ContentsHeading)).Append("</h2><ol style=\"list-style:none;margin:0;padding:0\">");
        foreach (var entry in sample.Contents.Take(Math.Max(1, spec.TocDepth) * 4))
        {
            var indent = entry.Number.Contains('.') ? 12 : 0;
            builder.Append("<li style=\"").Append(Style(
                ("padding", "2pt 0"),
                ("padding-left", $"{indent}pt"),
                ("border-bottom", spec.TocDotLeaders ? "0.5pt dotted currentColor" : "none")))
                .Append("><span>").Append(Esc(entry.Number.Length > 0 ? entry.Number + " " + entry.Title : entry.Title))
                .Append("</span><span style=\"float:inline-end\">").Append(Esc(spec.DigitSystem == "arabic-indic" ? AcademicRtlPolicy.FormatDigits(entry.Page, spec) : entry.Page)).Append("</span></li>");
        }
        builder.Append("</ol></nav>");
    }

    private static void RenderBody(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string align)
    {
        builder.Append("<article class=\"academic-body\" style=\"").Append(Style(
            ("padding", "16pt"),
            ("text-align", spec.JustifyBody ? "justify" : align),
            ("text-indent", spec.FirstLineIndentPt > 0 ? $"{spec.FirstLineIndentPt:0.#}pt" : "0"),
            ("column-count", spec.ColumnCount.ToString(CultureInfo.InvariantCulture)),
            ("column-gap", $"{spec.ColumnGapPt:0.#}pt")))
            .Append('>');

        foreach (var (heading, body, subHeading) in sample.Sections)
        {
            builder.Append("<h2 class=\"academic-h1\" style=\"").Append(Style(
                    ("font-family", AcademicRtlPolicy.CssHeadingStack(spec)),
                    ("font-size", $"{spec.HeadingSizePt(1):0.#}pt"),
                    ("font-weight", spec.HeadingsBold ? "700" : "400"),
                    ("text-transform", spec.H1Uppercase ? "uppercase" : "none"),
                    ("margin-top", $"{spec.HeadingTopGapPt:0.#}pt"),
                    ("text-align", align)))
                .Append("\">").Append(Esc(Numbered(heading, spec, "h1"))).Append("</h2>");
            builder.Append("<p style=\"margin:0 0 ").Append(spec.ParagraphAfterPt.ToString("0.##", CultureInfo.InvariantCulture)).Append("pt\">").Append(Esc(body)).Append("</p>");
            if (spec.FootnotesEnabled && body.Contains("[1", StringComparison.Ordinal))
            {
                builder.Append("<p class=\"academic-footnote\" style=\"").Append(Style(
                    ("font-size", $"{spec.BodySizePt * 0.83:0.#}pt"),
                    ("border-top", $"0.5pt solid {EscapeCssColor(spec.TextColorHex)}"),
                    ("padding-top", "4pt")))
                    .Append("\">").Append(Esc(sample.FootnoteText)).Append("</p>");
            }
            if (subHeading is not null)
            {
                builder.Append("<h3 class=\"academic-h2\" style=\"").Append(Style(
                        ("font-family", AcademicRtlPolicy.CssHeadingStack(spec)),
                        ("font-size", $"{spec.HeadingSizePt(2):0.#}pt"),
                        ("font-weight", spec.HeadingsBold ? "700" : "400"),
                        ("margin-top", $"{spec.HeadingTopGapPt / 2:0.#}pt"),
                        ("text-align", align)))
                    .Append("\">").Append(Esc(subHeading)).Append("</h3>");
            }
        }

        builder.Append("<figure class=\"academic-table\" style=\"margin:8pt 0\"><figcaption style=\"").Append(Style(
                ("font-family", AcademicRtlPolicy.CssCaptionStack(spec)),
                ("font-size", $"{spec.BodySizePt * 0.85:0.#}pt"),
                ("caption-side", spec.TableCaptionPosition == "above" ? "top" : "bottom")))
            .Append("\">").Append(Esc(sample.TableCaption)).Append("</figcaption>");
        builder.Append("<table style=\"border-collapse:collapse;width:100%;font-size:").Append((spec.BodySizePt * 0.9).ToString("0.#", CultureInfo.InvariantCulture)).Append("pt\">");
        foreach (var (rowIndex, row) in sample.TableRows.Select((row, index) => (index, row)))
            builder.Append("<tr>").Append(string.Concat(row.Select(cell =>
                "<td style=\"border:0.5pt solid currentColor;padding:3pt 5pt".ToString()
                + (rowIndex == 0 ? ";font-weight:600" : "") + "\">" + Esc(cell) + "</td>"))).Append("</tr>");
        builder.Append("</table></figure>");

        builder.Append("<figure class=\"academic-figure\" style=\"margin:8pt 0\"><div aria-hidden=\"true\" style=\"").Append(Style(
                ("border", $"0.5pt dashed {EscapeCssColor(spec.AccentColorHex)}"),
                ("height", "42pt"),
                ("background", MixHex(spec.AccentColorHex, spec.BackgroundColorHex, 0.06))))
            .Append("\"></div><figcaption style=\"").Append(Style(
                ("font-family", AcademicRtlPolicy.CssCaptionStack(spec)),
                ("font-size", $"{spec.BodySizePt * 0.85:0.#}pt")))
            .Append("\">").Append(Esc(sample.FigureCaption)).Append("</figcaption></figure>");

        builder.Append("<blockquote class=\"academic-quote\" style=\"").Append(Style(
                ("margin", "10pt 0"),
                ("padding", "4pt 14pt"),
                ("border-inline-start", $"2pt solid {EscapeCssColor(spec.AccentColorHex)}"),
                ("font-size", $"{spec.BodySizePt * 0.95:0.#}pt")))
            .Append('>').Append(Esc(sample.QuoteText))
            .Append("<cite style=\"display:block;font-style:normal\">").Append(Esc(sample.QuoteAttribution)).Append("</cite></blockquote>");

        builder.Append("<p class=\"academic-appendix\" style=\"margin-top:12pt\"><strong>").Append(Esc(sample.AppendixHeading)).Append("</strong> ").Append(Esc(sample.AppendixText)).Append("</p>");
        builder.Append("</article>");
    }

    private static void RenderBibliography(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string align)
    {
        builder.Append("<section class=\"academic-bibliography\" aria-label=\"Reference formatting sample\" style=\"").Append(Style(("padding", "12pt 16pt")))
            .Append("><h2 style=\"").Append(Style(("font-family", AcademicRtlPolicy.CssHeadingStack(spec)), ("font-size", $"{spec.HeadingSizePt(2):0.#}pt"), ("text-align", align)))
            .Append("\">").Append(Esc(sample.BibliographyHeading)).Append("</h2>");
        foreach (var entry in sample.Bibliography)
            builder.Append("<p class=\"academic-bib-entry\" style=\"").Append(Style(
                    ("margin", "0 0 4pt"),
                    ("padding-left", spec.BibliographyHangingIndent && !spec.IsRtl ? $"{spec.BibliographyIndentPt:0.#}pt" : "0"),
                    ("padding-right", spec.BibliographyHangingIndent && spec.IsRtl ? $"{spec.BibliographyIndentPt:0.#}pt" : "0"),
                    ("text-indent", spec.BibliographyHangingIndent ? $"-{spec.BibliographyIndentPt:0.#}pt" : "0")))
                .Append(">").Append(Esc(entry.Text)).Append("</p>");
        builder.Append("</section>");
    }

    private static void RenderPageFurniture(StringBuilder builder, AcademicDesignSpec spec, AcademicSampleContent sample, string align)
    {
        if (spec.PageNumberPosition == "none" && spec.RunningHeadContent == "none") return;
        var alignment = spec.PageNumberPhysicalAlignment();
        builder.Append("<footer class=\"academic-folio\" aria-label=\"Page number sample\" style=\"").Append(Style(
                ("padding", "8pt 16pt"),
                ("font-family", AcademicRtlPolicy.CssCaptionStack(spec)),
                ("font-size", $"{spec.BodySizePt * 0.85:0.#}pt"),
                ("text-align", alignment == "none" ? align : alignment)))
            .Append('>').Append(Esc(sample.PageLabelWord)).Append(" ")
            .Append(Esc(AcademicRtlPolicy.PageLabel(3, spec.BodyNumbering, spec))).Append("</footer>");
        if (spec.RunningHeadContent != "none")
        {
            builder.Append("<header class=\"academic-running-head\" aria-label=\"Running head sample\" style=\"").Append(Style(
                    ("padding", "6pt 16pt"),
                    ("font-family", AcademicRtlPolicy.CssCaptionStack(spec)),
                    ("font-size", $"{spec.BodySizePt * 0.8:0.#}pt"),
                    ("border-bottom", spec.RunningHeadRulePt > 0 ? $"{spec.RunningHeadRulePt:0.#}pt solid {EscapeCssColor(spec.AccentColorHex)}" : "none"),
                    ("text-align", align)))
                .Append('>').Append(Esc(sample.Title)).Append("</header>");
        }
    }

    private static void RenderApproximatePagination(StringBuilder builder, AcademicDesignSpec spec)
    {
        var pages = AcademicPaginationModel.EstimatedPages(spec, new[] { spec.LanguageCode });
        builder.Append("<div class=\"academic-approx-pagination\" role=\"note\" style=\"").Append(Style(
                ("padding", "6pt 16pt"),
                ("font-size", "8pt"),
                ("font-family", AcademicRtlPolicy.CssCaptionStack(spec))))
            .Append("\">Approximate sample pagination: ~").Append(pages)
            .Append(" page(s) at ").Append(AcademicPaginationModel.LinesPerPage(spec)).Append(" modelled body lines/page.</div>");
    }

    private static string Numbered(string heading, AcademicDesignSpec spec, string _) => heading;

    private static string Esc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EscapeCssColor(string hex) =>
        System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{3,8}$") ? hex : "#000000";

    private static string Style(params (string Property, string Value)[] declarations) =>
        string.Join(";", declarations.Select(pair => $"{pair.Property.Replace('_', '-')}:{pair.Value.Replace('"', (char)39)}")) + ";";

    /// <summary>Mixes two hex colours (0..1 weight toward <paramref name="foreground"/>) for tinted
    /// banner backgrounds without introducing raw alpha syntax into inline styles.</summary>
    private static string MixHex(string foreground, string background, double weight)
    {
        var f = Parse(foreground);
        var b = Parse(background);
        int Blend(int x, int y) => (int)Math.Round(x * weight + y * (1 - weight));
        return $"#{Blend(f.Item1, b.Item1):X2}{Blend(f.Item2, b.Item2):X2}{Blend(f.Item3, b.Item3):X2}";
    }

    private static (int, int, int) Parse(string hex)
    {
        var value = EscapeCssColor(hex)[1..];
        if (value.Length == 3) value = string.Concat(value.Select(c => $"{c}{c}"));
        return (Convert.ToInt32(value[..2], 16), Convert.ToInt32(value.Substring(2, 2), 16), Convert.ToInt32(value.Substring(4, 2), 16));
    }
}

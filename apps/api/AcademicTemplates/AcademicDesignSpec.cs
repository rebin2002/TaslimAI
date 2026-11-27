using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// Taxonomy constants shared by the template registry, the gallery DTOs and the web preview components.
/// Slugs are wire-stable identifiers: they appear inside versioned configuration ids and inside the
/// committed catalog JSON, so renaming one is a template-definition breaking change (bump
/// <see cref="AcademicTemplateDefinition.CurrentDefinitionVersion"/> when that happens).
/// </summary>
public static class AcademicTemplateTaxonomy
{
    public static readonly IReadOnlyList<string> DocumentKinds =
        new[] { "thesis", "dissertation", "research-proposal", "literature-review", "journal-article", "case-study", "lab-report", "technical-report", "monograph", "capstone-project" };

    public static readonly IReadOnlyList<string> CitationStyles =
        new[] { "apa-7", "mla-9", "chicago-notes-bibliography", "chicago-author-date", "ieee", "harvard", "vancouver", "turabian", "bluebook-21" };

    public static readonly IReadOnlyList<string> Languages = new[] { "en", "ar", "ku" };

    public static readonly IReadOnlyList<string> PaperFormats = new[] { "a4", "letter", "a5" };

    public static readonly IReadOnlyList<string> Directions = new[] { AcademicTextDirection.Ltr, AcademicTextDirection.Rtl };
}

/// <summary>Text direction constants. Mirrors the "ku"/"ar" language codes the Document renderer already uses.</summary>
public static class AcademicTextDirection
{
    public const string Ltr = "ltr";
    public const string Rtl = "rtl";
}

/// <summary>
/// The single canonical design specification for one selectable academic export configuration.
/// The same instance drives the gallery preview HTML, the DOCX style table / section properties and the
/// PDF layout, so the three representations cannot drift apart on the values students submit against
/// (margins, line spacing, page numbering, heading hierarchy, bibliography indentation).
/// Geometry is expressed in points (1pt = 1/72in) and converted to the DOCX twip grid at the renderer
/// boundary only. Values are deliberately explicit — no "auto" sentinels — because the canonical hash
/// below defines template distinctness: two configurations that differ only in unset-vs-default would
/// otherwise be silently considered different designs.
/// </summary>
public sealed record AcademicDesignSpec
{
    public const int TwipsPerPoint = 20;

    // ── Identity (excluded from the design hash: identity is derived, design is canonical) ──
    public string TemplateId { get; init; } = "";
    public string ConfigurationId { get; init; } = "";

    // ── Language & direction ──
    public string LanguageCode { get; init; } = "en";
    public string Direction { get; init; } = AcademicTextDirection.Ltr;

    // ── Page geometry (points) ──
    public string PaperFormat { get; init; } = "a4";
    public double MarginTopPt { get; init; } = 72;
    public double MarginBottomPt { get; init; } = 72;
    public double MarginLeftPt { get; init; } = 90;
    public double MarginRightPt { get; init; } = 90;
    /// <summary>Extra allowance on the binding edge (thesis spines). Mirrored to the right for RTL.</summary>
    public double BindingEdgePt { get; init; } = 0;
    /// <summary>Physical page edge carrying the binding allowance. RTL templates state "right" explicitly
    /// (Kurdish/Arabic volumes are spine-right), so no implicit mirror can double-flip the gutter.</summary>
    public string BindingEdge { get; init; } = "left"; // left | right (physical edge)
    public int ColumnCount { get; init; } = 1;
    public double ColumnGapPt { get; init; } = 18;

    // ── Typography ──
    public string BodyFontToken { get; init; } = "serif-classic";
    public string HeadingFontToken { get; init; } = "serif-classic";
    public string CaptionFontToken { get; init; } = "serif-classic";
    public string MonoFontToken { get; init; } = "mono-code";
    public double BodySizePt { get; init; } = 12;
    /// <summary>Modular scale ratio: H1 = body × ratio³, H2 = ratio², H3 = ratio¹.</summary>
    public double TypeScaleRatio { get; init; } = 1.25;
    public double LineSpacing { get; init; } = 1.5;
    public double ParagraphAfterPt { get; init; } = 8;
    public double FirstLineIndentPt { get; init; } = 0;
    public bool JustifyBody { get; init; }
    public double HeadingTopGapPt { get; init; } = 18;
    public bool HeadingsBold { get; init; } = true;
    public bool H1Uppercase { get; init; }
    public string HeadingNumbering { get; init; } = "decimal"; // decimal | outline | none
    public int HeadingLevels { get; init; } = 3;
    /// <summary>Digits used for page numbers and heading numbers: "latin" or "arabic-indic" (٠١٢…).</summary>
    public string DigitSystem { get; init; } = "latin";

    // ── Cover & front matter ──
    public string CoverStyle { get; init; } = "classic-title-page"; // classic-title-page | institutional-band | minimal-centered | two-part | none
    public bool IncludesApprovalPage { get; init; }
    /// <summary>Bilingual templates reserve an RTL abstract page (e.g. an English thesis carrying a
    /// Kurdish/Arabic ملخص abstract) whose paragraphs flow right-to-left independently of the body.</summary>
    public bool SupportsRtlAbstractPage { get; init; }
    /// <summary>
    /// When true the template carries a clearly-labelled "statement of generative-AI use" slot on the
    /// front matter so the disclosure field required by many institutions is present by default.
    /// This is an integrity affordance — it never hides authorship.
    /// </summary>
    public bool IncludesAiUseDisclosureSlot { get; init; }

    // ── Table of contents ──
    public bool IncludeToc { get; init; } = true;
    public int TocDepth { get; init; } = 2;
    public bool TocDotLeaders { get; init; } = true;
    public bool IncludeListOfFiguresTables { get; init; }

    // ── Pagination, headers & footers ──
    public string FrontMatterNumbering { get; init; } = "roman-lower"; // roman-lower | arabic | none
    public string BodyNumbering { get; init; } = "arabic";
    public bool RestartNumbersAfterFrontMatter { get; init; } = true;
    public string PageNumberPosition { get; init; } = "bottom-center"; // bottom-center | bottom-inside | bottom-outside | top-right | top-center | none
    public string RunningHeadContent { get; init; } = "none"; // title | author | short-title | none
    public double RunningHeadRulePt { get; init; }

    // ── Citations, notes & bibliography ──
    public string DefaultCitationStyle { get; init; } = "apa-7";
    public string CitationMarkers { get; init; } = "author-date"; // author-date | numeric | notes
    public bool FootnotesEnabled { get; init; }
    public string FootnotePlacement { get; init; } = "bottom-of-page"; // bottom-of-page | end-of-document
    public bool BibliographyHangingIndent { get; init; } = true;
    public double BibliographyIndentPt { get; init; } = 36;

    // ── Figures & tables ──
    public string FigureCaptionPosition { get; init; } = "below";
    public string TableCaptionPosition { get; init; } = "above";
    public string FigureNumbering { get; init; } = "chapter-dot"; // chapter-dot | continuous

    // ── Colour & accessibility profile ──
    public string TextColorHex { get; init; } = AcademicAccessibilityPolicy.DefaultTextColor;
    public string BackgroundColorHex { get; init; } = "#ffffff";
    public string AccentColorHex { get; init; } = "#1f3a5f";
    /// <summary>Declared so exporters can promise embedding where the format supports it (PDF yes, DOCX deferred to the host reader).</summary>
    public bool EmbedFontsInPdf { get; init; } = true;

    // ── Derived geometry ──

    public double PaperWidthPt => PaperFormat switch
    {
        "letter" => 612,
        "a5" => 419.53,
        _ => 595.28, // a4
    };

    public double PaperHeightPt => PaperFormat switch
    {
        "letter" => 792,
        "a5" => 595.28,
        _ => 841.89, // a4
    };

    public bool IsRtl => string.Equals(Direction, AcademicTextDirection.Rtl, StringComparison.Ordinal);

    /// <summary>
    /// Left/right margin including the binding allowance on its declared physical edge, as a PDF/preview
    /// would lay the page out. The DOCX writer keeps the gutter separate (Word's <c>w:gutter</c> plus
    /// <c>w:rtlGutter</c> on spine-right templates) so the
    /// printed binding allowance stays distinct from the text block.
    /// </summary>
    public (double LeftPt, double RightPt) MirroredSideMargins()
    {
        var gutterLeft = string.Equals(BindingEdge, "right", StringComparison.Ordinal) ? 0 : BindingEdgePt;
        var gutterRight = string.Equals(BindingEdge, "right", StringComparison.Ordinal) ? BindingEdgePt : 0;
        return (MarginLeftPt + gutterLeft, MarginRightPt + gutterRight);
    }

    /// <summary>Physical page number horizontal intent: "inside/outside" resolve against the spine, so
    /// they flip between LTR and RTL. Returns one of left | center | right.</summary>
    public string PageNumberPhysicalAlignment() => PageNumberPosition switch
    {
        "bottom-center" or "top-center" => "center",
        "bottom-inside" => IsRtl ? "right" : "left",
        "bottom-outside" => IsRtl ? "left" : "right",
        "top-right" => "right",
        _ => "none",
    };

    public double ContentWidthPt => Math.Max(1, PaperWidthPt - MirroredSideMargins().LeftPt - MirroredSideMargins().RightPt
        - (ColumnCount > 1 ? (ColumnCount - 1) * ColumnGapPt : 0));

    public double LineHeightPt => RoundPt(BodySizePt * LineSpacing);

    public double HeadingSizePt(int level) =>
        RoundPt(BodySizePt * Math.Pow(TypeScaleRatio, Math.Clamp(HeadingLevels - level + 1, 1, 4)));

    // ── Canonicalisation ──

    /// <summary>Fixed field order. Everything that visibly affects a rendered page participates here; identity fields do not.</summary>
    public string CanonicalDesignString()
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder(512);
        void Add(string name, string value) => builder.Append(name).Append('=').Append(value).Append('\u001f');
        void AddD(string name, double value) => Add(name, value.ToString("0.###", culture));
        void AddB(string name, bool value) => Add(name, value ? "1" : "0");
        void AddI(string name, int value) => Add(name, value.ToString(culture));

        Add("dir", Direction);
        Add("lang", LanguageCode);
        Add("paper", PaperFormat);
        AddD("mt", MarginTopPt);
        AddD("mb", MarginBottomPt);
        AddD("ml", MarginLeftPt);
        AddD("mr", MarginRightPt);
        AddD("gutter", BindingEdgePt);
        Add("gedge", BindingEdge);
        AddI("cols", ColumnCount);
        AddD("colgap", ColumnGapPt);
        Add("bodyFont", BodyFontToken);
        Add("headFont", HeadingFontToken);
        Add("capFont", CaptionFontToken);
        Add("monoFont", MonoFontToken);
        AddD("bodySize", BodySizePt);
        AddD("scale", TypeScaleRatio);
        AddD("leading", LineSpacing);
        AddD("paraAfter", ParagraphAfterPt);
        AddD("firstIndent", FirstLineIndentPt);
        AddB("justify", JustifyBody);
        AddD("headGap", HeadingTopGapPt);
        AddB("headBold", HeadingsBold);
        AddB("h1Upper", H1Uppercase);
        Add("headNum", HeadingNumbering);
        AddI("headLevels", HeadingLevels);
        Add("digits", DigitSystem);
        Add("cover", CoverStyle);
        AddB("approval", IncludesApprovalPage);
        AddB("rtlAbstract", SupportsRtlAbstractPage);
        AddB("aiSlot", IncludesAiUseDisclosureSlot);
        AddB("toc", IncludeToc);
        AddI("tocDepth", TocDepth);
        AddB("tocDots", TocDotLeaders);
        AddB("loft", IncludeListOfFiguresTables);
        Add("fmNum", FrontMatterNumbering);
        Add("bodyNum", BodyNumbering);
        AddB("restartNum", RestartNumbersAfterFrontMatter);
        Add("pageNumPos", PageNumberPosition);
        Add("runHead", RunningHeadContent);
        AddD("runHeadRule", RunningHeadRulePt);
        Add("citeMarkers", CitationMarkers);
        AddB("footnotes", FootnotesEnabled);
        Add("fnPlace", FootnotePlacement);
        Add("bibHanging", BibliographyHangingIndent ? "1" : "0");
        AddD("bibIndent", BibliographyIndentPt);
        Add("figCapPos", FigureCaptionPosition);
        Add("tabCapPos", TableCaptionPosition);
        Add("figNum", FigureNumbering);
        Add("textColor", TextColorHex);
        Add("bgColor", BackgroundColorHex);
        Add("accent", AccentColorHex);
        return builder.ToString();
    }

    /// <summary>
    /// Two configurations are the "same design" exactly when this hash matches. The registry deduplicates
    /// every expansion through it, which is the mechanical guarantee behind the "200 real designs, not 200
    /// aliases" acceptance rule.
    /// </summary>
    public string CanonicalDesignHash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalDesignString()));
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }

    /// <summary>
    /// Data-driven variant application: each registry option is a (property, value) delta onto the base
    /// spec. Unknown keys fail loudly — a silent no-op would collapse distinct variants into duplicates.
    /// </summary>
    public AcademicDesignSpec WithOverride(string key, string rawValue)
    {
        var culture = CultureInfo.InvariantCulture;
        double Number() => double.Parse(rawValue, CultureInfo.InvariantCulture);
        bool Flag() => rawValue is "true" or "1";
        int Integer() => int.Parse(rawValue, CultureInfo.InvariantCulture);
        return key switch
        {
            "languageCode" => this with { LanguageCode = rawValue },
            "direction" => this with { Direction = rawValue },
            "paperFormat" => this with { PaperFormat = rawValue },
            "marginTopPt" => this with { MarginTopPt = Number() },
            "marginBottomPt" => this with { MarginBottomPt = Number() },
            "marginLeftPt" => this with { MarginLeftPt = Number() },
            "marginRightPt" => this with { MarginRightPt = Number() },
            "bindingEdgePt" => this with { BindingEdgePt = Number() },
            "bindingEdge" => this with { BindingEdge = rawValue },
            "columnCount" => this with { ColumnCount = Integer() },
            "columnGapPt" => this with { ColumnGapPt = Number() },
            "bodyFontToken" => this with { BodyFontToken = rawValue },
            "headingFontToken" => this with { HeadingFontToken = rawValue },
            "captionFontToken" => this with { CaptionFontToken = rawValue },
            "monoFontToken" => this with { MonoFontToken = rawValue },
            "bodySizePt" => this with { BodySizePt = Number() },
            "typeScaleRatio" => this with { TypeScaleRatio = Number() },
            "lineSpacing" => this with { LineSpacing = Number() },
            "paragraphAfterPt" => this with { ParagraphAfterPt = Number() },
            "firstLineIndentPt" => this with { FirstLineIndentPt = Number() },
            "justifyBody" => this with { JustifyBody = Flag() },
            "headingTopGapPt" => this with { HeadingTopGapPt = Number() },
            "headingsBold" => this with { HeadingsBold = Flag() },
            "h1Uppercase" => this with { H1Uppercase = Flag() },
            "headingNumbering" => this with { HeadingNumbering = rawValue },
            "headingLevels" => this with { HeadingLevels = Integer() },
            "digitSystem" => this with { DigitSystem = rawValue },
            "coverStyle" => this with { CoverStyle = rawValue },
            "includesApprovalPage" => this with { IncludesApprovalPage = Flag() },
            "supportsRtlAbstractPage" => this with { SupportsRtlAbstractPage = Flag() },
            "includesAiUseDisclosureSlot" => this with { IncludesAiUseDisclosureSlot = Flag() },
            "includeToc" => this with { IncludeToc = Flag() },
            "tocDepth" => this with { TocDepth = Integer() },
            "tocDotLeaders" => this with { TocDotLeaders = Flag() },
            "includeListOfFiguresTables" => this with { IncludeListOfFiguresTables = Flag() },
            "frontMatterNumbering" => this with { FrontMatterNumbering = rawValue },
            "bodyNumbering" => this with { BodyNumbering = rawValue },
            "restartNumbersAfterFrontMatter" => this with { RestartNumbersAfterFrontMatter = Flag() },
            "pageNumberPosition" => this with { PageNumberPosition = rawValue },
            "runningHeadContent" => this with { RunningHeadContent = rawValue },
            "runningHeadRulePt" => this with { RunningHeadRulePt = Number() },
            "defaultCitationStyle" => this with { DefaultCitationStyle = rawValue },
            "citationMarkers" => this with { CitationMarkers = rawValue },
            "footnotesEnabled" => this with { FootnotesEnabled = Flag() },
            "footnotePlacement" => this with { FootnotePlacement = rawValue },
            "bibliographyHangingIndent" => this with { BibliographyHangingIndent = Flag() },
            "bibliographyIndentPt" => this with { BibliographyIndentPt = Number() },
            "figureCaptionPosition" => this with { FigureCaptionPosition = rawValue },
            "tableCaptionPosition" => this with { TableCaptionPosition = rawValue },
            "figureNumbering" => this with { FigureNumbering = rawValue },
            "textColorHex" => this with { TextColorHex = rawValue },
            "backgroundColorHex" => this with { BackgroundColorHex = rawValue },
            "accentColorHex" => this with { AccentColorHex = rawValue },
            _ => throw new KeyNotFoundException($"Unknown academic template override key '{key}'. Register it in WithOverride and in the web design-spec type together."),
        };
    }

    /// <summary>Applies overrides in dictionary key order (deterministic: keys are sorted at application).</summary>
    public AcademicDesignSpec WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var spec = this;
        foreach (var pair in new TreeMap(overrides))
            spec = spec.WithOverride(pair.Key, pair.Value);
        return spec;
    }

    /// <summary>Ordinal-sorted key enumeration so fold order never depends on dictionary insertion order.</summary>
    private sealed class TreeMap : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly List<KeyValuePair<string, string>> ordered;
        public TreeMap(IReadOnlyDictionary<string, string> source) =>
            ordered = source.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => ordered.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static double RoundPt(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// WCAG-style contrast enforcement for every shipped template: body text against the page background and
/// accent text (running heads, links, cover bands) against the background must both clear 4.5:1. The same
/// policy runs in the registry build (CI fails a non-compliant template) and in the web gallery.
/// </summary>
public static class AcademicAccessibilityPolicy
{
    public const string DefaultTextColor = "#1a1a1a";
    public const double MinimumTextContrastRatio = 4.5;
    public const double MinimumBodySizePt = 10;
    public const double MinimumPrintMarginPt = 54; // 0.75in — below this the copy risks the trim/binding

    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        var f = Luminance(foregroundHex);
        var b = Luminance(backgroundHex);
        var lighter = Math.Max(f, b);
        var darker = Math.Min(f, b);
        return Math.Round((lighter + 0.05) / (darker + 0.05), 2);
    }

    private static double Luminance(string hex)
    {
        var (r, g, b) = ParseRgb(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Channel(double byteValue)
    {
        var s = byteValue / 255d;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static (double R, double G, double B) ParseRgb(string hex)
    {
        var value = hex.StartsWith("#", StringComparison.Ordinal) ? hex[1..] : hex;
        if (value.Length == 3)
            value = $"{value[0]}{value[0]}{value[1]}{value[1]}{value[2]}{value[2]}";
        if (value.Length != 6 || !int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            throw new FormatException($"Colour '{hex}' is not a #rgb/#rrggbb hex literal.");
        return (((packed >> 16) & 0xFF), ((packed >> 8) & 0xFF), (packed & 0xFF));
    }
}

/// <summary>
/// Deterministic, pure estimation of how the synthetic sample flows across pages under a given spec.
/// The gallery labels it "approximate"; it exists so the preview can show running heads and the correct
/// front-matter restart without shipping a layout engine, and so tests can pin the arithmetic itself.
/// </summary>
public static class AcademicPaginationModel
{
    public static int LinesPerPage(AcademicDesignSpec spec)
    {
        var usableHeight = spec.PaperHeightPt - spec.MarginTopPt - spec.MarginBottomPt;
        if (spec.RunningHeadContent != "none") usableHeight -= spec.RunningHeadRulePt + spec.BodySizePt * spec.LineSpacing;
        var perColumn = (int)Math.Floor(usableHeight / spec.LineHeightPt);
        return Math.Max(1, perColumn) * Math.Max(1, spec.ColumnCount);
    }

    public static int EstimatedPages(AcademicDesignSpec spec, IReadOnlyList<string> paragraphLines)
    {
        var charsPerLine = Math.Max(20, spec.ContentWidthPt / (spec.BodySizePt * 0.5));
        var lines = paragraphLines
            .Select(line => Math.Max(1, (int)Math.Ceiling(line.Length / charsPerLine)))
            .Sum();
        return Math.Max(1, (int)Math.Ceiling(lines / (double)LinesPerPage(spec)));
    }
}

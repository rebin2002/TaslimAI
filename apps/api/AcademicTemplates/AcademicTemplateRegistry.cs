namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// The curated academic template library: vetted base templates plus their data-driven variant axes.
///
/// The registry is the single source of truth (the web gallery ships JSON generated from it and CI pins
/// the two together). Expansion is deterministic: fixed template order, fixed axis/option order, folding
/// every axis-option override onto the base spec, then dropping anything that violates the template's
/// compliance or accessibility profile, and finally deduplicating on the design's canonical hash. The
/// hash dedup is what makes "200+ configurations" a statement about 200+ genuinely distinct designs
/// rather than 200 aliases — a variant that would render identically to an earlier one never enters the
/// catalog.
/// </summary>
public static class AcademicTemplateRegistry
{
    private static readonly Lazy<RegistryState> State = new(Build);

    public static int DefinitionVersion => AcademicTemplateDefinition.CurrentDefinitionVersion;
    public static IReadOnlyList<AcademicTemplateDefinition> Templates => State.Value.Templates;
    public static IReadOnlyList<AcademicTemplateConfiguration> Configurations => State.Value.Configurations;

    public static AcademicTemplateDefinition? GetTemplate(string slug) =>
        Templates.FirstOrDefault(template => string.Equals(template.Slug, slug, StringComparison.Ordinal));

    public static AcademicTemplateConfiguration? GetConfiguration(string configurationId) =>
        State.Value.ByConfigurationId.TryGetValue(configurationId, out var configuration) ? configuration : null;

    /// <summary>Gallery filtering. Ordering is the registry's expansion order, so paging is stable.</summary>
    public static IReadOnlyList<AcademicTemplateConfiguration> Search(
        string? documentKind = null,
        string? citationStyle = null,
        string? language = null,
        string? paperFormat = null,
        string? direction = null,
        string? query = null,
        int limit = 50,
        int offset = 0)
    {
        var matches = EnumerateMatches(documentKind, citationStyle, language, paperFormat, direction, query);
        return matches
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 400))
            .ToList();
    }

    public static int CountMatches(
        string? documentKind = null, string? citationStyle = null, string? language = null,
        string? paperFormat = null, string? direction = null, string? query = null) =>
        EnumerateMatches(documentKind, citationStyle, language, paperFormat, direction, query).Count;

    /// <summary>Per-template configuration counts in registry order (gallery "N designs" badges).</summary>
    public static IReadOnlyDictionary<string, int> ConfigurationCountsByTemplate => State.Value.CountsByTemplate;

    private static List<AcademicTemplateConfiguration> EnumerateMatches(
        string? documentKind, string? citationStyle, string? language, string? paperFormat, string? direction, string? query)
    {
        var normalizedQuery = query?.Trim();
        // Language facets accept the friendly aliases students see in the gallery ("ckb", "ar-EG",
        // "en-GB") by normalising them to the registry's canonical codes before the membership test.
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? null : AcademicRtlPolicy.NormalizeLanguage(language);
        return Configurations
            .Where(configuration =>
            {
                var template = GetTemplate(configuration.TemplateSlug)!;
                if (!string.IsNullOrWhiteSpace(documentKind) && !template.DocumentKinds.Contains(documentKind, StringComparer.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(citationStyle) && !template.CitationStyles.Contains(citationStyle, StringComparer.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(language)
                    && !template.Languages.Contains(language, StringComparer.OrdinalIgnoreCase)
                    && !template.Languages.Contains(normalizedLanguage!, StringComparer.Ordinal)) return false;
                if (!string.IsNullOrWhiteSpace(paperFormat) && !template.PaperFormats.Contains(paperFormat, StringComparer.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(direction) && !string.Equals(template.Direction, direction, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrWhiteSpace(normalizedQuery)
                    && !configuration.DisplayName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    && !configuration.ConfigurationId.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            })
            .ToList();
    }

    private sealed record RegistryState(
        IReadOnlyList<AcademicTemplateDefinition> Templates,
        IReadOnlyList<AcademicTemplateConfiguration> Configurations,
        IReadOnlyDictionary<string, AcademicTemplateConfiguration> ByConfigurationId,
        IReadOnlyDictionary<string, int> CountsByTemplate);

    private static RegistryState Build()
    {
        var templates = DefineTemplates();
        var configurations = new List<AcademicTemplateConfiguration>();
        var seenDesignHashes = new HashSet<string>(StringComparer.Ordinal);
        var seenConfigurationIds = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var template in templates)
        {
            var templateCount = 0;
            foreach (var combination in Combinations(template.Axes))
            {
                var spec = template.BaseSpec with { TemplateId = template.TemplateId };
                var selection = new Dictionary<string, string>(StringComparer.Ordinal);
                var labels = new List<string> { template.DisplayName };
                foreach (var (axis, option) in combination)
                {
                    spec = spec.WithOverrides(option.SpecOverrides);
                    selection[axis.Name] = option.Value;
                    labels.Add(option.DisplayName);
                }

                var configurationId = combination.Count == 0
                    ? template.Slug
                    : template.Slug + "." + string.Join(".", combination.Select(pair => Sanitize(pair.Item2.Value)));
                if (!seenConfigurationIds.Add(configurationId))
                    throw new InvalidOperationException($"Academic template configuration id collision for '{configurationId}'.");

                spec = spec with { ConfigurationId = configurationId };

                // A configuration only ships when it clears both floors its own documents demand.
                if (template.Compliance.Violations(spec).Count > 0) continue;
                if (!AcademicRtlPolicy.IsDirectionConsistent(spec)) continue;
                if (AcademicAccessibilityPolicy.ContrastRatio(spec.TextColorHex, spec.BackgroundColorHex)
                        < AcademicAccessibilityPolicy.MinimumTextContrastRatio) continue;
                if (AcademicAccessibilityPolicy.ContrastRatio(spec.AccentColorHex, spec.BackgroundColorHex)
                        < AcademicAccessibilityPolicy.MinimumTextContrastRatio) continue;
                if (spec.BodySizePt < AcademicAccessibilityPolicy.MinimumBodySizePt) continue;
                if (!seenDesignHashes.Add(spec.CanonicalDesignHash())) continue; // no alias-to-identical designs

                configurations.Add(new AcademicTemplateConfiguration(
                    configurationId,
                    template.Slug,
                    string.Join(" · ", labels),
                    selection,
                    spec,
                    spec.CanonicalDesignHash()));
                templateCount++;
            }
            counts[template.Slug] = templateCount;
        }

        return new RegistryState(
            templates,
            configurations,
            configurations.ToDictionary(configuration => configuration.ConfigurationId, StringComparer.Ordinal),
            counts);
    }

    /// <summary>Cartesian product in fixed axis order; a template with no axes yields the bare base.</summary>
    private static IEnumerable<IReadOnlyList<(AcademicVariantAxis Axis, AcademicVariantOption Option)>> Combinations(
        IReadOnlyList<AcademicVariantAxis> axes)
    {
        IEnumerable<List<(AcademicVariantAxis, AcademicVariantOption)>> running = new[] { new List<(AcademicVariantAxis, AcademicVariantOption)>() };
        foreach (var axis in axes)
            running = running.SelectMany(prefix => axis.Options.Select(option =>
            {
                var next = new List<(AcademicVariantAxis, AcademicVariantOption)>(prefix) { (axis, option) };
                return next;
            }));
        return running;
    }

    private static string Sanitize(string value) => value.Replace("-", "").Replace(" ", "");

    // ── authoring helpers ──────────────────────────────────────────────────────────────
    private static AcademicVariantOption Opt(string value, string label, params (string Key, string Value)[] overrides) =>
        new(value, label, overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static AcademicVariantAxis Axis(string name, string label, params AcademicVariantOption[] options) =>
        new(name, label, options);

    /// <summary>Font-pair option: body + heading + caption move together (a half-swapped pairing is a
    /// separate axis of its own, not an accident of this one).</summary>
    private static AcademicVariantOption FontPair(string value, string label, string body, string heading, string caption) =>
        Opt(value, label, ("bodyFontToken", body), ("headingFontToken", heading), ("captionFontToken", caption));

    private static AcademicVariantAxis FontAxis(params AcademicVariantOption[] pairs) => Axis("fonts", "Type pairing", pairs);

    // ── the catalogue itself ───────────────────────────────────────────────────────────
    private static IReadOnlyList<AcademicTemplateDefinition> DefineTemplates() => new[]
    {
        ClassicThesis(),
        ModernDissertation(),
        UkPostgraduateDissertation(),
        UsDoctoralDissertation(),
        ResearchProposalStandard(),
        LiteratureReviewApa(),
        JournalDoubleColumn(),
        JournalSingleColumn(),
        IeeeConferencePaper(),
        CaseStudyReport(),
        LabReportImrad(),
        ChicagoHumanitiesMonograph(),
        BluebookLegalReport(),
        TechnicalReportHei(),
        CapstoneProject(),
        KurdishThesis(),
        ArabicDissertation(),
        ArabicResearchProposal(),
        KurdishLiteratureReview(),
        BilingualAbstractThesis(),
        ArabicMonographClassic(),
        KurdishFieldReport(),
    };

    private static AcademicTemplateDefinition ClassicThesis() => new()
    {
        Slug = "classic-thesis",
        DisplayName = "Classic Thesis",
        Description = "Two-inch spine margin, roman front matter restarting arabic at chapter one — the wide-times lineage most graduate schools still describe.",
        DocumentKinds = new[] { "thesis", "dissertation" },
        CitationStyles = new[] { "apa-7", "chicago-author-date", "harvard" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "General North-American / European graduate thesis convention",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 108, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 8,
            CoverStyle = "classic-title-page", IncludesApprovalPage = true, IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true, IncludeListOfFiguresTables = true,
            FrontMatterNumbering = "roman-lower", BodyNumbering = "arabic", RestartNumbersAfterFrontMatter = true,
            PageNumberPosition = "bottom-center", HeadingNumbering = "decimal",
            DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("cambria", "Cambria", "serif-humanist", "serif-humanist", "serif-humanist"),
                FontPair("serif-sans-heads", "Serif body, sans heads", "serif-humanist", "sans-modern", "sans-modern")),
            Axis("leading", "Leading",
                Opt("150", "1.5 spacing", ("lineSpacing", "1.5")),
                Opt("200", "Double spaced", ("lineSpacing", "2"))),
            Axis("cover", "Cover",
                Opt("classic", "Classic title page", ("coverStyle", "classic-title-page")),
                Opt("band", "Institutional band", ("coverStyle", "institutional-band"), ("accentColorHex", "#22406a"))),
            Axis("numbering", "Page numbers",
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center")),
                Opt("outside", "Bottom outside", ("pageNumberPosition", "bottom-outside"))),
        },
    };

    private static AcademicTemplateDefinition ModernDissertation() => new()
    {
        Slug = "modern-dissertation",
        DisplayName = "Modern Dissertation",
        Description = "Sans-serif heading architecture over a serif body with optional running heads — for programs that dropped the 1970s look but kept the rules.",
        DocumentKinds = new[] { "dissertation", "thesis" },
        CitationStyles = new[] { "apa-7", "ieee", "vancouver" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Contemporary interdisciplinary dissertation layout",
        Compliance = AcademicComplianceProfile.Dissertation,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 85, MarginBottomPt = 85, MarginLeftPt = 95, MarginRightPt = 85, BindingEdgePt = 14,
            BodyFontToken = "serif-humanist", HeadingFontToken = "sans-modern", CaptionFontToken = "sans-modern",
            BodySizePt = 11.5, TypeScaleRatio = 1.3, LineSpacing = 1.4, ParagraphAfterPt = 10,
            CoverStyle = "minimal-centered", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 3, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            AccentColorHex = "#31567f",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("palatino-calibri", "Palatino · Calibri", "serif-humanist", "sans-modern", "sans-modern"),
                FontPair("times-arial", "Times · Arial", "serif-classic", "sans-neutral", "sans-neutral"),
                FontPair("all-sans", "All sans", "sans-modern", "sans-modern", "sans-modern")),
            Axis("leading", "Leading",
                Opt("115", "1.15", ("lineSpacing", "1.15")),
                Opt("150", "1.5", ("lineSpacing", "1.5"))),
            Axis("cover", "Cover",
                Opt("minimal", "Minimal centred", ("coverStyle", "minimal-centered")),
                Opt("two-part", "Two-part cover", ("coverStyle", "two-part"))),
            Axis("heads", "Running head",
                Opt("none", "No running head", ("runningHeadContent", "none")),
                Opt("title", "Title running head", ("runningHeadContent", "short-title"), ("runningHeadRulePt", "6"))),
        },
    };

    private static AcademicTemplateDefinition UkPostgraduateDissertation() => new()
    {
        Slug = "uk-postgraduate-dissertation",
        DisplayName = "UK Postgraduate Dissertation",
        Description = "3 cm all-round margins on A4, 1.5 leading with a separate title and acknowledgement page — the UK taught-master's shape.",
        DocumentKinds = new[] { "dissertation", "thesis", "capstone-project" },
        CitationStyles = new[] { "harvard", "apa-7", "chicago-author-date" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "UK higher-education postgraduate convention",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 85, MarginBottomPt = 85, MarginLeftPt = 85, MarginRightPt = 85,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.2, LineSpacing = 1.5, ParagraphAfterPt = 6,
            CoverStyle = "classic-title-page", IncludesApprovalPage = false, IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", DefaultCitationStyle = "harvard", CitationMarkers = "author-date",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("arial", "Arial body", "sans-neutral", "sans-neutral", "sans-neutral")),
            Axis("leading", "Leading",
                Opt("150", "1.5", ("lineSpacing", "1.5")),
                Opt("200", "Double", ("lineSpacing", "2"))),
            Axis("numbering", "Page numbers",
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center")),
                Opt("topright", "Top right", ("pageNumberPosition", "top-right"))),
        },
    };

    private static AcademicTemplateDefinition UsDoctoralDissertation() => new()
    {
        Slug = "us-doctoral-dissertation",
        DisplayName = "US Doctoral Dissertation",
        Description = "The double-spaced, 1.5-inch-gutter doctoral standard with approval page and front-matter roman numbering, sized for Letter stock.",
        DocumentKinds = new[] { "dissertation" },
        CitationStyles = new[] { "chicago-notes-bibliography", "apa-7", "turabian" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "letter", "a4" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "US graduate-school doctoral submission convention",
        Compliance = AcademicComplianceProfile.Dissertation,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "letter",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 108, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.25, LineSpacing = 2, ParagraphAfterPt = 0, FirstLineIndentPt = 36,
            CoverStyle = "classic-title-page", IncludesApprovalPage = true, IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 3, TocDotLeaders = true, IncludeListOfFiguresTables = true,
            FrontMatterNumbering = "roman-lower", BodyNumbering = "arabic", RestartNumbersAfterFrontMatter = true,
            PageNumberPosition = "bottom-center", HeadingNumbering = "decimal",
            DefaultCitationStyle = "turabian", CitationMarkers = "notes", FootnotesEnabled = true,
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times New Roman", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("garamond", "Garamond body", "serif-humanist", "serif-humanist", "serif-humanist")),
            Axis("cover", "Cover",
                Opt("classic", "Classic", ("coverStyle", "classic-title-page")),
                Opt("hardbound", "Hardbound style", ("coverStyle", "institutional-band"), ("accentColorHex", "#1d1d3b"))),
            Axis("heads", "Chapter heads",
                Opt("numbered", "Numbered", ("headingNumbering", "decimal")),
                Opt("unnumbered", "Unnumbered", ("headingNumbering", "none"), ("h1Uppercase", "true"))),
        },
    };

    private static AcademicTemplateDefinition ResearchProposalStandard() => new()
    {
        Slug = "research-proposal-standard",
        DisplayName = "Research Proposal",
        Description = "Tight, reviewer-friendly proposal grid: budget and timetable tables are first-class sections, two-page summary keeps its own numbering.",
        DocumentKinds = new[] { "research-proposal" },
        CitationStyles = new[] { "apa-7", "harvard", "mla-9" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Funding-committee proposal convention",
        Compliance = AcademicComplianceProfile.Proposal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 90, MarginRightPt = 72,
            BodyFontToken = "serif-humanist", HeadingFontToken = "sans-modern", CaptionFontToken = "sans-modern",
            BodySizePt = 11, TypeScaleRatio = 1.25, LineSpacing = 1.15, ParagraphAfterPt = 8,
            CoverStyle = "minimal-centered", IncludesAiUseDisclosureSlot = true,
            IncludeToc = false, TocDepth = 1,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-outside",
            HeadingNumbering = "decimal", DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            AccentColorHex = "#1f5c4d",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("palatino-calibri", "Palatino · Calibri", "serif-humanist", "sans-modern", "sans-modern"),
                FontPair("times-calibri", "Times · Calibri", "serif-classic", "sans-modern", "sans-modern")),
            Axis("paper", "Stock",
                Opt("a4", "A4", ("paperFormat", "a4")),
                Opt("letter", "US Letter", ("paperFormat", "letter"), ("marginRightPt", "90"))),
            Axis("cover", "Cover",
                Opt("minimal", "Minimal", ("coverStyle", "minimal-centered")),
                Opt("band", "Institutional band", ("coverStyle", "institutional-band"))),
            Axis("leading", "Leading",
                Opt("115", "1.15", ("lineSpacing", "1.15")),
                Opt("150", "1.5", ("lineSpacing", "1.5"))),
        },
    };

    private static AcademicTemplateDefinition LiteratureReviewApa() => new()
    {
        Slug = "literature-review-apa",
        DisplayName = "Literature Review (APA 7)",
        Description = "Level-1-to-5 APA heading ladder with a hanging-indent reference shelf sized for long source lists.",
        DocumentKinds = new[] { "literature-review", "research-proposal" },
        CitationStyles = new[] { "apa-7" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "APA 7th edition manuscript conventions",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 72, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.0, LineSpacing = 1.5, ParagraphAfterPt = 0, FirstLineIndentPt = 36,
            CoverStyle = "none", IncludeToc = true, TocDepth = 1, TocDotLeaders = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "top-right",
            HeadingNumbering = "none", DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 72, HeadingsBold = true,
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("calibri", "Calibri", "sans-modern", "sans-modern", "sans-modern")),
            Axis("leading", "Leading",
                Opt("150", "1.5", ("lineSpacing", "1.5")),
                Opt("200", "Double", ("lineSpacing", "2")),
                Opt("115", "1.15", ("lineSpacing", "1.15"))),
            Axis("numbering", "Page numbers",
                Opt("topright", "Top right", ("pageNumberPosition", "top-right")),
                Opt("bottom", "Bottom centre", ("pageNumberPosition", "bottom-center"))),
        },
    };

    private static AcademicTemplateDefinition JournalDoubleColumn() => new()
    {
        Slug = "journal-double-column",
        DisplayName = "Journal Article (2-column)",
        Description = "Dense two-column manuscript grid with numeric references and compact captions — reads like the accepted-version PDF.",
        DocumentKinds = new[] { "journal-article", "literature-review" },
        CitationStyles = new[] { "ieee", "vancouver" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "IEEE/ Elsevier two-column manuscript convention",
        Compliance = AcademicComplianceProfile.Journal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 54, MarginBottomPt = 61, MarginLeftPt = 54, MarginRightPt = 54,
            BodyFontToken = "serif-classic", HeadingFontToken = "sans-neutral", CaptionFontToken = "serif-classic",
            BodySizePt = 10, TypeScaleRatio = 1.15, LineSpacing = 1.15, ParagraphAfterPt = 4,
            ColumnCount = 2, ColumnGapPt = 18,
            CoverStyle = "none", IncludeToc = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", DefaultCitationStyle = "ieee", CitationMarkers = "numeric",
            BibliographyHangingIndent = false, BibliographyIndentPt = 0,
            FigureCaptionPosition = "below", TableCaptionPosition = "above", FigureNumbering = "continuous",
            AccentColorHex = "#4a4a4a",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("palatino", "Palatino", "serif-humanist", "serif-humanist", "serif-humanist")),
            Axis("refs", "Reference markers",
                Opt("numeric", "Numeric [1]", ("citationMarkers", "numeric")),
                Opt("ad", "Author-date", ("citationMarkers", "author-date"), ("defaultCitationStyle", "harvard"))),
            Axis("gap", "Column gap",
                Opt("narrow", "Narrow (18 pt)", ("columnGapPt", "18")),
                Opt("wide", "Wide (26 pt)", ("columnGapPt", "26"))),
        },
    };

    private static AcademicTemplateDefinition JournalSingleColumn() => new()
    {
        Slug = "journal-single-column",
        DisplayName = "Journal Article (1-column)",
        Description = "Wide-margin single-column submission grid favoured by humanities and social-science journals, Chicago footnotes included.",
        DocumentKinds = new[] { "journal-article", "case-study" },
        CitationStyles = new[] { "chicago-notes-bibliography", "mla-9", "turabian" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Humanities single-column manuscript convention",
        Compliance = AcademicComplianceProfile.Journal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 90, MarginBottomPt = 90, MarginLeftPt = 90, MarginRightPt = 90,
            BodyFontToken = "serif-humanist", HeadingFontToken = "serif-humanist", CaptionFontToken = "serif-humanist",
            BodySizePt = 11, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 8, FirstLineIndentPt = 18,
            CoverStyle = "none", IncludeToc = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-center",
            HeadingNumbering = "none", DefaultCitationStyle = "chicago-notes-bibliography", CitationMarkers = "notes",
            FootnotesEnabled = true, FootnotePlacement = "bottom-of-page",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("palatino", "Palatino", "serif-humanist", "serif-humanist", "serif-humanist"),
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("garamond-arial", "Garamond · Arial heads", "serif-humanist", "sans-neutral", "sans-neutral")),
            Axis("indent", "Paragraphs",
                Opt("indent", "First-line indents", ("firstLineIndentPt", "18"), ("paragraphAfterPt", "4")),
                Opt("block", "Block paragraphs", ("firstLineIndentPt", "0"), ("paragraphAfterPt", "10"))),
        },
    };

    private static AcademicTemplateDefinition IeeeConferencePaper() => new()
    {
        Slug = "ieee-conference-paper",
        DisplayName = "Conference Paper (IEEE)",
        Description = "Strict IEEE conference density: 10 pt two-column body, numbered references, abstract at the head of column one.",
        DocumentKinds = new[] { "journal-article", "technical-report" },
        CitationStyles = new[] { "ieee" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "IEEE conference proceedings template family",
        Compliance = AcademicComplianceProfile.Journal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "letter",
            MarginTopPt = 54, MarginBottomPt = 61, MarginLeftPt = 54, MarginRightPt = 54,
            BodyFontToken = "serif-classic", HeadingFontToken = "sans-neutral", CaptionFontToken = "sans-neutral",
            BodySizePt = 10, TypeScaleRatio = 1.1, LineSpacing = 1.05, ParagraphAfterPt = 3,
            ColumnCount = 2, ColumnGapPt = 12,
            CoverStyle = "none", IncludeToc = false,
            FrontMatterNumbering = "none", BodyNumbering = "arabic", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", HeadingLevels = 3,
            DefaultCitationStyle = "ieee", CitationMarkers = "numeric",
            BibliographyHangingIndent = false, BibliographyIndentPt = 0,
            FigureNumbering = "continuous", AccentColorHex = "#333333",
        },
        Axes = new[]
        {
            Axis("paper", "Stock",
                Opt("letter", "US Letter", ("paperFormat", "letter")),
                Opt("a4", "A4", ("paperFormat", "a4"))),
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("helvetica", "Helvetica look", "sans-neutral", "sans-neutral", "sans-neutral")),
            Axis("heads", "Heading style",
                Opt("numbered", "Numbered", ("headingNumbering", "decimal")),
                Opt("outline", "Small-caps unnumbered", ("headingNumbering", "none"), ("h1Uppercase", "true"))),
        },
    };

    private static AcademicTemplateDefinition CaseStudyReport() => new()
    {
        Slug = "case-study-report",
        DisplayName = "Case Study",
        Description = "Business-school case write-up: executive-summary cover band, numbered exhibits with captions above, boxed quotes for interview evidence.",
        DocumentKinds = new[] { "case-study", "technical-report" },
        CitationStyles = new[] { "harvard", "apa-7", "chicago-author-date" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Case-method teaching report convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 85, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "sans-neutral", CaptionFontToken = "sans-neutral",
            BodySizePt = 11, TypeScaleRatio = 1.3, LineSpacing = 1.3, ParagraphAfterPt = 8,
            CoverStyle = "institutional-band", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-inside",
            HeadingNumbering = "decimal", DefaultCitationStyle = "harvard", CitationMarkers = "author-date",
            TableCaptionPosition = "above", FigureCaptionPosition = "below", FigureNumbering = "continuous",
            AccentColorHex = "#7a3024",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times-arial", "Times · Arial", "serif-classic", "sans-neutral", "sans-neutral"),
                FontPair("palatino-calibri", "Palatino · Calibri", "serif-humanist", "sans-modern", "sans-modern")),
            Axis("cover", "Cover",
                Opt("band", "Summary band", ("coverStyle", "institutional-band")),
                Opt("minimal", "Minimal", ("coverStyle", "minimal-centered"))),
            Axis("leading", "Leading",
                Opt("130", "1.3", ("lineSpacing", "1.3")),
                Opt("150", "1.5", ("lineSpacing", "1.5"))),
        },
    };

    private static AcademicTemplateDefinition LabReportImrad() => new()
    {
        Slug = "lab-report-imrad",
        DisplayName = "Lab Report (IMRaD)",
        Description = "Introduction-Methods-Results-Discussion skeleton: numeric references, captions tied to exhibits, results tables first-class.",
        DocumentKinds = new[] { "lab-report", "journal-article", "capstone-project" },
        CitationStyles = new[] { "vancouver", "apa-7", "ieee" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "IMRaD laboratory/science report convention",
        // IMRaD lab reports are submitted without a contents section, so the Report profile's
        // mandatory-TOC floor would (correctly) filter every expansion of this base; the Journal
        // floor (no TOC requirement, 36 pt margin minimum) is the honest gate for this kind.
        Compliance = AcademicComplianceProfile.Journal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 72, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "sans-modern", CaptionFontToken = "sans-modern",
            BodySizePt = 11, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 8,
            CoverStyle = "two-part", IncludesAiUseDisclosureSlot = true,
            IncludeToc = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "top-right",
            HeadingNumbering = "none", DefaultCitationStyle = "vancouver", CitationMarkers = "numeric",
            FigureCaptionPosition = "below", TableCaptionPosition = "above", FigureNumbering = "continuous",
            MonoFontToken = "mono-code",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times-calibri", "Times · Calibri", "serif-classic", "sans-modern", "sans-modern"),
                FontPair("arial", "All Arial", "sans-neutral", "sans-neutral", "sans-neutral")),
            Axis("leading", "Leading",
                Opt("150", "1.5", ("lineSpacing", "1.5")),
                Opt("200", "Double", ("lineSpacing", "2"))),
            Axis("numbering", "Page numbers",
                Opt("topright", "Top right", ("pageNumberPosition", "top-right")),
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center"))),
        },
    };

    private static AcademicTemplateDefinition ChicagoHumanitiesMonograph() => new()
    {
        Slug = "chicago-humanities-monograph",
        DisplayName = "Humanities Monograph",
        Description = "Chicago notes-and-bibliography at book length: true footnotes, uppercased chapter openers, outside folios.",
        DocumentKinds = new[] { "monograph", "thesis", "literature-review" },
        CitationStyles = new[] { "chicago-notes-bibliography", "turabian" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Chicago 17th notes-bibliography monograph convention",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 85, MarginBottomPt = 99, MarginLeftPt = 99, MarginRightPt = 72, BindingEdgePt = 14,
            BodyFontToken = "serif-humanist", HeadingFontToken = "serif-humanist", CaptionFontToken = "serif-humanist",
            BodySizePt = 12, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 0, FirstLineIndentPt = 21,
            CoverStyle = "classic-title-page",
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-outside",
            HeadingNumbering = "outline", H1Uppercase = true,
            DefaultCitationStyle = "chicago-notes-bibliography", CitationMarkers = "notes",
            FootnotesEnabled = true, FootnotePlacement = "bottom-of-page",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
            AccentColorHex = "#4d3b2c",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("palatino", "Palatino", "serif-humanist", "serif-humanist", "serif-humanist"),
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic")),
            Axis("notes", "Notes placement",
                Opt("bottom", "Footnotes at page foot", ("footnotePlacement", "bottom-of-page")),
                Opt("end", "Endnotes", ("footnotePlacement", "end-of-document"))),
            Axis("heads", "Chapter openers",
                Opt("caps", "Uppercased, folio hidden", ("h1Uppercase", "true"), ("pageNumberPosition", "top-center")),
                Opt("regular", "Regular folio", ("h1Uppercase", "false"), ("pageNumberPosition", "bottom-outside"))),
        },
    };

    private static AcademicTemplateDefinition BluebookLegalReport() => new()
    {
        Slug = "bluebook-legal-report",
        DisplayName = "Legal Research Report",
        Description = "Bluebook-flavoured: dense page-bottom notes, justified text blocks, roman-uppercase front matter.",
        DocumentKinds = new[] { "technical-report", "research-proposal", "monograph" },
        CitationStyles = new[] { "bluebook-21", "chicago-notes-bibliography" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "US law-review / Bluebook 21st edition convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "letter",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 108, MarginRightPt = 108,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.15, LineSpacing = 1.5, ParagraphAfterPt = 6, JustifyBody = true,
            CoverStyle = "classic-title-page",
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "roman-upper", PageNumberPosition = "bottom-center",
            HeadingNumbering = "outline",
            DefaultCitationStyle = "bluebook-21", CitationMarkers = "notes",
            FootnotesEnabled = true, FootnotePlacement = "bottom-of-page",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("garamond", "Garamond", "serif-humanist", "serif-humanist", "serif-humanist")),
            Axis("cover", "Cover",
                Opt("classic", "Classic report cover", ("coverStyle", "classic-title-page")),
                Opt("minimal", "Minimal", ("coverStyle", "minimal-centered"))),
            Axis("heads", "Headings",
                Opt("outline", "Outline numbering", ("headingNumbering", "outline")),
                Opt("plain", "Unnumbered", ("headingNumbering", "none"))),
        },
    };

    private static AcademicTemplateDefinition TechnicalReportHei() => new()
    {
        Slug = "technical-report-hei",
        DisplayName = "Institutional Technical Report",
        Description = "Working-report grid: document-identity running head, bottom-outside folios, code-listing typography sized for print review.",
        DocumentKinds = new[] { "technical-report", "lab-report", "capstone-project" },
        CitationStyles = new[] { "ieee", "apa-7", "vancouver" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Institutional/departmental technical report series convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 85, MarginRightPt = 72,
            BodyFontToken = "serif-humanist", HeadingFontToken = "sans-modern", CaptionFontToken = "sans-modern",
            BodySizePt = 11, TypeScaleRatio = 1.25, LineSpacing = 1.25, ParagraphAfterPt = 8,
            CoverStyle = "minimal-centered", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-outside",
            RunningHeadContent = "short-title", RunningHeadRulePt = 6,
            HeadingNumbering = "decimal", DefaultCitationStyle = "ieee", CitationMarkers = "numeric",
            MonoFontToken = "mono-code", AccentColorHex = "#2e5e4e",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("palatino-calibri", "Palatino · Calibri", "serif-humanist", "sans-modern", "sans-modern"),
                FontPair("times-arial", "Times · Arial", "serif-classic", "sans-neutral", "sans-neutral"),
                FontPair("arial-mono", "Arial with mono captions", "sans-neutral", "sans-neutral", "mono-code")),
            Axis("cover", "Cover",
                Opt("minimal", "Minimal", ("coverStyle", "minimal-centered")),
                Opt("band", "Band", ("coverStyle", "institutional-band"))),
            Axis("heads", "Running head",
                Opt("title", "Short title", ("runningHeadContent", "short-title")),
                Opt("author", "Author line", ("runningHeadContent", "author"))),
        },
    };

    private static AcademicTemplateDefinition CapstoneProject() => new()
    {
        Slug = "capstone-project",
        DisplayName = "Capstone Project",
        Description = "Final-year project report: two-part cover with team block, deliverable tables, lighter margins than a thesis but the same chapter spine.",
        DocumentKinds = new[] { "capstone-project", "technical-report" },
        CitationStyles = new[] { "ieee", "apa-7", "harvard" },
        Languages = new[] { "en" },
        PaperFormats = new[] { "letter", "a4" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Undergraduate capstone/portfolio convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", PaperFormat = "letter",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 90, MarginRightPt = 72,
            BodyFontToken = "sans-modern", HeadingFontToken = "sans-modern", CaptionFontToken = "sans-modern",
            BodySizePt = 11, TypeScaleRatio = 1.3, LineSpacing = 1.15, ParagraphAfterPt = 8,
            CoverStyle = "two-part", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", DefaultCitationStyle = "ieee", CitationMarkers = "numeric",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
            AccentColorHex = "#5b2d82",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("calibri", "Calibri", "sans-modern", "sans-modern", "sans-modern"),
                FontPair("times-heads", "Times body", "serif-classic", "sans-modern", "sans-modern")),
            Axis("leading", "Leading",
                Opt("115", "1.15", ("lineSpacing", "1.15")),
                Opt("150", "1.5", ("lineSpacing", "1.5"))),
            Axis("cover", "Cover",
                Opt("two-part", "Two-part", ("coverStyle", "two-part")),
                Opt("band", "Band", ("coverStyle", "institutional-band"))),
        },
    };

    // ── RTL library: Kurdish Sorani (ku/ckb) and Arabic (ar). Arabic script needs a larger nominal
    // body size and looser leading than Latin at the same optical weight; the bases bake that in and
    // the axes never drop below it, so "RTL variant of an English layout" is not a thing here. ──────

    private static AcademicTemplateDefinition KurdishThesis() => new()
    {
        Slug = "kurdish-thesis-ckb",
        DisplayName = "تۆزی ماستەر — Kurdish Thesis (Sorani)",
        Description = "Spine-right Kurdish Sorani thesis: naskh body at 13 pt, generous leading for connecting scripts, right binding gutter, roman front matter and arabic-indic folios as student options.",
        DocumentKinds = new[] { "thesis", "dissertation" },
        CitationStyles = new[] { "apa-7", "chicago-author-date", "harvard" },
        Languages = new[] { "ku" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Kurdistan Region higher-education thesis formatting profile",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ku", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 72, MarginRightPt = 108,
            BindingEdge = "right", BindingEdgePt = 14,
            BodyFontToken = "naskh-classic", HeadingFontToken = "naskh-classic", CaptionFontToken = "naskh-classic",
            BodySizePt = 13, TypeScaleRatio = 1.25, LineSpacing = 1.75, ParagraphAfterPt = 8,
            CoverStyle = "classic-title-page", IncludesApprovalPage = true, IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true, IncludeListOfFiguresTables = true,
            FrontMatterNumbering = "roman-lower", BodyNumbering = "arabic", RestartNumbersAfterFrontMatter = true,
            PageNumberPosition = "bottom-center", HeadingNumbering = "decimal", DigitSystem = "latin",
            DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
            AccentColorHex = "#1f4d3d",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("naskh", "Naskh", "naskh-classic", "naskh-classic", "naskh-classic"),
                FontPair("sans-arabic", "Noto Sans Arabic", "sans-arabic", "sans-arabic", "sans-arabic")),
            Axis("digits", "Digits",
                Opt("latin", "Latin 1,2,3", ("digitSystem", "latin")),
                Opt("arabic-indic", "Arabic-Indic ١,٢,٣", ("digitSystem", "arabic-indic"))),
            Axis("leading", "Leading",
                Opt("175", "1.75", ("lineSpacing", "1.75")),
                Opt("200", "2.0", ("lineSpacing", "2")),
                Opt("150", "1.5", ("lineSpacing", "1.5"))),
        },
    };

    private static AcademicTemplateDefinition ArabicDissertation() => new()
    {
        Slug = "arabic-dissertation-ar",
        DisplayName = "أطروحة دكتوراه — Arabic Dissertation",
        Description = "Full Arabic doctoral layout: naskh 14 pt at 1.6 leading, outside folios mirroring to the left page edge, footnotes and a mirrored bibliography shelf.",
        DocumentKinds = new[] { "dissertation", "thesis" },
        CitationStyles = new[] { "chicago-notes-bibliography", "turabian", "apa-7" },
        Languages = new[] { "ar" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Arab-world graduate dissertation convention",
        Compliance = AcademicComplianceProfile.Dissertation,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ar", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 85, MarginBottomPt = 85, MarginLeftPt = 85, MarginRightPt = 99,
            BindingEdge = "right", BindingEdgePt = 14,
            BodyFontToken = "naskh-classic", HeadingFontToken = "naskh-classic", CaptionFontToken = "naskh-classic",
            BodySizePt = 14, TypeScaleRatio = 1.2, LineSpacing = 1.6, ParagraphAfterPt = 8,
            CoverStyle = "institutional-band", IncludesApprovalPage = true, IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 3, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-outside",
            HeadingNumbering = "decimal", DigitSystem = "arabic-indic",
            DefaultCitationStyle = "chicago-notes-bibliography", CitationMarkers = "notes",
            FootnotesEnabled = true, FootnotePlacement = "bottom-of-page",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
            AccentColorHex = "#0e4a3c",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("naskh", "Naskh", "naskh-classic", "naskh-classic", "naskh-classic"),
                FontPair("sans", "Sans Arabic", "sans-arabic", "sans-arabic", "sans-arabic")),
            Axis("cover", "Cover",
                Opt("band", "Institutional band", ("coverStyle", "institutional-band")),
                Opt("classic", "Classic", ("coverStyle", "classic-title-page"))),
            Axis("digits", "Digits",
                Opt("indic", "Arabic-Indic ١٢٣", ("digitSystem", "arabic-indic")),
                Opt("latin", "Latin 123", ("digitSystem", "latin"))),
            Axis("numbering", "Folios",
                Opt("outside", "Bottom outside", ("pageNumberPosition", "bottom-outside")),
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center"))),
        },
    };

    private static AcademicTemplateDefinition ArabicResearchProposal() => new()
    {
        Slug = "arabic-research-proposal-ar",
        DisplayName = "خطة بحث — Arabic Research Proposal",
        Description = "Arabic proposal grid: compact title block, methodology and timetable tables, folios at the outer foot.",
        DocumentKinds = new[] { "research-proposal" },
        CitationStyles = new[] { "apa-7", "chicago-author-date", "harvard" },
        Languages = new[] { "ar" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Arab-world research-plan convention",
        Compliance = AcademicComplianceProfile.Proposal,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ar", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 85, MarginRightPt = 99,
            BindingEdge = "right",
            BodyFontToken = "sans-arabic", HeadingFontToken = "sans-arabic", CaptionFontToken = "sans-arabic",
            BodySizePt = 13, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 8,
            CoverStyle = "minimal-centered", IncludesAiUseDisclosureSlot = true,
            IncludeToc = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "bottom-outside",
            HeadingNumbering = "decimal", DigitSystem = "latin",
            DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
            AccentColorHex = "#8a5a12",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("sans", "Noto Sans Arabic", "sans-arabic", "sans-arabic", "sans-arabic"),
                FontPair("naskh", "Naskh", "naskh-classic", "naskh-classic", "naskh-classic")),
            Axis("leading", "Leading",
                Opt("150", "1.5", ("lineSpacing", "1.5")),
                Opt("175", "1.75", ("lineSpacing", "1.75"))),
            Axis("digits", "Digits",
                Opt("latin", "Latin", ("digitSystem", "latin")),
                Opt("indic", "Arabic-Indic", ("digitSystem", "arabic-indic"))),
        },
    };

    private static AcademicTemplateDefinition KurdishLiteratureReview() => new()
    {
        Slug = "kurdish-literature-review-ckb",
        DisplayName = "پێداچوونەوەی ڕۆژژمێر — Kurdish Literature Review",
        Description = "Sorani literature review: hanging shelf sized for mixed Kurdish/Arabic/Latin author names, numbered section ladder.",
        DocumentKinds = new[] { "literature-review" },
        CitationStyles = new[] { "apa-7", "harvard" },
        Languages = new[] { "ku" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Kurdish Sorani academic review convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ku", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 85, MarginRightPt = 85,
            BodyFontToken = "naskh-classic", HeadingFontToken = "sans-arabic", CaptionFontToken = "sans-arabic",
            BodySizePt = 13, TypeScaleRatio = 1.25, LineSpacing = 1.75, ParagraphAfterPt = 8,
            CoverStyle = "minimal-centered", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "arabic", PageNumberPosition = "top-center",
            HeadingNumbering = "decimal", DigitSystem = "arabic-indic",
            DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 42,
            AccentColorHex = "#144e4a",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("naskh-sansheads", "Naskh · Sans heads", "naskh-classic", "sans-arabic", "sans-arabic"),
                FontPair("all-naskh", "All naskh", "naskh-classic", "naskh-classic", "naskh-classic")),
            Axis("digits", "Digits",
                Opt("indic", "Arabic-Indic", ("digitSystem", "arabic-indic")),
                Opt("latin", "Latin", ("digitSystem", "latin"))),
            Axis("numbering", "Folios",
                Opt("topcenter", "Top centre", ("pageNumberPosition", "top-center")),
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center"))),
        },
    };

    private static AcademicTemplateDefinition BilingualAbstractThesis() => new()
    {
        Slug = "bilingual-abstract-thesis",
        DisplayName = "Bilingual Abstract Thesis",
        Description = "English thesis body carrying a mirrored Kurdish/Arabic abstract page (and the reverse option) — the layout most Kurdistan-region submissions actually need.",
        DocumentKinds = new[] { "thesis", "dissertation" },
        CitationStyles = new[] { "apa-7", "ieee", "harvard" },
        Languages = new[] { "en", "ar", "ku" },
        PaperFormats = new[] { "a4", "letter" },
        Direction = AcademicTextDirection.Ltr,
        FormattingLineage = "Bilingual submission convention (English body, RTL ملخص/پوختە)",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "en", Direction = AcademicTextDirection.Ltr, PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 108, MarginRightPt = 72,
            BodyFontToken = "serif-classic", HeadingFontToken = "serif-classic", CaptionFontToken = "serif-classic",
            BodySizePt = 12, TypeScaleRatio = 1.25, LineSpacing = 1.5, ParagraphAfterPt = 8,
            CoverStyle = "classic-title-page", IncludesApprovalPage = true, IncludesAiUseDisclosureSlot = true,
            SupportsRtlAbstractPage = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-center",
            HeadingNumbering = "decimal", DefaultCitationStyle = "apa-7", CitationMarkers = "author-date",
            BibliographyHangingIndent = true, BibliographyIndentPt = 36,
        },
        Axes = new[]
        {
            Axis("abstract", "RTL abstract",
                Opt("kurdish", "Kurdish ملخص (پوختە)", ("languageCode", "en"), ("supportsRtlAbstractPage", "true")),
                Opt("arabic", "Arabic ملخص", ("languageCode", "en"), ("supportsRtlAbstractPage", "true"), ("accentColorHex", "#1f4d3d"))),
            FontAxis(
                FontPair("times", "Times", "serif-classic", "serif-classic", "serif-classic"),
                FontPair("palatino", "Palatino", "serif-humanist", "serif-humanist", "serif-humanist")),
            Axis("leading", "Leading",
                Opt("150", "1.5", ("lineSpacing", "1.5")),
                Opt("200", "Double", ("lineSpacing", "2"))),
            Axis("cover", "Cover",
                Opt("classic", "Classic", ("coverStyle", "classic-title-page")),
                Opt("two-part", "Two-part", ("coverStyle", "two-part"))),
        },
    };

    private static AcademicTemplateDefinition ArabicMonographClassic() => new()
    {
        Slug = "arabic-monograph-classic",
        DisplayName = "كتاب بحثي — Arabic Monograph",
        Description = "Book-length Arabic study: chapter-opening pages, dense footnotes, TOC depth three, outside folios in Arabic-Indic digits.",
        DocumentKinds = new[] { "monograph", "thesis" },
        CitationStyles = new[] { "chicago-notes-bibliography", "turabian" },
        Languages = new[] { "ar" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Classical Arabic publishing/academic monograph convention",
        Compliance = AcademicComplianceProfile.Thesis,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ar", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 85, MarginBottomPt = 99, MarginLeftPt = 99, MarginRightPt = 85,
            BindingEdge = "right", BindingEdgePt = 14,
            BodyFontToken = "naskh-classic", HeadingFontToken = "naskh-classic", CaptionFontToken = "naskh-classic",
            BodySizePt = 14, TypeScaleRatio = 1.2, LineSpacing = 1.75, ParagraphAfterPt = 0, FirstLineIndentPt = 28,
            CoverStyle = "classic-title-page",
            IncludeToc = true, TocDepth = 3, TocDotLeaders = true,
            FrontMatterNumbering = "roman-lower", PageNumberPosition = "bottom-inside",
            HeadingNumbering = "outline", DigitSystem = "arabic-indic",
            DefaultCitationStyle = "chicago-notes-bibliography", CitationMarkers = "notes",
            FootnotesEnabled = true, FootnotePlacement = "bottom-of-page",
            BibliographyHangingIndent = true, BibliographyIndentPt = 42,
            AccentColorHex = "#3d2b1f",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("naskh", "Naskh", "naskh-classic", "naskh-classic", "naskh-classic"),
                FontPair("sans", "Sans Arabic", "sans-arabic", "sans-arabic", "sans-arabic")),
            Axis("toc", "TOC depth",
                Opt("depth2", "Two levels", ("tocDepth", "2")),
                Opt("depth3", "Three levels", ("tocDepth", "3"))),
            Axis("numbering", "Folios",
                Opt("inside", "Bottom inside", ("pageNumberPosition", "bottom-inside")),
                Opt("center", "Bottom centre", ("pageNumberPosition", "bottom-center"))),
        },
    };

    private static AcademicTemplateDefinition KurdishFieldReport() => new()
    {
        Slug = "kurdish-field-report-ckb",
        DisplayName = "ڕاپۆرتی مەیدانی — Kurdish Report",
        Description = "Sorani technical/field report: sans headings over naskh body, exhibit captions, folios top-centre for print review.",
        DocumentKinds = new[] { "technical-report", "lab-report", "case-study" },
        CitationStyles = new[] { "ieee", "apa-7", "vancouver" },
        Languages = new[] { "ku" },
        PaperFormats = new[] { "a4" },
        Direction = AcademicTextDirection.Rtl,
        FormattingLineage = "Kurdish Sorani institutional report convention",
        Compliance = AcademicComplianceProfile.Report,
        BaseSpec = new AcademicDesignSpec
        {
            LanguageCode = "ku", Direction = AcademicTextDirection.Rtl, PaperFormat = "a4",
            MarginTopPt = 72, MarginBottomPt = 72, MarginLeftPt = 85, MarginRightPt = 99,
            BindingEdge = "right",
            BodyFontToken = "naskh-classic", HeadingFontToken = "sans-arabic", CaptionFontToken = "sans-arabic",
            BodySizePt = 13, TypeScaleRatio = 1.25, LineSpacing = 1.6, ParagraphAfterPt = 8,
            CoverStyle = "institutional-band", IncludesAiUseDisclosureSlot = true,
            IncludeToc = true, TocDepth = 2, TocDotLeaders = false,
            FrontMatterNumbering = "arabic", PageNumberPosition = "top-center",
            HeadingNumbering = "decimal", DigitSystem = "latin",
            DefaultCitationStyle = "ieee", CitationMarkers = "numeric",
            TableCaptionPosition = "above", FigureCaptionPosition = "below",
            AccentColorHex = "#0f5d52",
        },
        Axes = new[]
        {
            FontAxis(
                FontPair("naskh-sansheads", "Naskh · Sans heads", "naskh-classic", "sans-arabic", "sans-arabic"),
                FontPair("all-sans", "All sans", "sans-arabic", "sans-arabic", "sans-arabic")),
            Axis("cover", "Cover",
                Opt("band", "Band", ("coverStyle", "institutional-band")),
                Opt("two-part", "Two-part", ("coverStyle", "two-part"))),
            Axis("digits", "Digits",
                Opt("latin", "Latin", ("digitSystem", "latin")),
                Opt("indic", "Arabic-Indic", ("digitSystem", "arabic-indic"))),
        },
    };
}

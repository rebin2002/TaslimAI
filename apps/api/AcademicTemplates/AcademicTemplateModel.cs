namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// A named option on one variant axis. The delta is pure data: keys must be registered
/// <see cref="AcademicDesignSpec.WithOverride"/> properties, values are invariant-culture strings.
/// </summary>
public sealed record AcademicVariantOption(
    string Value,
    string DisplayName,
    IReadOnlyDictionary<string, string> SpecOverrides);

/// <summary>One expandable design axis (e.g. font pairing, line spacing, cover treatment).</summary>
public sealed record AcademicVariantAxis(
    string Name,
    string DisplayName,
    IReadOnlyList<AcademicVariantOption> Options);

/// <summary>
/// Formatting floor a document kind must respect to be submissible at most institutions. The registry
/// rejects any expansion that drops a configuration below its base template's profile, which is the
/// mechanical answer to "no decorative layouts that violate thesis requirements".
/// </summary>
public sealed record AcademicComplianceProfile(
    double MinimumMarginPt,
    double MinimumLineSpacing,
    double MinimumBodySizePt,
    bool RequiresToc,
    bool RequiresPageNumbers)
{
    public static readonly AcademicComplianceProfile Thesis = new(70.87, 1.25, 11, RequiresToc: true, RequiresPageNumbers: true);   // 2.5 cm
    public static readonly AcademicComplianceProfile Dissertation = new(70.87, 1.25, 11, RequiresToc: true, RequiresPageNumbers: true);
    public static readonly AcademicComplianceProfile Proposal = new(54, 1.0, 10, RequiresToc: false, RequiresPageNumbers: true);
    public static readonly AcademicComplianceProfile Journal = new(36, 1.0, 8, RequiresToc: false, RequiresPageNumbers: true);
    public static readonly AcademicComplianceProfile Report = new(54, 1.0, 10, RequiresToc: true, RequiresPageNumbers: true);

    public IReadOnlyList<string> Violations(AcademicDesignSpec spec)
    {
        var issues = new List<string>();
        if (Math.Min(Math.Min(spec.MarginTopPt, spec.MarginBottomPt), Math.Min(spec.MarginLeftPt, spec.MarginRightPt)) < MinimumMarginPt)
            issues.Add("margin-below-floor");
        if (spec.LineSpacing < MinimumLineSpacing - 0.001) issues.Add("line-spacing-below-floor");
        if (spec.BodySizePt < MinimumBodySizePt - 0.001) issues.Add("body-size-below-floor");
        if (RequiresToc && !spec.IncludeToc) issues.Add("toc-required");
        if (RequiresPageNumbers && spec.PageNumberPosition == "none" && spec.FrontMatterNumbering == "none") issues.Add("page-numbers-required");
        return issues;
    }
}

/// <summary>Font token → concrete family names for each consumer surface. Keeping tokens (not raw
/// family strings) in the spec means a new base template never hard-codes a machine-specific font.</summary>
public sealed record AcademicFontToken(
    string Token,
    string DisplayName,
    string DocxFamily,
    string CssStack,
    IReadOnlyList<string> PdfFamilyCandidates,
    bool ArabicScript);

public static class AcademicFontTokens
{
    public static readonly IReadOnlyDictionary<string, AcademicFontToken> All =
        new Dictionary<string, AcademicFontToken>(StringComparer.Ordinal)
        {
            ["serif-classic"] = new("serif-classic", "Times New Roman",
                "Times New Roman",
                "\"Times New Roman\", \"Liberation Serif\", \"Nimbus Roman\", Georgia, serif",
                new[] { "Liberation Serif", "DejaVu Serif", "Times New Roman" }, ArabicScript: false),
            ["serif-humanist"] = new("serif-humanist", "Cambria / Palatino",
                "Cambria",
                "Cambria, \"Palatino Linotype\", \"Book Antiqua\", \"Liberation Serif\", Georgia, serif",
                new[] { "Liberation Serif", "DejaVu Serif", "Cambria" }, ArabicScript: false),
            ["sans-modern"] = new("sans-modern", "Calibri / Carlito",
                "Calibri",
                "Calibri, Carlito, \"Segoe UI\", \"Liberation Sans\", sans-serif",
                new[] { "Liberation Sans", "DejaVu Sans", "Carlito" }, ArabicScript: false),
            ["sans-neutral"] = new("sans-neutral", "Arial / Helvetica",
                "Arial",
                "Arial, Helvetica, \"Liberation Sans\", \"Nimbus Sans\", sans-serif",
                new[] { "Liberation Sans", "DejaVu Sans", "Nimbus Sans" }, ArabicScript: false),
            ["naskh-classic"] = new("naskh-classic", "Naskh (Traditional Arabic)",
                "Traditional Arabic",
                "\"Traditional Arabic\", \"Sakkal Majalla\", \"Noto Naskh Arabic\", \"Noto Sans Arabic\", serif",
                new[] { "Noto Sans Arabic", "Noto Naskh Arabic" }, ArabicScript: true),
            ["sans-arabic"] = new("sans-arabic", "Noto Sans Arabic",
                "Noto Sans Arabic",
                "\"Noto Sans Arabic\", \"Segoe UI\", Tahoma, sans-serif",
                new[] { "Noto Sans Arabic" }, ArabicScript: true),
            ["mono-code"] = new("mono-code", "Courier New",
                "Courier New",
                "\"Courier New\", \"Liberation Mono\", \"DejaVu Sans Mono\", monospace",
                new[] { "Liberation Mono", "DejaVu Sans Mono", "Courier New" }, ArabicScript: false),
        };

    public static AcademicFontToken Resolve(string token) =>
        All.TryGetValue(token, out var font) ? font : throw new KeyNotFoundException($"Unknown academic font token '{token}'.");
}

/// <summary>A fully materialized, previewable, selectable configuration.</summary>
public sealed record AcademicTemplateConfiguration(
    string ConfigurationId,
    string TemplateSlug,
    string DisplayName,
    IReadOnlyDictionary<string, string> VariantSelection,
    AcademicDesignSpec Spec,
    string DesignHash);

/// <summary>
/// A vetted base template: metadata, compliance profile, default design spec and the axes its variants
/// legitimately span. Base templates are the vetted unit; configurations are the selectable unit.
/// </summary>
public sealed record AcademicTemplateDefinition
{
    public const int CurrentDefinitionVersion = 1;

    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> DocumentKinds { get; init; }
    public required IReadOnlyList<string> CitationStyles { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required IReadOnlyList<string> PaperFormats { get; init; }
    public required string Direction { get; init; }
    /// <summary>Human summary of the formatting lineage ("Kurdistan Region higher-education thesis", etc.).
    /// Never claims formal compliance with a named institution's current rules.</summary>
    public required string FormattingLineage { get; init; }
    public required AcademicComplianceProfile Compliance { get; init; }
    public required AcademicDesignSpec BaseSpec { get; init; }
    public IReadOnlyList<AcademicVariantAxis> Axes { get; init; } = Array.Empty<AcademicVariantAxis>();
    public int DefinitionVersion { get; init; } = CurrentDefinitionVersion;

    /// <summary>Versioned template id carried by every snapshot and export setting.</summary>
    public string TemplateId => $"taslim.academic.{Slug}.v{DefinitionVersion}";
}

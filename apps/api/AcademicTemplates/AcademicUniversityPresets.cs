namespace Taslim.Api.AcademicTemplates;

/// <summary>
/// A named institutional formatting profile a student can attach to a template configuration.
///
/// Honest framing (academic-integrity requirement): presets describe *formatting lineages* — the
/// margin/leading/numbering conventions a category of institutions publishes — and never claim that a
/// document "complies with University X's current rules", because those rules change every academic
/// year and only the student's faculty guide is authoritative. Each preset therefore carries a
/// verification note surfaced in the gallery and in the export's disclosure slot, and every pinned
/// value stays overridable within the compliance floor: the student always controls the document.
/// </summary>
public sealed record AcademicUniversityPreset(
    string Id,
    string DisplayName,
    string Region,
    string Notes,
    string DefaultTemplateSlug,
    string DefaultConfigurationId,
    IReadOnlyDictionary<string, string> PinnedSpec,
    IReadOnlyList<string> PinnedKeys,
    IReadOnlyList<string> StudentEditableGroups,
    string VerificationNote);

/// <summary>Outcome of applying student overrides through a preset.</summary>
public sealed record AcademicOverrideResult(
    bool Accepted,
    AcademicDesignSpec? Spec,
    IReadOnlyDictionary<string, string> AppliedOverrides,
    IReadOnlyList<string> Rejections);

/// <summary>
/// Registry of the shipped presets plus the override validator. Pinned keys are values the institution
/// convention is known to fix (e.g. A4 stock, roman front matter); student groups stay open
/// (type pairing, leading beyond the floor, folio placement where the lineage allows a choice).
/// Rejections never fail silently: the caller receives the exact key and reason so the UI can point at
/// the control instead of quietly dropping the change.
/// </summary>
public static class AcademicUniversityPresetRegistry
{
    public const string CurrentVersion = "v1";

    public static readonly IReadOnlyList<AcademicUniversityPreset> All = new[]
    {
        new AcademicUniversityPreset(
            Id: "krg-graduate-thesis",
            DisplayName: "Kurdistan Region graduate thesis profile",
            Region: "IQ-KRG",
            Notes: "Wide spine margin, 1.5+ leading, separate Kurdish and English abstract pages.",
            DefaultTemplateSlug: "kurdish-thesis-ckb",
            DefaultConfigurationId: "kurdish-thesis-ckb.naskh.latin.150",
            PinnedSpec: Map(("paperFormat", "a4"), ("marginBottomPt", "72"), ("marginTopPt", "72")),
            PinnedKeys: new[] { "paperFormat", "marginTopPt", "marginBottomPt" },
            StudentEditableGroups: new[] { "fonts", "leading", "numbering", "cover" },
            VerificationNote: "Confirm page-order and cover fields against your faculty's current thesis guide before submission."),
        new AcademicUniversityPreset(
            Id: "ar-graduate-thesis",
            DisplayName: "Arab-world graduate thesis profile",
            Region: "AR-general",
            Notes: "Naskh 14pt minimum, Arabic-Indic folios default, mirrored right-hand binding gutter.",
            DefaultTemplateSlug: "arabic-dissertation-ar",
            DefaultConfigurationId: "arabic-dissertation-ar.naskh.band.indic.outside",
            PinnedSpec: Map(("paperFormat", "a4"), ("bodySizePt", "14"), ("digitSystem", "arabic-indic")),
            PinnedKeys: new[] { "paperFormat", "bodySizePt", "digitSystem" },
            StudentEditableGroups: new[] { "fonts", "cover", "numbering" },
            VerificationNote: "Faculty guides differ on transliteration and folio digit systems; verify before submission."),
        new AcademicUniversityPreset(
            Id: "uk-hei-dissertation",
            DisplayName: "UK higher-education dissertation profile",
            Region: "UK",
            Notes: "A4, 3 cm all-round margins, 1.5 leading, Harvard author-date default.",
            DefaultTemplateSlug: "uk-postgraduate-dissertation",
            DefaultConfigurationId: "uk-postgraduate-dissertation.times.150.center",
            PinnedSpec: Map(("paperFormat", "a4"), ("marginTopPt", "85"), ("marginBottomPt", "85"), ("marginLeftPt", "85"), ("marginRightPt", "85")),
            PinnedKeys: new[] { "paperFormat", "marginTopPt", "marginBottomPt", "marginLeftPt", "marginRightPt" },
            StudentEditableGroups: new[] { "fonts", "leading", "numbering" },
            VerificationNote: "3 cm is the common minimum; some departments require 4 cm binding side."),
        new AcademicUniversityPreset(
            Id: "us-graduate-thesis",
            DisplayName: "US graduate-school thesis profile",
            Region: "US",
            Notes: "Letter stock, 1 in body margins with 1.5 in spine, double-spaced chapters.",
            DefaultTemplateSlug: "us-doctoral-dissertation",
            DefaultConfigurationId: "us-doctoral-dissertation.times.classic.numbered",
            PinnedSpec: Map(("paperFormat", "letter"), ("marginLeftPt", "108")),
            PinnedKeys: new[] { "paperFormat", "marginLeftPt" },
            StudentEditableGroups: new[] { "fonts", "cover", "heads" },
            VerificationNote: "Graduate-school handbooks vary (proquest margins, embargo pages); verify before submission."),
        new AcademicUniversityPreset(
            Id: "bologna-master-thesis",
            DisplayName: "Bologna master thesis profile",
            Region: "EU",
            Notes: "A4, moderate margins, english+local abstract order.",
            DefaultTemplateSlug: "bilingual-abstract-thesis",
            DefaultConfigurationId: "bilingual-abstract-thesis.kurdish.times.150.classic",
            PinnedSpec: Map(("paperFormat", "a4")),
            PinnedKeys: new[] { "paperFormat" },
            StudentEditableGroups: new[] { "fonts", "leading", "cover", "abstract" },
            VerificationNote: "Check whether your department places the local-language abstract before or after the English one."),
        new AcademicUniversityPreset(
            Id: "custom-student",
            DisplayName: "Custom (my institution's guide)",
            Region: "*",
            Notes: "Nothing pinned beyond the document-kind compliance floor — every group is student-controlled.",
            DefaultTemplateSlug: "classic-thesis",
            DefaultConfigurationId: "classic-thesis.times.150.classic.center",
            PinnedSpec: Map(),
            PinnedKeys: Array.Empty<string>(),
            StudentEditableGroups: new[] { "fonts", "leading", "numbering", "cover", "heads", "toc" },
            VerificationNote: "You are the authority here: this preset exports a settings summary page so you can tick values against your institution's guide."),
    };

    private static IReadOnlyDictionary<string, string> Map(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static AcademicUniversityPreset? Get(string id) =>
        All.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Apply student-controlled overrides through a preset. Rules, in order:
    /// 1. unknown spec keys are rejected (never silently dropped);
    /// 2. changes to a pinned preset key are rejected with the preset's note;
    /// 3. a result violating the template's compliance profile or accessibility floors is rejected;
    /// 4. everything else is applied verbatim.
    /// </summary>
    public static AcademicOverrideResult ApplyOverrides(AcademicUniversityPreset preset, AcademicTemplateConfiguration configuration, IReadOnlyDictionary<string, string> requested)
    {
        var rejections = new List<string>();
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        var spec = configuration.Spec;

        foreach (var pair in requested.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (preset.PinnedKeys.Contains(pair.Key, StringComparer.Ordinal))
            {
                // Re-affirming the pinned value (a round-tripped settings panel saving unchanged
                // fields) is a no-op; only an actual change of a pinned key is rejected.
                if (!preset.PinnedSpec.TryGetValue(pair.Key, out var pinnedValue)
                    || !string.Equals(pinnedValue, pair.Value, StringComparison.Ordinal))
                {
                    rejections.Add($"{pair.Key}: fixed by the '{preset.Id}' profile ({preset.Notes}).");
                    continue;
                }
                applied[pair.Key] = pair.Value;
                continue;
            }
            try
            {
                spec = spec.WithOverride(pair.Key, pair.Value);
                applied[pair.Key] = pair.Value;
            }
            catch (KeyNotFoundException)
            {
                rejections.Add($"{pair.Key}: unknown design property.");
            }
            catch (FormatException)
            {
                rejections.Add($"{pair.Key}: '{pair.Value}' is not a valid value.");
            }
            catch (OverflowException)
            {
                rejections.Add($"{pair.Key}: '{pair.Value}' is out of range.");
            }
        }

        if (rejections.Count == 0 && spec.CanonicalDesignHash() == configuration.DesignHash)
            return new AcademicOverrideResult(true, spec, applied, Array.Empty<string>());

        foreach (var violation in presetViolations(preset, spec))
            rejections.Add(violation);
        foreach (var violation in templateOf(configuration)?.Compliance.Violations(spec) ?? Array.Empty<string>())
            rejections.Add($"{violation}: below the compliance floor of template '{configuration.TemplateSlug}'.");
        if (AcademicAccessibilityPolicy.ContrastRatio(spec.TextColorHex, spec.BackgroundColorHex) < AcademicAccessibilityPolicy.MinimumTextContrastRatio)
            rejections.Add("text-contrast: below WCAG 4.5:1 for body text.");
        if (AcademicRtlPolicy.MinimumLineSpacingFor(spec) > spec.LineSpacing + 0.001)
            rejections.Add($"line-spacing: RTL designs floor at {AcademicRtlPolicy.MinimumLineSpacingFor(spec):0.##}.");
        if (AcademicRtlPolicy.MinimumBodySizeFor(spec) > spec.BodySizePt + 0.001)
            rejections.Add($"body-size: RTL script floor is {AcademicRtlPolicy.MinimumBodySizeFor(spec):0.##} pt.");

        return rejections.Count > 0
            ? new AcademicOverrideResult(false, null, applied, rejections)
            : new AcademicOverrideResult(true, spec, applied, Array.Empty<string>());
    }

    private static AcademicTemplateDefinition? templateOf(AcademicTemplateConfiguration configuration) => AcademicTemplateRegistry.GetTemplate(configuration.TemplateSlug);

    private static IReadOnlyList<string> presetViolations(AcademicUniversityPreset preset, AcademicDesignSpec spec)
    {
        var issues = new List<string>();
        foreach (var pair in preset.PinnedSpec)
        {
            var currentValue = ReadSpecValue(spec, pair.Key);
            if (currentValue is not null && !string.Equals(currentValue, pair.Value, StringComparison.Ordinal))
                issues.Add($"{pair.Key}: preset pins '{pair.Value}'.");
        }
        return issues;
    }

    private static string? ReadSpecValue(AcademicDesignSpec spec, string key) => key switch
    {
        "paperFormat" => spec.PaperFormat,
        "marginTopPt" => spec.MarginTopPt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "marginBottomPt" => spec.MarginBottomPt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "marginLeftPt" => spec.MarginLeftPt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "marginRightPt" => spec.MarginRightPt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "bodySizePt" => spec.BodySizePt.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "lineSpacing" => spec.LineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "digitSystem" => spec.DigitSystem,
        _ => null,
    };
}

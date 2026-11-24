using Taslim.Api.AcademicTemplates;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Pure-policy tests: canonical design hashing, the override engine, RTL/digits rules, and the
/// zero-charge re-export decision table from issue #1260 ("reformat/re-export of saved research must
/// not re-run generation").
/// </summary>
public class AcademicTemplatePolicyTests
{
    private static AcademicTemplateConfiguration FirstConfig(string slug) =>
        AcademicTemplateRegistry.Configurations.First(c => c.TemplateSlug == slug);

    [Fact]
    public void Design_hash_ignores_identity_but_tracks_every_design_field()
    {
        var cfg = AcademicTemplateRegistry.GetConfiguration("classic-thesis.times.150.classic.center")!;
        var renamed = cfg.Spec with { TemplateId = "taslim.academic.other.v9", ConfigurationId = "other.id" };
        Assert.Equal(cfg.Spec.CanonicalDesignHash(), renamed.CanonicalDesignHash());

        // Flipping any single design-visible property changes the hash.
        Assert.NotEqual(cfg.Spec.CanonicalDesignHash(), (cfg.Spec with { LineSpacing = cfg.Spec.LineSpacing + 0.01 }).CanonicalDesignHash());
        Assert.NotEqual(cfg.Spec.CanonicalDesignHash(), (cfg.Spec with { BodySizePt = cfg.Spec.BodySizePt + 0.5 }).CanonicalDesignHash());
        Assert.NotEqual(cfg.Spec.CanonicalDesignHash(), (cfg.Spec with { PageNumberPosition = "top-center" }).CanonicalDesignHash());
        Assert.NotEqual(cfg.Spec.CanonicalDesignHash(), (cfg.Spec with { BibliographyHangingIndent = !cfg.Spec.BibliographyHangingIndent }).CanonicalDesignHash());
    }

    [Fact]
    public void Override_engine_rejects_unknown_keys_and_accepts_known_ones()
    {
        var cfg = FirstConfig("classic-thesis");
        Assert.Throws<KeyNotFoundException>(() => cfg.Spec.WithOverride("arbitraryHtml", "<b>hi</b>"));
        Assert.Throws<KeyNotFoundException>(() => cfg.Spec.WithOverride("provider", "whatever"));
        Assert.Throws<KeyNotFoundException>(() => cfg.Spec.WithOverride("model", "gpt"));
        var applied = cfg.Spec.WithOverride("bodySizePt", "12");
        Assert.Equal(12, applied.BodySizePt);
    }

    [Fact]
    public void Overrides_apply_order_independently()
    {
        var cfg = FirstConfig("classic-thesis");
        var a = cfg.Spec.WithOverrides(new Dictionary<string, string> { ["bodySizePt"] = "12", ["lineSpacing"] = "2" });
        var b = cfg.Spec.WithOverrides(new Dictionary<string, string> { ["lineSpacing"] = "2", ["bodySizePt"] = "12" });
        Assert.Equal(a.CanonicalDesignHash(), b.CanonicalDesignHash());
    }

    [Fact]
    public void Accessibility_policy_enforces_wcag_contrast_and_size_floors()
    {
        Assert.Equal(21, AcademicAccessibilityPolicy.ContrastRatio("#000000", "#ffffff"), 3);
        Assert.True(AcademicAccessibilityPolicy.ContrastRatio(AcademicAccessibilityPolicy.DefaultTextColor, "#ffffff")
            >= AcademicAccessibilityPolicy.MinimumTextContrastRatio);
        Assert.True(AcademicAccessibilityPolicy.ContrastRatio("#ffffff", "#ffffff") < 1.1);
    }

    [Fact]
    public void Pagination_estimates_shrink_with_leading_and_grow_with_paper_height()
    {
        var baseSpec = FirstConfig("classic-thesis").Spec;
        var doubleSpaced = baseSpec with { LineSpacing = 2.0 };
        Assert.True(AcademicPaginationModel.LinesPerPage(baseSpec) > AcademicPaginationModel.LinesPerPage(doubleSpaced));

        var lines = Enumerable.Repeat("x", 200).ToList();
        Assert.True(AcademicPaginationModel.EstimatedPages(doubleSpaced, lines) > AcademicPaginationModel.EstimatedPages(baseSpec, lines));
        Assert.True(AcademicPaginationModel.EstimatedPages(baseSpec, lines) >= 1);
    }

    [Fact]
    public void Language_normalization_maps_classroom_aliases()
    {
        Assert.Equal("ku", AcademicRtlPolicy.NormalizeLanguage("ckb"));
        Assert.Equal("ku", AcademicRtlPolicy.NormalizeLanguage("KU"));
        Assert.Equal("ar", AcademicRtlPolicy.NormalizeLanguage("ar-EG"));
        Assert.Equal("en", AcademicRtlPolicy.NormalizeLanguage("en-GB"));
        Assert.Equal("en", AcademicRtlPolicy.NormalizeLanguage("de"));
        Assert.Equal("en", AcademicRtlPolicy.NormalizeLanguage(null));
        Assert.True(AcademicRtlPolicy.IsRtlLanguage("ckb"));
        Assert.False(AcademicRtlPolicy.IsRtlLanguage("en-US"));
    }

    [Fact]
    public void Digits_render_in_eastern_arabic_indic_when_selected()
    {
        var cfg = AcademicTemplateRegistry.GetConfiguration("arabic-dissertation-ar.naskh.band.indic.outside")!;
        Assert.Equal("arabic-indic", cfg.Spec.DigitSystem);
        Assert.Equal("١٢٣", AcademicRtlPolicy.FormatDigits("123", cfg.Spec));
        var latinCfg = AcademicTemplateRegistry.GetConfiguration("kurdish-thesis-ckb.naskh.latin.150")!;
        Assert.Equal("latin", latinCfg.Spec.DigitSystem);
        Assert.Equal("123", AcademicRtlPolicy.FormatDigits("123", latinCfg.Spec));
    }

    [Fact]
    public void Page_labels_follow_scheme_and_script()
    {
        var spec = FirstConfig("classic-thesis").Spec;
        Assert.Equal("iii", AcademicRtlPolicy.PageLabel(3, "roman-lower", spec));
        Assert.Equal("III", AcademicRtlPolicy.PageLabel(3, "roman-upper", spec));
        Assert.Equal("4", AcademicRtlPolicy.PageLabel(4, "arabic", spec));
        Assert.Equal("", AcademicRtlPolicy.PageLabel(4, "none", spec));
        Assert.Equal("xiv", AcademicRtlPolicy.ToRoman(14)); // ToRoman yields lowercase; roman-upper uppercases it
        var arabic = AcademicTemplateRegistry.GetConfiguration("arabic-dissertation-ar.naskh.band.indic.outside")!.Spec;
        Assert.Equal("٤", AcademicRtlPolicy.PageLabel(4, "arabic", arabic));
        Assert.Equal("lowerRoman", AcademicRtlPolicy.WordPageNumberFormat("roman-lower", spec));
        Assert.Equal("none", AcademicRtlPolicy.WordPageNumberFormat("none", spec));
    }

    [Fact]
    public void Direction_consistency_rules()
    {
        var kurdish = AcademicTemplateRegistry.GetConfiguration("kurdish-thesis-ckb.naskh.latin.150")!.Spec;
        Assert.True(AcademicRtlPolicy.IsDirectionConsistent(kurdish));
        Assert.False(AcademicRtlPolicy.IsDirectionConsistent(kurdish with { Direction = "ltr" }));
        var english = FirstConfig("classic-thesis").Spec;
        Assert.False(AcademicRtlPolicy.IsDirectionConsistent(english with { Direction = "rtl" }));
        // A bilingual English design may legally flow RTL when it advertises the RTL abstract page.
        Assert.True(AcademicRtlPolicy.IsDirectionConsistent(english with { Direction = "rtl", SupportsRtlAbstractPage = true }));
    }

    [Fact]
    public void Snapshot_roundtrips_through_json_and_hashes_stably()
    {
        var cfg = FirstConfig("classic-thesis");
        var snapshot = AcademicExportSnapshot.From(cfg, "research_abc123", "sha256:deadbeef",
            new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string> { ["bodySizePt"] = "12" }, "uk-hei-dissertation");
        var json = snapshot.ToJson();
        var back = AcademicExportSnapshot.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal(snapshot.TemplateId, back!.TemplateId);
        Assert.Equal(snapshot.ConfigurationId, back.ConfigurationId);
        Assert.Equal(snapshot.DesignHash, back.DesignHash);
        Assert.Equal(snapshot.SourceContentHash, back.SourceContentHash);
        Assert.Equal("12", back.StudentOverrides["bodySizePt"]);
        Assert.Equal(snapshot.SnapshotHash(), back.SnapshotHash());
        Assert.Null(AcademicExportSnapshot.FromJson("{ not json"));
    }

    [Fact]
    public void Reexport_never_charges_when_source_payload_is_unchanged()
    {
        var cfg = FirstConfig("classic-thesis");
        var other = FirstConfig("uk-postgraduate-dissertation");
        var snapshot = AcademicExportSnapshot.From(cfg, "research_abc", "sha256:cafe", DateTimeOffset.UtcNow);

        var identical = AcademicReExportPolicy.Evaluate(snapshot, cfg, "sha256:cafe");
        Assert.True(identical.AllowedWithoutGenerationCharge);
        Assert.False(identical.TriggersProviderCall);
        Assert.Equal(AcademicReExportPolicy.ModeReExportSameSettings, identical.Mode);
        Assert.Equal("REEXPORT_IDENTICAL", identical.ReasonCode);

        var reformatted = AcademicReExportPolicy.Evaluate(snapshot, other, "sha256:cafe");
        Assert.True(reformatted.AllowedWithoutGenerationCharge);
        Assert.False(reformatted.TriggersProviderCall);
        Assert.Equal(AcademicReExportPolicy.ModeTemplateReformat, reformatted.Mode);
        Assert.Equal("REFORMAT_ONLY", reformatted.ReasonCode);
    }

    [Fact]
    public void Changed_source_or_unknown_target_never_authorises_a_free_generation()
    {
        var cfg = FirstConfig("classic-thesis");
        var snapshot = AcademicExportSnapshot.From(cfg, "research_abc", "sha256:cafe", DateTimeOffset.UtcNow);

        var changed = AcademicReExportPolicy.Evaluate(snapshot, cfg, "sha256:fresh");
        Assert.False(changed.AllowedWithoutGenerationCharge);
        Assert.True(changed.TriggersProviderCall);
        Assert.Equal(AcademicReExportPolicy.ModeNewGenerationRequired, changed.Mode);
        Assert.Equal("SOURCE_CHANGED", changed.ReasonCode);

        var missing = AcademicReExportPolicy.Evaluate(null, cfg, "sha256:cafe");
        Assert.False(missing.AllowedWithoutGenerationCharge);
        Assert.Equal("SNAPSHOT_MISSING", missing.ReasonCode);

        var unknown = AcademicReExportPolicy.Evaluate(snapshot, null, "sha256:cafe");
        Assert.False(unknown.AllowedWithoutGenerationCharge);
        Assert.Equal("TARGET_UNKNOWN", unknown.ReasonCode);
    }

    [Fact]
    public void Snapshot_from_requires_artifact_identity()
    {
        var cfg = FirstConfig("classic-thesis");
        Assert.Throws<ArgumentException>(() => AcademicExportSnapshot.From(cfg, "", "hash", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => AcademicExportSnapshot.From(cfg, "research_abc", " ", DateTimeOffset.UtcNow));
    }
}

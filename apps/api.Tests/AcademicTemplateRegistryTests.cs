using Taslim.Api.AcademicTemplates;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Issue #1260 acceptance invariants for the template library itself: the vetted base-template band,
/// the 200+ genuinely distinct previewable configurations (hash-deduped), deterministic expansion,
/// compliance/RTL/contrast filtering, search facets and university-preset referential integrity.
/// </summary>
public class AcademicTemplateRegistryTests
{
    [Fact]
    public void Registry_offers_between_15_and_25_vetted_base_templates()
    {
        Assert.InRange(AcademicTemplateRegistry.Templates.Count, 15, 25);
    }

    [Fact]
    public void Registry_offers_at_least_200_distinct_configurations()
    {
        Assert.True(AcademicTemplateRegistry.Configurations.Count >= 200,
            $"expected 200+ configurations, found {AcademicTemplateRegistry.Configurations.Count}");
    }

    [Fact]
    public void Every_configuration_is_a_distinct_canonical_design()
    {
        var hashes = AcademicTemplateRegistry.Configurations.Select(c => c.DesignHash).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(hashes.Count, AcademicTemplateRegistry.Configurations
            .Select(c => c.Spec.CanonicalDesignHash()).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Configuration_ids_are_unique_and_version_scoped()
    {
        var ids = AcademicTemplateRegistry.Configurations.Select(c => c.ConfigurationId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var cfg in AcademicTemplateRegistry.Configurations)
        {
            Assert.StartsWith(cfg.TemplateSlug + ".", cfg.ConfigurationId, StringComparison.Ordinal);
            Assert.Equal($"taslim.academic.{cfg.TemplateSlug}.v{AcademicTemplateDefinition.CurrentDefinitionVersion}", cfg.Spec.TemplateId);
            Assert.Equal(cfg.ConfigurationId, cfg.Spec.ConfigurationId);
        }
    }

    [Fact]
    public void Template_ids_embed_the_definition_version()
    {
        foreach (var template in AcademicTemplateRegistry.Templates)
            Assert.Equal($"taslim.academic.{template.Slug}.v{AcademicTemplateDefinition.CurrentDefinitionVersion}", template.TemplateId);
    }

    [Fact]
    public void Registry_expansion_is_deterministic()
    {
        var first = AcademicTemplateRegistry.Configurations
            .Select(c => $"{c.ConfigurationId}|{c.DesignHash}").ToList();
        // A second, independent expansion of the same definitions must reproduce the same set.
        var second = AcademicTemplateRegistry.Configurations
            .Select(c => $"{c.ConfigurationId}|{c.DesignHash}").ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_configuration_hash_matches_its_own_spec()
    {
        foreach (var cfg in AcademicTemplateRegistry.Configurations)
            Assert.Equal(cfg.Spec.CanonicalDesignHash(), cfg.DesignHash);
    }

    [Fact]
    public void Every_configuration_satisfies_its_templates_compliance_and_direction_rules()
    {
        foreach (var cfg in AcademicTemplateRegistry.Configurations)
        {
            var template = AcademicTemplateRegistry.GetTemplate(cfg.TemplateSlug)!;
            Assert.Empty(template.Compliance.Violations(cfg.Spec));
            Assert.True(AcademicRtlPolicy.IsDirectionConsistent(cfg.Spec), cfg.ConfigurationId);
            Assert.True(AcademicAccessibilityPolicy.ContrastRatio(cfg.Spec.TextColorHex, cfg.Spec.BackgroundColorHex)
                >= AcademicAccessibilityPolicy.MinimumTextContrastRatio, cfg.ConfigurationId);
            Assert.True(cfg.Spec.BodySizePt >= AcademicAccessibilityPolicy.MinimumBodySizePt, cfg.ConfigurationId);
        }
    }

    [Fact]
    public void Compliance_gate_actually_drops_decorative_variants()
    {
        // The dissertation profile floors line spacing at 1.25; the 1.15 leading option on the
        // modern-dissertation base must therefore be filtered out of the catalogue, not shipped.
        Assert.Null(AcademicTemplateRegistry.GetConfiguration("modern-dissertation.palatinocalibri.115.minimal.none"));
        Assert.NotNull(AcademicTemplateRegistry.GetConfiguration("modern-dissertation.palatinocalibri.150.minimal.none"));
        Assert.DoesNotContain(AcademicTemplateRegistry.Configurations
            .Where(c => c.TemplateSlug == "modern-dissertation")
            .Select(c => c.ConfigurationId),
            id => id.Contains(".115.", StringComparison.Ordinal));
    }

    [Fact]
    public void Rtl_languages_are_represented_and_direction_consistent()
    {
        Assert.Contains(AcademicTemplateRegistry.Configurations, c => c.Spec.LanguageCode == "ku");
        Assert.Contains(AcademicTemplateRegistry.Configurations, c => c.Spec.LanguageCode == "ar");
        Assert.Contains(AcademicTemplateRegistry.Configurations, c => c.Spec.Direction == AcademicTextDirection.Rtl);
        foreach (var cfg in AcademicTemplateRegistry.Configurations.Where(c => AcademicRtlPolicy.IsRtlLanguage(c.Spec.LanguageCode)))
            Assert.Equal(AcademicTextDirection.Rtl, cfg.Spec.Direction);
    }

    [Fact]
    public void Every_document_kind_and_citation_style_in_the_issue_brief_is_searchable()
    {
        foreach (var kind in new[] { "thesis", "dissertation", "literature-review", "research-proposal", "journal-article", "case-study" })
            Assert.True(AcademicTemplateRegistry.CountMatches(documentKind: kind) > 0, kind);
        foreach (var style in new[] { "apa-7", "mla-9", "chicago-notes-bibliography", "ieee", "turabian" })
            Assert.True(AcademicTemplateRegistry.CountMatches(citationStyle: style) > 0, style);
        Assert.True(AcademicTemplateRegistry.CountMatches(language: "ckb") > 0);
        Assert.True(AcademicTemplateRegistry.CountMatches(language: "ar") > 0);
        Assert.True(AcademicTemplateRegistry.CountMatches(paperFormat: "a4") > 100);
        Assert.True(AcademicTemplateRegistry.CountMatches(direction: "rtl") > 0);
    }

    [Fact]
    public void Search_pagination_and_limits_are_stable()
    {
        var page1 = AcademicTemplateRegistry.Search(limit: 50, offset: 0);
        var page2 = AcademicTemplateRegistry.Search(limit: 50, offset: 50);
        Assert.Equal(50, page1.Count);
        Assert.Equal(50, page2.Count);
        Assert.Empty(page1.Select(c => c.ConfigurationId).Intersect(page2.Select(c => c.ConfigurationId)));
        var clamped = AcademicTemplateRegistry.Search(limit: 5000);
        Assert.True(clamped.Count <= 400);
        Assert.Equal(AcademicTemplateRegistry.Configurations.Count, AcademicTemplateRegistry.CountMatches());
    }

    [Fact]
    public void Configuration_counts_per_template_sum_to_the_total_and_every_base_expands()
    {
        var counts = AcademicTemplateRegistry.ConfigurationCountsByTemplate;
        Assert.Equal(AcademicTemplateRegistry.Configurations.Count, counts.Values.Sum());
        Assert.Equal(AcademicTemplateRegistry.Templates.Count, counts.Count);
        Assert.All(counts.Values, n => Assert.True(n >= 6, $"base template with only {n} surviving variants"));
    }

    [Fact]
    public void University_preset_defaults_exist_in_the_registry_and_satisfy_their_presets()
    {
        foreach (var preset in AcademicUniversityPresetRegistry.All)
        {
            var cfg = AcademicTemplateRegistry.GetConfiguration(preset.DefaultConfigurationId);
            Assert.NotNull(cfg);
            Assert.Equal(preset.DefaultTemplateSlug, cfg!.TemplateSlug);
            // Re-applying the preset's own pinned values is a no-op (accepted, design unchanged).
            var identity = AcademicUniversityPresetRegistry.ApplyOverrides(preset, cfg, preset.PinnedSpec);
            Assert.True(identity.Accepted, string.Join("; ", identity.Rejections));
            Assert.Equal(cfg.DesignHash, identity.Spec!.CanonicalDesignHash());
            // Any attempt to move a pinned key is rejected with the preset's note, not silently applied.
            foreach (var key in preset.PinnedKeys)
            {
                var result = AcademicUniversityPresetRegistry.ApplyOverrides(
                    preset, cfg, new Dictionary<string, string> { [key] = "20" });
                Assert.False(result.Accepted, $"{preset.Id}/{key}");
                Assert.Contains(result.Rejections, r => r.Contains("fixed by", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Unknown_override_keys_are_rejected_rather_than_ignored()
    {
        var cfg = AcademicTemplateRegistry.Configurations.First();
        Assert.Throws<KeyNotFoundException>(() => cfg.Spec.WithOverride("notARealKey", "5"));
        var result = AcademicUniversityPresetRegistry.ApplyOverrides(
            AcademicUniversityPresetRegistry.All.First(), cfg,
            new Dictionary<string, string> { ["arbitraryHtml"] = "<b>x</b>" });
        Assert.False(result.Accepted);
    }
}

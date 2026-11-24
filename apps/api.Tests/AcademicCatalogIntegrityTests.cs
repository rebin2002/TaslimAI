using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.AcademicTemplates;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The two customer-facing promise gates from the epic: (1) backend identity (provider/model/routing/
/// cost) must never be reachable through catalog payloads or generated artifacts, and (2) the
/// bibliography gate refuses synthetic placeholders and unverifiable references in real exports —
/// preview material must never be smuggled into submitted work, in either direction.
/// </summary>
public class AcademicCatalogIntegrityTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Powered by OpenAI for you", "openai")]
    [InlineData("uses gpt-4 for drafting", "gpt-4")]
    [InlineData("model-routing decided", "routing")]
    [InlineData("see api-key field", "api-key")]
    [InlineData("per-job secret rotation", "secret")]
    [InlineData("credits deducted", "credits")]
    [InlineData("usd cost per page", "usd")]
    [InlineData("billing service", "billing")]
    [InlineData("UPSTREAM host", "upstream")]
    public void Guard_detects_backend_identity_tokens(string payload, string expectedToken)
    {
        Assert.Contains(expectedToken, AcademicCatalogPrivacyGuard.FindLeaks(payload));
    }

    [Theory]
    [InlineData("Academic thesis layout, Times & Calibri pairing")]
    [InlineData("Page numbering restarts after front matter")]
    [InlineData("Kurdish Sorani right-to-left dissertation")]
    public void Guard_leaves_benign_academic_copy_alone(string payload)
    {
        Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(payload));
    }

    [Fact]
    public void Scrub_refuses_payloads_that_would_leak()
    {
        Assert.Throws<InvalidOperationException>(() => AcademicCatalogPrivacyGuard.Scrub(new { note = "cost usd per generation" }));
        _ = AcademicCatalogPrivacyGuard.Scrub(new { note = "A4, 1.5 spacing, bottom-centre folio" });
    }

    [Fact]
    public void Entire_serialized_catalog_carries_no_backend_identity()
    {
        var controller = new AcademicTemplatesController();
        var catalog = Assert.IsType<AcademicTemplateCatalogDto>(
            Assert.IsType<OkObjectResult>(controller.Catalog()).Value);
        var leaks = AcademicCatalogPrivacyGuard.FindLeaks(JsonSerializer.Serialize(catalog, Web));
        Assert.Empty(leaks);

        foreach (var cfg in AcademicTemplateRegistry.Configurations)
        {
            var dto = AcademicTemplatesController.ToConfigurationDto(cfg);
            Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(JsonSerializer.Serialize(dto, Web)));
        }

        var presets = Assert.IsType<List<AcademicUniversityPresetDto>>(
            Assert.IsType<OkObjectResult>(controller.Presets()).Value);
        Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(JsonSerializer.Serialize(presets, Web)));
    }

    [Fact]
    public void Preview_dto_html_and_banner_are_token_clean()
    {
        var controller = new AcademicTemplatesController();
        var cfg = AcademicTemplateRegistry.GetConfiguration("arabic-dissertation-ar.naskh.band.indic.outside")!;
        var preview = Assert.IsType<AcademicTemplatePreviewDto>(
            Assert.IsType<OkObjectResult>(controller.Preview(cfg.ConfigurationId)).Value);
        Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(preview.PreviewHtml));
        Assert.Equal(cfg.DesignHash, preview.DesignHash);
        Assert.Contains("معاينة", preview.SampleBanner);
    }

    [Fact]
    public void Configuration_controller_paths_return_dto_or_404()
    {
        var controller = new AcademicTemplatesController();
        var ok = Assert.IsType<AcademicTemplateConfigurationDto>(
            Assert.IsType<OkObjectResult>(controller.Configuration("classic-thesis.times.150.classic.center")).Value);
        Assert.Equal("classic-thesis.times.150.classic.center", ok.ConfigurationId);

        var notFound = Assert.IsType<ObjectResult>(controller.Configuration("does-not-exist"));
        Assert.Equal(404, notFound.StatusCode);
        Assert.Contains("ACADEMIC_TEMPLATE_NOT_FOUND", JsonSerializer.Serialize(notFound.Value, Web));
        var previewMissing = Assert.IsType<ObjectResult>(controller.Preview("does-not-exist"));
        Assert.Equal(404, previewMissing.StatusCode);
    }

    [Fact]
    public void Bibliography_gate_blocks_placeholders_and_unverifiable_rows()
    {
        var report = AcademicBibliographyIntegrity.ValidateExportable(new[]
        {
            new AcademicBibliographyEntry("Author, A. (2020). A real study. https://doi.org/10.1000/xyz123", "10.1000/xyz123", null, null, false),
        });
        Assert.True(report.Passed);

        var blocked = AcademicBibliographyIntegrity.ValidateExportable(new[]
        {
            new AcademicBibliographyEntry("Format sample — not a real source", null, null, null, true),
            new AcademicBibliographyEntry("Mystery reference with no identifier", null, null, null, false),
            new AcademicBibliographyEntry("Placeholder host only", null, null, "https://example.org/x", false),
        });
        Assert.False(blocked.Passed);
        Assert.Equal(3, blocked.Violations.Count);
    }

    [Fact]
    public void Bibliography_gate_accepts_isbn_and_real_urls()
    {
        var report = AcademicBibliographyIntegrity.ValidateExportable(new[]
        {
            new AcademicBibliographyEntry("Handbook, 3rd ed.", null, "978-0306406137", null, false),
            new AcademicBibliographyEntry("Stats agency table", null, null, "https://data.gov.example-region/tables/42", false),
        });
        Assert.True(report.Passed);
    }

    [Fact]
    public void Fabricated_locator_heuristic_flags_sample_markers()
    {
        Assert.True(AcademicBibliographyIntegrity.LooksLikeFabricatedLocator(
            new AcademicBibliographyEntry(AcademicSampleContent.BibliographyMarkerEn, null, null, null, true)));
        Assert.False(AcademicBibliographyIntegrity.LooksLikeFabricatedLocator(
            new AcademicBibliographyEntry("Currie, J. (2022). Documented dataset, DOI attached.", "10.1000/abc", null, null, false)));
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.AcademicTemplates;

// ── gallery-facing DTOs ────────────────────────────────────────────────────────────────
// Shape rule for this whole file: the customer-facing surface carries design data only.
// No provider, model, routing, cost, credit or backend identity field exists on any of these
// records, and CI (AcademicCatalogPrivacyGuardTests) fails the build if one is ever added by name.

public sealed record AcademicTemplateSummaryDto(
    string Slug,
    string TemplateId,
    int DefinitionVersion,
    string DisplayName,
    string Description,
    IReadOnlyList<string> DocumentKinds,
    IReadOnlyList<string> CitationStyles,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> PaperFormats,
    string Direction,
    string FormattingLineage,
    int ConfigurationCount,
    IReadOnlyList<string> SampleConfigurationIds);

public sealed record AcademicTemplateCatalogDto(
    int DefinitionVersion,
    string CatalogVersion,
    int BaseTemplateCount,
    int DistinctConfigurationCount,
    IReadOnlyList<string> DocumentKinds,
    IReadOnlyList<string> CitationStyles,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> PaperFormats,
    IReadOnlyList<AcademicTemplateSummaryDto> Templates);

public sealed record AcademicTemplateConfigurationDto(
    string ConfigurationId,
    string TemplateSlug,
    string TemplateId,
    string DisplayName,
    IReadOnlyDictionary<string, string> VariantSelection,
    string DesignHash,
    AcademicDesignSpecDto Spec);

public sealed record AcademicDesignSpecDto(
    string LanguageCode,
    string Direction,
    string PaperFormat,
    double MarginTopPt,
    double MarginBottomPt,
    double MarginLeftPt,
    double MarginRightPt,
    double BindingEdgePt,
    string BindingEdge,
    int ColumnCount,
    double ColumnGapPt,
    string BodyFontToken,
    string HeadingFontToken,
    string CaptionFontToken,
    string MonoFontToken,
    double BodySizePt,
    double TypeScaleRatio,
    double LineSpacing,
    double ParagraphAfterPt,
    double FirstLineIndentPt,
    bool JustifyBody,
    double HeadingTopGapPt,
    bool HeadingsBold,
    bool H1Uppercase,
    string HeadingNumbering,
    int HeadingLevels,
    string DigitSystem,
    string CoverStyle,
    bool IncludesApprovalPage,
    bool SupportsRtlAbstractPage,
    bool IncludesAiUseDisclosureSlot,
    bool IncludeToc,
    int TocDepth,
    bool TocDotLeaders,
    bool IncludeListOfFiguresTables,
    string FrontMatterNumbering,
    string BodyNumbering,
    bool RestartNumbersAfterFrontMatter,
    string PageNumberPosition,
    string RunningHeadContent,
    double RunningHeadRulePt,
    string DefaultCitationStyle,
    string CitationMarkers,
    bool FootnotesEnabled,
    string FootnotePlacement,
    bool BibliographyHangingIndent,
    double BibliographyIndentPt,
    string FigureCaptionPosition,
    string TableCaptionPosition,
    string FigureNumbering,
    string TextColorHex,
    string BackgroundColorHex,
    string AccentColorHex,
    double PaperWidthPt,
    double PaperHeightPt);

public sealed record AcademicTemplatePreviewDto(
    string ConfigurationId,
    string DesignHash,
    string PreviewVersion,
    string PreviewHtml,
    string SampleBanner);

public sealed record AcademicUniversityPresetDto(
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

/// <summary>
/// Read-only catalog surface for the academic template gallery (issue #1260). Endpoints:
///   GET api/academic-templates                    — base templates + counters + taxonomy
///   GET api/academic-templates/configurations     — filterable, paged selectable designs
///   GET api/academic-templates/configurations/{id} — one design + its spec
///   GET api/academic-templates/configurations/{id}/preview — synthetic preview HTML
///   GET api/academic-templates/presets            — university formatting profiles
///
/// No write endpoints exist here by design: previewing is stateless and private-data-free, and the
/// export job creation stays in the research lanes (they attach snapshots via this module's contract).
/// </summary>
[ApiController]
[Authorize]
[Route("api/academic-templates")]
public sealed class AcademicTemplatesController : ControllerBase
{
    [HttpGet("")]
    public IActionResult Catalog()
    {
        var summaries = AcademicTemplateRegistry.Templates.Select(template =>
        {
            var ids = AcademicTemplateRegistry.Configurations
                .Where(configuration => configuration.TemplateSlug == template.Slug)
                .Select(configuration => configuration.ConfigurationId)
                .ToList();
            return new AcademicTemplateSummaryDto(
                template.Slug,
                template.TemplateId,
                template.DefinitionVersion,
                template.DisplayName,
                template.Description,
                template.DocumentKinds,
                template.CitationStyles,
                template.Languages,
                template.PaperFormats,
                template.Direction,
                template.FormattingLineage,
                ids.Count,
                ids.Take(4).ToList());
        }).ToList();

        return Ok(new AcademicTemplateCatalogDto(
            AcademicTemplateRegistry.DefinitionVersion,
            "academic-templates-" + AcademicTemplateRegistry.DefinitionVersion,
            summaries.Count,
            AcademicTemplateRegistry.Configurations.Count,
            AcademicTemplateTaxonomy.DocumentKinds,
            AcademicTemplateTaxonomy.CitationStyles,
            AcademicTemplateTaxonomy.Languages,
            AcademicTemplateTaxonomy.PaperFormats,
            summaries));
    }

    [HttpGet("configurations")]
    public IActionResult Configurations(
        [FromQuery] string? documentKind,
        [FromQuery] string? citationStyle,
        [FromQuery] string? language,
        [FromQuery] string? paperFormat,
        [FromQuery] string? direction,
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var total = AcademicTemplateRegistry.CountMatches(documentKind, citationStyle, language, paperFormat, direction, q);
        var items = AcademicTemplateRegistry.Search(documentKind, citationStyle, language, paperFormat, direction, q, limit, offset)
            .Select(ToConfigurationDto)
            .ToList();
        return Ok(new { total, limit = Math.Clamp(limit, 1, 400), offset = Math.Max(0, offset), items });
    }

    [HttpGet("configurations/{configurationId}")]
    public IActionResult Configuration(string configurationId)
    {
        var configuration = AcademicTemplateRegistry.GetConfiguration(configurationId);
        if (configuration is null)
            return ApiResults.Error(this, StatusCodes.Status404NotFound, "ACADEMIC_TEMPLATE_NOT_FOUND", "Unknown template configuration.");
        return Ok(ToConfigurationDto(configuration));
    }

    [HttpGet("configurations/{configurationId}/preview")]
    public IActionResult Preview(string configurationId)
    {
        var configuration = AcademicTemplateRegistry.GetConfiguration(configurationId);
        if (configuration is null)
            return ApiResults.Error(this, StatusCodes.Status404NotFound, "ACADEMIC_TEMPLATE_NOT_FOUND", "Unknown template configuration.");
        var sample = AcademicSampleContent.For(configuration.Spec);
        return Ok(new AcademicTemplatePreviewDto(
            configuration.ConfigurationId,
            configuration.DesignHash,
            AcademicPreviewRenderer.PreviewVersion,
            AcademicPreviewRenderer.Render(configuration.Spec, sample),
            sample.Banner));
    }

    [HttpGet("presets")]
    public IActionResult Presets() => Ok(AcademicUniversityPresetRegistry.All
        .Select(preset => new AcademicUniversityPresetDto(
            preset.Id, preset.DisplayName, preset.Region, preset.Notes, preset.DefaultTemplateSlug,
            preset.DefaultConfigurationId, preset.PinnedSpec, preset.PinnedKeys, preset.StudentEditableGroups,
            preset.VerificationNote))
        .ToList());

    internal static AcademicTemplateConfigurationDto ToConfigurationDto(AcademicTemplateConfiguration configuration) => new(
        configuration.ConfigurationId,
        configuration.TemplateSlug,
        configuration.Spec.TemplateId,
        configuration.DisplayName,
        configuration.VariantSelection,
        configuration.DesignHash,
        new AcademicDesignSpecDto(
            configuration.Spec.LanguageCode,
            configuration.Spec.Direction,
            configuration.Spec.PaperFormat,
            configuration.Spec.MarginTopPt,
            configuration.Spec.MarginBottomPt,
            configuration.Spec.MarginLeftPt,
            configuration.Spec.MarginRightPt,
            configuration.Spec.BindingEdgePt,
            configuration.Spec.BindingEdge,
            configuration.Spec.ColumnCount,
            configuration.Spec.ColumnGapPt,
            configuration.Spec.BodyFontToken,
            configuration.Spec.HeadingFontToken,
            configuration.Spec.CaptionFontToken,
            configuration.Spec.MonoFontToken,
            configuration.Spec.BodySizePt,
            configuration.Spec.TypeScaleRatio,
            configuration.Spec.LineSpacing,
            configuration.Spec.ParagraphAfterPt,
            configuration.Spec.FirstLineIndentPt,
            configuration.Spec.JustifyBody,
            configuration.Spec.HeadingTopGapPt,
            configuration.Spec.HeadingsBold,
            configuration.Spec.H1Uppercase,
            configuration.Spec.HeadingNumbering,
            configuration.Spec.HeadingLevels,
            configuration.Spec.DigitSystem,
            configuration.Spec.CoverStyle,
            configuration.Spec.IncludesApprovalPage,
            configuration.Spec.SupportsRtlAbstractPage,
            configuration.Spec.IncludesAiUseDisclosureSlot,
            configuration.Spec.IncludeToc,
            configuration.Spec.TocDepth,
            configuration.Spec.TocDotLeaders,
            configuration.Spec.IncludeListOfFiguresTables,
            configuration.Spec.FrontMatterNumbering,
            configuration.Spec.BodyNumbering,
            configuration.Spec.RestartNumbersAfterFrontMatter,
            configuration.Spec.PageNumberPosition,
            configuration.Spec.RunningHeadContent,
            configuration.Spec.RunningHeadRulePt,
            configuration.Spec.DefaultCitationStyle,
            configuration.Spec.CitationMarkers,
            configuration.Spec.FootnotesEnabled,
            configuration.Spec.FootnotePlacement,
            configuration.Spec.BibliographyHangingIndent,
            configuration.Spec.BibliographyIndentPt,
            configuration.Spec.FigureCaptionPosition,
            configuration.Spec.TableCaptionPosition,
            configuration.Spec.FigureNumbering,
            configuration.Spec.TextColorHex,
            configuration.Spec.BackgroundColorHex,
            configuration.Spec.AccentColorHex,
            configuration.Spec.PaperWidthPt,
            configuration.Spec.PaperHeightPt));
}

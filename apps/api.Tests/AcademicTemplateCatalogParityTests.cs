using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Taslim.Api.AcademicTemplates;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The web gallery ships a committed catalog snapshot (<c>apps/web/src/components/academic/templates/
/// academicTemplateCatalog.generated.json</c>) so the studio renders the full design library instantly,
/// even offline — and CI keeps the snapshot honest: this test re-derives it from the registry and fails
/// if any byte differs (drift between what the API offers and what the web bundle promises).
/// Set <c>TASLIM_ACADEMIC_REGEN=1</c> to rewrite the file after an intentional registry change.
/// </summary>
public class AcademicTemplateCatalogParityTests
{
    private const string RelativePath = "apps/web/src/components/academic/templates/academicTemplateCatalog.generated.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record WebFontToken(string Token, string DisplayName, string CssStack, string DocxFamily, bool ArabicScript);

    private sealed record WebTemplateSummary(
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
        int ConfigurationCount);

    private sealed record WebSpec(
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
        string FrontMatterNumbering,
        string BodyNumbering,
        bool RestartNumbersAfterFrontMatter,
        string PageNumberPosition,
        string RunningHeadContent,
        string DefaultCitationStyle,
        string CitationMarkers,
        bool FootnotesEnabled,
        bool BibliographyHangingIndent,
        double BibliographyIndentPt,
        string FigureCaptionPosition,
        string TableCaptionPosition,
        string TextColorHex,
        string BackgroundColorHex,
        string AccentColorHex,
        double PaperWidthPt,
        double PaperHeightPt);

    private sealed record WebConfiguration(
        string ConfigurationId,
        string TemplateSlug,
        string TemplateId,
        string DisplayName,
        IReadOnlyDictionary<string, string> VariantSelection,
        string DesignHash,
        WebSpec Spec);

    private sealed record WebCatalog(
        int Schema,
        string CatalogVersion,
        int DefinitionVersion,
        int BaseTemplateCount,
        int DistinctConfigurationCount,
        IReadOnlyList<string> DocumentKinds,
        IReadOnlyList<string> CitationStyles,
        IReadOnlyList<string> Languages,
        IReadOnlyList<string> PaperFormats,
        IReadOnlyList<string> Directions,
        IReadOnlyList<WebFontToken> FontTokens,
        IReadOnlyList<WebTemplateSummary> Templates,
        IReadOnlyList<WebConfiguration> Configurations,
        IReadOnlyList<AcademicUniversityPresetDto> Presets,
        IReadOnlyList<string> SyntheticBanners);

    private static string BuildJson()
    {
        var catalog = new WebCatalog(
            Schema: 1,
            CatalogVersion: "academic-templates-" + AcademicTemplateRegistry.DefinitionVersion,
            DefinitionVersion: AcademicTemplateRegistry.DefinitionVersion,
            BaseTemplateCount: AcademicTemplateRegistry.Templates.Count,
            DistinctConfigurationCount: AcademicTemplateRegistry.Configurations.Count,
            DocumentKinds: AcademicTemplateTaxonomy.DocumentKinds,
            CitationStyles: AcademicTemplateTaxonomy.CitationStyles,
            Languages: AcademicTemplateTaxonomy.Languages,
            PaperFormats: AcademicTemplateTaxonomy.PaperFormats,
            Directions: AcademicTemplateTaxonomy.Directions,
            FontTokens: AcademicFontTokens.All.Values
                .Select(token => new WebFontToken(token.Token, token.DisplayName, token.CssStack, token.DocxFamily, token.ArabicScript))
                .ToList(),
            Templates: AcademicTemplateRegistry.Templates.Select(template => new WebTemplateSummary(
                template.Slug, template.TemplateId, template.DefinitionVersion, template.DisplayName,
                template.Description, template.DocumentKinds, template.CitationStyles, template.Languages,
                template.PaperFormats, template.Direction, template.FormattingLineage,
                AcademicTemplateRegistry.Configurations.Count(c => c.TemplateSlug == template.Slug))).ToList(),
            Configurations: AcademicTemplateRegistry.Configurations.Select(cfg => new WebConfiguration(
                cfg.ConfigurationId, cfg.TemplateSlug, cfg.Spec.TemplateId, cfg.DisplayName,
                cfg.VariantSelection, cfg.DesignHash,
                new WebSpec(
                    cfg.Spec.LanguageCode, cfg.Spec.Direction, cfg.Spec.PaperFormat,
                    cfg.Spec.MarginTopPt, cfg.Spec.MarginBottomPt, cfg.Spec.MarginLeftPt, cfg.Spec.MarginRightPt,
                    cfg.Spec.BindingEdgePt, cfg.Spec.BindingEdge, cfg.Spec.ColumnCount, cfg.Spec.ColumnGapPt,
                    cfg.Spec.BodyFontToken, cfg.Spec.HeadingFontToken, cfg.Spec.CaptionFontToken, cfg.Spec.MonoFontToken,
                    cfg.Spec.BodySizePt, cfg.Spec.TypeScaleRatio, cfg.Spec.LineSpacing, cfg.Spec.ParagraphAfterPt,
                    cfg.Spec.FirstLineIndentPt, cfg.Spec.JustifyBody, cfg.Spec.HeadingsBold, cfg.Spec.H1Uppercase,
                    cfg.Spec.HeadingNumbering, cfg.Spec.HeadingLevels, cfg.Spec.DigitSystem, cfg.Spec.CoverStyle,
                    cfg.Spec.IncludesApprovalPage, cfg.Spec.SupportsRtlAbstractPage, cfg.Spec.IncludesAiUseDisclosureSlot,
                    cfg.Spec.IncludeToc, cfg.Spec.TocDepth, cfg.Spec.TocDotLeaders,
                    cfg.Spec.FrontMatterNumbering, cfg.Spec.BodyNumbering, cfg.Spec.RestartNumbersAfterFrontMatter,
                    cfg.Spec.PageNumberPosition, cfg.Spec.RunningHeadContent, cfg.Spec.DefaultCitationStyle,
                    cfg.Spec.CitationMarkers, cfg.Spec.FootnotesEnabled, cfg.Spec.BibliographyHangingIndent,
                    cfg.Spec.BibliographyIndentPt, cfg.Spec.FigureCaptionPosition, cfg.Spec.TableCaptionPosition,
                    cfg.Spec.TextColorHex, cfg.Spec.BackgroundColorHex, cfg.Spec.AccentColorHex,
                    cfg.Spec.PaperWidthPt, cfg.Spec.PaperHeightPt))).ToList(),
            Presets: AcademicUniversityPresetRegistry.All
                .Select(p => new AcademicUniversityPresetDto(p.Id, p.DisplayName, p.Region, p.Notes,
                    p.DefaultTemplateSlug, p.DefaultConfigurationId, p.PinnedSpec, p.PinnedKeys,
                    p.StudentEditableGroups, p.VerificationNote))
                .ToList(),
            SyntheticBanners: new[]
            {
                AcademicSampleContent.SyntheticBannerEn,
                AcademicSampleContent.SyntheticBannerAr,
                AcademicSampleContent.SyntheticBannerKu,
            });

        return JsonSerializer.Serialize(catalog, Options);
    }

    private static string? LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "apps", "web")) && File.Exists(Path.Combine(dir.FullName, "package.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void Committed_web_catalog_snapshot_matches_the_registry()
    {
        var root = LocateRepoRoot();
        Assert.NotNull(root);
        var target = Path.Combine(root!, RelativePath);
        var json = BuildJson();

        if (Environment.GetEnvironmentVariable("TASLIM_ACADEMIC_REGEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, json + Environment.NewLine, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(target), $"{RelativePath} is missing; run TASLIM_ACADEMIC_REGEN=1 dotnet test --filter AcademicTemplateCatalogParity");
        var committed = File.ReadAllText(target).TrimEnd('\r', '\n');
        Assert.Equal(json.TrimEnd('\r', '\n'), committed.Replace("\r\n", "\n"));

        // Even the regenerated form must stay privacy-clean and distinct-count-honest.
        Assert.Empty(AcademicCatalogPrivacyGuard.FindLeaks(json));
        Assert.True(JsonDocument.Parse(json).RootElement.GetProperty("distinctConfigurationCount").GetInt32() >= 200);
    }
}

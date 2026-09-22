namespace Taslim.Api.Research;

public sealed class ResearchGenerationOptions
{
    public bool Enabled { get; set; }
    public bool WebSourcesEnabled { get; set; } = true;
    public bool UserProvidedSourcesEnabled { get; set; } = true;
    public string SearchModel { get; set; } = "gpt-5.5";
    public string ReportTier { get; set; } = "Smart";
    public int MaxQuestionCharacters { get; set; } = 8_000;
    public int MaxAttachments { get; set; } = 5;
    public int MaxSourceCount { get; set; } = 16;
    public int MaxSourceTitleCharacters { get; set; } = 300;
    public int MaxSourceSnippetCharacters { get; set; } = 1_000;
    public int MaxSourceTextCharacters { get; set; } = 12_000;
    public int MaxEvidencePerSource { get; set; } = 8;
    public int MaxEvidenceCharacters { get; set; } = 2_000;
    public int MaxTotalEvidenceCharacters { get; set; } = 60_000;
    public int MaxContextCharacters { get; set; } = 80_000;
    public int MaxOutputTokens { get; set; } = 6_000;
    public int MaxSections { get; set; } = 20;
    public int MaxBlocks { get; set; } = 120;
    public int MaxKeyFindings { get; set; } = 12;
    public int MaxCitationsPerBlock { get; set; } = 8;
    public int MaxReportBlockCharacters { get; set; } = 8_000;
    public int MaxHeadingCharacters { get; set; } = 240;
    public int ProviderTimeoutSeconds { get; set; } = 240;
    public decimal EstimatedOutputUsdPer1KTokens { get; set; } = 0.01m;
    public string DefaultFontFamily { get; set; } = "Lato";
    public string RtlFontFamily { get; set; } = "Noto Sans Arabic";
    public string PricingVersion { get; set; } = "research-openai-2026-09-23";
    public string PricingSource { get; set; } = "https://developers.openai.com/api/docs/guides/tools-web-search";
}

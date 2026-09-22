namespace Taslim.Api.Presentations;

public sealed class PresentationGenerationOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAttachments { get; set; } = 5;
    public int MaxPromptCharacters { get; set; } = 8_000;
    public int MaxContextCharacters { get; set; } = 80_000;
    public int MaxOutputTokens { get; set; } = 5_000;
    public int MaxSlides { get; set; } = 18;
    public int MaxBlocksPerSlide { get; set; } = 8;
    public int MaxItemsPerBlock { get; set; } = 8;
    public int MaxRowsPerBlock { get; set; } = 8;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MaxSubtitleCharacters { get; set; } = 240;
    public int MaxBlockCharacters { get; set; } = 1_200;
    public int MaxNotesCharacters { get; set; } = 1_000;
    public int MaxVisualSuggestionCharacters { get; set; } = 400;
    public int MaxSourceRefsPerSlide { get; set; } = 8;
    public decimal EstimatedOutputUsdPer1KTokens { get; set; } = 0.01m;
    public string RequestedTier { get; set; } = "Smart";
    public int ProviderTimeoutSeconds { get; set; } = 180;
    public string DefaultTheme { get; set; } = "professional-navy";
    public string DefaultFontFamily { get; set; } = "Aptos";
    public string RtlFontFamily { get; set; } = "Noto Sans Arabic";
}

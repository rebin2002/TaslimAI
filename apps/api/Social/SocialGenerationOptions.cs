namespace Taslim.Api.Social;

public sealed class SocialGenerationOptions
{
    public bool Enabled { get; set; }
    public int MaxPromptCharacters { get; set; } = 6_000;
    public int MaxContextCharacters { get; set; } = 60_000;
    public int MaxOutputTokens { get; set; } = 5_000;
    public int MaxPosts { get; set; } = 12;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MaxHookCharacters { get; set; } = 240;
    public int MaxPostCharacters { get; set; } = 4_000;
    public int MaxHashtags { get; set; } = 12;
    public int MaxHashtagCharacters { get; set; } = 80;
    public int MaxAssetRefs { get; set; } = 8;
    public int MaxAssetRefCharacters { get; set; } = 160;
    public int MaxAudienceCharacters { get; set; } = 400;
    public int MaxBrandVoiceCharacters { get; set; } = 1_000;
    public int MaxCallToActionCharacters { get; set; } = 400;
    public int MaxAltTextCharacters { get; set; } = 800;
    public int MaxVisualDirectionCharacters { get; set; } = 1_000;
    public int MaxAttachmentSelections { get; set; } = 5;
    public int MaxAssetSelections { get; set; } = 8;
    public decimal? EstimatedOutputUsdPer1KTokens { get; set; }
    public string RequestedTier { get; set; } = "Smart";
    public int ProviderTimeoutSeconds { get; set; } = 180;
}

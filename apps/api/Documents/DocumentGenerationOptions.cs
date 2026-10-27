namespace Taslim.Api.Documents;

public sealed class DocumentGenerationOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxAttachments { get; set; } = 5;
    public int MaxPromptCharacters { get; set; } = 8_000;
    public int MaxContextCharacters { get; set; } = 80_000;
    public int MaxOutputTokens { get; set; } = 4_000;
    public int MaxSections { get; set; } = 24;
    public int MaxBlocks { get; set; } = 160;
    public int MaxHeadingCharacters { get; set; } = 240;
    public int MaxBlockCharacters { get; set; } = 8_000;
    public int MaxSummaryCharacters { get; set; } = 2_000;
    private decimal? estimatedOutputUsdPer1KTokens;
    public decimal? EstimatedOutputUsdPer1KTokens
    {
        get => estimatedOutputUsdPer1KTokens;
        set => estimatedOutputUsdPer1KTokens = EnsureNonNegative(value, nameof(EstimatedOutputUsdPer1KTokens));
    }
    public string RequestedTier { get; set; } = "Smart";
    public int ProviderTimeoutSeconds { get; set; } = 180;
    public string DefaultFontFamily { get; set; } = "Lato";
    public string RtlFontFamily { get; set; } = "Noto Sans Arabic";

    private static decimal? EnsureNonNegative(decimal? value, string parameterName)
    {
        if (value < 0m)
            throw new ArgumentOutOfRangeException(parameterName, value, "Document estimated output pricing cannot be negative.");
        return value;
    }
}

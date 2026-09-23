namespace Taslim.Api.Voice;

public sealed class VoiceGenerationOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string Model { get; set; } = "unconfigured";
    public int MaxTextCharacters { get; set; } = 10_000;
    public int MaxInstructionsCharacters { get; set; } = 3_000;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MaxOutputBytes { get; set; } = 10 * 1_048_576;
    public int ProviderTimeoutSeconds { get; set; } = 180;
}

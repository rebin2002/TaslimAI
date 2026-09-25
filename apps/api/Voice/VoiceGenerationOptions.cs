namespace Taslim.Api.Voice;

public sealed class VoiceGenerationOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string Model { get; set; } = "unconfigured";
    public AzureSpeechOptions AzureSpeech { get; set; } = new();
    public string ResponseFormat { get; set; } = "mp3";
    public string NeutralVoice { get; set; } = "alloy";
    public string WarmVoice { get; set; } = "coral";
    public string ProfessionalVoice { get; set; } = "onyx";
    public string StorytellingVoice { get; set; } = "fable";
    public string[] SupportedLanguages { get; set; } = ["en", "ar"];
    public decimal? PricingUsdPerMillionCharacters { get; set; }
    public string PricingVersion { get; set; } = "voice-provider-configured";
    public string Currency { get; set; } = "USD";
    public int MaxTextCharacters { get; set; } = 10_000;
    public int MaxProviderTextCharacters { get; set; } = 8_000;
    public int MaxInstructionsCharacters { get; set; } = 3_000;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MaxOutputBytes { get; set; } = 10 * 1_048_576;
    public int ProviderTimeoutSeconds { get; set; } = 180;
}

public sealed class AzureSpeechOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string ModelKey { get; set; } = "azure-speech-neural";
    public string UserAgent { get; set; } = "TaslimAI-Voice";
    public int MaxRetryAttempts { get; set; } = 2;
    public int RetryBaseDelayMilliseconds { get; set; } = 250;
}

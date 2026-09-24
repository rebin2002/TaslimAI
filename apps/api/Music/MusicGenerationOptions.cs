namespace Taslim.Api.Music;

public sealed class MusicGenerationOptions
{
    public bool Enabled { get; set; }
    public string ProviderKey { get; set; } = "unconfigured";
    public string Model { get; set; } = "unconfigured";
    public int ProviderTimeoutSeconds { get; set; } = 300;
    public int MaxPromptCharacters { get; set; } = 4_000;
    public int MaxPurposeCharacters { get; set; } = 1_000;
    public int MaxAdditionalInstructionsCharacters { get; set; } = 2_000;
    public int MaxTitleCharacters { get; set; } = 160;
    public int MinDurationSeconds { get; set; } = 15;
    public int MaxDurationSeconds { get; set; } = 600;
    public int MaxOutputBytes { get; set; } = 10 * 1_048_576;
    public string PricingVersion { get; set; } = "music-provider-unconfigured";
    public string Currency { get; set; } = "USD";
}

public sealed class MusicProviderUnavailableException() : Exception("The configured music provider is unavailable.");
public sealed class MusicProviderTimeoutException() : Exception("The music provider timed out.");
public sealed class MusicProviderFailureException() : Exception("The music provider failed.");
public sealed class MusicOutputInvalidException() : Exception("The music provider returned an invalid music file.");
public sealed class MusicRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class MusicOutputStorageException() : Exception("The generated music could not be saved.");

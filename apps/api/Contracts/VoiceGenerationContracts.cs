using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Taslim.Api.Domain;
using Taslim.Api.Voice;

namespace Taslim.Api.Contracts;

public static class VoiceGenerationValues
{
    public const string English = LanguageCodes.English;
    public const string Arabic = LanguageCodes.Arabic;
    public const string KurdishSorani = LanguageCodes.Kurdish;

    public const string Neutral = "neutral";
    public const string Warm = "warm";
    public const string Professional = "professional";
    public const string Storytelling = "storytelling";

    public const string Conversational = "conversational";
    public const string Clear = "clear";
    public const string Expressive = "expressive";
    public const string Calm = "calm";

    public static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        English, Arabic, KurdishSorani,
    };

    public static readonly IReadOnlySet<string> VoiceStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Neutral, Warm, Professional, Storytelling,
    };

    public static readonly IReadOnlySet<string> SpeakingStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Conversational, Clear, Expressive, Calm,
    };

    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "flac", "m4a", "mp3", "ogg", "wav", "webm",
    };
}

public sealed class VoiceGenerationRequest
{
    [Required]
    public Guid WorkspaceId { get; set; }

    public Guid? ProjectId { get; set; }

    [Required, StringLength(100_000)]
    public string Text { get; set; } = string.Empty;

    [Required, StringLength(5)]
    public string Language { get; set; } = VoiceGenerationValues.English;

    [Required, StringLength(40)]
    public string VoiceStyle { get; set; } = VoiceGenerationValues.Neutral;

    [Required, StringLength(40)]
    public string SpeakingStyle { get; set; } = VoiceGenerationValues.Clear;

    [StringLength(3_000)]
    public string? Instructions { get; set; }

    [StringLength(160)]
    public string? Title { get; set; }
}

public sealed record VoiceGenerationInput(
    Guid WorkspaceId,
    Guid? ProjectId,
    string Text,
    string Language,
    string VoiceStyle,
    string SpeakingStyle,
    string? Instructions,
    string? Title);

public sealed record CreateVoiceGenerationResponse(GenerationJobDto Job);

public sealed record VoiceOutputMetadata(
    string AssetType,
    string ContentType,
    string Format,
    string Language,
    string VoiceStyle,
    string SpeakingStyle,
    long SizeBytes,
    long? DurationMilliseconds,
    int? SampleRateHz);

public static class VoiceGenerationContractMapper
{
    public static VoiceGenerationInput ToInput(VoiceGenerationRequest request) => new(
        request.WorkspaceId,
        request.ProjectId,
        request.Text.Trim(),
        Normalize(request.Language, VoiceGenerationValues.English),
        Normalize(request.VoiceStyle, VoiceGenerationValues.Neutral),
        Normalize(request.SpeakingStyle, VoiceGenerationValues.Clear),
        NormalizeOptional(request.Instructions),
        NormalizeOptional(request.Title));

    public static string SerializeInput(VoiceGenerationInput input) => JsonSerializer.Serialize(input);

    public static bool TryDeserializeInput(string json, out VoiceGenerationInput? input)
    {
        try
        {
            input = JsonSerializer.Deserialize<VoiceGenerationInput>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return input is not null;
        }
        catch (JsonException)
        {
            input = null;
            return false;
        }
    }

    private static string Normalize(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class VoiceGenerationRequestValidator
{
    public static void Validate(VoiceGenerationInput input, VoiceGenerationOptions options)
    {
        if (input.WorkspaceId == Guid.Empty) Invalid("A workspace is required.");
        if (input.Text.Length < 1 || input.Text.Length > options.MaxTextCharacters) Invalid($"Voice text must be between 1 and {options.MaxTextCharacters} characters.");
        if (!VoiceGenerationValues.Languages.Contains(input.Language)) Invalid("The selected voice language is not supported.");
        if (!VoiceGenerationValues.VoiceStyles.Contains(input.VoiceStyle)) Invalid("The selected voice style is not supported.");
        if (!VoiceGenerationValues.SpeakingStyles.Contains(input.SpeakingStyle)) Invalid("The selected speaking style is not supported.");
        if (input.Instructions?.Length > options.MaxInstructionsCharacters) Invalid("Voice instructions are too long.");
        if (input.Title?.Length > options.MaxTitleCharacters) Invalid("The voice asset title is too long.");
        if (input.ProjectId == Guid.Empty) Invalid("The project selection is invalid.");
    }

    private static void Invalid(string message) => throw new VoiceRequestValidationException(GenerationJobErrorCodes.VoiceRequestInvalid, message);
}

public sealed class VoiceRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

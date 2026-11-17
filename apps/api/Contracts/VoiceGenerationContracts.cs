using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
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
       "aac", "flac", "m4a", "mp3", "ogg", "opus", "wav", "webm",
   };

    /// <summary>
    /// The media profile each advertised speech container is saved and served with, plus the provider
    /// spellings that name that same container. A saved take downloads under a file name ending in the
    /// container and is served with the media type recorded beside it, so both come from this one
    /// speech contract rather than from the provider's own header - the download name, the stored file
    /// record, the job result and the served Content-Type can then never disagree. Providers agree on
    /// the container but not on what to call it: RFC 7845 registers audio/opus for the Opus stream
    /// that lives in the Ogg container this contract calls audio/ogg, and the voice adapters already
    /// tolerate exactly those aliases (audio/mp3, audio/opus, audio/x-wav) while forwarding the raw
    /// header, which the private storage boundary then rejects after the synthesis was billed. Only a
    /// spelling of this container's own audio essence is accepted; a header naming a different
    /// container is refused by the quality gate before any output is built.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SpeechContainerProfile> SpeechContainers =
        new Dictionary<string, SpeechContainerProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["aac"] = new("audio/aac", Set("audio/aac", "audio/x-aac")),
            ["flac"] = new("audio/flac", Set("audio/flac", "audio/x-flac")),
            ["m4a"] = new("audio/mp4", Set("audio/mp4", "audio/x-m4a")),
            ["mp3"] = new("audio/mpeg", Set("audio/mpeg", "audio/mp3", "audio/x-mpeg")),
            ["ogg"] = new("audio/ogg", Set("audio/ogg", "audio/vorbis", "audio/opus")),
            ["opus"] = new("audio/ogg", Set("audio/ogg", "audio/opus")),
            ["wav"] = new("audio/wav", Set("audio/wav", "audio/x-wav", "audio/wave")),
            ["webm"] = new("audio/webm", Set("audio/webm")),
        };

    /// <summary>
    /// Resolves the single media type to persist for one speech container. The provider's header is
    /// consulted only to confirm that the response carries this container's audio essence; casing,
    /// surrounding whitespace and media-type parameters are decoration and never reach the record.
    /// </summary>
    public static bool TryResolveSpeechContentType(
        string? container,
        string? declaredContentType,
        out string persistedContentType)
    {
        persistedContentType = string.Empty;
        if (container is null || declaredContentType is null) return false;
        if (!SpeechContainers.TryGetValue(container.Trim(), out var profile)) return false;

        var declared = NormalizeDeclaredContentType(declaredContentType);
        if (!profile.ProviderContentTypes.Contains(declared)) return false;

        persistedContentType = profile.PersistedContentType;
        return true;
    }

    /// <summary>
    /// Reduces a provider's <c>Content-Type</c> header to the bare type name it is comparing against:
    /// an intermediary may pad the value with whitespace, and everything after the first semicolon is
    /// a media-type parameter, never a different type.
    /// </summary>
    public static string NormalizeDeclaredContentType(string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(declaredContentType)) return string.Empty;
        var value = declaredContentType.Trim();
        var separator = value.IndexOf(';');
        return separator >= 0 ? value[..separator].Trim() : value;
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One speech container's serving identity: the registered profile that is persisted and served, and
/// the header spellings that name that same audio essence.
/// </summary>
public sealed record SpeechContainerProfile(string PersistedContentType, IReadOnlySet<string> ProviderContentTypes);

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
    [property: JsonPropertyName("assetType")]
    string AssetType,
    [property: JsonPropertyName("contentType")]
    string ContentType,
    [property: JsonPropertyName("format")]
    string Format,
    [property: JsonPropertyName("language")]
    string Language,
    [property: JsonPropertyName("voiceStyle")]
    string VoiceStyle,
    [property: JsonPropertyName("speakingStyle")]
    string SpeakingStyle,
    [property: JsonPropertyName("sizeBytes")]
    long SizeBytes,
    [property: JsonPropertyName("durationMilliseconds")]
    long? DurationMilliseconds,
    [property: JsonPropertyName("sampleRateHz")]
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
            if (input is null
                || input.Text is null
                || input.Language is null
                || input.VoiceStyle is null
                || input.SpeakingStyle is null)
            {
                input = null;
                return false;
            }

            return true;
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

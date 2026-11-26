using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Voice;

/// <summary>
/// Fail-closed admission gate for the capability the configured voice provider declares.
/// Both shipped adapters refuse a locale outside <see cref="VoiceGenerationOptions.SupportedLanguages"/>
/// and a script above <see cref="VoiceGenerationOptions.MaxProviderTextCharacters"/> before any
/// network call, so a request that can never be synthesised is refused here instead of being
/// reserved, queued, and failed later as a durable job.
/// </summary>
public static class VoiceGenerationAdmissionPolicy
{
    public static void ValidateProviderSupport(VoiceGenerationInput input, VoiceGenerationOptions options)
    {
        if (!SupportsLanguage(input.Language, options))
        {
            throw new VoiceRequestValidationException(
                GenerationJobErrorCodes.VoiceLanguageUnsupported,
                "The selected voice language is not available from the configured voice provider.");
        }

        var providerCeiling = Math.Max(1, options.MaxProviderTextCharacters);
        if (input.Text.Length > providerCeiling)
        {
            throw new VoiceRequestValidationException(
                GenerationJobErrorCodes.VoiceProviderUnsupportedRequest,
                $"The voice script must be at most {providerCeiling} characters for the configured voice provider.");
        }
    }

    private static bool SupportsLanguage(string language, VoiceGenerationOptions options)
    {
        var supported = options.SupportedLanguages;
        return supported is { Length: > 0 }
            && supported.Contains(language, StringComparer.OrdinalIgnoreCase);
    }
}

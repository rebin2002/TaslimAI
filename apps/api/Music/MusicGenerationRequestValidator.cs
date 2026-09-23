using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Music;

public static class MusicGenerationRequestValidator
{
    public static void Validate(MusicGenerationInput request, MusicGenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length < 3 || request.Description.Length > options.MaxPromptCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Describe the music in 3 to 4,000 characters.");
        if (string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Trim().Length < 3 || request.Purpose.Length > options.MaxPurposeCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Describe the purpose in 3 to 1,000 characters.");
        if (string.IsNullOrWhiteSpace(request.Genre) || request.Genre.Length > 40 || !MusicGenerationValues.Genres.Contains(request.Genre.Trim()))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicGenreUnsupported, "The selected genre is not supported.");
        if (string.IsNullOrWhiteSpace(request.Mood) || request.Mood.Length > 80 || !MusicGenerationValues.Moods.Contains(request.Mood.Trim()))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicMoodUnsupported, "The selected mood is not supported.");
        if (!MusicGenerationValues.Durations.Contains(request.DurationSeconds) || request.DurationSeconds < options.MinDurationSeconds || request.DurationSeconds > options.MaxDurationSeconds)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicDurationUnsupported, "The selected duration is not supported.");
        ValidateChoice(request.VocalPreference, MusicGenerationValues.VocalPreferences, "vocal preference", GenerationJobErrorCodes.MusicVocalPreferenceUnsupported);
        var supportedLanguages = new HashSet<string>(LanguageCodes.Supported, StringComparer.OrdinalIgnoreCase) { MusicGenerationValues.Auto };
        ValidateChoice(request.Language, supportedLanguages, "language", GenerationJobErrorCodes.MusicLanguageUnsupported);
        if (request.ProjectId.HasValue && request.ProjectId == Guid.Empty)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The project selection is invalid.");
        if (request.Title?.Length > options.MaxTitleCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The music title is too long.");
        if (request.AdditionalInstructions?.Length > options.MaxAdditionalInstructionsCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The additional instructions are too long.");
    }

    private static void ValidateChoice(string? value, IReadOnlySet<string> supported, string field, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !supported.Contains(value.Trim()))
            throw new MusicRequestValidationException(code, $"The selected {field} is not supported.");
    }
}

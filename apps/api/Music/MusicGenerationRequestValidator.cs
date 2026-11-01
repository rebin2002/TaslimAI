using Taslim.Api.Contracts;
using Taslim.Api.Domain;

namespace Taslim.Api.Music;

public static class MusicGenerationRequestValidator
{
    private const int MaxLyricsSections = 24;
    private const int MaxLyricsLinesPerSection = 30;
    private const int MaxLyricsLineCharacters = 200;
    private const int MaxTotalLyricsCharacters = 3_500;

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
        ValidateCreationMode(request, options);
        var supportedLanguages = new HashSet<string>(LanguageCodes.Supported, StringComparer.OrdinalIgnoreCase) { MusicGenerationValues.Auto };
        ValidateChoice(request.Language, supportedLanguages, "language", GenerationJobErrorCodes.MusicLanguageUnsupported);
        if (request.ProjectId.HasValue && request.ProjectId == Guid.Empty)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The project selection is invalid.");
        if (request.Title?.Length > options.MaxTitleCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The music title is too long.");
        if (request.AdditionalInstructions?.Length > options.MaxAdditionalInstructionsCharacters)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The additional instructions are too long.");
    }

    private static void ValidateCreationMode(MusicGenerationInput request, MusicGenerationOptions options)
    {
        var mode = request.CreationMode?.Trim();
        if (string.IsNullOrWhiteSpace(mode)) mode = MusicGenerationValues.InstrumentalMode;
        if (!MusicGenerationValues.CreationModes.Contains(mode))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The selected music creation mode is not supported.");

        var capabilities = MusicGenerationCapabilityCatalog.ForProvider(options.ProviderKey);
        if (string.Equals(mode, MusicGenerationValues.InstrumentalMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!capabilities.SupportsInstrumental)
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicProviderUnavailable, "Music generation is not available for the configured provider.");
            if (!string.Equals(request.VocalPreference, MusicGenerationValues.Instrumental, StringComparison.OrdinalIgnoreCase))
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, "The configured music provider cannot guarantee a vocal song for this selection.");
            if (request.Lyrics is { Count: > 0 } || !string.IsNullOrWhiteSpace(request.LyricsLanguage) || !string.IsNullOrWhiteSpace(request.LyricsDialect) || request.LyricsRightsConfirmed)
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Lyrics are only valid for a song-with-vocals request.");
            return;
        }

        if (!string.Equals(mode, MusicGenerationValues.SongWithVocalsMode, StringComparison.OrdinalIgnoreCase))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "The selected music creation mode is not supported.");
        if (!string.Equals(request.VocalPreference, MusicGenerationValues.Vocal, StringComparison.OrdinalIgnoreCase))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, "A song-with-vocals request must explicitly select vocals.");

        ValidateLyrics(request);
        if (!capabilities.SupportsLyricsToVocals)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, "The configured music provider does not support sung lyrics.");
        var lyricsLanguage = request.LyricsLanguage!.Trim();
        if (!capabilities.SupportedLyricsLanguages.Contains(lyricsLanguage, StringComparer.OrdinalIgnoreCase))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicLanguageUnsupported, "The configured music provider does not support this lyrics language.");
        var lyricsDialect = request.LyricsDialect?.Trim();
        if (!string.IsNullOrWhiteSpace(lyricsDialect)
            && !capabilities.SupportedLyricsDialects.Contains(lyricsDialect, StringComparer.OrdinalIgnoreCase))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicLanguageUnsupported, "The configured music provider does not support this lyrics dialect.");
    }

    private static void ValidateLyrics(MusicGenerationInput request)
    {
        if (!request.LyricsRightsConfirmed)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Confirm that you hold the necessary rights to the lyrics before submitting a song request.");

        var language = request.LyricsLanguage?.Trim();
        if (string.IsNullOrWhiteSpace(language) || !MusicGenerationValues.LyricsLanguages.Contains(language))
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicLanguageUnsupported, "Choose English, Arabic, or Kurdish for the lyrics language.");

        var dialect = request.LyricsDialect?.Trim();
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(dialect))
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicLanguageUnsupported, "A dialect is only valid with its matching lyrics language.");
        }
        else if (string.IsNullOrWhiteSpace(dialect)
            || !MusicGenerationValues.LyricsDialectLanguages.TryGetValue(dialect, out var dialectLanguage)
            || !string.Equals(language, dialectLanguage, StringComparison.OrdinalIgnoreCase))
        {
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicLanguageUnsupported, "Choose a supported dialect that matches the lyrics language.");
        }

        if (request.Lyrics is null || request.Lyrics.Count == 0 || request.Lyrics.Count > MaxLyricsSections)
            throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Provide between 1 and 24 structured lyric sections.");

        var totalCharacters = 0;
        foreach (var section in request.Lyrics)
        {
            if (section is null || string.IsNullOrWhiteSpace(section.Section) || !MusicGenerationValues.LyricsSectionTypes.Contains(section.Section.Trim()))
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "A lyric section type is not supported.");
            if (section.Lines is null || section.Lines.Count == 0 || section.Lines.Count > MaxLyricsLinesPerSection)
                throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Each lyric section must contain between 1 and 30 lines.");

            foreach (var line in section.Lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLyricsLineCharacters || line.Length > MaxTotalLyricsCharacters - totalCharacters)
                    throw new MusicRequestValidationException(GenerationJobErrorCodes.MusicRequestInvalid, "Lyrics contain an empty or over-limit line.");
                totalCharacters += line.Length;
            }
        }
    }

    private static void ValidateChoice(string? value, IReadOnlySet<string> supported, string field, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || !supported.Contains(value.Trim()))
            throw new MusicRequestValidationException(code, $"The selected {field} is not supported.");
    }
}

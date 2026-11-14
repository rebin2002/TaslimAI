using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

// Issue #708, correctness fix for the RED contract pinned by draft PR #773.
//
// Bug: MusicGenerationRequestValidator measured the minimum prompt length with
// `!IsNullOrWhiteSpace(value) && value.Trim().Length >= 3`. String.Trim() only removes
// white_space-classed code points, so a song-generation draft whose description, purpose
// or lyrics contain only bidi format controls and zero-width characters (Cc/Cf such as
// U+200F RLM, U+061C ALM, U+202A LRE, U+200B ZWSP, U+200C ZWNJ) passed validation and was
// admitted to the billable cost-preflight path in MusicGenerationController.Create.
//
// These tests are provider-free and deterministic: they call the static validator
// directly, never construct an HTTP request, never register a job and cannot reach any
// provider (paid or fake). The API-level before-reservation proof stays owned by PR #773;
// #709's contract, capability catalog and song-contract tests remain untouched here.
public sealed class MusicInvisibleTextAdmissionTests
{
    // Invisible controls written as explicit escapes, mirroring PR #773's normalization-stable set.
    private const string BidiOnly = "\u200F\u061C\u202A\u202B\u202C";
    private const string ZeroWidthOnly = "\u200B\u200C\u200F\u200B\u200F\u200C";
    private const string ControlOnly = "\u0001\u0002\u0003";

    // Genuinely visible text that must keep passing admission (never reject valid Arabic/Sorani).
    private const string VisibleSorani = "ئەم گۆرانییە بۆ منداڵانی کوردستان";
    private const string VisibleSoraniPurpose = "مۆسیقای بەکگرۆوند بۆ ڤیدیۆیەک";
    private const string VocalizedArabic = "بِسْمِ اللهِ الرَّحْمَنِ الرَّحِيمِ";
    private const string SoraniWithWrappedControls = "\u200F\u200Bئەم گۆرانییە کوردییە\u202C\u200C";

    private static MusicGenerationOptions InstrumentalOptions() => new()
    {
        Enabled = true,
        ProviderKey = "mubert",
    };

    private static MusicGenerationInput Draft(string description, string purpose) => new(
        description,
        purpose,
        "folk",
        "calm",
        30,
        MusicGenerationValues.Instrumental,
        "ku",
        null,
        null,
        null,
        MusicGenerationValues.InstrumentalMode);

    private static MusicGenerationInput SongDraft(string lyricsLine) => new(
        "A warm Sorani folk arrangement with visible lyric intent.",
        VisibleSoraniPurpose,
        "folk",
        "inspiring",
        30,
        MusicGenerationValues.Vocal,
        "ku",
        null,
        null,
        null,
        MusicGenerationValues.SongWithVocalsMode,
        [new MusicLyricsSection("verse", [lyricsLine])],
        LyricsLanguage: "ku",
        LyricsRightsConfirmed: true,
        LyricsDialect: "sorani_erbil");

    [Fact]
    public void Bidi_format_control_only_description_never_passes_admission()
    {
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(Draft(BidiOnly, VisibleSoraniPurpose), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }

    [Fact]
    public void Zero_width_only_purpose_never_passes_admission()
    {
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(
                Draft("A genuinely visible description for the arrangement.", ZeroWidthOnly), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }

    [Fact]
    public void Control_character_only_description_never_passes_admission()
    {
        // Cc family (e.g. U+0001..U+0003) is invisible too and is not trimmed as whitespace.
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(Draft(ControlOnly, VisibleSoraniPurpose), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }

    [Fact]
    public void Invisible_only_lyrics_line_never_passes_admission()
    {
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(SongDraft(BidiOnly), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
        Assert.Equal("Lyrics contain an empty or over-limit line.", exception.Message);
    }

    [Theory]
    [InlineData(VisibleSorani, VisibleSoraniPurpose)]
    [InlineData(SoraniWithWrappedControls, VisibleSoraniPurpose)]
    [InlineData(VocalizedArabic, VisibleSoraniPurpose)]
    [InlineData("A warm ambient cue for a launch film", "Background bed for a product launch video.")]
    public void Visible_text_still_passes_admission(string description, string purpose)
    {
        MusicGenerationRequestValidator.Validate(Draft(description, purpose), InstrumentalOptions());
    }

    [Fact]
    public void Invisible_controls_wrapping_visible_lyrics_do_not_break_admission()
    {
        // Visible content, not invisibility, decides admission: the lyrics line carries real
        // Sorani glyphs around the controls, so the guard must not reject it. The request then
        // stops at the unrelated fail-closed provider gate, which proves the visible-text guard
        // was passed for the lyrics content.
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(
                SongDraft("\u200F" + VisibleSorani + "\u202C"), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicVocalPreferenceUnsupported, exception.Code);
    }

    [Fact]
    public void Short_visible_prompt_is_still_rejected_by_the_minimum_length()
    {
        // The pre-existing minimum stays enforced for genuinely visible but too-short text.
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(Draft("ab", VisibleSoraniPurpose), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }

    [Fact]
    public void Whitespace_padding_cannot_replace_visible_characters()
    {
        // Documents the guard's arithmetic: two visible glyphs plus invisible controls never
        // reach three visible characters, even though the raw length passes old Trim checks.
        var exception = Assert.Throws<MusicRequestValidationException>(
            () => MusicGenerationRequestValidator.Validate(Draft("ab\u200F\u200F\u200F", VisibleSoraniPurpose), InstrumentalOptions()));

        Assert.Equal(GenerationJobErrorCodes.MusicRequestInvalid, exception.Code);
    }
}

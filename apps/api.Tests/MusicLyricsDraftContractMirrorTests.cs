using System.Text.RegularExpressions;
using Taslim.Api.Contracts;
using Taslim.Api.Music;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Contract-mirror tests for the issue #708 lyrics stack (owner: the Music
/// Studio preflight integration lane). They pin the single authoritative
/// direction the architecture already requires - the server contract is the
/// source of truth and the browser mirrors it - without touching any file
/// owned by another open lane:
///   - <c>MusicGenerationContracts.cs</c>, <c>MusicGenerationRequestValidator.cs</c>
///     and <c>MusicGenerationCapabilityCatalog.cs</c> are read, never edited
///     here (they belong to draft PR #709 and its dependants #773/#813);
///   - <c>musicLyricsDraftPayload.ts</c> (PR #802, merged) is read only;
///   - <c>LyricsSectionEditor.tsx</c> (PR #772) is asserted to be the single
///     editor and to mirror this vocabulary rather than restate it; it was
///     pinned absent while that lane was unmerged, and the same two properties
///     - one editor, and a recorded studio-wiring follow-up - are still
///     enforced after its landing.
/// Nothing here enables a provider, changes a capability, or performs network
/// or provider work: these are file-and-catalog assertions only.
/// </summary>
public sealed class MusicLyricsDraftContractMirrorTests
{
    private const string ContractsPath = "apps/api/Contracts/MusicGenerationContracts.cs";
    private const string ValidatorPath = "apps/api/Music/MusicGenerationRequestValidator.cs";
    private const string PayloadHelperPath = "apps/web/src/lib/musicLyricsDraftPayload.ts";
    private const string LyricsEditorPath = "apps/web/src/components/music/LyricsSectionEditor.tsx";
    private const string StudioViewPath = "apps/web/src/components/MusicStudioView.tsx";
    private const string PreflightDocPath = "docs/MUSIC_STUDIO_PREFLIGHT_UI.md";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "apps")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test assembly.");
    }

    private static string Absolute(string relativePath) =>
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ReadSource(string relativePath) => File.ReadAllText(Absolute(relativePath));

    /// <summary>
    /// Returns one top-level declaration's own text, so a pattern can never
    /// read a neighbouring object literal (for example the studio language
    /// alias map) as though it were the vocabulary under test.
    /// </summary>
    private static string Declaration(string source, string marker, string terminator)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find {marker} in {PayloadHelperPath}.");
        var end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Expected {marker} to be closed by {terminator}.");
        return source.Substring(start, end - start + terminator.Length);
    }

    private static List<string> ArrayTokens(string declaration) =>
        Regex.Matches(declaration, @"""([a-z_]+)""").Select(match => match.Groups[1].Value).ToList();

    private static Dictionary<string, string> PairMap(string declaration) =>
        Regex.Matches(declaration, @"(?m)^\s*([a-z_]+):\s*""([a-z]{2})""")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.OrdinalIgnoreCase);

    private static int Number(string source, string pattern)
    {
        var match = Regex.Match(source, pattern);
        Assert.True(match.Success, $"Pattern {pattern} did not match its source.");
        return int.Parse(match.Groups[1].Value.Replace("_", string.Empty));
    }

    private static string Scalar(string source, string pattern)
    {
        var match = Regex.Match(source, pattern);
        Assert.True(match.Success, $"Pattern {pattern} did not match its source.");
        return match.Groups[1].Value;
    }

    [Fact]
    public void BrowserHelperMirrorsTheServerLyricsSectionVocabularyExactly()
    {
        var helper = ReadSource(PayloadHelperPath);
        var mirrored = ArrayTokens(Declaration(helper, "musicLyricsSectionTypes = Object.freeze([", "] as const"));

        Assert.Equal(MusicGenerationValues.LyricsSectionTypes.Count, mirrored.Count);
        Assert.Empty(MusicGenerationValues.LyricsSectionTypes.Except(mirrored, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(mirrored.Except(MusicGenerationValues.LyricsSectionTypes, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowserHelperMirrorsTheServerLyricsLanguagesExactly()
    {
        var helper = ReadSource(PayloadHelperPath);
        var mirrored = ArrayTokens(Declaration(helper, "musicLyricsLanguages = Object.freeze([", "] as const"));

        Assert.Equal(MusicGenerationValues.LyricsLanguages.Count, mirrored.Count);
        Assert.Empty(MusicGenerationValues.LyricsLanguages.Except(mirrored, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(mirrored.Except(MusicGenerationValues.LyricsLanguages, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowserHelperMirrorsEveryDialectTokenAndItsSingleLanguage()
    {
        var helper = ReadSource(PayloadHelperPath);
        var mirrored = PairMap(Declaration(helper, "musicLyricsDialectLanguages", "\n});"));

        Assert.Equal(MusicGenerationValues.LyricsDialectLanguages.Count, mirrored.Count);
        Assert.Empty(MusicGenerationValues.LyricsDialectLanguages.Keys.Except(mirrored.Keys, StringComparer.OrdinalIgnoreCase));
        Assert.Empty(mirrored.Keys.Except(MusicGenerationValues.LyricsDialectLanguages.Keys, StringComparer.OrdinalIgnoreCase));
        foreach (var (dialect, language) in mirrored)
        {
            Assert.Equal(MusicGenerationValues.LyricsDialectLanguages[dialect], language);
        }
    }

    [Fact]
    public void BrowserHelperMirrorsEveryNumericLimitOfTheValidator()
    {
        var helper = ReadSource(PayloadHelperPath);
        var validator = ReadSource(ValidatorPath);

        Assert.Equal(
            Number(validator, @"MaxLyricsSections = (?<value>\d+)"),
            Number(helper, @"MUSIC_LYRICS_MAX_SECTIONS = (?<value>[\d_]+)"));
        Assert.Equal(
            Number(validator, @"MaxLyricsLinesPerSection = (?<value>\d+)"),
            Number(helper, @"MUSIC_LYRICS_MAX_LINES_PER_SECTION = (?<value>[\d_]+)"));
        Assert.Equal(
            Number(validator, @"MaxLyricsLineCharacters = (?<value>\d+)"),
            Number(helper, @"MUSIC_LYRICS_MAX_LINE_CHARACTERS = (?<value>[\d_]+)"));
        Assert.Equal(
            Number(validator, @"MaxTotalLyricsCharacters = (?<value>[\d_]+)"),
            Number(helper, @"MUSIC_LYRICS_MAX_TOTAL_CHARACTERS = (?<value>[\d_]+)"));
        Assert.Equal(
            Number(validator, @"MaxReferenceAssets = (?<value>\d+)"),
            Number(helper, @"MUSIC_REFERENCES_MAX_ASSETS = (?<value>[\d_]+)"));
    }

    [Fact]
    public void BrowserHelperPinsTheSongModeFieldsTheContractExpects()
    {
        var helper = ReadSource(PayloadHelperPath);

        Assert.Equal(
            MusicGenerationValues.SongWithVocalsMode,
            Scalar(helper, @"MUSIC_LYRICS_SONG_CREATION_MODE = ""(?<value>[a-z_]+)"""));
        Assert.Equal(
            MusicGenerationValues.Vocal,
            Scalar(helper, @"MUSIC_LYRICS_SONG_VOCAL_PREFERENCE = ""(?<value>[a-z_]+)"""));
    }

    [Fact]
    public void BrowserHelperDefaultsSoraniAndRefusesSilentArabicAndEnglishDefaults()
    {
        var helper = ReadSource(PayloadHelperPath);
        var defaults = Declaration(helper, "musicLyricsDefaultDialects", "\n});");

        Assert.Equal("sorani", Scalar(defaults, @"(?m)^\s*ku: ""(?<value>[a-z_]+)"""));
        Assert.Matches(@"(?m)^\s*en: null,", defaults);
        Assert.Matches(@"(?m)^\s*ar: null,", defaults);
    }

    [Fact]
    public void ContractsStillDescribeLyricsAsModelledVocabularyNotProviderSupport()
    {
        // Guards the wording this slice repeats in the UI: the contract exists
        // so a selection round-trips, not so any provider is claimed to sing it.
        var contracts = ReadSource(ContractsPath);
        Assert.Contains("LyricsDialectLanguages", contracts, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportedByProvider", contracts, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredLyricsEditorStaysSingleSourcedAndTheWiringFollowUpStaysRecorded()
    {
        // PR #772 owns the section editor. While that lane was unmerged this
        // assertion pinned its file absent, so that landing it failed here and
        // pointed at the studio-wiring follow-up instead of at a second editor.
        // The lane has landed onto current main, so the absence check is retired
        // and the two properties it existed to protect are asserted positively:
        //   1. one editor only, and it mirrors the contract vocabulary rather
        //      than restating it (blocker B8: a 4-type / 12-section / 2,000-char
        //      model must never come back alongside the real one);
        //   2. the wiring follow-up stays recorded until MusicStudioView actually
        //      consumes the editor, so the preflight panel cannot sit unused.
        Assert.True(
            File.Exists(Absolute(LyricsEditorPath)),
            $"{LyricsEditorPath} is the structured lyrics editor owned by PR #772.");

        var editor = ReadSource(LyricsEditorPath);
        Assert.Contains("musicLyricsDraftPayload", editor, StringComparison.Ordinal);
        Assert.Contains("MUSIC_LYRICS_MAX_SECTIONS", editor, StringComparison.Ordinal);
        Assert.Contains("MUSIC_LYRICS_MAX_LINE_CHARACTERS", editor, StringComparison.Ordinal);
        Assert.Contains("MUSIC_LYRICS_MAX_TOTAL_CHARACTERS", editor, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly LyricsSectionType[] = [", editor, StringComparison.Ordinal);
        foreach (var token in MusicGenerationValues.LyricsSectionTypes)
        {
            Assert.DoesNotContain("\"" + token + "\"", editor, StringComparison.Ordinal);
        }

        // Property 1: no second declaration of the editor's section vocabulary.
        var declaredIn = Directory
            .EnumerateFiles(Absolute("apps/web/src"), "*.ts*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Absolute(LyricsEditorPath), Path.DirectorySeparatorChar == '/'
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("const lyricsSectionTypes", StringComparison.Ordinal)
                    || text.Contains("type LyricsSectionType =", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot(), path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        Assert.Empty(declaredIn);

        // Property 2: wired, or explicitly still owed. The sentence pinned here
        // is step 1 of "Follow-ups, in order" in that document.
        var wired = ReadSource(StudioViewPath).Contains("LyricsSectionEditor", StringComparison.Ordinal);
        var recorded = ReadSource(PreflightDocPath)
            .Contains("Wiring follow-up (open)", StringComparison.Ordinal);
        Assert.True(
            wired || recorded,
            $"{StudioViewPath} does not consume {LyricsEditorPath} yet, and {PreflightDocPath} no longer " +
            "records the wiring follow-up: either finish the studio wiring or restore its follow-up entry, " +
            "but do not leave the landed editor and the preflight panel unused.");
    }
}

/// <summary>
/// Fail-closed capability inventory for the lyrics-to-vocals path. It states a
/// negative on purpose: no provider key in the catalog may claim sung lyrics
/// today, so a clean browser preflight can never be read as an execution
/// promise, and any future enablement must arrive with its own evidence.
/// </summary>
public sealed class MusicVocalCapabilityFailClosedTests
{
    private static readonly string[] ProviderKeys =
    [
        "mubert", "stable-audio", "suno", "sunoapi-org", "test", "unconfigured", "", "unknown-provider",
    ];

    [Theory]
    [InlineData("mubert")]
    [InlineData("stable-audio")]
    [InlineData("suno")]
    [InlineData("sunoapi-org")]
    [InlineData("test")]
    [InlineData("unconfigured")]
    [InlineData("")]
    [InlineData("unknown-provider")]
    public void NoProviderKeyAdmitsSungLyricsOrReferenceConditioning(string providerKey)
    {
        var capabilities = MusicGenerationCapabilityCatalog.ForProvider(providerKey);

        Assert.False(capabilities.SupportsLyricsToVocals);
        Assert.Empty(capabilities.SupportedLyricsLanguages);
        Assert.Empty(capabilities.SupportedLyricsDialects);
        Assert.False(capabilities.SupportsAudioReferenceConditioning);
        Assert.False(capabilities.SupportsVideoAudioReference);
        Assert.False(capabilities.SupportsAuthorizedWorkspaceReferences);
        Assert.False(capabilities.SupportsAuthorizedCoverOrRearrangement);
        Assert.False(capabilities.SupportsRealSingerVoiceCloning);
    }

    [Fact]
    public void NoKnownProviderKeyAdmitsSungLyrics()
    {
        foreach (var providerKey in ProviderKeys)
        {
            Assert.False(MusicGenerationCapabilityCatalog.ForProvider(providerKey).SupportsLyricsToVocals, providerKey);
        }

        Assert.False(MusicGenerationCapabilityCatalog.ForProvider(null).SupportsLyricsToVocals);
    }

    [Fact]
    public void DialectVocabularyIsModelledButNeverAdvertisedAsQuality()
    {
        Assert.Contains("sorani", MusicGenerationValues.LyricsDialectLanguages.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("iraqi_baghdadi", MusicGenerationValues.LyricsDialectLanguages.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.All(
            MusicGenerationValues.LyricsDialectLanguages.Values,
            language => Assert.Contains(language, MusicGenerationValues.LyricsLanguages, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArabicDialectsAreModelledButKurdishIsTheOnlyDialectWithADefault()
    {
        Assert.Equal("ku", MusicGenerationValues.LyricsDialectLanguages["sorani"]);
        Assert.Equal("ku", MusicGenerationValues.LyricsDialectLanguages["sorani_erbil"]);
        Assert.Equal("ku", MusicGenerationValues.LyricsDialectLanguages["sorani_sulaymaniyah"]);
        Assert.Equal("ar", MusicGenerationValues.LyricsDialectLanguages["iraqi_baghdadi"]);
        Assert.Equal("ar", MusicGenerationValues.LyricsDialectLanguages["gulf_arabic"]);
        Assert.Equal("ar", MusicGenerationValues.LyricsDialectLanguages["levantine_arabic"]);

        // No Arabic dialect may be chosen silently: every Arabic token is an
        // explicit user decision, which is why the browser helper gives Arabic
        // a null default while Kurdish resolves to Sorani.
        Assert.Equal(
            3,
            MusicGenerationValues.LyricsDialectLanguages.Keys.Count(key => key.StartsWith("iraqi", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("arabic", MusicGenerationValues.LyricsDialectLanguages.Keys, StringComparer.OrdinalIgnoreCase);
    }
}

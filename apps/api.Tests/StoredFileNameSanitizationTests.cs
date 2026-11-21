using Taslim.Api.Files;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// Locks the download-name safety boundary in FileValidationService.SanitizeFileName.
/// The sanitized value becomes StoredFile.OriginalFileName, is handed to
/// AssetService.GetDownloadAsync as AssetDownload.FileName, and is echoed by the asset
/// download endpoints into the Content-Disposition header. It must therefore be identical on
/// every host and free of any character a browser, a download manager or Windows resolves as a
/// path separator, a reserved character or an extension override.
///
/// The API runs in a Linux container where Path.GetInvalidFileNameChars() reports only U+0000 and
/// '/', and where Path.GetFileName ignores a backslash. The previous implementation relied on both,
/// so a Windows path, a Windows-reserved character or a Unicode format override survived sanitisation
/// on the API host and reached the browser. This is the exact class the exporter already neutralises
/// in DocumentFileName ("deliberately independent of Path.GetInvalidFileNameChars() ... returns a
/// different character set on Windows and on Unix"); these tests pin the same guarantee for the
/// shared upload/generated-asset name boundary.
/// </summary>
public sealed class StoredFileNameSanitizationTests
{
    private const char RtlOverride = (char)0x202E;
    private const char ZeroWidthSpace = (char)0x200B;
    private const char ZeroWidthJoiner = (char)0x200D;
    private const char NonCharacter = (char)0xFFFF;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    private static readonly char[] Forbidden =
    [
        '\\', '/', ':', '"', '|', '<', '>', '*', '?',
        RtlOverride, ZeroWidthSpace, ZeroWidthJoiner, NonCharacter, LineSeparator, ParagraphSeparator,
    ];

    [Theory]
    // Windows path separators must never survive on any host.
    [InlineData("Rapor\\Basy")]
    [InlineData("C:\\Users\\victim\\report.pdf")]
    [InlineData("sub\\dir\\notes.txt")]
    // Windows-reserved characters.
    [InlineData("a:b.txt")]
    [InlineData("quote\".txt")]
    [InlineData("pipe|.txt")]
    [InlineData("less<greater>.txt")]
    [InlineData("star*.txt")]
    [InlineData("question?.txt")]
    // Bidirectional / zero-width overrides that spoof a base name or an extension on download.
    [InlineData("invoice\u202Egnp.exe")]
    [InlineData("brief\u200Bfinal.txt")]
    [InlineData("join\u200Der.txt")]
    // Line/paragraph separators and non-characters corrupt the header.
    [InlineData("line\u2028break.txt")]
    [InlineData("para\u2029graph.txt")]
    [InlineData("nonchar\uFFFF.txt")]
    public void Sanitized_name_never_contains_a_host_dependent_or_spoofing_character(string input)
    {
        var result = FileValidationService.SanitizeFileName(input);

        foreach (var forbidden in Forbidden)
        {
            AssertLacks(forbidden, result);
        }

        foreach (var character in result)
        {
            Assert.False(char.IsControl(character), "a control character leaked");
        }
    }

    [Fact]
    public void Windows_path_prefix_is_resolved_to_its_base_name()
    {
        // Path.GetFileName ignores '\' on Linux, so the whole Windows path used to survive; the
        // download name must collapse to the trailing component the way it would on Windows.
        Assert.Equal("report.pdf", FileValidationService.SanitizeFileName("C:\\Users\\victim\\Downloads\\report.pdf"));
    }

    [Fact]
    public void Extension_spoofing_override_cannot_hide_a_dangerous_suffix()
    {
        // "invoice<U+202E>gnp.exe" visually reads as "invoiceexe.png" in a download dialog; the
        // override must be removed so the real ".exe" suffix is exposed to every other check.
        Assert.Equal("invoicegnp.exe", FileValidationService.SanitizeFileName("invoice\u202Egnp.exe"));
    }

    [Theory]
    [InlineData("../../safe-name.mp3")]
    [InlineData("....txt")]
    [InlineData("a..b")]
    [InlineData("..\\..\\windows\\system32\\config")]
    [InlineData("//server//share//file.txt")]
    public void Traversal_and_path_fragments_never_survive(string input)
    {
        var result = FileValidationService.SanitizeFileName(input);
        Assert.DoesNotContain("..", result, StringComparison.Ordinal);
        AssertLacks('/', result);
        AssertLacks('\\', result);
        Assert.False(Path.IsPathRooted(result), "the download name must not be rooted");
    }

    [Fact]
    public void Canonical_upload_name_is_preserved_after_path_strip()
    {
        Assert.Equal("safe-name.mp3", FileValidationService.SanitizeFileName("../../safe-name.mp3"));
    }

    [Fact]
    public void Usable_unicode_names_are_preserved_verbatim()
    {
        Assert.Equal("Rapor\u00EA Derbar\u00EA.md", FileValidationService.SanitizeFileName("Rapor\u00EA Derbar\u00EA.md"));
        // A well-formed supplementary character (an emoji) is a real pair, not an override, and
        // must survive so legitimate names are not mangled.
        Assert.Equal("Ship \U0001F389 day.pdf", FileValidationService.SanitizeFileName("Ship \U0001F389 day.pdf"));
    }

    [Fact]
    public void Lone_surrogate_is_dropped_rather_than_corrupting_the_header()
    {
        var high = FileValidationService.SanitizeFileName("head" + (char)0xD800 + "tail.txt");
        var low = FileValidationService.SanitizeFileName("head" + (char)0xDC00 + "tail.txt");

        AssertNoLoneSurrogates(high);
        AssertNoLoneSurrogates(low);
    }

    [Fact]
    public void Truncation_on_the_length_cap_never_splits_a_surrogate_pair()
    {
        // Place the leading surrogate of an emoji pair beyond the 255 cap so a naive first-255
        // substring would leave a lone high surrogate in the header value.
        var name = new string('a', 254) + "\U0001F389" + ".txt";
        var result = FileValidationService.SanitizeFileName(name);

        Assert.True(result.Length <= 255, "download name must respect the 255-character cap");
        AssertNoLoneSurrogates(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("\\")]
    [InlineData("/")]
    [InlineData("\u202E")]
    public void Fallback_is_used_when_nothing_usable_remains(string? input)
    {
        Assert.Equal("uploaded-file", FileValidationService.SanitizeFileName(input));
    }

    [Fact]
    public void Generated_asset_ingestion_applies_the_same_download_name_boundary()
    {
        // Provider-declared names flow through GeneratedMediaSecurity.ValidateDescriptor, which
        // shares SanitizeFileName; a Windows path or override must not reach stored metadata either.
        var descriptor = GeneratedMediaSecurity.ValidateDescriptor("media\\C:\\evil\u202Egnp.mp3", "audio/mpeg");

        Assert.Equal("evilgnp.mp3", descriptor.SafeFileName);
        AssertLacks('\\', descriptor.SafeFileName);
        AssertLacks(RtlOverride, descriptor.SafeFileName);
    }

    private static void AssertNoLoneSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                Assert.True(index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]),
                    "a high surrogate must be paired");
                index++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(value[index]), "a lone low surrogate leaked");
        }
    }

    private static void AssertLacks(char forbidden, string value)
    {
        var label = "U+" + ((int)forbidden).ToString("X4");
        Assert.False(value.Contains(forbidden), $"{label} must not appear in the download name");
    }
}

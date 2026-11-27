using Taslim.Api.AcademicSources;
using Xunit;

namespace Taslim.Api.Tests.AcademicSources;

public sealed class AcademicSourceIdentityTests
{
    [Theory]
    [InlineData("10.1016/J.KR.2021.02.003", "10.1016/j.kr.2021.02.003")]
    [InlineData("https://doi.org/10.1016/j.knr.2021.02.003", "10.1016/j.knr.2021.02.003")]
    [InlineData("https://dx.doi.org/10.1007/s13347-020-00405-x", "10.1007/s13347-020-00405-x")]
    [InlineData("DOI:10.5555/12345678", "10.5555/12345678")]
    [InlineData("info:doi/10.5555/12345678", "10.5555/12345678")]
    [InlineData("10.5281/zenodo.1234567", "10.5281/zenodo.1234567")]
    [InlineData(" 10.1234/archive.4 ", "10.1234/archive.4")]
    public void Valid_dois_normalize(string input, string expected) => Assert.Equal(expected, AcademicSourceIdentity.NormalizeDoi(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("doi:not-a-doi")]
    [InlineData("10.12/toofewdigits")]          // registrant prefix needs 4-9 digits
    [InlineData("10.12345678901/toomany")]      // prefix digits beyond 9 without further group
    [InlineData("https://example.org/10.1234/x")]
    [InlineData("10.1234/")]                     // empty suffix
    [InlineData("10.1234/with space")]
    public void Invalid_dois_are_rejected(string? input) => Assert.Null(AcademicSourceIdentity.NormalizeDoi(input));

    [Fact]
    public void Path_safe_doi_keeps_slashes_and_escapes_delimiters()
    {
        Assert.Equal("10.1234/a.b/c-d_e~f", AcademicSourceIdentity.ToPathSafeDoi("10.1234/a.b/c-d_e~f"));
        Assert.Equal("10.1234/a%3fb%23c", AcademicSourceIdentity.ToPathSafeDoi("10.1234/a?b#c"));
        Assert.Null(AcademicSourceIdentity.ToPathSafeDoi("10.1234/../etc/passwd"));
        Assert.Null(AcademicSourceIdentity.ToPathSafeDoi("10.1234/ctrl\x01char"));
        // Non-ASCII DOI suffixes (real for some registrants) are UTF-8 percent-encoded.
        Assert.Equal("10.1234/%c3%bc", AcademicSourceIdentity.ToPathSafeDoi("10.1234/ü"));
    }

    [Theory]
    [InlineData("978-1-2345-6789-7", "9781234567897")]
    [InlineData("9781234567897", "9781234567897")]
    [InlineData("123456789X", "123456789X")]
    [InlineData("123456789x", "123456789X")] // the check digit normalizes to upper-case X
    public void Valid_isbns_normalize(string input, string expected) => Assert.Equal(expected, AcademicSourceIdentity.NormalizeIsbn(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("9781234567890")]   // wrong ISBN-13 check digit
    [InlineData("1234567890")]      // wrong ISBN-10 check digit
    [InlineData("97812345678")]     // 11 digits
    [InlineData("97812345678a7")]
    [InlineData("97X4123456789")]   // X only legal in ISBN-10 final position
    public void Invalid_isbns_are_rejected(string? input) => Assert.Null(AcademicSourceIdentity.NormalizeIsbn(input));

    [Fact]
    public void Isbn_10_and_13_convert_exactly_and_back()
    {
        Assert.Equal("9781234567897", AcademicSourceIdentity.ToIsbn13("123456789X"));
        Assert.Equal("123456789X", AcademicSourceIdentity.ToIsbn10("978-1-2345-6789-7"));
        // 979 group has no ISBN-10 representation.
        Assert.Null(AcademicSourceIdentity.ToIsbn10("9791090864009"));
        Assert.Equal("9791090864009", AcademicSourceIdentity.ToIsbn13("9791090864009"));
    }

    [Fact]
    public void Dedup_key_prefers_doi_then_isbn_then_provider_scope()
    {
        string Key(string? doi, string? isbn13, string? isbn10, string provider = "p", string id = "1") =>
            new AcademicSourceRecord { ProviderKey = provider, ProviderWorkId = id, Doi = doi, Isbn13 = isbn13, Isbn10 = isbn10, Title = "t", SourceType = "other", AccessLevel = AcademicAccessLevel.Metadata }.DedupKey;

        Assert.Equal("doi:10.1234/abc", Key("https://doi.org/10.1234/ABC", null, null));
        Assert.Equal("isbn:9781234567897", Key(null, "978-1-2345-6789-7", null));
        Assert.Equal("isbn:9781234567897", Key(null, null, "123456789X"));
        Assert.Equal("p:1", Key(null, null, null));
        // Identifier-less rows from different providers stay distinct.
        Assert.NotEqual(Key(null, null, null, "openalex", "W1"), Key(null, null, null, "openlibrary", "W1"));
    }
}

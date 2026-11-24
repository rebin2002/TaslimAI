using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Byte-exact contracts for <see cref="BibliographicIdentifierValidator"/>. The
/// point of these fixtures is that an identifier is only accepted when its own
/// check digit supports it, because a DOI or ISBN that merely looks right is the
/// main way an invented citation survives into a bibliography.
/// </summary>
public sealed class BibliographicIdentifierValidationTests
{
    [Theory]
    [InlineData("10.5555/kurdistan.pharma.2024.001")]
    [InlineData("https://doi.org/10.5555/kurdistan.pharma.2024.001")]
    [InlineData("http://dx.doi.org/10.5555/KURDISTAN.pharma.2024.001")]
    [InlineData("doi:10.5555/kurdistan.pharma.2024.001")]
    [InlineData("DOI: 10.5555/kurdistan.pharma.2024.001,")]
    public void Doi_forms_written_by_catalogues_all_canonicalise_together(string value)
    {
        var result = BibliographicIdentifierValidator.ValidateDoi(value);

        Assert.True(result.IsValid);
        Assert.Equal(AcademicEvidenceFixtures.TestDoi, result.Normalized);
        Assert.Equal("10.5555", result.RegistrantPrefix);
    }

    [Theory]
    [InlineData("", "DOI_MISSING")]
    [InlineData("   ", "DOI_MISSING")]
    [InlineData("not a doi", "DOI_MISSING_PREFIX")]
    [InlineData("12.3456/suffix", "DOI_MISSING_PREFIX")]
    [InlineData("10.345/suffix", "DOI_MALFORMED_REGISTRANT")]
    [InlineData("10.abcd/suffix", "DOI_MALFORMED_REGISTRANT")]
    [InlineData("10.1234567890/suffix", "DOI_MALFORMED_REGISTRANT")]
    [InlineData("10.3456/", "DOI_MALFORMED_SUFFIX")]
    [InlineData("10.3456", "DOI_MALFORMED_SUFFIX")]
    [InlineData("https://doi.org/", "DOI_MISSING")]
    public void Malformed_dois_are_rejected_with_a_reason_and_no_canonical_value(string value, string reason)
    {
        var result = BibliographicIdentifierValidator.ValidateDoi(value);

        Assert.False(result.IsValid);
        Assert.Null(result.Normalized);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void A_long_registrant_is_syntactically_valid_because_only_the_registry_knows_what_exists()
    {
        // Offline arithmetic can prove a DOI impossible; it can never prove one
        // real. ProvenanceVerifier still needs independent origins for that.
        var result = BibliographicIdentifierValidator.ValidateDoi("10.34567890/kurdistan");
        Assert.True(result.IsValid);
        Assert.Equal("10.34567890", result.RegistrantPrefix);
    }

    [Theory]
    [InlineData("978-3-16-148410-0")]
    [InlineData("9783161484100")]
    [InlineData("978 3 16 148410 0")]
    public void Isbn13_check_digit_is_accepted(string value)
    {
        var result = BibliographicIdentifierValidator.ValidateIsbn(value);

        Assert.True(result.IsValid);
        Assert.Equal("9783161484100", result.Normalized);
    }

    [Theory]
    [InlineData("978-3-16-148410-1")]
    [InlineData("977-3-16-148410-0")]
    [InlineData("978-3-16-14841O-0")]
    [InlineData("978-3-16-14841")]
    public void Isbn13_with_a_bad_digit_or_prefix_is_rejected(string value)
    {
        var result = BibliographicIdentifierValidator.ValidateIsbn(value);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void Isbn10_mod_11_sum_is_validated_including_the_trailing_x()
    {
        var ten = BibliographicIdentifierValidator.ValidateIsbn(AcademicEvidenceFixtures.TestIsbn10);
        Assert.True(ten.IsValid);
        Assert.Equal("0306406152", ten.Normalized);

        // Changing one digit breaks the mod-11 sum.
        Assert.False(BibliographicIdentifierValidator.ValidateIsbn("0-306-40615-3").IsValid);

        // 019852661X is the sum-10 case that prints as X rather than a digit.
        Assert.True(BibliographicIdentifierValidator.ValidateIsbn("0-198-52661-X").IsValid);
        Assert.False(BibliographicIdentifierValidator.ValidateIsbn("0-198-52661-9").IsValid);
    }

    [Fact]
    public void Isbn10_and_its_978_equivalent_collapse_to_one_canonical_value()
    {
        var ten = BibliographicIdentifierValidator.ValidateIsbn(AcademicEvidenceFixtures.TestIsbn10);
        var thirteen = BibliographicIdentifierValidator.ValidateIsbn("978-0-306-40615-7");

        Assert.True(ten.IsValid);
        Assert.True(thirteen.IsValid);
        Assert.Equal(
            BibliographicIdentifierValidator.ToIsbn13(ten),
            BibliographicIdentifierValidator.ToIsbn13(thirteen));
        Assert.Equal("9780306406157", BibliographicIdentifierValidator.ToIsbn13(ten));
    }

    [Theory]
    [InlineData("0000-0002-1825-0097")]
    [InlineData("https://orcid.org/0000-0002-1825-0097")]
    [InlineData("orcid:0000-0002-1825-0097")]
    public void Orcid_mod_11_2_check_digit_is_accepted(string value)
    {
        var result = BibliographicIdentifierValidator.ValidateOrcid(value);

        Assert.True(result.IsValid);
        Assert.Equal("0000-0002-1825-0097", result.Normalized);
    }

    [Theory]
    [InlineData("0000-0002-1825-0098")]
    [InlineData("0000-0002-1825-009")]
    [InlineData("0000000218250097")]
    public void Orcid_with_a_wrong_digit_or_shape_is_rejected(string value)
    {
        Assert.False(BibliographicIdentifierValidator.ValidateOrcid(value).IsValid);
    }

    [Theory]
    [InlineData("2049-3630", true)]
    [InlineData("2049-3631", false)]
    [InlineData("2049-363X", false)]
    public void Issn_check_digit_is_validated(string value, bool expected)
    {
        Assert.Equal(expected, BibliographicIdentifierValidator.ValidateIssn(value).IsValid);
    }

    [Theory]
    [InlineData("12345678", "12345678")]
    [InlineData("PMID: 12345678", "12345678")]
    [InlineData("0000123", "123")]
    public void Pubmed_shape_is_validated_without_claiming_a_check_digit(string value, string expected)
    {
        // PubMed ids carry no check digit, so a passing result means "this could be
        // an id", never "this id exists".
        var result = BibliographicIdentifierValidator.ValidatePubmed(value);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Normalized);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("123456789")]
    [InlineData("")]
    public void Pubmed_lookalikes_are_rejected(string value)
    {
        Assert.False(BibliographicIdentifierValidator.ValidatePubmed(value).IsValid);
    }

    [Theory]
    [InlineData("PMC3456789")]
    [InlineData("https://pmc.ncbi.nlm.nih.gov/articles/PMC3456789/")]
    public void Pmcid_accepts_the_id_and_the_article_url(string value)
    {
        var result = BibliographicIdentifierValidator.ValidatePmcid(value);

        Assert.True(result.IsValid);
        Assert.Equal("PMC3456789", result.Normalized);
    }

    [Theory]
    [InlineData("2403.12345", "2403.12345")]
    [InlineData("2403.12345v2", "2403.12345v2")]
    [InlineData("arXiv:2403.12345", "2403.12345")]
    [InlineData("https://arxiv.org/abs/2403.12345v1", "2403.12345v1")]
    [InlineData("hep-th/9901001", "hep-th/9901001")]
    [InlineData("math.GT/0309136v1", "math.gt/0309136v1")]
    public void Arxiv_modern_and_legacy_forms_are_both_recognised(string value, string expected)
    {
        var result = BibliographicIdentifierValidator.ValidateArxiv(value);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Normalized);
    }

    [Theory]
    [InlineData("2403.123456")]
    [InlineData("123456.1234")]
    [InlineData("hep-th/990100")]
    [InlineData("not-an-arxiv")]
    public void Arxiv_lookalikes_are_rejected(string value)
    {
        Assert.False(BibliographicIdentifierValidator.ValidateArxiv(value).IsValid);
    }

    [Fact]
    public void Url_with_embedded_credentials_is_rejected_as_a_spoofed_host()
    {
        // A reader sees "trusted.example" in the prefix while the URL resolves to
        // attacker.example, the same trap the research citation ingest policy pins.
        var result = BibliographicIdentifierValidator.ValidateUrl(
            "https://trusted.example:token@attacker.example/article");

        Assert.False(result.IsValid);
        Assert.Equal("URL_USERINFO", result.Reason);
    }

    [Theory]
    [InlineData("ftp://example.gov/a", "URL_SCHEME")]
    [InlineData("not a url", "URL_SHAPE")]
    public void Non_web_schemes_and_relative_values_are_rejected(string value, string reason)
    {
        var result = BibliographicIdentifierValidator.ValidateUrl(value);

        Assert.False(result.IsValid);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Dispatch_to_an_unsupported_kind_fails_closed()
    {
        var result = BibliographicIdentifierValidator.Validate(IdentifierKind.Unknown, "anything");

        Assert.False(result.IsValid);
        Assert.Equal("IDENTIFIER_KIND_UNSUPPORTED", result.Reason);
    }
}

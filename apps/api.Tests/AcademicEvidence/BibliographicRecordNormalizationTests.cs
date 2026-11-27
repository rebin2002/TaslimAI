using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Contracts for <see cref="BibliographicRecordNormalizer"/>: what happens when
/// providers disagree, when a catalogue date is not a year, and when a provider
/// claims more access than the platform actually holds.
/// </summary>
public sealed class BibliographicRecordNormalizationTests
{
    [Fact]
    public void Single_origin_yields_a_citable_record_but_only_one_corroborating_origin()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"),
            [AcademicEvidenceFixtures.Origin()],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal("2024", normalization.Record.PublicationYear);
        Assert.Equal(AcademicSourceKind.JournalArticle, normalization.Record.SourceKind);
        Assert.Equal("10.5555/kurdistan.pharma.2024.001",
            normalization.Record.NormalizedIdentifier(IdentifierKind.Doi));
        Assert.Equal(1, normalization.CorroboratingOriginCount);
        Assert.False(normalization.IsBlocked);
    }

    [Fact]
    public void Two_agreeing_origins_count_as_two_corroborations()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002"),
            AcademicEvidenceFixtures.CorroboratedJournalArticle(),
            AcademicEvidenceFixtures.Clock);

        Assert.Equal(2, normalization.CorroboratingOriginCount);
        Assert.Equal(
            ["crossref", "openalex"],
            normalization.AdmissibleIdentifierOrigins);
        Assert.Empty(normalization.Issues.Where(i => i.Severity == FindingSeverity.Blocking));
    }

    [Fact]
    public void Disagreement_between_providers_is_recorded_not_overwritten()
    {
        // Providers routinely differ in title casing and punctuation; that must not
        // read as a conflict. A different work does.
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003"),
            [
                AcademicEvidenceFixtures.Origin("crossref", title: "Formulary retention after centralised tendering"),
                AcademicEvidenceFixtures.Origin("openalex", title: "FORMULARY  RETENTION, AFTER CENTRALISED TENDERING"),
                AcademicEvidenceFixtures.Origin("member", title: "Pricing of insulin in Erbil pharmacies"),
            ],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal("Formulary retention after centralised tendering", normalization.Record.Title);

        var titleField = normalization.FieldProvenances.Single(f => f.FieldName == nameof(NormalizedBibliographicRecord.Title));
        Assert.Equal(["crossref", "openalex"], titleField.Origins);
        Assert.Equal(["member"], titleField.ConflictingOrigins);

        Assert.Contains(normalization.Issues, i =>
            i.Code == "TITLE_CONFLICT" && i.Severity == FindingSeverity.Blocking);
    }

    [Theory]
    [InlineData("2024", "2024")]
    [InlineData("© 2024", "2024")]
    [InlineData("c2024", "2024")]
    [InlineData("2024, cop.", "2024")]
    public void Catalogue_year_forms_that_state_one_year_are_read(string token, string expected)
    {
        Assert.Equal(expected, PublicationDateParser.ParseYear(token, AcademicEvidenceFixtures.Clock));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("n.d.")]
    [InlineData("c1978-1982")]
    [InlineData("between 1990 and 1995")]
    [InlineData("[19--?]")]
    [InlineData("1978 / 1982")]
    [InlineData("02024")]
    public void Dates_that_do_not_state_a_single_year_yield_no_year(string? token)
    {
        // Choosing a midpoint, or grabbing the first four digits, would put a date
        // in a student's citation that no source ever printed.
        Assert.Null(PublicationDateParser.ParseYear(token, AcademicEvidenceFixtures.Clock));
    }

    [Fact]
    public void Implausible_years_are_rejected_against_the_clock()
    {
        Assert.Null(PublicationDateParser.ParseYear("2029", AcademicEvidenceFixtures.Clock));
        Assert.Equal("2027", PublicationDateParser.ParseYear("2027", AcademicEvidenceFixtures.Clock));
        Assert.Null(PublicationDateParser.ParseYear("1204", AcademicEvidenceFixtures.Clock));
    }

    [Fact]
    public void Copyright_year_is_kept_as_a_separate_basis_from_publication_year()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000004"),
            [AcademicEvidenceFixtures.Origin(
                "openlibrary",
                sourceKindToken: "book",
                doi: null,
                isbn: AcademicEvidenceFixtures.TestIsbn13,
                yearToken: "© 2019",
                yearBasis: PublicationYearBasis.CopyrightYear,
                editionLabel: "3rd edition",
                publisher: "University of Sulaymaniyah Press",
                venue: null)],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal("2019", normalization.Record.PublicationYear);
        Assert.Equal(PublicationYearBasis.CopyrightYear, normalization.Record.YearBasis);
        Assert.Equal(3, normalization.Record.EditionNumber);
        Assert.Equal(AcademicSourceKind.Book, normalization.Record.SourceKind);
        Assert.Equal("9783161484100", normalization.Record.NormalizedIdentifier(IdentifierKind.Isbn));
    }

    [Fact]
    public void Open_access_claim_without_a_retrieved_copy_stays_metadata_only()
    {
        // A provider flagging is_oa is not the platform holding the text, and the
        // difference decides whether a quotation can be attributed to the work.
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000005"),
            [AcademicEvidenceFixtures.Origin(fullText: null, abstractText: null)],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal(SourceAccessLevel.MetadataOnly, normalization.Record.AccessLevel);
        Assert.Equal(ContentAcquisition.CatalogMetadata, normalization.Record.ContentAcquisition);
        Assert.Null(normalization.Record.FullText);
    }

    [Fact]
    public void Held_abstract_and_held_full_text_are_three_different_levels()
    {
        var abstractOnly = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000006"),
            [AcademicEvidenceFixtures.Origin(abstractText: AcademicEvidenceFixtures.AbstractText)],
            AcademicEvidenceFixtures.Clock);

        var fullText = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000007"),
            [AcademicEvidenceFixtures.Origin(
                abstractText: AcademicEvidenceFixtures.AbstractText,
                fullText: AcademicEvidenceFixtures.FullTextBody)],
            AcademicEvidenceFixtures.Clock);

        var upload = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000008"),
            [AcademicEvidenceFixtures.Origin(
                "member", fullText: AcademicEvidenceFixtures.FullTextBody, isMemberUpload: true)],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal(SourceAccessLevel.Abstract, abstractOnly.Record.AccessLevel);
        Assert.Equal(SourceAccessLevel.FullText, fullText.Record.AccessLevel);
        Assert.Equal(SourceAccessLevel.PrivateUpload, upload.Record.AccessLevel);
        Assert.Equal(ContentAcquisition.MemberUpload, upload.Record.ContentAcquisition);
    }

    [Fact]
    public void Page_range_without_an_observable_origin_is_dropped_not_stored()
    {
        // The classic fabrication: a model returns "pp. 412-419" because the record
        // looked like it should have pages.
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000009"),
            [AcademicEvidenceFixtures.Origin(pageRangeOrigin: LocatorOrigin.ModelInference)],
            AcademicEvidenceFixtures.Clock);

        Assert.Null(normalization.Record.PageRange);
        Assert.Equal(LocatorOrigin.Unknown, normalization.Record.PageRangeOrigin);
        Assert.Contains(normalization.Issues, i => i.Code == "PAGERANGE_ORIGIN_REJECTED");
    }

    [Fact]
    public void Registry_supplied_page_range_is_kept_with_its_origin()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a"),
            [AcademicEvidenceFixtures.Origin(pageRangeOrigin: LocatorOrigin.RegistryMetadata)],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal("412-419", normalization.Record.PageRange);
        Assert.Equal(LocatorOrigin.RegistryMetadata, normalization.Record.PageRangeOrigin);
    }

    [Fact]
    public void Malformed_identifier_is_retained_as_text_and_never_as_an_identity()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000b"),
            [AcademicEvidenceFixtures.Origin(doi: "10.1234/", pageRange: null)],
            AcademicEvidenceFixtures.Clock);

        var identifier = Assert.Single(normalization.Record.Identifiers);
        Assert.False(identifier.IsWellFormed);
        Assert.Null(identifier.NormalizedValue);
        Assert.Equal("DOI_MALFORMED_SUFFIX", identifier.MalformedReason);
        Assert.Null(normalization.Record.NormalizedIdentifier(IdentifierKind.Doi));
        Assert.Contains(normalization.Issues, i => i.Code == "IDENTIFIER_DOI_INVALID");
    }

    [Fact]
    public void Two_different_dois_on_one_record_block_it()
    {
        var origins = new List<RawBibliographicMetadata>
        {
            AcademicEvidenceFixtures.Origin("crossref", doi: AcademicEvidenceFixtures.TestDoi),
            AcademicEvidenceFixtures.Origin("openalex", doi: AcademicEvidenceFixtures.OtherTestDoi),
        };

        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000c"),
            origins,
            AcademicEvidenceFixtures.Clock);

        Assert.Contains(normalization.Issues, i => i.Code == "DOI_CONFLICT" && i.Severity == FindingSeverity.Blocking);
        Assert.Equal(2, normalization.Record.Identifiers.Count(i => i.IsWellFormed));
    }

    [Fact]
    public void Unmapped_provider_type_is_reported_instead_of_guessed()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000d"),
            [AcademicEvidenceFixtures.Origin(sourceKindToken: "digital-object-mystery")],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal(AcademicSourceKind.Unknown, normalization.Record.SourceKind);
        Assert.Contains(normalization.Issues, i => i.Code == "SOURCE_KIND_UNMAPPED");
    }

    [Fact]
    public void Author_names_from_different_catalogues_collapse_to_one_identity()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000e"),
            [
                AcademicEvidenceFixtures.Origin(),
                AcademicEvidenceFixtures.Origin("openalex", authors:
                [
                    new RawAuthorToken { Name = "Rebin Khoshnaw", Orcid = AcademicEvidenceFixtures.TestOrcid },
                    new RawAuthorToken { Name = "Ahmed, Salar" },
                ]),
            ],
            AcademicEvidenceFixtures.Clock);

        Assert.Equal(2, normalization.Record.Authors.Count);
        Assert.Equal(AcademicEvidenceFixtures.TestOrcid, normalization.Record.Authors[0].Orcid);
        // An ORCID outranks the name: two catalogues spelling the same person
        // differently still key together, and never apart.
        Assert.Equal("orcid:" + AcademicEvidenceFixtures.TestOrcid, normalization.Record.Authors[0].MatchKey);
        Assert.Equal("khoshnaw|r", AuthorNameNormalizer.BuildMatchKey("Khoshnaw", "Rebin", null));
        Assert.NotEqual("khoshnaw|r", AuthorNameNormalizer.BuildMatchKey("Khoshnaw", "Alan", null));
    }

    [Fact]
    public void Surname_only_matches_are_not_merged_into_one_person()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000f"),
            [AcademicEvidenceFixtures.Origin(authors:
            [
                new RawAuthorToken { Name = "Ahmed, S." },
                new RawAuthorToken { Name = "Ahmed" },
            ])],
            AcademicEvidenceFixtures.Clock);

        // "Ahmed, S." and a bare "Ahmed" are not provably the same person.
        Assert.Equal(2, normalization.Record.Authors.Count);
    }

    [Fact]
    public void Invalid_orcid_is_dropped_rather_than_becoming_a_shared_key()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000010"),
            [AcademicEvidenceFixtures.Origin(authors:
            [
                new RawAuthorToken { Name = "Khoshnaw, R.", Orcid = "0000-0002-1825-0098" },
            ])],
            AcademicEvidenceFixtures.Clock);

        var author = Assert.Single(normalization.Record.Authors);
        Assert.Null(author.Orcid);
        Assert.Equal("khoshnaw|r", author.MatchKey);
        Assert.Contains(normalization.Issues, i => i.Code == "ORCID_REJECTED");
    }

    [Fact]
    public void No_title_means_no_citable_record()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("aaaaaaaa-0000-4000-8000-000000000011"),
            [AcademicEvidenceFixtures.Origin(title: null, pageRange: null)],
            AcademicEvidenceFixtures.Clock);

        Assert.True(normalization.IsBlocked);
        Assert.False(normalization.Record.IsCitable);
        Assert.Contains(normalization.Issues, i => i.Code == "TITLE_MISSING");
    }

    [Theory]
    [InlineData("Journal Article", AcademicSourceKind.JournalArticle)]
    [InlineData("journal-article", AcademicSourceKind.JournalArticle)]
    [InlineData("book-chapter", AcademicSourceKind.BookChapter)]
    [InlineData("dissertation", AcademicSourceKind.Thesis)]
    [InlineData("posted-content", AcademicSourceKind.Preprint)]
    [InlineData("Proceedings-Article;", AcademicSourceKind.ConferencePaper)]
    [InlineData("unknown-type-xyz", null)]
    public void Provider_type_vocabularies_map_without_inventing_a_kind(string token, AcademicSourceKind? expected)
    {
        Assert.Equal(expected, SourceKindMap.Map(token));
    }

    [Fact]
    public void Fold_identity_ignores_case_and_punctuation_only()
    {
        Assert.Equal(
            BibliographicRecordNormalizer.FoldIdentity("Kurdistan's Oil Economy, 1991-2003"),
            BibliographicRecordNormalizer.FoldIdentity("kurdistan's oil economy 1991-2003"));

        // A transliterated title and the Sorani original must not fold together.
        Assert.NotEqual(
            BibliographicRecordNormalizer.FoldIdentity("تەڤایۆتی دەرمان"),
            BibliographicRecordNormalizer.FoldIdentity("Medicine Distribution"));
    }
}

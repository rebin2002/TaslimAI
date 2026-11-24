using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Synthetic inputs shared by the academic evidence suites.
///
/// Every value here is invented for the test. The DOIs use the <c>10.5555</c>
/// reservation that the DOI system sets aside for examples, the ISBNs are the
/// check-digit examples from the standard, and the titles describe a fictional
/// market-access study rather than any real publication. Nothing in this lane may
/// be quoted as if it were a source, so the fixtures never point at one either.
/// </summary>
internal static class AcademicEvidenceFixtures
{
    internal static readonly Guid Workspace = new("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid OtherWorkspace = new("22222222-2222-4222-8222-222222222222");
    internal static readonly Guid Project = new("33333333-3333-4333-8333-333333333333");

    /// <summary>Fixed clock so every assertion is reproducible.</summary>
    internal static readonly DateTimeOffset Clock = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Reserved example DOI, not a real article.</summary>
    internal const string TestDoi = "10.5555/kurdistan.pharma.2024.001";

    /// <summary>Second reserved DOI for a different work.</summary>
    internal const string OtherTestDoi = "10.5555/kurdistan.pharma.2024.002";

    /// <summary>Valid ISBN-13 example from the standard.</summary>
    internal const string TestIsbn13 = "978-3-16-148410-0";

    /// <summary>Valid ISBN-10 example from the standard.</summary>
    internal const string TestIsbn10 = "0-306-40615-2";

    /// <summary>Valid ORCID example from the standard.</summary>
    internal const string TestOrcid = "0000-0002-1825-0097";

    internal const string AbstractText =
        "Retention of essential medicines on formulary improved by 18 percent between 2021 and 2024 "
        + "after the regional tender process was centralised.";

    internal const string FullTextBody =
        "Chapter 1 Introduction. The Kurdistan Regional Government revised its procurement rules in 2021. "
        + "Retention of essential medicines on formulary improved by 18 percent between 2021 and 2024. "
        + "Chapter 2 Methods. Interviews were conducted with thirty two pharmacists in Erbil and Sulaymaniyah. "
        + "The mean price deviation across observed tenders was 7.4 percent.";

    /// <summary>
    /// Build one provider payload. Defaults describe a journal article whose DOI
    /// came straight from a registry response.
    /// </summary>
    internal static RawBibliographicMetadata Origin(
        string originKey = "crossref",
        DateTimeOffset? observedAtUtc = null,
        string? title = "Formulary retention after centralised tendering in the Kurdistan Region",
        string? yearToken = "2024",
        string? sourceKindToken = "journal-article",
        string? venue = "Journal of Regional Pharmaceutical Policy",
        string? publisher = null,
        string? doi = TestDoi,
        string? isbn = null,
        IdentifierOrigin identifierOrigin = IdentifierOrigin.RegistryResponse,
        string? abstractText = null,
        string? fullText = null,
        LocatorOrigin pageRangeOrigin = LocatorOrigin.RegistryMetadata,
        string? pageRange = "412-419",
        string? editionLabel = null,
        IReadOnlyList<RawAuthorToken>? authors = null,
        PublicationYearBasis yearBasis = PublicationYearBasis.PrintedPublicationYear,
        PeerReviewStatus? peerReviewAsserted = null,
        bool isMemberUpload = false,
        string? corpusId = null,
        string? language = "en",
        string? studyDesign = null)
    {
        var identifiers = new List<RawIdentifier>();
        if (doi is not null)
        {
            identifiers.Add(new RawIdentifier
            {
                Kind = IdentifierKind.Doi,
                Value = doi,
                OriginKey = originKey,
                Origin = identifierOrigin,
                ObservedAtUtc = observedAtUtc ?? Clock,
            });
        }

        if (isbn is not null)
        {
            identifiers.Add(new RawIdentifier
            {
                Kind = IdentifierKind.Isbn,
                Value = isbn,
                OriginKey = originKey,
                Origin = identifierOrigin,
                ObservedAtUtc = observedAtUtc ?? Clock,
            });
        }

        return new RawBibliographicMetadata
        {
            OriginKey = originKey,
            ObservedAtUtc = observedAtUtc ?? Clock,
            Title = title,
            YearToken = yearToken,
            YearBasis = yearBasis,
            SourceKindToken = sourceKindToken,
            Venue = venue,
            Publisher = publisher,
            EditionLabel = editionLabel,
            PageRange = pageRange,
            PageRangeOrigin = pageRange is null ? LocatorOrigin.Unknown : pageRangeOrigin,
            AbstractText = abstractText,
            FullText = fullText,
            Authors = authors ??
            [
                new RawAuthorToken { Name = "Khoshnaw, R.", Orcid = TestOrcid },
                new RawAuthorToken { Name = "Ahmed, S." },
            ],
            Identifiers = identifiers,
            PeerReviewAsserted = peerReviewAsserted,
            IsMemberUpload = isMemberUpload,
            CorpusId = corpusId,
            Language = language,
            StudyDesign = studyDesign,
        };
    }

    /// <summary>Two independent registry origins agreeing on one article.</summary>
    internal static IReadOnlyList<RawBibliographicMetadata> CorroboratedJournalArticle() =>
    [
        Origin("crossref", Clock.AddMinutes(-30)),
        Origin(
            "openalex",
            Clock.AddMinutes(-20),
            sourceKindToken: "article",
            doi: TestDoi,
            pageRange: null,
            corpusId: "W5000000001"),
    ];

    /// <summary>
    /// A record that satisfies every verification condition: DOI from a registry,
    /// two independent origins, a title, a year and known content.
    /// </summary>
    internal static (NormalizedBibliographicRecord Record, NormalizationResult Normalization) VerifyReady(
        Guid? sourceId = null,
        string? abstractText = AbstractText,
        string? fullText = null)
    {
        var id = sourceId ?? Guid.Parse("44444444-4444-4444-8444-444444444444");
        var origins = CorroboratedJournalArticle()
            .Select(o => o with { AbstractText = abstractText ?? o.AbstractText, FullText = fullText ?? o.FullText })
            .ToList();

        var normalization = BibliographicRecordNormalizer.Normalize(Workspace, id, origins, Clock);
        return (normalization.Record, normalization);
    }

    /// <summary>Map a normalized record to a provenance verdict for appraisal tests.</summary>
    internal static SourceProvenance ProvenanceOf(
        (NormalizedBibliographicRecord Record, NormalizationResult Normalization) pair,
        IReadOnlyList<RetractionNotice>? notices = null)
        => ProvenanceVerifier.Verify(pair.Record, pair.Normalization, notices, Clock);
}

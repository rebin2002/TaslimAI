using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>
/// One identifier exactly as an origin asserted it, before validation.
/// </summary>
public sealed record RawIdentifier
{
    public required IdentifierKind Kind { get; init; }
    public required string Value { get; init; }

    /// <summary>Origin that produced this identifier, for provenance counting.</summary>
    public required string OriginKey { get; init; }

    /// <summary>How the origin obtained it; caps the verdict the verifier may reach.</summary>
    public IdentifierOrigin Origin { get; init; } = IdentifierOrigin.ProviderAssertion;

    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>One author or editor string exactly as an origin asserted it.</summary>
public sealed record RawAuthorToken
{
    public required string Name { get; init; }
    public string? Orcid { get; init; }
    public string? Affiliation { get; init; }
    public AuthorRole Role { get; init; } = AuthorRole.Author;
}

/// <summary>
/// Provider-agnostic metadata handed to the normalizer. Deliberately all
/// strings and opt-in flags: a provider that returns <c>is_oa: true</c> has not
/// supplied the full text, and the normalizer derives the access level from what
/// content is actually present, never from that claim.
/// </summary>
public sealed record RawBibliographicMetadata
{
    /// <summary>Identity of the origin, for example <c>openalex</c> or <c>member</c>.</summary>
    public required string OriginKey { get; init; }

    /// <summary>When this origin's payload was read, UTC.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Corpus-native id, for example an OpenAlex work id.</summary>
    public string? CorpusId { get; init; }

    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? OriginalScriptTitle { get; init; }

    /// <summary>Provider type token, mapped to <see cref="AcademicSourceKind"/>.</summary>
    public string? SourceKindToken { get; init; }

    public string? Venue { get; init; }
    public string? Publisher { get; init; }
    public string? EditionLabel { get; init; }
    public string? Volume { get; init; }
    public string? Issue { get; init; }

    /// <summary>Page range as printed, with the origin that printed it.</summary>
    public string? PageRange { get; init; }

    public LocatorOrigin PageRangeOrigin { get; init; } = LocatorOrigin.Unknown;

    public string? LandingPageUrl { get; init; }
    public string? Language { get; init; }
    public string? Discipline { get; init; }
    public string? StudyDesign { get; init; }

    /// <summary>Abstract text actually retrieved.</summary>
    public string? AbstractText { get; init; }

    /// <summary>Full text actually retrieved and stored.</summary>
    public string? FullText { get; init; }

    /// <summary>Year token, which may be a catalogue string such as "© 2019".</summary>
    public string? YearToken { get; init; }

    /// <summary>Full date token, for example "2019-04-19" or a partial "[2019?]".</summary>
    public string? DateToken { get; init; }

    public PublicationYearBasis YearBasis { get; init; } = PublicationYearBasis.Unknown;

    public IReadOnlyList<RawAuthorToken> Authors { get; init; } = [];
    public IReadOnlyList<RawIdentifier> Identifiers { get; init; } = [];

    /// <summary>
    /// Peer-review status asserted by the provider. Kept separate from the
    /// verified status because an assertion is not a verification.
    /// </summary>
    public PeerReviewStatus? PeerReviewAsserted { get; init; }

    /// <summary>True when the content came from the member's own upload.</summary>
    public bool IsMemberUpload { get; init; }
}

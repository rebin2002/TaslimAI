namespace Taslim.Api.AcademicEvidence.Provenance;

/// <summary>
/// Stable codes explaining a provenance verdict. Surfaced verbatim in the source
/// library and in exports, so a member can see exactly why a citation is marked
/// verified or not, and a test can assert on the reason rather than on prose.
/// </summary>
public static class ProvenanceReasonCodes
{
    /// <summary>No origin supplied a title, so nothing is citable.</summary>
    public const string TitleMissing = "PROV_TITLE_MISSING";

    /// <summary>No DOI or ISBN survived validation.</summary>
    public const string NoPersistentIdentifier = "PROV_NO_PERSISTENT_IDENTIFIER";

    /// <summary>
    /// A DOI or ISBN is well formed but was asserted by an origin that cannot
    /// certify it, such as a model answer or a citing reference list.
    /// </summary>
    public const string IdentifierOriginWeak = "PROV_IDENTIFIER_ORIGIN_WEAK";

    /// <summary>Corroboration comes from one origin only.</summary>
    public const string SingleOrigin = "PROV_SINGLE_ORIGIN";

    /// <summary>Two or more independent origins agree on the core identity.</summary>
    public const string Corroborated = "PROV_CORROBORATED";

    /// <summary>Two origins disagree on a core identity field.</summary>
    public const string CoreFieldConflict = "PROV_CORE_FIELD_CONFLICT";

    /// <summary>More than one distinct DOI is attached to the record.</summary>
    public const string IdentifierConflict = "PROV_IDENTIFIER_CONFLICT";

    /// <summary>Record is metadata-only: no content was read.</summary>
    public const string MetadataOnly = "PROV_METADATA_ONLY";

    /// <summary>Abstract held, full text not held.</summary>
    public const string AbstractOnly = "PROV_ABSTRACT_ONLY";

    /// <summary>Full text held, from an open access or licensed copy.</summary>
    public const string FullTextHeld = "PROV_FULL_TEXT_HELD";

    /// <summary>Full text held came from the member's own upload.</summary>
    public const string PrivateUploadHeld = "PROV_PRIVATE_UPLOAD_HELD";

    /// <summary>An admissible retraction or correction notice is attached.</summary>
    public const string RetractionNotice = "PROV_RETRACTION_NOTICE";

    /// <summary>A retraction was asserted without an admissible origin, so it is a lead only.</summary>
    public const string RetractionUnconfirmed = "PROV_RETRACTION_UNCONFIRMED";

    /// <summary>Publication year is absent, so the citation renders without one.</summary>
    public const string YearMissing = "PROV_YEAR_MISSING";

    /// <summary>
    /// A record marked verified lost its corroboration on rescreen, so the
    /// platform downgraded it rather than keeping the badge.
    /// </summary>
    public const string DowngradedFromVerified = "PROV_DOWNGRADED_FROM_VERIFIED";
}

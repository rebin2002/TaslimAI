using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Normalization;

/// <summary>A defect found while normalizing, addressed at one field.</summary>
/// <param name="Code">Stable machine code such as <c>YEAR_RANGE_NOT_SINGLE</c>.</param>
/// <param name="FieldName">Field the issue concerns.</param>
/// <param name="Severity">How it affects citability.</param>
/// <param name="Detail">Member-facing explanation.</param>
public sealed record NormalizationIssue(
    string Code,
    string FieldName,
    FindingSeverity Severity,
    string Detail);

/// <summary>
/// Output of normalization: the canonical record plus the field-level evidence
/// that produced it, so the provenance layer can decide a verdict without
/// re-reading the providers.
/// </summary>
public sealed class NormalizationResult
{
    public required NormalizedBibliographicRecord Record { get; init; }
    public IReadOnlyList<FieldProvenance> FieldProvenances { get; init; } = [];

    /// <summary>Distinct origins whose title and year agree with the chosen record.</summary>
    public int CorroboratingOriginCount { get; init; }

    /// <summary>Distinct origins that asserted the record's identifier set.</summary>
    public IReadOnlyList<string> IdentifierOrigins { get; init; } = [];

    /// <summary>
    /// Distinct origins that asserted a well-formed persistent identifier from a
    /// location that can actually certify it, namely a registry response or the
    /// work's own landing page. An identifier a model produced, or one copied out
    /// of a citing paper's reference list, is a lead rather than corroboration, so
    /// it is excluded here no matter how many times it repeats.
    /// </summary>
    public IReadOnlyList<string> AdmissibleIdentifierOrigins { get; init; } = [];

    public IReadOnlyList<NormalizationIssue> Issues { get; init; } = [];

    /// <summary>True when a blocking issue means the record cannot be cited.</summary>
    public bool IsBlocked => Issues.Any(i => i.Severity == FindingSeverity.Blocking);

    /// <summary>Stable reason codes for the provenance layer.</summary>
    public IReadOnlyList<string> ReasonCodes => [.. Issues.Select(i => i.Code).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Merges one or more provider payloads into a single
/// <see cref="NormalizedBibliographicRecord"/>.
///
/// The rules exist to stop three specific failure modes:
/// <list type="bullet">
/// <item><description>Nothing is invented. An absent year stays absent, an
/// unparseable "c2019-2021" stays unresolved, and a page range no artifact
/// printed is dropped rather than filled in.</description></item>
/// <item><description>Nothing is averaged. A field takes the first
/// origin-supplied value in deterministic origin order; disagreeing origins are
/// recorded as conflicts instead of being silently overwritten.</description></item>
/// <item><description>Metadata is not full text. The access level is derived from
/// the content that is actually present, never from a provider's
/// <c>is_oa</c>-style flag.</description></item>
/// </list>
/// </summary>
public static partial class BibliographicRecordNormalizer
{
    /// <summary>Fields where two origins disagreeing means the identity is disputed.</summary>
    public static readonly IReadOnlySet<string> CoreIdentityFields =
        new HashSet<string>(StringComparer.Ordinal) { nameof(NormalizedBibliographicRecord.Title), "Year", "Doi", "Isbn" };

    private const int OldestPlausibleYear = 1450;

    /// <summary>
    /// Normalize payloads into one record.
    /// </summary>
    /// <param name="workspaceId">Tenant scope.</param>
    /// <param name="sourceId">Stable identity of the source.</param>
    /// <param name="origins">Provider payloads, in any order.</param>
    /// <param name="observedAtUtc">Clock used for plausibility bounds.</param>
    public static NormalizationResult Normalize(
        Guid workspaceId,
        Guid sourceId,
        IReadOnlyList<RawBibliographicMetadata> origins,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var issues = new List<NormalizationIssue>();

        if (origins.Count == 0)
        {
            issues.Add(new NormalizationIssue("NO_ORIGIN", "*", FindingSeverity.Blocking,
                "No metadata source was supplied for this record."));
            return new NormalizationResult
            {
                Record = new NormalizedBibliographicRecord { SourceId = sourceId, WorkspaceId = workspaceId },
                Issues = issues,
            };
        }

        // Deterministic origin order: oldest observation first, then origin key,
        // so the same payloads always produce the same primary values.
        var ordered = origins
            .OrderBy(o => o.ObservedAtUtc)
            .ThenBy(o => o.OriginKey, StringComparer.Ordinal)
            .ToList();

        var fields = new List<FieldProvenance>();

        var title = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Title),
            o => o.Title, issues, foldForAgreement: true);

        var subtitle = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Subtitle),
            o => o.Subtitle, issues, foldForAgreement: true);

        var venue = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Venue),
            o => o.Venue, issues, foldForAgreement: true);

        var publisher = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Publisher),
            o => o.Publisher, issues, foldForAgreement: true);

        var editionLabel = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.EditionLabel),
            o => o.EditionLabel, issues, foldForAgreement: true);

        var volume = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Volume),
            o => o.Volume, issues, foldForAgreement: false);

        var issueNumber = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Issue),
            o => o.Issue, issues, foldForAgreement: false);

        var originalScriptTitle = MergeTextField(fields, ordered,
            nameof(NormalizedBibliographicRecord.OriginalScriptTitle), o => o.OriginalScriptTitle,
            issues, foldForAgreement: true);

        var language = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Language),
            o => o.Language, issues, foldForAgreement: false);

        var discipline = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.Discipline),
            o => o.Discipline, issues, foldForAgreement: false);

        var studyDesign = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.StudyDesign),
            o => o.StudyDesign, issues, foldForAgreement: false);

        var landingPage = MergeTextField(fields, ordered, nameof(NormalizedBibliographicRecord.LandingPageUrl),
            o => o.LandingPageUrl, issues, foldForAgreement: false);

        if (string.IsNullOrWhiteSpace(title))
        {
            issues.Add(new NormalizationIssue("TITLE_MISSING", nameof(NormalizedBibliographicRecord.Title),
                FindingSeverity.Blocking, "No source supplied a title, so the work cannot be cited."));
        }

        var (year, yearBasis, dateUtc) = ResolveYear(ordered, observedAtUtc, fields, issues);

        var sourceKind = ResolveSourceKind(ordered, fields, issues);

        var (authors, editors) = ResolvePeople(ordered, issues);

        var (identifiers, identifierOrigins, admissibleIdentifierOrigins) =
            ResolveIdentifiers(ordered, fields, observedAtUtc, issues);

        var (pageRange, pageRangeOrigin) = ResolvePageRange(ordered, fields, issues);

        var (accessLevel, acquisition, abstractText, fullText) = ResolveAccess(ordered, issues);

        var corroborating = CountCorroboratingOrigins(ordered, title, year);

        var record = new NormalizedBibliographicRecord
        {
            SourceId = sourceId,
            WorkspaceId = workspaceId,
            Title = title,
            Subtitle = subtitle,
            OriginalScriptTitle = originalScriptTitle,
            SourceKind = sourceKind,
            Venue = venue,
            PublicationYear = year,
            YearBasis = yearBasis,
            PublicationDateUtc = dateUtc,
            Publisher = publisher,
            EditionLabel = editionLabel,
            EditionNumber = ParseEditionNumber(editionLabel),
            Volume = volume,
            Issue = issueNumber,
            PageRange = pageRange,
            PageRangeOrigin = pageRangeOrigin,
            Authors = authors,
            Editors = editors,
            Identifiers = identifiers,
            AccessLevel = accessLevel,
            ContentAcquisition = acquisition,
            LandingPageUrl = landingPage,
            Language = language,
            Discipline = discipline,
            StudyDesign = studyDesign,
            AbstractText = abstractText,
            FullText = fullText,
            CorpusId = ordered.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.CorpusId))?.CorpusId,
        };

        return new NormalizationResult
        {
            Record = record,
            FieldProvenances = fields,
            CorroboratingOriginCount = corroborating,
            IdentifierOrigins = identifierOrigins,
            AdmissibleIdentifierOrigins = admissibleIdentifierOrigins,
            Issues = issues,
        };
    }

    /// <summary>
    /// Case-fold, punctuation-strip and whitespace-collapse a title for equality
    /// only. The stored value stays exactly as the origin printed it.
    /// </summary>
    public static string FoldIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                continue;
            }

            if (char.IsPunctuation(character) || char.IsSymbol(character)) continue;
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static string? MergeTextField(
        List<FieldProvenance> fields,
        List<RawBibliographicMetadata> ordered,
        string fieldName,
        Func<RawBibliographicMetadata, string?> selector,
        List<NormalizationIssue> issues,
        bool foldForAgreement)
    {
        var values = new List<(string OriginKey, string Value)>();
        foreach (var origin in ordered)
        {
            var raw = selector(origin);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            values.Add((origin.OriginKey, raw.Trim()));
        }

        if (values.Count == 0) return null;

        var primary = values[0];
        var agreeing = new List<string>();
        var conflicting = new List<string>();
        foreach (var (originKey, value) in values)
        {
            var matches = foldForAgreement
                ? string.Equals(FoldIdentity(value), FoldIdentity(primary.Value), StringComparison.Ordinal)
                : string.Equals(value, primary.Value, StringComparison.Ordinal);
            if (matches)
            {
                if (!agreeing.Contains(originKey, StringComparer.Ordinal)) agreeing.Add(originKey);
            }
            else
            {
                if (!conflicting.Contains(originKey, StringComparer.Ordinal)) conflicting.Add(originKey);
            }
        }

        if (conflicting.Count > 0)
        {
            issues.Add(new NormalizationIssue(
                fieldName.ToUpperInvariant() + "_CONFLICT", fieldName,
                CoreIdentityFields.Contains(fieldName) ? FindingSeverity.Blocking : FindingSeverity.Warning,
                $"Metadata origins disagree on {fieldName}: kept {primary.OriginKey}, conflicts from {string.Join(", ", conflicting)}."));
        }

        fields.Add(new FieldProvenance
        {
            FieldName = fieldName,
            Value = primary.Value,
            Origins = agreeing,
            ConflictingOrigins = conflicting,
            ObservedAtUtc = ordered[0].ObservedAtUtc,
        });

        return primary.Value;
    }

    private static (string? Year, PublicationYearBasis Basis, DateTimeOffset? DateUtc) ResolveYear(
        List<RawBibliographicMetadata> ordered,
        DateTimeOffset observedAtUtc,
        List<FieldProvenance> fields,
        List<NormalizationIssue> issues)
    {
        string? chosenYear = null;
        var basis = PublicationYearBasis.Unknown;
        DateTimeOffset? dateUtc = null;
        var agreeing = new List<string>();
        var conflicting = new List<string>();

        foreach (var origin in ordered)
        {
            var parsed = PublicationDateParser.ParseYear(origin.YearToken, observedAtUtc);
            var date = PublicationDateParser.ParseDate(origin.DateToken, observedAtUtc);

            if (parsed is null && origin.DateToken is not null)
            {
                // A readable date still yields a year, but only a complete one.
                parsed = date?.Year.ToString(CultureInfo.InvariantCulture);
            }

            if (parsed is null) continue;

            if (chosenYear is null)
            {
                chosenYear = parsed;
                basis = origin.YearBasis == PublicationYearBasis.Unknown
                    ? PublicationYearBasis.PrintedPublicationYear
                    : origin.YearBasis;
                dateUtc = date;
                agreeing.Add(origin.OriginKey);
                continue;
            }

            if (string.Equals(parsed, chosenYear, StringComparison.Ordinal))
            {
                if (!agreeing.Contains(origin.OriginKey, StringComparer.Ordinal)) agreeing.Add(origin.OriginKey);
                dateUtc ??= date;
            }
            else if (Math.Abs(int.Parse(parsed, CultureInfo.InvariantCulture)
                               - int.Parse(chosenYear, CultureInfo.InvariantCulture)) == 1
                     && basis != PublicationYearBasis.CopyrightYear)
            {
                // A one-year gap is the classic preprint-versus-version-of-record
                // difference: recorded as a note, not as a disputed identity.
                issues.Add(new NormalizationIssue("YEAR_ADJACENT_VARIANTS", "Year", FindingSeverity.Note,
                    $"Origins report {chosenYear} and {parsed}; the earliest is kept as the publication year."));
                if (int.Parse(parsed, CultureInfo.InvariantCulture) < int.Parse(chosenYear, CultureInfo.InvariantCulture))
                {
                    chosenYear = parsed;
                    dateUtc = date ?? dateUtc;
                }
            }
            else
            {
                conflicting.Add(origin.OriginKey);
            }
        }

        if (conflicting.Count > 0)
        {
            issues.Add(new NormalizationIssue("YEAR_CONFLICT", "Year", FindingSeverity.Blocking,
                $"Metadata origins disagree on the publication year, so it cannot be asserted."));
        }

        if (chosenYear is null)
        {
            issues.Add(new NormalizationIssue("YEAR_UNAVAILABLE", "Year", FindingSeverity.Warning,
                "No source supplied a usable publication year; the citation renders without one."));
        }

        fields.Add(new FieldProvenance
        {
            FieldName = "Year",
            Value = chosenYear,
            Origins = agreeing,
            ConflictingOrigins = conflicting,
            ObservedAtUtc = ordered[0].ObservedAtUtc,
        });

        return (chosenYear, chosenYear is null ? PublicationYearBasis.Unknown : basis, dateUtc);
    }

    private static AcademicSourceKind ResolveSourceKind(
        List<RawBibliographicMetadata> ordered,
        List<FieldProvenance> fields,
        List<NormalizationIssue> issues)
    {
        AcademicSourceKind? kind = null;
        var agreeing = new List<string>();
        var conflicting = new List<string>();

        foreach (var origin in ordered)
        {
            if (string.IsNullOrWhiteSpace(origin.SourceKindToken)) continue;
            var mapped = SourceKindMap.Map(origin.SourceKindToken);
            if (mapped is null)
            {
                issues.Add(new NormalizationIssue("SOURCE_KIND_UNMAPPED", nameof(NormalizedBibliographicRecord.SourceKind),
                    FindingSeverity.Note, $"Unrecognised source type token \"{origin.SourceKindToken.Trim()}\" was not guessed."));
                continue;
            }

            if (kind is null)
            {
                kind = mapped;
                agreeing.Add(origin.OriginKey);
            }
            else if (kind != mapped)
            {
                conflicting.Add(origin.OriginKey);
            }
            else if (!agreeing.Contains(origin.OriginKey, StringComparer.Ordinal))
            {
                agreeing.Add(origin.OriginKey);
            }
        }

        fields.Add(new FieldProvenance
        {
            FieldName = "SourceKind",
            Value = kind?.ToString(),
            Origins = agreeing,
            ConflictingOrigins = conflicting,
        });

        return kind ?? AcademicSourceKind.Unknown;
    }

    private static (IReadOnlyList<BibliographicAuthor> Authors, IReadOnlyList<BibliographicAuthor> Editors) ResolvePeople(
        List<RawBibliographicMetadata> ordered,
        List<NormalizationIssue> issues)
    {
        var authors = new List<BibliographicAuthor>();
        var editors = new List<BibliographicAuthor>();
        var seenAuthor = new HashSet<string>(StringComparer.Ordinal);
        var seenEditor = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        foreach (var origin in ordered)
        {
            foreach (var token in origin.Authors)
            {
                var parsed = AuthorNameNormalizer.Parse(token.Name, token.Role, token.Orcid, token.Affiliation, warnings);
                if (parsed is null)
                {
                    warnings.Add("AUTHOR_UNPARSEABLE:" + token.Name);
                    continue;
                }

                var bucket = parsed.Role == AuthorRole.Editor ? editors : authors;
                var seen = parsed.Role == AuthorRole.Editor ? seenEditor : seenAuthor;

                // ORCID is a true identity; a bare surname is not, so two people
                // with one surname must not collapse into one author.
                var dedupeKey = parsed.Orcid is not null
                    ? "o:" + parsed.Orcid
                    : "n:" + parsed.MatchKey + ":" + (parsed.GivenNames is null ? 0 : 1);
                if (!seen.Add(dedupeKey)) continue;
                bucket.Add(parsed);
            }
        }

        if (authors.Count == 0 && editors.Count == 0)
        {
            issues.Add(new NormalizationIssue("AUTHORS_UNAVAILABLE", nameof(NormalizedBibliographicRecord.Authors),
                FindingSeverity.Warning, "No author or editor was supplied; the citation will fall back to the title."));
        }

        foreach (var warning in warnings.Distinct(StringComparer.Ordinal))
        {
            issues.Add(new NormalizationIssue(warning.Split(':', 2)[0], nameof(NormalizedBibliographicRecord.Authors),
                FindingSeverity.Note, warning));
        }

        return (authors, editors);
    }

    private static (
        IReadOnlyList<BibliographicIdentifier> Identifiers,
        IReadOnlyList<string> Origins,
        IReadOnlyList<string> AdmissibleOrigins) ResolveIdentifiers(
        List<RawBibliographicMetadata> ordered,
        List<FieldProvenance> fields,
        DateTimeOffset observedAtUtc,
        List<NormalizationIssue> issues)
    {
        var result = new List<BibliographicIdentifier>();
        var byNormalized = new Dictionary<string, BibliographicIdentifier>(StringComparer.Ordinal);
        var origins = new List<string>();
        var admissibleOrigins = new List<string>();
        var conflicts = new List<string>();

        foreach (var origin in ordered)
        {
            foreach (var raw in origin.Identifiers)
            {
                var validation = BibliographicIdentifierValidator.Validate(raw.Kind, raw.Value);
                var identifier = new BibliographicIdentifier
                {
                    Kind = raw.Kind,
                    RawValue = raw.Value.Trim(),
                    NormalizedValue = validation.IsValid ? validation.Normalized : null,
                    IsWellFormed = validation.IsValid,
                    MalformedReason = validation.Reason,
                    Origin = raw.Origin,
                    RegistrantPrefix = validation.RegistrantPrefix,
                    ObservedAtUtc = raw.ObservedAtUtc == default ? origin.ObservedAtUtc : raw.ObservedAtUtc,
                };

                if (!validation.IsValid)
                {
                    issues.Add(new NormalizationIssue("IDENTIFIER_" + raw.Kind.ToString().ToUpperInvariant() + "_INVALID",
                        raw.Kind.ToString(), FindingSeverity.Warning,
                        $"A {raw.Kind} value failed validation ({validation.Reason}) and is kept as unverified text only."));
                    result.Add(identifier);
                    continue;
                }

                if (raw.Origin is IdentifierOrigin.Unknown or IdentifierOrigin.ProviderAssertion)
                {
                    issues.Add(new NormalizationIssue("IDENTIFIER_ORIGIN_UNRECORDED", raw.Kind.ToString(),
                        FindingSeverity.Warning,
                        $"A {raw.Kind} was supplied without a verifiable origin, so it cannot certify provenance."));
                }

                // Only a registry response or the work's own landing page can
                // certify an identifier; a member, a citing paper or a model can
                // only repeat it.
                if (validation.IsValid
                    && raw.Origin is IdentifierOrigin.RegistryResponse or IdentifierOrigin.WorkLandingPage
                    && raw.Kind is IdentifierKind.Doi or IdentifierKind.Isbn
                    && !admissibleOrigins.Contains(origin.OriginKey, StringComparer.Ordinal))
                {
                    admissibleOrigins.Add(origin.OriginKey);
                }

                var key = raw.Kind + ":" + validation.Normalized;
                if (byNormalized.TryGetValue(key, out var existing))
                {
                    // Same identifier from a second origin is corroboration, not a
                    // new identifier.
                    if (!origins.Contains(origin.OriginKey, StringComparer.Ordinal)) origins.Add(origin.OriginKey);
                    _ = existing;
                    continue;
                }

                result.Add(identifier);
                byNormalized[key] = identifier;
                if (!origins.Contains(origin.OriginKey, StringComparer.Ordinal)) origins.Add(origin.OriginKey);
            }
        }

        // Two different DOIs on one record is a real identity conflict, not an
        // extra alias: keeping both would let a citation resolve to two papers.
        var dois = result.Where(i => i.Kind == IdentifierKind.Doi && i.IsWellFormed)
            .Select(i => i.NormalizedValue!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (dois.Count > 1)
        {
            conflicts.AddRange(dois.Skip(1).Select(_ => "doi-conflict"));
            issues.Add(new NormalizationIssue("DOI_CONFLICT", "Doi", FindingSeverity.Blocking,
                "More than one distinct DOI was asserted for this record; resolve before citing."));
        }

        var isbns = result.Where(i => i.Kind == IdentifierKind.Isbn && i.IsWellFormed)
            .Select(i => BibliographicIdentifierValidator.ToIsbn13(
                BibliographicIdentifierValidator.ValidateIsbn(i.NormalizedValue))!)
            .Where(v => v is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (isbns.Count > 1)
        {
            issues.Add(new NormalizationIssue("ISBN_CONFLICT", "Isbn", FindingSeverity.Warning,
                "More than one ISBN was asserted, which usually means different editions of one title."));
        }

        fields.Add(IdentifierField("Doi", IdentifierKind.Doi, dois.FirstOrDefault(), ordered, conflicts, observedAtUtc));
        fields.Add(IdentifierField("Isbn", IdentifierKind.Isbn, isbns.FirstOrDefault(), ordered, [], observedAtUtc));

        return (result, origins, admissibleOrigins);
    }

    /// <summary>
    /// Field evidence for one identifier kind, listing only the origins that
    /// actually asserted that kind rather than sharing one list across all of them.
    /// </summary>
    private static FieldProvenance IdentifierField(
        string fieldName,
        IdentifierKind kind,
        string? value,
        List<RawBibliographicMetadata> ordered,
        IReadOnlyList<string> conflicting,
        DateTimeOffset observedAtUtc)
    {
        var asserting = new List<string>();
        IdentifierOrigin origin = IdentifierOrigin.Unknown;
        foreach (var entry in ordered)
        {
            foreach (var identifier in entry.Identifiers)
            {
                if (identifier.Kind != kind) continue;
                if (!BibliographicIdentifierValidator.Validate(kind, identifier.Value).IsValid) continue;
                if (!asserting.Contains(entry.OriginKey, StringComparer.Ordinal)) asserting.Add(entry.OriginKey);
                if (origin == IdentifierOrigin.Unknown) origin = identifier.Origin;
            }
        }

        return new FieldProvenance
        {
            FieldName = fieldName,
            Value = value,
            Origins = asserting,
            ConflictingOrigins = conflicting,
            IdentifierOrigin = origin,
            ObservedAtUtc = observedAtUtc,
        };
    }

    private static (string? PageRange, LocatorOrigin Origin) ResolvePageRange(
        List<RawBibliographicMetadata> ordered,
        List<FieldProvenance> fields,
        List<NormalizationIssue> issues)
    {
        foreach (var origin in ordered)
        {
            if (string.IsNullOrWhiteSpace(origin.PageRange)) continue;

            // An unrecorded or model-supplied origin is the fabricated-page-number
            // path, so the value is dropped instead of being stored as metadata.
            if (origin.PageRangeOrigin is LocatorOrigin.Unknown or LocatorOrigin.ModelInference)
            {
                issues.Add(new NormalizationIssue("PAGERANGE_ORIGIN_REJECTED", nameof(NormalizedBibliographicRecord.PageRange),
                    FindingSeverity.Warning,
                    "A page range without an observable origin was discarded rather than stored."));
                continue;
            }

            var value = origin.PageRange.Trim();
            fields.Add(new FieldProvenance
            {
                FieldName = nameof(NormalizedBibliographicRecord.PageRange),
                Value = value,
                Origins = [origin.OriginKey],
            });

            return (value, origin.PageRangeOrigin);
        }

        return (null, LocatorOrigin.Unknown);
    }

    private static (SourceAccessLevel, ContentAcquisition, string?, string?) ResolveAccess(
        List<RawBibliographicMetadata> ordered,
        List<NormalizationIssue> issues)
    {
        var abstractText = ordered.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.AbstractText))?.AbstractText;
        var fullTextOrigin = ordered.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.FullText));

        if (fullTextOrigin is not null)
        {
            return (fullTextOrigin.IsMemberUpload ? SourceAccessLevel.PrivateUpload : SourceAccessLevel.FullText,
                fullTextOrigin.IsMemberUpload ? ContentAcquisition.MemberUpload : ContentAcquisition.OpenAccessCopy,
                abstractText, fullTextOrigin.FullText);
        }

        if (!string.IsNullOrWhiteSpace(abstractText))
        {
            return (SourceAccessLevel.Abstract, ContentAcquisition.ProviderAbstract, abstractText, null);
        }

        if (ordered.Any(o => o is { IsMemberUpload: true }))
        {
            issues.Add(new NormalizationIssue("UPLOAD_CONTENT_EMPTY", nameof(NormalizedBibliographicRecord.AccessLevel),
                FindingSeverity.Warning, "An upload was declared with no stored text, so it is treated as metadata only."));
        }

        return (SourceAccessLevel.MetadataOnly, ContentAcquisition.CatalogMetadata, null, null);
    }

    private static int CountCorroboratingOrigins(
        List<RawBibliographicMetadata> ordered,
        string? title,
        string? year)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0;

        var folded = FoldIdentity(title);
        var count = 0;
        foreach (var origin in ordered)
        {
            if (string.IsNullOrWhiteSpace(origin.Title)
                || !string.Equals(FoldIdentity(origin.Title), folded, StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = PublicationDateParser.ParseYear(origin.YearToken, default);
            if (year is not null && parsed is not null && parsed != year) continue;
            count++;
        }

        return count;
    }

    private static int? ParseEditionNumber(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        var match = EditionsRegex().Match(label);
        if (!match.Success) return null;

        if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric;
        }

        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "first" or "one" => 1,
            "second" or "two" => 2,
            "third" or "three" => 3,
            "fourth" => 4,
            "fifth" => 5,
            "sixth" => 6,
            "seventh" => 7,
            "eighth" => 8,
            "ninth" => 9,
            "tenth" => 10,
            _ => null,
        };
    }

    [GeneratedRegex(@"(\d{1,2})(?:st|nd|rd|th)|\b(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionsRegex();
}

using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;

namespace Taslim.Api.AcademicEvidence.Provenance;

/// <summary>
/// Decides whether a normalized record may be presented as a verified citation.
///
/// The rule is deliberately conservative, because the failure mode this lane must
/// avoid is a member trusting a citation that was never checked. A verdict of
/// <see cref="ProvenanceVerdict.Verified"/> requires all of:
/// <list type="number">
/// <item><description>a well-formed DOI or ISBN, including its check digit;</description></item>
/// <item><description>that identifier asserted from a location that can certify it,
/// namely a registry response or the work's own landing page;</description></item>
/// <item><description>at least
/// <see cref="AcademicEvidenceLimits.MinimumCorroboratingOrigins"/> distinct origins
/// agreeing on the identifier and the title;</description></item>
/// <item><description>no unresolved conflict on a core identity field.</description></item>
/// </list>
/// Anything short of that is <see cref="ProvenanceVerdict.Unverified"/>, and the
/// reason codes say which condition failed.
/// </summary>
public static class ProvenanceVerifier
{
    /// <summary>
    /// Verify one record against its normalization output.
    /// </summary>
    /// <param name="record">Normalized record, already validated field by field.</param>
    /// <param name="normalization">Origin evidence produced during normalization.</param>
    /// <param name="notices">Publication-integrity notices known for the work.</param>
    /// <param name="verifiedAtUtc">Timestamp recorded on the provenance row.</param>
    public static SourceProvenance Verify(
        NormalizedBibliographicRecord record,
        NormalizationResult normalization,
        IReadOnlyList<RetractionNotice>? notices = null,
        DateTimeOffset? verifiedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(normalization);

        var observedAt = verifiedAtUtc ?? normalization.FieldProvenances
            .Select(f => f.ObservedAtUtc)
            .DefaultIfEmpty(DateTimeOffset.UnixEpoch)
            .Max();

        var reasons = new List<string>();
        var fields = normalization.FieldProvenances;

        var retractionList = notices ?? [];
        var admissible = retractionList.Where(IsAdmissible).ToList();
        var unconfirmedLeads = retractionList.Where(n => !IsAdmissible(n)).ToList();

        if (unconfirmedLeads.Count > 0) reasons.Add(ProvenanceReasonCodes.RetractionUnconfirmed);

        if (string.IsNullOrWhiteSpace(record.Title))
        {
            reasons.Add(ProvenanceReasonCodes.TitleMissing);
            return Build(record, ProvenanceVerdict.Invalid, reasons, fields, 0, retractionList, observedAt);
        }

        var wellFormedPersistent = record.Identifiers
            .Where(i => i.IsWellFormed && i.Kind is IdentifierKind.Doi or IdentifierKind.Isbn)
            .ToList();

        if (wellFormedPersistent.Count == 0) reasons.Add(ProvenanceReasonCodes.NoPersistentIdentifier);

        if (wellFormedPersistent.Count > 0 && normalization.AdmissibleIdentifierOrigins.Count == 0)
        {
            reasons.Add(ProvenanceReasonCodes.IdentifierOriginWeak);
        }

        var coreConflicts = fields
            .Where(f => BibliographicRecordNormalizer.CoreIdentityFields.Contains(f.FieldName)
                        && f.ConflictingOrigins.Count > 0)
            .ToList();

        var distinctDois = wellFormedPersistent
            .Where(i => i.Kind == IdentifierKind.Doi)
            .Select(i => i.NormalizedValue)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var identifierConflict = distinctDois > 1;

        if (identifierConflict) reasons.Add(ProvenanceReasonCodes.IdentifierConflict);
        if (coreConflicts.Count > 0) reasons.Add(ProvenanceReasonCodes.CoreFieldConflict);

        // An unresolved conflict always outranks corroboration: two sources that
        // disagree about which paper this is cannot both certify it.
        if (identifierConflict || coreConflicts.Any(f => f.FieldName != "Year"))
        {
            return Build(record, ProvenanceVerdict.Disputed, reasons, fields,
                normalization.AdmissibleIdentifierOrigins.Count, retractionList, observedAt);
        }

        var fullRetracted = admissible.Any(n => n.Kind is RetractionKind.FullRetraction or RetractionKind.PartialRetraction);
        if (fullRetracted)
        {
            reasons.Add(ProvenanceReasonCodes.RetractionNotice);
            return Build(record, ProvenanceVerdict.Withdrawn, reasons, fields,
                normalization.AdmissibleIdentifierOrigins.Count, retractionList, observedAt);
        }

        var corroborated = normalization.AdmissibleIdentifierOrigins.Count >= AcademicEvidenceLimits.MinimumCorroboratingOrigins
            && normalization.CorroboratingOriginCount >= AcademicEvidenceLimits.MinimumCorroboratingOrigins
            && wellFormedPersistent.Count > 0
            && record.AccessLevel != SourceAccessLevel.Unknown;

        if (corroborated)
        {
            reasons.Add(ProvenanceReasonCodes.Corroborated);
            if (admissible.Any()) reasons.Add(ProvenanceReasonCodes.RetractionNotice);
            return Build(record, ProvenanceVerdict.Verified, reasons, fields,
                normalization.AdmissibleIdentifierOrigins.Count, retractionList, observedAt);
        }

        if (record.PublicationYear is null) reasons.Add(ProvenanceReasonCodes.YearMissing);
        reasons.Add(record.AccessLevel switch
        {
            SourceAccessLevel.MetadataOnly => ProvenanceReasonCodes.MetadataOnly,
            SourceAccessLevel.Abstract => ProvenanceReasonCodes.AbstractOnly,
            SourceAccessLevel.FullText => ProvenanceReasonCodes.FullTextHeld,
            SourceAccessLevel.PrivateUpload => ProvenanceReasonCodes.PrivateUploadHeld,
            _ => ProvenanceReasonCodes.NoPersistentIdentifier,
        });

        if (wellFormedPersistent.Count > 0 && normalization.AdmissibleIdentifierOrigins.Count > 0)
        {
            reasons.Add(ProvenanceReasonCodes.SingleOrigin);
        }

        return Build(record, ProvenanceVerdict.Unverified, reasons, fields,
            normalization.AdmissibleIdentifierOrigins.Count, retractionList, observedAt);
    }

    /// <summary>
    /// Decide again after new notices or a second provider hit arrive.
    ///
    /// A downgrade is always allowed, an upgrade is only allowed when the stored
    /// evidence supports it, and a record that a model asserted a retraction for is
    /// never silently removed: it stays where it was with a lead recorded.
    /// </summary>
    public static SourceProvenance Reverify(
        NormalizedBibliographicRecord record,
        NormalizationResult normalization,
        SourceProvenance previous,
        IReadOnlyList<RetractionNotice> notices,
        DateTimeOffset atUtc)
    {
        var next = Verify(record, normalization, notices, atUtc);

        if (previous.Verdict == ProvenanceVerdict.Verified && next.Verdict != ProvenanceVerdict.Verified)
        {
            var reasons = next.ReasonCodes.ToList();
            reasons.Add(ProvenanceReasonCodes.DowngradedFromVerified);
            return next with { Verdict = next.Verdict, ReasonCodes = reasons };
        }

        return next;
    }

    /// <summary>
    /// True when a notice can move a record to withdrawn: only one read from a
    /// registry, a retrieved copy, or supplied by the member with the notice text.
    /// </summary>
    private static bool IsAdmissible(RetractionNotice notice) =>
        notice.Origin is RetractionNoticeOrigin.Registry
            or RetractionNoticeOrigin.RetrievedCopy
            or RetractionNoticeOrigin.MemberProvided;

    private static SourceProvenance Build(
        NormalizedBibliographicRecord record,
        ProvenanceVerdict verdict,
        List<string> reasons,
        IReadOnlyList<FieldProvenance> fields,
        int corroboratingOrigins,
        IReadOnlyList<RetractionNotice> notices,
        DateTimeOffset observedAt) =>
        new()
        {
            SourceId = record.SourceId,
            WorkspaceId = record.WorkspaceId,
            Verdict = verdict,
            ReasonCodes = [.. reasons.Distinct(StringComparer.Ordinal)],
            Fields = fields,
            CorroboratingOriginCount = corroboratingOrigins,
            RetractionNotices = notices,
        };
}

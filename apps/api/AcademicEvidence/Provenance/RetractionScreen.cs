using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Provenance;

/// <summary>Outcome of screening a source against publication-integrity notices.</summary>
public enum RetractionScreenOutcome
{
    /// <summary>No admissible notice exists; the record stands as verified.</summary>
    Clear = 0,

    /// <summary>
    /// A notice exists but does not invalidate the work: an expression of concern
    /// or a correction. The source stays citable and must be labelled.
    /// </summary>
    Flagged,

    /// <summary>
    /// A retraction, partial retraction or unresolved correction notice exists;
    /// findings from the work must not be carried into the synthesis.
    /// </summary>
    Withdrawn,

    /// <summary>
    /// A notice was asserted by an origin that cannot certify it, so the platform
    /// records a lead to check rather than acting on it.
    /// </summary>
    UnconfirmedLead,
}

/// <summary>Result of the retraction screen for one source.</summary>
public sealed record RetractionScreenResult(
    SourceProvenance Provenance,
    RetractionScreenOutcome Outcome,
    IReadOnlyList<EvidenceFlag> Flags);

/// <summary>
/// Applies publication-integrity notices to a provenance verdict.
///
/// Two opposite failures are guarded here. A retracted study must never keep a
/// clean badge, so any admissible retraction downgrades the verdict. But a model
/// that invents a retraction must not silently delete a real source from a
/// student's bibliography either, so an inadmissible notice is recorded as a lead
/// to check and changes nothing about the verdict.
/// </summary>
public static class RetractionScreen
{
    /// <summary>
    /// Screen one source. The input provenance is never mutated; a new value is
    /// returned so the audit trail can show the before and after states.
    /// </summary>
    public static RetractionScreenResult Screen(SourceProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        var admissible = provenance.AdmissibleNotices;
        var leads = provenance.UnconfirmedNoticeLeads;
        var flags = new List<EvidenceFlag>();

        if (admissible.Count == 0)
        {
            if (leads.Count == 0)
            {
                return new RetractionScreenResult(provenance, RetractionScreenOutcome.Clear, []);
            }

            foreach (var lead in leads)
            {
                flags.Add(new EvidenceFlag
                {
                    Code = ProvenanceReasonCodes.RetractionUnconfirmed,
                    Severity = FindingSeverity.Warning,
                    Message = "A publication-integrity notice was asserted for this work by a source that "
                        + "cannot confirm it. Check the publisher record before acting on this.",
                });
            }

            // The lead is added to the record so it survives to the next screen,
            // but the verdict is untouched.
            var withLeads = provenance with
            {
                ReasonCodes = AppendReason(provenance.ReasonCodes, ProvenanceReasonCodes.RetractionUnconfirmed),
            };
            return new RetractionScreenResult(withLeads, RetractionScreenOutcome.UnconfirmedLead, flags);
        }

        var strongest = admissible
            .OrderByDescending(n => n.Kind)
            .First();

        var provenanceWithNotice = provenance with
        {
            ReasonCodes = AppendReason(provenance.ReasonCodes, ProvenanceReasonCodes.RetractionNotice),
        };

        if (strongest.Kind is RetractionKind.ExpressionOfConcern or RetractionKind.Correction)
        {
            flags.Add(new EvidenceFlag
            {
                Code = strongest.Kind == RetractionKind.Correction ? "RETRACTION_CORRECTION" : "RETRACTION_CONCERN",
                Severity = FindingSeverity.Warning,
                Message = strongest.Kind == RetractionKind.Correction
                    ? "This work has a published correction. Cite the corrected version and check whether the "
                        + "passage relied on was changed."
                    : "This work carries an expression of concern. It remains citable but must be labelled as "
                        + "contested wherever it is used.",
            });

            return new RetractionScreenResult(provenanceWithNotice, RetractionScreenOutcome.Flagged, flags);
        }

        flags.Add(new EvidenceFlag
        {
            Code = strongest.Kind == RetractionKind.FullRetraction ? "RETRACTION_FULL" : "RETRACTION_PARTIAL",
            Severity = FindingSeverity.Blocking,
            Message = strongest.Kind == RetractionKind.FullRetraction
                ? "This work has been retracted. It is kept in the library for the audit trail only and its "
                    + "findings must not be used to support a claim."
                : "Part of this work has been retracted. Only findings the retraction notice does not cover may "
                    + "be used, and each one must name the notice.",
        });

        if (provenance.Verdict == ProvenanceVerdict.Verified)
        {
            flags.Add(new EvidenceFlag
            {
                Code = "VERIFIED_WITH_NOTICE",
                Severity = FindingSeverity.Warning,
                Message = "The bibliographic identity is verified, but the work itself carries a retraction notice. "
                    + "Verified means the citation is real, not that the findings stand.",
            });
        }

        var withdrawn = provenanceWithNotice with
        {
            Verdict = ProvenanceVerdict.Withdrawn,
            CorroboratingOriginCount = provenance.CorroboratingOriginCount,
        };

        return new RetractionScreenResult(withdrawn, RettractionOutcome(strongest.Kind), flags);
    }

    /// <summary>
    /// True when evidence from this source may support a claim in the synthesis.
    /// A withdrawn source fails regardless of how strong its identity is.
    /// </summary>
    public static bool MaySupportClaims(SourceProvenance provenance) =>
        provenance.Verdict is not (ProvenanceVerdict.Withdrawn or ProvenanceVerdict.Invalid or ProvenanceVerdict.Disputed)
        && provenance.AdmissibleNotices.All(n => n.Kind is RetractionKind.ExpressionOfConcern or RetractionKind.Correction);

    private static RetractionScreenOutcome RettractionOutcome(RetractionKind kind) =>
        kind is RetractionKind.FullRetraction or RetractionKind.PartialRetraction
            ? RetractionScreenOutcome.Withdrawn
            : RetractionScreenOutcome.Flagged;

    private static IReadOnlyList<string> AppendReason(IReadOnlyList<string> existing, string reason)
    {
        if (existing.Contains(reason, StringComparer.Ordinal)) return existing;
        var next = existing.ToList();
        next.Add(reason);
        return next;
    }
}

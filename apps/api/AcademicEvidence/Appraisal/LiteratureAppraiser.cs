using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Appraisal;

/// <summary>Inputs for one end-to-end appraisal of a single source.</summary>
public sealed record AppraisalRequest(
    /// <summary>Tenant scope for every row produced.</summary>
    Guid WorkspaceId,

    /// <summary>Stable identity the source will be stored under.</summary>
    Guid SourceId,

    /// <summary>Provider payloads, in any order; the normalizer sorts them.</summary>
    IReadOnlyList<RawBibliographicMetadata> Origins,

    /// <summary>Publication-integrity notices known for the work.</summary>
    IReadOnlyList<RetractionNotice>? Notices = null,

    /// <summary>The review protocol to screen against.</summary>
    InclusionCriteria? Criteria = null,

    /// <summary>Screening weights and window.</summary>
    QualityScreenOptions? ScreenOptions = null,

    /// <summary>Risk-of-bias framework code, when an appraisal is recorded.</summary>
    string? BiasFramework = null,

    /// <summary>Answers for that framework's domains.</summary>
    IReadOnlyList<BiasDomainFinding>? BiasFindings = null,

    /// <summary>True when the member chose this source deliberately.</summary>
    bool MemberSelected = false,

    /// <summary>Source this record duplicates, when dedupe found one.</summary>
    Guid? DuplicateOf = null,

    /// <summary>Assessed topical relevance from 0 to 1, null when nobody assessed it.</summary>
    double? TopicRelevance = null,

    /// <summary>Peer-review status as recorded, if any origin established one.</summary>
    PeerReviewStatus? PeerReviewStatus = null,

    /// <summary>Clock for the pass; defaults to the newest observation.</summary>
    DateTimeOffset? ObservedAtUtc = null);

/// <summary>Everything one appraisal produced, kept together for persistence.</summary>
public sealed record AppraisalResult(
    NormalizationResult Normalization,
    NormalizedBibliographicRecord Record,
    SourceProvenance Provenance,
    RetractionScreenResult Retraction,
    QualityScreenResult Quality,
    RiskOfBiasAssessment? Bias,
    InclusionDecision Decision,
    IReadOnlyList<EvidenceFlag> Flags);

/// <summary>
/// Runs the whole Phase B pipeline for one source: normalize the metadata, verify
/// provenance, screen for retractions, assign a quality tier, appraise risk of bias
/// and decide inclusion, with every step's output preserved.
///
/// The order is the design. Provenance is settled before quality, because a
/// beautifully peer-reviewed paper that two origins disagree about which work it is
/// has no quality at all yet. The retraction screen runs before the quality tier, so
/// a notice downgrades the tier rather than being a note beneath it. And inclusion
/// runs last, so its reason codes can cite the verdicts that produced them.
/// </summary>
public interface ILiteratureAppraiser
{
    /// <summary>Appraise one source from its provider payloads.</summary>
    AppraisalResult Appraise(AppraisalRequest request);
}

/// <inheritdoc />
public sealed class LiteratureAppraiser : ILiteratureAppraiser
{
    /// <inheritdoc />
    public AppraisalResult Appraise(AppraisalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var observedAt = request.ObservedAtUtc
            ?? request.Origins.Select(o => o.ObservedAtUtc).DefaultIfEmpty(DateTimeOffset.UnixEpoch).Max();

        var normalization = BibliographicRecordNormalizer.Normalize(
            request.WorkspaceId, request.SourceId, request.Origins, observedAt);

        var record = normalization.Record;

        var verified = ProvenanceVerifier.Verify(
            record, normalization, request.Notices, observedAt);

        var retraction = RetractionScreen.Screen(verified);
        var provenance = retraction.Provenance;

        var bias = request.BiasFramework is { Length: > 0 } framework
            && request.BiasFindings is { Count: > 0 } findings
            && BiasFrameworks.DomainsFor(framework) is not null
            ? RiskOfBiasAssessor.Assess(record, framework, findings, observedAt)
            : null;

        var quality = SourceQualityScreener.Screen(
            record,
            provenance,
            request.PeerReviewStatus,
            bias,
            request.ScreenOptions,
            observedAt);

        var decision = InclusionExclusionPolicy.Evaluate(
            record,
            provenance,
            quality,
            request.Criteria,
            request.MemberSelected,
            request.DuplicateOf,
            request.TopicRelevance,
            observedAt).Decision;

        var flags = new List<EvidenceFlag>();
        flags.AddRange(normalization.Issues
            .Where(i => i.Severity != FindingSeverity.Note)
            .Select(i => new EvidenceFlag
            {
                Code = i.Code,
                Severity = i.Severity,
                Message = i.Detail,
            }));
        flags.AddRange(retraction.Flags);
        flags.AddRange(quality.Flags);
        if (bias is not null) flags.AddRange(bias.Flags);

        return new AppraisalResult(
            normalization,
            record,
            provenance,
            retraction,
            quality,
            bias,
            decision,
            [.. flags]);
    }
}

/// <summary>
/// Writes the audit rows an appraisal produces.
///
/// Kept separate from the appraiser so the appraisal stays a pure function of its
/// inputs - which is what makes it unit-testable without a database - and the trail
/// of "what the platform knew and when" stays a storage concern.
/// </summary>
public static class AppraisalAuditWriter
{
    /// <summary>Append the events for one completed appraisal.</summary>
    public static int Write(
        ProvenanceAuditChain chain,
        AppraisalResult result,
        DateTimeOffset atUtc,
        AuditActorKind actorKind = AuditActorKind.System,
        Guid? actorId = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(result);

        var before = chain.Events.Count;

        chain.Append(result.Record.SourceId, result.Record.WorkspaceId,
            ProvenanceAuditKinds.RecordNormalized,
            new
            {
                title = result.Record.Title,
                year = result.Record.PublicationYear,
                kind = result.Record.SourceKind.ToString(),
                accessLevel = result.Record.AccessLevel.ToString(),
                originCount = result.Normalization.CorroboratingOriginCount,
            },
            atUtc, actorKind, actorId);

        chain.Append(result.Record.SourceId, result.Record.WorkspaceId,
            ProvenanceAuditKinds.VerdictComputed,
            new
            {
                verdict = result.Provenance.Verdict.ToString(),
                reasons = result.Provenance.ReasonCodes,
                corroboratingOrigins = result.Provenance.CorroboratingOriginCount,
                mayPresentAsVerified = result.Provenance.MayPresentAsVerified,
            },
            atUtc, actorKind, actorId);

        if (result.Provenance.RetractionNotices.Count > 0)
        {
            chain.Append(result.Record.SourceId, result.Record.WorkspaceId,
                ProvenanceAuditKinds.RetractionNoticed,
                new
                {
                    outcome = result.Retraction.Outcome.ToString(),
                    notices = result.Provenance.RetractionNotices
                        .Select(n => new { kind = n.Kind.ToString(), origin = n.Origin.ToString(), doi = n.NoticeDoi })
                        .ToArray(),
                },
                atUtc, actorKind, actorId);
        }

        chain.Append(result.Record.SourceId, result.Record.WorkspaceId,
            ProvenanceAuditKinds.ScreeningDecided,
            new
            {
                decision = result.Decision.Decision.ToString(),
                inclusion = result.Decision.InclusionReasons,
                exclusion = result.Decision.ExclusionReasons,
                criteria = result.Decision.CriteriaVersion,
                tier = result.Quality.Tier.ToString(),
                score = result.Quality.Score,
                coverage = result.Quality.EvidenceCoverage,
            },
            atUtc, actorKind, actorId);

        return chain.Events.Count - before;
    }
}

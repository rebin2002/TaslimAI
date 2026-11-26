using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Evidence;

/// <summary>
/// What recording one evidence item produced: the item, the checks that ran, and
/// whether it may be exported.
/// </summary>
public sealed record EvidenceBuildResult(
    EvidenceItem Item,
    LocatorDecision Locator,
    SupportVerification Support,
    IReadOnlyList<EvidenceFlag> Flags)
{
    /// <summary>True when nothing was rejected by either gate.</summary>
    public bool IsClean => Locator.Admitted
        && Support.Status is EvidenceIntegrityStatus.VerifiedAgainstSource
            or EvidenceIntegrityStatus.Unchecked;
}

/// <summary>
/// Builds evidence items that cannot carry an unsourced pinpoint or an
/// uncheckable quotation.
///
/// Both gates run on every item, in this order: the locator is admitted or
/// discarded first, then the content is checked against the text actually held at
/// the source's access level. A rejected locator does not invalidate the quotation
/// - the item is still kept and searchable, without a page - because a member must
/// be able to see that the system removed a pinpoint rather than that the evidence
/// vanished.
/// </summary>
public static class EvidenceRecordBuilder
{
    /// <summary>Record a quotation or finding against a source.</summary>
    /// <param name="record">Normalized source.</param>
    /// <param name="provenance">Verified provenance; content claims need it to pass.</param>
    /// <param name="topic">Research question or theme the item answers.</param>
    /// <param name="supportKind">What the item asserts.</param>
    /// <param name="content">Quoted or restated text.</param>
    /// <param name="claim">The proposition the content supports.</param>
    /// <param name="structuredClaim">Machine-comparable form of the claim.</param>
    /// <param name="locator">Candidate pinpoint, may be null.</param>
    /// <param name="recordedAtUtc">Timestamp on the item.</param>
    public static EvidenceBuildResult Record(
        NormalizedBibliographicRecord record,
        SourceProvenance provenance,
        string topic,
        EvidenceSupportKind supportKind,
        string? content,
        string? claim = null,
        EvidenceClaim? structuredClaim = null,
        SourceLocator? locator = null,
        DateTimeOffset? recordedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(provenance);

        var assertsContent = supportKind is EvidenceSupportKind.DirectQuote
            or EvidenceSupportKind.Paraphrase
            or EvidenceSupportKind.QuantitativeFinding;

        var storedText = AccessLevelPolicy.GetSearchableText(record);
        var locatorDecision = SourceLocatorPolicy.Admit(locator, record, storedText, assertsContent);

        var flags = new List<EvidenceFlag>(locatorDecision.Flags);

        // A source whose identity is disputed or withdrawn cannot support content,
        // however well its text matches, because "this is that paper" is unproven.
        if (assertsContent && !RetractionScreen.MaySupportClaims(provenance))
        {
            flags.Add(new EvidenceFlag
            {
                Code = provenance.Verdict == ProvenanceVerdict.Withdrawn
                    ? "EVIDENCE_WITHDRAWN_SOURCE"
                    : "EVIDENCE_DISPUTED_SOURCE",
                Severity = FindingSeverity.Blocking,
                Message = provenance.Verdict == ProvenanceVerdict.Withdrawn
                    ? "This source has been retracted, so nothing extracted from it may support a claim."
                    : "The identity of this source is disputed between metadata origins, so its content is "
                        + "not attributable until that is resolved.",
            });
        }

        var support = QuotedSupportVerifier.Verify(record, supportKind, content, claim);
        flags.AddRange(support.Flags);

        var integrity = support.Status;
        if (integrity == EvidenceIntegrityStatus.VerifiedAgainstSource
            && flags.Any(f => f.Severity == FindingSeverity.Blocking))
        {
            integrity = EvidenceIntegrityStatus.UnsupportedByStoredText;
        }

        if (!locatorDecision.Admitted && locator is not null)
        {
            integrity = EvidenceIntegrityStatus.LocatorRejected;
        }

        var reasons = new List<string>(support.ReasonCodes);
        if (!locatorDecision.Admitted) reasons.Add(locatorDecision.Code);

        var item = new EvidenceItem
        {
            EvidenceId = DeterministicId(record.SourceId, topic, content ?? claim ?? supportKind.ToString()),
            WorkspaceId = record.WorkspaceId,
            SourceId = record.SourceId,
            Topic = topic,
            SupportKind = supportKind,
            Content = content,
            Claim = claim,
            StructuredClaim = structuredClaim,
            Locator = locatorDecision.Locator,
            AccessLevelAtRecord = record.AccessLevel,
            Integrity = integrity,
            IntegrityReasons = [.. reasons.Distinct(StringComparer.Ordinal)],
            Flags = [.. flags],
            RecordedAtUtc = recordedAtUtc ?? DateTimeOffset.UnixEpoch,
        };

        return new EvidenceBuildResult(item, locatorDecision, support, flags);
    }

    /// <summary>
    /// Recompute the integrity verdict for a stored item, for example after a full
    /// text arrives for a source that previously held only metadata. The recorded
    /// content and locator are not rewritten, only re-checked.
    /// </summary>
    public static EvidenceItem Recheck(
        EvidenceItem item,
        NormalizedBibliographicRecord record,
        SourceProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(record);

        var assertsContent = item.SupportKind is EvidenceSupportKind.DirectQuote
            or EvidenceSupportKind.Paraphrase
            or EvidenceSupportKind.QuantitativeFinding;

        var storedText = AccessLevelPolicy.GetSearchableText(record);
        var locatorDecision = item.Locator is null
            ? new LocatorDecision(true, LocatorDecisionCodes.NoneSupplied, null, [])
            : SourceLocatorPolicy.Admit(item.Locator, record, storedText, assertsContent);

        var support = QuotedSupportVerifier.Verify(record, item.SupportKind, item.Content, item.Claim);

        var integrity = support.Status;
        var flags = new List<EvidenceFlag>(support.Flags);

        if (!locatorDecision.Admitted)
        {
            integrity = EvidenceIntegrityStatus.LocatorRejected;
            flags.AddRange(locatorDecision.Flags);
        }

        if (assertsContent && !RetractionScreen.MaySupportClaims(provenance))
        {
            integrity = EvidenceIntegrityStatus.NotSupportedAtAccessLevel;
        }

        return item with
        {
            Locator = locatorDecision.Locator ?? item.Locator,
            AccessLevelAtRecord = record.AccessLevel,
            Integrity = integrity,
            Flags = [.. flags],
        };
    }

    /// <summary>
    /// Stable identity for an evidence row, derived from source, topic and content
    /// so re-recording the same extraction is an update rather than a duplicate.
    /// </summary>
    private static Guid DeterministicId(Guid sourceId, string topic, string content)
    {
        var material = $"{sourceId:N}|{topic}|{content}".ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);

        // Set version 5 (name-based) and RFC 4122 variant bits.
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }
}

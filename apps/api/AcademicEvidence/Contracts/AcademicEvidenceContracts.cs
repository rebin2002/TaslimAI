using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Evidence;
using Taslim.Api.AcademicEvidence.Matrix;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Contracts;

/// <summary>
/// Error codes the lane returns. Codes are stable strings so the web client can map
/// them to localised text, and so a test can assert on the reason for a rejection
/// rather than on a sentence that copy will later change.
/// </summary>
public static class AcademicEvidenceErrorCodes
{
    /// <summary>No usable title was supplied by any origin.</summary>
    public const string RecordNotCitable = "ACADEMIC_RECORD_NOT_CITABLE";

    /// <summary>Metadata origins disagree about the work's identity.</summary>
    public const string ProvenanceDisputed = "ACADEMIC_PROVENANCE_DISPUTED";

    /// <summary>The work carries an admissible retraction notice.</summary>
    public const string SourceWithdrawn = "ACADEMIC_SOURCE_WITHDRAWN";

    /// <summary>A content claim was attempted from catalog metadata alone.</summary>
    public const string ContentNotRead = "ACADEMIC_CONTENT_NOT_READ";

    /// <summary>A pinpoint was discarded because no artifact was read from.</summary>
    public const string LocatorRejected = "ACADEMIC_LOCATOR_REJECTED";

    /// <summary>Quoted or reported content is absent from the stored text.</summary>
    public const string SupportNotFound = "ACADEMIC_SUPPORT_NOT_FOUND";

    /// <summary>The source does not belong to the caller's workspace.</summary>
    public const string CrossWorkspaceAccess = "ACADEMIC_CROSS_WORKSPACE_ACCESS";
}

/// <summary>Author as returned to the client.</summary>
public sealed record BibliographicAuthorDto(
    string DisplayName,
    string FamilyName,
    string? GivenNames,
    string? Orcid,
    string Role);

/// <summary>Identifier as returned to the client.</summary>
public sealed record BibliographicIdentifierDto(
    string Kind,
    string RawValue,
    string? NormalizedValue,
    bool IsWellFormed,
    string? MalformedReason,
    string Origin);

/// <summary>Normalized record as returned to the client.</summary>
public sealed record NormalizedRecordDto(
    Guid SourceId,
    string? Title,
    string? Subtitle,
    string? OriginalScriptTitle,
    string SourceKind,
    string? Venue,
    string? PublicationYear,
    string YearBasis,
    string? Publisher,
    string? EditionLabel,
    int? EditionNumber,
    string? Volume,
    string? Issue,
    string? PageRange,
    string? PageRangeOrigin,
    IReadOnlyList<BibliographicAuthorDto> Authors,
    IReadOnlyList<BibliographicAuthorDto> Editors,
    IReadOnlyList<BibliographicIdentifierDto> Identifiers,
    string AccessLevel,
    string ContentAcquisition,
    bool IsCitable,

    /// <summary>
    /// True when the platform holds readable content. The client renders this as a
    /// separate chip from the citation badge, because "we have metadata" and "we
    /// read it" are different statements.
    /// </summary>
    bool ContentHeld,
    bool HasAbstract,
    bool HasFullText);

/// <summary>Provenance as returned to the client.</summary>
public sealed record SourceProvenanceDto(
    Guid SourceId,
    string Verdict,
    IReadOnlyList<string> ReasonCodes,

    /// <summary>Drives the "verified citation" badge; never inferred from verdict alone.</summary>
    bool MayPresentAsVerified,
    int CorroboratingOriginCount,
    IReadOnlyList<string> NoticeKinds);

/// <summary>Screening outcome as returned to the client.</summary>
public sealed record ScreeningDto(
    Guid SourceId,
    string QualityTier,
    double Score,
    double EvidenceCoverage,
    string Decision,
    IReadOnlyList<string> InclusionReasons,
    IReadOnlyList<string> ExclusionReasons,
    string? OverallBiasRisk,
    double? BiasAnsweredCoverage,
    IReadOnlyList<EvidenceFlagDto> Flags);

/// <summary>A disclosure flag as returned to the client.</summary>
public sealed record EvidenceFlagDto(string Code, string Severity, string Message);

/// <summary>An evidence row as returned to the client.</summary>
public sealed record EvidenceItemDto(
    Guid EvidenceId,
    Guid SourceId,
    string Topic,
    string SupportKind,
    string? Content,
    string? Claim,
    string? Locator,
    string? LocatorOrigin,
    bool LocatorWasRejected,
    string? LocatorRejectionCode,
    string Integrity,
    IReadOnlyList<string> IntegrityReasons,
    bool IsExportable,
    IReadOnlyList<EvidenceFlagDto> Flags);

/// <summary>A matrix cell as returned to the client.</summary>
public sealed record EvidenceMatrixCellDto(
    string QuestionKey,
    Guid SourceId,
    string Stance,
    bool HasVerifiableLocator,
    string AccessLevel,
    string Provenance,
    string Quality,
    string Screening);

/// <summary>A gap or relationship as returned to the client.</summary>
public sealed record MatrixFindingDto(
    string FindingType,
    string Code,
    string QuestionKey,
    string Severity,
    string Explanation,
    string? SuggestedStep,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<Guid> SourceIds);

/// <summary>The whole matrix as returned to the client.</summary>
public sealed record EvidenceMatrixDto(
    Guid ProjectId,
    IReadOnlyList<MatrixQuestionDto> Questions,
    IReadOnlyList<Guid> SourceIds,
    IReadOnlyList<EvidenceMatrixCellDto> Cells,
    IReadOnlyList<MatrixFindingDto> Findings,
    IReadOnlyList<string> RenderedMarkdownLines);

/// <summary>A research question as returned to the client.</summary>
public sealed record MatrixQuestionDto(string Key, string Text, string? Hypothesis);

/// <summary>
/// Maps domain values onto the client contract.
///
/// The mapper exists so the API never exposes an entity that carries a private
/// manuscript: <see cref="NormalizedBibliographicRecord.FullText"/> and the abstract
/// are deliberately absent from the DTO, since the client reads them through the
/// document endpoint where the download policy applies. It is also the single place
/// that decides how a locator is printed, so a file-paginated page cannot be rendered
/// as a journal page by accident.
/// </summary>
public static class AcademicEvidenceContractMapper
{
    /// <summary>Map a normalized record.</summary>
    public static NormalizedRecordDto ToDto(NormalizedBibliographicRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new NormalizedRecordDto(
            record.SourceId,
            record.Title,
            record.Subtitle,
            record.OriginalScriptTitle,
            record.SourceKind.ToString(),
            record.Venue,
            record.PublicationYear,
            record.YearBasis.ToString(),
            record.Publisher,
            record.EditionLabel,
            record.EditionNumber,
            record.Volume,
            record.Issue,
            record.PageRange,
            record.PageRangeOrigin.ToString(),
            [.. record.Authors.Select(ToDto)],
            [.. record.Editors.Select(ToDto)],
            [.. record.Identifiers.Select(ToDto)],
            record.AccessLevel.ToString(),
            record.ContentAcquisition.ToString(),
            record.IsCitable,
            AccessLevelPolicy.SupportsContentClaims(record.AccessLevel),
            !string.IsNullOrWhiteSpace(record.AbstractText),
            !string.IsNullOrWhiteSpace(record.FullText));
    }

    /// <summary>Map a provenance verdict.</summary>
    public static SourceProvenanceDto ToDto(SourceProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        return new SourceProvenanceDto(
            provenance.SourceId,
            provenance.Verdict.ToString(),
            provenance.ReasonCodes,
            provenance.MayPresentAsVerified,
            provenance.CorroboratingOriginCount,
            [.. provenance.AdmissibleNotices.Select(n => n.Kind.ToString())]);
    }

    /// <summary>Map quality and inclusion together.</summary>
    public static ScreeningDto ToDto(
        QualityScreenResult quality,
        InclusionDecision decision,
        RiskOfBiasAssessment? bias)
    {
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(decision);

        return new ScreeningDto(
            quality.SourceId,
            quality.Tier.ToString(),
            quality.Score,
            quality.EvidenceCoverage,
            decision.Decision.ToString(),
            decision.InclusionReasons,
            decision.ExclusionReasons,
            bias?.OverallRisk.ToString(),
            bias?.AnsweredCoverage,
            [.. quality.Flags.Select(ToDto)]);
    }

    /// <summary>Map an evidence row, including how its locator failed if it did.</summary>
    public static EvidenceItemDto ToDto(EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var rejection = item.IntegrityReasons.FirstOrDefault(
            reason => reason.StartsWith("LOCATOR", StringComparison.Ordinal));

        return new EvidenceItemDto(
            item.EvidenceId,
            item.SourceId,
            item.Topic,
            item.SupportKind.ToString(),
            item.Content,
            item.Claim,
            SourceLocatorPolicy.FormatForCitation(item.Locator),
            item.Locator?.Origin.ToString(),
            item.Integrity == EvidenceIntegrityStatus.LocatorRejected,
            rejection,
            item.Integrity.ToString(),
            item.IntegrityReasons,
            item.IsExportable,
            [.. item.Flags.Select(ToDto)]);
    }

    /// <summary>Map a finished matrix.</summary>
    public static EvidenceMatrixDto ToDto(Domain.EvidenceMatrix matrix, Func<Guid, string?> sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(sourceLabel);

        var markdown = EvidenceMatrixMarkdownRenderer.Render(matrix, sourceLabel)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToList();

        return new EvidenceMatrixDto(
            matrix.ProjectId,
            [.. matrix.Questions.Select(q => new MatrixQuestionDto(q.Key, q.Text, q.Hypothesis))],
            matrix.SourceIds,
            [.. matrix.Cells
                .OrderBy(c => c.QuestionKey, StringComparer.Ordinal)
                .ThenBy(c => c.SourceId)
                .Select(ToDto)],
            [.. matrix.Relationships
                .OrderBy(r => r.QuestionKey, StringComparer.Ordinal)
                .Select(r => new MatrixFindingDto(
                    "relationship",
                    r.Relation.ToString(),
                    r.QuestionKey,
                    r.Relation == ClaimRelation.Contradiction ? "Warning" : "Note",
                    r.Explanation ?? string.Empty,
                    null,
                    r.ReasonCodes,
                    [r.LeftSourceId, r.RightSourceId]))
                .Concat(matrix.Gaps
                    .OrderBy(g => g.Kind)
                    .Select(g => new MatrixFindingDto(
                        "gap",
                        g.Kind.ToString(),
                        g.QuestionKey ?? string.Empty,
                        g.Severity.ToString(),
                        g.Description,
                        g.SuggestedStep,
                        [],
                        g.SourceIds)))],
            [.. markdown]);
    }

    private static EvidenceMatrixCellDto ToDto(EvidenceMatrixCell cell) => new(
        cell.QuestionKey,
        cell.SourceId,
        cell.Stance.ToString(),
        cell.HasVerifiableLocator,
        cell.AccessLevel.ToString(),
        cell.Provenance.ToString(),
        cell.Quality.ToString(),
        cell.Screening.ToString());

    private static BibliographicAuthorDto ToDto(BibliographicAuthor author) => new(
        author.DisplayName,
        author.FamilyName,
        author.GivenNames,
        author.Orcid,
        author.Role.ToString());

    private static BibliographicIdentifierDto ToDto(BibliographicIdentifier identifier) => new(
        identifier.Kind.ToString(),
        identifier.RawValue,
        identifier.NormalizedValue,
        identifier.IsWellFormed,
        identifier.MalformedReason,
        identifier.Origin.ToString());

    private static EvidenceFlagDto ToDto(EvidenceFlag flag) => new(
        flag.Code,
        flag.Severity.ToString(),
        flag.Message);
}

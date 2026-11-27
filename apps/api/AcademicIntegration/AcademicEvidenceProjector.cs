using System.Security.Cryptography;
using System.Text;
using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Persistence;
using Taslim.Api.AcademicEvidence.Matrix;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicWorkflow;

namespace Taslim.Api.AcademicIntegration;

/// <summary>
/// Projects committed workflow artifacts into the Phase B evidence lane
/// (<c>AcademicEvidence</c>): normalized records, provenance verdicts, screening
/// rows, evidence items and the rebuilt question-by-source matrix.
///
/// The projection is deliberately conservative about what the workflow lane can
/// certify. A workflow <see cref="AcademicSourceRecord"/> carries catalog metadata,
/// never retrieved body text, so every origin is recorded as a provider assertion
/// (which the evidence lane treats as a lead, not corroboration), full-text stays
/// absent, and a locator a stage reports without an admissible anchor is persisted as
/// rejected rather than laundered into a citation. That is the citation-verification
/// boundary the integration must hold: a matrix cell can only claim verifiable support
/// from content the platform actually read.
///
/// Re-running the projection for the same snapshot produces identical rows (all ids
/// derive from SHA-256 of stable inputs, and the matrix write is a rebuild), so it is
/// safe after a crash between the workflow commit and the projection watermark.
/// </summary>
public sealed class AcademicEvidenceProjector(ILiteratureAppraiser appraiser, IAcademicEvidenceStore evidenceStore)
{
    private const string OriginKey = "academic-workflow";

    /// <summary>Stable screening-protocol identity for one workflow project.</summary>
    public static string ProtocolIdFor(Guid projectId) => "wf-" + projectId.ToString("N");

    /// <summary>True when there is anything worth projecting yet.</summary>
    public static bool HasProjectableArtifacts(AcademicWorkflowSnapshot snapshot) =>
        snapshot.Plan is not null && snapshot.Sources.Count > 0;

    /// <summary>
    /// Project the committed snapshot. Returns the number of sources whose appraisal
    /// rows were written; callers advance the watermark only after this completes.
    /// </summary>
    public async Task<int> ProjectAsync(AcademicWorkflowSnapshot snapshot, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HasProjectableArtifacts(snapshot)) return 0;

        var protocolId = ProtocolIdFor(snapshot.ProjectId);
        var questions = QuestionsFor(snapshot);
        var assessments = new List<SourceAssessment>(snapshot.Sources.Count);
        var labels = new Dictionary<Guid, string>();
        var written = 0;

        foreach (var source in snapshot.Sources.OrderBy(s => s.SourceId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceGuid = DeriveSourceId(snapshot.ProjectId, source.SourceId);
            labels[sourceGuid] = source.Title;

            var appraisal = appraiser.Appraise(new AppraisalRequest(
                WorkspaceId: snapshot.WorkspaceId,
                SourceId: sourceGuid,
                Origins: [RawMetadataFor(snapshot, source)],
                Notices: RetractionNoticesFor(source, atUtc),
                Criteria: new InclusionCriteria(Version: protocolId),
                ObservedAtUtc: atUtc));

            // The workflow's own screening verdict outranks the pipeline's default once
            // the student approved the library, but it can only move a source to the
            // same three states the protocol already knows. An integrity exclusion the
            // pipeline found (a disputed identity, a withdrawn notice) is never undone
            // here: workflow screening refines scope, it does not overrule integrity.
            var decision = MergeDecision(appraisal.Decision, snapshot, source.SourceId, sourceGuid, protocolId, atUtc);

            var evidence = MapEvidence(snapshot, source, sourceGuid, questions, atUtc);

            await evidenceStore.SaveAppraisalAsync(
                new AcademicEvidenceSnapshot(appraisal.Record, appraisal.Provenance, appraisal.Quality, decision, appraisal.Bias, evidence),
                snapshot.ProjectId,
                protocolId,
                atUtc,
                cancellationToken);

            await evidenceStore.ReplaceEvidenceAsync(snapshot.WorkspaceId, sourceGuid, evidence, cancellationToken);

            assessments.Add(new SourceAssessment(appraisal.Record, appraisal.Provenance, appraisal.Quality, decision, appraisal.Bias, evidence));
            written++;
        }

        var matrix = EvidenceMatrixBuilder.Build(snapshot.WorkspaceId, snapshot.ProjectId, questions, assessments);
        await evidenceStore.SaveMatrixAsync(matrix, id => labels.TryGetValue(id, out var label) ? label : null, atUtc, cancellationToken);
        return written;
    }

    // --- deterministic identity ------------------------------------------------------------

    /// <summary>Stable evidence-lane source id for one workflow source within one project.</summary>
    public static Guid DeriveSourceId(Guid projectId, string workflowSourceId) =>
        DeriveGuid($"src|{projectId:N}|{workflowSourceId}");

    private static Guid DeriveEvidenceId(Guid projectId, string workflowEvidenceId) =>
        DeriveGuid($"ev|{projectId:N}|{workflowEvidenceId}");

    private static Guid DeriveGuid(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    // --- mapping ---------------------------------------------------------------------------

    private static RawBibliographicMetadata RawMetadataFor(
        AcademicWorkflowSnapshot snapshot,
        AcademicSourceRecord source)
    {
        var observed = new DateTimeOffset(snapshot.CreatedAtUtc == default ? DateTime.UtcNow : snapshot.CreatedAtUtc, TimeSpan.Zero);
        var identifiers = new List<RawIdentifier>();
        if (source.IdentifierKind is AcademicIdentifierKind kind && !string.IsNullOrWhiteSpace(source.IdentifierValue))
        {
            identifiers.Add(new RawIdentifier
            {
                Kind = MapIdentifierKind(kind),
                Value = source.IdentifierValue,
                OriginKey = OriginKey,
                // A workflow source record is catalog metadata the stage read; until a
                // resolver confirms it, it asserts, it does not certify.
                Origin = IdentifierOrigin.ProviderAssertion,
                ObservedAtUtc = observed,
            });
        }

        return new RawBibliographicMetadata
        {
            OriginKey = OriginKey,
            ObservedAtUtc = observed,
            Title = source.Title,
            SourceKindToken = source.SourceType,
            Publisher = source.Publisher,
            YearToken = source.PublicationYear?.ToString(),
            YearBasis = source.PublicationYear is null ? PublicationYearBasis.Unknown : PublicationYearBasis.PrintedPublicationYear,
            LandingPageUrl = source.Url,
            AbstractText = source.AbstractText,
            // No FullText: the workflow artifact never carries retrieved body text, and a
            // claimed access level is not content. The body handoff is the provider lane's
            // seam, documented in ACADEMIC_INTEGRATION_SLICE.md.
            FullText = null,
            Language = string.IsNullOrWhiteSpace(snapshot.Request.Language) ? null : snapshot.Request.Language,
            Discipline = snapshot.Request.Discipline,
            Authors = [.. source.Authors
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => new RawAuthorToken { Name = a }),
            ],
            Identifiers = identifiers,
        };
    }

    private static IReadOnlyList<RetractionNotice>? RetractionNoticesFor(AcademicSourceRecord source, DateTimeOffset atUtc)
    {
        if (!source.IsRetracted) return null;
        return
        [
            new RetractionNotice
            {
                Kind = RetractionKind.FullRetraction,
                // The stage reported the retraction, not a registry notice: the evidence
                // lane keeps this as a lead to check and never rewrites history from it,
                // while the workflow screening decision still excludes the source.
                Origin = RetractionNoticeOrigin.ProviderAssertion,
                NoticeText = source.AccessNote ?? "The discovery stage reported a retraction notice for this work.",
                NoticedAtUtc = atUtc,
            },
        ];
    }

    private static IdentifierKind MapIdentifierKind(AcademicIdentifierKind kind) => kind switch
    {
        AcademicIdentifierKind.Doi => IdentifierKind.Doi,
        AcademicIdentifierKind.Isbn => IdentifierKind.Isbn,
        AcademicIdentifierKind.ArXiv => IdentifierKind.Arxiv,
        AcademicIdentifierKind.Pmid => IdentifierKind.Pubmed,
        AcademicIdentifierKind.Url => IdentifierKind.Url,
        _ => IdentifierKind.Unknown,
    };

    private static InclusionDecision MergeDecision(
        InclusionDecision pipeline,
        AcademicWorkflowSnapshot snapshot,
        string workflowSourceId,
        Guid sourceGuid,
        string protocolId,
        DateTimeOffset atUtc)
    {
        var screening = snapshot.Screening.FirstOrDefault(s =>
            string.Equals(s.SourceId, workflowSourceId, StringComparison.Ordinal));
        if (screening is null)
            return pipeline with { CriteriaVersion = protocolId };

        var mapped = screening.Decision switch
        {
            AcademicScreeningDecision.Included => ScreeningDecision.Included,
            AcademicScreeningDecision.Excluded => ScreeningDecision.Excluded,
            AcademicScreeningDecision.Uncertain => ScreeningDecision.Undetermined,
            _ => ScreeningDecision.Undetermined,
        };

        // An integrity exclusion from the pipeline is final: screening may exclude a
        // source the pipeline would keep, never keep one the pipeline had to drop.
        if (mapped == ScreeningDecision.Included && pipeline.Decision == ScreeningDecision.Excluded)
        {
            return pipeline with { CriteriaVersion = protocolId, MemberNote = Truncate(screening.Reason, 4096) };
        }

        return new InclusionDecision
        {
            SourceId = sourceGuid,
            WorkspaceId = snapshot.WorkspaceId,
            Decision = mapped,
            InclusionReasons = mapped == ScreeningDecision.Included ? ["WF_SCREEN_INCLUDED"] : [],
            ExclusionReasons = mapped == ScreeningDecision.Excluded
                ? ["WF_SCREEN_EXCLUDED", .. pipeline.ExclusionReasons]
                : pipeline.ExclusionReasons,
            UndeterminedReasons = mapped == ScreeningDecision.Undetermined
                ? ["WF_SCREEN_UNCERTAIN", .. pipeline.UndeterminedReasons]
                : pipeline.UndeterminedReasons,
            CriteriaVersion = protocolId,
            MemberNote = Truncate(screening.Reason, 4096),
            DecidedBy = AuditActorKind.System,
            DecidedAtUtc = atUtc,
        };
    }

    private List<EvidenceItem> MapEvidence(
        AcademicWorkflowSnapshot snapshot,
        AcademicSourceRecord source,
        Guid sourceGuid,
        IReadOnlyList<MatrixQuestion> questions,
        DateTimeOffset atUtc)
    {
        var items = snapshot.Evidence
            .Where(e => string.Equals(e.SourceId, source.SourceId, StringComparison.Ordinal))
            .OrderBy(e => e.EvidenceId, StringComparer.Ordinal)
            .ToList();

        var mapped = new List<EvidenceItem>(items.Count);
        foreach (var item in items)
        {
            var topicKey = MatchTopicKey(item.Topic, questions);
            var access = source.AccessLevel;
            var flags = new List<EvidenceFlag>();

            EvidenceSupportKind support;
            EvidenceIntegrityStatus integrity;
            var reasons = new List<string>();

            bool contentReadable = access != AcademicAccessLevel.MetadataOnly && !string.IsNullOrWhiteSpace(item.Content);
            bool quoteSupported = access == AcademicAccessLevel.Abstract
                && item.Verbatim
                && AcademicAccessRules.ContainsNormalized(source.AbstractText, item.Content);

            if (access == AcademicAccessLevel.MetadataOnly)
            {
                support = EvidenceSupportKind.BibliographicOnly;
                integrity = string.IsNullOrWhiteSpace(item.Content)
                    ? EvidenceIntegrityStatus.Unchecked
                    : EvidenceIntegrityStatus.NotSupportedAtAccessLevel;
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    reasons.Add("WF_METADATA_ONLY_CONTENT");
                    flags.Add(Note("WF_METADATA_ONLY_CONTENT",
                        "Metadata-only sources cannot carry content evidence; the text was kept bibliographic."));
                }
            }
            else if (item.Verbatim && quoteSupported)
            {
                support = EvidenceSupportKind.DirectQuote;
                integrity = EvidenceIntegrityStatus.VerifiedAgainstSource;
                reasons.Add("WF_ABSTRACT_CONTAINMENT");
            }
            else if (item.Verbatim)
            {
                support = EvidenceSupportKind.DirectQuote;
                integrity = EvidenceIntegrityStatus.UnsupportedByStoredText;
                reasons.Add("WF_QUOTE_NOT_IN_STORED_TEXT");
                flags.Add(Note("WF_QUOTE_NOT_IN_STORED_TEXT",
                    "The quoted words were not found in the stored abstract, so the quotation is not presented as supported."));
            }
            else
            {
                support = EvidenceSupportKind.Paraphrase;
                integrity = contentReadable
                    ? EvidenceIntegrityStatus.Unchecked
                    : EvidenceIntegrityStatus.NotSupportedAtAccessLevel;
                if (access == AcademicAccessLevel.FullText)
                {
                    // A full-text claim without a retrieved copy in the lane: the
                    // paraphrase is kept, but nothing about it can be verified.
                    flags.Add(Note("WF_FULLTEXT_BODY_ABSENT",
                        "The stage reported full-text access but no retrieved copy was handed to the evidence lane, so this paraphrase is unverifiable."));
                    reasons.Add("WF_FULLTEXT_BODY_ABSENT");
                }
            }

            // No-fabricated-page rule across the seam: a locator arriving without an
            // admissible anchor is recorded as rejected, and never becomes a pinpoint.
            SourceLocator? locator = null;
            if (!string.IsNullOrWhiteSpace(item.Locator))
            {
                integrity = EvidenceIntegrityStatus.LocatorRejected;
                // The lane's rejection vocabulary (LOCATOR_MEMBER_ASSERTED: a member of the
                // pipeline asserted a page with no retrieved-copy anchor behind it) is what
                // earns LocatorRejectionCode on the stored row; the WF marker records which
                // lane observed it, so attribution survives either direction of reading.
                reasons.Add("LOCATOR_MEMBER_ASSERTED");
                reasons.Add("WF_LOCATOR_UNANCHORED");
                flags.Add(Note("WF_LOCATOR_UNANCHORED",
                    "The reported page has no retrieved-copy anchor in the evidence lane, so it was not used as a citation pinpoint."));
            }

            mapped.Add(new EvidenceItem
            {
                EvidenceId = DeriveEvidenceId(snapshot.ProjectId, item.EvidenceId),
                WorkspaceId = snapshot.WorkspaceId,
                SourceId = sourceGuid,
                Topic = topicKey,
                Content = string.IsNullOrWhiteSpace(item.Content) ? null : item.Content,
                Claim = null,
                StructuredClaim = null,
                Locator = locator,
                AccessLevelAtRecord = MapAccess(access),
                SupportKind = support,
                Integrity = integrity,
                IntegrityReasons = reasons,
                Flags = flags,
                RecordedAtUtc = atUtc,
            });
        }

        return mapped;
    }

    private static EvidenceFlag Note(string code, string message) =>
        new() { Code = code, Severity = FindingSeverity.Warning, Message = message };

    private static SourceAccessLevel MapAccess(AcademicAccessLevel access) => access switch
    {
        AcademicAccessLevel.MetadataOnly => SourceAccessLevel.MetadataOnly,
        AcademicAccessLevel.Abstract => SourceAccessLevel.Abstract,
        AcademicAccessLevel.FullText => SourceAccessLevel.FullText,
        _ => SourceAccessLevel.Unknown,
    };

    /// <summary>
    /// Matrix questions: the approved plan's objectives first (keys q1..qn), plus one
    /// slug-keyed question per distinct evidence topic the extraction stage reported.
    /// Deterministic in both ordering and keys, so a rebuild yields the same question set.
    /// </summary>
    public static IReadOnlyList<MatrixQuestion> QuestionsFor(AcademicWorkflowSnapshot snapshot)
    {
        var questions = new List<MatrixQuestion>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var objectives = snapshot.Plan?.Objectives ?? [];
        for (var i = 0; i < objectives.Count; i++)
        {
            var key = $"q{i + 1}";
            if (keys.Add(key))
                questions.Add(new MatrixQuestion { Key = key, Text = objectives[i], Discipline = snapshot.Request.Discipline });
        }

        if (snapshot.Plan is not null && keys.Add("rq"))
            questions.Add(new MatrixQuestion
            {
                Key = "rq",
                Text = snapshot.Plan.ResearchQuestion,
                Hypothesis = null,
                Discipline = snapshot.Request.Discipline,
            });

        foreach (var topic in snapshot.Evidence.Select(e => e.Topic)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(t => t, StringComparer.Ordinal))
        {
            var key = TopicKey(topic, objectives);
            if (keys.Add(key))
                questions.Add(new MatrixQuestion { Key = key, Text = topic });
        }

        return questions;
    }

    /// <summary>
    /// Maps a free-text evidence topic onto its matrix question key: an exact objective
    /// key ("q2"), an objective's text, or a deterministic slug of the topic itself.
    /// </summary>
    public static string MatchTopicKey(string? topic, IReadOnlyList<MatrixQuestion> questions)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "unsorted";
        var trimmed = topic.Trim();
        foreach (var question in questions)
        {
            if (string.Equals(question.Key, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(question.Text, trimmed, StringComparison.OrdinalIgnoreCase))
                return question.Key;
        }

        return Slug(trimmed, 96);
    }

    private static string TopicKey(string topic, IReadOnlyList<string> objectives)
    {
        for (var i = 0; i < objectives.Count; i++)
        {
            if (string.Equals(objectives[i], topic, StringComparison.OrdinalIgnoreCase)) return $"q{i + 1}";
            var key = $"q{i + 1}";
            if (string.Equals(key, topic, StringComparison.OrdinalIgnoreCase)) return key;
        }

        return Slug(topic, 96);
    }

    private static string Slug(string value, int max)
    {
        var builder = new StringBuilder(max + 16);
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        while (builder.Length > 0 && builder[^1] == '-') builder.Length--;
        var slug = builder.ToString();
        if (slug.Length > max) slug = slug[..max];
        return string.IsNullOrEmpty(slug) ? "t" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant() : slug;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Provenance;

namespace Taslim.Api.AcademicEvidence.Persistence;

/// <summary>
/// Converts between the normalized domain model and the stored rows.
///
/// Two properties matter here. Enum names are stored as text rather than as ints, so
/// a reordering in the domain cannot silently reinterpret existing rows. And the
/// mapper is lossless in both directions for everything the integrity rules read
/// back: an evidence row whose locator was rejected must still say it was rejected
/// after a reload, or the export path would print a pinpoint that the policy threw
/// away.
/// </summary>
public static class AcademicEvidenceRowMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>SHA-256 of the stored text, lowercased hex; empty for no text.</summary>
    public static string ContentDigest(string? text) => string.IsNullOrEmpty(text)
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Map a normalized record to its row.</summary>
    public static AcademicSourceEntity ToRow(
        NormalizedBibliographicRecord record,
        Guid? projectId,
        string? protocolId,
        Guid? duplicateOf,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(record);

        var when = atUtc.UtcDateTime;

        return new AcademicSourceEntity
        {
            Id = record.SourceId,
            WorkspaceId = record.WorkspaceId,
            ProjectId = projectId,
            Title = record.Title ?? string.Empty,
            Subtitle = record.Subtitle,
            OriginalScriptTitle = record.OriginalScriptTitle,
            SourceKind = record.SourceKind.ToString(),
            PublicationYear = record.PublicationYear,
            YearBasis = record.YearBasis.ToString(),
            PublicationDateUtc = record.PublicationDateUtc?.UtcDateTime,
            Publisher = record.Publisher,
            EditionLabel = record.EditionLabel,
            EditionNumber = record.EditionNumber,
            Volume = record.Volume,
            Issue = record.Issue,
            PageRange = record.PageRange,
            PageRangeOrigin = record.PageRange == null ? null : record.PageRangeOrigin.ToString(),
            Venue = record.Venue,
            AccessLevel = record.AccessLevel.ToString(),
            ContentAcquisition = record.ContentAcquisition.ToString(),
            LandingPageUrl = record.LandingPageUrl,
            Language = record.Language,
            Discipline = record.Discipline,
            StudyDesign = record.StudyDesign,
            CorpusId = record.CorpusId,
            AuthorsJson = Serialize(record.Authors.Select(a => new
            {
                display = a.DisplayName,
                family = a.FamilyName,
                given = a.GivenNames,
                orcid = a.Orcid,
                affiliation = a.Affiliation,
                role = a.Role.ToString(),
                key = a.MatchKey,
            })),
            EditorsJson = Serialize(record.Editors.Select(a => new
            {
                display = a.DisplayName,
                family = a.FamilyName,
                given = a.GivenNames,
                orcid = a.Orcid,
                role = a.Role.ToString(),
                key = a.MatchKey,
            })),
            IdentifiersJson = Serialize(record.Identifiers.Select(i => new
            {
                kind = i.Kind.ToString(),
                raw = i.RawValue,
                normalized = i.NormalizedValue,
                wellFormed = i.IsWellFormed,
                reason = i.MalformedReason,
                origin = i.Origin.ToString(),
                prefix = i.RegistrantPrefix,
                observed = i.ObservedAtUtc.ToUnixTimeMilliseconds(),
            })),
            AbstractText = record.AbstractText,
            FullText = record.FullText,
            ContentSha256 = ContentDigest(record.FullText),
            DedupeKey = Normalization.BibliographicFingerprint.TitleAuthorYearKey(record),
            DuplicateOfSourceId = duplicateOf,
            ProtocolId = protocolId,
            NormalizedAtUtc = when,
            UpdatedAtUtc = when,
        };
    }

    /// <summary>Map a provenance verdict to its row.</summary>
    public static AcademicProvenanceEntity ToRow(
        SourceProvenance provenance,
        DateTimeOffset assessedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        return new AcademicProvenanceEntity
        {
            Id = Guid.NewGuid(),
            SourceId = provenance.SourceId,
            WorkspaceId = provenance.WorkspaceId,
            Verdict = provenance.Verdict.ToString(),
            CorroboratingOriginCount = provenance.CorroboratingOriginCount,
            ReasonCodesJson = Serialize(provenance.ReasonCodes),
            FieldEvidenceJson = Serialize(provenance.Fields.Select(f => new
            {
                field = f.FieldName,
                value = f.Value,
                origins = f.Origins,
                conflicts = f.ConflictingOrigins,
                identifierOrigin = f.IdentifierOrigin.ToString(),
                observed = f.ObservedAtUtc.ToUnixTimeMilliseconds(),
            })),
            RetractionNoticesJson = Serialize(provenance.RetractionNotices.Select(n => new
            {
                kind = n.Kind.ToString(),
                noticeDoi = n.NoticeDoi,
                noticeUrl = n.NoticeUrl,
                notifiedBy = n.NotifiedBy,
                origin = n.Origin.ToString(),
                noticed = n.NoticedAtUtc.ToUnixTimeMilliseconds(),
            })),
            MayPresentAsVerified = provenance.MayPresentAsVerified,
            AssessedAtUtc = assessedAtUtc.UtcDateTime,
        };
    }

    /// <summary>Map a screening outcome to its row.</summary>
    public static AcademicScreeningEntity ToRow(
        QualityScreenResult quality,
        InclusionDecision decision,
        RiskOfBiasAssessment? bias,
        string protocolId)
    {
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(decision);

        return new AcademicScreeningEntity
        {
            Id = Guid.NewGuid(),
            SourceId = quality.SourceId,
            WorkspaceId = quality.WorkspaceId,
            ProtocolId = protocolId,
            QualityTier = quality.Tier.ToString(),
            QualityScore = quality.Score,
            QualityCoverage = quality.EvidenceCoverage,
            CriteriaJson = Serialize(quality.Criteria.Select(c => new
            {
                code = c.CriterionCode,
                outcome = c.Outcome.ToString(),
                weight = c.Weight,
                earned = c.Earned,
                origin = c.SignalOrigin,
                note = c.Note,
            })),
            FlagsJson = Serialize(quality.Flags.Select(FlagJson)),
            ProvenanceVerdict = quality.ProvenanceVerdictAtScreen.ToString(),
            BiasJson = bias is null ? null : SerializeSingle(new
            {
                framework = bias.Framework,
                studyDesign = bias.StudyDesign,
                overall = bias.OverallRisk.ToString(),
                coverage = bias.AnsweredCoverage,
                domains = bias.Domains.Select(d => new
                {
                    domain = d.DomainCode,
                    judgement = d.Judgement.ToString(),
                    rationale = d.Rationale,
                    source = d.Source.ToString(),
                }),
                flags = bias.Flags.Select(FlagJson),
            }),
            Decision = decision.Decision.ToString(),
            InclusionReasonsJson = Serialize(decision.InclusionReasons),
            ExclusionReasonsJson = Serialize(decision.ExclusionReasons),
            UndeterminedReasonsJson = Serialize(decision.UndeterminedReasons),
            DecidedBy = decision.DecidedBy.ToString(),
            MemberNote = decision.MemberNote,
            ScreenedAtUtc = quality.ScreenedAtUtc.UtcDateTime,
            DecidedAtUtc = decision.DecidedAtUtc.UtcDateTime,
        };
    }

    /// <summary>Map an evidence item to its row, preserving any rejection.</summary>
    public static AcademicEvidenceItemEntity ToRow(EvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var rejected = item.Integrity == EvidenceIntegrityStatus.LocatorRejected
            ? item.IntegrityReasons.FirstOrDefault(reason => reason.StartsWith("LOCATOR", StringComparison.Ordinal))
            : null;

        return new AcademicEvidenceItemEntity
        {
            Id = item.EvidenceId,
            SourceId = item.SourceId,
            WorkspaceId = item.WorkspaceId,
            Topic = item.Topic,
            SupportKind = item.SupportKind.ToString(),
            Content = item.Content,
            Claim = item.Claim,
            ClaimJson = item.StructuredClaim is null ? null : SerializeSingle(new
            {
                subject = item.StructuredClaim.SubjectKey,
                predicate = item.StructuredClaim.PredicateKey,
                direction = item.StructuredClaim.Direction.ToString(),
                magnitude = item.StructuredClaim.Magnitude,
                unit = item.StructuredClaim.Unit,
                low = item.StructuredClaim.IntervalLow,
                high = item.StructuredClaim.IntervalHigh,
                population = item.StructuredClaim.PopulationKey,
                timeframe = item.StructuredClaim.Timeframe,
            }),
            LocatorKind = item.Locator is null || item.Locator.IsEmpty ? null : item.Locator.Kind.ToString(),
            LocatorOrigin = item.Locator is null || item.Locator.IsEmpty ? null : item.Locator.Origin.ToString(),
            LocatorLabel = item.Locator?.Label,
            LocatorPaginationBasis = item.Locator is null || item.Locator.IsEmpty
                ? null
                : item.Locator.PaginationBasis.ToString(),
            AnchorStart = item.Locator?.AnchorStart,
            AnchorEnd = item.Locator?.AnchorEnd,
            LocatorRejectionCode = rejected,
            Integrity = item.Integrity.ToString(),
            IntegrityReasonsJson = Serialize(item.IntegrityReasons),
            FlagsJson = Serialize(item.Flags.Select(FlagJson)),
            AccessLevelAtRecord = item.AccessLevelAtRecord.ToString(),
            RecordedAtUtc = item.RecordedAtUtc.UtcDateTime,
        };
    }

    /// <summary>Rebuild a normalized record from its row.</summary>
    public static NormalizedBibliographicRecord ToRecord(AcademicSourceEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var authors = (Deserialize<AuthorRow[]>(row.AuthorsJson) ?? [])
            .Select(a => new BibliographicAuthor
            {
                DisplayName = a.display,
                FamilyName = a.family,
                GivenNames = a.given,
                Orcid = a.orcid,
                Affiliation = a.affiliation,
                Role = ParseEnum(a.role, AuthorRole.Author),
                MatchKey = a.key ?? string.Empty,
            })
            .ToList();

        var editors = (Deserialize<AuthorRow[]>(row.EditorsJson) ?? [])
            .Select(a => new BibliographicAuthor
            {
                DisplayName = a.display,
                FamilyName = a.family,
                GivenNames = a.given,
                Orcid = a.orcid,
                Role = ParseEnum(a.role, AuthorRole.Editor),
                MatchKey = a.key ?? string.Empty,
            })
            .ToList();

        var identifiers = (Deserialize<IdentifierRow[]>(row.IdentifiersJson) ?? [])
            .Select(i => new BibliographicIdentifier
            {
                Kind = ParseEnum(i.kind, IdentifierKind.Unknown),
                RawValue = i.raw,
                NormalizedValue = i.normalized,
                IsWellFormed = i.wellFormed,
                MalformedReason = i.reason,
                Origin = ParseEnum(i.origin, IdentifierOrigin.Unknown),
                RegistrantPrefix = i.prefix,
                ObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(i.observed),
            })
            .ToList();

        var accessLevel = ParseEnum(row.AccessLevel, SourceAccessLevel.Unknown);
        var fullText = accessLevel is SourceAccessLevel.FullText or SourceAccessLevel.PrivateUpload
            ? row.FullText
            : null;

        return new NormalizedBibliographicRecord
        {
            SourceId = row.Id,
            WorkspaceId = row.WorkspaceId,
            Title = string.IsNullOrWhiteSpace(row.Title) ? null : row.Title,
            Subtitle = row.Subtitle,
            OriginalScriptTitle = row.OriginalScriptTitle,
            SourceKind = ParseEnum(row.SourceKind, AcademicSourceKind.Unknown),
            Venue = row.Venue,
            PublicationYear = row.PublicationYear,
            YearBasis = ParseEnum(row.YearBasis, PublicationYearBasis.Unknown),
            PublicationDateUtc = row.PublicationDateUtc is { } date
                ? new DateTimeOffset(date, TimeSpan.Zero)
                : null,
            Publisher = row.Publisher,
            EditionLabel = row.EditionLabel,
            EditionNumber = row.EditionNumber,
            Volume = row.Volume,
            Issue = row.Issue,
            PageRange = row.PageRange,
            PageRangeOrigin = ParseEnum(row.PageRangeOrigin, LocatorOrigin.Unknown),
            Authors = authors,
            Editors = editors,
            Identifiers = identifiers,
            AccessLevel = accessLevel,
            ContentAcquisition = ParseEnum(row.ContentAcquisition, ContentAcquisition.None),
            LandingPageUrl = row.LandingPageUrl,
            Language = row.Language,
            Discipline = row.Discipline,
            StudyDesign = row.StudyDesign,
            AbstractText = row.AbstractText,
            FullText = fullText,
            CorpusId = row.CorpusId,
        };
    }

    /// <summary>Rebuild an evidence item from its row.</summary>
    public static EvidenceItem ToItem(AcademicEvidenceItemEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);

        SourceLocator? locator = null;
        // A rejected pinpoint is stored as a rejection code with no kind, and must
        // come back as no locator at all: a half-restored one could still print.
        if (!string.IsNullOrWhiteSpace(row.LocatorKind)
            && ParseEnum(row.LocatorKind, LocatorKind.None) != LocatorKind.None)
        {
            locator = new SourceLocator
            {
                Kind = ParseEnum(row.LocatorKind, LocatorKind.None),
                Origin = ParseEnum(row.LocatorOrigin, LocatorOrigin.Unknown),
                Label = row.LocatorLabel,
                PaginationBasis = ParseEnum(row.LocatorPaginationBasis, PaginationBasis.NotApplicable),
                AnchorStart = row.AnchorStart,
                AnchorEnd = row.AnchorEnd,
            };
        }

        EvidenceClaim? claim = null;
        if (!string.IsNullOrWhiteSpace(row.ClaimJson))
        {
            var parsed = Deserialize<ClaimRow>(row.ClaimJson);
            claim = new EvidenceClaim
            {
                SubjectKey = parsed.subject,
                PredicateKey = parsed.predicate,
                Direction = ParseEnum(parsed.direction, EffectDirection.Unknown),
                Magnitude = parsed.magnitude,
                Unit = parsed.unit,
                IntervalLow = parsed.low,
                IntervalHigh = parsed.high,
                PopulationKey = parsed.population ?? string.Empty,
                Timeframe = parsed.timeframe,
            };
        }

        return new EvidenceItem
        {
            EvidenceId = row.Id,
            WorkspaceId = row.WorkspaceId,
            SourceId = row.SourceId,
            Topic = row.Topic,
            SupportKind = ParseEnum(row.SupportKind, EvidenceSupportKind.BibliographicOnly),
            Content = row.Content,
            Claim = row.Claim,
            StructuredClaim = claim,
            Locator = locator,
            AccessLevelAtRecord = ParseEnum(row.AccessLevelAtRecord, SourceAccessLevel.Unknown),
            Integrity = ParseEnum(row.Integrity, EvidenceIntegrityStatus.Unchecked),
            IntegrityReasons = Deserialize<string[]>(row.IntegrityReasonsJson) ?? [],
            Flags = [.. (Deserialize<FlagRow[]>(row.FlagsJson) ?? []).Select(ToFlag)],
            RecordedAtUtc = new DateTimeOffset(row.RecordedAtUtc, TimeSpan.Zero),
        };
    }

    /// <summary>Rebuild a provenance verdict from its row.</summary>
    public static SourceProvenance ToProvenance(AcademicProvenanceEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new SourceProvenance
        {
            SourceId = row.SourceId,
            WorkspaceId = row.WorkspaceId,
            Verdict = ParseEnum(row.Verdict, ProvenanceVerdict.Unassessed),
            ReasonCodes = Deserialize<string[]>(row.ReasonCodesJson) ?? [],
            Fields = Deserialize<FieldRow[]>(row.FieldEvidenceJson)?.Select(f => new FieldProvenance
            {
                FieldName = f.field,
                Value = f.value,
                Origins = f.origins ?? [],
                ConflictingOrigins = f.conflicts ?? [],
                IdentifierOrigin = ParseEnum(f.identifierOrigin, IdentifierOrigin.Unknown),
                ObservedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(f.observed),
            }).ToList() ?? [],
            CorroboratingOriginCount = row.CorroboratingOriginCount,
            RetractionNotices = Deserialize<NoticeRow[]>(row.RetractionNoticesJson)?.Select(n => new RetractionNotice
            {
                Kind = ParseEnum(n.kind, RetractionKind.ExpressionOfConcern),
                NoticeDoi = n.noticeDoi,
                NoticeUrl = n.noticeUrl,
                NotifiedBy = n.notifiedBy,
                Origin = ParseEnum(n.origin, RetractionNoticeOrigin.Unknown),
                NoticedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(n.noticed),
            }).ToList() ?? [],
        };
    }

    /// <summary>Serialize a collection as canonical JSON for a text column.</summary>
    /// <summary>Rebuild the quality screening result, criteria and all.</summary>
    public static QualityScreenResult ToQuality(AcademicScreeningEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new QualityScreenResult
        {
            SourceId = row.SourceId,
            WorkspaceId = row.WorkspaceId,
            Tier = ParseEnum(row.QualityTier, QualityTier.InsufficientInformation),
            Score = row.QualityScore,
            EvidenceCoverage = row.QualityCoverage,
            Criteria = Deserialize<CriterionRow[]>(row.CriteriaJson)?.Select(c => new QualityCriterionFinding
            {
                CriterionCode = c.code,
                Outcome = ParseEnum(c.outcome, CriterionOutcome.NoSignal),
                Weight = c.weight,
                Earned = c.earned,
                SignalOrigin = c.origin,
                Note = c.note,
            }).ToList() ?? [],
            Flags = Deserialize<FlagRow[]>(row.FlagsJson)?.Select(ToFlag).ToList() ?? [],
            ProvenanceVerdictAtScreen = ParseEnum(row.ProvenanceVerdict, ProvenanceVerdict.Unassessed),
            ScreenedAtUtc = new DateTimeOffset(row.ScreenedAtUtc, TimeSpan.Zero),
        };
    }

    /// <summary>Rebuild the inclusion decision with its reason codes.</summary>
    public static InclusionDecision ToDecision(AcademicScreeningEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new InclusionDecision
        {
            SourceId = row.SourceId,
            WorkspaceId = row.WorkspaceId,
            Decision = ParseEnum(row.Decision, ScreeningDecision.Unscheduled),
            InclusionReasons = Deserialize<string[]>(row.InclusionReasonsJson) ?? [],
            ExclusionReasons = Deserialize<string[]>(row.ExclusionReasonsJson) ?? [],
            UndeterminedReasons = Deserialize<string[]>(row.UndeterminedReasonsJson) ?? [],
            CriteriaVersion = row.ProtocolId,
            MemberNote = row.MemberNote,
            DecidedBy = ParseEnum(row.DecidedBy, AuditActorKind.System),
            DecidedAtUtc = new DateTimeOffset(row.DecidedAtUtc, TimeSpan.Zero),
        };
    }

    /// <summary>Rebuild the risk-of-bias appraisal, or null when none was recorded.</summary>
    public static RiskOfBiasAssessment? ToBias(AcademicScreeningEntity row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.BiasJson)) return null;
        var parsed = Deserialize<BiasRow>(row.BiasJson);
        if (parsed is null || string.IsNullOrEmpty(parsed.framework)) return null;
        return new RiskOfBiasAssessment
        {
            SourceId = row.SourceId,
            WorkspaceId = row.WorkspaceId,
            Framework = parsed.framework,
            StudyDesign = parsed.studyDesign ?? "unspecified",
            OverallRisk = ParseEnum(parsed.overall, OverallBiasRisk.Unclear),
            AnsweredCoverage = parsed.coverage,
            Domains = (parsed.domains ?? []).Select(d => new BiasDomainFinding
            {
                DomainCode = d.domain,
                Judgement = ParseEnum(d.judgement, BiasJudgement.NoInformation),
                Rationale = d.rationale,
                Source = ParseEnum(d.source, BiasJudgementSource.Unanswered),
            }).ToList(),
            Flags = (parsed.flags ?? []).Select(ToFlag).ToList(),
            AssessedAtUtc = new DateTimeOffset(row.ScreenedAtUtc, TimeSpan.Zero),
        };
    }

    public static string Serialize(IEnumerable<object> values)
    {
        var list = values.ToList();
        return list.Count == 0
            ? "[]"
            : ProvenanceAuditChain.Canonicalize(list);
    }

    /// <summary>Serialize one value as canonical JSON.</summary>
    public static string SerializeSingle(object value) => ProvenanceAuditChain.Canonicalize(value);

    private static object FlagJson(EvidenceFlag flag) => new
    {
        code = flag.Code,
        severity = flag.Severity.ToString(),
        message = flag.Message,
    };

    private static EvidenceFlag ToFlag(FlagRow row) => new()
    {
        Code = row.code,
        Severity = ParseEnum(row.severity, FindingSeverity.Note),
        Message = row.message,
    };

    private static string Serialize<T>(IEnumerable<T> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "[]" : ProvenanceAuditChain.Canonicalize(list);
    }

    private static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default!;
        return JsonSerializer.Deserialize<T>(json, JsonOptions)!;
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private sealed record AuthorRow(
        string display,
        string family,
        string? given,
        string? orcid,
        string? affiliation,
        string role,
        string? key);

    private sealed record IdentifierRow(
        string kind,
        string raw,
        string? normalized,
        bool wellFormed,
        string? reason,
        string origin,
        string? prefix,
        long observed);

    private sealed record FieldRow(
        string field,
        string? value,
        string[]? origins,
        string[]? conflicts,
        string? identifierOrigin,
        long observed);

    private sealed record NoticeRow(
        string kind,
        string? noticeDoi,
        string? noticeUrl,
        string? notifiedBy,
        string origin,
        long noticed);

    private sealed record ClaimRow(
        string subject,
        string predicate,
        string direction,
        double? magnitude,
        string? unit,
        double? low,
        double? high,
        string? population,
        string? timeframe);

    private sealed record FlagRow(string code, string severity, string message);
    private sealed record CriterionRow(
        string code,
        string outcome,
        double weight,
        double earned,
        string? origin,
        string? note);
    private sealed record BiasDomainRow(
        string domain,
        string judgement,
        string? rationale,
        string source);
    private sealed record BiasRow(
        string framework,
        string? studyDesign,
        string overall,
        double coverage,
        BiasDomainRow[]? domains,
        FlagRow[]? flags);

    private static string FormatDouble(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}

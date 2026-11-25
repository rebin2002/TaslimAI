using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Provenance and retraction contracts. The behaviour that matters here is the
/// negative one: a record must not be able to earn a "verified" badge from a single
/// provider hit, from an identifier a model produced, or from the same corpus
/// queried twice.
/// </summary>
public sealed class ProvenanceAndRetractionTests
{
    [Fact]
    public void Two_independent_registry_origins_agreeing_on_doi_and_title_are_verified()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var provenance = ProvenanceVerifier.Verify(
            pair.Record, pair.Normalization, notices: null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(ProvenanceVerdict.Verified, provenance.Verdict);
        Assert.Equal(2, provenance.CorroboratingOriginCount);
        Assert.True(provenance.MayPresentAsVerified);
        Assert.Contains(ProvenanceReasonCodes.Corroborated, provenance.ReasonCodes);
        Assert.False(provenance.ReasonCodes.Contains(ProvenanceReasonCodes.SingleOrigin));
    }

    [Fact]
    public void One_registry_hit_is_unverified_however_clean_the_doi_is()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001"),
            [AcademicEvidenceFixtures.Origin()],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(ProvenanceVerdict.Unverified, provenance.Verdict);
        Assert.False(provenance.MayPresentAsVerified);
        Assert.Contains(ProvenanceReasonCodes.SingleOrigin, provenance.ReasonCodes);
    }

    [Fact]
    public void The_same_corpus_queried_twice_is_not_two_origins()
    {
        // Same OriginKey, two records: corroboration counts distinct origins, and a
        // single database cannot corroborate itself.
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"),
            [
                AcademicEvidenceFixtures.Origin("crossref", observedAtUtc: AcademicEvidenceFixtures.Clock.AddHours(-2)),
                AcademicEvidenceFixtures.Origin("crossref", observedAtUtc: AcademicEvidenceFixtures.Clock.AddHours(-1)),
            ],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(1, provenance.CorroboratingOriginCount);
        Assert.Equal(ProvenanceVerdict.Unverified, provenance.Verdict);
    }

    [Fact]
    public void Identifiers_a_model_asserted_never_certify_provenance()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000003"),
            [
                AcademicEvidenceFixtures.Origin(
                    "ai-provider-a", identifierOrigin: IdentifierOrigin.ProviderAssertion),
                AcademicEvidenceFixtures.Origin(
                    "ai-provider-b", sourceKindToken: "article",
                    identifierOrigin: IdentifierOrigin.ProviderAssertion, pageRange: null),
            ],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(ProvenanceVerdict.Unverified, provenance.Verdict);
        Assert.Equal(0, provenance.CorroboratingOriginCount);
        Assert.Contains(ProvenanceReasonCodes.IdentifierOriginWeak, provenance.ReasonCodes);
        Assert.False(provenance.MayPresentAsVerified);
    }

    [Fact]
    public void Conflicting_dois_dispute_the_identity_of_the_record()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000004"),
            [
                AcademicEvidenceFixtures.Origin("crossref", doi: AcademicEvidenceFixtures.TestDoi),
                AcademicEvidenceFixtures.Origin("openalex", doi: AcademicEvidenceFixtures.OtherTestDoi),
            ],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(ProvenanceVerdict.Disputed, provenance.Verdict);
        Assert.Contains(ProvenanceReasonCodes.IdentifierConflict, provenance.ReasonCodes);
        Assert.Contains(ProvenanceReasonCodes.CoreFieldConflict, provenance.ReasonCodes);
    }

    [Fact]
    public void Missing_title_invalidates_the_record()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000005"),
            [AcademicEvidenceFixtures.Origin(title: null, pageRange: null)],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.Equal(ProvenanceVerdict.Invalid, provenance.Verdict);
        Assert.Contains(ProvenanceReasonCodes.TitleMissing, provenance.ReasonCodes);
    }

    [Fact]
    public void One_year_apart_between_providers_is_a_note_not_a_dispute()
    {
        // The preprint landed in 2023 and the version of record in 2024; calling
        // that a conflict would block a perfectly citable paper.
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("bbbbbbbb-0000-4000-8000-000000000006"),
            [
                AcademicEvidenceFixtures.Origin("crossref", yearToken: "2024"),
                AcademicEvidenceFixtures.Origin("openalex", yearToken: "2023", pageRange: null),
            ],
            AcademicEvidenceFixtures.Clock);

        var provenance = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);

        Assert.NotEqual(ProvenanceVerdict.Disputed, provenance.Verdict);
        Assert.Contains(normalization.Issues, i => i.Code == "YEAR_ADJACENT_VARIANTS");
        Assert.Equal("2023", provenance.Fields.First(f => f.FieldName == "Year").Value);
    }

    [Fact]
    public void Registry_recorded_retraction_withdraws_a_previously_verified_source()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var notice = new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            NoticeDoi = "10.5555/retraction.2026.004",
            Origin = RetractionNoticeOrigin.Registry,
            NotifiedBy = "Journal of Regional Pharmaceutical Policy",
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };

        var verified = ProvenanceVerifier.Verify(
            pair.Record, pair.Normalization, [notice], AcademicEvidenceFixtures.Clock);
        var screened = RetractionScreen.Screen(verified);

        Assert.Equal(ProvenanceVerdict.Withdrawn, screened.Provenance.Verdict);
        Assert.Equal(RetractionScreenOutcome.Withdrawn, screened.Outcome);
        Assert.False(screened.Provenance.MayPresentAsVerified);
        Assert.False(RetractionScreen.MaySupportClaims(screened.Provenance));
        Assert.Contains(screened.Flags, f => f.Code == "RETRACTION_FULL"
            && f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void A_model_inventing_a_retraction_cannot_delete_a_real_source()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var lead = new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.ProviderAssertion,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };

        var verified = ProvenanceVerifier.Verify(
            pair.Record, pair.Normalization, [lead], AcademicEvidenceFixtures.Clock);

        // The verdict still stands, because nothing admissible supports the claim.
        Assert.Equal(ProvenanceVerdict.Verified, verified.Verdict);
        Assert.Contains(ProvenanceReasonCodes.RetractionUnconfirmed, verified.ReasonCodes);
        Assert.Single(verified.UnconfirmedNoticeLeads);
        Assert.Empty(verified.AdmissibleNotices);

        var screened = RetractionScreen.Screen(verified);
        Assert.Equal(RetractionScreenOutcome.UnconfirmedLead, screened.Outcome);
        Assert.Equal(ProvenanceVerdict.Verified, screened.Provenance.Verdict);
        Assert.True(RetractionScreen.MaySupportClaims(screened.Provenance));
    }

    [Fact]
    public void An_expression_of_concern_flags_a_source_without_removing_it()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var notice = new RetractionNotice
        {
            Kind = RetractionKind.ExpressionOfConcern,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };

        var verified = ProvenanceVerifier.Verify(
            pair.Record, pair.Normalization, [notice], AcademicEvidenceFixtures.Clock);
        var screened = RetractionScreen.Screen(verified);

        Assert.Equal(ProvenanceVerdict.Verified, screened.Provenance.Verdict);
        Assert.Equal(RetractionScreenOutcome.Flagged, screened.Outcome);
        Assert.True(RetractionScreen.MaySupportClaims(screened.Provenance));
        Assert.Contains(screened.Flags, f => f.Code == "RETRACTION_CONCERN");
    }

    [Fact]
    public void May_present_as_verified_survives_only_on_real_corroboration()
    {
        // A hand-built provenance that claims the badge without the evidence for it
        // must still refuse to render it.
        var hollow = new SourceProvenance
        {
            SourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-0000000000aa"),
            WorkspaceId = AcademicEvidenceFixtures.Workspace,
            Verdict = ProvenanceVerdict.Verified,
            CorroboratingOriginCount = 1,
        };

        Assert.False(hollow.MayPresentAsVerified);

        var whole = hollow with { CorroboratingOriginCount = 2 };
        Assert.True(whole.MayPresentAsVerified);
    }

    [Fact]
    public void Reverify_downgrades_a_verified_source_when_evidence_is_lost()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var before = ProvenanceVerifier.Verify(pair.Record, pair.Normalization, null, AcademicEvidenceFixtures.Clock);
        Assert.Equal(ProvenanceVerdict.Verified, before.Verdict);

        // A rescreen that only has the original hit left can no longer certify it.
        var thinned = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace, pair.Record.SourceId,
            [AcademicEvidenceFixtures.Origin(abstractText: AcademicEvidenceFixtures.AbstractText)],
            AcademicEvidenceFixtures.Clock);

        var after = ProvenanceVerifier.Reverify(
            thinned.Record, thinned, before, [], AcademicEvidenceFixtures.Clock.AddHours(1));

        Assert.Equal(ProvenanceVerdict.Unverified, after.Verdict);
        Assert.Contains(ProvenanceReasonCodes.DowngradedFromVerified, after.ReasonCodes);
    }

    [Fact]
    public void Audit_chain_links_entries_and_detects_tampering()
    {
        var sourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000007");
        var chain = new ProvenanceAuditChain();

        var first = chain.Append(sourceId, AcademicEvidenceFixtures.Workspace,
            ProvenanceAuditKinds.RecordNormalized,
            new { verdict = "Unverified", title = "Formulary retention" },
            AcademicEvidenceFixtures.Clock);

        var second = chain.Append(sourceId, AcademicEvidenceFixtures.Workspace,
            ProvenanceAuditKinds.VerdictComputed,
            new { verdict = "Verified", corroboratingOrigins = 2 },
            AcademicEvidenceFixtures.Clock.AddMinutes(1));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.EntryHash, second.PreviousHash);
        Assert.True(chain.IsValid(out var failure));
        Assert.Null(failure);

        // Rewriting a payload after the fact breaks the recomputed hash, and a
        // spliced or reordered list is rejected when the trail is read back.
        var forged = chain.Events[0] with { PayloadCanonical = "{\"verdict\":\"Verified\"}" };
        Assert.False(ProvenanceAuditChain.Validate([forged, chain.Events[1]], out var tamperFailure));
        Assert.Equal("payload altered at 1", tamperFailure);

        var reordered = new[] { chain.Events[1], chain.Events[0] };
        Assert.False(ProvenanceAuditChain.Validate(reordered, out var orderFailure));
        Assert.Equal("sequence gap at 1", orderFailure);

        // Sequences intact, link cut: an event lifted out of another trail.
        var spliced = chain.Events[1] with { PreviousHash = new string('0', 64) };
        Assert.False(ProvenanceAuditChain.Validate([chain.Events[0], spliced], out var linkFailure));
        Assert.Equal("broken predecessor link at 2", linkFailure);
    }

    [Fact]
    public void Canonical_payload_hashing_is_independent_of_property_order()
    {
        var left = ProvenanceAuditChain.Canonicalize(new { alpha = 1, beta = "x" });
        var right = ProvenanceAuditChain.Canonicalize(new Dictionary<string, object>
        {
            ["beta"] = "x",
            ["alpha"] = 1,
        });

        Assert.Equal("{\"alpha\":1,\"beta\":\"x\"}", left);
        Assert.Equal(left, right);
    }

    [Fact]
    public void Access_level_policy_separates_metadata_from_content()
    {
        var metadata = AccessLevelPolicy.Evaluate(SourceAccessLevel.MetadataOnly);
        Assert.True(metadata.MayCite);
        Assert.True(metadata.MayBibliograph);
        Assert.False(metadata.MayQuote);
        Assert.False(metadata.MayParaphrase);
        Assert.False(metadata.MayAssertPageNumbers);
        Assert.Contains(AccessLevelPolicy.MetadataOnlyReason, metadata.BlockingReasons);

        var abstractOnly = AccessLevelPolicy.Evaluate(SourceAccessLevel.Abstract);
        Assert.True(abstractOnly.MayQuote);
        Assert.False(abstractOnly.MayAssertPageNumbers);

        var fullText = AccessLevelPolicy.Evaluate(SourceAccessLevel.FullText);
        Assert.True(fullText.MayQuote);
        Assert.True(fullText.MayAssertPageNumbers);
        Assert.Empty(fullText.BlockingReasons);
    }

    [Fact]
    public void Reconcile_cannot_upgrade_a_level_the_platform_has_no_text_for()
    {
        var record = new NormalizedBibliographicRecord
        {
            SourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000008"),
            WorkspaceId = AcademicEvidenceFixtures.Workspace,
            Title = "Formulary retention",
            AccessLevel = SourceAccessLevel.FullText,
            AbstractText = null,
            FullText = null,
        };

        Assert.Equal(SourceAccessLevel.MetadataOnly, AccessLevelPolicy.Reconcile(SourceAccessLevel.FullText, record));

        var withAbstract = record with { AbstractText = AcademicEvidenceFixtures.AbstractText };
        Assert.Equal(SourceAccessLevel.Abstract, AccessLevelPolicy.Reconcile(SourceAccessLevel.FullText, withAbstract));
    }

    [Fact]
    public void Fingerprint_treats_a_preprint_and_its_version_of_record_as_related_not_duplicate()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(sourceId: Guid.Parse("bbbbbbbb-0000-4000-8000-000000000009"));

        var preprint = pair.Record with
        {
            SourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000a"),
            SourceKind = AcademicSourceKind.Preprint,
            Identifiers = [new BibliographicIdentifier
            {
                Kind = IdentifierKind.Doi,
                RawValue = "10.5555/preprint.2023.001",
                NormalizedValue = "10.5555/preprint.2023.001",
                IsWellFormed = true,
                Origin = IdentifierOrigin.RegistryResponse,
            }],
        };

        var candidate = BibliographicFingerprint.Compare(preprint, pair.Record);

        Assert.Equal(DuplicateRelation.PreprintOfVersionOfRecord, candidate.Relation);
        Assert.Equal(DuplicateConfidence.Certain, candidate.Confidence);
    }

    [Fact]
    public void Same_title_different_first_author_is_not_a_duplicate()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(sourceId: Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b"));

        var other = pair.Record with
        {
            SourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000c"),
            Identifiers = [],
            Authors = [],
        };

        var candidate = BibliographicFingerprint.Compare(pair.Record, other);
        Assert.Equal(DuplicateConfidence.Distinct, candidate.Confidence);
    }

    [Fact]
    public void Shared_doi_makes_two_rows_the_same_work()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady(sourceId: Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000d"));

        var twin = pair.Record with { SourceId = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000e") };

        var candidate = BibliographicFingerprint.Compare(pair.Record, twin);

        Assert.Equal(DuplicateConfidence.Certain, candidate.Confidence);
        Assert.Equal(DuplicateRelation.SameWork, candidate.Relation);
        Assert.True(BibliographicFingerprint.SharesIdentifier(pair.Record, twin));
        Assert.Contains("doi:" + AcademicEvidenceFixtures.TestDoi, candidate.EvidenceKeys);
    }
}

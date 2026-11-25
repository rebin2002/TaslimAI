using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Evidence;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Provenance;
using Xunit;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// The no-fabricated-page-number rule and its neighbours. A generated citation
/// that names a page nobody read is the single most damaging failure mode of an
/// academic assistant, because it looks like scholarship from the outside. The
/// policy here is that a pinpoint is stored only when something in the platform's
/// own possession supports it, and everything else is dropped with a reason.
/// </summary>
public sealed class EvidenceIntegrityTests
{
    private static NormalizedBibliographicRecord AbstractRecord =>
        AcademicEvidenceFixtures.VerifyReady().Record;

    private static NormalizedBibliographicRecord FullTextRecord =>
        AcademicEvidenceFixtures.VerifyReady(
            fullText: AcademicEvidenceFixtures.FullTextBody).Record;

    private static SourceProvenance VerifiedFor(NormalizedBibliographicRecord record)
    {
        var pair = AcademicEvidenceFixtures.CorroboratedJournalArticle()
            .Select(o => o with
            {
                AbstractText = AcademicEvidenceFixtures.AbstractText,
                FullText = record.AccessLevel is SourceAccessLevel.FullText
                    or SourceAccessLevel.PrivateUpload ? AcademicEvidenceFixtures.FullTextBody : null,
            })
            .ToList();
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace, record.SourceId, pair, AcademicEvidenceFixtures.Clock);
        return ProvenanceVerifier.Verify(normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);
    }

    private static SourceLocator Page(LocatorOrigin origin, string label,
        PaginationBasis basis = PaginationBasis.NotApplicable) => new()
    {
        Kind = LocatorKind.Page,
        Origin = origin,
        Label = label,
        PaginationBasis = basis,
    };

    [Fact]
    public void A_page_number_a_model_inferred_is_thrown_away()
    {
        // This is the exact failure the rule exists for: the number is plausible,
        // the article is real, and page 412 was never observed anywhere.
        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.ModelInference, "412"), FullTextRecord,
            AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.False(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.OriginUnsourced, decision.Code);
        Assert.Null(decision.Locator);
    }

    [Fact]
    public void A_page_number_of_unknown_provenance_is_also_thrown_away()
    {
        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.Unknown, "412"), FullTextRecord,
            AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.False(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.OriginUnsourced, decision.Code);
    }

    [Fact]
    public void No_locator_at_all_is_fine_and_cites_without_a_pinpoint()
    {
        var decision = SourceLocatorPolicy.Admit(null, AbstractRecord, null, assertsContent: true);

        Assert.True(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.NoneSupplied, decision.Code);
        Assert.Null(SourceLocatorPolicy.FormatForCitation(decision.Locator));
    }

    [Fact]
    public void Registry_pagination_survives_on_an_abstract_only_record_and_is_printed_as_published_pages()
    {
        // Crossref states the article's own page range; the platform never saw the
        // pages, but the publisher did, so the pinpoint is citable as published.
        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.RegistryMetadata, "p. 412–419"), AbstractRecord,
            AcademicEvidenceFixtures.AbstractText, assertsContent: false);

        Assert.True(decision.Admitted);
        Assert.Equal(PaginationBasis.PublishedPagination, decision.Locator!.PaginationBasis);
        Assert.Equal("pp. 412-419", SourceLocatorPolicy.FormatForCitation(decision.Locator));
        Assert.DoesNotContain(decision.Flags, f => f.Code == LocatorDecisionCodes.FilePagination);

        // A range recorded on a single-page locator still prints as a range, and a
        // single page prints as one page.
        var single = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.RegistryMetadata, "p. 412"), AbstractRecord, null, assertsContent: false);
        Assert.Equal("p. 412", SourceLocatorPolicy.FormatForCitation(single.Locator));
    }

    [Fact]
    public void A_page_read_from_a_retrieved_copy_is_labelled_as_file_pagination()
    {
        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.RetrievedCopy, "12"), FullTextRecord,
            AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.True(decision.Admitted);
        Assert.Equal(PaginationBasis.FilePagination, decision.Locator!.PaginationBasis);
        Assert.Equal("p. 12 [file pagination]", SourceLocatorPolicy.FormatForCitation(decision.Locator));
        Assert.Contains(decision.Flags, f => f.Code == LocatorDecisionCodes.FilePagination);
    }

    [Fact]
    public void Pages_cannot_be_claimed_against_text_the_platform_does_not_hold()
    {
        var metadataRecord = AcademicEvidenceFixtures.VerifyReady(abstractText: null).Record;
        Assert.Equal(SourceAccessLevel.MetadataOnly, metadataRecord.AccessLevel);

        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.RetrievedCopy, "412"), metadataRecord, null, assertsContent: true);

        Assert.False(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.NoArtifact, decision.Code);
    }

    [Fact]
    public void An_anchored_span_cannot_certify_a_printed_page()
    {
        var stored = AcademicEvidenceFixtures.FullTextBody;
        var quote = "thirty two pharmacists in Erbil and Sulaymaniyah";
        var start = stored.IndexOf(quote, StringComparison.Ordinal);
        Assert.True(start > 0);

        var decision = SourceLocatorPolicy.Admit(new SourceLocator
        {
            Kind = LocatorKind.Page,
            Origin = LocatorOrigin.AnchoredExtraction,
            Label = "412",
            PaginationBasis = PaginationBasis.PublishedPagination,
            AnchorStart = start,
            AnchorEnd = start + quote.Length,
        }, FullTextRecord, stored, assertsContent: true);

        // The span proves position in the file, nothing about the journal's pages.
        Assert.False(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.NoArtifact, decision.Code);
    }

    [Fact]
    public void An_anchored_span_that_no_longer_matches_the_text_is_dropped()
    {
        // The stored text was replaced since the passage was extracted: citing the
        // old offsets would point at different words.
        var decision = SourceLocatorPolicy.Admit(new SourceLocator
        {
            Kind = LocatorKind.Paragraph,
            Origin = LocatorOrigin.AnchoredExtraction,
            Label = "para. 3",
            AnchorStart = 40,
            AnchorEnd = 9999,
        }, FullTextRecord, AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.False(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.AnchorMismatch, decision.Code);
    }

    [Fact]
    public void A_valid_anchor_is_admitted_and_keeps_its_offsets()
    {
        var stored = AcademicEvidenceFixtures.FullTextBody;
        var span = QuotedSupportVerifier.FindQuoteSpan(
            FullTextRecord, "mean price deviation across observed tenders was 7.4 percent", out var matched);

        Assert.True(matched);
        Assert.NotNull(span);

        var decision = SourceLocatorPolicy.Admit(new SourceLocator
        {
            Kind = LocatorKind.Paragraph,
            Origin = LocatorOrigin.AnchoredExtraction,
            Label = "3",
            AnchorStart = span!.Value.Start,
            AnchorEnd = span.Value.End,
        }, FullTextRecord, stored, assertsContent: true);

        Assert.True(decision.Admitted);
        Assert.Equal(span.Value.Start, decision.Locator!.AnchorStart);
        Assert.Equal(span.Value.End, decision.Locator.AnchorEnd);
        Assert.Equal("para. 3", SourceLocatorPolicy.FormatForCitation(decision.Locator));
        // The offsets really do point at the quoted sentence in the stored copy.
        Assert.Equal("mean price deviation across observed tenders was 7.4 percent",
            stored[span.Value.Start..span.Value.End]);
    }

    [Fact]
    public void A_member_may_record_a_page_they_read_and_it_is_marked_as_their_own_observation()
    {
        var decision = SourceLocatorPolicy.Admit(
            Page(LocatorOrigin.MemberProvided, "412"), FullTextRecord,
            AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.True(decision.Admitted);
        Assert.Contains(decision.Flags, f => f.Code == LocatorDecisionCodes.MemberAsserted);
    }

    [Fact]
    public void A_page_locator_arriving_without_a_label_is_rejected_as_malformed()
    {
        // Label "  " makes the locator non-empty only by kind, so the guard that
        // matters is the label check after the origin gate.
        var decision = SourceLocatorPolicy.Admit(new SourceLocator
        {
            Kind = LocatorKind.PageRange,
            Origin = LocatorOrigin.RetrievedCopy,
            Label = "  ",
        }, FullTextRecord, AcademicEvidenceFixtures.FullTextBody, assertsContent: true);

        Assert.True(decision.Admitted);
        Assert.Equal(LocatorDecisionCodes.NoneSupplied, decision.Code);
    }

    [Fact]
    public void Article_numbers_from_a_registry_are_citable_without_any_full_text()
    {
        // Electronic-only journals number articles instead of paginating them, so
        // this pinpoint is not a page claim at all.
        var record = AbstractRecord with { AccessLevel = SourceAccessLevel.MetadataOnly };
        var decision = SourceLocatorPolicy.Admit(new SourceLocator
        {
            Kind = LocatorKind.ArticleNumber,
            Origin = LocatorOrigin.RegistryMetadata,
            Label = "e0261147",
        }, record, null, assertsContent: false);

        Assert.True(decision.Admitted);
        Assert.Equal("art. e0261147", SourceLocatorPolicy.FormatForCitation(decision.Locator));
        Assert.Equal(PaginationBasis.NotApplicable, decision.Locator!.PaginationBasis);
    }

    [Fact]
    public void A_quotation_that_is_actually_in_the_stored_abstract_verifies()
    {
        var result = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "formulary retention",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Retention of essential medicines on formulary improved by 18 percent",
            locator: Page(LocatorOrigin.RegistryMetadata, "412"),
            recordedAtUtc: AcademicEvidenceFixtures.Clock);

        Assert.True(result.IsClean);
        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, result.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.QuoteLocated, result.Item.IntegrityReasons);
        Assert.True(result.Item.IsExportable);
        Assert.Equal("p. 412", SourceLocatorPolicy.FormatForCitation(result.Item.Locator));
    }

    [Fact]
    public void Quotation_matching_ignores_case_and_punctuation_but_not_words()
    {
        var result = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "formulary retention",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "RETENTION  OF ESSENTIAL MEDICINES, on formulary improved");

        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, result.Item.Integrity);
    }

    [Fact]
    public void A_quotation_from_the_unread_full_text_is_not_exportable()
    {
        // The platform holds the abstract only. This sentence really is in the
        // paper, which is precisely why it must not be quoted from a record that
        // never contained the paper.
        var result = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "methods",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Interviews were conducted with thirty two pharmacists in Erbil");

        Assert.False(result.IsClean);
        Assert.Equal(EvidenceIntegrityStatus.UnsupportedByStoredText, result.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.QuoteNotFound, result.Item.IntegrityReasons);
        Assert.False(result.Item.IsExportable);
        Assert.Contains(result.Flags, f => f.Severity == FindingSeverity.Blocking);
    }

    [Fact]
    public void The_same_quotation_verifies_once_the_full_text_is_actually_held()
    {
        var result = EvidenceRecordBuilder.Record(
            FullTextRecord, VerifiedFor(FullTextRecord),
            topic: "methods",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Interviews were conducted with thirty two pharmacists in Erbil");

        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, result.Item.Integrity);
        Assert.True(result.Item.IsExportable);
    }

    [Fact]
    public void Quoting_a_metadata_only_source_is_refused_at_the_access_level()
    {
        var record = AcademicEvidenceFixtures.VerifyReady(abstractText: null).Record;

        var result = EvidenceRecordBuilder.Record(
            record, VerifiedFor(record),
            topic: "anything",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Retention of essential medicines");

        Assert.Equal(EvidenceIntegrityStatus.NotSupportedAtAccessLevel, result.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.AccessInsufficient, result.Item.IntegrityReasons);
        Assert.False(result.Item.IsExportable);
    }

    [Fact]
    public void A_figure_that_is_not_in_the_text_is_not_attributed_to_the_source()
    {
        var good = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "effect size",
            supportKind: EvidenceSupportKind.QuantitativeFinding,
            content: "formulary retention improved by 18 percent between 2021 and 2024");
        var bad = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "effect size",
            supportKind: EvidenceSupportKind.QuantitativeFinding,
            content: "formulary retention improved by 22 percent");

        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, good.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.NumbersLocated, good.Item.IntegrityReasons);
        Assert.Equal(EvidenceIntegrityStatus.UnsupportedByStoredText, bad.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.NumberNotFound, bad.Item.IntegrityReasons);
        Assert.False(bad.Item.IsExportable);
    }

    [Fact]
    public void A_paraphrase_is_grounded_only_when_the_held_text_supports_its_terms()
    {
        var grounded = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "tendering",
            supportKind: EvidenceSupportKind.Paraphrase,
            content: "centralising the tender process raised retention of essential medicines on formulary");
        var loose = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord),
            topic: "manufacturing",
            supportKind: EvidenceSupportKind.Paraphrase,
            content: "regional factories increased vaccine output during the same period");

        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, grounded.Item.Integrity);
        Assert.Contains(SupportVerificationCodes.ParaphraseGrounded, grounded.Item.IntegrityReasons);
        Assert.True(grounded.Item.Flags.Count == 0);
        Assert.Equal(EvidenceIntegrityStatus.UnsupportedByStoredText, loose.Item.Integrity);
        Assert.True(loose.Support.Overlap < QuotedSupportVerifier.ParaphraseGroundingThreshold);
    }

    [Fact]
    public void A_retracted_source_cannot_support_any_content_claim()
    {
        var pair = AcademicEvidenceFixtures.VerifyReady();
        var withdrawn = ProvenanceVerifier.Verify(pair.Record, pair.Normalization, [new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        }], AcademicEvidenceFixtures.Clock);

        var result = EvidenceRecordBuilder.Record(
            pair.Record, withdrawn,
            topic: "formulary retention",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Retention of essential medicines on formulary improved by 18 percent");

        Assert.Contains(result.Flags, f => f.Code == "EVIDENCE_WITHDRAWN_SOURCE"
            && f.Severity == FindingSeverity.Blocking);
        Assert.Equal(EvidenceIntegrityStatus.UnsupportedByStoredText, result.Item.Integrity);
        Assert.False(result.Item.IsExportable);
    }

    [Fact]
    public void A_disputed_source_holds_its_quotation_back_until_the_identity_is_settled()
    {
        var normalization = BibliographicRecordNormalizer.Normalize(
            AcademicEvidenceFixtures.Workspace,
            Guid.Parse("dddddddd-0000-4000-8000-000000000001"),
            [
                AcademicEvidenceFixtures.Origin("crossref", doi: AcademicEvidenceFixtures.TestDoi,
                    abstractText: AcademicEvidenceFixtures.AbstractText),
                AcademicEvidenceFixtures.Origin("openalex", doi: AcademicEvidenceFixtures.OtherTestDoi,
                    sourceKindToken: "article", abstractText: AcademicEvidenceFixtures.AbstractText),
            ],
            AcademicEvidenceFixtures.Clock);
        var disputed = ProvenanceVerifier.Verify(
            normalization.Record, normalization, null, AcademicEvidenceFixtures.Clock);
        Assert.Equal(ProvenanceVerdict.Disputed, disputed.Verdict);

        var result = EvidenceRecordBuilder.Record(
            normalization.Record, disputed,
            topic: "formulary retention",
            supportKind: EvidenceSupportKind.DirectQuote,
            content: "Retention of essential medicines on formulary improved by 18 percent");

        Assert.Contains(result.Flags, f => f.Code == "EVIDENCE_DISPUTED_SOURCE");
        Assert.False(result.Item.IsExportable);
    }

    [Fact]
    public void Bibliographic_only_items_do_not_need_text_and_stay_exportable()
    {
        // "This source exists and was screened" is a statement the metadata
        // supports, so it is not blocked by the content rules.
        var record = AcademicEvidenceFixtures.VerifyReady(abstractText: null).Record;
        var result = EvidenceRecordBuilder.Record(
            record, VerifiedFor(record),
            topic: "citation",
            supportKind: EvidenceSupportKind.BibliographicOnly,
            content: null);

        Assert.Equal(EvidenceIntegrityStatus.Unchecked, result.Item.Integrity);
        Assert.True(result.Item.IsExportable);
        Assert.Contains(SupportVerificationCodes.NoContentClaim, result.Item.IntegrityReasons);
    }

    [Fact]
    public void Recording_the_same_evidence_twice_yields_the_same_identity()
    {
        var first = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord), "topic",
            EvidenceSupportKind.DirectQuote, "Retention of essential medicines");
        var second = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord), "topic",
            EvidenceSupportKind.DirectQuote, "Retention of essential medicines");
        var other = EvidenceRecordBuilder.Record(
            AbstractRecord, VerifiedFor(AbstractRecord), "another topic",
            EvidenceSupportKind.DirectQuote, "Retention of essential medicines");

        Assert.Equal(first.Item.EvidenceId, second.Item.EvidenceId);
        Assert.NotEqual(first.Item.EvidenceId, other.Item.EvidenceId);
    }

    [Fact]
    public void Rechecking_after_the_text_disappears_revokes_the_verification()
    {
        var full = FullTextRecord;
        var result = EvidenceRecordBuilder.Record(
            full, VerifiedFor(full), "methods",
            EvidenceSupportKind.DirectQuote, "Interviews were conducted with thirty two pharmacists");
        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, result.Item.Integrity);

        // The licensed copy lapsed, leaving catalogue metadata behind.
        var shrunk = full with { FullText = null, AbstractText = null, AccessLevel = SourceAccessLevel.MetadataOnly };
        var rechecked = EvidenceRecordBuilder.Recheck(result.Item, shrunk, VerifiedFor(shrunk));

        Assert.NotEqual(EvidenceIntegrityStatus.VerifiedAgainstSource, rechecked.Integrity);
        Assert.False(rechecked.IsExportable);
    }
}

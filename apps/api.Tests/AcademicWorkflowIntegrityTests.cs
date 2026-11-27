using Taslim.Api.AcademicWorkflow;
using Xunit;

namespace Taslim.Api.Tests;

/// <summary>
/// The fabrication firewall. These fixtures prove the guard blocks: invented identifiers,
/// quotations from metadata-only sources, unverifiable "abstract" quotes, page numbers
/// without full text, citations of excluded or retracted works, and off-outline drafts —
/// while still permitting honest bibliographic use of low-access sources.
/// </summary>
public sealed class AcademicWorkflowIntegrityTests
{
    [Theory]
    [InlineData("10.1001/kurdhj.2020.3617", true)]
    [InlineData("10.1000/elasticity.2022", true)]
    [InlineData("10.12345678/nested.1", true)]
    [InlineData("doi:10.1001/prefixed", false)]
    [InlineData("10.1/not-enough-digits", false)]
    [InlineData("11.1000/wrong-prefix", false)]
    [InlineData("", false)]
    public void Doi_syntax_is_enforced(string value, bool expected) =>
        Assert.Equal(expected, AcademicIdentifierValidator.IsValidDoi(value));

    [Theory]
    // Checksum-valid ISBNs.
    [InlineData("9780198808732", true)]
    [InlineData("978-0-19-880873-2", true)]
    [InlineData("0306406152", true)]
    [InlineData("080442957X", true)]
    // Checksum-invalid or malformed ISBNs must never be minted into citations.
    [InlineData("9780198808738", false)]
    [InlineData("0306406153", false)]
    [InlineData("97801988087", false)]
    [InlineData("not-an-isbn", false)]
    public void Isbn_checksum_is_enforced(string value, bool expected) =>
        Assert.Equal(expected, AcademicIdentifierValidator.IsValidIsbn(value));

    [Theory]
    [InlineData("2001.00001", true)]
    [InlineData("arXiv:2001.00001v2", true)]
    [InlineData("hep-th/9901001", true)]
    [InlineData("banana/99", false)]
    public void ArXiv_syntax_is_enforced(string value, bool expected) =>
        Assert.Equal(expected, AcademicIdentifierValidator.IsValidArXiv(value));

    [Theory]
    [InlineData("12345678", true)]
    [InlineData("0123456", false)]
    [InlineData("abc", false)]
    public void Pmid_syntax_is_enforced(string value, bool expected) =>
        Assert.Equal(expected, AcademicIdentifierValidator.IsValidPmid(value));

    [Theory]
    [InlineData("https://example.gov/report", true)]
    [InlineData("http://example.org/a/b", true)]
    [InlineData("https://user:secret@evil.example/report", false)]
    [InlineData("ftp://example.gov/report", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    public void Landing_urls_must_be_absolute_and_credential_free(string value, bool expected) =>
        Assert.Equal(expected, AcademicIdentifierValidator.IsValidLandingUrl(value));

    [Fact]
    public void Access_rules_gate_content_quotes_and_locators()
    {
        var metadata = Source(AcademicAccessLevel.MetadataOnly);
        var abstracted = Source(AcademicAccessLevel.Abstract);
        var fullText = Source(AcademicAccessLevel.FullText);

        Assert.False(AcademicAccessRules.MayCarryContent(metadata.AccessLevel));
        Assert.True(AcademicAccessRules.MayCarryContent(abstracted.AccessLevel));
        Assert.True(AcademicAccessRules.MayCarryContent(fullText.AccessLevel));

        Assert.False(AcademicAccessRules.MayCarryLocator(metadata.AccessLevel));
        Assert.False(AcademicAccessRules.MayCarryLocator(abstracted.AccessLevel));
        Assert.True(AcademicAccessRules.MayCarryLocator(fullText.AccessLevel));

        // Abstract quoting is containment-checked, whitespace and case normalized.
        Assert.True(AcademicAccessRules.MayQuote(abstracted, "regulation of essential medicines"));
        Assert.True(AcademicAccessRules.MayQuote(abstracted, "REGULATION   OF ESSENTIAL MEDICINES"));
        Assert.False(AcademicAccessRules.MayQuote(abstracted, "a claim the abstract never makes"));
        Assert.False(AcademicAccessRules.MayQuote(metadata, "anything at all"));
    }

    [Fact]
    public void Golden_artifact_set_produces_no_blocking_issues()
    {
        var blocking = AcademicIntegrityGuard.BlockingIssues(GoldenSnapshot());

        Assert.Empty(blocking);
    }

    [Fact]
    public void Metadata_only_source_cannot_carry_content_evidence()
    {
        var snapshot = GoldenSnapshot();
        snapshot.Evidence.Add(new AcademicEvidenceItem
        {
            EvidenceId = "ev-bad-1",
            SourceId = AcademicWorkflowFixtures.S3,
            Topic = "Invented paragraph",
            Content = "The report states coverage rose 18 percent in 2021.",
            Verbatim = false,
        });

        AssertFinding(AcademicIntegrityGuard.Validate(snapshot), "EVT_METADATA_ONLY", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Verbatim_quote_must_be_found_in_the_licensed_abstract_text()
    {
        var snapshot = GoldenSnapshot();
        snapshot.Evidence.Add(new AcademicEvidenceItem
        {
            EvidenceId = "ev-bad-2",
            SourceId = AcademicWorkflowFixtures.S2,
            Topic = "Fabricated quotation",
            Content = "Trial elasticity estimates prove uniform regional pricing.",
            Verbatim = true,
        });

        AssertFinding(AcademicIntegrityGuard.Validate(snapshot), "EVT_QUOTE_UNVERIFIED", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Page_locators_require_full_text_access_and_are_never_invented()
    {
        var snapshot = GoldenSnapshot();
        snapshot.Evidence.Add(new AcademicEvidenceItem
        {
            EvidenceId = "ev-bad-3",
            SourceId = AcademicWorkflowFixtures.S2,
            Topic = "Ghost page reference",
            Content = "Regional regulation of essential medicines diverges.",
            Verbatim = false,
            Locator = "pp. 118-119",
        });

        AssertFinding(AcademicIntegrityGuard.Validate(snapshot), "EVT_LOCATOR_REQUIRES_FULLTEXT", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Citations_must_resolve_to_screened_in_sources()
    {
        var snapshot = GoldenSnapshot();
        ReplaceChapterBlock(snapshot, block => block.Kind == AcademicBlockKind.Paragraph
            ? block with { CitationSourceIds = [AcademicWorkflowFixtures.S4], Quotes = [] } // retracted, screened out
            : block);

        var issues = AcademicIntegrityGuard.Validate(snapshot);
        AssertFinding(issues, "CHA_CITATION_NOT_INCLUDED", AcademicIssueSeverity.Blocking);

        var unknown = GoldenSnapshot();
        ReplaceChapterBlock(unknown, block => block.Kind == AcademicBlockKind.Paragraph
            ? block with { CitationSourceIds = ["src-does-not-exist"], Quotes = [] }
            : block);
        AssertFinding(AcademicIntegrityGuard.Validate(unknown), "CHA_UNKNOWN_CITATION", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Inline_quotes_follow_the_same_access_firewall_as_evidence()
    {
        var snapshot = GoldenSnapshot();
        ReplaceChapterBlock(snapshot, block => block.Kind == AcademicBlockKind.Paragraph
            ? block with
            {
                Quotes =
                [
                    new AcademicInlineQuote
                    {
                        SourceId = AcademicWorkflowFixtures.S2,
                        QuotedText = "I definitely said this in my book.",
                        Locator = "p. 84",
                    },
                ],
            }
            : block);

        var issues = AcademicIntegrityGuard.Validate(snapshot);
        AssertFinding(issues, "QUO_NOT_IN_ABSTRACT", AcademicIssueSeverity.Blocking);
        AssertFinding(issues, "QUO_LOCATOR_REQUIRES_FULLTEXT", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Retracted_sources_cannot_be_screened_in_and_coverage_must_be_total()
    {
        var includeRetracted = GoldenSnapshot();
        var retractedIndex = includeRetracted.Screening.FindIndex(item => item.SourceId == AcademicWorkflowFixtures.S4);
        includeRetracted.Screening[retractedIndex] = includeRetracted.Screening[retractedIndex] with
        {
            Decision = AcademicScreeningDecision.Included,
            Reason = "Included despite retraction notice (rule violation).",
        };
        AssertFinding(AcademicIntegrityGuard.Validate(includeRetracted), "SCR_INCLUDE_RETRACTED", AcademicIssueSeverity.Blocking);

        var uncovered = GoldenSnapshot();
        uncovered.Screening.RemoveAll(item => item.SourceId == AcademicWorkflowFixtures.S2);
        AssertFinding(AcademicIntegrityGuard.Validate(uncovered), "SCR_COVERAGE", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Discovery_records_are_validated_for_identifiers_access_and_dedup()
    {
        var badDoi = GoldenSnapshot();
        badDoi.Sources[0] = badDoi.Sources[0] with { IdentifierValue = "10.1/not-a-real-registration" };
        AssertFinding(AcademicIntegrityGuard.Validate(badDoi), "SRC_IDENTIFIER_INVALID", AcademicIssueSeverity.Blocking);

        var abstractOnMetadata = GoldenSnapshot();
        abstractOnMetadata.Sources[2] = abstractOnMetadata.Sources[2] with { AbstractText = "Sneaked-in abstract text." };
        AssertFinding(AcademicIntegrityGuard.Validate(abstractOnMetadata), "SRC_ABSTRACT_WITHOUT_ACCESS", AcademicIssueSeverity.Blocking);

        var abstractless = GoldenSnapshot();
        abstractless.Sources[1] = abstractless.Sources[1] with { AbstractText = null };
        AssertFinding(AcademicIntegrityGuard.Validate(abstractless), "SRC_ABSTRACT_MISSING", AcademicIssueSeverity.Blocking);

        var duplicateDedup = GoldenSnapshot();
        duplicateDedup.Sources.Add(duplicateDedup.Sources[0] with { SourceId = "src-copy" });
        AssertFinding(AcademicIntegrityGuard.Validate(duplicateDedup), "SRC_DUPLICATE_DEDUP", AcademicIssueSeverity.Blocking);

        var overLimit = GoldenSnapshot();
        var overLimitSnapshot = GoldenSnapshot(AcademicWorkflowFixtures.Request("over-limit") with { MaxDiscoverySources = 3 });
        AssertFinding(AcademicIntegrityGuard.Validate(overLimitSnapshot), "SRC_OVER_LIMIT", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public void Themes_and_verification_reports_have_their_own_contracts()
    {
        var danglingTheme = GoldenSnapshot();
        danglingTheme.Themes[0] = danglingTheme.Themes[0] with { SupportingEvidenceIds = ["ev-does-not-exist"] };
        AssertFinding(AcademicIntegrityGuard.Validate(danglingTheme), "THM_UNKNOWN_EVIDENCE", AcademicIssueSeverity.Blocking);

        var thinReport = GoldenSnapshot();
        thinReport.Verification = thinReport.Verification! with { ChecksRun = ["citation-resolution"] };
        AssertFinding(AcademicIntegrityGuard.Validate(thinReport), "VFY_CHECKS_INCOMPLETE", AcademicIssueSeverity.Blocking);
    }

    [Fact]
    public async Task Engine_discards_metadata_only_quotation_attempt_then_accepts_repair()
    {
        var harness = new AcademicWorkflowHarness();
        harness.Handlers[AcademicStage.Extract].QueueOutput(new AcademicStageOutput
        {
            Evidence =
            [
                new AcademicEvidenceItem
                {
                    EvidenceId = "ev-fabricated",
                    SourceId = AcademicWorkflowFixtures.S3,
                    Topic = "Quotation from a metadata-only report",
                    Content = "Coverage of essential medicines reached 92 percent of facilities.",
                    Verbatim = true,
                    Locator = "p. 4",
                },
            ],
        });

        await harness.ResumeUntilGateAsync(AcademicApprovalKind.SourceLibrary);
        await harness.ApprovePendingAsync();
        var violation = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);

        Assert.Equal(AcademicWorkflowRunStatus.StageFailure, violation.Status);
        Assert.Equal("ACADEMIC_INTEGRITY_VIOLATION", violation.Code);
        Assert.Contains("metadata-only", violation.Detail);
        Assert.Empty(violation.Snapshot!.Evidence); // the fabricated extraction never reaches durable state

        var repaired = await harness.Orchestrator.ResumeAsync(harness.ProjectId, CancellationToken.None);
        Assert.Equal(AcademicWorkflowRunStatus.AwaitingApproval, repaired.Status);
        Assert.Equal(2, repaired.Snapshot!.Evidence.Count);
    }

    private static void AssertFinding(
        IReadOnlyList<AcademicIntegrityIssue> issues,
        string code,
        AcademicIssueSeverity severity)
    {
        Assert.Contains(issues, issue => issue.Code == code && issue.Severity == severity);
    }

    private static void ReplaceChapterBlock(
        AcademicWorkflowSnapshot snapshot,
        Func<AcademicChapterBlock, AcademicChapterBlock> mutate)
    {
        var chapter = snapshot.Chapters[0];
        snapshot.Chapters[0] = chapter with { Blocks = chapter.Blocks.Select(mutate).ToList() };
    }

    private static AcademicSourceRecord Source(AcademicAccessLevel access) => new()
    {
        SourceId = "rule-source",
        Title = "Regulation of essential medicines in emerging markets",
        Authors = ["Qadir"],
        SourceType = "journal-article",
        AccessLevel = access,
        DedupKey = "rule-source-key",
        AbstractText = access == AcademicAccessLevel.MetadataOnly ? null : "This study surveys regulation of essential medicines across markets.",
    };

    private static AcademicWorkflowSnapshot GoldenSnapshot(AcademicWorkflowRequest? request = null)
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var workflowRequest = request ?? AcademicWorkflowFixtures.Request("integrity-unit");
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = Guid.NewGuid(),
            WorkspaceId = AcademicWorkflowFixtures.WorkspaceId,
            RequestKey = workflowRequest.RequestKey,
            Request = workflowRequest,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        foreach (var record in snapshot.Stages)
            record.State = AcademicStageState.Completed;

        snapshot.Plan = AcademicWorkflowFixtures.Plan();
        snapshot.Sources = AcademicWorkflowFixtures.Sources();
        snapshot.Screening = AcademicWorkflowFixtures.Screening();
        snapshot.Evidence = AcademicWorkflowFixtures.Evidence();
        snapshot.Themes = AcademicWorkflowFixtures.Themes();
        snapshot.Chapters = AcademicWorkflowFixtures.Chapters();
        snapshot.Verification = AcademicWorkflowFixtures.Verification();
        return snapshot;
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicEvidence.Appraisal;
using Taslim.Api.AcademicEvidence.Domain;
using Taslim.Api.AcademicEvidence.Evidence;
using Taslim.Api.AcademicEvidence.Matrix;
using Taslim.Api.AcademicEvidence.Normalization;
using Taslim.Api.AcademicEvidence.Persistence;
using Taslim.Api.AcademicEvidence.Provenance;
using Xunit;
using AcademicDomain = Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.Tests.AcademicEvidence;

/// <summary>
/// Storage round trips for the evidence lane. The schema is applied to an
/// in-memory SQLite database through the same <see cref="AcademicEvidenceModelBuilderExtensions"/>
/// the composition root will use, so what these tests prove about column widths,
/// uniqueness and tenant scoping is what production will get.
/// </summary>
public sealed class AcademicEvidencePersistenceTests
{
    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyAcademicEvidenceModel();
    }

    /// <summary>
    /// One in-memory database per test, created through the same model the
    /// composition root will use. The connection has to stay open for the
    /// lifetime of the fixture, since an in-memory SQLite database lives there.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        internal Fixture()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            Db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>()
                .UseSqlite(_connection)
                .Options);
            Db.Database.EnsureCreated();
            Store = new EfAcademicEvidenceStore(Db);
        }

        internal TestDbContext Db { get; }
        internal EfAcademicEvidenceStore Store { get; }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private const string Question = "tender-centralisation-effect";

    /// <summary>
    /// Appraise a source. The title varies per call because the dedupe index treats
    /// same title, same authors, same year as one work, which is exactly what the
    /// collision test below proves.
    /// </summary>
    // A second appraisal of the same source, this time carrying a registry
    // retraction notice, so the trail shows a provenance change as well as a
    // screening change.
    private static AppraisalResult AppraiseRetracted(Guid sourceId)
    {
        var appraised = Appraise(sourceId, fullText: AcademicEvidenceFixtures.FullTextBody);
        var notice = new RetractionNotice
        {
            Kind = RetractionKind.PartialRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock,
        };

        return appraised with
        {
            Provenance = ProvenanceVerifier.Verify(
                appraised.Record, appraised.Normalization, [notice], AcademicEvidenceFixtures.Clock),
        };
    }

    private static AppraisalResult Appraise(Guid sourceId, string? fullText = null, string? title = null)
        => new LiteratureAppraiser().Appraise(new AppraisalRequest(
            WorkspaceId: AcademicEvidenceFixtures.Workspace,
            SourceId: sourceId,
            Origins: AcademicEvidenceFixtures.CorroboratedJournalArticle()
                .Select(o => o with
                {
                    Title = title ?? o.Title,
                    AbstractText = AcademicEvidenceFixtures.AbstractText,
                    FullText = fullText,
                })
                .ToList(),
            Criteria: new InclusionCriteria(Version: "protocol-1"),
            BiasFramework: "rob2",
            BiasFindings: BiasFrameworks.DomainsFor("rob2")!
                .Select(d => new BiasDomainFinding
                {
                    DomainCode = d,
                    Judgement = BiasJudgement.LowConcern,
                    Source = BiasJudgementSource.Reviewer,
                })
                .ToList(),
            PeerReviewStatus: PeerReviewStatus.VerifiedFromRegistry));

    private static AcademicEvidenceSnapshot Snapshot(AppraisalResult result, params EvidenceItem[] items)
        => new(result.Record, result.Provenance, result.Quality, result.Decision, result.Bias, items);

    private static EvidenceItem Quantitative(
        AppraisalResult result,
        EvidenceClaim? claim = null,
        string topic = Question,
        string? content = "Retention of essential medicines on formulary improved by 18 percent",
        SourceLocator? locator = null)
        => EvidenceRecordBuilder.Record(
            result.Record, result.Provenance, topic,
            EvidenceSupportKind.QuantitativeFinding, content,
            structuredClaim: claim,
            locator: locator ?? new SourceLocator
            {
                Kind = LocatorKind.PageRange,
                Origin = LocatorOrigin.RegistryMetadata,
                Label = "412-419",
            },
            recordedAtUtc: AcademicEvidenceFixtures.Clock).Item;

    [Fact]
    public async Task A_normalised_record_survives_the_database_unchanged()
    {
        using var fixture = new Fixture();
        var result = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000001"),
            fullText: AcademicEvidenceFixtures.FullTextBody);

        await fixture.Store.SaveAppraisalAsync(
            Snapshot(result), AcademicEvidenceFixtures.Project, "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.ReplaceEvidenceAsync(
            AcademicEvidenceFixtures.Workspace, result.Record.SourceId,
            [Quantitative(result, Claim())], TestCancellationToken());

        var loaded = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, result.Record.SourceId, TestCancellationToken());

        Assert.NotNull(loaded);
        var record = loaded!.Record;
        Assert.Equal(result.Record.Title, record.Title);
        Assert.Equal(result.Record.PublicationYear, record.PublicationYear);
        Assert.Equal(result.Record.YearBasis, record.YearBasis);
        Assert.Equal(result.Record.SourceKind, record.SourceKind);
        Assert.Equal(result.Record.AccessLevel, record.AccessLevel);
        Assert.Equal(result.Record.ContentAcquisition, record.ContentAcquisition);
        Assert.Equal(result.Record.Venue, record.Venue);
        Assert.Equal(result.Record.Language, record.Language);
        Assert.Equal(result.Record.PageRange, record.PageRange);
        Assert.Equal(result.Record.PageRangeOrigin, record.PageRangeOrigin);
        Assert.Equal(result.Record.AbstractText, record.AbstractText);
        Assert.Equal(result.Record.FullText, record.FullText);
        Assert.Equal("10.5555/kurdistan.pharma.2024.001", record.NormalizedIdentifier(IdentifierKind.Doi));
        Assert.Equal(result.Record.Authors.Select(a => a.MatchKey), record.Authors.Select(a => a.MatchKey));
        Assert.Equal(result.Record.Authors.Select(a => a.Orcid), record.Authors.Select(a => a.Orcid));
        Assert.True(record.IsCitable);

        // The appraisal travels with it, including the reasons, not just the label.
        Assert.Equal(ProvenanceVerdict.Verified, loaded.Provenance.Verdict);
        Assert.True(loaded.Provenance.MayPresentAsVerified);
        Assert.Equal(result.Provenance.ReasonCodes, loaded.Provenance.ReasonCodes);
        Assert.Equal(result.Quality.Tier, loaded.Quality!.Tier);
        Assert.Equal(result.Quality.Score, loaded.Quality!.Score, 6);
        Assert.Equal(result.Quality.Criteria.Select(c => (c.CriterionCode, c.Outcome)),
            loaded.Quality.Criteria.Select(c => (c.CriterionCode, c.Outcome)));
        Assert.Equal(ScreeningDecision.Included, loaded.Decision!.Decision);
        Assert.Equal(result.Decision.InclusionReasons, loaded.Decision.InclusionReasons);
        Assert.Equal(OverallBiasRisk.LowRisk, loaded.Bias!.OverallRisk);
        Assert.Equal(5, loaded.Bias.Domains.Count);

        var evidence = Assert.Single(loaded.Evidence);
        Assert.Equal(EvidenceIntegrityStatus.VerifiedAgainstSource, evidence.Integrity);
        Assert.Equal("pp. 412-419", SourceLocatorPolicy.FormatForCitation(evidence.Locator));
        Assert.Equal(1.6, evidence.StructuredClaim!.Magnitude);
        Assert.True(evidence.IsExportable);
    }

    [Fact]
    public async Task Metadata_only_and_full_text_sources_are_kept_apart()
    {
        using var fixture = new Fixture();
        var metadata = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000002"),
            title: "Abstract-only tendering study");
        var full = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000003"),
            fullText: AcademicEvidenceFixtures.FullTextBody, title: "Read tendering study");

        await fixture.Store.SaveAppraisalAsync(Snapshot(metadata), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.SaveAppraisalAsync(Snapshot(full), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);

        var reloadedMetadata = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, metadata.Record.SourceId, TestCancellationToken());
        var reloadedFull = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, full.Record.SourceId, TestCancellationToken());

        Assert.Equal(SourceAccessLevel.Abstract, reloadedMetadata!.Record.AccessLevel);
        Assert.Null(reloadedMetadata.Record.FullText);
        Assert.Equal(SourceAccessLevel.FullText, reloadedFull!.Record.AccessLevel);
        Assert.NotNull(reloadedFull.Record.FullText);

        // A quoting capability follows the stored text, never the provider's claim.
        Assert.True(AccessLevelPolicy.SupportsContentClaims(SourceAccessLevel.Abstract));
        Assert.Contains("Retention",
            AccessLevelPolicy.GetSearchableText(reloadedMetadata.Record)!, StringComparison.Ordinal);
        Assert.True(AccessLevelPolicy.SupportsPrintedPagination(SourceAccessLevel.FullText));
        Assert.False(AccessLevelPolicy.SupportsPrintedPagination(SourceAccessLevel.Abstract));
    }

    [Fact]
    public async Task A_rejected_page_number_does_not_come_back_out_of_storage()
    {
        using var fixture = new Fixture();
        var result = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000004"),
            fullText: AcademicEvidenceFixtures.FullTextBody);

        // The model supplied "p. 412" for a passage it never saw a page of.
        var rejection = SourceLocatorPolicy.Admit(
            new SourceLocator { Kind = LocatorKind.Page, Origin = LocatorOrigin.ModelInference, Label = "412" },
            result.Record, AcademicEvidenceFixtures.FullTextBody, assertsContent: true);
        Assert.False(rejection.Admitted);

        var item = Quantitative(result, Claim(),
            locator: new SourceLocator { Kind = LocatorKind.Page, Origin = LocatorOrigin.ModelInference, Label = "412" });
        Assert.Equal(EvidenceIntegrityStatus.LocatorRejected, item.Integrity);

        await fixture.Store.SaveAppraisalAsync(Snapshot(result, item), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.ReplaceEvidenceAsync(
            AcademicEvidenceFixtures.Workspace, result.Record.SourceId, [item], TestCancellationToken());

        var loaded = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, result.Record.SourceId, TestCancellationToken());
        var stored = Assert.Single(loaded!.Evidence);

        // Nothing that looks like a page number is stored, so nothing can be printed.
        Assert.Null(stored.Locator);
        Assert.False(stored.IsExportable);
        Assert.Equal(EvidenceIntegrityStatus.LocatorRejected, stored.Integrity);
        Assert.Contains(LocatorDecisionCodes.OriginUnsourced, stored.IntegrityReasons);
        Assert.Null(SourceLocatorPolicy.FormatForCitation(stored.Locator));
    }

    [Fact]
    public async Task One_workspace_can_never_read_another_works_evidence()
    {
        using var fixture = new Fixture();
        var result = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000005"));
        await fixture.Store.SaveAppraisalAsync(Snapshot(result), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);

        var asOtherTenant = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.OtherWorkspace, result.Record.SourceId, TestCancellationToken());
        var otherList = await fixture.Store.ListAsync(
            AcademicEvidenceFixtures.OtherWorkspace, AcademicEvidenceFixtures.Project, 10, TestCancellationToken());

        Assert.Null(asOtherTenant);
        Assert.Empty(otherList);

        var own = await fixture.Store.ListAsync(
            AcademicEvidenceFixtures.Workspace, AcademicEvidenceFixtures.Project, 10, TestCancellationToken());
        Assert.Single(own);

        // No project filter still cannot cross tenants.
        var allForOther = await fixture.Store.ListAsync(
            AcademicEvidenceFixtures.OtherWorkspace, null, 10, TestCancellationToken());
        Assert.Empty(allForOther);
    }

    [Fact]
    public async Task Updating_an_appraisal_rewrites_the_verdict_rather_than_adding_a_second_one()
    {
        using var fixture = new Fixture();
        var sourceId = Guid.Parse("f0000000-0000-4000-8000-000000000006");
        var first = Appraise(sourceId);
        await fixture.Store.SaveAppraisalAsync(Snapshot(first), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);

        // A retraction arrives; the same source is re-appraised and re-saved.
        var notice = new RetractionNotice
        {
            Kind = RetractionKind.FullRetraction,
            Origin = RetractionNoticeOrigin.Registry,
            NoticedAtUtc = AcademicEvidenceFixtures.Clock.AddHours(1),
        };
        var withdrawn = ProvenanceVerifier.Verify(first.Record, first.Normalization, [notice],
            AcademicEvidenceFixtures.Clock.AddHours(1));
        var screening = SourceQualityScreener.Screen(first.Record, withdrawn,
            PeerReviewStatus.VerifiedFromRegistry, screenedAtUtc: AcademicEvidenceFixtures.Clock.AddHours(1));
        var decision = InclusionExclusionPolicy.Evaluate(first.Record, withdrawn, screening,
            decidedAtUtc: AcademicEvidenceFixtures.Clock.AddHours(1)).Decision;

        await fixture.Store.SaveAppraisalAsync(
            new AcademicEvidenceSnapshot(first.Record, withdrawn, screening, decision, first.Bias, []),
            AcademicEvidenceFixtures.Project, "protocol-1", AcademicEvidenceFixtures.Clock.AddHours(1));

        var loaded = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, sourceId, TestCancellationToken());

        Assert.Equal(ProvenanceVerdict.Withdrawn, loaded!.Provenance.Verdict);
        Assert.False(loaded.Provenance.MayPresentAsVerified);
        Assert.Equal(QualityTier.Withdrawn, loaded.Quality!.Tier);
        Assert.Equal(ScreeningDecision.Excluded, loaded.Decision!.Decision);
        Assert.Single(loaded.Provenance.RetractionNotices);
        Assert.Equal(1, await fixture.Db.Set<AcademicSourceEntity>().CountAsync(TestCancellationToken()));
        Assert.Equal(1, await fixture.Db.Set<AcademicProvenanceEntity>().CountAsync(TestCancellationToken()));
    }

    [Fact]
    public async Task A_second_source_with_the_same_identity_collides_in_one_project_only()
    {
        using var fixture = new Fixture();
        var kept = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000007"));
        var twin = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000008"));

        await fixture.Store.SaveAppraisalAsync(Snapshot(kept), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);

        // Same title, authors and year: the dedupe key matches, so a second
        // unlinked row in the same project is refused by the index.
        await fixture.Db.SaveChangesAsync(TestCancellationToken());
        var duplicateRow = AcademicEvidenceRowMapper.ToRow(
            twin.Record, AcademicEvidenceFixtures.Project, "protocol-1", null, AcademicEvidenceFixtures.Clock);
        duplicateRow.DedupeKey = AcademicEvidenceRowMapper.ToRow(
            kept.Record, AcademicEvidenceFixtures.Project, "protocol-1", null, AcademicEvidenceFixtures.Clock).DedupeKey;
        fixture.Db.Add(duplicateRow);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => fixture.Db.SaveChangesAsync(TestCancellationToken()));

        // Once the duplicate is linked to the survivor it may be stored, which is
        // how a merged record keeps its audit trail.
        fixture.Db.ChangeTracker.Clear();
        duplicateRow.DuplicateOfSourceId = kept.Record.SourceId;
        fixture.Db.Add(duplicateRow);
        await fixture.Db.SaveChangesAsync(TestCancellationToken());

        // A different project is a different review: the same work may sit in two
        // students' libraries.
        fixture.Db.ChangeTracker.Clear();
        var otherProject = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var otherProjectRow = AcademicEvidenceRowMapper.ToRow(
            twin.Record, otherProject, "protocol-1", null, AcademicEvidenceFixtures.Clock);
        otherProjectRow.DedupeKey = duplicateRow.DedupeKey;
        otherProjectRow.Id = Guid.Parse("f0000000-0000-4000-8000-0000000000ff");
        fixture.Db.Add(otherProjectRow);
        await fixture.Db.SaveChangesAsync(TestCancellationToken());

        // Three rows in total: the survivor and its linked duplicate in the first
        // project, and the same work again in the second review.
        Assert.Equal(3, await fixture.Db.Set<AcademicSourceEntity>().CountAsync(TestCancellationToken()));
        Assert.Equal(2, await fixture.Db.Set<AcademicSourceEntity>().AsNoTracking()
            .CountAsync(s => s.ProjectId == AcademicEvidenceFixtures.Project, TestCancellationToken()));
        Assert.Equal(1, await fixture.Db.Set<AcademicSourceEntity>().AsNoTracking()
            .CountAsync(s => s.ProjectId == otherProject, TestCancellationToken()));
        Assert.Equal(1, await fixture.Db.Set<AcademicSourceEntity>().AsNoTracking()
            .CountAsync(s => s.DuplicateOfSourceId != null, TestCancellationToken()));
    }

    [Fact]
    public async Task The_matrix_is_derived_state_replaced_on_every_rebuild()
    {
        using var fixture = new Fixture();
        var left = Appraise(Guid.Parse("f0000000-0000-4000-8000-000000000009"),
            fullText: AcademicEvidenceFixtures.FullTextBody, title: "Tendering raised retention");
        var right = Appraise(Guid.Parse("f0000000-0000-4000-8000-00000000000a"),
            fullText: AcademicEvidenceFixtures.FullTextBody, title: "Tendering lowered retention");

        var matrix = EvidenceMatrixBuilder.Build(
            AcademicEvidenceFixtures.Workspace,
            AcademicEvidenceFixtures.Project,
            [new MatrixQuestion { Key = Question, Text = "Does centralised tendering improve retention?" }],
            [
                new SourceAssessment(left.Record, left.Provenance, left.Quality, left.Decision, left.Bias,
                    [Quantitative(left, Claim(EffectDirection.Increase))]),
                new SourceAssessment(right.Record, right.Provenance, right.Quality, right.Decision, right.Bias,
                    [Quantitative(right, Claim(EffectDirection.Decrease))]),
            ]);

        await fixture.Store.SaveAppraisalAsync(Snapshot(left), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.SaveAppraisalAsync(Snapshot(right), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.SaveMatrixAsync(matrix, id => id.ToString("N"),
            AcademicEvidenceFixtures.Clock, TestCancellationToken());

        var cells = await fixture.Db.Set<AcademicMatrixCellEntity>()
            .AsNoTracking().OrderBy(c => c.SourceId).ToListAsync(TestCancellationToken());
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c =>
        {
            Assert.Equal(Question, c.QuestionKey);
            Assert.Equal("Verified", c.Provenance);
        });
        Assert.Contains(cells, c => c.Stance == nameof(SourceStance.Supports));
        Assert.Contains(cells, c => c.Stance == nameof(SourceStance.Contradicts));
        Assert.All(cells, c => Assert.True(c.HasVerifiableLocator));

        var findings = await fixture.Db.Set<AcademicMatrixFindingEntity>()
            .AsNoTracking().ToListAsync(TestCancellationToken());
        Assert.Contains(findings, f =>
            f.FindingType == "relationship" && f.Code == nameof(ClaimRelation.Contradiction)
            && f.Severity == nameof(FindingSeverity.Warning));
        Assert.Contains(findings, f => f.FindingType == "gap" && f.Code == nameof(ResearchGapKind.UnresolvedContradiction));

        // Rebuilding with one source drops the stale rows instead of accumulating them.
        var rebuilt = EvidenceMatrixBuilder.Build(
            AcademicEvidenceFixtures.Workspace,
            AcademicEvidenceFixtures.Project,
            [new MatrixQuestion { Key = Question, Text = "Does centralised tendering improve retention?" }],
            [new SourceAssessment(left.Record, left.Provenance, left.Quality, left.Decision, left.Bias,
                [Quantitative(left, Claim(EffectDirection.Increase))])]);
        await fixture.Store.SaveMatrixAsync(rebuilt, _ => null,
            AcademicEvidenceFixtures.Clock.AddMinutes(1), TestCancellationToken());

        Assert.Single(await fixture.Db.Set<AcademicMatrixCellEntity>()
            .AsNoTracking().ToListAsync(TestCancellationToken()));
        Assert.Empty(await fixture.Db.Set<AcademicMatrixFindingEntity>()
            .AsNoTracking().Where(f => f.FindingType == "relationship").ToListAsync(TestCancellationToken()));
    }

    [Fact]
    public async Task The_audit_trail_stays_valid_after_a_round_trip()
    {
        using var fixture = new Fixture();
        var chain = new ProvenanceAuditChain();
        var sourceId = Guid.Parse("f0000000-0000-4000-8000-00000000000b");
        AppraisalAuditWriter.Write(chain, Appraise(sourceId), AcademicEvidenceFixtures.Clock);
        AppraisalAuditWriter.Write(
            chain,
            AppraiseRetracted(sourceId),
            AcademicEvidenceFixtures.Clock.AddMinutes(1),
            AuditActorKind.Member,
            Guid.Parse("88888888-8888-4888-8888-888888888888"));

        await fixture.Store.AppendAuditAsync(chain.Events, TestCancellationToken());

        var rows = await fixture.Db.Set<AcademicAuditEventEntity>().AsNoTracking()
            .Where(e => e.SourceId == sourceId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(TestCancellationToken());

        Assert.Equal(chain.Events.Count, rows.Count);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L], rows.Select(r => r.Sequence));
        Assert.Contains(rows, r => r.ActorKind == nameof(AuditActorKind.Member));

        var reloaded = rows.Select(r => new ProvenanceAuditEvent
        {
            Sequence = r.Sequence,
            SourceId = r.SourceId,
            WorkspaceId = r.WorkspaceId,
            EventKind = r.EventKind,
            ActorKind = Enum.Parse<AuditActorKind>(r.ActorKind),
            ActorId = r.ActorId,
            PayloadCanonical = r.PayloadCanonical,
            OccurredAtUtc = new DateTimeOffset(r.OccurredAtUtc, TimeSpan.Zero),
            PreviousHash = r.PreviousHash,
            EntryHash = r.EntryHash,
        }).ToList();

        Assert.True(ProvenanceAuditChain.Validate(reloaded, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public async Task Replacing_evidence_removes_only_that_sources_items()
    {
        using var fixture = new Fixture();
        var first = Appraise(Guid.Parse("f0000000-0000-4000-8000-00000000000c"),
            fullText: AcademicEvidenceFixtures.FullTextBody, title: "First evidence source");
        var second = Appraise(Guid.Parse("f0000000-0000-4000-8000-00000000000d"),
            fullText: AcademicEvidenceFixtures.FullTextBody, title: "Second evidence source");
        await fixture.Store.SaveAppraisalAsync(Snapshot(first), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);
        await fixture.Store.SaveAppraisalAsync(Snapshot(second), AcademicEvidenceFixtures.Project,
            "protocol-1", AcademicEvidenceFixtures.Clock);

        await fixture.Store.ReplaceEvidenceAsync(AcademicEvidenceFixtures.Workspace, first.Record.SourceId,
            [Quantitative(first, Claim()), Quantitative(first, Claim(), topic: "second-question")],
            TestCancellationToken());
        await fixture.Store.ReplaceEvidenceAsync(AcademicEvidenceFixtures.Workspace, second.Record.SourceId,
            [Quantitative(second, Claim())], TestCancellationToken());

        Assert.Equal(3, await fixture.Db.Set<AcademicEvidenceItemEntity>().CountAsync(TestCancellationToken()));

        await fixture.Store.ReplaceEvidenceAsync(AcademicEvidenceFixtures.Workspace, first.Record.SourceId,
            [Quantitative(first, Claim())], TestCancellationToken());

        Assert.Equal(2, await fixture.Db.Set<AcademicEvidenceItemEntity>().CountAsync(TestCancellationToken()));
        var kept = await fixture.Store.LoadAsync(
            AcademicEvidenceFixtures.Workspace, second.Record.SourceId, TestCancellationToken());
        Assert.Single(kept!.Evidence);
    }

    [Fact]
    public async Task Listing_is_newest_first_and_bounded()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 5; i++)
        {
            var result = Appraise(Guid.Parse($"f000000{i}-0000-4000-8000-00000000000e"),
                title: $"Library source {i}");
            await fixture.Store.SaveAppraisalAsync(Snapshot(result), AcademicEvidenceFixtures.Project,
                "protocol-1", AcademicEvidenceFixtures.Clock.AddMinutes(i));
        }

        var page = await fixture.Store.ListAsync(
            AcademicEvidenceFixtures.Workspace, AcademicEvidenceFixtures.Project, 3, TestCancellationToken());

        Assert.Equal(3, page.Count);
        var limited = await fixture.Store.ListAsync(
            AcademicEvidenceFixtures.Workspace, null, 0, TestCancellationToken());
        Assert.NotEmpty(limited);
    }

    private static EvidenceClaim Claim(EffectDirection direction = EffectDirection.Increase) => new()
    {
        SubjectKey = "formulary retention",
        PredicateKey = "effect of centralised tendering",
        Direction = direction,
        Magnitude = 1.6,
        Unit = "OR",
        IntervalLow = 1.2,
        IntervalHigh = 2.0,
        PopulationKey = "kurdistan region pharmacy market",
    };

    private static CancellationToken TestCancellationToken() => CancellationToken.None;
}

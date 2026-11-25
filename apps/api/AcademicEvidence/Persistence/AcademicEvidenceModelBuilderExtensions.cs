using Microsoft.EntityFrameworkCore;

namespace Taslim.Api.AcademicEvidence.Persistence;

/// <summary>
/// Relational mapping for the academic evidence tables.
///
/// This is the only place the schema is described, and it is intentionally an
/// extension method rather than an edit to the shared context: Phase A and Phase C
/// both touch <c>TaslimDbContext</c>, so the lane keeps its schema in its own folder
/// and the integration commit adds a single call to
/// <see cref="ApplyAcademicEvidenceModel"/>. No migration is generated here, and
/// nothing is deployed: the tables arrive when the owning branch runs its migration
/// step.
/// </summary>
public static class AcademicEvidenceModelBuilderExtensions
{
    /// <summary>Apply the academic evidence mapping to a model builder.</summary>
    public static ModelBuilder ApplyAcademicEvidenceModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AcademicSourceEntity>(source =>
        {
            source.ToTable("AcademicEvidenceSources");
            source.HasKey(e => e.Id);

            source.Property(e => e.Title).HasMaxLength(2048).IsRequired();
            source.Property(e => e.Subtitle).HasMaxLength(1024);
            source.Property(e => e.OriginalScriptTitle).HasMaxLength(2048);
            source.Property(e => e.SourceKind).HasMaxLength(32);
            source.Property(e => e.PublicationYear).HasMaxLength(8);
            source.Property(e => e.YearBasis).HasMaxLength(32);
            source.Property(e => e.Publisher).HasMaxLength(512);
            source.Property(e => e.EditionLabel).HasMaxLength(128);
            source.Property(e => e.Volume).HasMaxLength(32);
            source.Property(e => e.Issue).HasMaxLength(32);
            source.Property(e => e.PageRange).HasMaxLength(64);
            source.Property(e => e.PageRangeOrigin).HasMaxLength(32);
            source.Property(e => e.Venue).HasMaxLength(1024);
            source.Property(e => e.AccessLevel).HasMaxLength(32);
            source.Property(e => e.ContentAcquisition).HasMaxLength(32);
            source.Property(e => e.LandingPageUrl).HasMaxLength(2048);
            source.Property(e => e.Language).HasMaxLength(16);
            source.Property(e => e.Discipline).HasMaxLength(256);
            source.Property(e => e.StudyDesign).HasMaxLength(128);
            source.Property(e => e.CorpusId).HasMaxLength(256);
            source.Property(e => e.ContentSha256).HasMaxLength(64);
            source.Property(e => e.DedupeKey).HasMaxLength(2048);
            source.Property(e => e.ProtocolId).HasMaxLength(64);

            // Large bodies stay unbounded text; a thesis chapter is not 4000 characters.
            source.Property(e => e.AuthorsJson).HasColumnType("text");
            source.Property(e => e.EditorsJson).HasColumnType("text");
            source.Property(e => e.IdentifiersJson).HasColumnType("text");
            source.Property(e => e.AbstractText).HasColumnType("text");
            source.Property(e => e.FullText).HasColumnType("text");

            source.HasIndex(e => e.WorkspaceId);
            source.HasIndex(e => new { e.WorkspaceId, e.ProjectId });
            source.HasIndex(e => new { e.WorkspaceId, e.CorpusId });

            // The dedupe key is what stops one article entering a review twice, but
            // it is only unique per project: the same work legitimately appears in two
            // students' libraries.
            source.HasIndex(e => new { e.WorkspaceId, e.ProjectId, e.DedupeKey })
                .IsUnique()
                .HasFilter("\"DuplicateOfSourceId\" IS NULL");
        });

        modelBuilder.Entity<AcademicProvenanceEntity>(provenance =>
        {
            provenance.ToTable("AcademicEvidenceProvenance");
            provenance.HasKey(e => e.Id);

            provenance.Property(e => e.Verdict).HasMaxLength(32);
            provenance.Property(e => e.ReasonCodesJson).HasColumnType("text");
            provenance.Property(e => e.FieldEvidenceJson).HasColumnType("text");
            provenance.Property(e => e.RetractionNoticesJson).HasColumnType("text");

            provenance.HasIndex(e => e.SourceId).IsUnique();
            provenance.HasIndex(e => new { e.WorkspaceId, e.Verdict });

            provenance.HasOne<AcademicSourceEntity>()
                .WithMany()
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AcademicScreeningEntity>(screening =>
        {
            screening.ToTable("AcademicEvidenceScreening");
            screening.HasKey(e => e.Id);

            screening.Property(e => e.ProtocolId).HasMaxLength(64);
            screening.Property(e => e.QualityTier).HasMaxLength(32);
            screening.Property(e => e.Decision).HasMaxLength(32);
            screening.Property(e => e.MemberNote).HasMaxLength(4096);
            screening.Property(e => e.CriteriaJson).HasColumnType("text");
            screening.Property(e => e.FlagsJson).HasColumnType("text");
            screening.Property(e => e.BiasJson).HasColumnType("text");
            screening.Property(e => e.InclusionReasonsJson).HasColumnType("text");
            screening.Property(e => e.ExclusionReasonsJson).HasColumnType("text");
            screening.Property(e => e.UndeterminedReasonsJson).HasColumnType("text");
            screening.Property(e => e.DecidedBy).HasMaxLength(32);
            screening.Property(e => e.ProvenanceVerdict).HasMaxLength(32);

            screening.HasIndex(e => new { e.SourceId, e.ProtocolId }).IsUnique();
            screening.HasIndex(e => new { e.WorkspaceId, e.Decision });

            screening.HasOne<AcademicSourceEntity>()
                .WithMany()
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AcademicEvidenceItemEntity>(item =>
        {
            item.ToTable("AcademicEvidenceItems");
            item.HasKey(e => e.Id);

            item.Property(e => e.Topic).HasMaxLength(256);
            item.Property(e => e.SupportKind).HasMaxLength(32);
            item.Property(e => e.Content).HasColumnType("text");
            item.Property(e => e.Claim).HasColumnType("text");
            item.Property(e => e.ClaimJson).HasColumnType("text");
            item.Property(e => e.LocatorKind).HasMaxLength(32);
            item.Property(e => e.LocatorOrigin).HasMaxLength(32);
            item.Property(e => e.LocatorLabel).HasMaxLength(128);
            item.Property(e => e.LocatorPaginationBasis).HasMaxLength(32);
            item.Property(e => e.LocatorRejectionCode).HasMaxLength(64);
            item.Property(e => e.Integrity).HasMaxLength(32);
            item.Property(e => e.AccessLevelAtRecord).HasMaxLength(32);
            item.Property(e => e.IntegrityReasonsJson).HasColumnType("text");
            item.Property(e => e.FlagsJson).HasColumnType("text");

            item.HasIndex(e => new { e.SourceId, e.Topic });
            item.HasIndex(e => new { e.WorkspaceId, e.Integrity });

            item.HasOne<AcademicSourceEntity>()
                .WithMany()
                .HasForeignKey(e => e.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AcademicMatrixCellEntity>(cell =>
        {
            cell.ToTable("AcademicEvidenceMatrixCells");
            cell.HasKey(e => e.Id);

            cell.Property(e => e.QuestionKey).HasMaxLength(256);
            cell.Property(e => e.QuestionText).HasMaxLength(2048);
            cell.Property(e => e.Stance).HasMaxLength(32);
            cell.Property(e => e.AccessLevel).HasMaxLength(32);
            cell.Property(e => e.Provenance).HasMaxLength(32);
            cell.Property(e => e.Quality).HasMaxLength(32);
            cell.Property(e => e.Screening).HasMaxLength(32);
            cell.Property(e => e.EvidenceIdsJson).HasColumnType("text");
            cell.Property(e => e.FlagsJson).HasColumnType("text");

            // One intersection may be rebuilt many times but never duplicated.
            cell.HasIndex(e => new { e.ProjectId, e.QuestionKey, e.SourceId }).IsUnique();

            cell.HasIndex(e => new { e.WorkspaceId, e.ProjectId });
        });

        modelBuilder.Entity<AcademicMatrixFindingEntity>(finding =>
        {
            finding.ToTable("AcademicEvidenceMatrixFindings");
            finding.HasKey(e => e.Id);

            finding.Property(e => e.FindingType).HasMaxLength(16);
            finding.Property(e => e.QuestionKey).HasMaxLength(256);
            finding.Property(e => e.Code).HasMaxLength(64);
            finding.Property(e => e.Severity).HasMaxLength(16);
            finding.Property(e => e.Explanation).HasColumnType("text");
            finding.Property(e => e.SuggestedStep).HasMaxLength(2048);
            finding.Property(e => e.SourceIdsJson).HasColumnType("text");
            finding.Property(e => e.ReasonCodesJson).HasColumnType("text");

            finding.HasIndex(e => new { e.ProjectId, e.FindingType, e.Code });

            finding.HasIndex(e => new { e.WorkspaceId, e.ProjectId });
        });

        modelBuilder.Entity<AcademicAuditEventEntity>(audit =>
        {
            audit.ToTable("AcademicEvidenceAuditEvents");

            // Sequence is per source, so the pair is the key: the chain is read back
            // ordered by it, and a duplicated sequence number is a broken trail.
            audit.HasKey(e => new { e.SourceId, e.Sequence });

            audit.Property(e => e.EventKind).HasMaxLength(64);
            audit.Property(e => e.ActorKind).HasMaxLength(16);
            audit.Property(e => e.PreviousHash).HasMaxLength(64);
            audit.Property(e => e.EntryHash).HasMaxLength(64);
            audit.Property(e => e.PayloadCanonical).HasColumnType("text");

            audit.HasIndex(e => new { e.WorkspaceId, e.SourceId, e.Sequence });
            audit.HasIndex(e => e.EntryHash);
        });

        return modelBuilder;
    }
}

using Microsoft.EntityFrameworkCore;

namespace Taslim.Api.AcademicIntegration.Persistence;

/// <summary>
/// Relational mapping for the academic integration tables, kept in the lane's own
/// folder following the pattern of <c>ApplyAcademicEvidenceModel</c>: the composition
/// commit adds a single call in <c>TaslimDbContext.OnModelCreating</c>, and schema
/// changes to this lane never edit the shared context body.
/// </summary>
public static class AcademicIntegrationModelBuilderExtensions
{
    /// <summary>Apply the academic workflow integration mapping to a model builder.</summary>
    public static ModelBuilder ApplyAcademicIntegrationModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AcademicWorkflowProjectEntity>(project =>
        {
            project.ToTable("AcademicWorkflowProjects");
            project.HasKey(e => e.ProjectId);

            project.Property(e => e.RequestKey).HasMaxLength(200).IsRequired();
            project.Property(e => e.State).HasMaxLength(32).IsRequired();
            project.Property(e => e.StateCode).HasMaxLength(64);
            project.Property(e => e.StateDetail).HasMaxLength(1024);
            project.Property(e => e.SnapshotJson).HasColumnType("text");

            // Idempotent intake: one project per workspace-scoped request key, and the
            // derived project id itself is the primary key, so a replayed create collides
            // on either constraint and can never mint a second project.
            project.HasIndex(e => new { e.WorkspaceId, e.RequestKey }).IsUnique();
            project.HasIndex(e => new { e.WorkspaceId, e.CreatedAtUtc });
            project.HasIndex(e => new { e.WorkspaceId, e.State });
        });

        modelBuilder.Entity<AcademicWorkflowCheckpointEntity>(checkpoint =>
        {
            checkpoint.ToTable("AcademicWorkflowCheckpoints");
            checkpoint.HasKey(e => e.Id);

            checkpoint.Property(e => e.CheckpointKey).HasMaxLength(160).IsRequired();

            checkpoint.HasIndex(e => e.CheckpointKey).IsUnique();
            checkpoint.HasIndex(e => new { e.ProjectId, e.Revision }).IsUnique();
            checkpoint.HasIndex(e => e.WorkspaceId);

            checkpoint.HasOne<AcademicWorkflowProjectEntity>()
                .WithMany()
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}

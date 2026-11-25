namespace Taslim.Api.AcademicIntegration.Persistence;

/// <summary>
/// Durable row for one academic research project.
///
/// The whole <c>AcademicWorkflowSnapshot</c> is stored as canonical JSON, which is
/// the shape the Phase C engine documented for the integration lane: the JSON column
/// is the state, the scalar columns are its indexes. <see cref="Revision"/> duplicates
/// the snapshot's committed revision and is the optimistic-concurrency token every
/// checkpoint commit compares against, so two workers can never both advance one
/// project. <see cref="EvidenceProjectedRevision"/> is the watermark of the last
/// snapshot the evidence projection consumed; a crash between a workflow commit and a
/// projection leaves the watermark behind, and the next run re-projects from the
/// committed checkpoint instead of losing derived state.
/// </summary>
public sealed class AcademicWorkflowProjectEntity
{
    public Guid ProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string RequestKey { get; set; } = string.Empty;

    /// <summary>Denormalized <c>AcademicProjectState</c> for listing and fences.</summary>
    public string State { get; set; } = "Running";
    public string? StateCode { get; set; }
    public string? StateDetail { get; set; }

    /// <summary>Committed checkpoint revision; matches the snapshot JSON's revision.</summary>
    public long Revision { get; set; }

    /// <summary>Canonical snapshot JSON (<c>AcademicWorkflowSerialization</c>).</summary>
    public string SnapshotJson { get; set; } = string.Empty;

    /// <summary>
    /// Highest snapshot revision already projected into the evidence tables.
    /// Starts at -1 so "never projected" is distinguishable from "projected revision 0".
    /// </summary>
    public long EvidenceProjectedRevision { get; set; } = -1;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Append-only checkpoint ledger, one row per committed revision.
///
/// The <see cref="CheckpointKey"/> uniqueness index is what makes checkpoint commits
/// idempotent across crashes: a replayed commit collides with the stored key and is
/// reported as a duplicate instead of double-applying a stage. The surrogate id is
/// derived from the key so a replayed ledger row is byte-identical, which keeps the
/// audit trail deterministic.
/// </summary>
public sealed class AcademicWorkflowCheckpointEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkspaceId { get; set; }
    public long Revision { get; set; }
    public string CheckpointKey { get; set; } = string.Empty;
    public DateTime CommittedAtUtc { get; set; }
}

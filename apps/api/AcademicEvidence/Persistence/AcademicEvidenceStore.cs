using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicEvidence.Domain;

namespace Taslim.Api.AcademicEvidence.Persistence;

/// <summary>
/// One row of the provenance audit trail. The hash columns are stored as written, so
/// a later reader can recompute the chain and show that a verdict was recorded before
/// or after a retraction notice arrived, rather than being rewritten afterwards.
/// </summary>
public sealed class AcademicAuditEventEntity
{
    public long Sequence { get; set; }
    public Guid SourceId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string EventKind { get; set; } = string.Empty;
    public string ActorKind { get; set; } = "System";
    public Guid? ActorId { get; set; }
    public string PayloadCanonical { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}

/// <summary>
/// A whole assessed source: the record, its provenance, its screening and the
/// evidence recorded against it, loaded together.
/// </summary>
public sealed record AcademicEvidenceSnapshot(
    NormalizedBibliographicRecord Record,
    SourceProvenance Provenance,
    QualityScreenResult? Quality,
    InclusionDecision? Decision,
    RiskOfBiasAssessment? Bias,
    IReadOnlyList<EvidenceItem> Evidence);

/// <summary>
/// Storage contract for the academic evidence lane.
///
/// Everything is workspace-scoped on read as well as on write: a source id is a
/// guessable value once it appears in an export, and a lookup that trusts it alone
/// would let one tenant read another student's library, including the manuscripts
/// they uploaded privately.
/// </summary>
public interface IAcademicEvidenceStore
{
    /// <summary>Insert or replace one assessed source and its rows.</summary>
    Task SaveAppraisalAsync(
        AcademicEvidenceSnapshot snapshot,
        Guid? projectId,
        string protocolId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Load one assessed source within a workspace.</summary>
    Task<AcademicEvidenceSnapshot?> LoadAsync(
        Guid workspaceId,
        Guid sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>List assessed sources for a workspace, newest first.</summary>
    Task<IReadOnlyList<AcademicEvidenceSnapshot>> ListAsync(
        Guid workspaceId,
        Guid? projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Replace the evidence rows recorded for one source.</summary>
    Task ReplaceEvidenceAsync(
        Guid workspaceId,
        Guid sourceId,
        IReadOnlyList<EvidenceItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>Store a rebuilt matrix and its findings.</summary>
    Task SaveMatrixAsync(
        Domain.EvidenceMatrix matrix,
        Func<Guid, string?> sourceLabel,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Append audit events.</summary>
    Task AppendAuditAsync(
        IEnumerable<ProvenanceAuditEvent> events,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Entity Framework implementation of <see cref="IAcademicEvidenceStore"/>.
///
/// It takes a plain <see cref="DbContext"/> rather than the shared context type, so
/// the lane compiles and is testable before the integration commit adds
/// <c>ApplyAcademicEvidenceModel</c> to that context. Every query filters on
/// <c>WorkspaceId</c>, including the delete inside <see cref="ReplaceEvidenceAsync"/>.
/// </summary>
public sealed class EfAcademicEvidenceStore(DbContext db) : IAcademicEvidenceStore
{
    private readonly DbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public async Task SaveAppraisalAsync(
        AcademicEvidenceSnapshot snapshot,
        Guid? projectId,
        string protocolId,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sources = _db.Set<AcademicSourceEntity>();
        var existing = await sources.FindAsync([snapshot.Record.SourceId], cancellationToken);
        var row = AcademicEvidenceRowMapper.ToRow(snapshot.Record, projectId, protocolId, null, atUtc);

        if (existing is null)
        {
            await sources.AddAsync(row, cancellationToken);
        }
        else
        {
            // Tenant check before touching a row that may belong to another workspace.
            if (existing.WorkspaceId != snapshot.Record.WorkspaceId)
            {
                throw new InvalidOperationException(
                    "Source " + snapshot.Record.SourceId + " belongs to another workspace.");
            }

            _db.Entry(existing).CurrentValues.SetValues(row);
        }

        await UpsertAsync(_db.Set<AcademicProvenanceEntity>(),
            AcademicEvidenceRowMapper.ToRow(snapshot.Provenance, atUtc),
            e => e.SourceId == snapshot.Record.SourceId,
            cancellationToken);

        if (snapshot.Quality is not null && snapshot.Decision is not null)
        {
            await UpsertAsync(_db.Set<AcademicScreeningEntity>(),
                AcademicEvidenceRowMapper.ToRow(snapshot.Quality, snapshot.Decision, snapshot.Bias, protocolId),
                e => e.SourceId == snapshot.Record.SourceId && e.ProtocolId == protocolId,
                cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AcademicEvidenceSnapshot?> LoadAsync(
        Guid workspaceId,
        Guid sourceId,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.Set<AcademicSourceEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == sourceId && e.WorkspaceId == workspaceId, cancellationToken);

        if (row is null) return null;

        var provenance = await _db.Set<AcademicProvenanceEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceId == sourceId && e.WorkspaceId == workspaceId, cancellationToken);

        var screening = await _db.Set<AcademicScreeningEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceId == sourceId && e.WorkspaceId == workspaceId, cancellationToken);

        var evidence = await _db.Set<AcademicEvidenceItemEntity>()
            .AsNoTracking()
            .Where(e => e.SourceId == sourceId && e.WorkspaceId == workspaceId)
            .OrderBy(e => e.Topic)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        return BuildSnapshot(row, provenance, screening, evidence);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AcademicEvidenceSnapshot>> ListAsync(
        Guid workspaceId,
        Guid? projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Set<AcademicSourceEntity>().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId);

        if (projectId is Guid project) query = query.Where(e => e.ProjectId == project);

        var rows = await query
            .OrderByDescending(e => e.NormalizedAtUtc)
            .ThenBy(e => e.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        var ids = rows.Select(r => r.Id).ToList();

        var provenanceRows = await _db.Set<AcademicProvenanceEntity>().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && ids.Contains(e.SourceId))
            .ToListAsync(cancellationToken);

        var screeningRows = await _db.Set<AcademicScreeningEntity>().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && ids.Contains(e.SourceId))
            .ToListAsync(cancellationToken);

        var evidenceRows = await _db.Set<AcademicEvidenceItemEntity>().AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && ids.Contains(e.SourceId))
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => BuildSnapshot(
            row,
            provenanceRows.FirstOrDefault(p => p.SourceId == row.Id),
            screeningRows.FirstOrDefault(s => s.SourceId == row.Id),
            evidenceRows.Where(i => i.SourceId == row.Id).OrderBy(i => i.Topic).ThenBy(i => i.Id).ToList()))];
    }

    /// <inheritdoc />
    public async Task ReplaceEvidenceAsync(
        Guid workspaceId,
        Guid sourceId,
        IReadOnlyList<EvidenceItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _db.Set<AcademicEvidenceItemEntity>()
            .Where(e => e.SourceId == sourceId && e.WorkspaceId == workspaceId)
            .ExecuteDeleteAsync(cancellationToken);

        // The delete bypassed the change tracker, so rows this context added earlier
        // have to be detached before their identifiers can be reused.
        foreach (var stale in _db.ChangeTracker.Entries<AcademicEvidenceItemEntity>()
                     .Where(e => e.Entity.SourceId == sourceId && e.Entity.WorkspaceId == workspaceId)
                     .ToList())
        {
            stale.State = EntityState.Detached;
        }

        var rows = items.Select(AcademicEvidenceRowMapper.ToRow).ToList();
        if (rows.Count > 0) await _db.Set<AcademicEvidenceItemEntity>().AddRangeAsync(rows, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveMatrixAsync(
        Domain.EvidenceMatrix matrix,
        Func<Guid, string?> sourceLabel,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(sourceLabel);

        // Rebuild semantics: the matrix is derived state, so the project's rows are
        // replaced rather than merged, and a stale cell cannot survive a rebuild.
        await _db.Set<AcademicMatrixCellEntity>()
            .Where(e => e.WorkspaceId == matrix.WorkspaceId && e.ProjectId == matrix.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);

        await _db.Set<AcademicMatrixFindingEntity>()
            .Where(e => e.WorkspaceId == matrix.WorkspaceId && e.ProjectId == matrix.ProjectId)
            .ExecuteDeleteAsync(cancellationToken);

        var when = atUtc.UtcDateTime;

        var cells = matrix.Cells.Select(cell => new AcademicMatrixCellEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = matrix.WorkspaceId,
            ProjectId = matrix.ProjectId,
            QuestionKey = cell.QuestionKey,
            QuestionText = matrix.Questions.FirstOrDefault(q => q.Key == cell.QuestionKey)?.Text ?? cell.QuestionKey,
            SourceId = cell.SourceId,
            Stance = cell.Stance.ToString(),
            EvidenceIdsJson = AcademicEvidenceRowMapper.Serialize(cell.EvidenceIds.Select(id => (object)id)),
            HasVerifiableLocator = cell.HasVerifiableLocator,
            AccessLevel = cell.AccessLevel.ToString(),
            Provenance = cell.Provenance.ToString(),
            Quality = cell.Quality.ToString(),
            Screening = cell.Screening.ToString(),
            FlagsJson = AcademicEvidenceRowMapper.Serialize(cell.Flags.Select(f => (object)new
            {
                code = f.Code,
                severity = f.Severity.ToString(),
                message = f.Message,
            })),
            BuiltAtUtc = when,
        }).ToList();

        var findings = matrix.Relationships.Select(relationship => new AcademicMatrixFindingEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = matrix.WorkspaceId,
            ProjectId = matrix.ProjectId,
            FindingType = "relationship",
            QuestionKey = relationship.QuestionKey,
            Code = relationship.Relation.ToString(),
            LeftSourceId = relationship.LeftSourceId,
            RightSourceId = relationship.RightSourceId,
            Severity = relationship.Relation == ClaimRelation.Contradiction
                ? FindingSeverity.Warning.ToString()
                : FindingSeverity.Note.ToString(),
            Explanation = relationship.Explanation ?? string.Empty,
            SourceIdsJson = "[]",
            ReasonCodesJson = AcademicEvidenceRowMapper.Serialize(relationship.ReasonCodes.Select(c => (object)c)),
            BuiltAtUtc = when,
        }).Concat(matrix.Gaps.Select(gap => new AcademicMatrixFindingEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = matrix.WorkspaceId,
            ProjectId = matrix.ProjectId,
            FindingType = "gap",
            QuestionKey = gap.QuestionKey ?? string.Empty,
            Code = gap.Kind.ToString(),
            Severity = gap.Severity.ToString(),
            Explanation = gap.Description,
            SourceIdsJson = AcademicEvidenceRowMapper.Serialize(gap.SourceIds.Select(id => (object)id)),
            ReasonCodesJson = "[]",
            SuggestedStep = gap.SuggestedStep,
            BuiltAtUtc = when,
        })).ToList();

        if (cells.Count > 0) await _db.Set<AcademicMatrixCellEntity>().AddRangeAsync(cells, cancellationToken);
        if (findings.Count > 0) await _db.Set<AcademicMatrixFindingEntity>().AddRangeAsync(findings, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AppendAuditAsync(
        IEnumerable<ProvenanceAuditEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var rows = events.Select(e => new AcademicAuditEventEntity
        {
            Sequence = e.Sequence,
            SourceId = e.SourceId,
            WorkspaceId = e.WorkspaceId,
            EventKind = e.EventKind,
            ActorKind = e.ActorKind.ToString(),
            ActorId = e.ActorId,
            PayloadCanonical = e.PayloadCanonical,
            OccurredAtUtc = e.OccurredAtUtc.UtcDateTime,
            PreviousHash = e.PreviousHash,
            EntryHash = e.EntryHash,
        }).ToList();

        if (rows.Count == 0) return;

        await _db.Set<AcademicAuditEventEntity>().AddRangeAsync(rows, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertAsync<TEntity>(
        Microsoft.EntityFrameworkCore.DbSet<TEntity> set,
        TEntity candidate,
        System.Linq.Expressions.Expression<Func<TEntity, bool>> matcher,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var existing = await set.FirstOrDefaultAsync(matcher, cancellationToken);
        if (existing is null)
        {
            await set.AddAsync(candidate, cancellationToken);
            return;
        }

        // The candidate carries a freshly minted surrogate key; copying it onto the
        // stored row would ask the database to move a primary key.
        var candidateEntry = _db.Entry(candidate);
        foreach (var keyProperty in candidateEntry.Metadata.FindPrimaryKey().Properties)
        {
            candidateEntry.Property(keyProperty.Name).CurrentValue =
                _db.Entry(existing).Property(keyProperty.Name).CurrentValue;
        }

        _db.Entry(existing).CurrentValues.SetValues(candidate);
    }

    private AcademicEvidenceSnapshot BuildSnapshot(
        AcademicSourceEntity row,
        AcademicProvenanceEntity? provenance,
        AcademicScreeningEntity? screening,
        IReadOnlyList<AcademicEvidenceItemEntity> evidence)
    {
        var record = AcademicEvidenceRowMapper.ToRecord(row);
        var provenanceModel = provenance is null
            ? new SourceProvenance { SourceId = row.Id, WorkspaceId = row.WorkspaceId }
            : AcademicEvidenceRowMapper.ToProvenance(provenance);

        QualityScreenResult? quality = null;
        InclusionDecision? decision = null;

        if (screening is not null)
        {
            // Rebuilt from the same rows that produced them, so the explanation a
            // reviewer read yesterday is still there today.
            quality = AcademicEvidenceRowMapper.ToQuality(screening);
            decision = AcademicEvidenceRowMapper.ToDecision(screening);
        }

        return new AcademicEvidenceSnapshot(
            record,
            provenanceModel,
            quality,
            decision,
            screening is null ? null : AcademicEvidenceRowMapper.ToBias(screening),
            [.. evidence.Select(AcademicEvidenceRowMapper.ToItem)]);
    }

    private static IReadOnlyList<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

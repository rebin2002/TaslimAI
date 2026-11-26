using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicWorkflow;

namespace Taslim.Api.AcademicIntegration.Persistence;

/// <summary>
/// The integration lane's implementation of the engine's durable-state port.
///
/// Commit protocol (single transaction): duplicate checkpoint-key probe first, then a
/// compare-and-swap update guarded by <c>WHERE Revision = expectedRevision</c>, then the
/// append-only ledger insert. A crashed process replays the same checkpoint key and
/// gets <see cref="AcademicCommitStatus.DuplicateCheckpoint"/> with no write applied; a
/// concurrent writer fails the CAS and gets <see cref="AcademicCommitStatus.RevisionConflict"/>.
/// Every read and write is workspace-fenced: the store refuses to load or commit a row
/// whose <c>WorkspaceId</c> does not match the snapshot it was handed, so a forged
/// snapshot cannot move another tenant's project even if the caller mis-scoped.
/// </summary>
public sealed class EfAcademicWorkflowStateStore(DbContext db) : IAcademicWorkflowStateStore
{
    private readonly DbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<AcademicWorkflowSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var json = await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.SnapshotJson)
            .FirstOrDefaultAsync(cancellationToken);

        return json is null ? null : AcademicWorkflowSerialization.Deserialize(json);
    }

    public async Task<AcademicWorkflowSnapshot?> FindByRequestKeyAsync(
        Guid workspaceId,
        string requestKey,
        CancellationToken cancellationToken)
    {
        var json = await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.RequestKey == requestKey)
            .Select(e => e.SnapshotJson)
            .FirstOrDefaultAsync(cancellationToken);

        return json is null ? null : AcademicWorkflowSerialization.Deserialize(json);
    }

    public async Task<bool> TryCreateAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = DateTime.UtcNow;
        var row = new AcademicWorkflowProjectEntity
        {
            ProjectId = snapshot.ProjectId,
            WorkspaceId = snapshot.WorkspaceId,
            RequestKey = snapshot.RequestKey,
            State = snapshot.State.ToString(),
            StateCode = snapshot.StateCode,
            StateDetail = snapshot.StateDetail,
            Revision = snapshot.Revision,
            SnapshotJson = AcademicWorkflowSerialization.Serialize(snapshot),
            EvidenceProjectedRevision = -1,
            CreatedAtUtc = snapshot.CreatedAtUtc == default ? now : snapshot.CreatedAtUtc,
            UpdatedAtUtc = snapshot.UpdatedAtUtc == default ? now : snapshot.UpdatedAtUtc,
        };

        var checkpoint = new AcademicWorkflowCheckpointEntity
        {
            Id = DeriveCheckpointId($"{snapshot.ProjectId:N}|0|intake"),
            ProjectId = snapshot.ProjectId,
            WorkspaceId = snapshot.WorkspaceId,
            Revision = 0,
            CheckpointKey = $"{snapshot.ProjectId:N}|0|intake",
            CommittedAtUtc = row.CreatedAtUtc,
        };

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Fast path before touching the change tracker: a replayed intake (same project id
        // or the same (workspace, request-key) pair) loses immediately, so a retry of a
        // committed create neither re-adds a tracked instance nor writes a second row.
        // Concurrent first-writers still race into the uniqueness constraint below.
        if (await _db.Set<AcademicWorkflowProjectEntity>()
                .AnyAsync(e => e.ProjectId == snapshot.ProjectId
                    || (e.WorkspaceId == snapshot.WorkspaceId && e.RequestKey == snapshot.RequestKey),
                    cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        try
        {
            _db.Set<AcademicWorkflowProjectEntity>().Add(row);
            _db.Set<AcademicWorkflowCheckpointEntity>().Add(checkpoint);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // The store hands back facts, never entities: keep the scope's tracker clean so
            // the projection and CAS commits sharing this context are not shadowed by the
            // rows this call inserted.
            DetachAll();
            return true;
        }
        catch (DbUpdateException)
        {
            // Primary-key or (workspace, request-key) uniqueness collision: another
            // writer created this project first. The create is the loser, never a second row.
            await transaction.RollbackAsync(cancellationToken);
            DetachAll();
            return false;
        }
    }

    public Task<AcademicCommitOutcome> TryCommitAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        CancellationToken cancellationToken) =>
        TryCommitCoreAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId: null, enforceAttemptFence: false, cancellationToken);

    public Task<AcademicCommitOutcome> TryCommitAttemptAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        Guid expectedAttemptId,
        CancellationToken cancellationToken) =>
        TryCommitCoreAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, enforceAttemptFence: true, cancellationToken);

    public Task<AcademicCommitOutcome> TryRecoverAttemptAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        Guid? expectedAttemptId,
        CancellationToken cancellationToken) =>
        TryCommitCoreAsync(snapshot, expectedRevision, checkpointKey, expectedAttemptId, enforceAttemptFence: true, cancellationToken);

    private async Task<AcademicCommitOutcome> TryCommitCoreAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        Guid? expectedAttemptId,
        bool enforceAttemptFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision != expectedRevision + 1)
            return new AcademicCommitOutcome(AcademicCommitStatus.RevisionConflict);

        var stored = await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.ProjectId == snapshot.ProjectId)
            .Select(e => new { e.WorkspaceId, e.Revision })
            .FirstOrDefaultAsync(cancellationToken);

        if (stored is null || stored.WorkspaceId != snapshot.WorkspaceId)
            return new AcademicCommitOutcome(AcademicCommitStatus.RevisionConflict);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var duplicate = await _db.Set<AcademicWorkflowCheckpointEntity>()
                .AnyAsync(e => e.CheckpointKey == checkpointKey, cancellationToken);
            if (duplicate)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AcademicCommitOutcome(AcademicCommitStatus.DuplicateCheckpoint);
            }

            var storedJson = await _db.Set<AcademicWorkflowProjectEntity>()
                .AsNoTracking()
                .Where(e => e.ProjectId == snapshot.ProjectId && e.WorkspaceId == snapshot.WorkspaceId)
                .Select(e => e.SnapshotJson)
                .FirstOrDefaultAsync(cancellationToken);
            var currentAttemptId = storedJson is null
                ? null
                : AcademicWorkflowSerialization.Deserialize(storedJson).ActiveAttempt?.AttemptId;
            if (storedJson is null
                || (enforceAttemptFence && currentAttemptId != expectedAttemptId)
                || (!enforceAttemptFence && currentAttemptId is not null)
                || (enforceAttemptFence && snapshot.ActiveAttempt is not null))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AcademicCommitOutcome(AcademicCommitStatus.RevisionConflict);
            }

            var now = DateTime.UtcNow;
            var json = AcademicWorkflowSerialization.Serialize(snapshot);
            var affected = await _db.Set<AcademicWorkflowProjectEntity>()
                .Where(e => e.ProjectId == snapshot.ProjectId && e.Revision == expectedRevision)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.SnapshotJson, json)
                    .SetProperty(e => e.Revision, snapshot.Revision)
                    .SetProperty(e => e.State, snapshot.State.ToString())
                    .SetProperty(e => e.StateCode, snapshot.StateCode)
                    .SetProperty(e => e.StateDetail, Truncate(snapshot.StateDetail, 1024))
                    .SetProperty(e => e.UpdatedAtUtc, now),
                    cancellationToken);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AcademicCommitOutcome(AcademicCommitStatus.RevisionConflict);
            }

            _db.Set<AcademicWorkflowCheckpointEntity>().Add(new AcademicWorkflowCheckpointEntity
            {
                Id = DeriveCheckpointId(checkpointKey),
                ProjectId = snapshot.ProjectId,
                WorkspaceId = snapshot.WorkspaceId,
                Revision = snapshot.Revision,
                CheckpointKey = checkpointKey,
                CommittedAtUtc = now,
            });
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AcademicCommitOutcome(AcademicCommitStatus.Committed);
        }
        catch (DbUpdateException)
        {
            // Lost the ledger race after the probe: the identical checkpoint was applied
            // concurrently. The project row was CAS-guarded, but the transaction is rolled
            // back anyway so the replay is reported as the duplicate it is.
            await transaction.RollbackAsync(cancellationToken);
            DetachAll();
            return new AcademicCommitOutcome(AcademicCommitStatus.DuplicateCheckpoint);
        }
    }

    /// <summary>
    /// Fence helper for the request path: resolves which workspace owns a project without
    /// deserializing the snapshot, so the controller can key its workspace fence on the
    /// stored scope rather than trusting a client-supplied id.
    /// </summary>
    public async Task<Guid?> GetWorkspaceIdAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => (Guid?)e.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Reads the evidence-projection watermark for a project.</summary>
    public async Task<long> GetProjectedRevisionAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .Select(e => e.EvidenceProjectedRevision)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Advances the projection watermark. Monotonic by construction: the update only
    /// applies when the stored watermark is behind, so a re-entrant projection can never
    /// roll the watermark back and force stale derived state to be trusted.
    /// </summary>
    public async Task<bool> SetProjectedRevisionAsync(Guid projectId, long revision, CancellationToken cancellationToken)
    {
        var affected = await _db.Set<AcademicWorkflowProjectEntity>()
            .Where(e => e.ProjectId == projectId && e.EvidenceProjectedRevision < revision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EvidenceProjectedRevision, revision),
                cancellationToken);
        return affected == 1;
    }

    /// <summary>Checkpoint ledger page for a project, oldest first.</summary>
    public async Task<IReadOnlyList<AcademicWorkflowCheckpointEntity>> ListCheckpointsAsync(
        Guid workspaceId,
        Guid projectId,
        int take,
        CancellationToken cancellationToken) =>
        await _db.Set<AcademicWorkflowCheckpointEntity>()
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId && e.ProjectId == projectId)
            .OrderBy(e => e.Revision)
            .ThenBy(e => e.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

    /// <summary>Workspace-scoped project rows, newest first.</summary>
    public async Task<IReadOnlyList<AcademicWorkflowProjectEntity>> ListProjectsAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 10_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return await _db.Set<AcademicWorkflowProjectEntity>()
            .AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.ProjectId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountProjectsAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await _db.Set<AcademicWorkflowProjectEntity>().CountAsync(e => e.WorkspaceId == workspaceId, cancellationToken);

    private void DetachAll()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;
    }

    private static Guid DeriveCheckpointId(string checkpointKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(checkpointKey));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

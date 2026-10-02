using Taslim.Api.Persistence;

namespace Taslim.Api.Autopilot;

/// <summary>
/// Append-only audit writer. Every controller decision is recorded with reason,
/// status, attempt, task id, branch, and SHA so the autonomous flow is fully
/// auditable after the fact.
/// </summary>
public interface IAutopilotAuditLog
{
    Task RecordAsync(AutopilotAuditEvent entry, CancellationToken cancellationToken = default);
}

public sealed class EfAutopilotAuditLog(TaslimDbContext db) : IAutopilotAuditLog
{
    public async Task RecordAsync(AutopilotAuditEvent entry, CancellationToken cancellationToken = default)
    {
        if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
        if (entry.CreatedAt == default) entry.CreatedAt = DateTime.UtcNow;
        db.AutopilotAuditEvents.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public static class AutopilotAuditFactory
{
    public static AutopilotAuditEvent Create(
        string action,
        string outcome,
        string? reason = null,
        string? statusDetail = null,
        string? waveKey = null,
        string? taskId = null,
        string? runState = null,
        string? taskState = null,
        int attempt = 0,
        string? branch = null,
        string? baseSha = null,
        string? candidateSha = null,
        Guid? targetId = null,
        Guid? actorUserId = null,
        string? requestId = null,
        bool dryRun = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            Action = action,
            Outcome = outcome,
            Reason = Bounded(reason, 400),
            StatusDetail = Bounded(statusDetail, 600),
            WaveKey = Bounded(waveKey, 120),
            TaskId = Bounded(taskId, 200),
            RunState = runState,
            TaskState = taskState,
            Attempt = attempt,
            Branch = Bounded(branch, 300),
            BaseSha = Bounded(baseSha, 64),
            CandidateSha = Bounded(candidateSha, 64),
            TargetType = "autopilot",
            TargetId = targetId,
            ActorUserId = actorUserId,
            RequestId = Bounded(requestId, 200),
            DryRun = dryRun,
            CreatedAt = DateTime.UtcNow,
        };

    private static string? Bounded(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
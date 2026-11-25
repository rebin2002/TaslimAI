namespace Taslim.Api.AcademicWorkflow;

/// <summary>
/// Durable-state port for the engine. The integration lane implements this over the
/// persistence stack (a JSONB snapshot column with an optimistic concurrency token and
/// a checkpoint key uniqueness index is the recommended shape); the engine itself never
/// touches a database, HTTP client, or provider.
/// </summary>
public interface IAcademicWorkflowStateStore
{
    Task<AcademicWorkflowSnapshot?> LoadAsync(Guid projectId, CancellationToken cancellationToken);

    Task<AcademicWorkflowSnapshot?> FindByRequestKeyAsync(Guid workspaceId, string requestKey, CancellationToken cancellationToken);

    /// <summary>Creates the revision-0 snapshot. Returns false when the request key already exists.</summary>
    Task<bool> TryCreateAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically swaps the stored snapshot when its revision equals <paramref name="expectedRevision"/>.
    /// A repeated checkpoint key must return <see cref="AcademicCommitStatus.DuplicateCheckpoint"/>
    /// without applying the write, which makes checkpoint commits idempotent across crashes.
    /// </summary>
    Task<AcademicCommitOutcome> TryCommitAsync(
        AcademicWorkflowSnapshot snapshot,
        long expectedRevision,
        string checkpointKey,
        CancellationToken cancellationToken);
}

public enum AcademicCommitStatus
{
    Committed = 0,
    RevisionConflict = 1,

    /// <summary>This exact checkpoint was already applied; safe no-op on resume.</summary>
    DuplicateCheckpoint = 2,
}

public sealed record AcademicCommitOutcome(AcademicCommitStatus Status);

/// <summary>
/// A stage worker port. Implementations live in the provider lanes (OpenAlex/Crossref
/// discovery, screening policy, extraction, synthesis, chapter drafting, final
/// verification) and must be side-effect-free against durable state: they return typed
/// output and measured usage, and the engine alone commits checkpoints.
/// </summary>
public interface IAcademicStageHandler
{
    AcademicStage Stage { get; }

    /// <summary>Cost ceiling the engine preflights and reserves before invoking the handler.</summary>
    AcademicStageEstimate Estimate(AcademicStageContext context);

    Task<AcademicStageOutcome> ExecuteAsync(AcademicStageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Everything a stage handler may see. The snapshot is a private clone: mutating it does
/// not affect durable state, so only returned outputs can advance the workflow.
/// </summary>
public sealed record AcademicStageContext
{
    public required AcademicWorkflowSnapshot Snapshot { get; init; }
    public required AcademicStage Stage { get; init; }

    /// <summary>projectId|stage|fingerprint; the handler should use it for provider-side idempotency too.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>1-based attempt number within the current input fingerprint lineage.</summary>
    public required int Attempt { get; init; }

    /// <summary>Reviewer notes after a RequestRevision decision, when applicable.</summary>
    public string? ApprovalFeedback { get; init; }
}

public enum AcademicWorkflowRunStatus
{
    /// <summary>Verification passed and all required approvals are in; the project is exported.</summary>
    Completed = 0,

    /// <summary>A human approval checkpoint is pending; the engine will not advance past it.</summary>
    AwaitingApproval = 1,

    /// <summary>An attempt preflight or metering refused to spend; resumable after budget changes.</summary>
    PausedBudget = 2,

    /// <summary>A retryable stage failure; the next resume re-runs this stage.</summary>
    StageFailure = 3,

    /// <summary>Terminal failure: attempts exhausted, integrity-mandated stop, or missing handler.</summary>
    ProjectFailed = 4,

    /// <summary>An approval rejected the checkpoint; the project is cancelled.</summary>
    Cancelled = 5,

    /// <summary>Another writer committed first; reload and resume.</summary>
    RevisionConflict = 6,

    NotFound = 7,
}

public sealed record AcademicWorkflowRunResult
{
    public required AcademicWorkflowRunStatus Status { get; init; }
    public AcademicWorkflowSnapshot? Snapshot { get; init; }
    public string? Code { get; init; }
    public string? Detail { get; init; }
    public AcademicStage? Stage { get; init; }

    public static AcademicWorkflowRunResult From(AcademicWorkflowRunStatus status, AcademicWorkflowSnapshot? snapshot, AcademicStage? stage = null, string? code = null, string? detail = null) =>
        new() { Status = status, Snapshot = snapshot, Stage = stage, Code = code, Detail = detail };
}

public enum AcademicApprovalSubmissionStatus
{
    Applied = 0,

    /// <summary>The identical decision was already recorded; no state moved.</summary>
    AlreadyApplied = 1,
    NoPendingApproval = 2,
    ProjectNotRunning = 3,
    NotFound = 4,
    RevisionConflict = 5,
    CheckpointMismatch = 6,
    DecisionConflict = 7,
}

public sealed record AcademicApprovalSubmissionResult
{
    public required AcademicApprovalSubmissionStatus Status { get; init; }
    public AcademicWorkflowSnapshot? Snapshot { get; init; }

    /// <summary>Set when status is ProjectNotRunning or the project is terminal.</summary>
    public string? Code { get; init; }
}

public sealed class AcademicWorkflowEngineOptions
{
    /// <summary>
    /// Hard bound on stages executed per resume slice, independent of stage count, so a
    /// defective handler cannot spin the loop. Also the effective max when a slice is
    /// requested: the engine stops cleanly afterwards and the caller resumes.
    /// </summary>
    public int MaxStagesPerSlice { get; set; } = 7;
}

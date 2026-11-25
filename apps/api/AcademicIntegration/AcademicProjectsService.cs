using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;

namespace Taslim.Api.AcademicIntegration;

/// <summary>Machine-readable outcomes the controller maps onto HTTP statuses.</summary>
public enum AcademicProjectServiceStatus
{
    Ok = 0,
    NotFound = 1,
    Validation = 2,
    NotConnected = 3,
    Conflict = 4,
}

public sealed record AcademicProjectServiceResult
{
    public required AcademicProjectServiceStatus Status { get; init; }
    public AcademicWorkflowSnapshot? Snapshot { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    /// <summary>True when this call newly created the project (versus replaying intake).</summary>
    public bool Created { get; init; }

    /// <summary>True when the evidence projection ran (or was confirmed current) for the returned snapshot.</summary>
    public bool EvidenceProjected { get; init; }

    /// <summary>Stage whose handler is not wired, for the honest 501 payload.</summary>
    public AcademicStage? MissingStage { get; init; }

    public static AcademicProjectServiceResult NotFound(string code = "PROJECT_NOT_FOUND") =>
        new() { Status = AcademicProjectServiceStatus.NotFound, Code = code };

    public static AcademicProjectServiceResult Invalid(string detail) =>
        new() { Status = AcademicProjectServiceStatus.Validation, Code = "ACADEMIC_REQUEST_INVALID", Detail = detail };
}

public sealed record AcademicProjectCreateInput
{
    public required string RequestKey { get; init; }
    public required string ResearchQuestion { get; init; }
    public required string Discipline { get; init; }
    public AcademicResearchLevel Level { get; init; } = AcademicResearchLevel.Masters;
    public AcademicMethod Method { get; init; } = AcademicMethod.SystematicReview;
    public IReadOnlyList<AcademicMethod> SelectedMethods { get; init; } = [];
    public AcademicCitationStyle CitationStyle { get; init; } = AcademicCitationStyle.Apa7;
    public string Language { get; init; } = "en";
    public int MaxDiscoverySources { get; init; } = 120;
    public int MaxEvidencePerSource { get; init; } = 6;
    public IReadOnlyList<string> TargetChapterHeadings { get; init; } = [];
    public string? WorkingTitle { get; init; }
    public string? GeographicScope { get; init; }
    public string? PopulationOrContext { get; init; }
    public string? TimePeriod { get; init; }
    public IReadOnlyList<string> SubQuestions { get; init; } = [];
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public int? MinimumSources { get; init; }
    public int? MinimumPeerReviewed { get; init; }
    public IReadOnlyList<string> AllowedSourceTypes { get; init; } = [];
    public bool RequireFullText { get; init; }
    public string? ProgrammeRequirements { get; init; }
    public IReadOnlyList<AcademicApprovalKind> RequiredApprovals { get; init; } =
    [
        AcademicApprovalKind.Plan,
        AcademicApprovalKind.SourceLibrary,
        AcademicApprovalKind.Synthesis,
        AcademicApprovalKind.FinalVerification,
    ];
    public int MaxStageAttempts { get; init; } = 3;
    public AcademicBudgetPolicy Budget { get; init; } = new();
}

public sealed record AcademicBudgetRaiseInput
{
    public decimal? MaxTotalUsd { get; init; }
    public decimal? MaxStageUsd { get; init; }
    public int? MaxTotalCalls { get; init; }
    public int? MaxStageCalls { get; init; }
}

/// <summary>
/// The API-facing orchestration lane for academic projects.
///
/// Responsibilities the Phase C engine deliberately left to the integration layer:
/// workspace fencing (which project belongs to which tenant), idempotent intake
/// (create is a single attempt; a replayed request key resolves to the same project
/// with no second row and no extra charge), the connectivity gate (an unimplemented
/// stage is reported as not-connected instead of being run by a stub or silently
/// skipped, so nothing is ever presented as functional before its provider lane is
/// wired), resumable execution through the engine's checkpoint protocol, and the
/// evidence projection watermark that keeps the Phase B tables consistent with the
/// last committed snapshot.
/// </summary>
public sealed class AcademicProjectsService(
    EfAcademicWorkflowStateStore store,
    AcademicWorkflowOrchestrator engine,
    AcademicEvidenceProjector projector,
    TimeProvider clock,
    IEnumerable<IAcademicStageHandler> stageHandlers,
    AcademicProjectionGate projectionGate,
    ILogger<AcademicProjectsService> logger)
{
    /// <summary>Intake ceilings enforced before validation, so a bad value aborts rather than shrinks.</summary>
    public const int AbsoluteMaxDiscoverySources = 400;
    public const int AbsoluteMaxEvidencePerSource = 20;

    private readonly HashSet<AcademicStage> connectedStages =
        [.. stageHandlers.Select(handler => handler.Stage)];

    /// <summary>
    /// Stages whose handler is wired into this process. The deterministic Plan handler is
    /// local; later stages are added only through <c>AddAcademicStageHandler</c> as their
    /// independently reviewed lanes land.
    /// </summary>
    public IReadOnlySet<AcademicStage> ConnectedStages => connectedStages;

    public async Task<AcademicProjectServiceResult> CreateAsync(
        Guid workspaceId,
        AcademicProjectCreateInput input,
        CancellationToken cancellationToken)
    {
        var request = new AcademicWorkflowRequest
        {
            WorkspaceId = workspaceId,
            RequestKey = (input.RequestKey ?? string.Empty).Trim(),
            ResearchQuestion = (input.ResearchQuestion ?? string.Empty).Trim(),
            Discipline = (input.Discipline ?? string.Empty).Trim(),
            Level = input.Level,
            Method = input.Method,
            SelectedMethods = input.SelectedMethods,
            CitationStyle = input.CitationStyle,
            Language = string.IsNullOrWhiteSpace(input.Language) ? "en" : input.Language.Trim(),
            MaxDiscoverySources = input.MaxDiscoverySources,
            MaxEvidencePerSource = input.MaxEvidencePerSource,
            TargetChapterHeadings = input.TargetChapterHeadings,
            WorkingTitle = input.WorkingTitle,
            GeographicScope = input.GeographicScope,
            PopulationOrContext = input.PopulationOrContext,
            TimePeriod = input.TimePeriod,
            SubQuestions = input.SubQuestions,
            Keywords = input.Keywords,
            MinimumSources = input.MinimumSources,
            MinimumPeerReviewed = input.MinimumPeerReviewed,
            AllowedSourceTypes = input.AllowedSourceTypes,
            RequireFullText = input.RequireFullText,
            ProgrammeRequirements = input.ProgrammeRequirements,
            Budget = input.Budget,
            RequiredApprovals = input.RequiredApprovals,
            MaxStageAttempts = input.MaxStageAttempts,
        };

        try
        {
            request.Validate();
        }
        catch (AcademicWorkflowRequestException exception)
        {
            return AcademicProjectServiceResult.Invalid(exception.Message);
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Idempotent intake without a lookup-then-insert race: the project id derives from
        // (workspace, request key), so a replay collides with the unique index inside
        // TryCreateAsync and is answered from the stored row.
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = AcademicWorkflowOrchestrator.DeriveProjectId(workspaceId, request.RequestKey),
            WorkspaceId = workspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            State = AcademicProjectState.Running,
            Revision = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        if (!await store.TryCreateAsync(snapshot, cancellationToken))
        {
            var existing = await store.FindByRequestKeyAsync(workspaceId, request.RequestKey, cancellationToken);
            if (existing is null)
                return new AcademicProjectServiceResult
                {
                    Status = AcademicProjectServiceStatus.Conflict,
                    Code = "STATE_STORE_CONFLICT",
                    Detail = "Intake collided with an existing request key but the project could not be loaded.",
                };
            return new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = existing,
                Created = false,
                Detail = "The same request key was already accepted; nothing new was created or charged.",
            };
        }

        return new AcademicProjectServiceResult
        {
            Status = AcademicProjectServiceStatus.Ok,
            Snapshot = snapshot,
            Created = true,
        };
    }

    public async Task<AcademicWorkflowSnapshot?> LoadFencedAsync(
        Guid workspaceId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var owner = await store.GetWorkspaceIdAsync(projectId, cancellationToken);
        if (owner is null || owner.Value != workspaceId) return null;
        return await store.LoadAsync(projectId, cancellationToken);
    }

    /// <summary>Resolves a project's owning workspace for the controller fence. Null when unknown.</summary>
    public Task<Guid?> ResolveWorkspaceAsync(Guid projectId, CancellationToken cancellationToken) =>
        store.GetWorkspaceIdAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<AcademicWorkflowProjectEntity>> ListAsync(
        Guid workspaceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        store.ListProjectsAsync(workspaceId, page, pageSize, cancellationToken);

    public Task<int> CountAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        store.CountProjectsAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<AcademicWorkflowCheckpointEntity>> CheckpointsAsync(
        Guid workspaceId,
        Guid projectId,
        int take,
        CancellationToken cancellationToken) =>
        store.ListCheckpointsAsync(workspaceId, projectId, take, cancellationToken);

    /// <summary>
    /// Advances one resume slice. The run endpoint is the only place the engine executes
    /// stage work from the API: if the next actionable stage has no registered handler,
    /// the call reports the exact missing stage with <see cref="AcademicProjectServiceStatus.NotConnected"/>
    /// and does not touch durable state. A project can therefore sit at
    /// <c>Running</c>/revision-0 for days while lanes land, and every completed stage on a
    /// partially connected project still resumes without re-running committed checkpoints.
    /// </summary>
    public async Task<AcademicProjectServiceResult> RunAsync(Guid workspaceId, Guid projectId, CancellationToken cancellationToken)
    {
        var snapshot = await LoadFencedAsync(workspaceId, projectId, cancellationToken);
        if (snapshot is null) return AcademicProjectServiceResult.NotFound();

        var nextStage = FirstActionableStage(snapshot);
        if (snapshot.State == AcademicProjectState.Running && nextStage is AcademicStage stage && !ConnectedStages.Contains(stage))
        {
            return new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.NotConnected,
                Snapshot = snapshot,
                MissingStage = stage,
                Code = "ACADEMIC_STAGE_NOT_CONNECTED",
                Detail = $"The {stage} stage has no provider handler registered in this build; the project stays resumable at revision {snapshot.Revision}.",
            };
        }

        var run = await engine.ResumeAsync(projectId, cancellationToken);
        if (run.Snapshot is null)
        {
            return new AcademicProjectServiceResult
            {
                Status = run.Status == AcademicWorkflowRunStatus.NotFound
                    ? AcademicProjectServiceStatus.NotFound
                    : AcademicProjectServiceStatus.Conflict,
                Code = run.Code ?? "RUN_FAILED",
                Detail = run.Detail ?? string.Empty,
            };
        }

        // A resume that lost the checkpoint race returns the loser's copy of the tree.
        // Projecting it here would rewrite the derived evidence tables from state the
        // durable store already rejected, so the conflict path never projects; the
        // winner's response projected the committed revision.
        var projected = run.Status != AcademicWorkflowRunStatus.RevisionConflict
            && await ProjectEvidenceAsync(run.Snapshot, cancellationToken);
        return new AcademicProjectServiceResult
        {
            Status = AcademicProjectServiceStatus.Ok,
            Snapshot = run.Snapshot,
            EvidenceProjected = projected,
            Code = run.Code ?? string.Empty,
            Detail = run.Detail ?? string.Empty,
        };
    }

    public async Task<AcademicProjectServiceResult> SubmitApprovalAsync(
        Guid workspaceId,
        Guid projectId,
        AcademicApprovalKind kind,
        AcademicApprovalDecision decision,
        string? notes,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadFencedAsync(workspaceId, projectId, cancellationToken);
        if (snapshot is null) return AcademicProjectServiceResult.NotFound();

        var submission = await engine.SubmitApprovalDecisionAsync(projectId, kind, decision, notes, userId, cancellationToken);
        var resulting = submission.Snapshot ?? snapshot;

        return submission.Status switch
        {
            AcademicApprovalSubmissionStatus.NotFound => AcademicProjectServiceResult.NotFound(),
            AcademicApprovalSubmissionStatus.RevisionConflict => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Conflict,
                Snapshot = resulting,
                Code = "CHECKPOINT_CONFLICT",
                Detail = "Another writer advanced the project first; reload and retry.",
            },
            AcademicApprovalSubmissionStatus.NoPendingApproval => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Conflict,
                Snapshot = resulting,
                Code = "NO_PENDING_APPROVAL",
                Detail = $"No {kind} checkpoint is waiting for a decision.",
            },
            AcademicApprovalSubmissionStatus.ProjectNotRunning => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Conflict,
                Snapshot = resulting,
                Code = resulting.StateCode ?? "PROJECT_NOT_RUNNING",
                Detail = $"The project is {resulting.State}; a decision no longer applies.",
            },
            AcademicApprovalSubmissionStatus.AlreadyApplied => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = resulting,
                Code = "ALREADY_APPLIED",
                Detail = "This checkpoint decision was already recorded; no state moved.",
            },
            _ => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = resulting,
                Code = "APPLIED",
                Detail = "The decision was recorded against the pending checkpoint.",
            },
        };
    }

    /// <summary>
    /// Raises a paused project's budget ceilings. Ceilings may only increase, are re-validated
    /// by the policy itself (out-of-range aborts; nothing silently shrinks), and the change is
    /// committed as an ordinary checkpoint so every later resume replays deterministically.
    /// </summary>
    public async Task<AcademicProjectServiceResult> RaiseBudgetAsync(
        Guid workspaceId,
        Guid projectId,
        AcademicBudgetRaiseInput input,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadFencedAsync(workspaceId, projectId, cancellationToken);
        if (snapshot is null) return AcademicProjectServiceResult.NotFound();

        if (snapshot.State is AcademicProjectState.Completed or AcademicProjectState.Cancelled or AcademicProjectState.Failed)
        {
            return new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Conflict,
                Snapshot = snapshot,
                Code = "PROJECT_TERMINAL",
                Detail = $"A {snapshot.State} project's budget can no longer change.",
            };
        }

        var current = snapshot.Request.Budget;
        var proposed = current with
        {
            MaxTotalUsd = input.MaxTotalUsd is null || input.MaxTotalUsd <= 0m
                ? current.MaxTotalUsd
                : Math.Max(current.MaxTotalUsd, input.MaxTotalUsd.Value),
            MaxStageUsd = input.MaxStageUsd is null || input.MaxStageUsd <= 0m
                ? current.MaxStageUsd
                : Math.Max(current.MaxStageUsd, input.MaxStageUsd.Value),
            MaxTotalProviderCalls = input.MaxTotalCalls is null || input.MaxTotalCalls <= 0
                ? current.MaxTotalProviderCalls
                : Math.Max(current.MaxTotalProviderCalls, input.MaxTotalCalls.Value),
            MaxStageProviderCalls = input.MaxStageCalls is null || input.MaxStageCalls <= 0
                ? current.MaxStageProviderCalls
                : Math.Max(current.MaxStageProviderCalls, input.MaxStageCalls.Value),
        };

        try
        {
            proposed.Validate();
        }
        catch (AcademicWorkflowRequestException exception)
        {
            return AcademicProjectServiceResult.Invalid(exception.Message);
        }

        // A raise that changes nothing must not move state: ceilings only ever increase,
        // so an equal proposal (including a "lowering" request clamped back to current)
        // answers without a checkpoint and the project stays paused exactly as it was.
        if (proposed == current)
        {
            return new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = snapshot,
                Code = "BUDGET_UNCHANGED",
                Detail = "Ceilings only increase; the request changed nothing.",
            };
        }

        // Request is init-only by design (the engine never rewrites intake), so the raise
        // round-trips through the canonical serializer: the budget node is replaced in the
        // stored shape and revalidated on deserialize. No hand-rolled JSON shape here;
        // AcademicWorkflowSerialization owns it.
        var updated = ReplaceBudget(snapshot, proposed);
        if (updated.State == AcademicProjectState.PausedBudget)
        {
            updated.State = AcademicProjectState.Running;
            updated.StateCode = null;
            updated.StateDetail = null;
        }

        updated.Revision = snapshot.Revision + 1;
        updated.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        var checkpointKey = AcademicIntegrationCheckpointKeys.BudgetRaise(projectId, updated.Revision, proposed);
        var commit = await store.TryCommitAsync(updated, snapshot.Revision, checkpointKey, cancellationToken);

        return commit.Status switch
        {
            AcademicCommitStatus.Committed => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = updated,
                Code = "BUDGET_RAISED",
                Detail = "Ceilings increased; the project is resumable.",
            },
            AcademicCommitStatus.DuplicateCheckpoint => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = await store.LoadAsync(projectId, cancellationToken) ?? snapshot,
                Code = "BUDGET_ALREADY_APPLIED",
                Detail = "The identical raise was already committed.",
            },
            _ => new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Conflict,
                Snapshot = snapshot,
                Code = "CHECKPOINT_CONFLICT",
                Detail = "Another writer advanced the project first; reload and retry.",
            },
        };
    }

    /// <summary>
    /// Best-effort projection into the evidence lane with a durable watermark: if the
    /// request dies after the workflow commit but before the watermark advances, the next
    /// run re-projects the same state (rebuild semantics make it a no-op rewrite).
    ///
    /// Concurrency contract for the combined tree (committed snapshot + derived Phase B
    /// tables + watermark): two successful mutations can project overlapping revisions
    /// only if each writes the CURRENT committed revision, at most one writes per project
    /// at a time, and the watermark is claimed only while the tree still matches what was
    /// written. Without these fences a stale writer's rebuild rewrites the derived tables
    /// under a newer watermark — the monotonic claim silently swallows the late advance
    /// while the reads keep reporting <c>evidenceCurrent</c> for the wrong content.
    /// </summary>
    public async Task<bool> ProjectEvidenceAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!AcademicEvidenceProjector.HasProjectableArtifacts(snapshot)) return false;
        return await projectionGate.RunAsync(snapshot.ProjectId, () => ProjectFencedAsync(snapshot, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The projection attempt itself, always taken under the per-project gate so two
    /// concurrent mutations can never interleave their derived-table writes. Inside the
    /// gate the durable store — not the caller's copy — decides what is projected.
    /// </summary>
    private async Task<bool> ProjectFencedAsync(AcademicWorkflowSnapshot snapshot, CancellationToken cancellationToken)
    {
        var current = await store.LoadAsync(snapshot.ProjectId, cancellationToken);
        if (current is null) return false;

        var watermark = await store.GetProjectedRevisionAsync(current.ProjectId, cancellationToken);
        if (watermark >= current.Revision) return true;

        if (current.Revision != snapshot.Revision)
        {
            // A concurrent mutation committed newer state while this caller's snapshot was
            // in flight. Rewriting the tables from the older copy would leave the combined
            // tree stale under a watermark that claims it is current, so the tree honestly
            // stays stale and the newer writer's projection (or the next run) lands it.
            return false;
        }

        try
        {
            await projector.ProjectAsync(current, clock.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Projection failure never corrupts or reverses workflow state; the watermark
            // stays behind and the derived tables are marked stale in API responses.
            logger.LogWarning(exception,
                "Academic evidence projection failed for project {ProjectId} at revision {Revision}; it will be retried on the next run.",
                snapshot.ProjectId, snapshot.Revision);
            return false;
        }

        // Claim the watermark only while the committed tree still matches exactly what was
        // just written; a commit that landed during the writes makes this claim (and these
        // rows) stale, and the newer revision's own caller owns the next projection.
        var afterWrite = await store.LoadAsync(snapshot.ProjectId, cancellationToken);
        if (afterWrite is null || afterWrite.Revision != current.Revision) return false;

        await store.SetProjectedRevisionAsync(current.ProjectId, current.Revision, cancellationToken);
        return true;
    }

    /// <summary>True when the snapshot has unprojected committed state the evidence lane should hold.</summary>
    public async Task<bool> IsProjectionStaleAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var watermark = await store.GetProjectedRevisionAsync(projectId, cancellationToken);
        var snapshot = await store.LoadAsync(projectId, cancellationToken);
        return snapshot is not null
            && AcademicEvidenceProjector.HasProjectableArtifacts(snapshot)
            && watermark < snapshot.Revision;
    }

    private static AcademicStage? FirstActionableStage(AcademicWorkflowSnapshot snapshot)
    {
        foreach (var stage in AcademicStageOrder.Stages)
        {
            var record = snapshot.Stage(stage);
            if (record.State != AcademicStageState.Completed) return stage;
        }

        return null;
    }

    private static AcademicWorkflowSnapshot ReplaceBudget(AcademicWorkflowSnapshot snapshot, AcademicBudgetPolicy proposed)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(AcademicWorkflowSerialization.Serialize(snapshot))!
            .AsObject();
        var budget = root["request"]!.AsObject()["budget"]!.AsObject();
        budget["maxTotalUsd"] = proposed.MaxTotalUsd;
        budget["maxStageUsd"] = proposed.MaxStageUsd;
        budget["maxTotalProviderCalls"] = proposed.MaxTotalProviderCalls;
        budget["maxStageProviderCalls"] = proposed.MaxStageProviderCalls;
        budget["stageEstimateSafetyFactor"] = proposed.StageEstimateSafetyFactor;
        var copy = AcademicWorkflowSerialization.Deserialize(root.ToJsonString());
        copy.Request.Budget.Validate();
        return copy;
    }
}

/// <summary>Checkpoint keys for commits the integration lane makes outside the engine loop.</summary>
internal static class AcademicIntegrationCheckpointKeys
{
    public static string BudgetRaise(Guid projectId, long revision, AcademicBudgetPolicy policy)
    {
        var fingerprint = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{policy.MaxTotalUsd:0.####}|{policy.MaxStageUsd:0.####}|{policy.MaxTotalProviderCalls}|{policy.MaxStageProviderCalls}"));
        return $"{projectId:N}|{revision}|{Convert.ToHexString(fingerprint).ToLowerInvariant()}";
    }
}

/// <summary>
/// Per-project serialization of evidence-projection attempts inside this process: the
/// combined tree may have at most one in-flight projection per project, so the fenced
/// revision check and the derived-table writes cannot interleave with a competing
/// writer's. One instance handles the whole deployment (single Railway API service);
/// durable correctness still rests on the store's CAS commits and monotonic watermark,
/// which keep crash replays and cross-process writers honest even without the gate.
/// Gates are cheap and keyed per project id; they are intentionally never evicted, so
/// a waiter can never race a gate that is being removed underneath it.
/// </summary>
public sealed class AcademicProjectionGate
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public async Task<T> RunAsync<T>(Guid projectId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var gate = gates.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Taslim.Api.AcademicWorkflow;

/// <summary>
/// The pure Phase C engine: bounded, resumable, checkpointed orchestration over
/// plan → discover → screen → extract → synthesize → chapter draft → verify with
/// human approval gates. The orchestrator performs no I/O of its own: durable state
/// goes through <see cref="IAcademicWorkflowStateStore"/> and provider work through
/// <see cref="IAcademicStageHandler"/>, which keeps it fully testable with scripted
/// doubles and safe to host inside the existing generation-job pipeline later.
/// </summary>
public sealed partial class AcademicWorkflowOrchestrator
{
    private readonly IAcademicWorkflowStateStore store;
    private readonly Dictionary<AcademicStage, IAcademicStageHandler> handlers;
    private readonly TimeProvider clock;
    private readonly AcademicWorkflowEngineOptions options;

    public AcademicWorkflowOrchestrator(
        IAcademicWorkflowStateStore store,
        IEnumerable<IAcademicStageHandler> handlers,
        TimeProvider? clock = null,
        AcademicWorkflowEngineOptions? options = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.handlers = handlers.ToDictionary(handler => handler.Stage);
        this.clock = clock ?? TimeProvider.System;
        this.options = options ?? new AcademicWorkflowEngineOptions();
        if (this.options.MaxStagesPerSlice < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxStagesPerSlice must be at least 1.");
    }

    public Task<AcademicWorkflowSnapshot?> GetStateAsync(Guid projectId, CancellationToken cancellationToken) =>
        store.LoadAsync(projectId, cancellationToken);

    /// <summary>
    /// Creates a project revision-0 snapshot and runs it to the first blocking point.
    /// Calling again with the same workspace + request key resumes the existing project
    /// rather than starting a duplicate, so retried intake can never double-charge.
    /// </summary>
    public async Task<AcademicWorkflowRunResult> StartAsync(AcademicWorkflowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var existing = await store.FindByRequestKeyAsync(request.WorkspaceId, request.RequestKey, cancellationToken);
        if (existing is not null)
            return await ResumeAsync(existing.ProjectId, cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = new AcademicWorkflowSnapshot
        {
            ProjectId = DeriveProjectId(request.WorkspaceId, request.RequestKey),
            WorkspaceId = request.WorkspaceId,
            RequestKey = request.RequestKey,
            Request = request,
            State = AcademicProjectState.Running,
            Revision = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        if (!await store.TryCreateAsync(snapshot, cancellationToken))
        {
            var raced = await store.FindByRequestKeyAsync(request.WorkspaceId, request.RequestKey, cancellationToken);
            if (raced is null)
                return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.NotFound, null, code: "STATE_STORE_UNAVAILABLE");
            return AcademicWorkflowRunResult.From(MapStatus(raced.State), raced, code: raced.StateCode, detail: raced.StateDetail);
        }

        return await ResumeAsync(snapshot.ProjectId, cancellationToken);
    }

    /// <summary>
    /// Runs at most <see cref="AcademicWorkflowEngineOptions.MaxStagesPerSlice"/> stage
    /// attempts from the last committed checkpoint and stops at the first blocking point
    /// (approval gate, budget refusal, stage failure, or terminal state). Safe to call
    /// repeatedly and from any worker: completed stages are skipped via their committed
    /// idempotency keys, and a concurrent writer is rejected by the checkpoint revision.
    /// </summary>
    public async Task<AcademicWorkflowRunResult> ResumeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var sliceBudget = Math.Max(options.MaxStagesPerSlice, AcademicStageOrder.Stages.Count);
        for (var slice = 0; slice < sliceBudget; slice++)
        {
            var loaded = await store.LoadAsync(projectId, cancellationToken);
            if (loaded is null)
                return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.NotFound, null, code: "PROJECT_NOT_FOUND");

            if (loaded.State != AcademicProjectState.Running)
                return AcademicWorkflowRunResult.From(
                    MapStatus(loaded.State), loaded,
                    stage: loaded.PendingApproval?.Stage,
                    code: loaded.StateCode,
                    detail: loaded.StateDetail);

            var next = FirstActionableStage(loaded);
            if (next is null)
            {
                var done = loaded.Clone();
                done.State = AcademicProjectState.Completed;
                done.StateCode = null;
                done.StateDetail = null;
                var commit = await CommitAsync(done, loaded.Revision, $"project-complete|r{loaded.Revision + 1}", cancellationToken);
                if (commit == AcademicCommitStatus.RevisionConflict)
                    return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.RevisionConflict, loaded, code: "CHECKPOINT_CONFLICT");
                return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.Completed, done);
            }

            var outcome = await ExecuteStageAsync(loaded, next.Value, cancellationToken);
            if (outcome.TerminatesSlice)
                return outcome.Result;
        }

        var stalled = await store.LoadAsync(projectId, cancellationToken);
        if (stalled is null)
            return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.NotFound, null, code: "PROJECT_NOT_FOUND");
        return AcademicWorkflowRunResult.From(
            stalled.State == AcademicProjectState.Running
                ? AcademicWorkflowRunStatus.StageFailure
                : MapStatus(stalled.State),
            stalled,
            code: "SLICE_BOUND_EXCEEDED",
            detail: "The resume slice reached its stage bound without a blocking point; call resume again.");
    }

    private static AcademicStage? FirstActionableStage(AcademicWorkflowSnapshot snapshot)
    {
        foreach (var stage in AcademicStageOrder.Stages)
        {
            var record = snapshot.Stage(stage);
            if (record.State != AcademicStageState.Completed)
                return stage;
        }

        return null;
    }

    private static AcademicWorkflowRunStatus MapStatus(AcademicProjectState state) => state switch
    {
        AcademicProjectState.Completed => AcademicWorkflowRunStatus.Completed,
        AcademicProjectState.AwaitingApproval => AcademicWorkflowRunStatus.AwaitingApproval,
        AcademicProjectState.PausedBudget => AcademicWorkflowRunStatus.PausedBudget,
        AcademicProjectState.Failed => AcademicWorkflowRunStatus.ProjectFailed,
        AcademicProjectState.Cancelled => AcademicWorkflowRunStatus.Cancelled,
        _ => AcademicWorkflowRunStatus.NotFound,
    };

    /// <summary>
    /// Deterministic project identity from workspace + request key. Replays of intake
    /// resolve to the same row, which is what makes creation idempotent without a
    /// separate lookup-then-insert race.
    /// </summary>
    internal static Guid DeriveProjectId(Guid workspaceId, string requestKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{workspaceId:N}|{requestKey}"));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private static string BuildCheckpointKey(Guid projectId, long revision, string reason) =>
        $"{projectId:N}|{revision}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason))).ToLowerInvariant()}";

    private async Task<AcademicCommitStatus> CommitAsync(
        AcademicWorkflowSnapshot working,
        long expectedRevision,
        string reason,
        CancellationToken cancellationToken)
    {
        working.Revision = expectedRevision + 1;
        working.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        var outcome = await store.TryCommitAsync(working, expectedRevision, BuildCheckpointKey(working.ProjectId, working.Revision, reason), cancellationToken);
        return outcome.Status;
    }
}

// --- Stage execution ------------------------------------------------------------

public sealed partial class AcademicWorkflowOrchestrator
{
    private async Task<(bool TerminatesSlice, AcademicWorkflowRunResult Result)> ExecuteStageAsync(
        AcademicWorkflowSnapshot loaded,
        AcademicStage stage,
        CancellationToken cancellationToken)
    {
        if (!handlers.TryGetValue(stage, out var handler))
            return (true, await FailProjectAsync(loaded, "NO_STAGE_HANDLER", $"No stage handler is registered for {stage}.", cancellationToken));

        var working = loaded.Clone();
        var record = working.Stage(stage);
        var fingerprint = AcademicWorkflowSerialization.ComputeStageInputFingerprint(working, stage);
        var stageKey = AcademicWorkflowSerialization.BuildStageIdempotencyKey(working.ProjectId, stage, fingerprint);
        var attempt = record.AttemptCount + 1;
        var attemptKey = $"{stageKey}|a{attempt}";
        var context = new AcademicStageContext
        {
            Snapshot = working,
            Stage = stage,
            IdempotencyKey = stageKey,
            Attempt = attempt,
            ApprovalFeedback = record.ApprovalFeedback,
        };

        var estimate = handler.Estimate(context);
        if (!AcademicBudgetLedger.TryPreflight(working, stage, estimate, out var budgetCode, out var budgetDetail))
            return (true, await PauseForBudgetAsync(working, stage, budgetCode, budgetDetail, cancellationToken));

        AcademicBudgetLedger.Reserve(working, attemptKey, stage, attempt, estimate);
        record.State = AcademicStageState.Running;
        record.AttemptCount = attempt;
        record.IdempotencyKey = stageKey;
        record.InputFingerprint = fingerprint;
        record.LastErrorCode = null;
        record.LastErrorDetail = null;
        record.StartedAtUtc ??= clock.GetUtcNow().UtcDateTime;

        // Commit the reservation before the provider call so an attempt that dies
        // mid-flight leaves an auditable, already-charged reservation on resume.
        var startCommit = await CommitAsync(working, loaded.Revision, $"stage-start|{attemptKey}", cancellationToken);
        if (startCommit == AcademicCommitStatus.RevisionConflict)
            return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.RevisionConflict, loaded, stage, "CHECKPOINT_CONFLICT", "Another writer advanced the checkpoint first; resume again."));

        AcademicStageOutcome outcome;
        var meterUsage = true;
        try
        {
            outcome = await handler.ExecuteAsync(context with { Snapshot = working.Clone() }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Durable state already records the running attempt; leave it resumable.
            return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.StageFailure, working, stage, "STAGE_CANCELLED", "The stage worker was cancelled; resume re-runs it."));
        }
        catch (Exception exception)
        {
            meterUsage = false;
            outcome = AcademicStageOutcome.Failure("STAGE_EXCEPTION", exception.Message, retryable: true);
        }

        if (meterUsage && !AcademicBudgetLedger.Meter(working, attemptKey, outcome.Usage, out budgetCode, out budgetDetail))
            return (true, await PauseForBudgetAsync(working, stage, budgetCode, budgetDetail, cancellationToken));

        if (!outcome.Succeeded)
            return (true, await RecordStageFailureAsync(working, stage, attemptKey, outcome.ErrorCode ?? "STAGE_FAILED", outcome.ErrorDetail ?? "The stage failed.", outcome.Retryable, cancellationToken));

        var contract = ValidateOutputContract(stage, outcome.Output);
        if (contract is not null)
            return (true, await RecordStageFailureAsync(working, stage, attemptKey, contract.Value.Code, contract.Value.Detail, retryable: true, cancellationToken));

        var candidate = working.Clone();
        if (!TryMergeOutput(candidate, stage, outcome.Output!, clock.GetUtcNow().UtcDateTime, out var mergeCode, out var mergeDetail))
            return (true, await RecordStageFailureAsync(working, stage, attemptKey, mergeCode, mergeDetail, retryable: true, cancellationToken));

        var blocking = AcademicIntegrityGuard.Validate(candidate)
            .Where(issue => issue.Severity == AcademicIssueSeverity.Blocking)
            .ToList();

        if (blocking.Count > 0)
        {
            var first = blocking[0];
            var detail = blocking.Count == 1
                ? $"{first.Code}: {first.Detail}"
                : $"{first.Code}: {first.Detail} (+{blocking.Count - 1} further integrity findings)";
            return (true, await RecordStageFailureAsync(working, stage, attemptKey, "ACADEMIC_INTEGRITY_VIOLATION", detail, retryable: true, cancellationToken));
        }

        // Commit the stage completion and any approval gate in one checkpoint.
        record = candidate.Stage(stage);
        record.State = AcademicStageState.Completed;
        record.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        record.ApprovalFeedback = null;
        record.LastErrorCode = null;
        record.LastErrorDetail = null;

        var gateKind = AcademicStageOrder.ApprovalAfter(stage);
        var gateRequired = gateKind is { } kind && loaded.Request.RequiredApprovals.Contains(kind);
        if (gateRequired)
        {
            var checkpointRevision = candidate.Revision + 1;
            candidate.PendingApproval = new AcademicPendingApproval
            {
                Kind = gateKind!.Value,
                Stage = stage,
                CheckpointRevision = checkpointRevision,
                ApprovalKey = AcademicWorkflowSerialization.BuildApprovalKey(candidate.ProjectId, gateKind.Value, checkpointRevision),
                RequestedAtUtc = clock.GetUtcNow().UtcDateTime,
                ReviewerPrompts = ReviewerPromptsFor(gateKind.Value),
            };
            record.OutputCheckpointRevision = checkpointRevision;
            candidate.State = AcademicProjectState.AwaitingApproval;
            var gateCommit = await CommitAsync(candidate, working.Revision, $"stage-complete|{attemptKey}", cancellationToken);
            if (gateCommit == AcademicCommitStatus.RevisionConflict)
                return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.RevisionConflict, working, stage, "CHECKPOINT_CONFLICT", null));
            return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.AwaitingApproval, candidate, stage, "APPROVAL_REQUIRED", ReviewerPromptsFor(gateKind.Value).FirstOrDefault()));
        }

        if (stage == AcademicStage.Verify)
        {
            candidate.State = AcademicProjectState.Completed;
            record.OutputCheckpointRevision = candidate.Revision + 1;
            var finalCommit = await CommitAsync(candidate, working.Revision, $"stage-complete|{attemptKey}", cancellationToken);
            if (finalCommit == AcademicCommitStatus.RevisionConflict)
                return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.RevisionConflict, working, stage, "CHECKPOINT_CONFLICT", null));
            return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.Completed, candidate, stage));
        }

        record.OutputCheckpointRevision = candidate.Revision + 1;
        var commit = await CommitAsync(candidate, working.Revision, $"stage-complete|{attemptKey}", cancellationToken);
        if (commit == AcademicCommitStatus.RevisionConflict)
            return (true, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.RevisionConflict, working, stage, "CHECKPOINT_CONFLICT", null));

        return (false, AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.Completed, candidate, stage));
    }

    private async Task<AcademicWorkflowRunResult> RecordStageFailureAsync(
        AcademicWorkflowSnapshot working,
        AcademicStage stage,
        string attemptKey,
        string code,
        string detail,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var record = working.Stage(stage);
        record.State = AcademicStageState.Failed;
        record.LastErrorCode = code;
        record.LastErrorDetail = detail;

        if (retryable && record.AttemptCount < working.Request.MaxStageAttempts)
        {
            await CommitAsync(working, working.Revision, $"stage-failure|{attemptKey}", cancellationToken);
            return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.StageFailure, working, stage, code, detail);
        }

        return await FailProjectAsync(working, code, detail, cancellationToken);
    }

    private async Task<AcademicWorkflowRunResult> PauseForBudgetAsync(
        AcademicWorkflowSnapshot working,
        AcademicStage stage,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        working.State = AcademicProjectState.PausedBudget;
        working.StateCode = code;
        working.StateDetail = detail;
        await CommitAsync(working, working.Revision, $"budget-pause|{code}|r{working.Revision + 1}", cancellationToken);
        return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.PausedBudget, working, stage, code, detail);
    }

    private async Task<AcademicWorkflowRunResult> FailProjectAsync(
        AcademicWorkflowSnapshot snapshot,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        var failed = snapshot.Clone();
        failed.State = AcademicProjectState.Failed;
        failed.StateCode = code;
        failed.StateDetail = detail;
        failed.PendingApproval = null;
        await CommitAsync(failed, snapshot.Revision, $"project-failed|{code}|r{failed.Revision}", cancellationToken);
        return AcademicWorkflowRunResult.From(AcademicWorkflowRunStatus.ProjectFailed, failed, code: code, detail: detail);
    }

    /// <summary>
    /// Each stage owns exactly one output field. Wrong-field or empty output is a
    /// contract violation: it can never merge into durable state.
    /// </summary>
    internal static (string Code, string Detail)? ValidateOutputContract(AcademicStage stage, AcademicStageOutput? output)
    {
        if (output is null || output.IsEmpty)
            return ("STAGE_OUTPUT_MISSING", $"Stage {stage} succeeded without producing its output artifact.");

        var populatedFields = (output.Plan is not null ? 1 : 0) + (output.Sources is not null ? 1 : 0) + (output.Screening is not null ? 1 : 0)
            + (output.Evidence is not null ? 1 : 0) + (output.Themes is not null ? 1 : 0) + (output.Chapters is not null ? 1 : 0)
            + (output.Verification is not null ? 1 : 0);
        bool OwnFieldSet = stage switch
        {
            AcademicStage.Plan => output.Plan is not null,
            AcademicStage.Discover => output.Sources is not null,
            AcademicStage.Screen => output.Screening is not null,
            AcademicStage.Extract => output.Evidence is not null,
            AcademicStage.Synthesize => output.Themes is not null,
            AcademicStage.DraftChapter => output.Chapters is not null,
            AcademicStage.Verify => output.Verification is not null,
            _ => false,
        };
        if (populatedFields != 1)
            return ("STAGE_OUTPUT_MISUSE", $"Stage {stage} returned artifacts owned by other stages.");
        if (!OwnFieldSet)
            return ("STAGE_OUTPUT_MISUSE", $"Stage {stage} returned an artifact owned by another stage.");

        return null;
    }

    internal static bool TryMergeOutput(
        AcademicWorkflowSnapshot snapshot,
        AcademicStage stage,
        AcademicStageOutput output,
        DateTime now,
        out string code,
        out string detail)
    {
        code = string.Empty;
        detail = string.Empty;
        switch (stage)
        {
            case AcademicStage.Plan:
                snapshot.Plan = output.Plan! with
                {
                    PlanVersion = (snapshot.Plan?.PlanVersion ?? 0) + 1,
                    ResearchQuestion = string.IsNullOrWhiteSpace(output.Plan!.ResearchQuestion)
                        ? snapshot.Request.ResearchQuestion
                        : output.Plan.ResearchQuestion,
                };
                return true;
            case AcademicStage.Discover:
                snapshot.Sources = output.Sources!.ToList();
                return true;
            case AcademicStage.Screen:
                snapshot.Screening = output.Screening!.ToList();
                return true;
            case AcademicStage.Extract:
                snapshot.Evidence.AddRange(output.Evidence!);
                return true;
            case AcademicStage.Synthesize:
                snapshot.Themes = output.Themes!.ToList();
                return true;
            case AcademicStage.DraftChapter:
                snapshot.Chapters = output.Chapters!.ToList();
                return true;
            case AcademicStage.Verify:
                var report = output.Verification!;
                var guardIssues = AcademicIntegrityGuard.Validate(snapshot);
                var blocking = guardIssues.Where(issue => issue.Severity == AcademicIssueSeverity.Blocking).ToList();
                var warnings = guardIssues.Where(issue => issue.Severity == AcademicIssueSeverity.Warning).ToList();
                var effective = report with
                {
                    Passed = report.Passed && blocking.Count == 0,
                    Issues = report.Issues.Concat(blocking).Concat(warnings).ToList(),
                };
                snapshot.Verification = effective;
                if (!effective.Passed)
                {
                    var first = blocking.Count > 0 ? blocking[0] : effective.Issues.FirstOrDefault();
                    code = blocking.Count > 0 ? "ACADEMIC_INTEGRITY_VIOLATION" : "ACADEMIC_VERIFICATION_FAILED";
                    detail = first?.Detail ?? "The verification report failed.";
                    return false;
                }

                return true;
            default:
                code = "STAGE_UNKNOWN";
                detail = $"Unknown stage {stage}.";
                return false;
        }
    }

    internal static IReadOnlyList<string> ReviewerPromptsFor(AcademicApprovalKind kind) => kind switch
    {
        AcademicApprovalKind.Plan =>
        [
            "Confirm the research question, objectives, and inclusion/exclusion criteria before discovery runs.",
            "Adjust scope or criteria now; revisions re-plan and re-discover under the same budget ceilings.",
        ],
        AcademicApprovalKind.SourceLibrary =>
        [
            "Review the screened source library: scholarly books and articles only, with access levels shown.",
            "Only sources with at least abstract access can carry quotations; metadata-only sources stay bibliographic.",
        ],
        AcademicApprovalKind.Synthesis =>
        [
            "Review the evidence matrix, contradictions, and stated research gaps before chapter drafting.",
        ],
        AcademicApprovalKind.ChapterDraft =>
        [
            "Review chapter drafts: every quotation and locator here was validated against licensed source access.",
        ],
        AcademicApprovalKind.FinalVerification =>
        [
            "Review the final verification report (citation resolution, access policy, locator policy) before completion.",
        ],
        _ => [],
    };

    /// <summary>
    /// Records a human decision for the pending checkpoint. Decisions are idempotent by
    /// approval key: replaying the same decision is a reported no-op, and a decision is
    /// only ever valid for the exact checkpoint that requested it.
    /// </summary>
    public async Task<AcademicApprovalSubmissionResult> SubmitApprovalDecisionAsync(
        Guid projectId,
        AcademicApprovalKind kind,
        string approvalKey,
        long checkpointRevision,
        AcademicApprovalDecision decision,
        string? notes,
        Guid decidedByUserId,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown academic approval decision.");

        var loaded = await store.LoadAsync(projectId, cancellationToken);
        if (loaded is null)
            return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.NotFound };

        var working = loaded.Clone();
        var pending = working.PendingApproval;
        if (pending is null)
        {
            // A replay is recognized only for the exact committed checkpoint and decision.
            // Kind-only matching can mistake an old decision for a new same-kind checkpoint.
            var recorded = working.Approvals.FirstOrDefault(record =>
                record.CheckpointRevision == checkpointRevision
                && string.Equals(record.ApprovalKey, approvalKey, StringComparison.Ordinal));
            if (recorded is not null)
            {
                if (recorded.Kind != kind)
                    return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.NoPendingApproval, Snapshot = working };

                return new AcademicApprovalSubmissionResult
                {
                    Status = recorded.Decision == decision
                        ? AcademicApprovalSubmissionStatus.AlreadyApplied
                        : AcademicApprovalSubmissionStatus.DecisionConflict,
                    Snapshot = working,
                };
            }

            if (working.State is AcademicProjectState.Completed or AcademicProjectState.Cancelled or AcademicProjectState.Failed)
                return new AcademicApprovalSubmissionResult
                {
                    Status = AcademicApprovalSubmissionStatus.ProjectNotRunning,
                    Snapshot = working,
                    Code = working.StateCode,
                };

            return new AcademicApprovalSubmissionResult
            {
                Status = AcademicApprovalSubmissionStatus.CheckpointMismatch,
                Snapshot = working,
            };
        }

        if (pending.Kind != kind)
            return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.NoPendingApproval, Snapshot = working };

        if (!string.Equals(pending.ApprovalKey, approvalKey, StringComparison.Ordinal)
            || pending.CheckpointRevision != checkpointRevision)
            return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.CheckpointMismatch, Snapshot = working };

        working.Approvals.Add(new AcademicApprovalRecord
        {
            ApprovalKey = pending.ApprovalKey,
            Kind = pending.Kind,
            CheckpointRevision = pending.CheckpointRevision,
            Decision = decision,
            Notes = notes,
            DecidedByUserId = decidedByUserId,
            DecidedAtUtc = clock.GetUtcNow().UtcDateTime,
        });
        working.PendingApproval = null;

        switch (decision)
        {
            case AcademicApprovalDecision.Approved:
                working.State = AcademicProjectState.Running;
                break;
            case AcademicApprovalDecision.RequestRevision:
                var targetStage = AcademicStageOrder.StageFor(kind);
                InvalidateFromStage(working, targetStage, notes);
                working.State = AcademicProjectState.Running;
                break;
            case AcademicApprovalDecision.Rejected:
                working.State = AcademicProjectState.Cancelled;
                working.StateCode = "APPROVAL_REJECTED";
                working.StateDetail = $"A reviewer rejected the {kind} checkpoint.";
                break;
        }

        var commit = await CommitAsync(working, loaded.Revision, $"approval|{pending.ApprovalKey}", cancellationToken);
        if (commit == AcademicCommitStatus.Committed)
            return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.Applied, Snapshot = working };

        // A CAS loser or duplicate checkpoint is not proof that this request committed.
        // Reconcile against the durable head and report a replay only if its exact decision
        // is present there; otherwise return the committed snapshot as a conflict.
        var committed = await store.LoadAsync(projectId, cancellationToken);
        if (committed is null)
            return new AcademicApprovalSubmissionResult { Status = AcademicApprovalSubmissionStatus.NotFound };

        var committedDecision = committed.Approvals.FirstOrDefault(record =>
            record.CheckpointRevision == checkpointRevision
            && string.Equals(record.ApprovalKey, approvalKey, StringComparison.Ordinal));
        if (committedDecision is not null)
            return new AcademicApprovalSubmissionResult
            {
                Status = committedDecision.Kind == kind && committedDecision.Decision == decision
                    ? AcademicApprovalSubmissionStatus.AlreadyApplied
                    : AcademicApprovalSubmissionStatus.DecisionConflict,
                Snapshot = committed,
            };

        return commit switch
        {
            AcademicCommitStatus.RevisionConflict or AcademicCommitStatus.DuplicateCheckpoint => new AcademicApprovalSubmissionResult
            {
                Status = AcademicApprovalSubmissionStatus.RevisionConflict,
                Snapshot = committed,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(commit), commit, "Unknown academic commit outcome."),
        };
    }

    /// <summary>
    /// Rolls the workflow cursor back to <paramref name="stage"/>: clears that stage's
    /// artifact and every downstream artifact, resets their idempotency lineage, and
    /// carries the reviewer notes into the re-run's fingerprint so the replay produces a
    /// genuinely new attempt rather than a cached skip.
    /// </summary>
    internal static void InvalidateFromStage(AcademicWorkflowSnapshot snapshot, AcademicStage stage, string? feedback)
    {
        foreach (var target in AcademicStageOrder.From(stage))
        {
            var record = snapshot.Stage(target);
            record.State = target == stage ? AcademicStageState.NotStarted : AcademicStageState.Invalidated;
            record.IdempotencyKey = null;
            record.InputFingerprint = null;
            record.OutputCheckpointRevision = null;
            record.AttemptCount = 0;
            record.AttemptKeys.Clear();
            record.LastErrorCode = null;
            record.LastErrorDetail = null;
            record.StartedAtUtc = null;
            record.CompletedAtUtc = null;
            record.ApprovalFeedback = target == stage ? feedback : null;

            if (target == AcademicStage.Plan) snapshot.Plan = null;
            if (target == AcademicStage.Discover) snapshot.Sources.Clear();
            if (target == AcademicStage.Screen) snapshot.Screening.Clear();
            if (target == AcademicStage.Extract) snapshot.Evidence.Clear();
            if (target == AcademicStage.Synthesize) snapshot.Themes.Clear();
            if (target == AcademicStage.DraftChapter) snapshot.Chapters.Clear();
            if (target == AcademicStage.Verify) snapshot.Verification = null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taslim.Api.AcademicWorkflow;

/// <summary>
/// The complete durable state of one academic project. The engine mutates only a clone of
/// a loaded snapshot and commits it through the state store with optimistic concurrency:
/// every checkpoint bumps <see cref="Revision"/> by exactly one, so a resumable workflow
/// can always restart from the last committed checkpoint without redoing finished work.
/// Persistence mapping (JSONB column, EF entity, row-lock strategy) is intentionally left
/// to the integration lane; the shape below is the storage contract.
/// </summary>
public sealed class AcademicWorkflowSnapshot
{
    public required Guid ProjectId { get; init; }
    public required Guid WorkspaceId { get; init; }

    /// <summary>Client-stable key that makes project creation idempotent.</summary>
    public required string RequestKey { get; init; }

    public required AcademicWorkflowRequest Request { get; init; }
    public AcademicProjectState State { get; set; } = AcademicProjectState.Running;

    /// <summary>
    /// Durable owner of the currently executing stage attempt. Older snapshots omit this
    /// property; a legacy stage still marked Running without an owner is held for explicit
    /// recovery rather than being assumed safe to execute again.
    /// </summary>
    public AcademicLiveAttempt? ActiveAttempt { get; set; }

    /// <summary>
    /// Internal audit of the most recent privileged abandoned-attempt recovery. This stays
    /// in the workspace-scoped snapshot and is deliberately not part of the member DTO.
    /// </summary>
    public AcademicAttemptRecoveryAudit? LastAttemptRecovery { get; set; }

    /// <summary>Committed checkpoint revision; starts at 0 and only ever increases.</summary>
    public long Revision { get; set; }

    public AcademicResearchPlan? Plan { get; set; }
    public List<AcademicSourceRecord> Sources { get; set; } = [];
    public List<AcademicScreeningDecisionRecord> Screening { get; set; } = [];
    public List<AcademicEvidenceItem> Evidence { get; set; } = [];
    public List<AcademicTheme> Themes { get; set; } = [];
    public List<AcademicChapterDraft> Chapters { get; set; } = [];
    public AcademicVerificationReport? Verification { get; set; }

    /// <summary>Exactly one record per stage, ordered by <see cref="AcademicStageOrder"/>.</summary>
    public List<AcademicStageRecord> Stages { get; set; } =
        AcademicStageOrder.Stages.Select(stage => new AcademicStageRecord { Stage = stage }).ToList();

    public List<AcademicBudgetEntry> Budget { get; set; } = [];
    public AcademicPendingApproval? PendingApproval { get; set; }
    public List<AcademicApprovalRecord> Approvals { get; set; } = [];

    /// <summary>Machine-readable code accompanying non-running states.</summary>
    public string? StateCode { get; set; }
    public string? StateDetail { get; set; }

    public required DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; set; }

    public AcademicStageRecord Stage(AcademicStage stage)
    {
        var record = Stages.FirstOrDefault(item => item.Stage == stage);
        return record ?? throw new InvalidOperationException($"Stage record missing for {stage}.");
    }

    public IReadOnlySet<string> IncludedSourceIds =>
        Screening.Where(item => item.Decision == AcademicScreeningDecision.Included)
            .Select(item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);

    public AcademicSourceRecord? FindSource(string sourceId) =>
        Sources.FirstOrDefault(item => string.Equals(item.SourceId, sourceId, StringComparison.Ordinal));

    /// <summary>Deep copy through the canonical serializer; used before every mutation.</summary>
    public AcademicWorkflowSnapshot Clone() =>
        AcademicWorkflowSerialization.Deserialize(AcademicWorkflowSerialization.Serialize(this));
}

/// <summary>
/// The worker identity and fencing token committed before a stage handler is entered.
/// Only the worker holding <see cref="AttemptId"/> may commit that attempt's outcome.
/// </summary>
public sealed record AcademicLiveAttempt
{
    public required Guid AttemptId { get; init; }
    public required Guid OwnerId { get; init; }
    public required AcademicStage Stage { get; init; }
    public required int Attempt { get; init; }
    public required DateTime StartedAtUtc { get; init; }
}

public sealed record AcademicAttemptRecoveryAudit
{
    public Guid? AttemptId { get; init; }
    public required Guid VerifiedByUserId { get; init; }
    public required DateTime VerifiedAtUtc { get; init; }
    public required string Reason { get; init; }
}

public sealed record AcademicStageRecord
{
    public required AcademicStage Stage { get; init; }
    public AcademicStageState State { get; set; } = AcademicStageState.NotStarted;

    /// <summary>projectId|stage|inputFingerprint. Completed keys are never re-executed.</summary>
    public string? IdempotencyKey { get; set; }

    public string? InputFingerprint { get; set; }
    public int AttemptCount { get; set; }

    /// <summary>Attempt keys already metered into the budget ledger, in order.</summary>
    public List<string> AttemptKeys { get; set; } = [];

    public string? LastErrorCode { get; set; }
    public string? LastErrorDetail { get; set; }

    /// <summary>Reviewer feedback carried into the next attempt after RequestRevision.</summary>
    public string? ApprovalFeedback { get; set; }

    /// <summary>Revision of the checkpoint whose commit merged this stage's output.</summary>
    public long? OutputCheckpointRevision { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

/// <summary>
/// Approval emitted after a producing stage checkpoint. A decision is only valid for the
/// exact checkpoint it was requested against, so a stale approval cannot release a newer
/// plan, source library, or draft.
/// </summary>
public sealed record AcademicPendingApproval
{
    public required AcademicApprovalKind Kind { get; init; }
    public required AcademicStage Stage { get; init; }
    public required long CheckpointRevision { get; init; }
    public required string ApprovalKey { get; init; }
    public required DateTime RequestedAtUtc { get; init; }
    public IReadOnlyList<string> ReviewerPrompts { get; init; } = [];
}

public sealed record AcademicApprovalRecord
{
    public required string ApprovalKey { get; init; }
    public required AcademicApprovalKind Kind { get; init; }
    public required long CheckpointRevision { get; init; }
    public required AcademicApprovalDecision Decision { get; init; }
    public string? Notes { get; init; }
    public required Guid DecidedByUserId { get; init; }
    public required DateTime DecidedAtUtc { get; init; }
}

/// <summary>
/// One budgeted stage attempt. The reservation records what the engine promised to spend
/// before the call; <see cref="Committed"/> replaces it with measured usage once the
/// attempt returns. Charged usage is the committed amount when present and the reserved
/// amount otherwise, so an attempt that died mid-flight is still paid for exactly once
/// and can never be silently re-billed on resume.
/// </summary>
public sealed record AcademicBudgetEntry
{
    public required string AttemptKey { get; init; }
    public required AcademicStage Stage { get; init; }
    public required int Attempt { get; init; }
    public required decimal ReservedUsd { get; init; }
    public required int ReservedProviderCalls { get; init; }
    public AcademicUsageRecord? Committed { get; set; }

    public decimal ChargedUsd => Committed?.CostUsd ?? ReservedUsd;
    public int ChargedProviderCalls => Committed?.ProviderCalls ?? ReservedProviderCalls;
}

/// <summary>
/// Pure budget arithmetic over a snapshot: preflight ceilings, reservation metering,
/// and committed usage, all keyed by attempt idempotency keys.
/// </summary>
public static class AcademicBudgetLedger
{
    public static decimal ChargedUsd(AcademicWorkflowSnapshot snapshot) =>
        snapshot.Budget.Sum(entry => entry.ChargedUsd);

    public static int ChargedProviderCalls(AcademicWorkflowSnapshot snapshot) =>
        snapshot.Budget.Sum(entry => entry.ChargedProviderCalls);

    public static decimal RemainingUsd(AcademicWorkflowSnapshot snapshot) =>
        Math.Max(0m, snapshot.Request.Budget.MaxTotalUsd - ChargedUsd(snapshot));

    public static int RemainingProviderCalls(AcademicWorkflowSnapshot snapshot) =>
        Math.Max(0, snapshot.Request.Budget.MaxTotalProviderCalls - ChargedProviderCalls(snapshot));

    public static decimal StageChargedUsd(AcademicWorkflowSnapshot snapshot, AcademicStage stage) =>
        snapshot.Budget.Where(entry => entry.Stage == stage).Sum(entry => entry.ChargedUsd);

    public static int StageChargedCalls(AcademicWorkflowSnapshot snapshot, AcademicStage stage) =>
        snapshot.Budget.Where(entry => entry.Stage == stage).Sum(entry => entry.ChargedProviderCalls);

    /// <summary>
    /// Preflight for one attempt: an estimate must fit the per-stage ceiling (counting the
    /// stage's already-charged attempts) and the project total. Returns a refusal code when
    /// the attempt must not start; never mutates the snapshot.
    /// </summary>
    public static bool TryPreflight(
        AcademicWorkflowSnapshot snapshot,
        AcademicStage stage,
        AcademicStageEstimate estimate,
        out string code,
        out string detail)
    {
        estimate.Validate();
        var policy = snapshot.Request.Budget;
        var allowance = estimate.EstimatedCostUsd * policy.StageEstimateSafetyFactor;
        var stageUsed = StageChargedUsd(snapshot, stage);
        var stageCalls = StageChargedCalls(snapshot, stage);
        var projectedStage = stageUsed + allowance;
        var projectedTotal = ChargedUsd(snapshot) + allowance;

        if (allowance > policy.MaxStageUsd)
        {
            code = "BUDGET_STAGE_LIMIT";
            detail = $"Stage {stage} estimate {allowance:0.####} USD exceeds the per-stage ceiling {policy.MaxStageUsd:0.####} USD.";
            return false;
        }

        if (projectedStage > policy.MaxStageUsd)
        {
            code = "BUDGET_STAGE_LIMIT";
            detail = $"Stage {stage} would exceed its per-stage ceiling after this attempt.";
            return false;
        }

        if (projectedTotal > policy.MaxTotalUsd)
        {
            code = "BUDGET_TOTAL_LIMIT";
            detail = "Project would exceed its total budget after this attempt.";
            return false;
        }

        if (estimate.EstimatedProviderCalls + stageCalls > policy.MaxStageProviderCalls)
        {
            code = "BUDGET_STAGE_CALL_LIMIT";
            detail = $"Stage {stage} would exceed its provider-call ceiling.";
            return false;
        }

        if (estimate.EstimatedProviderCalls + ChargedProviderCalls(snapshot) > policy.MaxTotalProviderCalls)
        {
            code = "BUDGET_TOTAL_CALL_LIMIT";
            detail = "Project would exceed its total provider-call ceiling.";
            return false;
        }

        code = string.Empty;
        detail = string.Empty;
        return true;
    }

    /// <summary>Records the pre-run reservation. Idempotent per attempt key.</summary>
    public static bool Reserve(
        AcademicWorkflowSnapshot snapshot,
        string attemptKey,
        AcademicStage stage,
        int attempt,
        AcademicStageEstimate estimate)
    {
        if (snapshot.Budget.Any(entry => string.Equals(entry.AttemptKey, attemptKey, StringComparison.Ordinal)))
            return false;
        estimate.Validate();
        snapshot.Budget.Add(new AcademicBudgetEntry
        {
            AttemptKey = attemptKey,
            Stage = stage,
            Attempt = attempt,
            ReservedUsd = estimate.EstimatedCostUsd,
            ReservedProviderCalls = estimate.EstimatedProviderCalls,
        });
        return true;
    }

    /// <summary>
    /// Replaces the reservation with measured usage. Idempotent: re-metering the same key
    /// after it already carries committed usage is a no-op, and metering into a snapshot
    /// whose budget would exceed the total hard ceiling is refused (fail closed).
    /// </summary>
    public static bool Meter(
        AcademicWorkflowSnapshot snapshot,
        string attemptKey,
        AcademicUsageRecord usage,
        out string code,
        out string detail)
    {
        usage.Validate();
        var entry = snapshot.Budget.FirstOrDefault(item =>
            string.Equals(item.AttemptKey, attemptKey, StringComparison.Ordinal));
        if (entry is null)
        {
            code = "BUDGET_UNRESERVED_USAGE";
            detail = $"Usage reported for unknown attempt key {attemptKey}.";
            return false;
        }

        if (entry.Committed is not null)
        {
            code = string.Empty;
            detail = string.Empty;
            return true; // attempt already metered; duplicates must not double-charge.
        }

        // Replacing a reservation with actual usage: charge the delta against the ceiling
        // only when usage exceeds what was reserved, because the reservation already counted.
        var extra = Math.Max(0m, usage.CostUsd - entry.ReservedUsd);
        var extraCalls = Math.Max(0, usage.ProviderCalls - entry.ReservedProviderCalls);
        if (ChargedUsd(snapshot) + extra > snapshot.Request.Budget.MaxTotalUsd)
        {
            code = "BUDGET_TOTAL_LIMIT";
            detail = "Measured usage would exceed the total budget ceiling.";
            return false;
        }

        if (ChargedProviderCalls(snapshot) + extraCalls > snapshot.Request.Budget.MaxTotalProviderCalls)
        {
            code = "BUDGET_TOTAL_CALL_LIMIT";
            detail = "Measured usage would exceed the total provider-call ceiling.";
            return false;
        }

        entry.Committed = usage;
        code = string.Empty;
        detail = string.Empty;
        return true;
    }
}

public static class AcademicWorkflowSerialization
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(AcademicWorkflowSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    public static AcademicWorkflowSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<AcademicWorkflowSnapshot>(json, Options)
        ?? throw new InvalidOperationException("The academic workflow snapshot deserialized to null.");

    /// <summary>
    /// Deterministic fingerprint of the inputs that materially feed one stage. It excludes
    /// attempt counters, timestamps, and status noise, so a resume after a crash produces
    /// the same stage idempotency key, while a reviewer revision or a new upstream artifact
    /// produces a fresh one.
    /// </summary>
    public static string ComputeStageInputFingerprint(AcademicWorkflowSnapshot snapshot, AcademicStage stage)
    {
        var included = snapshot.IncludedSourceIds;
        object slice = stage switch
        {
            AcademicStage.Plan => new PlanFingerprintSlice(
                snapshot.Request.ResearchQuestion,
                snapshot.Request.Discipline,
                snapshot.Request.Level,
                snapshot.Request.Method,
                snapshot.Request.SelectedMethods,
                snapshot.Request.CitationStyle,
                snapshot.Request.Language,
                snapshot.Request.TargetChapterHeadings,
                snapshot.Request.WorkingTitle,
                snapshot.Request.GeographicScope,
                snapshot.Request.PopulationOrContext,
                snapshot.Request.TimePeriod,
                snapshot.Request.SubQuestions,
                snapshot.Request.Keywords,
                snapshot.Request.MinimumSources,
                snapshot.Request.MinimumPeerReviewed,
                snapshot.Request.AllowedSourceTypes,
                snapshot.Request.RequireFullText,
                snapshot.Request.ProgrammeRequirements,
                snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.Discover => new DiscoverFingerprintSlice(snapshot.Plan, snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.Screen => new ScreenFingerprintSlice(snapshot.Plan, snapshot.Sources, snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.Extract => new ExtractFingerprintSlice(
                snapshot.Plan,
                snapshot.Sources.Where(source => included.Contains(source.SourceId)).ToList(),
                snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.Synthesize => new SynthesizeFingerprintSlice(
                snapshot.Evidence,
                included.OrderBy(item => item, StringComparer.Ordinal).ToList(),
                snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.DraftChapter => new DraftFingerprintSlice(
                snapshot.Plan?.ChapterOutline,
                snapshot.Themes,
                snapshot.Evidence,
                included.OrderBy(item => item, StringComparer.Ordinal).ToList(),
                snapshot.Stage(stage).ApprovalFeedback),
            AcademicStage.Verify => new VerifyFingerprintSlice(
                snapshot.Chapters,
                snapshot.Sources,
                snapshot.Screening,
                snapshot.Evidence,
                snapshot.Stage(stage).ApprovalFeedback),
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        var json = JsonSerializer.Serialize(slice, slice.GetType(), Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string BuildStageIdempotencyKey(Guid projectId, AcademicStage stage, string fingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{projectId:N}|{(int)stage}|{fingerprint}"))).ToLowerInvariant();

    public static string BuildApprovalKey(Guid projectId, AcademicApprovalKind kind, long checkpointRevision) =>
        $"{projectId:N}:{kind}:{checkpointRevision}";

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record PlanFingerprintSlice(
        string Question,
        string Discipline,
        AcademicResearchLevel Level,
        AcademicMethod Method,
        IReadOnlyList<AcademicMethod> SelectedMethods,
        AcademicCitationStyle CitationStyle,
        string Language,
        IReadOnlyList<string> TargetChapterHeadings,
        string? WorkingTitle,
        string? GeographicScope,
        string? PopulationOrContext,
        string? TimePeriod,
        IReadOnlyList<string> SubQuestions,
        IReadOnlyList<string> Keywords,
        int? MinimumSources,
        int? MinimumPeerReviewed,
        IReadOnlyList<string> AllowedSourceTypes,
        bool RequireFullText,
        string? ProgrammeRequirements,
        string? Feedback);

    private sealed record DiscoverFingerprintSlice(AcademicResearchPlan? Plan, string? Feedback);

    private sealed record ScreenFingerprintSlice(
        AcademicResearchPlan? Plan,
        IReadOnlyList<AcademicSourceRecord> Sources,
        string? Feedback);

    private sealed record ExtractFingerprintSlice(
        AcademicResearchPlan? Plan,
        IReadOnlyList<AcademicSourceRecord> IncludedSources,
        string? Feedback);

    private sealed record SynthesizeFingerprintSlice(
        IReadOnlyList<AcademicEvidenceItem> Evidence,
        IReadOnlyList<string> IncludedSourceIds,
        string? Feedback);

    private sealed record DraftFingerprintSlice(
        IReadOnlyList<string>? ChapterOutline,
        IReadOnlyList<AcademicTheme> Themes,
        IReadOnlyList<AcademicEvidenceItem> Evidence,
        IReadOnlyList<string> IncludedSourceIds,
        string? Feedback);

    private sealed record VerifyFingerprintSlice(
        IReadOnlyList<AcademicChapterDraft> Chapters,
        IReadOnlyList<AcademicSourceRecord> Sources,
        IReadOnlyList<AcademicScreeningDecisionRecord> Screening,
        IReadOnlyList<AcademicEvidenceItem> Evidence,
        string? Feedback);
}

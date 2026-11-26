using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.AcademicEvidence.Persistence;
using Taslim.Api.AcademicIntegration.Persistence;
using Taslim.Api.AcademicWorkflow;
using Taslim.Api.Authorization;
using Taslim.Api.Infrastructure;

namespace Taslim.Api.AcademicIntegration;

// ── request/response shapes ────────────────────────────────────────────────────────────
// Shape rule shared with the template lane: nothing on the customer-facing surface names
// a backend service, model or billing rail. Usage is expressed only as the project's own
// ceiling and spent amounts; stage errors pass through the token sanitizer below.

public sealed record AcademicProjectCreateRequest(
    string? RequestKey,
    string? ResearchQuestion,
    string? Discipline,
    string? Level,
    string? Method,
    IReadOnlyList<string>? Methodologies,
    string? CitationStyle,
    string? Language,
    int? MaxDiscoverySources,
    int? MaxEvidencePerSource,
    IReadOnlyList<string>? TargetChapterHeadings,
    IReadOnlyList<string>? RequiredApprovals,
    int? MaxStageAttempts,
    AcademicBudgetRequestDto? Budget,
    string? WorkingTitle,
    string? GeographicScope,
    string? PopulationOrContext,
    string? TimePeriod,
    IReadOnlyList<string>? SubQuestions,
    IReadOnlyList<string>? Keywords,
    int? MinimumSources,
    int? MinimumPeerReviewed,
    IReadOnlyList<string>? AllowedSourceTypes,
    bool? RequireFullText,
    string? ProgrammeRequirements);

public sealed record AcademicBudgetRequestDto(
    decimal? MaxTotalUsd,
    decimal? MaxStageUsd,
    int? MaxTotalCalls,
    int? MaxStageCalls);

public sealed record AcademicApprovalRequest(
    string? Decision,
    string? Notes,
    string? ApprovalKey,
    long? CheckpointRevision);

public sealed record AcademicAttemptRecoveryRequest(
    Guid? AttemptId,
    long? ExpectedRevision,
    bool? OperatorConfirmedWorkerStopped,
    string? VerificationReason);

public sealed record AcademicStageDto(
    string Stage,
    string State,
    int AttemptCount,
    long? OutputCheckpointRevision,
    string? LastErrorCode,
    string? LastErrorDetail,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record AcademicApprovalDto(
    string Kind,
    long CheckpointRevision,
    string Decision,
    string? Notes,
    DateTime DecidedAtUtc);

public sealed record AcademicPendingApprovalDto(
    string Kind,
    string ApprovalKey,
    long CheckpointRevision,
    DateTime RequestedAtUtc,
    IReadOnlyList<string> ReviewerPrompts);

public sealed record AcademicUsageDto(
    decimal SpentUsd,
    decimal CeilingUsd,
    decimal RemainingUsd,
    int CallsUsed,
    int CallCeiling,
    int CallsRemaining);

public sealed record AcademicProjectSummaryDto(
    Guid ProjectId,
    Guid WorkspaceId,
    string RequestKey,
    string ResearchQuestion,
    string Discipline,
    string Level,
    string State,
    string? StateCode,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record AcademicProjectDto(
    AcademicProjectSummaryDto Project,
    IReadOnlyList<AcademicStageDto> Stages,
    Guid? ActiveAttemptId,
    AcademicPendingApprovalDto? PendingApproval,
    IReadOnlyList<AcademicApprovalDto> Approvals,
    AcademicUsageDto Usage,
    IReadOnlyList<string> ConnectedStages,
    IReadOnlyList<string> MissingStages,
    bool EvidenceCurrent,
    int PlanVersion,
    int SourceCount,
    int IncludedCount,
    int ExcludedCount,
    int UndeterminedCount,
    int EvidenceCount,
    int ThemeCount,
    int ChapterCount,
    AcademicVerificationDto? Verification,
    AcademicResearchPlanDto? Plan,
    // Machine-readable outcome of the action that produced this snapshot (APPLIED,
    // ALREADY_APPLIED, BUDGET_RAISED, BUDGET_UNCHANGED, ...). Null on plain reads.
    string? Code);

public sealed record AcademicResearchPlanDto(
    int PlanVersion,
    string ResearchQuestion,
    string? WorkingTitle,
    string Level,
    string Method,
    IReadOnlyList<string> Methodologies,
    string CitationStyle,
    IReadOnlyList<string> Objectives,
    IReadOnlyList<string> InclusionCriteria,
    IReadOnlyList<string> ExclusionCriteria,
    IReadOnlyList<AcademicSearchSpecificationDto> SearchStrategy,
    IReadOnlyList<string> ChapterOutline,
    string? GeographicScope,
    string? PopulationOrContext,
    string? TimePeriod,
    IReadOnlyList<string> SubQuestions,
    IReadOnlyList<string> Keywords,
    int? MinimumSources,
    int? MinimumPeerReviewed,
    IReadOnlyList<string> AllowedSourceTypes,
    bool RequireFullText,
    string? ProgrammeRequirements);

public sealed record AcademicSearchSpecificationDto(
    string Collection,
    string Query,
    IReadOnlyList<string> Filters);

public sealed record AcademicVerificationDto(
    bool? ReportPassed,
    IReadOnlyList<string> ChecksRequired,
    IReadOnlyList<string> ChecksRun,
    IReadOnlyList<AcademicIntegrityIssueDto> Findings,
    string Boundary);

public sealed record AcademicIntegrityIssueDto(
    string Code,
    string Detail,
    string Severity,
    string Stage);

public sealed record AcademicMatrixCellViewDto(
    string QuestionKey,
    Guid SourceId,
    string Stance,
    bool HasVerifiableLocator,
    string AccessLevel,
    string Provenance,
    string Quality,
    string Screening,
    IReadOnlyList<Guid> EvidenceIds);

public sealed record AcademicMatrixFindingViewDto(
    string FindingType,
    string QuestionKey,
    string Code,
    string Severity,
    string Explanation,
    string? SuggestedStep,
    Guid? LeftSourceId,
    Guid? RightSourceId);

public sealed record AcademicMatrixViewDto(
    Guid ProjectId,
    string ProtocolId,
    bool Projected,
    IReadOnlyList<AcademicMatrixCellViewDto> Cells,
    IReadOnlyList<AcademicMatrixFindingViewDto> Findings);

public sealed record AcademicCheckpointDto(
    long Revision,
    string CheckpointKey,
    DateTime CommittedAtUtc);

/// <summary>
/// Authenticated, workspace-scoped surface for academic research projects.
///
/// Every route resolves the owning workspace from stored state and requires membership;
/// unknown projects and projects in foreign workspaces both answer the identical 404
/// envelope so project ids cannot be enumerated across tenants. Mutations run behind
/// CSRF like the rest of the API, and every mutation carries the same existing
/// abuse-boundary policies the comparable lanes use: intake and checkpoint decisions sit
/// behind the generation policy, a resume (which spends stage work when lanes connect)
/// behind the expensive-AI policy, and budget changes plus abandoned-attempt recovery behind
/// account security. Recovery additionally requires the administrator policy and an explicit
/// attestation with a reason; it is never enabled by elapsed time. Recovery is revision/attempt
/// fenced and never calls a stage handler. Reads stay on the global limiter only. None of these
/// routes reaches a scholarly catalog or an export renderer: <c>run</c> can prepare and
/// checkpoint the deterministic Plan stage; until downstream lanes register stage handlers,
/// it reports the exact missing stage instead of pretending, and reads return committed checkpoints.
/// </summary>
[ApiController]
[Authorize]
public sealed class AcademicProjectsController(
    AcademicProjectsService service,
    DbContext db,
    WorkspaceAccessService access) : ControllerBase
{
    [HttpGet("api/workspaces/{workspaceId:guid}/academic-projects")]
    public async Task<IActionResult> List(
        Guid workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(GetUserId(), workspaceId, cancellationToken)) return Forbid();
        var rows = await service.ListAsync(workspaceId, page, pageSize, cancellationToken);
        var total = await service.CountAsync(workspaceId, cancellationToken);
        Response.Headers["X-Page"] = Math.Clamp(page, 1, 10_000).ToString();
        Response.Headers["X-Page-Size"] = Math.Clamp(pageSize, 1, 100).ToString();
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(rows.Select(Summary).ToArray());
    }

    [HttpPost("api/workspaces/{workspaceId:guid}/academic-projects")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> Create(
        Guid workspaceId,
        AcademicProjectCreateRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!await access.IsMemberAsync(GetUserId(), workspaceId, cancellationToken)) return Forbid();

        var (input, parseError) = ParseCreateInput(request, idempotencyKey);
        if (input is null) return ApiResults.Validation(this, parseError!);

        var outcome = await service.CreateAsync(workspaceId, input, cancellationToken);
        return outcome.Status switch
        {
            AcademicProjectServiceStatus.Validation => ApiResults.Error(this, 400, outcome.Code, outcome.Detail),
            AcademicProjectServiceStatus.Conflict => ApiResults.Error(this, 409, outcome.Code, outcome.Detail),
            _ when outcome.Created => Created($"/api/academic-projects/{outcome.Snapshot!.ProjectId}", await BuildDtoAsync(outcome, cancellationToken)),
            _ => Ok(await BuildDtoAsync(outcome, cancellationToken)),
        };
    }

    [HttpGet("api/academic-projects/{projectId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;
        return Ok(await BuildDtoAsync(
            new AcademicProjectServiceResult
            {
                Status = AcademicProjectServiceStatus.Ok,
                Snapshot = snapshot,
            },
            cancellationToken));
    }

    [HttpPost("api/academic-projects/{projectId:guid}/run")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.ExpensiveAi)]
    public async Task<IActionResult> Run(Guid projectId, CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        var outcome = await service.RunAsync(snapshot!.WorkspaceId, projectId, cancellationToken);
        return outcome.Status switch
        {
            AcademicProjectServiceStatus.NotFound => ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."),
            AcademicProjectServiceStatus.NotConnected => StatusCode(
                StatusCodes.Status501NotImplemented,
                new
                {
                    error = new
                    {
                        code = outcome.Code,
                        message = $"The {outcome.MissingStage} stage is not wired in this build yet.",
                        missingStage = outcome.MissingStage?.ToString(),
                        connectedStages = service.ConnectedStages
                            .OrderBy(stage => (int)stage)
                            .Select(stage => stage.ToString())
                            .ToArray(),
                        resumableAtRevision = outcome.Snapshot?.Revision,
                    },
                }),
            AcademicProjectServiceStatus.Conflict => ApiResults.Error(this, 409, outcome.Code, outcome.Detail),
            _ => Ok(await BuildDtoAsync(outcome, cancellationToken)),
        };
    }

    [Authorize(Policy = AdminPolicies.Usage)]
    [HttpPost("api/academic-projects/{projectId:guid}/recover-abandoned")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.AccountSecurity)]
    public async Task<IActionResult> RecoverAbandoned(
        Guid projectId,
        AcademicAttemptRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;
        if (request.ExpectedRevision is null or <= 0)
            return ApiResults.Validation(this, "A positive ExpectedRevision is required for abandoned-attempt recovery.");
        if (request.OperatorConfirmedWorkerStopped is not true
            || string.IsNullOrWhiteSpace(request.VerificationReason)
            || request.VerificationReason.Length > 500)
            return ApiResults.Validation(this,
                "Administrator recovery requires confirmation that the worker has stopped and a reason of at most 500 characters.");

        var outcome = await service.RecoverAbandonedAsync(
            snapshot!.WorkspaceId,
            projectId,
            request.ExpectedRevision.Value,
            request.AttemptId,
            GetUserId(),
            request.VerificationReason.Trim(),
            cancellationToken);
        return outcome.Status switch
        {
            AcademicProjectServiceStatus.NotFound => ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."),
            AcademicProjectServiceStatus.Conflict => ApiResults.Error(this, 409, outcome.Code, outcome.Detail),
            _ => Ok(await BuildDtoAsync(outcome, cancellationToken)),
        };
    }

    [HttpPost("api/academic-projects/{projectId:guid}/approvals/{kind}")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.Generation)]
    public async Task<IActionResult> Approve(
        Guid projectId,
        string kind,
        AcademicApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        if (!Enum.TryParse<AcademicApprovalKind>(kind, ignoreCase: true, out var approvalKind))
            return ApiResults.Validation(this, "Unknown approval checkpoint.");
        if (!Enum.TryParse<AcademicApprovalDecision>(request.Decision, ignoreCase: true, out var decision)
            || !Enum.IsDefined(decision))
            return ApiResults.Validation(this, "Decision must be Approved, RequestRevision, or Rejected.");
        if (string.IsNullOrWhiteSpace(request.ApprovalKey) || request.CheckpointRevision is null or <= 0)
            return ApiResults.Validation(this, "ApprovalKey and a positive CheckpointRevision are required.");

        var outcome = await service.SubmitApprovalAsync(
            snapshot!.WorkspaceId, projectId, approvalKind, request.ApprovalKey, request.CheckpointRevision.Value,
            decision, request.Notes, GetUserId(), cancellationToken);

        return outcome.Status switch
        {
            AcademicProjectServiceStatus.NotFound => ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."),
            AcademicProjectServiceStatus.Conflict => ApiResults.Error(this, 409, outcome.Code, outcome.Detail),
            _ => Ok(await BuildDtoAsync(outcome, cancellationToken)),
        };
    }

    [HttpPost("api/academic-projects/{projectId:guid}/budget")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.AccountSecurity)]
    public async Task<IActionResult> RaiseBudget(
        Guid projectId,
        AcademicBudgetRequestDto request,
        CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        var outcome = await service.RaiseBudgetAsync(
            snapshot!.WorkspaceId,
            projectId,
            new AcademicBudgetRaiseInput
            {
                MaxTotalUsd = request.MaxTotalUsd,
                MaxStageUsd = request.MaxStageUsd,
                MaxTotalCalls = request.MaxTotalCalls,
                MaxStageCalls = request.MaxStageCalls,
            },
            cancellationToken);

        return outcome.Status switch
        {
            AcademicProjectServiceStatus.NotFound => ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."),
            AcademicProjectServiceStatus.Validation => ApiResults.Error(this, 400, outcome.Code, outcome.Detail),
            AcademicProjectServiceStatus.Conflict => ApiResults.Error(this, 409, outcome.Code, outcome.Detail),
            _ => Ok(await BuildDtoAsync(outcome, cancellationToken)),
        };
    }

    [HttpGet("api/academic-projects/{projectId:guid}/evidence-matrix")]
    public async Task<IActionResult> Matrix(Guid projectId, CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        var cells = await db.Set<AcademicMatrixCellEntity>()
            .AsNoTracking()
            .Where(c => c.WorkspaceId == snapshot!.WorkspaceId && c.ProjectId == projectId)
            .OrderBy(c => c.QuestionKey)
            .ThenBy(c => c.SourceId)
            .ToListAsync(cancellationToken);

        var findings = await db.Set<AcademicMatrixFindingEntity>()
            .AsNoTracking()
            .Where(f => f.WorkspaceId == snapshot!.WorkspaceId && f.ProjectId == projectId)
            .OrderBy(f => f.FindingType)
            .ThenBy(f => f.QuestionKey)
            .ThenBy(f => f.Id)
            .ToListAsync(cancellationToken);

        return Ok(new AcademicMatrixViewDto(
            projectId,
            AcademicEvidenceProjector.ProtocolIdFor(projectId),
            Projected: cells.Count > 0 || findings.Count > 0,
            [.. cells.Select(cell => new AcademicMatrixCellViewDto(
                cell.QuestionKey,
                cell.SourceId,
                cell.Stance,
                cell.HasVerifiableLocator,
                cell.AccessLevel,
                cell.Provenance,
                cell.Quality,
                cell.Screening,
                ParseGuids(cell.EvidenceIdsJson)))],
            [.. findings.Select(finding => new AcademicMatrixFindingViewDto(
                finding.FindingType,
                finding.QuestionKey,
                finding.Code,
                finding.Severity,
                AcademicProjectTextPolicy.Sanitize(finding.Explanation),
                finding.SuggestedStep is null ? null : AcademicProjectTextPolicy.Sanitize(finding.SuggestedStep),
                finding.LeftSourceId,
                finding.RightSourceId))]));
    }

    [HttpGet("api/academic-projects/{projectId:guid}/verification")]
    public async Task<IActionResult> Verification(Guid projectId, CancellationToken cancellationToken)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        var findings = AcademicIntegrityGuard.Validate(snapshot!)
            .Select(ToIntegrityDto)
            .ToArray();

        var report = snapshot!.Verification;
        return Ok(new AcademicVerificationDto(
            report?.Passed,
            [.. AcademicIntegrityGuard.RequiredVerificationChecks],
            report is null ? [] : [.. report.ChecksRun],
            findings,
            "Citations may reference only sources inside the approved library, quotations only words actually read at "
            + "the recorded access level, and page pinpoints only locators anchored to a retrieved copy. Findings here "
            + "recompute from the committed checkpoint; nothing is asserted beyond what the lanes stored."));
    }

    [HttpGet("api/academic-projects/{projectId:guid}/checkpoints")]
    public async Task<IActionResult> Checkpoints(
        Guid projectId,
        [FromQuery] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var (snapshot, failure) = await FencedLoadAsync(projectId, cancellationToken);
        if (failure is not null) return failure;

        var rows = await service.CheckpointsAsync(snapshot!.WorkspaceId, projectId, take, cancellationToken);
        return Ok(rows
            .Select(row => new AcademicCheckpointDto(row.Revision, row.CheckpointKey, row.CommittedAtUtc))
            .ToArray());
    }

    // --- plumbing ----------------------------------------------------------------------------

    /// <summary>
    /// Fence keyed on stored scope: the workspace that owns the row decides membership, and a
    /// project id that is unknown or foreign answers the identical 404. Only after the fence
    /// does the snapshot get deserialized for a member.
    /// </summary>
    private async Task<(AcademicWorkflowSnapshot? Snapshot, IActionResult? Failure)> FencedLoadAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var ownerWorkspace = await service.ResolveWorkspaceAsync(projectId, cancellationToken);
        if (ownerWorkspace is null)
            return (null, ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."));
        if (!await access.IsMemberAsync(GetUserId(), ownerWorkspace.Value, cancellationToken))
            return (null, ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."));

        var snapshot = await service.LoadFencedAsync(ownerWorkspace.Value, projectId, cancellationToken);
        if (snapshot is null)
            return (null, ApiResults.Error(this, 404, "PROJECT_NOT_FOUND", "Project not found."));
        return (snapshot, null);
    }

    private async Task<AcademicProjectDto> BuildDtoAsync(AcademicProjectServiceResult outcome, CancellationToken cancellationToken)
    {
        var snapshot = outcome.Snapshot!;
        var included = snapshot.IncludedSourceIds.Count;
        var excluded = snapshot.Screening.Count(s => s.Decision == AcademicScreeningDecision.Excluded);
        var undetermined = snapshot.Screening.Count(s => s.Decision == AcademicScreeningDecision.Uncertain);
        var stale = await service.IsProjectionStaleAsync(snapshot.ProjectId, cancellationToken);

        return new AcademicProjectDto(
            Summary(snapshot),
            [.. snapshot.Stages.Select(stage => new AcademicStageDto(
                stage.Stage.ToString(),
                stage.State.ToString(),
                stage.AttemptCount,
                stage.OutputCheckpointRevision,
                stage.LastErrorCode,
                stage.LastErrorDetail is null ? null : AcademicProjectTextPolicy.Sanitize(stage.LastErrorDetail),
                stage.StartedAtUtc,
                stage.CompletedAtUtc))],
            snapshot.ActiveAttempt?.AttemptId,
            snapshot.PendingApproval is null ? null : new AcademicPendingApprovalDto(
                snapshot.PendingApproval.Kind.ToString(),
                snapshot.PendingApproval.ApprovalKey,
                snapshot.PendingApproval.CheckpointRevision,
                snapshot.PendingApproval.RequestedAtUtc,
                [.. snapshot.PendingApproval.ReviewerPrompts.Select(AcademicProjectTextPolicy.Sanitize)]),
            [.. snapshot.Approvals.Select(approval => new AcademicApprovalDto(
                approval.Kind.ToString(),
                approval.CheckpointRevision,
                approval.Decision.ToString(),
                approval.Notes is null ? null : AcademicProjectTextPolicy.Sanitize(approval.Notes),
                approval.DecidedAtUtc))],
            new AcademicUsageDto(
                AcademicBudgetLedger.ChargedUsd(snapshot),
                snapshot.Request.Budget.MaxTotalUsd,
                AcademicBudgetLedger.RemainingUsd(snapshot),
                AcademicBudgetLedger.ChargedProviderCalls(snapshot),
                snapshot.Request.Budget.MaxTotalProviderCalls,
                AcademicBudgetLedger.RemainingProviderCalls(snapshot)),
            [.. AcademicStageOrder.Stages.Where(service.ConnectedStages.Contains).Select(s => s.ToString())],
            [.. AcademicStageOrder.Stages.Where(s => !service.ConnectedStages.Contains(s)).Select(s => s.ToString())],
            EvidenceCurrent: !stale,
            snapshot.Plan?.PlanVersion ?? 0,
            snapshot.Sources.Count,
            included,
            excluded,
            undetermined,
            snapshot.Evidence.Count,
            snapshot.Themes.Count,
            snapshot.Chapters.Count,
            snapshot.Verification is null ? null : ToVerification(snapshot),
            snapshot.Plan is null ? null : ToPlan(snapshot.Plan),
            outcome.Code is { Length: > 0 } ? outcome.Code : null);
    }

    private static AcademicResearchPlanDto ToPlan(AcademicResearchPlan plan) => new(
        plan.PlanVersion,
        plan.ResearchQuestion,
        plan.WorkingTitle,
        plan.Level.ToString(),
        plan.Method.ToString(),
        [.. plan.Methodologies.DefaultIfEmpty(plan.Method).Select(method => method.ToString())],
        plan.CitationStyle.ToString(),
        plan.Objectives,
        plan.InclusionCriteria,
        plan.ExclusionCriteria,
        [.. plan.SearchStrategy.Select(search => new AcademicSearchSpecificationDto(
            search.Catalog switch
            {
                AcademicCatalog.OpenAlex => "ScholarlyRecords",
                AcademicCatalog.Crossref => "JournalAndIdentifierRecords",
                AcademicCatalog.OpenLibrary or AcademicCatalog.GoogleBooks => "BookRecords",
                AcademicCatalog.PubMed or AcademicCatalog.EuropePMC => "BiomedicalRecords",
                AcademicCatalog.InstitutionalRepository => "InstitutionalRecords",
                _ => "ScholarlyRecords",
            },
            search.Query,
            search.Filters))],
        plan.ChapterOutline,
        plan.GeographicScope,
        plan.PopulationOrContext,
        plan.TimePeriod,
        plan.SubQuestions,
        plan.Keywords,
        plan.MinimumSources,
        plan.MinimumPeerReviewed,
        plan.AllowedSourceTypes,
        plan.RequireFullText,
        plan.ProgrammeRequirements);

    private static AcademicVerificationDto ToVerification(AcademicWorkflowSnapshot snapshot) => new(
        snapshot.Verification!.Passed,
        [.. AcademicIntegrityGuard.RequiredVerificationChecks],
        [.. snapshot.Verification.ChecksRun],
        [.. snapshot.Verification.Issues.Select(ToIntegrityDto)],
        "Stored verification report recomputed against the committed checkpoint. Blocking findings prevent completion.");

    private static AcademicIntegrityIssueDto ToIntegrityDto(AcademicIntegrityIssue issue) => new(
        issue.Code,
        AcademicProjectTextPolicy.Sanitize(issue.Detail),
        issue.Severity.ToString(),
        issue.Stage.ToString());

    private static AcademicProjectSummaryDto Summary(AcademicWorkflowProjectEntity row)
    {
        var snapshot = SafeDeserialize(row.SnapshotJson);
        return new AcademicProjectSummaryDto(
            row.ProjectId,
            row.WorkspaceId,
            row.RequestKey,
            snapshot?.Request.ResearchQuestion ?? string.Empty,
            snapshot?.Request.Discipline ?? string.Empty,
            snapshot?.Request.Level.ToString() ?? "Masters",
            row.State,
            row.StateCode,
            row.Revision,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);
    }

    private static AcademicProjectSummaryDto Summary(AcademicWorkflowSnapshot snapshot) => new(
        snapshot.ProjectId,
        snapshot.WorkspaceId,
        snapshot.RequestKey,
        snapshot.Request.ResearchQuestion,
        snapshot.Request.Discipline,
        snapshot.Request.Level.ToString(),
        snapshot.State.ToString(),
        snapshot.StateCode,
        snapshot.Revision,
        snapshot.CreatedAtUtc,
        snapshot.UpdatedAtUtc);

    private static AcademicWorkflowSnapshot? SafeDeserialize(string json)
    {
        try
        {
            return AcademicWorkflowSerialization.Deserialize(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<Guid> ParseGuids(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var ids = new List<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
                if (Guid.TryParse(text, out var guid)) ids.Add(guid);
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Guid GetUserId() => Guid.Parse(
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));

    private static (AcademicProjectCreateInput? Input, string? Error) ParseCreateInput(
        AcademicProjectCreateRequest request,
        string? idempotencyKey)
    {
        var requestKey = !string.IsNullOrWhiteSpace(idempotencyKey)
            ? idempotencyKey.Trim()
            : (request.RequestKey ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(requestKey) || requestKey.Length > 200)
            return (null, "A request key of 1 to 200 characters (or an Idempotency-Key header) is required.");
        if (string.IsNullOrWhiteSpace(request.ResearchQuestion))
            return (null, "The research question is required.");
        if (string.IsNullOrWhiteSpace(request.Discipline))
            return (null, "The discipline is required.");

        var level = ParseEnum<AcademicResearchLevel>(request.Level, AcademicResearchLevel.Masters);
        var method = ParseEnum<AcademicMethod>(request.Method, AcademicMethod.SystematicReview);
        var methods = new List<AcademicMethod>();
        if (request.Methodologies is { Count: > 0 })
        {
            foreach (var item in request.Methodologies)
            {
                if (!Enum.TryParse<AcademicMethod>(item, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return (null, $"Unknown research methodology: {item}");
                methods.Add(parsed);
            }
            method = methods[0];
        }
        else
        {
            methods.Add(method);
        }
        var style = ParseEnum<AcademicCitationStyle>(request.CitationStyle, AcademicCitationStyle.Apa7);

        List<AcademicApprovalKind>? approvals = null;
        if (request.RequiredApprovals is { Count: > 0 })
        {
            approvals = [];
            foreach (var item in request.RequiredApprovals)
            {
                if (!Enum.TryParse<AcademicApprovalKind>(item, ignoreCase: true, out var parsed))
                    return (null, $"Unknown approval checkpoint: {item}");
                if (!approvals.Contains(parsed)) approvals.Add(parsed);
            }
        }

        var budget = new AcademicBudgetPolicy();
        if (request.Budget is not null)
        {
            budget = budget with
            {
                MaxTotalUsd = request.Budget.MaxTotalUsd ?? budget.MaxTotalUsd,
                MaxStageUsd = request.Budget.MaxStageUsd ?? budget.MaxStageUsd,
                MaxTotalProviderCalls = request.Budget.MaxTotalCalls ?? budget.MaxTotalProviderCalls,
                MaxStageProviderCalls = request.Budget.MaxStageCalls ?? budget.MaxStageProviderCalls,
            };
        }

        return (new AcademicProjectCreateInput
        {
            RequestKey = requestKey,
            ResearchQuestion = request.ResearchQuestion,
            Discipline = request.Discipline,
            Level = level,
            Method = method,
            SelectedMethods = methods,
            CitationStyle = style,
            Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim(),
            MaxDiscoverySources = Math.Clamp(request.MaxDiscoverySources ?? 120, 1, AcademicProjectsService.AbsoluteMaxDiscoverySources),
            MaxEvidencePerSource = Math.Clamp(request.MaxEvidencePerSource ?? 6, 1, AcademicProjectsService.AbsoluteMaxEvidencePerSource),
            TargetChapterHeadings = (request.TargetChapterHeadings ?? [])
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim())
                .ToArray(),
            WorkingTitle = request.WorkingTitle?.Trim(),
            GeographicScope = request.GeographicScope?.Trim(),
            PopulationOrContext = request.PopulationOrContext?.Trim(),
            TimePeriod = request.TimePeriod?.Trim(),
            SubQuestions = (request.SubQuestions ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray(),
            Keywords = (request.Keywords ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray(),
            MinimumSources = request.MinimumSources,
            MinimumPeerReviewed = request.MinimumPeerReviewed,
            AllowedSourceTypes = (request.AllowedSourceTypes ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray(),
            RequireFullText = request.RequireFullText ?? false,
            ProgrammeRequirements = request.ProgrammeRequirements?.Trim(),
            RequiredApprovals = approvals ??
            [
                AcademicApprovalKind.Plan,
                AcademicApprovalKind.SourceLibrary,
                AcademicApprovalKind.Synthesis,
                AcademicApprovalKind.FinalVerification,
            ],
            MaxStageAttempts = Math.Clamp(request.MaxStageAttempts ?? 3, 1, 10),
            Budget = budget,
        }, null);
    }

    private static T ParseEnum<T>(string? value, T fallback) where T : struct =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

/// <summary>
/// Customer-facing text policy for stage output and stored details: backend service nouns
/// that leaked into an error string (a catalog failure detail, a budget sentence) are
/// rewritten to the platform-neutral wording the UI is allowed to show. The machine code
/// beside the text keeps the precision for support.
/// </summary>
public static class AcademicProjectTextPolicy
{
    private static readonly (string From, string To)[] Replacements =
    [
        ("provider-call", "call"),
        ("provider calls", "calls"),
        ("provider call", "call"),
        ("providers", "services"),
        ("provider", "service"),
        ("OpenAlex", "the scholarly catalog"),
        ("Crossref", "the registry"),
        ("OpenLibrary", "the book catalog"),
        ("GoogleBooks", "the book catalog"),
    ];

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value;
        foreach (var (from, to) in Replacements)
        {
            text = ReplaceOrdinal(text, from, to);
        }

        return text.Length > 600 ? text[..600] : text;
    }

    private static string ReplaceOrdinal(string text, string from, string to)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var found = text.IndexOf(from, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, found - index);
            builder.Append(to);
            index = found + from.Length;
        }

        return builder.ToString();
    }
}

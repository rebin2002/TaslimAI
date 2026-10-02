using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;

namespace Taslim.Api.Controllers;

/// <summary>
/// Administrator-only control surface for the Autopilot Controller. It exposes
/// status, audit history, and kill switch / pause / resume semantics. It cannot
/// be used to force a merge, a release, or a paid capability.
/// </summary>
[ApiController]
[Authorize(Policy = AdminPolicies.Usage)]
[Route("api/admin/autopilot")]
public sealed class AutopilotConsoleController(
    IAutopilotOrchestrator orchestrator,
    AutopilotConsoleService console,
    AutopilotBacklogService backlog) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<AutopilotOverviewDto>> Overview(
        [FromQuery] string? waveKey,
        CancellationToken cancellationToken)
    {
        var control = await orchestrator.GetControlAsync(cancellationToken);
        return Ok(await console.GetOverviewAsync(control, waveKey, cancellationToken));
    }

    [HttpGet("events")]
    public async Task<ActionResult<IReadOnlyList<AutopilotEventDto>>> Events(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default) =>
        Ok(await console.ListEventsAsync(limit, cancellationToken));

    [HttpGet("audit")]
    public async Task<ActionResult<IReadOnlyList<AutopilotAuditDto>>> Audit(
        [FromQuery] string? waveKey,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await console.ListAuditAsync(waveKey, limit, cancellationToken));

    [HttpGet("decisions")]
    public ActionResult<object> Decisions() =>
        Ok(new
        {
            humanRequired = console.HumanDecisionCatalogue(),
            neverAutomatic = console.ForbiddenOperations(),
            automaticRepair = new[] { AutopilotFailureClasses.Ordinary, AutopilotFailureClasses.Infrastructure },
        });

    /// <summary>Follow-on wave launches, including blocked batches and their exact reason.</summary>
    [HttpGet("next-waves")]
    public async Task<ActionResult<IReadOnlyList<AutopilotWaveLaunchBatchDto>>> NextWaves(
        [FromQuery] string? sourceWaveKey,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await console.ListLaunchesAsync(sourceWaveKey, limit, cancellationToken));

    /// <summary>Pre-approved backlog. Only approved development items are ever selected.</summary>
    [HttpGet("backlog")]
    public async Task<ActionResult<IReadOnlyList<AutopilotBacklogItemDto>>> Backlog(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await console.ListBacklogAsync(limit, cancellationToken));

    /// <summary>
    /// Creates or updates a backlog item. Approval is an explicit human decision
    /// and requires a reason; the controller itself can only consume approved work.
    /// </summary>
    [HttpPost("backlog")]
    public async Task<ActionResult<AutopilotBacklogItemDto>> UpsertBacklog(
        [FromBody] AutopilotBacklogItemRequest request,
        CancellationToken cancellationToken)
    {
        var actorValue = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? actorUserId = Guid.TryParse(actorValue, out var parsed) ? parsed : null;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = new { code = "AUTOPILOT_REASON_REQUIRED", message = "A reason is required to change the backlog." } });

        var result = await backlog.UpsertAsync(request, actorUserId, cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new { error = new { code = "AUTOPILOT_BACKLOG_REJECTED", message = result.Reason } });

        return Ok(result.Item);
    }

    [HttpPost("control")]
    public async Task<ActionResult<AutopilotControlDto>> Control(
        [FromBody] AutopilotControlRequest request,
        CancellationToken cancellationToken)
    {
        var actorValue = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Guid? actorUserId = Guid.TryParse(actorValue, out var parsed) ? parsed : null;
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = new { code = "AUTOPILOT_REASON_REQUIRED", message = "A reason is required for a control change." } });

        var control = await orchestrator.SetControlAsync(request.Paused, request.KillSwitch, actorUserId, request.Reason, cancellationToken);
        return Ok(console.MapControl(control));
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<AutopilotCycleResult>> Reconcile(
        [FromBody] AutopilotReconcileRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = new { code = "AUTOPILOT_REASON_REQUIRED", message = "A reason is required to run reconciliation." } });

        var result = await orchestrator.ReconcileAsync($"manual:{request.Reason}", cancellationToken);
        return Ok(result);
    }
}

public sealed class AutopilotControlRequest
{
    public bool? Paused { get; set; }
    public bool? KillSwitch { get; set; }
    public string? Reason { get; set; }
}

public sealed class AutopilotReconcileRequest
{
    public string? Reason { get; set; }
}

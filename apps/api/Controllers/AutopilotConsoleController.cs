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
    AutopilotConsoleService console) : ControllerBase
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

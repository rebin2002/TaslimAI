using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Autopilot;
using Taslim.Api.Authorization;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize(Policy = AdminPolicies.Usage)]
[Route("api/admin/autopilot")]
public sealed class AutopilotController(IAutopilotControllerService controller) : ControllerBase
{
    [HttpPost("waves")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Launch([FromBody] LaunchAutopilotWaveRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await controller.LaunchAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { waveId = summary.Id }, summary);
        }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpGet("waves/{waveId:guid}")]
    public async Task<IActionResult> Get(Guid waveId, CancellationToken cancellationToken)
    {
        var summary = await controller.GetSummaryAsync(waveId, cancellationToken);
        return summary is null ? NotFound(new { error = new { code = "AUTOPILOT_WAVE_NOT_FOUND", message = "The wave does not exist." } }) : Ok(summary);
    }

    [HttpPost("waves/{waveId:guid}/events")]
    public async Task<IActionResult> Event(Guid waveId, [FromBody] AutopilotEventRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.HandleEventAsync(waveId, request, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid waveId, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.ReconcileAsync(waveId, false, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/gates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Gate(Guid waveId, [FromBody] AutopilotGateResultRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.RecordGateAsync(waveId, request, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/deployment")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deployment(Guid waveId, [FromBody] AutopilotDeploymentRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.RecordDeploymentAsync(waveId, request, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/smoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Smoke(Guid waveId, [FromBody] AutopilotSmokeRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.RecordSmokeAsync(waveId, request, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/decision")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decision(Guid waveId, [FromBody] AutopilotDecisionRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.ResolveDecisionAsync(waveId, request, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    [HttpPost("waves/{waveId:guid}/pause")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pause(Guid waveId, [FromBody] string reason, CancellationToken cancellationToken)
    {
        try { return Ok(await controller.PauseAsync(waveId, reason, cancellationToken)); }
        catch (AutopilotTransitionException exception) { return Error(exception); }
    }

    private IActionResult Error(AutopilotTransitionException exception)
    {
        var status = exception.Code switch
        {
            "AUTOPILOT_WAVE_NOT_FOUND" => StatusCodes.Status404NotFound,
            "AUTOPILOT_LOCKED" or "AUTOPILOT_GATE_ORDER" or "AUTOPILOT_DEPLOY_ORDER" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, new { error = new { code = exception.Code, message = exception.Message } });
    }
}

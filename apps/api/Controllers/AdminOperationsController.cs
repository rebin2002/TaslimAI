using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Operations;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize(Policy = AdminPolicies.Usage)]
[Route("api/admin/operations")]
public sealed class AdminOperationsController(IAdminOperationsService operations) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<AdminOperationsDashboardDto>> Dashboard(
        [FromQuery] AdminOperationsQuery query,
        CancellationToken cancellationToken) =>
        Ok(await operations.GetDashboardAsync(query.ToFilter(), cancellationToken));

    [HttpPost("jobs/{jobId:guid}/recover")]
    public async Task<ActionResult<AdminJobRecoveryResult>> Recover(
        Guid jobId,
        [FromBody] AdminJobRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var actorValue = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(actorValue, out var actorUserId)) return Unauthorized();
        try
        {
            var result = await operations.RecoverExpiredJobAsync(actorUserId, jobId, request.Reason, HttpContext.TraceIdentifier, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        }
        catch (AdminOperationConflictException exception)
        {
            return Conflict(new { error = new { code = exception.Code, message = exception.Message } });
        }
    }
}

public sealed class AdminOperationsQuery
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    public AdminOperationsFilter ToFilter() => new(FromUtc, ToUtc);
}

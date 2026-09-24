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
}

public sealed class AdminOperationsQuery
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    public AdminOperationsFilter ToFilter() => new(FromUtc, ToUtc);
}

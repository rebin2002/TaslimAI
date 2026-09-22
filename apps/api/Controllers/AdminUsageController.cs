using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Usage;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize(Policy = AdminPolicies.Usage)]
[Route("api/admin/usage")]
public sealed class AdminUsageController(IAdminUsageService usage) : ControllerBase
{
    [HttpGet("report")]
    public async Task<ActionResult<AdminUsageReportDto>> Report([FromQuery] AdminUsageQuery query, CancellationToken cancellationToken)
        => Ok(await usage.GetReportAsync(query.ToFilter(), cancellationToken));

    [HttpGet("summary")]
    public async Task<ActionResult<AdminUsageSummaryDto>> Summary([FromQuery] AdminUsageQuery query, CancellationToken cancellationToken)
        => Ok((await usage.GetReportAsync(query.ToFilter(), cancellationToken)).Summary);

    [HttpGet("breakdowns")]
    public async Task<ActionResult<AdminUsageBreakdownsDto>> Breakdowns([FromQuery] AdminUsageQuery query, CancellationToken cancellationToken)
        => Ok((await usage.GetReportAsync(query.ToFilter(), cancellationToken)).Breakdowns);

    [HttpGet("transactions")]
    public async Task<ActionResult<AdminUsageTransactionListDto>> Transactions([FromQuery] AdminUsageQuery query, CancellationToken cancellationToken)
        => Ok((await usage.GetReportAsync(query.ToFilter(), cancellationToken)).Transactions);

    [HttpGet("transactions/{id:guid}")]
    public async Task<ActionResult<AdminUsageTransactionDto>> Transaction(Guid id, CancellationToken cancellationToken)
    {
        var result = await usage.GetTransactionAsync(id, cancellationToken);
        return result is null ? NotFound(new { error = new { code = "USAGE_TRANSACTION_NOT_FOUND", message = "The usage transaction was not found." } }) : Ok(result);
    }
}

public sealed class AdminUsageQuery
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public UsageFeature? Feature { get; set; }
    public UsageTransactionStatus? Status { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? GenerationJobId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public AdminUsageFilter ToFilter() => new(FromUtc, ToUtc, Feature, Status, WorkspaceId, UserId, GenerationJobId, Page, PageSize);
}

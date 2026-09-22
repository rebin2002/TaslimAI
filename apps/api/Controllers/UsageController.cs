using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Persistence;

namespace Taslim.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces/{workspaceId:guid}/usage")]
public sealed class UsageController(
    TaslimDbContext db,
    WorkspaceAccessService access) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!await access.IsMemberAsync(GetUserId(), workspaceId, cancellationToken)) return Forbid();

        var transactions = await db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.WorkspaceId == workspaceId)
            .Select(transaction => new
            {
                transaction.Status,
                transaction.InputTokens,
                transaction.CachedInputTokens,
                transaction.OutputTokens,
                transaction.ChargedAmount,
            })
            .ToListAsync(cancellationToken);
            var summary = new UsageSummaryDto(
                transactions.Count,
                transactions.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Completed),
                transactions.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Failed),
                transactions.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Cancelled),
                transactions.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Refunded),
                transactions.Sum(transaction => (long?)transaction.InputTokens ?? 0L),
                transactions.Sum(transaction => (long?)transaction.CachedInputTokens ?? 0L),
                transactions.Sum(transaction => (long?)transaction.OutputTokens ?? 0L),
                transactions.Sum(transaction => transaction.ChargedAmount),
            Domain.UsageChargeUnit.Usd.ToString());
        return Ok(summary);
    }

    [HttpGet]
    public async Task<IActionResult> History(Guid workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(GetUserId(), workspaceId, cancellationToken)) return Forbid();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.WorkspaceId == workspaceId)
            .OrderByDescending(transaction => transaction.CreatedAt)
            .ThenByDescending(transaction => transaction.Id);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(transaction => new UsageTransactionDto(
            transaction.Id,
            transaction.Feature.ToString(),
            transaction.Status.ToString(),
            transaction.InputTokens,
            transaction.CachedInputTokens,
            transaction.OutputTokens,
            transaction.ChargedAmount,
            transaction.ChargedUnit.ToString(),
            transaction.CreatedAt,
            transaction.CompletedAt,
            transaction.FailureCode)).ToListAsync(cancellationToken);
        return Ok(new UsageHistoryDto(items, page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

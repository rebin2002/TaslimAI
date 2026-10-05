using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Infrastructure;
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

        var aggregate = await db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.WorkspaceId == workspaceId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                TotalRequests = group.Count(),
                CompletedRequests = group.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Completed),
                FailedRequests = group.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Failed),
                CancelledRequests = group.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Cancelled),
                RefundedRequests = group.Count(transaction => transaction.Status == Domain.UsageTransactionStatus.Refunded),
                InputTokens = group.Sum(transaction => (long?)transaction.InputTokens) ?? 0L,
                CachedInputTokens = group.Sum(transaction => (long?)transaction.CachedInputTokens) ?? 0L,
                OutputTokens = group.Sum(transaction => (long?)transaction.OutputTokens) ?? 0L,
                ChargedAmount = group.Sum(transaction => (decimal?)transaction.ChargedAmount) ?? 0m,
                ReversedAmount = group.Sum(transaction => (decimal?)transaction.ReversedAmount) ?? 0m,
            })
            .FirstOrDefaultAsync(cancellationToken);
        var customerChargedAmount = aggregate is null
            ? 0m
            : decimal.Round(aggregate.ChargedAmount - aggregate.ReversedAmount, 8);
        var summary = new UsageSummaryDto(
                aggregate?.TotalRequests ?? 0,
                aggregate?.CompletedRequests ?? 0,
                aggregate?.FailedRequests ?? 0,
                aggregate?.CancelledRequests ?? 0,
                aggregate?.RefundedRequests ?? 0,
                aggregate?.InputTokens ?? 0L,
                aggregate?.CachedInputTokens ?? 0L,
                aggregate?.OutputTokens ?? 0L,
                customerChargedAmount,
            Domain.UsageChargeUnit.Usd.ToString());
        return Ok(summary);
    }

    [HttpGet]
    public async Task<IActionResult> History(Guid workspaceId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await access.IsMemberAsync(GetUserId(), workspaceId, cancellationToken)) return Forbid();
        page = ApiPagination.NormalizePage(page);
        pageSize = ApiPagination.NormalizePageSize(pageSize);

        var query = db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.WorkspaceId == workspaceId)
            .OrderByDescending(transaction => transaction.CreatedAt)
            .ThenByDescending(transaction => transaction.Id);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.Skip(ApiPagination.GetOffset(page, pageSize)).Take(pageSize).Select(transaction => new UsageTransactionDto(
            transaction.Id,
            transaction.Feature.ToString(),
            transaction.Status.ToString(),
            transaction.InputTokens,
            transaction.CachedInputTokens,
            transaction.OutputTokens,
            transaction.ChargedAmount - transaction.ReversedAmount,
            transaction.ChargedUnit.ToString(),
            transaction.CreatedAt,
            transaction.CompletedAt,
            transaction.FailureCode)).ToListAsync(cancellationToken);
        return Ok(new UsageHistoryDto(items, page, pageSize, totalCount, (int)Math.Ceiling(totalCount / (double)pageSize)));
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new InvalidOperationException("Authenticated user identifier is missing."));
}

using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Taslim.Api.Authorization;
using Taslim.Api.Contracts;
using Taslim.Api.Domain;
using Taslim.Api.Infrastructure;
using Taslim.Api.Persistence;

namespace Taslim.Api.Usage;

/// <summary>
/// Explicit admin control for provider-cost facts that cannot be obtained from the provider API.
/// Customer charges and reversals are never changed by this operation.
/// </summary>
[ApiController]
[Authorize(Policy = AdminPolicies.Usage)]
[Route("api/admin/usage/music-provider-cost-settlements")]
public sealed class MusicProviderCostSettlementController(TaslimDbContext db) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<ActionResult<IReadOnlyList<MusicProviderCostSettlementCandidateDto>>> Pending(CancellationToken cancellationToken)
    {
        var attempts = await db.GenerationProviderAttempts.AsNoTracking()
            .Where(attempt => attempt.Capability == GenerationJobTypes.MusicGenerate
                && !attempt.ActualProviderCostKnown
                && attempt.Status != GenerationProviderAttemptStatus.Started
                && attempt.Status != GenerationProviderAttemptStatus.Rejected
                && (attempt.GenerationJob.Status == GenerationJobStatus.Succeeded
                    || attempt.GenerationJob.Status == GenerationJobStatus.Failed
                    || attempt.GenerationJob.Status == GenerationJobStatus.Cancelled))
            .OrderBy(attempt => attempt.CompletedAt)
            .ThenBy(attempt => attempt.StartedAt)
            .Select(attempt => new
            {
                attempt.Id,
                attempt.GenerationJobId,
                JobStatus = attempt.GenerationJob.Status,
                AttemptStatus = attempt.Status,
                attempt.Provider,
                attempt.ProviderExecutionId,
                attempt.ResultClassification,
                attempt.FailureCode,
                attempt.StartedAt,
                attempt.CompletedAt,
                attempt.EstimatedProviderCostUsd,
                attempt.EstimatedProviderCostKnown,
                attempt.SafeMetadataJson,
            })
            .ToListAsync(cancellationToken);
        if (attempts.Count == 0) return Ok(Array.Empty<MusicProviderCostSettlementCandidateDto>());

        var jobIds = attempts.Select(attempt => attempt.GenerationJobId).Distinct().ToArray();
        var ledgers = await db.UsageTransactions.AsNoTracking()
            .Where(transaction => transaction.GenerationJobId.HasValue
                && jobIds.Contains(transaction.GenerationJobId.Value)
                && transaction.Feature == UsageFeature.Music
                && !transaction.ProviderCostKnown
                && (transaction.Status == UsageTransactionStatus.Completed
                    || transaction.Status == UsageTransactionStatus.Failed
                    || transaction.Status == UsageTransactionStatus.Cancelled))
            .ToDictionaryAsync(transaction => transaction.GenerationJobId!.Value, cancellationToken);

        var candidates = attempts
            .Where(attempt => ledgers.ContainsKey(attempt.GenerationJobId))
            .Select(attempt =>
            {
                var ledger = ledgers[attempt.GenerationJobId];
                return new MusicProviderCostSettlementCandidateDto(
                    attempt.Id,
                    attempt.GenerationJobId,
                    attempt.JobStatus.ToString(),
                    attempt.AttemptStatus.ToString(),
                    attempt.Provider,
                    attempt.ProviderExecutionId,
                    attempt.ResultClassification,
                    attempt.FailureCode,
                    attempt.StartedAt,
                    attempt.CompletedAt,
                    attempt.EstimatedProviderCostUsd,
                    attempt.EstimatedProviderCostKnown,
                    attempt.SafeMetadataJson,
                    ledger.ProviderCostUsd,
                    ledger.ProviderCostKnown,
                    ledger.ChargedAmount,
                    ledger.ReversedAmount);
            })
            .ToArray();
        return Ok(candidates);
    }

    [HttpPost("{attemptId:guid}")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimiting.AccountSecurity)]
    public async Task<ActionResult<SettleMusicProviderCostResponse>> Settle(
        Guid attemptId,
        [FromBody] SettleMusicProviderCostRequest request,
        CancellationToken cancellationToken)
    {
        var actorText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(actorText, out var actorUserId)) return Unauthorized();
        var evidenceReference = request.EvidenceReference?.Trim();
        if (request.ActualProviderCostUsd < 0m || request.ActualProviderCostUsd > 1_000_000m
            || !IsSafeEvidenceReference(evidenceReference))
        {
            return BadRequest(new { error = new { code = "MUSIC_PROVIDER_COST_SETTLEMENT_INVALID", message = "Enter a non-negative USD amount and an opaque evidence reference using only ASCII letters, numbers, and -_.:/# (500 characters max)." } });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var attempt = await db.GenerationProviderAttempts
            .Include(item => item.GenerationJob)
            .SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
        if (attempt is null) return NotFound(new { error = new { code = "MUSIC_PROVIDER_ATTEMPT_NOT_FOUND", message = "The Music provider attempt was not found." } });
        if (attempt.Capability != GenerationJobTypes.MusicGenerate
            || attempt.GenerationJob.JobType != GenerationJobTypes.MusicGenerate
            || attempt.Status == GenerationProviderAttemptStatus.Started
            || attempt.Status == GenerationProviderAttemptStatus.Rejected
            || attempt.GenerationJob.Status is not (GenerationJobStatus.Succeeded or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled))
        {
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_SETTLEMENT_NOT_TERMINAL", message = "Only a terminal Music provider attempt can be manually settled." } });
        }
        if (attempt.ActualProviderCostKnown)
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_ALREADY_SETTLED", message = "The provider cost is already known and cannot be overwritten." } });

        var usage = await db.UsageTransactions.SingleOrDefaultAsync(item =>
            item.GenerationJobId == attempt.GenerationJobId && item.Feature == UsageFeature.Music,
            cancellationToken);
        if (usage is null || usage.Status is not (UsageTransactionStatus.Completed or UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled))
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_LEDGER_NOT_TERMINAL", message = "The matching Music usage ledger is not in a terminal state." } });

        // Serialize distinct attempt settlements that share this one ledger row. The no-op update
        // acquires the database's write lock until commit; refreshing afterwards prevents a second
        // administrator from overwriting the first attempt's amount or audit entry.
        var locked = await db.UsageTransactions.Where(item => item.Id == usage.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ProviderCostUsd, item => item.ProviderCostUsd), cancellationToken);
        if (locked != 1)
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_SETTLEMENT_RACE", message = "The matching usage ledger changed while it was being settled; reload the pending list." } });
        await db.Entry(usage).ReloadAsync(cancellationToken);
        await db.Entry(attempt).ReloadAsync(cancellationToken);

        if (attempt.ActualProviderCostKnown)
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_ALREADY_SETTLED", message = "The provider cost is already known and cannot be overwritten." } });
        if (attempt.Status == GenerationProviderAttemptStatus.Started || attempt.Status == GenerationProviderAttemptStatus.Rejected
            || usage.Status is not (UsageTransactionStatus.Completed or UsageTransactionStatus.Failed or UsageTransactionStatus.Cancelled))
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_SETTLEMENT_NOT_TERMINAL", message = "Only a terminal Music provider attempt and usage ledger can be manually settled." } });
        if (usage.ProviderCostKnown)
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_LEDGER_ALREADY_SETTLED", message = "The matching usage ledger already has a known provider cost." } });

        var settlementAt = DateTime.UtcNow;
        var attemptMetadata = AddSettlementAudit(attempt.SafeMetadataJson, attemptId, request.ActualProviderCostUsd, evidenceReference!, actorUserId, settlementAt);
        var changed = await db.GenerationProviderAttempts
            .Where(item => item.Id == attemptId
                && item.JobConcurrencyToken == attempt.JobConcurrencyToken
                && !item.ActualProviderCostKnown
                && item.Status == attempt.Status)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ActualProviderCostUsd, request.ActualProviderCostUsd)
                .SetProperty(item => item.ActualProviderCostKnown, true)
                .SetProperty(item => item.SafeMetadataJson, attemptMetadata), cancellationToken);
        if (changed != 1)
            return Conflict(new { error = new { code = "MUSIC_PROVIDER_COST_SETTLEMENT_RACE", message = "The provider attempt changed while it was being settled; reload the pending list." } });

        var attemptCosts = await db.GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == attempt.GenerationJobId
                && item.Status != GenerationProviderAttemptStatus.Rejected)
            .Select(item => new { item.ActualProviderCostUsd, item.ActualProviderCostKnown })
            .ToListAsync(cancellationToken);
        var knownCosts = attemptCosts.Where(item => item.ActualProviderCostKnown).ToArray();
        usage.ProviderCostUsd = knownCosts.Sum(item => item.ActualProviderCostUsd ?? 0m);
        usage.ProviderCostKnown = attemptCosts.Count > 0 && attemptCosts.All(item => item.ActualProviderCostKnown);
        usage.CostBasis = usage.ProviderCostKnown ? UsageCostBasis.Manual : UsageCostBasis.Unknown;
        usage.ActualRecordedAt = usage.ProviderCostKnown ? settlementAt : null;
        usage.SafeMetadataJson = AddSettlementAudit(usage.SafeMetadataJson, attemptId, request.ActualProviderCostUsd, evidenceReference!, actorUserId, settlementAt);
        // ChargedAmount and ReversedAmount are intentionally untouched: this control settles provider
        // spend only and must not create a customer charge or reversal for a cancelled/unknown outcome.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Ok(new SettleMusicProviderCostResponse(
            attemptId,
            attempt.GenerationJobId,
            request.ActualProviderCostUsd,
            true,
            usage.ProviderCostUsd,
            usage.ProviderCostKnown,
            usage.CostBasis ?? UsageCostBasis.Manual,
            usage.ChargedAmount,
            usage.ReversedAmount,
            actorUserId,
            settlementAt,
            evidenceReference));
    }

    private static string AddSettlementAudit(
        string? existingJson,
        Guid attemptId,
        decimal amountUsd,
        string evidenceReference,
        Guid actorUserId,
        DateTime settledAtUtc)
    {
        JsonObject metadata;
        try
        {
            metadata = JsonNode.Parse(existingJson ?? "{}") as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            metadata = new JsonObject();
            if (!string.IsNullOrWhiteSpace(existingJson))
                metadata["priorSafeMetadataJson"] = existingJson.Length <= 2_000 ? existingJson : existingJson[..2_000];
        }
        var settlements = metadata["manualProviderCostSettlements"] as JsonObject ?? new JsonObject();
        var settlementKey = attemptId.ToString("D");
        if (settlements.ContainsKey(settlementKey))
            throw new InvalidOperationException("A manual provider-cost settlement audit record already exists for this attempt.");
        settlements[settlementKey] = new JsonObject
        {
            ["amountUsd"] = JsonValue.Create(amountUsd),
            ["currency"] = UsageCurrencies.Usd,
            ["evidenceReference"] = evidenceReference,
            ["settledByUserId"] = actorUserId.ToString("D"),
            ["settledAtUtc"] = settledAtUtc.ToString("O"),
        };
        metadata["manualProviderCostSettlements"] = settlements;
        var result = metadata.ToJsonString();
        if (result.Length > 8_000)
            throw new InvalidOperationException("The provider-cost audit metadata exceeds the safe storage limit.");
        return result;
    }

    private static bool IsSafeEvidenceReference(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 500
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/' or '#');
}

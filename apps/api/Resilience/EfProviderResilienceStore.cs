using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Resilience;

public sealed class EfProviderResilienceStore(TaslimDbContext db) : IProviderResilienceStore
{
    public async Task<ProviderFinalizationClaimResult> TryClaimFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, DateTime now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var existing = await db.ProviderExecutionFinalizations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GenerationJobId == generationJobId && item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (string.Equals(existing?.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            // A terminal parent can replay its outcome. Only an active Running claim may start work.
            if (!await db.GenerationJobs.AsNoTracking().AnyAsync(job => job.Id == generationJobId
                    && job.ConcurrencyToken == jobConcurrencyToken, cancellationToken))
                throw new StaleProviderWorkerException(generationJobId);
            return ProviderFinalizationClaimResult.AlreadyCompleted;
        }

        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        if (existing is not null)
        {
            if (existing.ClaimExpiresAt > now)
                return ProviderFinalizationClaimResult.AlreadyClaimed;

            var claimed = await db.ProviderExecutionFinalizations
                .Where(item => item.Id == existing.Id
                    && item.State == "claimed"
                    && item.ClaimExpiresAt <= now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.JobConcurrencyToken, jobConcurrencyToken)
                    .SetProperty(item => item.ClaimedAt, now)
                    .SetProperty(item => item.ClaimExpiresAt, now.Add(lease)), cancellationToken);
            if (claimed == 1)
            {
                SynchronizeTrackedFinalizationClaim(existing.Id, jobConcurrencyToken, now, now.Add(lease));
                return ProviderFinalizationClaimResult.Claimed;
            }

            var currentState = await db.ProviderExecutionFinalizations.AsNoTracking()
                .Where(item => item.Id == existing.Id)
                .Select(item => item.State)
                .SingleOrDefaultAsync(cancellationToken);
            return string.Equals(currentState, "completed", StringComparison.OrdinalIgnoreCase)
                ? ProviderFinalizationClaimResult.AlreadyCompleted
                : ProviderFinalizationClaimResult.AlreadyClaimed;
        }

        db.ProviderExecutionFinalizations.Add(new ProviderExecutionFinalization
        {
            Id = Guid.NewGuid(),
            GenerationJobId = generationJobId,
            JobConcurrencyToken = jobConcurrencyToken,
            IdempotencyKey = idempotencyKey,
            State = "claimed",
            ClaimedAt = now,
            ClaimExpiresAt = now.Add(lease),
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return ProviderFinalizationClaimResult.Claimed;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var concurrent = await db.ProviderExecutionFinalizations.AsNoTracking().SingleAsync(item => item.GenerationJobId == generationJobId && item.IdempotencyKey == idempotencyKey, cancellationToken);
            return string.Equals(concurrent.State, "completed", StringComparison.OrdinalIgnoreCase)
                ? ProviderFinalizationClaimResult.AlreadyCompleted
                : ProviderFinalizationClaimResult.AlreadyClaimed;
        }
    }

    public async Task<ProviderFinalizationOutcome?> ReadCompletedOutcomeAsync(
        Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var recorded = await db.ProviderExecutionFinalizations.AsNoTracking()
            .Where(row => row.GenerationJobId == generationJobId && row.IdempotencyKey == idempotencyKey && row.State == "completed")
            .Select(row => new { row.ResultCategory, row.ResultCode })
            .SingleOrDefaultAsync(cancellationToken);
        // Historical completions did not retain the outcome. Unknown future spellings and malformed
        // numeric values must also remain unresolved instead of being interpreted as success.
        if (recorded?.ResultCategory is not { } name || !Enum.TryParse<ProviderAttemptResultCategory>(name, out var category)
            || !Enum.IsDefined(category) || category.ToString() != name)
            return null;
        return new(category, recorded.ResultCode);
    }

    public async Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default, string? errorCode = null)
    {
        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        var finalization = db.ProviderExecutionFinalizations.Where(row => row.GenerationJobId == generationJobId && row.IdempotencyKey == idempotencyKey);
        var category = result.ToString();
        // Store only bounded machine codes, never arbitrary provider text.
        var safeCode = errorCode is { Length: > 0 and <= 100 } && errorCode.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? errorCode : null;
        var now = DateTime.UtcNow;
        // Once published, the logical outcome cannot be replaced by a stale completion.
        var updated = await finalization.Where(row => row.State == "claimed" && row.JobConcurrencyToken == jobConcurrencyToken
                && row.GenerationJob.Status == GenerationJobStatus.Running && row.GenerationJob.ConcurrencyToken == jobConcurrencyToken)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.State, "completed")
                .SetProperty(row => row.ResultCategory, category)
                .SetProperty(row => row.ResultCode, safeCode)
                .SetProperty(row => row.CompletedAt, now)
                .SetProperty(row => row.ClaimExpiresAt, (DateTime?)null), cancellationToken);
        var tracked = db.ProviderExecutionFinalizations.Local.SingleOrDefault(row => row.GenerationJobId == generationJobId && row.IdempotencyKey == idempotencyKey);
        if (tracked is not null)
            await db.Entry(tracked).ReloadAsync(cancellationToken);
        if (updated == 1) return;

        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        if (await finalization.AsNoTracking().AnyAsync(row => row.State == "claimed" && row.JobConcurrencyToken != jobConcurrencyToken, cancellationToken))
            throw new StaleProviderWorkerException(generationJobId);
    }

    public async Task<ProviderAttemptHistory> ReadAttemptHistoryAsync(Guid generationJobId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var prefix = idempotencyKey + ":";
        var rows = await db.GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == generationJobId)
            .OrderBy(item => item.AttemptNumber)
            .Select(item => new { item.AttemptNumber, item.FinalizationKey, item.Provider, item.Status, item.ResultClassification })
            .ToListAsync(cancellationToken);
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ProviderAttemptResultCategory? lastCategory = null;
        var needsReconciliation = false;
        foreach (var row in rows.Where(row => row.FinalizationKey == prefix + row.AttemptNumber))
        {
            tried.Add(row.Provider.Trim());
            // A circuit denial and an explicit rate rejection prove the vendor did
            // not accept this attempt. Every other interrupted sequence needs reconciliation.
            if (row.Status == GenerationProviderAttemptStatus.Failed && row.ResultClassification == nameof(ProviderAttemptResultCategory.CircuitOpen))
                lastCategory = ProviderAttemptResultCategory.CircuitOpen;
            else if (row.Status == GenerationProviderAttemptStatus.Failed && row.ResultClassification == nameof(ProviderAttemptResultCategory.RateLimited))
                lastCategory = ProviderAttemptResultCategory.RateLimited;
            else
                needsReconciliation = true;
        }
        return new(rows.Count == 0 ? 0 : rows[^1].AttemptNumber, tried, lastCategory, needsReconciliation);
    }

    public async Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default, decimal? reservedCostUsd = null)
    {
        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        var attempt = new GenerationProviderAttempt
        {
            Id = Guid.NewGuid(),
            GenerationJobId = generationJobId,
            JobConcurrencyToken = jobConcurrencyToken,
            IdempotencyKey = $"{idempotencyKey}:{attemptNumber}",
            Capability = capability,
            AttemptNumber = attemptNumber,
            RetryNumber = isRetry ? Math.Max(1, attemptNumber - 1) : 0,
            IsRetry = isRetry,
            IsFallback = isFallback,
            Provider = string.IsNullOrWhiteSpace(providerKey) ? "unknown" : providerKey.Trim(),
            Status = GenerationProviderAttemptStatus.Started,
            ResultClassification = "Started",
            EstimatedProviderCostUsd = reservedCostUsd,
            EstimatedProviderCostKnown = reservedCostUsd.HasValue,
            FinalizationKey = $"{idempotencyKey}:{attemptNumber}",
            StartedAt = startedAt,
        };
        db.GenerationProviderAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return new(attempt.Id, generationJobId, jobConcurrencyToken, attempt.IdempotencyKey, capability, providerKey, attemptNumber, isRetry, isFallback, startedAt);
    }

    public async Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default, Guid? replacedReservationId = null)
    {
        await EnsureCurrentClaimAsync(attempt.GenerationJobId, attempt.JobConcurrencyToken, cancellationToken);
        var row = await db.GenerationProviderAttempts.SingleOrDefaultAsync(item => item.Id == attempt.Id, cancellationToken);
        if (row is null || row.JobConcurrencyToken != attempt.JobConcurrencyToken)
            throw new StaleProviderWorkerException(attempt.GenerationJobId);
        if (replacedReservationId is { } reservationId)
        {
            var prior = await db.GenerationProviderAttempts.SingleAsync(item => item.Id == reservationId
                && item.GenerationJobId == attempt.GenerationJobId && item.JobConcurrencyToken == attempt.JobConcurrencyToken,
                cancellationToken);
            // The accepted result now accounts for this same-key logical dispatch.
            prior.EstimatedProviderCostUsd = 0m;
            prior.EstimatedProviderCostKnown = true;
        }
        row.ResultClassification = result.ToString();
        row.Status = result switch
        {
            ProviderAttemptResultCategory.Success => GenerationProviderAttemptStatus.Succeeded,
            ProviderAttemptResultCategory.Cancelled => GenerationProviderAttemptStatus.Cancelled,
            ProviderAttemptResultCategory.CostGuardRejected => GenerationProviderAttemptStatus.Rejected,
            _ => GenerationProviderAttemptStatus.Failed,
        };
        row.FailureCode = errorCode;
        row.LatencyMs = latencyMs;
        row.EstimatedProviderCostUsd = estimatedCostUsd;
        row.EstimatedProviderCostKnown = estimatedCostUsd.HasValue;
        row.RateLimited = result == ProviderAttemptResultCategory.RateLimited;
        row.TimedOut = result == ProviderAttemptResultCategory.TimedOut;
        row.CircuitOpen = result == ProviderAttemptResultCategory.CircuitOpen;
        row.CompletedAt = completedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var circuit = await GetOrCreateCircuitAsync(providerKey, capability, now, cancellationToken);
            var state = ParseState(circuit.State);
            if (state == ProviderCircuitState.Closed)
                return ProviderCircuitAdmission.Allowed;
            if (state == ProviderCircuitState.Open && circuit.OpenUntil > now)
                return ProviderCircuitAdmission.Open;

            if (state == ProviderCircuitState.Open)
            {
                circuit.State = "half_open";
                circuit.ProbeExpiresAt = now.Add(probeLease);
                circuit.UpdatedAt = now;
            }
            else if (circuit.ProbeExpiresAt is null || circuit.ProbeExpiresAt <= now)
            {
                circuit.ProbeExpiresAt = now.Add(probeLease);
                circuit.UpdatedAt = now;
            }
            else
            {
                return ProviderCircuitAdmission.ProbeInProgress;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ProviderCircuitAdmission.Allowed;
            }
            catch (DbUpdateConcurrencyException) when (retry < 2)
            {
                db.ChangeTracker.Clear();
            }
        }

        return ProviderCircuitAdmission.ProbeInProgress;
    }

    public async Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var circuit = await GetOrCreateCircuitAsync(providerKey, capability, now, cancellationToken);
            circuit.State = "closed";
            circuit.ConsecutiveFailures = 0;
            circuit.OpenedAt = null;
            circuit.OpenUntil = null;
            circuit.ProbeExpiresAt = null;
            circuit.LastSuccessAt = now;
            circuit.UpdatedAt = now;
            circuit.RowVersion = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (retry < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var circuit = await GetOrCreateCircuitAsync(providerKey, capability, now, cancellationToken);
            circuit.ConsecutiveFailures++;
            circuit.LastFailureAt = now;
            circuit.UpdatedAt = now;
            circuit.RowVersion = Guid.NewGuid();
            if (circuit.ConsecutiveFailures >= Math.Max(1, failureThreshold) || ParseState(circuit.State) == ProviderCircuitState.HalfOpen)
            {
                circuit.State = "open";
                circuit.OpenedAt = now;
                circuit.OpenUntil = now.Add(openDuration);
                circuit.ProbeExpiresAt = null;
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (retry < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default)
    {
        var circuit = await db.ProviderCircuits.AsNoTracking().SingleOrDefaultAsync(item => item.ProviderKey == providerKey && item.Capability == capability, cancellationToken);
        return circuit is null ? null : new(providerKey, capability, ParseState(circuit.State), circuit.ConsecutiveFailures, circuit.OpenUntil, circuit.ProbeExpiresAt);
    }

    public Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default) =>
        db.GenerationJobs.AsNoTracking().Where(item => item.Id == generationJobId).Select(item => item.CancellationRequested).SingleOrDefaultAsync(cancellationToken);

    public async Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default, Guid? excludeReservationId = null)
    {
        var attempts = db.GenerationProviderAttempts.AsNoTracking()
            .Where(item => item.GenerationJobId == generationJobId
                && item.ResultClassification != nameof(ProviderAttemptResultCategory.CircuitOpen)
                && item.ResultClassification != nameof(ProviderAttemptResultCategory.RateLimited)
                && (item.EstimatedProviderCostKnown || item.Status == GenerationProviderAttemptStatus.Started
                    || item.Status == GenerationProviderAttemptStatus.Succeeded || item.Status == GenerationProviderAttemptStatus.Cancelled
                    || item.TimedOut || item.ResultClassification == nameof(ProviderAttemptResultCategory.TransientFailure)));
        if (excludeReservationId is { } reservationId)
            attempts = attempts.Where(item => item.Id != reservationId);
        var estimates = await attempts.Select(item => item.EstimatedProviderCostKnown ? item.EstimatedProviderCostUsd : null).ToListAsync(cancellationToken);
        decimal total = 0m;
        foreach (var estimate in estimates)
        {
            if (estimate is not { } amount || amount < 0m || amount > decimal.MaxValue - total)
                return -1m; // Unpriced exposure and invalid totals cannot authorize another dispatch.
            total += amount;
        }
        return total;
    }

    private async Task<ProviderCircuit> GetOrCreateCircuitAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken)
    {
        var circuit = await db.ProviderCircuits.SingleOrDefaultAsync(item => item.ProviderKey == providerKey && item.Capability == capability, cancellationToken);
        if (circuit is not null) return circuit;
        circuit = new ProviderCircuit
        {
            Id = Guid.NewGuid(),
            ProviderKey = providerKey,
            Capability = capability,
            State = "closed",
            UpdatedAt = now,
        };
        db.ProviderCircuits.Add(circuit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return circuit;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return await db.ProviderCircuits.SingleAsync(item => item.ProviderKey == providerKey && item.Capability == capability, cancellationToken);
        }
    }

    private async Task EnsureCurrentClaimAsync(Guid generationJobId, Guid jobConcurrencyToken, CancellationToken cancellationToken)
    {
        var current = await db.GenerationJobs.AsNoTracking().Where(item => item.Id == generationJobId).Select(item => new { item.ConcurrencyToken, item.Status }).SingleOrDefaultAsync(cancellationToken);
        if (current is null || current.ConcurrencyToken != jobConcurrencyToken || current.Status != GenerationJobStatus.Running)
            throw new StaleProviderWorkerException(generationJobId);
    }

    private void SynchronizeTrackedFinalizationClaim(Guid finalizationId, Guid jobConcurrencyToken, DateTime claimedAt, DateTime claimExpiresAt)
    {
        var tracked = db.ProviderExecutionFinalizations.Local.SingleOrDefault(item => item.Id == finalizationId);
        if (tracked is null) return;

        var entry = db.Entry(tracked);
        var state = entry.Property(item => item.State);
        state.CurrentValue = "claimed";
        state.OriginalValue = "claimed";
        state.IsModified = false;

        var token = entry.Property(item => item.JobConcurrencyToken);
        token.CurrentValue = jobConcurrencyToken;
        token.OriginalValue = jobConcurrencyToken;
        token.IsModified = false;

        var claimed = entry.Property(item => item.ClaimedAt);
        claimed.CurrentValue = claimedAt;
        claimed.OriginalValue = claimedAt;
        claimed.IsModified = false;

        var expires = entry.Property(item => item.ClaimExpiresAt);
        expires.CurrentValue = claimExpiresAt;
        expires.OriginalValue = claimExpiresAt;
        expires.IsModified = false;
    }

    private static ProviderCircuitState ParseState(string state) => state switch
    {
        "open" => ProviderCircuitState.Open,
        "half_open" => ProviderCircuitState.HalfOpen,
        _ => ProviderCircuitState.Closed,
    };
}

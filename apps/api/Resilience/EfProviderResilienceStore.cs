using Microsoft.EntityFrameworkCore;
using Taslim.Api.Domain;
using Taslim.Api.Persistence;

namespace Taslim.Api.Resilience;

public sealed class EfProviderResilienceStore(TaslimDbContext db) : IProviderResilienceStore
{
    public async Task<ProviderFinalizationClaimResult> TryClaimFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, DateTime now, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        var existing = await db.ProviderExecutionFinalizations.SingleOrDefaultAsync(item => item.GenerationJobId == generationJobId && item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(existing.State, "completed", StringComparison.OrdinalIgnoreCase))
                return ProviderFinalizationClaimResult.AlreadyCompleted;
            if (existing.ClaimExpiresAt > now)
                return ProviderFinalizationClaimResult.AlreadyClaimed;
            existing.JobConcurrencyToken = jobConcurrencyToken;
            existing.State = "claimed";
            existing.ClaimedAt = now;
            existing.ClaimExpiresAt = now.Add(lease);
            await db.SaveChangesAsync(cancellationToken);
            return ProviderFinalizationClaimResult.Claimed;
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

    public async Task CompleteFinalizationAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, ProviderAttemptResultCategory result, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        var finalization = await db.ProviderExecutionFinalizations.SingleOrDefaultAsync(item => item.GenerationJobId == generationJobId && item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (finalization is null || !string.Equals(finalization.State, "claimed", StringComparison.OrdinalIgnoreCase))
            return;
        if (finalization.JobConcurrencyToken != jobConcurrencyToken)
            throw new StaleProviderWorkerException(generationJobId);
        finalization.State = "completed";
        finalization.CompletedAt = DateTime.UtcNow;
        finalization.ClaimExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderAttemptRecord> StartAttemptAsync(Guid generationJobId, Guid jobConcurrencyToken, string idempotencyKey, string capability, string providerKey, int attemptNumber, bool isRetry, bool isFallback, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentClaimAsync(generationJobId, jobConcurrencyToken, cancellationToken);
        var attempt = new ProviderAttempt
        {
            Id = Guid.NewGuid(),
            GenerationJobId = generationJobId,
            JobConcurrencyToken = jobConcurrencyToken,
            IdempotencyKey = $"{idempotencyKey}:{attemptNumber}",
            Capability = capability,
            ProviderKey = providerKey,
            AttemptNumber = attemptNumber,
            ResultCategory = "started",
            IsRetry = isRetry,
            IsFallback = isFallback,
            StartedAt = startedAt,
        };
        db.ProviderAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return new(attempt.Id, generationJobId, jobConcurrencyToken, attempt.IdempotencyKey, capability, providerKey, attemptNumber, isRetry, isFallback, startedAt);
    }

    public async Task CompleteAttemptAsync(ProviderAttemptRecord attempt, ProviderAttemptResultCategory result, string? errorCode, long latencyMs, decimal? estimatedCostUsd, DateTime completedAt, CancellationToken cancellationToken = default)
    {
        await EnsureCurrentClaimAsync(attempt.GenerationJobId, attempt.JobConcurrencyToken, cancellationToken);
        var row = await db.ProviderAttempts.SingleOrDefaultAsync(item => item.Id == attempt.Id, cancellationToken);
        if (row is null || row.JobConcurrencyToken != attempt.JobConcurrencyToken)
            throw new StaleProviderWorkerException(attempt.GenerationJobId);
        row.ResultCategory = result.ToString();
        row.ErrorCode = errorCode;
        row.LatencyMs = latencyMs;
        row.EstimatedCostUsd = estimatedCostUsd;
        row.CompletedAt = completedAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderCircuitAdmission> TryAcquireCircuitAsync(string providerKey, string capability, DateTime now, TimeSpan probeLease, CancellationToken cancellationToken = default)
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
            await db.SaveChangesAsync(cancellationToken);
            return ProviderCircuitAdmission.Allowed;
        }
        if (circuit.ProbeExpiresAt is null || circuit.ProbeExpiresAt <= now)
        {
            circuit.ProbeExpiresAt = now.Add(probeLease);
            circuit.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return ProviderCircuitAdmission.Allowed;
        }
        return ProviderCircuitAdmission.ProbeInProgress;
    }

    public async Task RecordCircuitSuccessAsync(string providerKey, string capability, DateTime now, CancellationToken cancellationToken = default)
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
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordCircuitFailureAsync(string providerKey, string capability, DateTime now, int failureThreshold, TimeSpan openDuration, CancellationToken cancellationToken = default)
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
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderCircuitSnapshot?> GetCircuitAsync(string providerKey, string capability, CancellationToken cancellationToken = default)
    {
        var circuit = await db.ProviderCircuits.AsNoTracking().SingleOrDefaultAsync(item => item.ProviderKey == providerKey && item.Capability == capability, cancellationToken);
        return circuit is null ? null : new(providerKey, capability, ParseState(circuit.State), circuit.ConsecutiveFailures, circuit.OpenUntil, circuit.ProbeExpiresAt);
    }

    public Task<bool> IsCancellationRequestedAsync(Guid generationJobId, CancellationToken cancellationToken = default) =>
        db.GenerationJobs.AsNoTracking().Where(item => item.Id == generationJobId).Select(item => item.CancellationRequested).SingleOrDefaultAsync(cancellationToken);

    public Task<decimal> GetCumulativeEstimatedCostAsync(Guid generationJobId, CancellationToken cancellationToken = default) =>
        db.ProviderAttempts.Where(item => item.GenerationJobId == generationJobId).SumAsync(item => item.EstimatedCostUsd ?? 0m, cancellationToken);

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

    private static ProviderCircuitState ParseState(string state) => state switch
    {
        "open" => ProviderCircuitState.Open,
        "half_open" => ProviderCircuitState.HalfOpen,
        _ => ProviderCircuitState.Closed,
    };
}
